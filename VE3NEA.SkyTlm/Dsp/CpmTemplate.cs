using System.Collections.Concurrent;
using MathNet.Numerics;
using VE3NEA.SkyTlm.Core;
using VE3NEA;

namespace VE3NEA.SkyTlm.Dsp
{
  /// <summary>
  /// One expected-shape hypothesis from <see cref="CpmTemplate.SynthesizeBank"/>: a candidate synthesized
  /// spectrum with a short display name ("rect h=0.5", "gauss h=1") and its shape class — <see cref="Bell"/>
  /// when the spectrum peaks at the carrier (smooth lobe: MSK, GMSK/GFSK), false for two-tone (a DC valley
  /// between tones at ±dev). The class picks the per-hypothesis validation threshold
  /// (<see cref="Core.StreamingOptions.EffectiveMinShapeScore(ShapeHypothesis)"/>): an analog interloper
  /// (SSTV) can mimic a bell-shaped burst average but not two symmetric tones.
  /// </summary>
  public sealed record ShapeHypothesis(string Name, LearnedShape Shape, bool Bell);

  /// <summary>
  /// Numerically-synthesized CPM power spectrum: modulate a long random NRZ stream with the actual frequency
  /// pulse (Gaussian premod for GMSK/GFSK, rectangular for plain FSK) and modulation index <c>h = 2·dev/Rs</c>,
  /// then take a Welch-averaged periodogram. This reproduces the real spectrum — two tones at <c>±dev</c> with
  /// the correct filling between them (set by <i>h</i> and the pulse BT), and the discrete lines at integer
  /// <i>h</i>. Returned as a <see cref="LearnedShape"/> (baud-normalized, peak = 1); cached per parameter set.
  ///
  /// <para>Used as the expected-shape template for <b>burst selection</b> (see <see cref="Match"/>): after the
  /// energy detector finds candidate spans, each burst's averaged spectrum is correlated with the hypothesis
  /// bank (<see cref="SynthesizeBank"/>) to accept/reject it — uniformly across all FSK flavors. Also rendered
  /// in the WinForms shape view.</para>
  /// </summary>
  internal static class CpmTemplate
  {
    private readonly record struct Key(PulseShape Pulse, int BaudHz, int DevHz);
    private static readonly ConcurrentDictionary<Key, LearnedShape> cache = new();

    private readonly record struct AfskKey(int BaudHz, int MarkHz, int SpaceHz, int RfDevHz);
    private static readonly ConcurrentDictionary<AfskKey, LearnedShape> afskCache = new();

    /// <summary>Synthesized PSD template for the params, resampled onto the <see cref="LearnedShape"/> grid (cached).</summary>
    public static LearnedShape Synthesize(SignalParams p)
    {
      double baud = p.Baud;

      // linear PSK is NOT a CPM/FSK signal — it has no frequency pulse or deviation. Its PSD is the
      // raised-cosine (|RRC|²) baseband lobe, built analytically rather than by FM-synthesizing tones.
      if (p.Modulation is Modulation.BPSK or Modulation.QPSK)
      {
        var pskKey = new Key(PulseShape.None, (int)System.Math.Round(baud), 0);
        return cache.GetOrAdd(pskKey, _ => BuildPsk(baud, ModulationTemplate.PskRolloff));
      }

      (PulseShape pulse, double bt, double dev) = ResolveCpm(p);
      return Cached(pulse, bt, baud, dev);
    }

    /// <summary>
    /// Expected-shape hypothesis bank for burst validation. The DB label + deviation pick a single spectral
    /// model, but the labels are unreliable (SatNOGS "GFSK9k6" for unfiltered FSK, "MSK4k8" with the tone
    /// spacing in the deviation field), and a wrong model changes the spectrum <b>class</b> — integer-h
    /// rectangular CPFSK has discrete tones at ±dev where shaped / h≤1 signals have a smooth line-free lobe.
    /// So an FSK-family burst is matched against a few canonical CPM shapes and the best fit wins (see
    /// <see cref="MatchBest"/>): the labeled model, rectangular h=0.5 (true MSK), rectangular h=1 (Sunde FSK),
    /// and Gaussian BT=0.5 at the labeled deviation (shaped GFSK). Hypotheses duplicating the labeled model
    /// are dropped; non-CPM modulations keep the single labeled template.
    /// </summary>
    public static IReadOnlyList<ShapeHypothesis> SynthesizeBank(SignalParams p)
    {
      double baud = p.Baud;
      // non-CPM labels: the single labeled template, with today's two-tone (0.55) threshold class
      if (p.Modulation is not (Modulation.FSK or Modulation.AFSK or Modulation.GFSK or Modulation.GMSK))
        return new[] { new ShapeHypothesis(p.Modulation.ToString(), Synthesize(p), Bell: false) };

      // AFSK-over-FM has no RF-domain deviation to plug into the plain two-tone CPFSK model below (see
      // SynthesizeAfskBank) — its RF spectrum is a different shape entirely.
      if (p.Modulation == Modulation.AFSK) return SynthesizeAfskBank(p);

      var (pulse, bt, dev) = ResolveCpm(p);
      var candidates = new (PulseShape pulse, double bt, double dev, bool labeled)[]
      {
        (pulse, bt, dev, true),                            // the labeled model (the single-template behavior)
        (PulseShape.Rectangular, 0.0, baud / 4.0, false),  // rect h=0.5: true MSK — smooth compact lobe
        (PulseShape.Rectangular, 0.0, baud / 2.0, false),  // rect h=1: Sunde FSK — discrete tones at ±Rs/2
        (PulseShape.Gaussian, 0.5, dev, false),            // Gaussian BT=0.5 at the labeled deviation: shaped GFSK
      };

      var bank = new List<ShapeHypothesis>(candidates.Length);
      var seen = new HashSet<(PulseShape, int)>();
      foreach (var c in candidates)
      {
        if (!seen.Add((c.pulse, (int)System.Math.Round(c.dev)))) continue;
        var shape = Cached(c.pulse, c.bt, baud, c.dev);
        // the labeled entry keeps the modulation-based class (the exact thresholds the single template got);
        // canonical entries are classified from the shape itself (peak at the carrier = bell).
        bool bell = c.labeled ? p.Modulation is Modulation.GMSK or Modulation.GFSK : shape.SampleAtBaud(0) >= 0.5;
        string name = $"{(c.pulse == PulseShape.Gaussian ? "gauss" : "rect")} h={2 * c.dev / baud:0.##}";
        bank.Add(new ShapeHypothesis(name, shape, bell));
      }
      return bank;
    }

    /// <summary>Best <see cref="Match"/> over the hypothesis bank: the highest score and the hypothesis that
    /// produced it.</summary>
    public static (double Match, ShapeHypothesis Best) MatchBest(LearnedShape measured, IReadOnlyList<ShapeHypothesis> bank)
    {
      double best = double.NegativeInfinity;
      ShapeHypothesis bestHyp = bank[0];
      foreach (var h in bank)
      {
        double m = Match(measured, h.Shape);
        if (m > best) { best = m; bestHyp = h; }
      }
      return (best, bestHyp);
    }

    /// <summary>
    /// AFSK-over-FM hypothesis bank. <see cref="SignalParams.Deviation"/> for AFSK is the <b>audio</b> tone
    /// half-spacing after FM discrimination (Bell-202: 500 Hz mark/space straddling <see
    /// cref="SignalParams.AfCarrier"/> — see <see cref="AfskDemodulator"/>), not an RF-domain deviation, so the
    /// plain two-tone CPFSK model <see cref="ResolveCpm"/> builds for FSK does not represent this signal's
    /// RF/IQ spectrum at all: it is a carrier-dominated FM subcarrier — the continuous-phase Bell-202 audio tone
    /// itself frequency-modulates the RF carrier, at an RF deviation the transmitter sets and
    /// <see cref="SignalParams"/> has no field for. Numerically synthesize that two-stage (audio-CPFSK-into-RF-FM)
    /// signal at a small sweep of plausible RF deviations bracketing the ~6 kHz Carson bandwidth
    /// <see cref="AfskDemodulator.DefaultRfBandwidthHz"/> assumes, and let <see cref="MatchBest"/> pick whichever
    /// the measured burst actually looks like.
    /// </summary>
    private static IReadOnlyList<ShapeHypothesis> SynthesizeAfskBank(SignalParams p)
    {
      double baud = p.Baud;
      double audioDev = p.Deviation is double d && d > 0 ? d : AfskDemodulator.DefaultDeviationHz;
      double afCarrier = p.AfCarrier ?? AfskDemodulator.DefaultAfCarrierHz;
      double markHz = afCarrier - audioDev, spaceHz = afCarrier + audioDev;

      var rfDevsHz = new[] { 1500.0, 2500.0, 3500.0, 4500.0 };
      var bank = new List<ShapeHypothesis>(rfDevsHz.Length);
      foreach (double rfDev in rfDevsHz)
      {
        var shape = CachedAfskSubcarrier(baud, markHz, spaceHz, rfDev);
        // carrier-dominated FM: peaks at the carrier for any deviation in this range, so it is scored as a
        // "bell" shape (the stricter analog-interloper threshold), same class as GMSK/GFSK.
        bank.Add(new ShapeHypothesis($"AFSK subcarrier {rfDev / 1000:0.#}k", shape, Bell: true));
      }
      return bank;
    }

    /// <summary>The (pulse, BT, deviation) triple a labeled CPM modulation maps to — the single-model rules
    /// the synthesis has always used for the labeled template.</summary>
    private static (PulseShape pulse, double bt, double dev) ResolveCpm(SignalParams p) => p.Modulation switch
    {
      Modulation.GMSK or Modulation.GFSK => (PulseShape.Gaussian, 0.5, p.Deviation ?? p.Baud / 4.0),
      Modulation.FSK or Modulation.AFSK => (PulseShape.Rectangular, 0.0, p.Deviation is double d && d > 0 ? d : p.Baud * 0.5),
      _ => (PulseShape.Gaussian, 0.5, p.Deviation ?? p.Baud / 4.0),
    };

    /// <summary>The synthesized shape for one (pulse, BT, baud, dev) model, via the cache.</summary>
    private static LearnedShape Cached(PulseShape pulse, double bt, double baud, double dev)
    {
      var key = new Key(pulse, (int)System.Math.Round(baud), (int)System.Math.Round(dev));
      return cache.GetOrAdd(key, _ => Build(baud, dev, pulse, bt));
    }

    /// <summary>Analytic raised-cosine PSD template for linear PSK, sampled onto the <see cref="LearnedShape"/>
    /// grid (peak = 1).</summary>
    private static LearnedShape BuildPsk(double baud, double rolloff)
    {
      int n = LearnedShape.GridPoints;
      var profile = new float[n];
      for (int i = 0; i < n; i++)
      {
        double f = LearnedShape.BaudAt(i) * baud;       // grid offset (Hz) from the carrier
        profile[i] = (float)ModulationTemplate.RaisedCosine(f, baud, rolloff);
      }
      return new LearnedShape { DeviationHz = 0, BandwidthHz = 0, Profile = profile, Count = 1 };
    }

    /// <summary>
    /// Correlate a burst's <b>averaged</b> measured spectrum with the expected template (both baud-normalized
    /// <see cref="LearnedShape"/> profiles) to decide if the burst is the expected modulation. Pearson over a
    /// window 2× the template's support (so the signal occupies the centre and the template's zero skirt frames
    /// it), <b>clipped to the measured spectrum's valid extent</b> (<see cref="LearnedShape.ValidHalfBaud"/>): a
    /// measured burst is band-limited to the detector's occupied band, so beyond it the profile is hard zero —
    /// correlating into that region adds a shared −40 dB plateau (the band-limit edge) to both vectors that
    /// dominates the dB-domain Pearson and lets band-limited NOISE mimic the modulation lobe. Comparing only over
    /// real data, a true burst (energy in the centre, floor in the skirt) scores high while flat/filtered noise
    /// (no centre excess) scores ~0. Returns r∈[−1,1].
    /// </summary>
    public static double Match(LearnedShape measured, LearnedShape template)
    {
      TemplateWindow(template, out double w);
      // clip the window to where the MEASURED spectrum actually carries data: a measured burst spectrum is
      // band-limited to the detector's occupied band, so beyond ±ValidHalfBaud it is hard zero (no data), not a
      // spectral floor. Correlating into that region adds a shared −40 dB plateau to both vectors (the template's
      // own zero skirt), which dominates the dB-domain Pearson and makes band-limited NOISE — a lobe ending at the
      // filter edge — correlate highly with the modulation lobe. Comparing only over real data removes the artifact.
      double wEff = System.Math.Min(w, measured.ValidHalfBaud);
      if (wEff <= 0) return 0;
      const int M = 161;
      var t = Slice(template, -wEff, wEff, M);
      var s = Slice(measured, -wEff, wEff, M);       // measured slice, lag 0 (carrier-aligned)
      return Ncc(ToLog(s), ToLog(t));                // correlate in dB (log power)
    }

    /// <summary>
    /// The normalized cross-correlation behind <see cref="Match"/>: at each frequency lag, take the equal-length
    /// <b>slice</b> of the measured spectrum under the template, DC-subtract and unit-scale <i>both</i> the slice
    /// and the template independently, and correlate. Returns the lag axis (baud units) and r∈[−1,1] at each lag.
    /// The peak (≈ the <see cref="Match"/> value) sits at the frequency-alignment offset. For the shape view.
    /// </summary>
    public static (double[] lagBaud, double[] corr) Correlation(LearnedShape measured, LearnedShape template, double maxLagBaud = 1.0)
    {
      TemplateWindow(template, out double w);
      const int Lags = 81, M = 161;
      var lag = new double[Lags]; var corr = new double[Lags];
      double wBase = System.Math.Min(w, measured.ValidHalfBaud);   // lag-0 window (matches Match)
      if (wBase <= 0) return (lag, corr);
      for (int li = 0; li < Lags; li++)
      {
        double L = -maxLagBaud + 2.0 * maxLagBaud * li / (Lags - 1);
        lag[li] = L;
        // shrink the window at this lag so the shifted slice stays inside the measured band (see Match): a lag
        // that would slide the window onto the band-limit's hard-zero skirt is narrowed, not allowed to fabricate
        // correlation. Lags so large that nothing valid remains score 0.
        double wEff = System.Math.Min(wBase, measured.ValidHalfBaud - System.Math.Abs(L));
        if (wEff <= 0) continue;
        var tLog = ToLog(Slice(template, -wEff, wEff, M));
        var s = Slice(measured, -wEff + L, wEff + L, M);    // slice the measured spectrum at this lag
        corr[li] = Ncc(ToLog(s), tLog);                     // correlate in dB (log power)
      }
      return (lag, corr);
    }

    /// <summary>
    /// The exact windowed, z-scored (mean-subtracted, unit-variance) dB slices that <see cref="Match"/> feeds
    /// into <see cref="Ncc"/> — i.e. what the detector actually compares, not the full peak-normalized profile
    /// <see cref="LearnedShape.Profile"/> holds. Returns the shared frequency-offset axis (Hz, lag 0) and both
    /// z-scored curves; empty arrays when the window collapses (<see cref="Match"/> would return 0).
    /// </summary>
    public static (double[] hz, double[] measuredZ, double[] templateZ) MatchWindow(LearnedShape measured, LearnedShape template, double baud)
    {
      TemplateWindow(template, out double w);
      double wEff = System.Math.Min(w, measured.ValidHalfBaud);
      if (wEff <= 0) return (System.Array.Empty<double>(), System.Array.Empty<double>(), System.Array.Empty<double>());
      const int M = 161;
      var t = Slice(template, -wEff, wEff, M);
      var s = Slice(measured, -wEff, wEff, M);
      var hz = new double[M];
      for (int k = 0; k < M; k++) hz[k] = (-wEff + 2.0 * wEff * k / (M - 1)) * baud;
      return (hz, ZScore(ToLog(s)), ZScore(ToLog(t)));
    }

    /// <summary>Mean-subtract and unit-variance-scale a vector (population std). Flat input maps to all zeros.</summary>
    private static double[] ZScore(double[] x)
    {
      int n = x.Length;
      double m = 0; for (int i = 0; i < n; i++) m += x[i]; m /= n;
      double v = 0; for (int i = 0; i < n; i++) { double d = x[i] - m; v += d * d; }
      double sd = System.Math.Sqrt(v / n);
      var y = new double[n];
      if (sd <= 0) return y;
      for (int i = 0; i < n; i++) y[i] = (x[i] - m) / sd;
      return y;
    }

    /// <summary>Half-width (baud) of the correlation window: 2× the template's significant support (≥5% of
    /// peak), so the central half holds the signal and the outer half is the template's zero skirt. 0 when the
    /// template has no support.</summary>
    private static void TemplateWindow(LearnedShape template, out double w)
    {
      double peak = 0; foreach (var v in template.Profile) if (v > peak) peak = v;
      double supBaud = 0;
      if (peak > 0)
      {
        double th = 0.05 * peak;
        for (int i = 0; i < template.Profile.Length; i++)
          if (template.Profile[i] >= th) { double fb = System.Math.Abs(LearnedShape.BaudAt(i)); if (fb > supBaud) supBaud = fb; }
      }
      w = 2.0 * supBaud;
    }

    /// <summary>Sample a shape's profile at <paramref name="n"/> equally-spaced baud offsets over [lo, hi].</summary>
    private static double[] Slice(LearnedShape shape, double lo, double hi, int n)
    {
      var a = new double[n];
      for (int k = 0; k < n; k++) a[k] = shape.SampleAtBaud(lo + (hi - lo) * k / (n - 1));
      return a;
    }

    /// <summary>Floor for the log-power conversion: −40 dB below the peak-normalized (=1) spectrum, so the
    /// zero skirts map to a finite −40 dB instead of −∞.</summary>
    public const double LogFloor = 1e-4;

    /// <summary>Convert a peak-normalized power vector to dB with a floor (10·log10, clamped to <see cref="LogFloor"/>).</summary>
    public static double[] ToLog(double[] x)
    {
      var y = new double[x.Length];
      for (int i = 0; i < x.Length; i++) y[i] = 10.0 * System.Math.Log10(System.Math.Max(x[i], LogFloor));
      return y;
    }

    /// <summary>Normalized cross-correlation of two equal-length vectors: DC-subtract and unit-scale BOTH, then
    /// dot. (Pearson r — flat input → 0 regardless of level; a shape match → ~1.)</summary>
    private static double Ncc(double[] s, double[] t)
    {
      int n = s.Length;
      double sm = 0, tm = 0;
      for (int i = 0; i < n; i++) { sm += s[i]; tm += t[i]; }
      sm /= n; tm /= n;
      double num = 0, sv = 0, tv = 0;
      for (int i = 0; i < n; i++) { double ds = s[i] - sm, dt = t[i] - tm; num += ds * dt; sv += ds * ds; tv += dt * dt; }
      return sv > 0 && tv > 0 ? num / (System.Math.Sqrt(sv) * System.Math.Sqrt(tv)) : 0;
    }

    private static LearnedShape Build(double baud, double dev, PulseShape pulse, double bt)
    {
      var (freqHz, psd) = SynthesizePsd(baud, dev, pulse, bt);

      int n = LearnedShape.GridPoints;
      var profile = new float[n];
      double peak = 1e-30;
      for (int i = 0; i < n; i++)
      {
        double f = LearnedShape.BaudAt(i) * baud;       // grid offset (Hz) from the carrier
        float v = (float)Sample(freqHz, psd, f);
        profile[i] = v;
        if (v > peak) peak = v;
      }
      for (int i = 0; i < n; i++) profile[i] = (float)(profile[i] / peak);

      return new LearnedShape { DeviationHz = dev, BandwidthHz = 0, Profile = profile, Count = 1 };
    }

    /// <summary>The synthesized AFSK-over-FM PSD template for one RF deviation, resampled onto the
    /// <see cref="LearnedShape"/> grid (cached).</summary>
    private static LearnedShape CachedAfskSubcarrier(double baud, double markHz, double spaceHz, double rfDevHz)
    {
      var key = new AfskKey((int)System.Math.Round(baud), (int)System.Math.Round(markHz), (int)System.Math.Round(spaceHz), (int)System.Math.Round(rfDevHz));
      return afskCache.GetOrAdd(key, _ => BuildAfskSubcarrier(baud, markHz, spaceHz, rfDevHz));
    }

    private static LearnedShape BuildAfskSubcarrier(double baud, double markHz, double spaceHz, double rfDevHz)
    {
      var (freqHz, psd) = SynthesizeAfskSubcarrierPsd(baud, markHz, spaceHz, rfDevHz);

      int n = LearnedShape.GridPoints;
      var profile = new float[n];
      double peak = 1e-30;
      for (int i = 0; i < n; i++)
      {
        double f = LearnedShape.BaudAt(i) * baud;       // grid offset (Hz) from the carrier
        float v = (float)Sample(freqHz, psd, f);
        profile[i] = v;
        if (v > peak) peak = v;
      }
      for (int i = 0; i < n; i++) profile[i] = (float)(profile[i] / peak);

      return new LearnedShape { DeviationHz = rfDevHz, BandwidthHz = 0, Profile = profile, Count = 1 };
    }

    /// <summary>
    /// Random-data AFSK-over-FM synthesis → Welch PSD: a continuous-phase Bell-202 audio tone (alternating
    /// mark/space, non-coherent CPFSK at the data baud rate) itself frequency-modulates an RF carrier at
    /// <paramref name="rfDevHz"/> peak deviation — the two-stage "FM of an FSK audio tone" that
    /// <see cref="AfskDemodulator"/>'s FM-discriminate-then-tone-correlate front end assumes. Integrated
    /// sample-by-sample (same Euler-integrator style as <see cref="SynthesizePsd"/>) rather than solved via the
    /// classical Bessel-line FM spectrum, so the same random-data/Welch-PSD machinery applies unchanged.
    /// Returns (freqHz ascending, peak-normalized power).
    /// </summary>
    private static (double[] freqHz, double[] psd) SynthesizeAfskSubcarrierPsd(double baud, double markHz, double spaceHz, double rfDevHz)
    {
      const int sps = 16;   // headroom above the 2·spaceHz + 2·rfDevHz instantaneous excursion
      const int nSym = 1 << 16;
      int n = nSym * sps;
      double fsSyn = sps * baud;

      // random mark/space bits (deterministic seed → stable, cacheable shape)
      var rnd = new System.Random(0x5EED);
      var s = new Complex32[n];
      double audioPhase = 0, rfPhase = 0;
      for (int k = 0; k < nSym; k++)
      {
        double toneHz = rnd.Next(2) == 0 ? markHz : spaceHz;
        double audioStep = 2.0 * System.Math.PI * toneHz / fsSyn;
        double rfStep = 2.0 * System.Math.PI * rfDevHz / fsSyn;
        int b = k * sps;
        for (int i = 0; i < sps; i++)
        {
          audioPhase += audioStep;                                  // continuous-phase audio tone (mark/space)
          rfPhase += rfStep * System.Math.Cos(audioPhase);           // that tone FM-modulates the RF carrier
          s[b + i] = new Complex32((float)System.Math.Cos(rfPhase), (float)System.Math.Sin(rfPhase));
        }
      }

      const int L = 8192, hop = L / 2;
      float[] win = global::VE3NEA.Dsp.BlackmanHarrisWindow(L);
      var acc = new double[L];
      int blocks = 0;
      using (var fft = new Fft<Complex32>(L, NativeFftw.FftwFlags.Estimate))
      {
        for (int off = 0; off + L <= n; off += hop)
        {
          for (int i = 0; i < L; i++) fft.InputData[i] = s[off + i] * win[i];
          fft.Execute();
          for (int k = 0; k < L; k++)
          {
            var c = fft.OutputData[k];
            acc[k] += (double)c.Real * c.Real + (double)c.Imaginary * c.Imaginary;
          }
          blocks++;
        }
      }

      double binHz = fsSyn / L;
      var freq = new double[L];
      var val = new double[L];
      double max = 1e-30;
      for (int k = 0; k < L; k++)
      {
        int signed = k <= L / 2 ? k : k - L;
        freq[k] = signed * binHz;
        val[k] = blocks > 0 ? acc[k] / blocks : 0;
        if (val[k] > max) max = val[k];
      }
      for (int k = 0; k < L; k++) val[k] /= max;
      System.Array.Sort(freq, val);
      return (freq, val);
    }

    /// <summary>Random-data CPM synthesis → Welch PSD. Returns (freqHz ascending, peak-normalized power).</summary>
    private static (double[] freqHz, double[] psd) SynthesizePsd(double baud, double dev, PulseShape pulse, double bt)
    {
      const int sps = 8;
      const int nSym = 1 << 16;
      int n = nSym * sps;
      double fsSyn = sps * baud;

      // random ±1 NRZ staircase (deterministic seed → stable, cacheable shape)
      var rnd = new System.Random(0x5EED);
      var stair = new float[n];
      for (int k = 0; k < nSym; k++)
      {
        float a = rnd.Next(2) == 0 ? -1f : 1f;
        int b = k * sps;
        for (int i = 0; i < sps; i++) stair[b + i] = a;
      }

      float[] shaped = pulse == PulseShape.Gaussian ? GaussianPremod(stair, sps, bt) : stair;

      // FM integrate: a sustained ±1 reaches ±dev Hz → phase step 2π·dev/fsSyn per sample.
      var s = new Complex32[n];
      double kp = 2.0 * System.Math.PI * dev / fsSyn;
      double phase = 0;
      for (int i = 0; i < n; i++)
      {
        phase += kp * shaped[i];
        s[i] = new Complex32((float)System.Math.Cos(phase), (float)System.Math.Sin(phase));
      }

      const int L = 8192, hop = L / 2;
      float[] win = global::VE3NEA.Dsp.BlackmanHarrisWindow(L);
      var acc = new double[L];
      int blocks = 0;
      using (var fft = new Fft<Complex32>(L, NativeFftw.FftwFlags.Estimate))
      {
        for (int off = 0; off + L <= n; off += hop)
        {
          for (int i = 0; i < L; i++) fft.InputData[i] = s[off + i] * win[i];
          fft.Execute();
          for (int k = 0; k < L; k++)
          {
            var c = fft.OutputData[k];
            acc[k] += (double)c.Real * c.Real + (double)c.Imaginary * c.Imaginary;
          }
          blocks++;
        }
      }

      double binHz = fsSyn / L;
      var freq = new double[L];
      var val = new double[L];
      double max = 1e-30;
      for (int k = 0; k < L; k++)
      {
        int signed = k <= L / 2 ? k : k - L;
        freq[k] = signed * binHz;
        val[k] = blocks > 0 ? acc[k] / blocks : 0;
        if (val[k] > max) max = val[k];
      }
      for (int k = 0; k < L; k++) val[k] /= max;
      System.Array.Sort(freq, val);
      return (freq, val);
    }

    /// <summary>Unit-DC-gain Gaussian premodulation filter (the GMSK/GFSK pulse-shaping LPF), σ = √ln2/(2π·BT).</summary>
    private static float[] GaussianPremod(float[] x, int sps, double bt)
    {
      double sigma = System.Math.Sqrt(System.Math.Log(2.0)) / (2.0 * System.Math.PI * bt) * sps; // samples
      int half = System.Math.Max(1, (int)System.Math.Round(3.0 * sigma));
      int m = 2 * half + 1;
      var h = new double[m];
      double sum = 0;
      for (int i = 0; i < m; i++) { double t = i - half; h[i] = System.Math.Exp(-(t * t) / (2 * sigma * sigma)); sum += h[i]; }
      for (int i = 0; i < m; i++) h[i] /= sum;

      var y = new float[x.Length];
      for (int i = 0; i < x.Length; i++)
      {
        double a = 0;
        for (int k = 0; k < m; k++) { int j = i + k - half; if ((uint)j < (uint)x.Length) a += x[j] * h[k]; }
        y[i] = (float)a;
      }
      return y;
    }

    /// <summary>Linear interpolation of the PSD curve at frequency <paramref name="f"/> Hz (0 outside its range).</summary>
    private static double Sample(double[] freq, double[] val, double f)
    {
      if (f <= freq[0] || f >= freq[^1]) return 0;
      int lo = 0, hi = freq.Length - 1;
      while (hi - lo > 1) { int mid = (lo + hi) >> 1; if (freq[mid] <= f) lo = mid; else hi = mid; }
      double t = (f - freq[lo]) / (freq[hi] - freq[lo]);
      return val[lo] * (1 - t) + val[hi] * t;
    }
  }
}
