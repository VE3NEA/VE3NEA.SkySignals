using System;
using MathNet.Numerics;
using VE3NEA.SkyTlm.Core;
using VDsp = VE3NEA.Dsp;   // shared general-purpose DSP helpers (Median, GaussianLowpass, Interp, GaussianQ, …)

namespace VE3NEA.SkyTlm.Dsp
{
  /// <summary>
  /// Generic non-coherent CPM/FSK demodulator:
  /// the shared front end <b>channel LPF → FM discriminator → short noise low-pass → polyphase Gardner
  /// symbol-timing recovery</b>, followed by a pluggable <see cref="IDetector"/> decision stage. The flavor
  /// (pulse shape, modulation index, M-ary order, detector choice) is described by a <see cref="ModProfile"/>;
  /// the algorithmic tunables live in <see cref="GmskDemodOptions"/>. GMSK is the profile
  /// <c>{Gaussian, BT=0.5, h=0.5, M=2}</c> with the DF-DD detector — see <see cref="GmskDemodulator"/>, the
  /// thin compatibility shim that preserves the GMSK surface byte-for-byte. New binary FSK flavors
  /// (GFSK with a real BT/h, MSK, wide-h 2-FSK) are added as profiles plus, where their structure pays off, a
  /// new detector — the pipeline, burst detector and CFO stage are unchanged.
  ///
  /// The discriminator output is already the TX frequency pulse <c>NRZ⊛g</c>, so the post-detection filter is
  /// a <i>short</i> LP for noise only — a full frequency-pulse matched filter would convolve a second pulse in
  /// series and close the eye with partial-response ISI (see <see cref="Smooth"/>). We emit <b>soft</b>
  /// decisions for the FEC stage. Operates on the whole (short) burst and may be non-causal within it.
  /// </summary>
  public class CpmFskDemodulator : IDemodulator
  {
    private readonly GmskDemodOptions opt;
    private readonly ModProfile profile;
    private readonly IDetector detector;

    public CpmFskDemodulator(ModProfile profile, GmskDemodOptions? options = null)
    {
      this.profile = profile ?? throw new ArgumentNullException(nameof(profile));
      opt = options ?? new GmskDemodOptions();
      detector = BuildDetector(profile, opt);
    }

    /// <summary>
    /// Resolve the decision stage. A profile may pin a detector explicitly; a
    /// <c>null</c> detector uses the GMSK/GFSK rule — DF-DD when
    /// <see cref="GmskDemodOptions.DifferentialOrder"/> ≥ 2, else the discriminator slicer.
    /// </summary>
    private static IDetector BuildDetector(ModProfile profile, GmskDemodOptions opt)
    {
      DetectorKind kind = profile.Detector
        ?? (opt.UseMlse ? DetectorKind.MlsePsp
            : opt.DifferentialOrder >= 2 ? DetectorKind.Differential : DetectorKind.Discriminator);
      return kind switch
      {
        DetectorKind.Discriminator => new DiscriminatorDetector(),
        DetectorKind.Differential => new DifferentialDetector(opt, profile),
        DetectorKind.OrthogonalMatchedFilter => new OrthogonalFskDetector(profile),
        DetectorKind.MlsePsp => new MlsePspDetector(profile, opt),
        DetectorKind.CoherentLinear =>
          throw new NotSupportedException("Coherent linear detector (MSK) is not yet implemented."),
        _ => new DiscriminatorDetector()
      };
    }

    public SoftSymbols Demodulate(Complex32[] iq, Burst burst, SignalParams p)
      => Trace(Acquisition.Derotate(iq, burst), p).Symbols;

    /// <summary>Demodulate an already CFO-corrected baseband segment to soft symbols.</summary>
    public SoftSymbols DemodulateSegment(Complex32[] seg, SignalParams p) => Trace(seg, p).Symbols;

    /// <summary>
    /// <b>Continuous</b> demodulation (FR continuous-demod): demodulate a long, already time-varying-CFO-corrected
    /// baseband stream (see <see cref="Acquisition.DerotateVarying"/>) in one pass — the bursts only supplied the
    /// CFO trajectory. Identical front end to <see cref="Trace"/> except the discriminator's whole-burst
    /// cluster-midpoint centring is replaced by a <b>leaky DC blocker</b> (<see cref="GmskDemodOptions.DcBlockSymbols"/>):
    /// a non-stop stream spanning silence + many bursts has no single midpoint.
    /// <paramref name="anchors"/> (sample indices into <paramref name="seg"/>, typically the detected burst
    /// starts) re-seed the Gardner loop: free-running over a multi-second noise gap random-walks the loop's
    /// period and phase, and re-acquiring within a fraction-of-a-second burst is not guaranteed — at each
    /// anchor the loop snaps back to the nominal period with a fresh feed-forward (Oerder–Meyr) phase, so
    /// every known burst is entered timing-locked while the gaps in between stay covered.
    /// </summary>
    public GmskTrace TraceStream(Complex32[] seg, SignalParams p, int[]? anchors = null)
    {
      // band-limit at the NATIVE rate (taps ∝ native sps), THEN oversample the already-narrowband signal:
      // identical selectivity in Hz at a fraction of the MACs.
      Complex32[] chan = ChannelFilter(seg, p);
      int up = UpsampleFactorFor(p.SampleRate / p.Baud);
      if (up > 1) { chan = Upsample(chan, up); p = p with { SampleRate = p.SampleRate * up }; }

      double sps = p.SampleRate / p.Baud;
      float[] disc = DiscriminateRaw(chan, p);       // freq pulse, NOT globally DC-centred (no single midpoint)
      RunningDcBlock(disc, sps, opt.DcBlockSymbols); // leaky high-pass tracks the drifting baseline instead
      float[] mf = Smooth(disc, sps);
      double[]? seeds = null;
      if (anchors is { Length: > 0 })
      {
        seeds = new double[anchors.Length];
        for (int i = 0; i < anchors.Length; i++) seeds[i] = (double)anchors[i] * up;  // post-upsample positions
        Array.Sort(seeds);
      }
      var (gardnerSoft, strobes, settledSps) = GardnerSync(mf, sps, seeds);

      float[] soft = detector.Detect(new DetectorContext
      {
        Baseband = chan,
        GardnerSoft = gardnerSoft,
        Strobes = strobes,
        Sps = sps,
        Params = p
      });

      var (eyeDb, ambig) = EyeQuality(soft);
      var symbols = new SoftSymbols
      {
        Soft = soft,
        SymbolRate = p.Baud,
        SamplesPerSymbol = settledSps / up,
        EyeSnrDb = eyeDb,
        AmbiguousFraction = ambig
      };
      // callers map a symbol index → stream time via SamplesPerSymbol (back at the original rate); the trace
      // keeps strobes at the internal (post-upsample) rate so they stay aligned with mf for the eye view.
      return new GmskTrace(mf, strobes, sps, symbols);
    }

    /// <summary>Continuous demod (<see cref="TraceStream"/>) returning just the soft symbols.</summary>
    public SoftSymbols DemodulateStream(Complex32[] seg, SignalParams p, int[]? anchors = null)
      => TraceStream(seg, p, anchors).Symbols;

    /// <summary>
    /// Full demod with the intermediate matched-filter signal and recovered strobe positions kept,
    /// for the eye diagram / constellation view and tests.
    /// </summary>
    public GmskTrace Trace(Complex32[] seg, SignalParams p)
    {
      // band-limit to ~Carson BW at the NATIVE rate (taps ∝ native sps — same selectivity in Hz as
      // filtering after the upsample, at a fraction of the MACs), then
      // optionally oversample the narrowband signal so the nonlinear discriminator and the fractional-strobe
      // interpolation have headroom on low-sps (high-baud) bursts. Scaling SampleRate makes every downstream
      // stage (sps, deviation scaling, filter cutoffs) follow automatically; symbol-normalized loop gains
      // are already sps-invariant.
      Complex32[] chan = ChannelFilter(seg, p);      // band-limit (kills out-of-band noise)
      int up = UpsampleFactorFor(p.SampleRate / p.Baud);
      if (up > 1) { chan = Upsample(chan, up); p = p with { SampleRate = p.SampleRate * up }; }

      double sps = p.SampleRate / p.Baud;
      float[] disc = Discriminate(chan, p, out float discCenter);   // instantaneous frequency, normalized to ±1 nominal
      float[] mf = Smooth(disc, sps);                // short noise LP (NOT a full freq-pulse matched filter — that double-filters)
      var (gardnerSoft, strobes, settledSps) = GardnerSync(mf, sps); // timing recovery → one soft symbol per period

      // pluggable decision stage. The discriminator detector returns the Gardner soft unchanged (byte-identical
      // slicer path); DF-DD resamples the complex baseband at the recovered strobes for the ~2.5-3 dB gain.
      float[] soft = detector.Detect(new DetectorContext
      {
        Baseband = chan,
        GardnerSoft = gardnerSoft,
        Strobes = strobes,
        Sps = sps,
        Params = p
      });

      var (eyeDb, ambig) = EyeQuality(soft);
      var symbols = new SoftSymbols
      {
        Soft = soft,
        SymbolRate = p.Baud,
        SamplesPerSymbol = settledSps / up,   // back to original-rate sps so it stays comparable across factors
        EyeSnrDb = eyeDb,
        AmbiguousFraction = ambig,
        // the removed cluster midpoint in Hz — the carrier error the caller's derotation left behind
        ResidualCfoHz = discCenter * (p.Deviation ?? p.Baud / 4.0)
      };
      return new GmskTrace(mf, strobes, sps, symbols);
    }

    // --- optional pre-stage: integer oversampling ----------------------------------------------

    /// <summary>
    /// Integer oversampling factor for a burst at <paramref name="sps"/> samples/symbol: the nearest power
    /// of two to <c>TargetSps/sps</c>, clamped to <c>[1, MaxUpsample]</c>. Powers of two only (efficient
    /// cascaded ×2 interpolation, exact for the standard 48 kHz baud set). Returns 1 (no oversampling) for
    /// bursts already at/above the target, e.g. ≤1200 Bd.
    /// </summary>
    internal int UpsampleFactorFor(double sps)
    {
      if (opt.UpsampleTargetSps <= 0 || sps <= 0) return 1;
      double ratio = opt.UpsampleTargetSps / sps;
      if (ratio <= 1) return 1;
      int factor = 1 << (int)Math.Round(Math.Log2(ratio));   // nearest power of two
      return Math.Clamp(factor, 1, opt.MaxUpsample);
    }

    /// <summary>
    /// Polyphase-equivalent integer upsampler by <paramref name="L"/>: zero-stuff then a windowed-sinc
    /// anti-image low-pass with cutoff at the <i>original</i> Nyquist (0.5/L of the new rate) and gain L.
    /// Reconstructs the band-limited continuous signal and re-samples it L× denser; it adds no
    /// information (the 48 kHz signal is already Nyquist-sampled) but gives the nonlinear discriminator
    /// and the fractional-strobe interpolation more headroom at low sps. Amplitude is immaterial to the
    /// detectors here (discriminator is <c>arg</c>, DF-DD divides by magnitude, Gardner RMS-normalizes),
    /// so the gain only keeps the soft scale familiar.
    /// </summary>
    internal static Complex32[] Upsample(Complex32[] x, int L)
    {
      if (L <= 1) return x;
      int taps = Math.Min((16 * L) | 1, 257);
      // gain L compensates the zero-stuffing power loss (kernel is unit-DC); scaled copies are cached so the
      // shared KernelCache entry is never mutated
      float[] h = KernelCache.BlackmanSincScaled(0.5 / L, taps, L);
      return VE3NEA.LiquidFir.Interpolate(x, h, L);          // polyphase firinterp, zero-phase
    }

    // --- stage 0: channel filter ---------------------------------------------------------------

    /// <summary>
    /// Complex low-pass (windowed-sinc) the CFO-corrected burst to ~Carson bandwidth before the
    /// discriminator. The burst still spans the full 48 kHz recording; the wideband noise outside the
    /// signal band would otherwise dominate the per-sample phase difference and close the eye.
    /// Zero-phase (centered) so symbol timing is unaffected.
    /// </summary>
    internal Complex32[] ChannelFilter(Complex32[] x, SignalParams p)
    {
      double cutoffHz = opt.ChannelBwBaud * p.Baud;
      // wide-h 2-FSK: the tones sit at ±dev, which for h≥2 is at/beyond the 1.0·Rs GMSK cutoff and would
      // be attenuated. Widen to pass the outer tone plus ~0.75·Rs of its main lobe. Gated on the None pulse so
      // GMSK/GFSK (Gaussian) keep their exact cutoff and stay byte-identical.
      if (profile.Pulse == PulseShape.None)
      {
        double dev = p.Deviation ?? p.Baud;
        cutoffHz = Math.Max(cutoffHz, dev + 0.75 * p.Baud);
      }
      double fc = cutoffHz / p.SampleRate;                  // normalized cutoff (cycles/sample)
      if (fc >= 0.5) return x;                              // already narrower than Nyquist → no filter
      int taps = (int)Math.Round(6 * (p.SampleRate / p.Baud)) | 1;           // odd length, ~6 symbols
      // floor of 41: at low native sps (high baud, pre-upsample filtering) ~6 symbols is too few taps for a
      // clean skirt, and marginal FEC decodes are sensitive to it. Still ~6× cheaper than the old
      // filter-after-upsample arrangement.
      taps = Math.Max(41, Math.Min(taps, 511));
      float[] h = KernelCache.BlackmanSinc(fc, taps);
      return LiquidFir.ConvolveSame(x, h);   // SIMD firfilt_crcf, zero-phase (group delay compensated)
    }

    // --- stage 1: FM discriminator -------------------------------------------------------------

    /// <summary>
    /// Quadrature/FM discriminator <c>arg(x[n]·conj(x[n−1]))</c> (rad/sample), scaled so the
    /// nominal GMSK deviation (Rs/4, h=0.5) maps to ±1, then DC-centered: the burst is mean-removed
    /// so the slicer threshold is 0 even with a small residual offset the NCO left behind.
    /// </summary>
    internal static float[] Discriminate(Complex32[] x, SignalParams p) => Discriminate(x, p, out _);

    /// <summary>As <see cref="Discriminate(Complex32[],SignalParams)"/>, also reporting the cluster midpoint
    /// the centring removed (in soft units, 1.0 = nominal deviation): that midpoint IS the residual carrier
    /// error the derotation left behind, measured from the NRZ itself — amplitude-independent, so it stays
    /// valid even when the mis-centred channel filter attenuates one tone.</summary>
    internal static float[] Discriminate(Complex32[] x, SignalParams p, out float centerSoft)
    {
      var f = DiscriminateRaw(x, p);
      centerSoft = CenterGlobal(f);
      return f;
    }

    /// <summary>
    /// FM discriminator <c>arg(x[n]·conj(x[n−1]))</c> (rad/sample) scaled so the nominal deviation maps to ±1,
    /// <b>without</b> DC centring. <see cref="Discriminate"/> (per-burst) follows it with the whole-burst
    /// cluster-midpoint <see cref="CenterGlobal"/>; the continuous path (<see cref="TraceStream"/>) follows it
    /// with the leaky <see cref="RunningDcBlock"/> instead.
    /// </summary>
    internal static float[] DiscriminateRaw(Complex32[] x, SignalParams p)
    {
      int n = x.Length;
      var f = new float[n];
      // nominal peak deviation in rad/sample = 2π·dev/fs; scale so that maps to 1.0
      double dev = p.Deviation ?? p.Baud / 4.0;
      double nominalRad = 2.0 * Math.PI * dev / p.SampleRate;
      double scale = nominalRad > 1e-9 ? 1.0 / nominalRad : 1.0;

      for (int i = 1; i < n; i++)
      {
        // arg(x[i] * conj(x[i-1]))
        float re = x[i].Real * x[i - 1].Real + x[i].Imaginary * x[i - 1].Imaginary;
        float im = x[i].Imaginary * x[i - 1].Real - x[i].Real * x[i - 1].Imaginary;
        f[i] = (float)(Math.Atan2(im, re) * scale);
      }
      f[0] = n > 1 ? f[1] : 0f;
      return f;
    }

    /// <summary>
    /// Whole-burst DC-block / slicer threshold (per-burst path). Not the plain mean: a data 1/0 imbalance, or
    /// the noisy guard padding the burst carries at both edges, pulls the mean off the eye centre. Use the
    /// midpoint of the two frequency clusters instead — the mean of samples above a provisional mean and the
    /// mean of those below — which sits at the eye centre even for an imbalanced/edge-noisy run.
    /// </summary>
    internal static float CenterGlobal(float[] f)
    {
      int n = f.Length;
      if (n == 0) return 0;
      double sum = 0;
      for (int i = 0; i < n; i++) sum += f[i];
      float m0 = (float)(sum / n);
      double sumP = 0, sumN = 0; int nP = 0, nN = 0;
      for (int i = 0; i < n; i++)
        if (f[i] >= m0) { sumP += f[i]; nP++; } else { sumN += f[i]; nN++; }
      float center = nP > 0 && nN > 0 ? (float)((sumP / nP + sumN / nN) / 2.0) : m0;
      for (int i = 0; i < n; i++) f[i] -= center;
      return center;
    }

    /// <summary>
    /// Leaky one-pole DC blocker for the <b>continuous</b> path (<see cref="TraceStream"/>): a non-stop stream
    /// has no single eye centre, so track the slowly drifting discriminator baseline with a high-pass whose
    /// time constant is <paramref name="symbols"/> symbols (≫ 1 symbol, so symbol-rate content is untouched).
    /// In place. Seeded with the leading sample so it doesn't ramp from zero.
    /// </summary>
    internal static void RunningDcBlock(float[] f, double sps, double symbols)
    {
      int n = f.Length;
      if (n == 0) return;
      double alpha = 1.0 / Math.Max(1.0, symbols * sps);   // pole at ≈ `symbols` symbols
      double m = f[0];
      for (int i = 0; i < n; i++) { m += alpha * (f[i] - m); f[i] -= (float)m; }
    }

    // --- stage 2: post-discriminator noise low-pass --------------------------------------------

    /// <summary>
    /// Smooth the discriminator output with a <b>short Gaussian low-pass</b> (<see cref="GmskDemodOptions.RxSmoothingSymbols"/>
    /// wide, ≪ 1 symbol) for noise reduction. Crucially this is <i>not</i> a frequency-pulse matched filter:
    /// the discriminator output is already <c>NRZ⊛g</c>, so a second full pulse would convolve another symbol-wide
    /// kernel and create partial-response ISI (the closed, multi-level eye). Zero-phase, unit DC gain so a steady
    /// ±1 run stays ±1. Width 0 (or a degenerate kernel) passes the discriminator through unchanged.
    /// </summary>
    internal float[] Smooth(float[] f, double sps)
    {
      float[] h = KernelCache.GaussianLowpass(opt.RxSmoothingSymbols * sps);
      return LiquidFir.ConvolveSame(f, h);   // SIMD firfilt; clones f when the kernel is degenerate
    }

    // --- (unused by the live path) Gaussian frequency-pulse matched filter ---------------------
    // kept for the unit tests and the coherent (Laurent) detector. NOT used by Trace: matching
    // the discriminator output against a full frequency pulse double-filters and closes the eye.

    /// <summary>
    /// Filter the discriminator output with the <b>GMSK frequency pulse</b> g(t) (a Gaussian-shaped
    /// pulse of one symbol, BT from options) — the matched filter for the frequency waveform. Taps
    /// are normalized to unit DC gain so a steady ±1 run stays ±1 (keeps the soft scale meaningful).
    /// </summary>
    internal float[] MatchedFilter(float[] f, double sps)
    {
      float[] h = FrequencyPulse(sps, opt.FilterBt, opt.FilterSpanSymbols);
      int m = h.Length, half = m / 2;
      int n = f.Length;
      var y = new float[n];
      for (int i = 0; i < n; i++)
      {
        double acc = 0;
        for (int k = 0; k < m; k++)
        {
          int j = i + k - half;
          if ((uint)j < (uint)n) acc += (double)f[j] * h[k];
        }
        y[i] = (float)acc;
      }
      return y;
    }

    /// <summary>
    /// GMSK frequency pulse g(t) = (1/2)[Q(2πB(t−T/2)/√ln2) − Q(2πB(t+T/2)/√ln2)], B=BT/T, sampled at
    /// <paramref name="sps"/> and normalized to unit sum. This is the Gaussian-filtered rectangle the
    /// transmitter applies to the frequency; matched filtering against it maximizes SNR (g is symmetric).
    /// </summary>
    internal static float[] FrequencyPulse(double sps, double bt, int spanSymbols)
    {
      int half = (int)Math.Round(spanSymbols * sps / 2.0);
      int m = 2 * half + 1;
      var h = new float[m];
      double b = bt;                       // B·T (T=1 symbol in symbol-normalized time)
      double k = 2.0 * Math.PI * b / Math.Sqrt(Math.Log(2.0));
      double sum = 0;
      for (int i = 0; i < m; i++)
      {
        double t = (i - half) / sps;       // time in symbols
        double g = 0.5 * (VDsp.GaussianQ(k * (t - 0.5)) - VDsp.GaussianQ(k * (t + 0.5)));
        h[i] = (float)g;
        sum += g;
      }
      if (sum > 1e-12) for (int i = 0; i < m; i++) h[i] /= (float)sum;
      return h;
    }

    // --- stage 3: Gardner symbol-timing recovery -----------------------------------------------

    /// <summary>
    /// Gardner timing-error-detector loop with a cubic (4-point) interpolator and a 2nd-order PI loop
    /// filter. The Gardner TED <c>e = y_mid·(y_late − y_early)</c> is constellation-agnostic and
    /// needs no carrier phase — ideal for the discriminator's real signal. Emits one soft symbol per
    /// recovered period. Returns the soft stream and the average samples/symbol the loop settled on.
    /// Optional <paramref name="anchors"/> (sorted sample positions, continuous path) re-seed the loop —
    /// nominal period + fresh Oerder–Meyr phase from the signal right after the anchor — discarding the
    /// state the free run over the preceding noise gap accumulated (see <see cref="TraceStream"/>).
    /// </summary>
    internal (float[] soft, double[] strobes, double settledSps) GardnerSync(float[] y, double sps, double[]? anchors = null)
    {
      int n = y.Length;
      int cap = Math.Max(16, (int)(n / sps) + 1);
      var soft = new System.Collections.Generic.List<float>(cap);
      var strobes = new System.Collections.Generic.List<double>(cap);

      // PI loop gains for the timing recovery (Gardner/Mengali), normalized to symbol time.
      double bw = opt.LoopBandwidth, zeta = opt.LoopDamping;
      double denom = 1.0 + 2.0 * zeta * bw + bw * bw;
      double kp = 4.0 * zeta * bw / denom;
      double ki = 4.0 * bw * bw / denom;

      double period = sps;                 // adaptive samples/symbol (integral state lives here)
      double minP = sps * (1.0 - opt.MaxClockError);
      double maxP = sps * (1.0 + opt.MaxClockError);

      // running RMS so the TED gain is independent of burst amplitude / fading. Seeded from the window
      // head (as the anchor re-seed does): starting from ~0 made the first ~50 TED errors enormous
      // (÷√rms), slamming the period integral against its clamp rails before the EMA warmed up — the
      // settled clock then carried a bias that slipped symbols over a long frame, and whether a burst
      // decoded depended chaotically on what sat in the first few symbols of the window.
      double rms;
      {
        int w = Math.Min(n, (int)(SeedSymbols * sps));
        double acc = 0;
        for (int j = 0; j < w; j++) acc += (double)y[j] * y[j];
        rms = Math.Max(w > 0 ? acc / w : 0, 1e-6);
      }

      // feed-forward Oerder–Meyr phase so the loop starts AT a symbol centre instead of acquiring
      // from an arbitrary (possibly transition) instant — critical for short bursts and high sps.
      double phase0 = OerderMeyrPhase(y, sps);
      double t = phase0 + sps;             // first decision instant (one symbol in, for a valid prev)
      float prev = VDsp.Interp(y, t - period);
      double periodSum = 0; int periodCnt = 0;
      int nextAnchor = 0;

      while (t + 1 < n - 2)
      {
        // anchor re-seed (continuous path): entering a known burst, drop the free-run state from the
        // preceding noise gap — nominal period, feed-forward phase from the burst head, fresh TED gain.
        if (anchors != null && nextAnchor < anchors.Length && t >= anchors[nextAnchor])
        {
          double a = anchors[nextAnchor];
          while (nextAnchor < anchors.Length && anchors[nextAnchor] <= t) nextAnchor++;
          int w0 = (int)a, w1 = (int)Math.Min(n, a + SeedSymbols * sps);
          if (w1 - w0 >= 4 * sps)
          {
            period = sps;
            double tNew = w0 + OerderMeyrPhase(y, w0, w1, sps);
            while (tNew < t) tNew += sps;          // never step back (no duplicate symbols)
            t = tNew;
            if (t + 1 >= n - 2) break;
            prev = VDsp.Interp(y, t - period);
            double acc = 0;
            for (int j = w0; j < w1; j++) acc += (double)y[j] * y[j];
            rms = Math.Max(acc / (w1 - w0), 1e-6);
          }
        }

        float cur = VDsp.Interp(y, t);
        float mid = VDsp.Interp(y, t - period / 2.0);
        soft.Add(cur);
        strobes.Add(t);

        // amplitude-normalized Gardner error. Negative feedback: the timing update SUBTRACTS the
        // error (τ ← τ − μ·e), so the symbol centre is the stable lock. With the opposite sign the
        // loop locks half a symbol off onto the zero-crossings (transitions) — a closed eye.
        double inst = (double)cur * cur;
        rms += 0.05 * (inst - rms);
        double norm = Math.Sqrt(rms) + 1e-6;
        double e = (mid * (cur - prev)) / norm;

        // PI update: integral adjusts the period, proportional nudges this step's advance.
        period -= ki * e;
        if (period < minP) period = minP; else if (period > maxP) period = maxP;
        periodSum += period; periodCnt++;

        t += period - kp * e;
        prev = cur;
      }

      double settled = periodCnt > 0 ? periodSum / periodCnt : sps;
      return (soft.ToArray(), strobes.ToArray(), settled);
    }

    /// <summary>Symbols of signal the anchor re-seed estimates its Oerder–Meyr phase over — well inside
    /// even the shortest (~0.1 s) detected bursts, long enough to average the discriminator noise.</summary>
    private const int SeedSymbols = 256;

    /// <summary>
    /// Oerder–Meyr feed-forward symbol-timing estimate: the phase of the symbol-rate spectral line in
    /// |y|² gives the optimal sampling instant in closed form, <c>τ = −(sps/2π)·arg(Σ |y[n]|²·e^(−j2πn/sps))</c>.
    /// Unbiased and acquisition-free, ideal for buffered short bursts (non-causal). Returns τ in [0,sps).
    /// </summary>
    internal static double OerderMeyrPhase(float[] y, double sps) => OerderMeyrPhase(y, 0, y.Length, sps);

    /// <summary>Oerder–Meyr phase over <c>y[from..to)</c>; τ in [0,sps) is relative to <paramref name="from"/>.</summary>
    internal static double OerderMeyrPhase(float[] y, int from, int to, double sps)
    {
      double w = 2.0 * Math.PI / sps;
      double re = 0, im = 0;
      for (int n = from; n < to; n++)
      {
        double e2 = (double)y[n] * y[n];
        double ph = w * (n - from);
        re += e2 * Math.Cos(ph);
        im -= e2 * Math.Sin(ph);
      }
      double tau = -Math.Atan2(im, re) / (2.0 * Math.PI) * sps; // offset of the optimal instant
      tau %= sps; if (tau < 0) tau += sps;
      return tau;
    }

    // --- shared helper -------------------------------------------------------------------------

    /// <summary>Zero-phase, length-preserving convolution of <paramref name="x"/> with a centred kernel <paramref name="h"/>.</summary>
    internal static float[] ConvolveSame(float[] x, float[] h)
    {
      int n = x.Length, m = h.Length, half = m / 2;
      var y = new float[n];
      for (int i = 0; i < n; i++)
      {
        double acc = 0;
        for (int k = 0; k < m; k++)
        {
          int j = i + k - half;
          if ((uint)j < (uint)n) acc += (double)x[j] * h[k];
        }
        y[i] = (float)acc;
      }
      return y;
    }

    // --- quality metric ------------------------------------------------------------------------

    /// <summary>
    /// Eye opening as an SNR-like figure: split the soft symbols at 0 into the two
    /// clusters, return <c>20·log10((μ₊−μ₋)/(2σ))</c> and the fraction of near-threshold (ambiguous)
    /// symbols. An intrinsic demod-quality figure independent of the downstream frame decode.
    /// </summary>
    internal static (double eyeDb, double ambiguous) EyeQuality(float[] soft)
    {
      if (soft.Length < 4) return (0, 1);
      double sumP = 0, sumN = 0; int nP = 0, nN = 0;
      foreach (var s in soft)
        if (s >= 0) { sumP += s; nP++; } else { sumN += s; nN++; }
      if (nP == 0 || nN == 0) return (0, 1);
      double muP = sumP / nP, muN = sumN / nN;

      double varAcc = 0;
      foreach (var s in soft) { double m = s >= 0 ? muP : muN; varAcc += (s - m) * (s - m); }
      double sigma = Math.Sqrt(varAcc / soft.Length);

      double sep = muP - muN;
      double eyeDb = sigma > 1e-9 ? 20.0 * Math.Log10(sep / (2.0 * sigma)) : 60.0;

      double thresh = 0.25 * (sep / 2.0);
      int ambig = 0;
      foreach (var s in soft) if (Math.Abs(s) < thresh) ambig++;
      return (eyeDb, (double)ambig / soft.Length);
    }
  }
}
