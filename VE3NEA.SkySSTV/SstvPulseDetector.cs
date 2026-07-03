using System;
using System.Collections.Generic;

namespace VE3NEA.SkySSTV
{
  /// <summary>
  /// Streaming 1200 Hz sync-pulse detector — the full separable zero-mean 2D matched filter (plan §4/§4.1,
  /// retro items A/C/N). <b>Frequency axis:</b> energy-normalized coherence
  /// <c>g(t) = |Σ x·e^{−jωn}|² / (W·E)</c> over a short centered window — amplitude-invariant and blind to
  /// broadband splatter (FM is constant-envelope, so only the <i>fraction</i> of energy at 1200 Hz separates
  /// a real sync, g ≈ 0.5, from a separator step, g ≈ 0.002). <b>Time axis:</b> a bipolar boxcar of the mode
  /// family's sync length L — <c>score(t) = mean(g over [t, t+L)) − ½·mean(g over the equal flanks)</c> —
  /// which drives a sustained 1200 Hz carrier (no time contrast) to ~0 and turns the flat-topped coherence
  /// into a triangular peak whose argmax is the pulse <b>onset</b> (the P3 centroid heuristic is gone).
  /// One detector instance per sync-duration family (Robot 9 ms / PD 20 ms); emitted pulses carry the
  /// template length, so the Robot-vs-PD discriminant survives into the MHT (retro C).
  ///
  /// <para>All state is bounded (two rings + running sums) and long-run stable (retro N): the mixer runs on
  /// a complex-rotation recurrence with periodic renormalization — never <c>cos(ω·i)</c> at an unbounded
  /// absolute index — and every running sum is periodically re-accumulated from its ring, so floating-point
  /// drift cannot grow over a multi-minute pass. Input is the Stage-2 bandpassed audio
  /// (<see cref="SstvDecoder.SyncAudio"/>), which owns DC/out-of-band rejection (retro J).</para>
  /// </summary>
  internal sealed class SstvPulseDetector
  {
    private const double FreqWindowMs = 4.0;      // coherence window: several 1200 Hz cycles, < any sync
    internal const double ScoreThreshold = 0.18;  // spawn tier: score to seed a new hypothesis (clean sync ≈ 0.33+)
    internal const double AssocThreshold = 0.10;  // associate tier: soft pulses that may only CONFIRM a train

    /// <summary>Per-pulse emission threshold, default <see cref="ScoreThreshold"/>. The extractor runs its
    /// detectors at <see cref="AssocThreshold"/> — the two-tier soft-evidence scheme (plan §4.1): every
    /// emitted pulse may associate with an existing train (the tight RLS gate discriminates), but the
    /// extractor spawns a triplet only from <see cref="ScoreThreshold"/>-strong members.</summary>
    internal double Threshold { get; init; } = ScoreThreshold;

    private const int RenormInterval = 4096;      // oscillator re-normalization cadence (samples)
    private const int ReanchorInterval = 1 << 20; // running-sum re-accumulation cadence (samples)

    private readonly double fs;
    private readonly int win;                     // frequency-axis window W (samples)
    private readonly int pulseLen;                // time-axis template length L (samples)
    private readonly float durMs;                 // the family's sync duration carried on each pulse

    // 1200 Hz mixer (recurrence) + per-sample contribution rings and their running sums
    private readonly double cw, sw;
    private double oscRe = 1, oscIm;
    private readonly double[] mixC, mixS, mixE;
    private double sumC, sumS, sumE;

    // coherence ring (3L) + bipolar span sums, peak-picking state
    private readonly double[] gRing;
    private double sumL, sumP, sumR;
    private long n;                               // input samples processed
    private long m = -1;                          // last g index produced
    private bool inRun;
    private double runBest;
    private long runBestT;
    private long lastEmitT = long.MinValue;

    /// <summary>Largest bipolar matched-filter score seen so far, thresholded or not — a probe for the
    /// P6(c) threshold experiments and the real-capture score measurements.</summary>
    public double MaxScore { get; private set; }

    /// <summary><paramref name="windowMs"/> overrides the coherence-window length for the
    /// longer-coherent-integration experiments (wider = more coherent gain at 1200 Hz, blunter time
    /// peak; must stay under the family's sync duration).</summary>
    public SstvPulseDetector(double fs, double pulseLenMs, double windowMs = FreqWindowMs)
    {
      this.fs = fs;
      durMs = (float)pulseLenMs;
      win = Math.Max(8, (int)Math.Round(windowMs / 1000.0 * fs));
      pulseLen = Math.Max(win, (int)Math.Round(pulseLenMs / 1000.0 * fs));

      double w = 2 * Math.PI * SstvTones.Sync / fs;
      cw = Math.Cos(w); sw = Math.Sin(w);
      mixC = new double[win]; mixS = new double[win]; mixE = new double[win];
      gRing = new double[3 * pulseLen];
    }

    /// <summary>Detect the sync-pulse train in <paramref name="audio"/> (Stage-2 bandpassed). Offline
    /// convenience that streams the samples through <see cref="Process"/>; state is bounded throughout.</summary>
    public List<SstvPulse> Detect(double[] audio)
    {
      var pulses = new List<SstvPulse>();
      for (int i = 0; i < audio.Length; i++) Process(audio[i], pulses);
      Flush(pulses);
      return pulses;
    }

    /// <summary>Push one sample; completed pulses are appended to <paramref name="pulses"/>.</summary>
    public void Process(double x, List<SstvPulse> pulses)
    {
      // mixer: rotate e^{−jωn} by recurrence, fold the sample into the window sums via the ring
      double c = x * oscRe, s = x * oscIm, e = x * x;
      int slot = (int)(n % win);
      sumC += c - mixC[slot]; sumS += s - mixS[slot]; sumE += e - mixE[slot];
      mixC[slot] = c; mixS[slot] = s; mixE[slot] = e;

      double t = oscRe * cw + oscIm * sw;
      oscIm = oscIm * cw - oscRe * sw;
      oscRe = t;
      if (n % RenormInterval == RenormInterval - 1)
      {
        double mag = Math.Sqrt(oscRe * oscRe + oscIm * oscIm);
        if (mag > 0) { oscRe /= mag; oscIm /= mag; }
      }
      if (n % ReanchorInterval == ReanchorInterval - 1) Reanchor();

      if (n >= win - 1) PushG(sumE > 0 ? (sumC * sumC + sumS * sumS) / (win * sumE) : 0.0, pulses);
      n++;
    }

    /// <summary>Close a pulse still above threshold at the end of the stream.</summary>
    public void Flush(List<SstvPulse> pulses)
    {
      if (inRun) Emit(pulses);
      inRun = false;
    }

    /// <summary>Advance the time axis: fold a new coherence value into the three bipolar spans and
    /// peak-pick the score. The score computed here is for onset <c>t = m − 2L + 1</c> (its right flank
    /// ends at the newest g), i.e. the detector runs at a fixed 2L + W/2 latency.</summary>
    private void PushG(double g, List<SstvPulse> pulses)
    {
      m++;
      int size = gRing.Length, l = pulseLen;
      double v1 = gRing[(int)((m + size - l) % size)];        // leaves the right span → pulse span
      double v2 = gRing[(int)((m + size - 2 * l) % size)];    // leaves the pulse span → left span
      double v3 = gRing[(int)(m % size)];                     // leaves the left span (slot being overwritten)
      sumR += g - v1; sumP += v1 - v2; sumL += v2 - v3;
      gRing[(int)(m % size)] = g;

      if (m < size - 1) return;                               // rings not warm yet
      long onset = m - 2 * l + 1;
      double score = (sumP - 0.5 * (sumL + sumR)) / l;
      if (score > MaxScore) MaxScore = score;

      if (score > Threshold)
      {
        if (!inRun) { inRun = true; runBest = score; runBestT = onset; }
        else if (score > runBest) { runBest = score; runBestT = onset; }
      }
      else if (inRun)
      {
        inRun = false;
        Emit(pulses);
      }
    }

    /// <summary>Emit the current run's argmax as a pulse (absolute input-sample onset). Runs closer than
    /// two template lengths are merged, keeping the stronger — threshold chatter cannot double-emit.</summary>
    private void Emit(List<SstvPulse> pulses)
    {
      long tAbs = runBestT + (win - 1) - win / 2;             // g index → input-sample time (centered window)
      if (pulses.Count > 0 && tAbs - lastEmitT < 2 * pulseLen)
      {
        if (runBest > pulses[^1].Power)
          pulses[^1] = new SstvPulse((int)tAbs, (float)runBest, durMs);
        return;
      }
      pulses.Add(new SstvPulse((int)tAbs, (float)runBest, durMs));
      lastEmitT = tAbs;
    }

    /// <summary>Re-accumulate every running sum from its ring so add/subtract floating-point drift is
    /// bounded regardless of stream length (retro N).</summary>
    private void Reanchor()
    {
      sumC = 0; sumS = 0; sumE = 0;
      for (int i = 0; i < win; i++) { sumC += mixC[i]; sumS += mixS[i]; sumE += mixE[i]; }

      sumL = 0; sumP = 0; sumR = 0;
      int size = gRing.Length, l = pulseLen;
      for (int k = 0; k < l; k++)
      {
        sumR += gRing[(int)((m + size - k) % size)];
        sumP += gRing[(int)((m + size - l - k) % size)];
        sumL += gRing[(int)((m + size - 2 * l - k) % size)];
      }
    }
  }
}
