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
  /// then a direwolf-style transition-nudged DPLL (<see cref="DpllSync"/>) recovers symbol timing and the AX.25
  /// deframer is reused unchanged — only this front end is AFSK-specific. (The shared Gardner PI loop was tried but
  /// its whole-burst Oerder–Meyr seed is corrupted by the fading tail and it locks half a symbol off; the DPLL is
  /// robust there. A whole-burst timing fit that beats the DPLL is future work — see the Phase 2 plan.)
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

    public SoftSymbols Demodulate(Complex32[] iq, Burst burst, SignalParams p)
      => DemodulateSegment(Acquisition.Derotate(iq, burst), p);

    /// <summary>Demodulate an already CFO-corrected RF segment: discriminate to audio, run the mark/space tone
    /// correlator to a real decision signal, then recover timing + soft symbols with the shared engine.</summary>
    public SoftSymbols DemodulateSegment(Complex32[] seg, SignalParams p)
    {
      float[] demod = CorrelatorDemod(seg, p);
      CpmFskDemodulator.CenterGlobal(demod);   // whole-burst two-cluster threshold → decision signal centred on 0

      double sps = p.SampleRate / p.Baud;
      var (soft, settledSps) = DpllSync(demod, sps);
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

    /// <summary>
    /// Direwolf-style digital PLL symbol timing: a phase accumulator advances by one symbol per revolution and
    /// samples <paramref name="demod"/> at each wrap; every zero-crossing (bit transition) nudges the accumulator
    /// toward the transition. A first-order, transition-tracked loop that acquires within a couple symbols and
    /// re-locks through fades — more robust on the marginal AFSK burst than the Gardner PI loop, whose whole-burst
    /// Oerder–Meyr seed is corrupted by the fading tail.
    /// </summary>
    private static (float[] soft, double sps) DpllSync(float[] demod, double sps)
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

      // FM-discriminate the RF to real audio (rad/sample). Carried in the real part of a complex buffer so the
      // shared complex mixer can rotate it; a constant CFO only adds a DC term (the tones stay at ±dev about af_carrier).
      var mark = new Complex32[n];
      for (int i = 1; i < n; i++)
      {
        float re = seg[i].Real * seg[i - 1].Real + seg[i].Imaginary * seg[i - 1].Imaginary;
        float im = seg[i].Imaginary * seg[i - 1].Real - seg[i].Real * seg[i - 1].Imaginary;
        mark[i] = new Complex32((float)Math.Atan2(im, re), 0f);
      }
      mark[0] = n > 1 ? mark[1] : default;
      var space = (Complex32[])mark.Clone();

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
  }
}
