using System;
using System.Collections.Generic;

namespace VE3NEA.SkySSTV
{
  /// <summary>One extracted scan line: which block claimed it, the train that owns it, and the pulse
  /// (= transmitted-line) number on that train's grid. The line's sync onset is
  /// <c>Train.Regr.GetPulseTime(PulseNo)</c>.</summary>
  internal struct SstvScanLine
  {
    public int BlkNo;
    public int PulseNo;
    public SstvPulseTrain Train;
  }

  /// <summary>
  /// The pulse-train MHT (plan §4.1/§6.1, ported from Hopper's <c>TPulseTrainExtractor</c>): consumes the
  /// time-ordered sync-pulse stream from the per-family <see cref="SstvPulseDetector"/>s and maintains a
  /// small list of <see cref="SstvPulseTrain"/> hypotheses. Each pulse is first offered to the existing
  /// trains (associate-first — which also kills the half-rate harmonic: a Robot36 pulse lands on the Robot36
  /// train before it could ever seed a Robot72 grid); an unclaimed pulse may <b>spawn</b> a new candidate,
  /// but only as the third point of a period-consistent 3-pulse triplet for some mode of its sync-duration
  /// family — a mini-comb clutter almost never fakes. Candidates promote on N-of-M evidence (back-filling
  /// the pulses they explain), idle candidates are pruned, idle actives retire (§1.10 T_gap). Per line block
  /// the <b>best train</b> (smoothed sync power, 1.5× switch hysteresis, incumbent preferred) claims the
  /// block's scan lines; a promotion/revision/retirement marks earlier blocks dirty and their lines are
  /// re-extracted (§1.13 bounded re-render). A valid VIS seeds a high-prior <see cref="SstvVisPulseTrain"/>.
  ///
  /// <para>State is bounded: the pulse buffer keeps only the trailing few seconds (spawn looks back two
  /// line periods, back-fill one retire timeout), and the train list is pruned by the lifecycle. The scan
  /// lines and any trains they reference are kept for the whole pass (bounded by pass length; the rolling
  /// dump lands with the push-based decoder).</para>
  /// </summary>
  internal sealed class SstvPulseTrainExtractor
  {
    private const double BlockSeconds = 0.25;      // line extraction granularity, ~ line-period scale
    private const double TripletTolMs = 7.0;       // 3rd-pulse collinearity gate (Hopper ±20 @ 2.76 kHz)
    private const double MergeWingMs = 20.0;       // grid-continuation gate for merging a fragment train
    private const double MergeGapSeconds = 12.0;   // max fade between a train and the fragment it absorbs
    private const double PeriodTol = 0.03;         // triplet spacing gate: ±3 % of a mode's nominal period
    private const double SwitchHysteresis = 1.5;   // a challenger must beat the incumbent by this factor
    private const double PruneSeconds = 8.0;       // pulse-buffer tail (> the retire timeout)
    private const double MinLineFraction = 0.25;   // an image train must claim ≥ ¼ of the mode's lines
    private const int MinCombPulses = 6;           // comb-seeded image trains need this much pulse support

    private readonly double fs;
    private readonly int blockSize;
    private readonly int tripletTol;
    private readonly int pruneLen;
    private readonly int smoothBlocksWing;         // trailing blocks whose smoothed power can still change
    private readonly (SstvModeSpec spec, double minPeriod, double maxPeriod)[] modeGates;
    private readonly double maxPeriod;

    private readonly List<SstvPulse> pulses = new();
    private readonly List<SstvPulseTrain> trains = new();
    private readonly List<SstvScanLine> lines = new();
    private int dirtyBlock;
    private int pendingDirty = int.MaxValue;     // dirty mark set between lifecycle passes (comb seeding)
    private int rewindLow = int.MaxValue;        // lowest Lines index (re)written since TakeLineRewind

    public IReadOnlyList<SstvPulseTrain> Trains => trains;
    public IReadOnlyList<SstvScanLine> Lines => lines;

    /// <summary>The train that retired during the last <see cref="Process"/> call, if any — the future
    /// image-finalize hook (plan §1.10).</summary>
    public SstvPulseTrain? RetiredTrain { get; private set; }

    /// <summary>Lowest <see cref="Lines"/> index (re)written since the last call — the streaming image
    /// builder re-renders from here (the §1.13 dirty-block re-render window). Returns
    /// <see cref="int.MaxValue"/> when nothing changed; resets on read.</summary>
    public int TakeLineRewind()
    {
      int r = rewindLow;
      rewindLow = int.MaxValue;
      return r;
    }

    public SstvPulseTrainExtractor(double fs)
    {
      this.fs = fs;
      blockSize = (int)Math.Round(BlockSeconds * fs);
      tripletTol = (int)Math.Round(TripletTolMs / 1000.0 * fs);
      pruneLen = (int)Math.Round(PruneSeconds * fs);

      modeGates = new (SstvModeSpec, double, double)[SstvModes.All.Count];
      double max = 0;
      for (int i = 0; i < SstvModes.All.Count; i++)
      {
        var spec = SstvModes.All[i];
        double nominal = spec.LinePeriodMs / 1000.0 * fs;
        modeGates[i] = (spec, nominal * (1 - PeriodTol), nominal * (1 + PeriodTol));
        max = Math.Max(max, nominal * (1 + PeriodTol));
      }
      maxPeriod = max;
      smoothBlocksWing = (int)Math.Ceiling((4 + 1) * maxPeriod / blockSize);
    }

    /// <summary>Seed a high-prior train from a decoded VIS header (plan §4.1): mode and line-0 onset known.</summary>
    public void AddVisTrain(SstvMode mode, int headerEndSample)
      => trains.Add(new SstvVisPulseTrain(mode, headerEndSample, fs));

    /// <summary>Seed (or refresh) a high-prior train from a confirmed soft-comb hit (plan §4.1): like a VIS
    /// seed, but born Active — the comb's over-threshold ridge over ~100 line periods IS the promotion
    /// evidence — and back-dated one comb memory so the accumulated span's lines are claimed. A hit whose
    /// span is already explained by an existing train seeds nothing: the ridge persists one comb memory
    /// after its cause, so a hit right after a strong train retires is that train's own echo.</summary>
    public void AddCombTrain(SstvMode mode, int anchorSample)
    {
      double period = SstvModes.Get(mode).LinePeriodMs / 1000.0 * fs;
      double memory = SstvSoftComb.MemoryPeriods * period;

      // a hit on the grid of the live comb train refreshes its life — the comb is its evidence clock
      foreach (var train in trains)
        if (train is SstvCombPulseTrain comb && comb.Format == mode
            && comb.State == SstvTrainState.Active
            && Math.Abs(anchorSample - comb.Regr.GetPulseTime(comb.Regr.GetPulseNo(anchorSample)))
               <= MergeWingMs / 1000.0 * fs)
        { comb.RegisterHit(anchorSample); return; }

      // the no-overlap rule (one transmission per FM channel) applies to comb seeds too
      foreach (var train in trains)
        if (train.State == SstvTrainState.Active) return;

      // explained evidence: any same-family train (a candidate racing to promotion, or one retired within
      // the comb memory) whose pulses fed the ridge — seeding on it would duplicate that transmission
      double syncMs = SstvModes.Get(mode).SyncMs;
      foreach (var train in trains)
        if (Math.Abs(SstvModes.Get(train.Format).SyncMs - syncMs) < 0.5
            && train.Regr.LastPulseTime > anchorSample - memory) return;

      // back-date one comb memory, clamped to the stream start with the comb phase preserved
      int back = Math.Min(SstvSoftComb.MemoryPeriods, (int)Math.Floor(anchorSample / period));
      int start = anchorSample - (int)Math.Round(back * period);
      trains.Add(new SstvCombPulseTrain(mode, start, anchorSample, fs));
      pendingDirty = Math.Min(pendingDirty, start / blockSize);
    }

    /// <summary>Fold in the pulses detected up to <paramref name="uptoTime"/> (absolute samples, pulses in
    /// time order), then run the train lifecycle and (re-)extract the scan lines of the settled blocks.</summary>
    public void Process(IReadOnlyList<SstvPulse> newPulses, int uptoTime)
    {
      foreach (var pulse in newPulses)
      {
        Prune(pulse.Time);
        pulses.Add(pulse);
        ProcessPulse();
      }
      UpdateTrainList(uptoTime);
      UpdateLines(uptoTime / blockSize - 1);
    }

    /// <summary>End of stream: final lifecycle pass and line extraction through the last, partial block.</summary>
    public void Finish(int endTime)
    {
      UpdateTrainList(endTime);
      UpdateLines(endTime / blockSize);
    }

    /// <summary>The dominant train of the pass: the one claiming the most scan lines (ties to the higher
    /// pulse count). Null when nothing ever promoted — e.g. pure noise.</summary>
    public SstvPulseTrain? BestTrain(SstvMode? mode = null)
    {
      var claims = new Dictionary<SstvPulseTrain, int>();
      foreach (var line in lines)
        if (mode == null || line.Train.Format == mode)
          claims[line.Train] = claims.TryGetValue(line.Train, out int n) ? n + 1 : 1;

      SstvPulseTrain? best = null; int bestClaim = 0;
      foreach (var (train, claim) in claims)
        if (claim > bestClaim || (claim == bestClaim && train.PulseCnt > (best?.PulseCnt ?? 0)))
        { best = train; bestClaim = claim; }
      return best;
    }

    /// <summary>Scan lines claimed by <paramref name="train"/>.</summary>
    public int ClaimedLines(SstvPulseTrain train)
    {
      int claimed = 0;
      foreach (var line in lines) if (line.Train == train) claimed++;
      return claimed;
    }

    /// <summary>The image-emission gate (retro item D, resolved 2026-07-02): a promoted train yields an
    /// image when it claims at least <see cref="MinLineFraction"/> of its mode's lines. No evidence-quality
    /// rejection beyond that: the retro-D measurement first suggested a pulses/claimed-lines fill-ratio
    /// gate (noise ≤ 0.34 vs real ≥ 0.46), but the "noise" trains it was tuned on turned out to be REAL
    /// weak transmissions (user-confirmed on the FskDemod spectrogram — e.g. the 12_37_50 Monitor-3
    /// ~157 s burst), and pure noise already fails at promotion (the MHT triplet + N-of-M gates, pinned
    /// by the synthetic clutter tests). <see cref="FillRatio"/> stays as a quality diagnostic for the
    /// image META, not a gate.
    ///
    /// <para>One exception (the comb false-positive guard, P7, 2026-07-04): a <b>comb-seeded</b> train is
    /// born promoted on the ridge alone, so it bypasses every pulse-count gate — and burst telemetry under
    /// the blanked chain can sustain a ridge with real-regime persistence (the user-refuted 11_09 Robot72
    /// train at 117.9–161 s). The pulse stream still separates cleanly: every real comb find carries
    /// ≥ 21 associated pulses (04-18 p=21, 12_37_50 p=23; pre-blanker minimum 7), the telemetry ridge
    /// only 3 — so a comb train must also show <see cref="MinCombPulses"/> pulses of support before it
    /// may emit an image.</para></summary>
    public bool IsImageTrain(SstvPulseTrain train)
    {
      if (train.State != SstvTrainState.Active && train.State != SstvTrainState.Retired) return false;
      if (train is SstvCombPulseTrain && train.PulseCnt < MinCombPulses) return false;
      return ClaimedLines(train) >= MinLineFraction * SstvModes.Get(train.Format).LineCount;
    }

    /// <summary>Fraction of the train's claimed lines that carry a detected sync pulse — an image-quality
    /// confidence (≈1 for a strong burst; low values mean the grid mostly coasted).</summary>
    public double FillRatio(SstvPulseTrain train)
    {
      int claimed = ClaimedLines(train);
      return claimed > 0 ? (double)train.PulseCnt / claimed : 0;
    }


    // ----------------------------------------------------------------------------------------------------
    //                                      pulse association / spawn
    // ----------------------------------------------------------------------------------------------------


    /// <summary>Associate-or-spawn for the newest pulse in the buffer (Hopper <c>ProcessPulse</c>).
    /// Two-tier soft evidence (plan §4.1, the streaming soft-comb): ANY pulse — down to the detectors'
    /// low associate tier — may confirm an existing train through the tight RLS gate, accumulating weak
    /// consistent evidence across line periods; but only <see cref="SstvPulseDetector.ScoreThreshold"/>-
    /// strong pulses may form the spawn triplet, so clutter at the soft tier can never create a
    /// hypothesis, only support one.</summary>
    private void ProcessPulse()
    {
      int newest = pulses.Count - 1;
      var p0 = pulses[newest];

      // associate first: VIS trains have priority, then the most recently updated
      foreach (var train in OrderedTrains())
        if (train.State != SstvTrainState.Retired && train.TryAddPulse(p0))
        {
          MarkUsed(newest);
          return;
        }

      // no overlapping trains (user decision 2026-07-03): one FM channel carries one transmission at a
      // time, so while a promoted train is actively tracking, no new hypothesis may spawn — a genuinely
      // new transmission spawns after the incumbent retires (candidates still compete freely before
      // promotion, so the mode competition is preserved). This makes a mid-burst duplicate categorically
      // impossible rather than merely gated.
      foreach (var train in trains)
        if (train.State == SstvTrainState.Active) return;

      // spawn: the pulse plus two buffered ones must form a period-consistent triplet — of spawn-tier
      // strength — for some mode of the pulse's sync-duration family
      if (p0.Power < SstvPulseDetector.ScoreThreshold) return;
      foreach (var (spec, minPeriod, maxPeriod) in modeGates)
      {
        if (Math.Abs(spec.SyncMs - p0.DurMs) > 0.5) continue;

        int mid = -1;
        for (int i = newest - 1; i >= 0; i--)
        {
          double dist = p0.Time - (double)pulses[i].Time;
          if (dist > 2 * maxPeriod + tripletTol) break;
          if (pulses[i].Power < SstvPulseDetector.ScoreThreshold) continue;

          if (dist >= minPeriod && dist <= maxPeriod) mid = i;

          if (mid >= 0 && i != mid
              && Math.Abs(dist - 2 * (p0.Time - (double)pulses[mid].Time)) <= tripletTol)
          {
            SpawnTrain(spec.Mode, i, mid, newest);
            break;
          }
        }
      }
    }

    /// <summary>Seed a train from a fresh triplet — adopted by a waiting VIS train whose anchor the triplet's
    /// grid extrapolates to, otherwise a new candidate.</summary>
    private void SpawnTrain(SstvMode mode, int i0, int i1, int i2)
    {
      var p0 = pulses[i0];
      var p1 = pulses[i1];
      var p2 = pulses[i2];

      bool adopted = false;
      foreach (var train in trains)
        if (train.Format == mode && train.State == SstvTrainState.VisOnly
            && train is SstvVisPulseTrain vis && vis.TryAddPulses(p0, p1, p2))
        { adopted = true; break; }

      if (!adopted) trains.Add(new SstvPulseTrain(mode, p0, p1, p2, fs));
      MarkUsed(i0);
      MarkUsed(i1);
      MarkUsed(i2);
    }

    /// <summary>VIS trains first (the strong prior), then by recency of the last accepted pulse.</summary>
    private IEnumerable<SstvPulseTrain> OrderedTrains()
    {
      var ordered = new List<SstvPulseTrain>(trains);
      ordered.Sort((a, b) =>
      {
        bool va = a is SstvVisPulseTrain, vb = b is SstvVisPulseTrain;
        if (va != vb) return va ? -1 : 1;
        return b.Regr.LastPulseTime.CompareTo(a.Regr.LastPulseTime);
      });
      return ordered;
    }

    private void MarkUsed(int index)
    {
      var p = pulses[index];
      p.Used = true;
      pulses[index] = p;
    }

    /// <summary>Keep the pulse buffer a bounded tail: spawn looks back two periods, back-fill one retire
    /// timeout — everything older is dead weight (§1.13).</summary>
    private void Prune(int now)
    {
      int keepFrom = 0;
      while (keepFrom < pulses.Count && pulses[keepFrom].Time < now - pruneLen) keepFrom++;
      if (keepFrom > 0) pulses.RemoveRange(0, keepFrom);
    }


    // ----------------------------------------------------------------------------------------------------
    //                                   train lifecycle / line extraction
    // ----------------------------------------------------------------------------------------------------


    /// <summary>Promote / kill / retire (Hopper <c>UpdateTrainList</c>), accumulating the earliest block
    /// whose extracted lines may have changed. The trailing <see cref="smoothBlocksWing"/> blocks are always
    /// dirty — their smoothed power can still move as pulses arrive.</summary>
    private void UpdateTrainList(int time)
    {
      RetiredTrain = null;
      dirtyBlock = Math.Min(time / blockSize - 1 - smoothBlocksWing, pendingDirty);
      pendingDirty = int.MaxValue;

      for (int i = trains.Count - 1; i >= 0; i--)
      {
        var train = trains[i];
        switch (train.State)
        {
          case SstvTrainState.Candidate:
          case SstvTrainState.VisOnly:
            if (train.HasEnoughPulses)
            {
              // merge-on-promote: if an existing train's grid continues onto this candidate, it is a
              // fragment of the same transmission (a fade where the sync timing wandered past the RLS
              // gate re-spawns mid-burst) — absorb it instead of emitting a duplicate image
              if (train is not SstvVisPulseTrain && FindMergeHost(train) is SstvPulseTrain host)
              {
                foreach (var p in train.Pulses) host.AddPulse(p);
                if (host.State == SstvTrainState.Retired) host.State = SstvTrainState.Active;
                MarkDirty(train.Regr.FirstPulseTime);
                trains.RemoveAt(i);
                break;
              }
              train.State = SstvTrainState.Active;
              train.AddOldPulses(pulses);
              MarkDirty(train.Regr.FirstPulseTime);
            }
            else if (train.IsCandidateIdle(time))
            {
              if (train is SstvVisPulseTrain vis) MarkDirty(vis.VisTime);
              trains.RemoveAt(i);
            }
            break;

          case SstvTrainState.Active:
            if (train.CanRetire(time))
            {
              train.State = SstvTrainState.Retired;
              MarkDirty(train.Regr.LastPulseTime);
              RetiredTrain = train;
            }
            else if (train.RevisionDue())
              MarkDirty(train.Regr.FirstPulseTime);
            break;
        }
      }
    }

    private void MarkDirty(double time) => dirtyBlock = Math.Min(dirtyBlock, (int)(time / blockSize));

    /// <summary>An existing same-mode train whose grid <b>continues</b> onto the promoting
    /// <paramref name="candidate"/>: it precedes the candidate by no more than <see cref="MergeGapSeconds"/>
    /// (a bridgeable fade) and predicts the candidate's first pulse within <see cref="MergeWingMs"/>
    /// (loose enough for the fade-timing wander that split them, far tighter than a chance alignment of
    /// two independent transmissions).</summary>
    private SstvPulseTrain? FindMergeHost(SstvPulseTrain candidate)
    {
      int candStart = (int)Math.Round(candidate.Regr.GetPulseTime(0));
      double wing = MergeWingMs / 1000.0 * fs;
      foreach (var host in trains)
      {
        if (host == candidate || host.Format != candidate.Format) continue;
        if (host.State != SstvTrainState.Active && host.State != SstvTrainState.Retired) continue;
        if (host.Regr.LastPulseTime >= candStart) continue;
        if (candStart - host.Regr.LastPulseTime > MergeGapSeconds * fs) continue;
        double expected = host.Regr.GetPulseTime(host.Regr.GetPulseNo(candStart));
        if (Math.Abs(candStart - expected) <= wing) return host;
      }
      return null;
    }

    /// <summary>(Re-)extract the scan lines of blocks [<see cref="dirtyBlock"/>, <paramref name="throughBlock"/>]
    /// (Hopper <c>UpdateLines</c>): drop the lines of dirty blocks, then per block let the best train claim
    /// the lines whose predicted sync times fall inside it. The incumbent (previous line's train) keeps the
    /// block unless a challenger's smoothed power beats it by the hysteresis factor.</summary>
    private void UpdateLines(int throughBlock)
    {
      while (lines.Count > 0 && lines[^1].BlkNo >= dirtyBlock) lines.RemoveAt(lines.Count - 1);
      rewindLow = Math.Min(rewindLow, lines.Count);

      for (int b = Math.Max(dirtyBlock, 0); b <= throughBlock; b++)
      {
        int blockStart = b * blockSize;
        int blockCenter = blockStart + blockSize / 2;

        SstvPulseTrain? best = null;
        double bestPower = 0;
        if (lines.Count > 0 && !lines[^1].Train.IsRetiredAt(blockStart))
        {
          best = lines[^1].Train;
          bestPower = best.GetPower(blockCenter);
        }

        foreach (var train in trains)
        {
          if (train.State == SstvTrainState.Retired) continue;
          if (train.State != SstvTrainState.Active && train is not SstvVisPulseTrain) continue;
          double power = train.GetPower(blockCenter);
          if (power > SwitchHysteresis * bestPower) { best = train; bestPower = power; }
        }
        if (best == null) continue;

        int pulseNo = lines.Count > 0 && lines[^1].Train == best
          ? lines[^1].PulseNo + 1
          : best.Regr.GetPulseNo(blockStart);
        for (; ; pulseNo++)
        {
          double pulseTime = best.Regr.GetPulseTime(pulseNo);
          if (pulseTime < blockStart) continue;
          if (pulseTime >= blockStart + blockSize) break;
          lines.Add(new SstvScanLine { BlkNo = b, PulseNo = pulseNo, Train = best });
        }
      }
    }
  }
}
