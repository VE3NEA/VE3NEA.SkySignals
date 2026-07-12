using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using MathNet.Numerics;
using VE3NEA.SkyTlm.Core;
using VDsp = VE3NEA.Dsp;   // shared general-purpose DSP helpers (Interp, …)

namespace VE3NEA.SkyTlm.Dsp
{
  /// <summary>Matched-filter pulse shape for <see cref="BpskDemodulator"/>.</summary>
  public enum PskPulse
  {
    /// <summary>Root-raised-cosine (theoretical match for an RRC-shaped transmitter).</summary>
    Rrc,
    /// <summary>One-symbol boxcar = integrate-and-dump — the matched filter for rectangular NRZ, which is what
    /// scrambled-NRZ G3RUH transmitters approximate.</summary>
    Boxcar,
    /// <summary>Gaussian low-pass (set by <see cref="BpskDemodOptions.GaussianBt"/>) — a smoothed boxcar that
    /// matches band-limited NRZ better than a narrow RRC.</summary>
    Gaussian,
    /// <summary>direwolf-style cosine-windowed sinc low-pass (the field-proven G3RUH/BPSK choice): ~1 symbol
    /// wide, cutoff <see cref="BpskDemodOptions.LpfBaudFraction"/> × baud. Not a Nyquist matched filter — a
    /// pragmatic noise-limiting LPF before slicing.</summary>
    WindowedSincLpf
  }

  /// <summary>Symbol-timing recovery method — for <see cref="BpskDemodulator"/>, and for the per-burst
  /// <see cref="CpmFskDemodulator"/> path via <see cref="GmskDemodOptions.Timing"/>.</summary>
  public enum PskTiming
  {
    /// <summary>Feed-forward, whole-burst: an Oérder–Meyr baud-line estimator refined for the true clock
    /// <b>rate</b> (the burst is buffered, so one block estimate beats a sample-by-sample loop — no
    /// acquisition transient, lower variance, and it tracks a clock-frequency offset exactly instead of a
    /// feedback loop lagging it). Default for PSK.</summary>
    Feedforward,
    /// <summary>Gardner 2nd-order PLL (the streaming/CPM-shared loop). Tracks slow timing but can leave a
    /// residual clock-rate error over a burst; kept for comparison/fallback.</summary>
    Gardner
  }

  /// <summary>Tunables for <see cref="BpskDemodulator"/>; defaults target narrowband cubesat BPSK telemetry.</summary>
  public sealed class BpskDemodOptions
  {
    /// <summary>Symbol-timing recovery method (<see cref="PskTiming.Feedforward"/> by default).</summary>
    public PskTiming Timing { get; init; } = PskTiming.Feedforward;

    /// <summary>
    /// Matched-filter pulse shape. Real G3RUH BPSK is <b>scrambled NRZ through the radio</b> (≈rectangular),
    /// not RRC-shaped, so a narrow RRC matched filter injects ISI; <see cref="PskPulse.Boxcar"/> /
    /// <see cref="PskPulse.Gaussian"/> match the on-air pulse far better. RRC stays the default so the
    /// synthetic RRC-TX round-trips keep their theoretical match.
    /// </summary>
    public PskPulse MatchedFilter { get; init; } = PskPulse.Rrc;

    /// <summary>Gaussian matched-filter bandwidth-symbol product (used when <see cref="MatchedFilter"/> is
    /// <see cref="PskPulse.Gaussian"/>). Larger = wider passband / narrower impulse.</summary>
    public double GaussianBt { get; init; } = 0.5;

    /// <summary>Cutoff of the <see cref="PskPulse.WindowedSincLpf"/>, as a fraction of the baud rate. The
    /// default BPSK profile uses 0.60; the 9600 G3RUH profile uses 1.00.</summary>
    public double LpfBaudFraction { get; init; } = 0.60;

    /// <summary>Width of the <see cref="PskPulse.WindowedSincLpf"/> in symbols (~1.061).</summary>
    public double LpfWidthSymbols { get; init; } = 1.061;

    /// <summary>Root-raised-cosine matched-filter roll-off (excess bandwidth). 0.35 is the common
    /// satellite-telemetry value; the same RRC band-limits the full-bandwidth burst, so no separate
    /// channel LPF is needed.</summary>
    public double RrcRolloff { get; init; } = 0.35;

    /// <summary>RRC matched-filter span in symbols (the impulse response is truncated to this).</summary>
    public int RrcSpanSymbols { get; init; } = 8;

    /// <summary>Gardner timing-loop normalized bandwidth (cycles/symbol). Small = slow, stable tracking.</summary>
    public double LoopBandwidth { get; init; } = 0.01;

    /// <summary>Gardner loop damping (≈0.707 critically damped).</summary>
    public double LoopDamping { get; init; } = 0.707;

    /// <summary>Max fractional clock deviation the timing loop may track away from nominal sps (±).</summary>
    public double MaxClockError { get; init; } = 0.02;

    /// <summary>Costas carrier-loop normalized bandwidth (coherent path only). Wider than the timing loop so
    /// the residual CFO left after trajectory derotation is pulled in quickly over a short burst.</summary>
    public double CarrierLoopBandwidth { get; init; } = 0.02;

    /// <summary>Costas carrier-loop damping.</summary>
    public double CarrierLoopDamping { get; init; } = 0.707;

    /// <summary>
    /// <b>Differential</b> detection (DBPSK): decide each symbol from the phase <i>change</i>
    /// <c>Re{y_k·conj(y_{k-1})}</c> instead of an absolute carrier phase. No carrier recovery, so it is robust
    /// to residual CFO/Doppler and free of the 180° ambiguity. <c>false</c> = coherent BPSK (Costas).
    /// </summary>
    public bool Differential { get; init; } = false;

    /// <summary>Differential path only: estimate and remove the constant per-symbol carrier rotation left by a
    /// residual CFO (the squared-symbol angle, which cancels the 0/π data steps). On by default.</summary>
    public bool RemoveResidualCfo { get; init; } = true;

    /// <summary>
    /// Manchester (bi-phase-L) line coding present (the <c>DBPSK Manchester</c> AMSAT/FUNcube case). The symbol
    /// loop runs at the channel <b>chip</b> rate and consecutive chip pairs are combined into half-rate data
    /// soft symbols (<see cref="ManchesterCombine"/>).
    /// </summary>
    public bool Manchester { get; init; } = false;

    /// <summary>
    /// Pre-detection adaptive equalizer (<see cref="PskEqualizer.Off"/> by default). The real G3RUH error floor
    /// is amplitude ISI a fixed matched filter can't remove; <see cref="PskEqualizer.Fse"/> learns and inverts
    /// the channel (CMA→DD-LMS, per-burst multi-pass).
    /// </summary>
    public PskEqualizer Equalizer { get; init; } = PskEqualizer.Off;

    /// <summary>Fractionally-spaced (T/2) equalizer FIR length (forced odd). ~2·symbols of channel memory.</summary>
    public int EqTaps { get; init; } = 11;

    /// <summary>CMA (blind) equalizer step size.</summary>
    public double EqStepCma { get; init; } = 1e-3;

    /// <summary>Decision-directed LMS equalizer step size.</summary>
    public double EqStepDd { get; init; } = 3e-3;

    /// <summary>Offline equalizer adaptation passes over the buffered burst (pass 0 CMA, the rest DD).</summary>
    public int EqPasses { get; init; } = 4;

    /// <summary>Target samples/symbol the burst is oversampled toward before demod (nearest power of two,
    /// clamped to [1, <see cref="MaxUpsample"/>]). PSK timing/RRC want ≥4 sps; low-sps (high-baud) bursts get
    /// the most. 0 disables. Mirrors <see cref="GmskDemodOptions.UpsampleTargetSps"/>.</summary>
    public double UpsampleTargetSps { get; init; } = 8;

    /// <summary>Hard cap on the oversampling factor.</summary>
    public int MaxUpsample { get; init; } = 16;
  }

  /// <summary>
  /// BPSK / DBPSK demodulator — the PSK <b>sibling</b> of the non-coherent <see cref="CpmFskDemodulator"/>.
  /// PSK is <i>linear</i>, not CPM, so it does not reuse the FM-discriminator front end; it shares only burst
  /// detection + CFO derotation (done by the pipeline before <see cref="Demodulate"/>) and Gardner symbol
  /// timing — and swaps in an RRC matched filter plus a carrier-recovery / differential decision stage:
  /// <list type="bullet">
  ///   <item><b>Coherent BPSK</b> (<see cref="BpskDemodOptions.Differential"/> = false): a decision-directed
  ///   Costas loop tracks the carrier phase; soft = the in-phase projection. The unavoidable 180° phase
  ///   ambiguity is resolved downstream by the deframer's both-polarity sync search.</item>
  ///   <item><b>DBPSK</b> (Differential = true): differential detection <c>Re{y_k·conj(y_{k-1})}</c>, CFO-robust
  ///   and ambiguity-free; the dominant in-corpus form additionally carries <see cref="BpskDemodOptions.Manchester"/>
  ///   line coding, combined to data symbols after detection.</item>
  /// </list>
  /// Selected by <see cref="BpskDemodOptions.Differential"/>; <see cref="Demodulators"/> builds the coherent
  /// variant for <see cref="Modulation.BPSK"/> and the <see cref="Core.StreamingPipeline"/> tries both sub-modes
  /// per burst when <see cref="SignalParams.Differential"/> is still null, recording the one that decodes. It
  /// implements <see cref="IDemodulator"/> so the pipeline drives it through the generic per-burst path.
  /// </summary>
  public sealed class BpskDemodulator : IDemodulator
  {
    private readonly BpskDemodOptions opt;

    public BpskDemodulator(BpskDemodOptions? options = null) => opt = options ?? new BpskDemodOptions();

    /// <summary>Demodulate one detected burst (derotated by its CFO first, like the CPM engine).</summary>
    public SoftSymbols Demodulate(Complex32[] iq, Burst burst, SignalParams p)
      => DemodulateSegment(Acquisition.Derotate(iq, burst), p);

    /// <summary>Demodulate an already CFO-corrected baseband segment to soft symbols.</summary>
    public SoftSymbols DemodulateSegment(Complex32[] seg, SignalParams p) => Trace(seg, p).Symbols;

    /// <summary>
    /// Full demod with the intermediate signals exposed for inspection (<see cref="CpmFskDemodulator.Trace"/>'s
    /// PSK sibling): the matched-filter <b>magnitude</b> envelope |y| (carrier-blind, so it peaks at the symbol
    /// centres regardless of carrier phase — the right backdrop for checking Gardner strobe alignment) and the
    /// fractional strobe positions into it. Drives the headless strobe/eye PNG export.
    /// </summary>
    internal GmskTrace Trace(Complex32[] seg, SignalParams p)
    {
      // oversample the (full-bandwidth) burst toward the target sps so the RRC matched filter and the
      // fractional-strobe timing loop have headroom on low-sps bursts. Scaling SampleRate makes sps follow.
      int up = UpsampleFactorFor(p.SampleRate / p.Baud);
      Complex32[] x = up > 1 ? CpmFskDemodulator.Upsample(seg, up) : seg;
      if (up > 1) p = p with { SampleRate = p.SampleRate * up };
      double sps = p.SampleRate / p.Baud;

      // matched filter (RRC) — also the band-limiting stage (its cutoff is ~Rs/2).
      var (re, im) = MatchedFilter(x, sps);

      // symbol-timing recovery on the complex matched-filter output (carrier-phase blind, so timing precedes
      // carrier recovery). One complex strobe sample per recovered symbol/chip.
      var (yr, yi, strobes, settledSps) = opt.Timing == PskTiming.Feedforward
        ? FeedforwardSync(re, im, sps)
        : GardnerSync(re, im, sps);

      // optional pre-detection equalizer: learn and invert the linear channel ISI (the dominant G3RUH error
      // floor) before carrier recovery, so both the coherent and differential detectors see an opened eye.
      if (opt.Equalizer == PskEqualizer.Fse && strobes.Length >= 8)
      {
        var (hr, hi) = HalfRateSamples(re, im, strobes, settledSps);
        (yr, yi) = new BpskEqualizer(opt.EqTaps, opt.EqStepCma, opt.EqStepDd, opt.EqPasses).Equalize(hr, hi);
      }

      // decision stage.
      float[] chipSoft = opt.Differential ? DifferentialDetect(yr, yi) : CoherentDetect(yr, yi);

      // manchester combine: chip rate → data rate.
      float[] soft = opt.Manchester ? ManchesterCombine(chipSoft) : chipSoft;

      var (eyeDb, ambig) = CpmFskDemodulator.EyeQuality(soft);
      double symPeriod = (settledSps / up) * (opt.Manchester ? 2.0 : 1.0);
      var syms = new SoftSymbols
      {
        Soft = soft,
        SymbolRate = opt.Manchester ? p.Baud / 2.0 : p.Baud,   // baud is the channel chip rate when Manchester
        SamplesPerSymbol = symPeriod,
        EyeSnrDb = eyeDb,
        AmbiguousFraction = ambig
      };

      var mag = new float[re.Length];
      for (int i = 0; i < mag.Length; i++) mag[i] = MathF.Sqrt(re[i] * re[i] + im[i] * im[i]);
      return new GmskTrace(mag, strobes, sps, syms);
    }

    /// <summary>Diagnostic: the complex matched-filter output and the Gardner strobe positions into it (the
    /// inputs to the decision stage), for eye/strobe inspection. Mirrors the front end of <see cref="Trace"/>.</summary>
    internal (float[] re, float[] im, double[] strobes, double sps) MatchedAndStrobes(Complex32[] seg, SignalParams p)
    {
      int up = UpsampleFactorFor(p.SampleRate / p.Baud);
      Complex32[] x = up > 1 ? CpmFskDemodulator.Upsample(seg, up) : seg;
      if (up > 1) p = p with { SampleRate = p.SampleRate * up };
      double sps = p.SampleRate / p.Baud;
      var (re, im) = MatchedFilter(x, sps);
      var (_, _, strobes, _) = opt.Timing == PskTiming.Feedforward ? FeedforwardSync(re, im, sps) : GardnerSync(re, im, sps);
      return (re, im, strobes, sps);
    }

    // --- pre-stage: integer oversampling (same power-of-two rule as the CPM engine) -------------

    internal int UpsampleFactorFor(double sps)
    {
      if (opt.UpsampleTargetSps <= 0 || sps <= 0) return 1;
      double ratio = opt.UpsampleTargetSps / sps;
      if (ratio <= 1) return 1;
      int factor = 1 << (int)Math.Round(Math.Log2(ratio));
      return Math.Clamp(factor, 1, opt.MaxUpsample);
    }

    // --- stage 1: matched filter ---------------------------------------------------------------

    /// <summary>Filter the complex baseband with the (real, symmetric) matched filter, I and Q separately.</summary>
    private (float[] re, float[] im) MatchedFilter(Complex32[] x, double sps)
    {
      float[] h = MatchedKernel(sps);
      int n = x.Length;
      var re = new float[n]; var im = new float[n];
      for (int i = 0; i < n; i++) { re[i] = x[i].Real; im[i] = x[i].Imaginary; }
      re = LiquidFir.ConvolveSame(re, h);   // zero-phase (group delay compensated)
      im = LiquidFir.ConvolveSame(im, h);
      return (re, im);
    }

    private static readonly ConcurrentDictionary<(PskPulse Shape, double Sps, double P, int Span), float[]> KernelCache = new();

    /// <summary>The matched-filter impulse response for the configured pulse shape (unit energy, odd length).
    /// RRC is the theoretical match for RRC-shaped TX; <b>Boxcar</b> (integrate-and-dump) and <b>Gaussian</b>
    /// better match the scrambled-NRZ-through-a-radio pulse that real G3RUH transmitters actually emit.</summary>
    private float[] MatchedKernel(double sps) => opt.MatchedFilter switch
    {
      PskPulse.Rrc => RrcKernel(sps, opt.RrcRolloff, opt.RrcSpanSymbols),
      PskPulse.Boxcar => KernelCache.GetOrAdd((PskPulse.Boxcar, sps, 0, opt.RrcSpanSymbols), static k =>
      {
        int half = Math.Max(1, (int)Math.Round(k.Sps / 2.0));   // ~one symbol wide, odd length
        int m = 2 * half + 1;
        var h = new float[m];
        float v = (float)(1.0 / Math.Sqrt(m));
        for (int i = 0; i < m; i++) h[i] = v;
        return h;
      }),
      PskPulse.Gaussian => KernelCache.GetOrAdd((PskPulse.Gaussian, sps, opt.GaussianBt, opt.RrcSpanSymbols), static k =>
      {
        // gaussian LPF with bandwidth-symbol product BT: σ (in symbols) = sqrt(ln2)/(2π·BT).
        double sigma = Math.Sqrt(Math.Log(2)) / (2 * Math.PI * Math.Max(0.05, k.P)) * k.Sps;
        int half = Math.Max(1, (int)Math.Round(k.Span * k.Sps / 2.0));
        int m = 2 * half + 1;
        var h = new double[m]; double e = 0;
        for (int i = 0; i < m; i++) { double t = i - half; double v = Math.Exp(-0.5 * t * t / (sigma * sigma)); h[i] = v; e += v * v; }
        var hf = new float[m]; double norm = 1.0 / Math.Sqrt(e);
        for (int i = 0; i < m; i++) hf[i] = (float)(h[i] * norm);
        return hf;
      }),
      PskPulse.WindowedSincLpf => KernelCache.GetOrAdd((PskPulse.WindowedSincLpf, sps, opt.LpfBaudFraction, (int)Math.Round(opt.LpfWidthSymbols * 1000)), static k =>
      {
        // direwolf gen_lowpass: sinc(2π·fc·(j-center))·cos((j-center)/size·π), fc as a fraction of Fs = (LpfBaud·baud)/Fs.
        int size = Math.Max(3, (int)Math.Round(k.Span / 1000.0 * k.Sps));
        if ((size & 1) == 0) size++;                       // odd length, zero-phase
        double fc = k.P / k.Sps;                            // P = LpfBaudFraction; baud/Fs = 1/sps
        double center = 0.5 * (size - 1);
        var h = new double[size]; double e = 0;
        for (int j = 0; j < size; j++)
        {
          double d = j - center;
          double sinc = d == 0 ? 2 * fc : Math.Sin(2 * Math.PI * fc * d) / (Math.PI * d);
          double win = Math.Cos(d / size * Math.PI);
          h[j] = sinc * win; e += h[j] * h[j];
        }
        var hf = new float[size]; double norm = e > 1e-12 ? 1.0 / Math.Sqrt(e) : 1.0;
        for (int j = 0; j < size; j++) hf[j] = (float)(h[j] * norm);
        return hf;
      }),
      _ => RrcKernel(sps, opt.RrcRolloff, opt.RrcSpanSymbols)
    };

    private static readonly ConcurrentDictionary<(double Sps, double Beta, int Span), float[]> RrcCache = new();

    /// <summary>
    /// Root-raised-cosine impulse response sampled at <paramref name="sps"/>, spanning
    /// <paramref name="span"/> symbols, normalized to unit energy. As both the TX pulse-shaping filter and the
    /// RX matched filter, the cascade is a raised cosine (zero ISI at the symbol instants). Cached per
    /// (sps, β, span) like the FSK kernels in <see cref="KernelCache"/>.
    /// </summary>
    internal static float[] RrcKernel(double sps, double beta, int span) =>
      RrcCache.GetOrAdd((sps, beta, span), static k =>
      {
        int half = (int)Math.Round(k.Span * k.Sps / 2.0);
        int m = 2 * half + 1;
        var h = new double[m];
        double energy = 0;
        for (int i = 0; i < m; i++)
        {
          double t = (i - half) / k.Sps;     // time in symbols (T = 1)
          double v = Rrc(t, k.Beta);
          h[i] = v; energy += v * v;
        }
        double norm = energy > 1e-12 ? 1.0 / Math.Sqrt(energy) : 1.0;
        var hf = new float[m];
        for (int i = 0; i < m; i++) hf[i] = (float)(h[i] * norm);
        return hf;
      });

    /// <summary>RRC sample h(t) (t in symbols), with the two removable-singularity cases handled in closed form.</summary>
    private static double Rrc(double t, double beta)
    {
      if (Math.Abs(t) < 1e-9) return 1.0 - beta + 4.0 * beta / Math.PI;
      double fourBetaT = 4.0 * beta * t;
      if (beta > 1e-9 && Math.Abs(Math.Abs(fourBetaT) - 1.0) < 1e-9)   // t = ±1/(4β)
      {
        double a = Math.PI / (4.0 * beta);
        return (beta / Math.Sqrt(2.0)) *
          ((1.0 + 2.0 / Math.PI) * Math.Sin(a) + (1.0 - 2.0 / Math.PI) * Math.Cos(a));
      }
      double num = Math.Sin(Math.PI * t * (1.0 - beta)) + fourBetaT * Math.Cos(Math.PI * t * (1.0 + beta));
      double den = Math.PI * t * (1.0 - fourBetaT * fourBetaT);
      return num / den;
    }

    // --- stage 2a: feed-forward symbol-timing recovery (whole burst) ---------------------------

    /// <summary>
    /// Feed-forward symbol timing over the whole (buffered) burst. The symbol-rate spectral line lives in the
    /// real sequence <c>c[n] = |y_n|²</c>; its <b>frequency</b> is the true symbol rate and its <b>phase</b> is
    /// the sampling offset (Oerder–Meyr). A Gardner feedback loop only tracks the line's phase and leaves a
    /// residual clock-<i>rate</i> error that walks the strobes ~½ symbol across a burst; here we instead
    /// estimate the rate directly by maximizing <c>|Σ c[n]·e^{-j2πf·n}|</c> over <c>f</c> near <c>1/sps</c>
    /// (coarse grid + parabolic refine), then strike strobes at the constant true period from the O&amp;M phase.
    /// No acquisition transient (symbol 0 is as well-timed as symbol N), lower variance than a per-symbol TED.
    /// </summary>
    internal (float[] yr, float[] yi, double[] strobes, double settledSps) FeedforwardSync(float[] re, float[] im, double sps)
    {
      int n = re.Length;
      if (n < 4 || sps <= 1)
        return (Array.Empty<float>(), Array.Empty<float>(), Array.Empty<double>(), sps);

      // c[n] = |y|² (mean-removed so the strong DC term doesn't bias the near-DC numerics).
      var c = new double[n];
      double mean = 0;
      for (int i = 0; i < n; i++) { c[i] = (double)re[i] * re[i] + (double)im[i] * im[i]; mean += c[i]; }
      mean /= n;
      for (int i = 0; i < n; i++) c[i] -= mean;

      // clock rate + phase from the shared whole-burst estimator (decimated chirp-Z grid search over
      // ±MaxClockError, parabolic refine, O&M phase at the refined frequency) — the same core drives the
      // CPM/discriminator port (CpmFskDemodulator.FeedforwardSync).
      var (period, tau) = FeedforwardTiming.EstimateClock(c, sps, opt.MaxClockError);

      // strike one strobe per symbol at the constant true period (cubic interpolation, valid index range).
      int cap = (int)(n / period) + 2;
      var yr = new List<float>(cap); var yi = new List<float>(cap); var strobes = new List<double>(cap);
      for (double t = tau; t + 2 < n; t += period)
      {
        if (t < 1) continue;
        yr.Add((float)VDsp.Interp(re, t)); yi.Add((float)VDsp.Interp(im, t)); strobes.Add(t);
      }

      TrimBurstEdges(yr, yi, strobes);
      return (yr.ToArray(), yi.ToArray(), strobes.ToArray(), period);
    }

    /// <summary>Fraction of the burst-median matched-filter envelope below which a strobe is a rise/fall
    /// <i>transient</i>, not a data symbol.</summary>
    private const double EdgeEnvelopeFraction = 0.4;

    /// <summary>
    /// Drop the burst's rise/fall ramp from the symbol stream. A detected burst spans a little past the signal
    /// on each side, so the first/last strobes sit on the envelope ramp where |y| sweeps through zero — they
    /// carry no data, yet they get struck as symbols (collapsing to the origin in the constellation and adding
    /// low-confidence junk bits at the frame ends). Remove the <b>contiguous</b> low-envelope run at each end
    /// (envelope &lt; <see cref="EdgeEnvelopeFraction"/>·median); interior fades are left untouched so the symbol
    /// stream stays contiguous for the deframer. A clean, flat-envelope burst (the synthetic round-trips) has no
    /// such run and is unchanged.
    /// </summary>
    private static void TrimBurstEdges(List<float> yr, List<float> yi, List<double> strobes)
    {
      int count = strobes.Count;
      if (count < 16) return;
      var env = new double[count];
      for (int k = 0; k < count; k++) env[k] = Math.Sqrt((double)yr[k] * yr[k] + (double)yi[k] * yi[k]);
      var sorted = (double[])env.Clone(); Array.Sort(sorted);
      double thr = EdgeEnvelopeFraction * sorted[count / 2];   // fraction of the median envelope

      int lo = 0; while (lo < count && env[lo] < thr) lo++;
      int hi = count - 1; while (hi > lo && env[hi] < thr) hi--;
      if (lo == 0 && hi == count - 1) return;                  // nothing to trim
      if (hi - lo + 1 < 8) return;                             // never trim away the whole burst

      strobes.RemoveRange(hi + 1, count - 1 - hi); strobes.RemoveRange(0, lo);
      yr.RemoveRange(hi + 1, count - 1 - hi); yr.RemoveRange(0, lo);
      yi.RemoveRange(hi + 1, count - 1 - hi); yi.RemoveRange(0, lo);
    }

    // --- stage 2c: T/2 sampling for the equalizer ----------------------------------------------

    /// <summary>
    /// Sample the matched-filter output at T/2 spacing for the fractionally-spaced equalizer: for each recovered
    /// symbol, the half-symbol <b>midpoint</b> (strobe − period/2) and the <b>strobe</b> instant, interleaved
    /// <c>[mid₀, strobe₀, mid₁, strobe₁, …]</c>. Cubic interpolation, with positions clamped to the valid range.
    /// </summary>
    private static (float[] hr, float[] hi) HalfRateSamples(float[] re, float[] im, double[] strobes, double period)
    {
      int K = strobes.Length, n = re.Length;
      var hr = new float[2 * K]; var hi = new float[2 * K];
      double hi0 = 1, hiN = n - 3;                 // valid cubic-interp range
      for (int k = 0; k < K; k++)
      {
        double s = Math.Clamp(strobes[k], hi0, hiN);
        double mid = Math.Clamp(strobes[k] - period / 2.0, hi0, hiN);
        hr[2 * k] = (float)VDsp.Interp(re, mid); hi[2 * k] = (float)VDsp.Interp(im, mid);
        hr[2 * k + 1] = (float)VDsp.Interp(re, s); hi[2 * k + 1] = (float)VDsp.Interp(im, s);
      }
      return (hr, hi);
    }

    // --- stage 2b: Gardner symbol-timing recovery (complex) ------------------------------------

    /// <summary>Symbols of signal the Oerder–Meyr seed averages over (well inside even short bursts).</summary>
    private const int SeedSymbols = 256;

    /// <summary>
    /// Gardner timing recovery on the complex matched-filter output: a cubic-interpolated, 2nd-order PI loop
    /// with the constellation-blind TED <c>e = Re{ conj(y_mid)·(y_late − y_early) }</c> (= the in-phase plus
    /// quadrature Gardner terms), so it locks before carrier phase is known. Seeded with the Oerder–Meyr
    /// feed-forward phase computed from the symbol-rate spectral line in <c>|y|²</c>. Returns the complex
    /// strobe samples, their positions, and the average samples/symbol the loop settled on.
    /// </summary>
    internal (float[] yr, float[] yi, double[] strobes, double settledSps) GardnerSync(float[] re, float[] im, double sps)
    {
      int n = re.Length;
      int cap = Math.Max(16, (int)(n / sps) + 1);
      var yr = new List<float>(cap); var yi = new List<float>(cap);
      var strobes = new List<double>(cap);
      if (n < 4) return (yr.ToArray(), yi.ToArray(), strobes.ToArray(), sps);

      double bw = opt.LoopBandwidth, zeta = opt.LoopDamping;
      double denom = 1.0 + 2.0 * zeta * bw + bw * bw;
      double kp = 4.0 * zeta * bw / denom;
      double ki = 4.0 * bw * bw / denom;

      double period = sps;
      double minP = sps * (1.0 - opt.MaxClockError);
      double maxP = sps * (1.0 + opt.MaxClockError);

      // |y| for the Oerder–Meyr seed: that estimator SQUARES its input internally, and the symbol-timing
      // spectral line lives in |y|² for a linearly modulated signal — so feed it the magnitude |y|, NOT |y|²
      // (passing |y|² would give |y|⁴, whose line is half a symbol off and seeds the loop onto the transitions).
      var mag = new float[n];
      for (int j = 0; j < n; j++) mag[j] = (float)Math.Sqrt(re[j] * re[j] + im[j] * im[j]);

      double rms;
      {
        int w = Math.Min(n, (int)(SeedSymbols * sps));
        double acc = 0;
        for (int j = 0; j < w; j++) acc += (double)re[j] * re[j] + (double)im[j] * im[j];
        rms = Math.Max(w > 0 ? acc / w : 0, 1e-6);
      }

      double phase0 = CpmFskDemodulator.OerderMeyrPhase(mag, sps);
      double t = phase0 + sps;             // first decision instant (one symbol in, for a valid prev)
      double prevR = VDsp.Interp(re, t - period), prevI = VDsp.Interp(im, t - period);
      double periodSum = 0; int periodCnt = 0;

      while (t + 1 < n - 2)
      {
        double curR = VDsp.Interp(re, t), curI = VDsp.Interp(im, t);
        double midR = VDsp.Interp(re, t - period / 2.0), midI = VDsp.Interp(im, t - period / 2.0);
        yr.Add((float)curR); yi.Add((float)curI); strobes.Add(t);

        // amplitude-normalized complex Gardner error; negative feedback (τ ← τ − μ·e) locks at the symbol
        // centre (the textbook PAM form, as in CpmFskDemodulator.GardnerSync).
        double inst = curR * curR + curI * curI;
        rms += 0.05 * (inst - rms);
        double norm = Math.Sqrt(rms) + 1e-6;
        double e = (midR * (curR - prevR) + midI * (curI - prevI)) / norm;

        period -= ki * e;
        if (period < minP) period = minP; else if (period > maxP) period = maxP;
        periodSum += period; periodCnt++;

        t += period - kp * e;
        prevR = curR; prevI = curI;
      }

      double settled = periodCnt > 0 ? periodSum / periodCnt : sps;
      return (yr.ToArray(), yi.ToArray(), strobes.ToArray(), settled);
    }

    // --- stage 3a: coherent BPSK (decision-directed Costas) ------------------------------------

    /// <summary>
    /// Coherent BPSK detection: a 2nd-order decision-directed Costas loop tracks the carrier phase across the
    /// recovered symbols and the soft value is the derotated in-phase projection, normalized to ≈[−1,1]. The
    /// phase is seeded from the squared-symbol angle (resolves the static phase in one shot; the residual 180°
    /// flip is left to the deframer's polarity search).
    /// </summary>
    private float[] CoherentDetect(float[] yr, float[] yi)
    {
      int K = yr.Length;
      var soft = new float[K];
      if (K == 0) return soft;

      // coarse static-phase init from the M=2 power-law line over the burst head.
      double sr = 0, si = 0;
      int w = Math.Min(K, 64);
      for (int k = 0; k < w; k++) { double r = yr[k], i = yi[k]; sr += r * r - i * i; si += 2.0 * r * i; }
      double theta = 0.5 * Math.Atan2(si, sr);

      // seed the loop FREQUENCY from the residual per-symbol carrier rotation (the same squared-symbol estimate
      // the differential path uses): squaring removes the BPSK data, so the angle of Σ y_k²·conj(y_{k-1}²) is
      // 2·ω. Without this the loop must acquire the residual CFO from freq=0, which at the narrow carrier
      // bandwidth costs hundreds of symbols of lock-in — long enough to corrupt a whole short telemetry frame
      // (the burst is already derotated by its MEAN CFO, but a Doppler rate / estimation error leaves a residual).
      double fr = 0, fi = 0;
      for (int k = 1; k < K; k++)
      {
        double a = yr[k] * yr[k] - yi[k] * yi[k], b = 2.0 * yr[k] * yi[k];          // y_k²
        double a1 = yr[k - 1] * yr[k - 1] - yi[k - 1] * yi[k - 1], b1 = 2.0 * yr[k - 1] * yi[k - 1]; // y_{k-1}²
        fr += a * a1 + b * b1; fi += b * a1 - a * b1;                                // y_k²·conj(y_{k-1}²)
      }
      double freq = 0.5 * Math.Atan2(fi, fr);

      double bw = opt.CarrierLoopBandwidth, zeta = opt.CarrierLoopDamping;
      double denom = 1.0 + 2.0 * zeta * bw + bw * bw;
      double kp = 4.0 * zeta * bw / denom;
      double ki = 4.0 * bw * bw / denom;

      for (int k = 0; k < K; k++)
      {
        double c = Math.Cos(theta), s = Math.Sin(theta);
        double zr = yr[k] * c + yi[k] * s;    // y · e^{−jθ}
        double zi = -yr[k] * s + yi[k] * c;
        double mag = Math.Sqrt(zr * zr + zi * zi) + 1e-12;
        soft[k] = (float)(zr / mag);
        // decision-directed error Im{z·conj(d)} with d = sign(Re z): drives θ so the constellation sits on ±I.
        double e = (zi * Math.Sign(zr)) / mag;
        freq += ki * e;
        theta += freq + kp * e;
      }
      return soft;
    }

    // --- stage 3b: DBPSK (differential detection) ----------------------------------------------

    /// <summary>
    /// Differential detection: the soft value is the normalized real part of <c>y_k·conj(y_{k-1})</c> — the
    /// cosine of the phase change, +1 for a 0-step and −1 for a π-step. No carrier recovery (CFO-robust) and no
    /// 180° ambiguity. A residual constant CFO rotates every step equally; estimated via the squared-product
    /// angle (which cancels the 0/π data) and removed when <see cref="BpskDemodOptions.RemoveResidualCfo"/> is set.
    /// Symbol 0 has no predecessor → soft 0 (no confidence), as in <see cref="DifferentialDetector"/>.
    /// </summary>
    private float[] DifferentialDetect(float[] yr, float[] yi)
    {
      int K = yr.Length;
      var soft = new float[K];
      if (K == 0) return soft;

      double cfo = 0;
      if (opt.RemoveResidualCfo)
      {
        double sr = 0, si = 0;
        for (int k = 1; k < K; k++)
        {
          double zr = yr[k] * yr[k - 1] + yi[k] * yi[k - 1];   // re{y_k·conj(y_{k-1})}
          double zi = yi[k] * yr[k - 1] - yr[k] * yi[k - 1];   // im{…}
          sr += zr * zr - zi * zi; si += 2.0 * zr * zi;        // accumulate z²
        }
        cfo = 0.5 * Math.Atan2(si, sr);
      }
      double cc = Math.Cos(cfo), cs = Math.Sin(cfo);

      soft[0] = 0f;
      for (int k = 1; k < K; k++)
      {
        double zr = yr[k] * yr[k - 1] + yi[k] * yi[k - 1];
        double zi = yi[k] * yr[k - 1] - yr[k] * yi[k - 1];
        double dr = zr * cc + zi * cs;                          // re{ z · e^{−j·cfo} }
        double mag = Math.Sqrt((double)(yr[k] * yr[k] + yi[k] * yi[k])) *
                     Math.Sqrt((double)(yr[k - 1] * yr[k - 1] + yi[k - 1] * yi[k - 1])) + 1e-12;
        soft[k] = (float)(dr / mag);
      }
      return soft;
    }

    // --- stage 4: Manchester combine -----------------------------------------------------------

    /// <summary>
    /// Combine Manchester (bi-phase-L) chip soft values into data soft values. Each data bit is two opposite
    /// half-chips, so the correct of the two pairings (offset 0 or 1) is the one whose chip pairs are most
    /// consistently opposite — pick the offset that maximizes the summed <c>|chip_a − chip_b|</c>. The data soft
    /// is the half-difference (sign = the transition direction); global polarity is resolved by the deframer.
    /// </summary>
    internal static float[] ManchesterCombine(float[] chip)
    {
      int K = chip.Length;
      if (K < 2) return chip;
      double e0 = 0, e1 = 0;
      for (int m = 0; 2 * m + 1 < K; m++) e0 += Math.Abs(chip[2 * m] - chip[2 * m + 1]);
      for (int m = 0; 2 * m + 2 < K; m++) e1 += Math.Abs(chip[2 * m + 1] - chip[2 * m + 2]);
      int off = e1 > e0 ? 1 : 0;
      int count = (K - off) / 2;
      var data = new float[count];
      for (int m = 0; m < count; m++)
        data[m] = 0.5f * (chip[2 * m + off] - chip[2 * m + 1 + off]);
      return data;
    }
  }
}
