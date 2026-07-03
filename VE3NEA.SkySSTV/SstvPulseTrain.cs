using System;
using System.Collections.Generic;

namespace VE3NEA.SkySSTV
{
  /// <summary>One detected 1200 Hz sync pulse: absolute sample time, matched-filter power, the
  /// sync-template duration that detected it (ms — the Robot-vs-PD family discriminant, retro
  /// item C), and a flag marking it already consumed by a train. No per-pulse frequency: FM-on-FM puts
  /// every sync at exactly 1200 Hz (plan §1.6), so Hopper's frequency gate is dropped (retro item E).</summary>
  public struct SstvPulse
  {
    public int Time;
    public float Power;
    public float DurMs;
    public bool Used;
    public SstvPulse(int time, float power, float durMs = 0)
    { Time = time; Power = power; DurMs = durMs; Used = false; }
  }

  /// <summary>Lifecycle of a pulse-train hypothesis (plan §4.1). <see cref="VisOnly"/> is the initial state
  /// of a VIS-seeded train that has not yet caught a sync pulse.</summary>
  public enum SstvTrainState { Candidate, Active, Retired, VisOnly }

  /// <summary>
  /// One MHT hypothesis (plan §4.1/§6.1, ported from Hopper's <c>TPulseTrain</c>): a candidate SSTV sync train
  /// of a specific <see cref="SstvMode"/>, seeded by a period-consistent 3-pulse triplet and tracked by an
  /// <see cref="SstvSyncRegressor"/>. New pulses are <b>associated</b> through <see cref="TryAddPulse"/> — a
  /// sync-duration family gate plus the regressor's growing time-tolerance gate — so clutter
  /// outside the gate is ignored, not fitted. A candidate <b>promotes</b> to active once it has enough pulses
  /// within the promote timeout (back-filling earlier pulses it can explain, <see cref="AddOldPulses"/>), and
  /// <b>retires</b> after a run of inactivity. The extractor owns the state transitions and picks the best
  /// train per line block by smoothed sync power (<see cref="GetPower"/>) with hysteresis.
  /// </summary>
  internal class SstvPulseTrain
  {
    private const double PromoteSeconds = 4.0;   // promote-or-kill a candidate by this idle time
    private const double RetireSeconds = 6.0;    // retire an active train after this idle time (§1.10 T_gap)
    private const int MinStrongPromote = 6;      // spawn-tier pulses required among the promotion evidence
    private const double WeakPower = 2 * SstvPulseDetector.ScoreThreshold;  // a weak last pulse holds the claim longer
    protected const int SmoothPulsesWing = 4;    // train power is smoothed over ±4 pulse slots

    protected readonly double fs;
    protected readonly double nominalPeriod;
    protected readonly double promoteTimeout;
    protected readonly double retireTimeout;
    protected readonly List<SstvPulse> pulses = new();
    private readonly int createdTime;            // seed time — bounds the promote window (N-of-M, retro P)
    private int lastPulseNo = int.MinValue;      // rejects a second pulse landing in the same line slot
    private int revisionCnt;

    /// <summary>Time of the last spawn-tier (≥ <see cref="SstvPulseDetector.ScoreThreshold"/>) pulse — the
    /// retire/idle clock. Soft associate-tier pulses may confirm and extend the fit, but with the wide RLS
    /// gate a noise pulse lands in-gate every few seconds, so letting them reset the idle clocks would keep
    /// a dead train alive indefinitely.</summary>
    protected int LastStrongTime { get; set; }

    /// <summary>Count of spawn-tier pulses on the train — the promotion evidence that soft in-gate noise
    /// cannot fake (see <see cref="HasEnoughPulses"/>).</summary>
    protected int StrongCnt { get; private set; }

    public SstvTrainState State { get; set; }
    public SstvMode Format { get; }
    public SstvSyncRegressor Regr { get; protected set; }
    public int PulseCnt => pulses.Count;
    public IReadOnlyList<SstvPulse> Pulses => pulses;

    /// <summary>Mean matched-filter power over the train's pulses — the mode-detection confidence.</summary>
    public double MeanPower
    {
      get
      {
        if (pulses.Count == 0) return 0;
        double sum = 0;
        foreach (var p in pulses) sum += p.Power;
        return sum / pulses.Count;
      }
    }

    /// <summary>Seed a train from three period-consistent pulses of <paramref name="mode"/> (ascending time).</summary>
    public SstvPulseTrain(SstvMode mode, SstvPulse p0, SstvPulse p1, SstvPulse p2, double fs)
      : this(mode, fs)
    {
      Regr = new SstvSyncRegressor(p0.Time, nominalPeriod, fs);
      Regr.ProcessPulse(p0.Time);
      Regr.ProcessPulse(p1.Time);
      Regr.ProcessPulse(p2.Time);
      pulses.Add(p0); pulses.Add(p1); pulses.Add(p2);
      createdTime = p0.Time;
      LastStrongTime = p2.Time;
      StrongCnt = 3;                             // the spawn triplet is spawn-tier by construction
      lastPulseNo = Regr.GetPulseNo(p2.Time);
      State = SstvTrainState.Candidate;
    }

    /// <summary>Shared field setup for the triplet and VIS constructors.</summary>
    protected SstvPulseTrain(SstvMode mode, double fs)
    {
      Format = mode;
      this.fs = fs;
      var spec = SstvModes.Get(mode);
      nominalPeriod = spec.LinePeriodMs / 1000.0 * fs;
      promoteTimeout = PromoteSeconds * fs;
      retireTimeout = RetireSeconds * fs;
      createdTime = 0;
      Regr = new SstvSyncRegressor(0, nominalPeriod, fs);
    }

    /// <summary>Try to associate a pulse with this train: rejected if of the wrong sync-duration family, past
    /// the image end, outside the regressor's time gate, or landing in an already-filled line
    /// slot (a duplicate would be double-fitted and double-counted toward promotion, retro P); otherwise
    /// folded in. Returns whether it was accepted.</summary>
    public virtual bool TryAddPulse(in SstvPulse pulse)
    {
      var spec = SstvModes.Get(Format);
      if (!MatchesFamily(pulse, spec)) return false;
      if (pulse.Time > Regr.GetPulseTime(spec.LineCount + 50)) return false;

      int pulseNo = Regr.GetPulseNo(pulse.Time);
      if (pulseNo == lastPulseNo) return false;
      double expected = Regr.GetPulseTime(pulseNo);
      if (Math.Abs(pulse.Time - expected) > Regr.GetMaxError(pulseNo)) return false;

      AddPulse(pulse);
      return true;
    }

    /// <summary>A pulse detected by a different family's sync template (e.g. the 20 ms PD template on a
    /// Robot train) carries a biased onset and must not be fitted. Zero DurMs (unknown) always passes.</summary>
    protected static bool MatchesFamily(in SstvPulse pulse, SstvModeSpec spec)
      => pulse.DurMs == 0 || Math.Abs(pulse.DurMs - spec.SyncMs) < 0.5;

    /// <summary>Fold an accepted pulse in: update the regressor. The pulse
    /// list stays time-sorted (back-fill inserts at the front).</summary>
    public void AddPulse(in SstvPulse pulse)
    {
      if (pulses.Count > 0 && pulse.Time < pulses[0].Time) pulses.Insert(0, pulse);
      else pulses.Add(pulse);

      if (pulse.Power >= SstvPulseDetector.ScoreThreshold)
      {
        StrongCnt++;
        if (pulse.Time > LastStrongTime) LastStrongTime = pulse.Time;
      }
      lastPulseNo = Regr.GetPulseNo(pulse.Time);
      Regr.ProcessPulse(pulse.Time);
    }

    /// <summary>On promotion, back-fill earlier detections this train explains (Hopper <c>AddOldPulses</c>):
    /// walk <paramref name="all"/> backwards from the current start, adopting unclaimed <b>spawn-tier</b>
    /// pulses that pass the association gates, back to one retire-timeout before the start. Soft pulses are
    /// excluded here: back-fill has no future evidence to confirm them, and with the wide gate a chain of
    /// in-gate noise pulses would creep the start ever backward, before the real transmission. If any were
    /// adopted, the regressor is rebuilt from the new first pulse and refitted over the whole train.
    /// Returns whether the start moved.</summary>
    public virtual bool AddOldPulses(IReadOnlyList<SstvPulse> all)
    {
      double startTime = Regr.FirstPulseTime;
      bool added = false;
      for (int i = all.Count - 1; i >= 0; i--)
      {
        if (all[i].Time >= startTime) continue;
        if (all[i].Time < startTime - retireTimeout) break;
        if (all[i].Used) continue;
        if (all[i].Power < SstvPulseDetector.ScoreThreshold) continue;
        if (TryAddPulse(all[i])) { startTime = all[i].Time; added = true; }
      }
      if (!added) return false;

      Regr = new SstvSyncRegressor((int)startTime, nominalPeriod, fs);
      foreach (var p in pulses) Regr.ProcessPulse(p.Time);
      return true;
    }

    /// <summary>Smoothed sync power near <paramref name="time"/>: the pulse power summed over the ±<see
    /// cref="SmoothPulsesWing"/> line slots around it and averaged over the <b>whole window</b> (empty slots
    /// count as zero — density-weighted), rejecting the block edge cases (no pulse in the center
    /// slot and support on only one side ⇒ before the start / past the end of the train; a center pulse with
    /// no neighbors at all ⇒ an isolated spike). Drives the extractor's best-train-per-block choice.
    /// Density weighting is the half-rate-harmonic discriminant: a Robot72 grid riding a Robot36 signal
    /// fills at most every other of its slots, so the true train out-scores it ~2:1 — enough to clear the
    /// 1.5× switch hysteresis (with the two-tier soft evidence, harmonic hypotheses promote more easily,
    /// and the density-blind mean let them keep their blocks).</summary>
    public virtual double GetPower(int time)
    {
      var neighbors = new double[2 * SmoothPulsesWing + 1];
      int centerNo = Regr.GetPulseNo(time);
      for (int i = pulses.Count - 1; i >= 0; i--)
      {
        int dist = Regr.GetPulseNo(pulses[i].Time) - centerNo;
        if (dist < -SmoothPulsesWing) break;
        if (dist <= SmoothPulsesWing) neighbors[dist + SmoothPulsesWing] = pulses[i].Power;
      }

      bool hasLeft = false, hasRight = false;
      for (int i = 1; i <= SmoothPulsesWing; i++)
      {
        hasLeft = hasLeft || neighbors[SmoothPulsesWing - i] > 0;
        hasRight = hasRight || neighbors[SmoothPulsesWing + i] > 0;
      }
      if (neighbors[SmoothPulsesWing] == 0) { if (!(hasLeft && hasRight)) return 0; }
      else { if (!(hasLeft || hasRight)) return 0; }

      double sum = 0;
      foreach (double p in neighbors) sum += p;
      return sum / neighbors.Length;
    }

    /// <summary>A candidate has enough pulses to promote to active (N-of-M over the promote window). Soft
    /// associate-tier pulses count toward the total, but at least <see cref="MinStrongPromote"/> must be
    /// spawn-tier: with the wide RLS gate, a chance noise triplet collecting in-gate soft noise could
    /// otherwise ride to promotion (seen as soft-dominated phantom trains, mean power ≈ 0.16).</summary>
    public virtual bool HasEnoughPulses
      => PulseCnt >= Math.Max(6, (int)Math.Round(0.4 * promoteTimeout / Regr.NominalPeriod))
         && StrongCnt >= MinStrongPromote;

    /// <summary>A candidate is due to be killed: idle too long (no spawn-tier pulse), or — bounding the
    /// N-of-M promote window (retro P) — older than the promote timeout without having promoted. Without
    /// the age bound, clutter that trickles one associated pulse per few seconds would stay alive and
    /// eventually promote.</summary>
    public virtual bool IsCandidateIdle(int time)
      => (time - LastStrongTime) > promoteTimeout || (time - createdTime) > promoteTimeout;

    /// <summary>An active train has gone idle long enough to retire. Two clocks: any associated pulse
    /// holds the train for one retire timeout (bridging weak stretches where only soft pulses survive),
    /// but soft-only life is bounded at twice that — with the wide RLS gate an in-gate noise pulse arrives
    /// every few seconds, so a pulse-of-any-tier clock alone would never let a dead train retire.</summary>
    public virtual bool CanRetire(int time)
      => (time - Regr.LastPulseTime) > retireTimeout || (time - LastStrongTime) > 2 * retireTimeout;

    /// <summary>Whether a retired train has stopped claiming line blocks at <paramref name="time"/>: right
    /// after its last strong pulse when that pulse was strong (the transmission clearly ended there), one
    /// retire timeout later when the tail was weak (it may just be faded).</summary>
    public virtual bool IsRetiredAt(int time)
    {
      if (State != SstvTrainState.Retired) return false;
      double retireTime = LastStrongTime;
      if (pulses.Count > 0 && pulses[^1].Power < WeakPower) retireTime += retireTimeout;
      return retireTime < time;
    }

    /// <summary>True (at most twice, at ~20 and ~40 pulses) when the timing estimate has improved enough
    /// that already-extracted lines should be revised (the extractor re-marks the dirty block).</summary>
    public bool RevisionDue()
    {
      bool due = revisionCnt < 2 && PulseCnt >= 20 * (revisionCnt + 1);
      if (due) revisionCnt++;
      return due;
    }
  }

  /// <summary>
  /// A VIS-seeded high-prior train (plan §4.1, Hopper's <c>TVisPulseTrain</c>): created when a valid VIS
  /// header is decoded, anchored at the header end (= the line-0 sync onset), in state
  /// <see cref="SstvTrainState.VisOnly"/> with no pulses yet. Because the mode and phase are already known,
  /// it promotes on just 3 confirming pulses (vs the triplet-spawned candidate's ~11) — the "strong prior"
  /// realization. The first pulse must land within a tight wing of the predicted grid; a period-consistent
  /// triplet whose grid extrapolates back to the anchor may also be adopted whole
  /// (<see cref="TryAddPulses"/>).</summary>
  internal sealed class SstvVisPulseTrain : SstvPulseTrain
  {
    private const double AnchorWingMs = 7.0;     // first-pulse gate around the predicted grid
    private const double TripletWingMs = 18.0;   // triplet-adoption gate: grid extrapolated back to the anchor

    private readonly int anchorWing;
    private readonly int tripletWing;

    /// <summary>Sample index of the VIS header end = the predicted line-0 sync onset.</summary>
    public int VisTime { get; }

    public SstvVisPulseTrain(SstvMode mode, int headerEndSample, double fs) : base(mode, fs)
    {
      VisTime = headerEndSample;
      anchorWing = (int)Math.Round(AnchorWingMs / 1000.0 * fs);
      tripletWing = (int)Math.Round(TripletWingMs / 1000.0 * fs);
      Regr = new SstvSyncRegressor(headerEndSample, nominalPeriod, fs);
      LastStrongTime = headerEndSample;
      State = SstvTrainState.VisOnly;
    }

    public override bool TryAddPulse(in SstvPulse pulse)
    {
      if (State != SstvTrainState.VisOnly && State != SstvTrainState.Candidate)
        return base.TryAddPulse(pulse);

      // promotion evidence must be spawn-tier: the VIS train promotes on just 3 confirming pulses, and
      // with the wide gate a soft in-gate noise pulse arrives often enough that 3 soft ones within the
      // window are a real false-promotion risk. Soft pulses may still confirm once Active (base path).
      if (pulse.Power < SstvPulseDetector.ScoreThreshold) return false;

      var spec = SstvModes.Get(Format);
      if (!MatchesFamily(pulse, spec)) return false;
      if (pulse.Time < VisTime - anchorWing) return false;

      int pulseNo = Regr.GetPulseNo(pulse.Time);
      if (pulseNo > spec.LineCount + 1) return false;
      double expected = Regr.GetPulseTime(pulseNo);
      int gate = PulseCnt == 0 ? anchorWing : (int)Regr.GetMaxError(pulseNo);
      if (Math.Abs(pulse.Time - expected) > gate) return false;

      AddPulse(pulse);
      State = SstvTrainState.Candidate;
      return true;
    }

    /// <summary>Adopt a period-consistent triplet if a line fit through it extrapolates back to the VIS
    /// anchor: the extractor offers each fresh triplet here before spawning a plain candidate. The triplet
    /// must lie inside the image span the anchor predicts — over hundreds of periods the ±18 ms
    /// extrapolation gate alone is nearly vacuous, and without the span gate a noise triplet minutes
    /// before the anchor could hijack the train (seen on the UmKA-1 capture).</summary>
    public bool TryAddPulses(in SstvPulse p0, in SstvPulse p1, in SstvPulse p2)
    {
      if (p0.Time < VisTime - anchorWing) return false;
      if (Regr.GetPulseNo(p2.Time) > SstvModes.Get(Format).LineCount + 1) return false;

      var fit = new SstvSyncRegressor(p0.Time, nominalPeriod, fs);
      fit.ProcessPulse(p0.Time);
      fit.ProcessPulse(p1.Time);
      fit.ProcessPulse(p2.Time);
      if (Math.Abs(fit.GetPulseTime(fit.GetPulseNo(VisTime)) - VisTime) >= tripletWing) return false;

      AddPulse(p0);
      AddPulse(p1);
      AddPulse(p2);
      State = SstvTrainState.Candidate;
      return true;
    }

    /// <summary>The VIS anchor already fixes the start; there is nothing older to explain.</summary>
    public override bool AddOldPulses(IReadOnlyList<SstvPulse> all) => false;

    /// <summary>The VIS prior stands in for the triplet-candidate's pulse count: 3 confirming pulses suffice.</summary>
    public override bool HasEnoughPulses => PulseCnt >= 3;

    /// <summary>A VIS train is given twice the normal promote window before being killed.</summary>
    public override bool IsCandidateIdle(int time) => (time - LastStrongTime) > 2 * promoteTimeout;

    /// <summary>The mode is known, so the train retires exactly at its predicted image end.</summary>
    public override bool CanRetire(int time)
      => Regr.GetPulseTime(SstvModes.Get(Format).LineCount - 1) < time;

    public override bool IsRetiredAt(int time) => State == SstvTrainState.Retired && CanRetire(time);

    /// <summary>Between the VIS header and the first few syncs the normal ±wing smoothing has no support
    /// yet; hold the block claim with the latest pulse's power so the image start is not lost.</summary>
    public override double GetPower(int time)
    {
      double power = base.GetPower(time);
      if (power == 0 && PulseCnt > 0 && time > VisTime
          && Regr.GetPulseNo(time) < SmoothPulsesWing + 1)
        power = pulses[^1].Power;
      return power;
    }
  }
}
