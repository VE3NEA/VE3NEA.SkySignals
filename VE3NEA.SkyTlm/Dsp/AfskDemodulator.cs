using System;
using MathNet.Numerics;
using VE3NEA.SkyTlm.Core;

namespace VE3NEA.SkyTlm.Dsp
{
  /// <summary>
  /// AFSK-over-FM demodulator (Bell-202 audio subcarrier carried on an FM link, e.g. CUBEBUG-2's 1k2 downlink).
  /// The RF is FM, so the audio is one discriminator away; but that audio is itself a 1200 Hz (mark) / 2200 Hz
  /// (space) tone pair. Bell-202 is <b>non-orthogonal</b> (h = 2·500/1200 ≈ 0.83), so the shared FSK engine's
  /// down-mix-and-orthogonal-boxcar detector — built for wide-h orthogonal FSK — closes the eye on carrier-dominated
  /// passes. This front end instead runs the <b>direwolf "profile-A" tone correlator</b>: FM-discriminate the RF to
  /// audio, then two <b>continuous mark/space quadrature correlators</b> (quadrature-mix at each tone, matched-filter
  /// low-pass over ~1.5 symbols, take the envelope magnitude) whose difference <c>|mark| − |space|</c> is the soft
  /// decision signal. A whole-burst two-cluster threshold (<see cref="CpmFskDemodulator.CenterGlobal"/>) centres it,
  /// then <see cref="RecoverTiming"/> recovers symbol timing with the direwolf transition-nudged causal DPLL
  /// (<see cref="DpllSyncLegacy"/>). A non-causal whole-burst clock-line fit + bidirectional pass (Phase 2 T1/T2,
  /// <see cref="DpllStrobes"/>/<see cref="WeightedLineFit"/>) is opt-in via <c>AFSK_TIMING=fit</c> but is not the
  /// default: it helped a synthetic fading tail yet regressed every burst of the real CUBEBUG-2 pass (a straight clock
  /// line can't follow the within-burst timing curvature the adaptive loop tracks).
  /// The AX.25 deframer is reused unchanged — only this front end is AFSK-specific. (The shared Gardner PI loop was
  /// tried but its whole-burst Oerder–Meyr seed is corrupted by the fading tail and it locks half a symbol off; the
  /// DPLL is robust there.)
  /// Ported/validated against direwolf on the CUBEBUG-2 ground-truth recording (single CRC-valid frame at t≈197.6 s).
  /// Keeping the modulation tagged AFSK (not collapsed to FSK) also makes <see cref="CfoEstimator"/> use
  /// carrier-symmetry CFO, which locks the dominant unmodulated carrier line.
  /// </summary>
  public sealed class AfskDemodulator : IDemodulator
  {
    /// <summary>Bell-202 audio subcarrier centre (Hz) — midway between the 1200/2200 tones.</summary>
    public const double DefaultAfCarrierHz = 1700.0;

    /// <summary>Bell-202 tone half-spacing (Hz) = (2200 − 1200)/2 — the FSK deviation of the down-mixed baseband.</summary>
    public const double DefaultDeviationHz = 500.0;

    /// <summary>Correlator low-pass cutoff as a fraction of the baud rate. 0.6·baud gives an effective matched-filter
    /// integration of ≈1/(0.6·Rs) ≈ 1.7 symbols — the ablation sweet spot (≥1 symbol is essential, ~2.8-symbol
    /// rectangular over-integrates into ISI).</summary>
    private const double LowpassCutoffBaud = 0.6;

    /// <summary>Correlator low-pass window length in symbols — long enough to realize the <see cref="LowpassCutoffBaud"/>
    /// skirt cleanly (the window sharpens the band edge; the cutoff, not the window, sets the integration time).</summary>
    private const double LowpassWindowSymbols = 2.8;

    /// <summary>One-sided pre-discriminator RF low-pass cutoff (Hz) ≈ a 12.5 kHz NBFM channel. Band-limiting the FM
    /// signal before the nonlinear discriminator suppresses out-of-band noise that would otherwise turn into
    /// impulsive click noise and flip symbols despite a good average eye. Override with <c>AFSK_RFBW</c> (0 = off).</summary>
    private const double DefaultRfBandwidthHz = 6000.0;

    // the correlator + DPLL front end is self-contained; the GMSK options are accepted for factory parity but the
    // AFSK path has no GMSK-style tunables (its filter/timing constants live here).
    public AfskDemodulator(GmskDemodOptions? options = null) { }

    /// <summary>Use the coherent MLSE decision stage (<see cref="DemodulateSegmentMlse"/>) instead of the
    /// non-coherent correlator envelope difference. Off by default — the pipeline's CRC-gated AFSK
    /// detector retry opts in per burst, so a real frame can never be lost to the new path.</summary>
    public bool UseMlseDetector { get; init; }

    public SoftSymbols Demodulate(Complex32[] iq, Burst burst, SignalParams p)
      => DemodulateSegment(Acquisition.Derotate(iq, burst), p);

    /// <summary>Demodulate an already CFO-corrected RF segment with the selected decision stage.</summary>
    public SoftSymbols DemodulateSegment(Complex32[] seg, SignalParams p)
      => UseMlseDetector ? DemodulateSegmentMlse(seg, p) : DemodulateSegmentCorrelator(seg, p);

    /// <summary>Demodulate an already CFO-corrected RF segment: discriminate to audio, run the mark/space tone
    /// correlator to a real decision signal, then recover timing + soft symbols with the shared engine.</summary>
    private SoftSymbols DemodulateSegmentCorrelator(Complex32[] seg, SignalParams p)
    {
      float[] demod = CorrelatorDemod(seg, p);
      CpmFskDemodulator.CenterGlobal(demod);   // whole-burst two-cluster threshold → decision signal centred on 0

      double sps = p.SampleRate / p.Baud;
      var (soft, settledSps) = RecoverTiming(demod, sps);
      var (eyeDb, ambig) = CpmFskDemodulator.EyeQuality(soft);
      return new SoftSymbols
      {
        Soft = soft,
        SymbolRate = p.Baud,
        SamplesPerSymbol = settledSps,
        EyeSnrDb = eyeDb,
        AmbiguousFraction = ambig
      };
    }

    /// <summary>Fewest strobes a whole-burst clock-line fit needs to be meaningful; below this (a runt burst) fall
    /// back to the plain causal DPLL, whose per-sample output degrades gracefully.</summary>
    private const int MinStrobesForFit = 8;

    /// <summary>
    /// Whole-burst symbol timing (Phase 2: T1 log-and-fit clock-skew resample + T2 bidirectional DPLL). The direwolf
    /// DPLL (<see cref="DpllStrobes"/>) is robust but causal and first-order: it lag-tracks the TX↔RX clock skew
    /// (worst at the fading FCS tail) and acquires <i>through</i> the weak fade-in edge — leaving whole-burst timing
    /// information on the table that, because we buffer the burst, we don't have to. <b>T1:</b> take the DPLL's own
    /// strobe instants and weighted-least-squares fit a straight clock line <c>t(k)=φ₀+T·k</c> over the whole burst
    /// (<see cref="WeightedLineFit"/>) — this averages ~all ~1200 strobes so timing-noise variance drops, and it
    /// replaces the lagging/wandering tail strobes with the extrapolated ideal grid. <b>T2:</b> run the DPLL again on
    /// the time-reversed burst so its acquisition transient lands on the opposite edge; the first-order tracking lag
    /// is equal-and-opposite in the two directions, so averaging the two fitted grids cancels it. Finally resample
    /// <paramref name="demod"/> at the averaged fractional instants with a cubic interpolator
    /// (<see cref="global::VE3NEA.Dsp.Interp"/>). Opt-in via <c>AFSK_TIMING=fit</c>; the default is the causal loop
    /// (<see cref="DpllSyncLegacy"/>), which beat this fit on the real CUBEBUG-2 pass.
    /// </summary>
    private static (float[] soft, double sps) RecoverTiming(float[] demod, double sps)
    {
      // default is the causal DPLL. On the real CUBEBUG-2 pass the whole-burst line fit regressed every burst (eye
      // ~0.4-1.8 dB lower) and lost the only CRC-valid frame (2026-07-06): a straight clock line can't follow the
      // within-burst timing curvature the adaptive loop tracks. T1+T2 is opt-in via AFSK_TIMING=fit (fade-tail case).
      if (!string.Equals(Environment.GetEnvironmentVariable("AFSK_TIMING"), "fit", StringComparison.OrdinalIgnoreCase))
        return DpllSyncLegacy(demod, sps);

      // T1: forward DPLL strobes → weighted clock-line fit (φ₀, T).
      double[] fwd = DpllStrobes(demod, sps, reversed: false);
      if (fwd.Length < MinStrobesForFit) return DpllSyncLegacy(demod, sps);
      var (phi0f, tf) = WeightedLineFit(fwd, demod);

      // T2: reverse-time DPLL strobes → second fit. Its acquisition transient is on the opposite (far) edge, so the
      // first-order tracking lag is equal-and-opposite and cancels when the two fitted grids are averaged.
      double[] bwd = DpllStrobes(demod, sps, reversed: true);
      bool bidir = bwd.Length >= MinStrobesForFit;
      var (phi0b, tb) = bidir ? WeightedLineFit(bwd, demod) : (phi0f, tf);

      int k = fwd.Length;
      double period = bidir ? 0.5 * (tf + tb) : tf;
      var soft = new float[k];
      for (int j = 0; j < k; j++)
      {
        double instF = phi0f + tf * j;
        // map the backward grid to the same physical symbol (its nearest index) before averaging the two instants.
        double instB = bidir ? phi0b + tb * Math.Round((instF - phi0b) / tb) : instF;
        soft[j] = global::VE3NEA.Dsp.Interp(demod, 0.5 * (instF + instB));
      }
      return (soft, period);
    }

    /// <summary>
    /// One direwolf DPLL pass returning the fractional sample instant of every strobe (accumulator wrap) — the raw
    /// material for the clock-line fit — instead of the sampled values. Same loop as <see cref="DpllSyncLegacy"/>
    /// (advance 2³² per symbol, strobe at the wrap, nudge the phase toward every bit transition); when
    /// <paramref name="reversed"/> the burst is scanned end-to-first so the acquisition transient falls on the far
    /// edge (T2). Instants are always returned in forward (increasing) sample order.
    /// </summary>
    private static double[] DpllStrobes(float[] demod, double sps, bool reversed)
    {
      int n = demod.Length;
      var strobes = new System.Collections.Generic.List<double>(n / Math.Max(1, (int)sps) + 16);
      uint step = (uint)Math.Round(4294967296.0 / sps);   // 2^32 per symbol
      int pll = 0; bool prevBit = false;
      for (int i = 0; i < n; i++)
      {
        int idx = reversed ? n - 1 - i : i;
        int prev = pll;
        pll = unchecked((int)((uint)pll + step));
        if (pll < 0 && prev >= 0)
        {
          double frac = (2147483648.0 - prev) / step;             // sub-sample wrap position within this step, (0,1]
          double t = i - 1 + frac;                                 // strobe instant in scan-time
          strobes.Add(reversed ? n - 1 - t : t);                  // → forward sample-time
        }
        bool bit = demod[idx] > 0;
        if (bit != prevBit) pll = (int)(pll * 0.6f);               // nudge the phase toward the observed transition
        prevBit = bit;
      }
      if (reversed) strobes.Reverse();                             // return forward-increasing instants
      return strobes.ToArray();
    }

    /// <summary>
    /// Weighted least-squares fit of a straight clock line <c>t(k)=φ₀+T·k</c> to the strobe instants, each weighted
    /// by the local decision envelope <c>|demod|</c> so the noisy fading tail cannot pull the fit (unweighted
    /// whole-burst timing is e/actly what the plain Oerder–Meyr estimate gets wrong on this burst). Returns the phase
    /// offset φ₀ (sample instant of symbol 0) and the fitted symbol period T (samples/symbol — the skew-corrected
    /// clock). Degenerate input (collinear weights) falls back to the endpoint slope.
    /// </summary>
    private static (double phi0, double period) WeightedLineFit(double[] strobes, float[] demod)
    {
      double sw = 0, sk = 0, skk = 0, st = 0, skt = 0;
      for (int k = 0; k < strobes.Length; k++)
      {
        double w = Math.Abs(global::VE3NEA.Dsp.Interp(demod, strobes[k])) + 1e-6;
        sw += w; sk += w * k; skk += w * (double)k * k; st += w * strobes[k]; skt += w * k * strobes[k];
      }
      double det = sw * skk - sk * sk;
      if (Math.Abs(det) < 1e-9)
      {
        double slope = strobes.Length > 1 ? (strobes[^1] - strobes[0]) / (strobes.Length - 1) : 0;
        return (strobes[0], slope);
      }
      double period = (sw * skt - sk * st) / det;
      double phi0 = (st - period * sk) / sw;
      return (phi0, period);
    }

    /// <summary>
    /// Plain direwolf digital-PLL symbol timing (the Phase 1 causal loop, now the default; <c>AFSK_TIMING=fit</c>
    /// opts into the T1/T2 fit instead): a phase accumulator advances by one symbol per revolution and samples
    /// <paramref name="demod"/> at each wrap; every bit transition nudges the accumulator toward the transition.
    /// First-order and transition-tracked — acquires within a couple symbols and re-locks through fades.
    /// </summary>
    private static (float[] soft, double sps) DpllSyncLegacy(float[] demod, double sps)
    {
      int n = demod.Length;
      var soft = new System.Collections.Generic.List<float>(n / Math.Max(1, (int)sps) + 16);
      uint step = (uint)Math.Round(4294967296.0 / sps);   // 2^32 per symbol
      int pll = 0; bool prevBit = false;
      for (int i = 0; i < n; i++)
      {
        int prev = pll;
        pll = unchecked((int)((uint)pll + step));
        if (pll < 0 && prev >= 0) soft.Add(demod[i]);       // accumulator wrapped → sample at the symbol centre
        bool bit = demod[i] > 0;
        if (bit != prevBit) pll = (int)(pll * 0.6f);         // nudge the phase toward the observed transition
        prevBit = bit;
      }
      return (soft.ToArray(), sps);
    }

    /// <summary>
    /// Direwolf profile-A tone correlator. FM-discriminate the RF burst to the real audio waveform
    /// (<c>arg(x[n]·conj(x[n−1]))</c>, the message signal carrying the 1200/2200 Hz tones), then run two
    /// quadrature correlators: quadrature-mix the audio down from the mark and space tone frequencies to DC (native
    /// SIMD <see cref="global::VE3NEA.Dsp.Mix"/>), matched-filter low-pass each complex result over ~1.5 symbols
    /// (<see cref="LiquidFir.ConvolveSame"/>), and take the envelope magnitude. The per-sample soft decision is the
    /// <b>tone difference</b> <c>|mark| − |space|</c>. A pre-discriminator RF band-limit (<see cref="DefaultRfBandwidthHz"/>)
    /// keeps out-of-band noise from becoming click-noise outliers in the nonlinear discriminator. The discriminator's
    /// constant scale is immaterial (the downstream <see cref="CpmFskDemodulator.CenterGlobal"/> sets the threshold).
    /// </summary>
    private static float[] CorrelatorDemod(Complex32[] seg, SignalParams p)
    {
      var mark = DiscriminateAudio(seg, p);
      var space = (Complex32[])mark.Clone();
      int n = mark.Length;

      // mark = af_carrier − dev (1200 Hz), space = af_carrier + dev (2200 Hz)
      double afCarrier = p.AfCarrier ?? DefaultAfCarrierHz;
      double dev = p.Deviation ?? DefaultDeviationHz;
      double markF = (afCarrier - dev) / p.SampleRate;   // cycles/sample
      double spaceF = (afCarrier + dev) / p.SampleRate;

      // quadrature-mix each tone down to DC, then matched-filter low-pass to its complex envelope. The windowed-sinc
      // LP beat every boxcar length and the fade-normalized variant on the synthetic; a longer window only sharpens
      // the band edge — the cutoff, not the window, sets the ~1.7-symbol integration time.
      global::VE3NEA.Dsp.Mix(mark, -markF);
      global::VE3NEA.Dsp.Mix(space, -spaceF);
      double cutoff = LowpassCutoffBaud * p.Baud / p.SampleRate;
      int taps = Math.Max(41, Math.Min((int)Math.Round(LowpassWindowSymbols * p.SampleRate / p.Baud) | 1, 511));
      float[] lp = KernelCache.BlackmanSinc(cutoff, taps);
      mark = LiquidFir.ConvolveSame(mark, lp);
      space = LiquidFir.ConvolveSame(space, lp);

      // tone difference |mark| − |space| is the soft decision (CenterGlobal handles the threshold downstream)
      var demod = new float[n];
      for (int i = 0; i < n; i++)
        demod[i] = mark[i].Magnitude - space[i].Magnitude;
      return demod;
    }

    /// <summary>
    /// FM-discriminate the (band-limited) RF burst to the real audio waveform (rad/sample). Carried in the real
    /// part of a complex buffer so the shared complex mixer can rotate it; a constant CFO only adds a DC term
    /// (the tones stay at ±dev about af_carrier).
    /// </summary>
    private static Complex32[] DiscriminateAudio(Complex32[] seg, SignalParams p)
    {
      int n = seg.Length;

      // band-limit the RF to ~Carson bandwidth BEFORE the nonlinear discriminator: out-of-band noise otherwise
      // dominates the per-sample phase difference and produces impulsive click noise that flips symbols despite a
      // good average eye. Zero-phase, so symbol timing is unaffected.
      double rfBwHz = double.TryParse(Environment.GetEnvironmentVariable("AFSK_RFBW"), out var rb) ? rb : DefaultRfBandwidthHz;
      if (rfBwHz > 0)
      {
        double fcRf = rfBwHz / p.SampleRate;
        if (fcRf < 0.5)
        {
          int rfTaps = (int)Math.Round(6 * (p.SampleRate / p.Baud)) | 1;
          rfTaps = Math.Max(41, Math.Min(rfTaps, 511));
          seg = LiquidFir.ConvolveSame(seg, KernelCache.BlackmanSinc(fcRf, rfTaps));
        }
      }

      var audio = new Complex32[n];
      for (int i = 1; i < n; i++)
      {
        float re = seg[i].Real * seg[i - 1].Real + seg[i].Imaginary * seg[i - 1].Imaginary;
        float im = seg[i].Imaginary * seg[i - 1].Real - seg[i].Real * seg[i - 1].Imaginary;
        audio[i] = new Complex32((float)Math.Atan2(im, re), 0f);
      }
      audio[0] = n > 1 ? audio[1] : default;
      return audio;
    }




    // ----------------------------------------------------------------------------------------------------
    //                                       MLSE decision stage
    // ----------------------------------------------------------------------------------------------------
    /// <summary>
    /// Coherent MLSE decision stage over the analytic audio subcarrier (Phase-3 MLSE for h = 5/6):
    /// FM-discriminate to audio as usual, but instead of the non-coherent |mark|−|space| envelope
    /// difference, mix the audio once at −af_carrier to a complex ±dev FSK baseband and run the
    /// generalized rational-h <see cref="MlsePspDetector"/> (Bell-202: h = 2·500/1200 = 5/6 → 12 phase
    /// states, true non-orthogonal tone correlations) at the correlator DPLL's strobes. Timing still
    /// comes from the proven correlator + DPLL chain — only the per-symbol decision rule changes.
    /// The soft output is flipped to the correlator's mark-positive sign convention. A runt burst
    /// (too few strobes) falls back to the default path.
    /// </summary>
    public SoftSymbols DemodulateSegmentMlse(Complex32[] seg, SignalParams p)
    {
      // correlator decision signal, centred exactly as the default path — it drives the DPLL timing
      float[] demod = CorrelatorDemod(seg, p);
      CpmFskDemodulator.CenterGlobal(demod);
      double sps = p.SampleRate / p.Baud;
      double[] strobes = DpllStrobes(demod, sps, reversed: false);
      if (strobes.Length < MinStrobesForFit) return DemodulateSegmentCorrelator(seg, p);

      // analytic subcarrier baseband: audio mixed down from af_carrier, low-passed to kill the
      // −2·af_carrier image of the real audio signal (cutoff dev + 0.75·baud passes the tones plus
      // the transition band — the CPM channel-filter sizing rule)
      var bb = DiscriminateAudio(seg, p);
      double afCarrier = p.AfCarrier ?? DefaultAfCarrierHz;
      double dev = p.Deviation ?? DefaultDeviationHz;
      global::VE3NEA.Dsp.Mix(bb, -afCarrier / p.SampleRate);
      double fc = (dev + 0.75 * p.Baud) / p.SampleRate;
      int taps = Math.Max(41, Math.Min((int)Math.Round(6 * sps) | 1, 511));
      bb = LiquidFir.ConvolveSame(bb, KernelCache.BlackmanSinc(fc, taps));

      double h = 2 * dev / p.Baud;
      var profile = new ModProfile { Pulse = PulseShape.Rectangular, Bt = null, ModIndex = h };
      var det = new MlsePspDetector(profile, new GmskDemodOptions());
      var soft = det.Detect(new DetectorContext
      {
        Baseband = bb,
        GardnerSoft = new float[strobes.Length],
        Strobes = strobes,
        Sps = sps,
        Params = p with { Deviation = dev }
      });
      // the detector's a = +1 is the +dev (space, 2200 Hz) tone; downstream expects mark positive
      for (int k = 0; k < soft.Length; k++) soft[k] = -soft[k];

      var (eyeDb, ambig) = CpmFskDemodulator.EyeQuality(soft);
      return new SoftSymbols
      {
        Soft = soft,
        SymbolRate = p.Baud,
        SamplesPerSymbol = sps,
        EyeSnrDb = eyeDb,
        AmbiguousFraction = ambig
      };
    }
  }
}
