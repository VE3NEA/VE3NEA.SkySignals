using System;
using MathNet.Numerics;
using VE3NEA.SkyTlm.Core;

namespace VE3NEA.SkyTlm.Dsp
{
  /// <summary>
  /// AFSK-over-FM demodulator (Bell-202 audio subcarrier carried on an FM link, e.g. CUBEBUG-2's 1k2 downlink).
  /// The RF is FM, so the audio is one discriminator away; but that audio is itself a 1200 Hz (mark) / 2200 Hz
  /// (space) tone pair, which the plain FSK slicer would sample directly as NRZI and close the eye. This adds the
  /// missing audio→symbol stage: <b>FM-discriminate the RF → real audio → complex-mix down by the audio
  /// subcarrier (<see cref="SignalParams.AfCarrier"/>) so the two tones straddle DC at ±<see
  /// cref="SignalParams.Deviation"/></b>, then hand the resulting complex baseband to the shared
  /// <see cref="CpmFskDemodulator"/> with the plain-2-FSK profile (the non-coherent orthogonal dual-tone matched
  /// filter). The whole FSK engine — channel filter, Gardner timing, eye metric — and the AX.25 deframer are
  /// reused unchanged; only this front end is AFSK-specific. This is the gr-satellites AFSK chain
  /// (discriminate → freq-xlate by af_carrier → FSK demod at the tone half-spacing) expressed over the SkyTlm
  /// engine. Keeping the modulation tagged AFSK (not collapsed to FSK) also makes <see cref="CfoEstimator"/> use
  /// carrier-symmetry CFO, which locks the dominant unmodulated carrier line instead of the mark sideband.
  /// </summary>
  public sealed class AfskDemodulator : IDemodulator
  {
    /// <summary>Bell-202 audio subcarrier centre (Hz) — midway between the 1200/2200 tones.</summary>
    public const double DefaultAfCarrierHz = 1700.0;

    /// <summary>Bell-202 tone half-spacing (Hz) = (2200 − 1200)/2 — the FSK deviation of the down-mixed baseband.</summary>
    public const double DefaultDeviationHz = 500.0;

    private readonly CpmFskDemodulator inner;

    public AfskDemodulator(GmskDemodOptions? options = null)
      => inner = new CpmFskDemodulator(ModProfile.Fsk, options);

    public SoftSymbols Demodulate(Complex32[] iq, Burst burst, SignalParams p)
      => DemodulateSegment(Acquisition.Derotate(iq, burst), p);

    /// <summary>Demodulate an already CFO-corrected RF segment: discriminate to audio, mix to baseband,
    /// then run the shared FSK engine at the tone half-spacing.</summary>
    public SoftSymbols DemodulateSegment(Complex32[] seg, SignalParams p)
    {
      Complex32[] baseband = AudioToBaseband(seg, p);
      // the down-mixed baseband is a plain 2-FSK signal: tones at ±(tone half-spacing) about DC. Reuse the FSK
      // engine (orthogonal MF) at that deviation. The audio→baseband step keeps the sample rate, so the settled
      // samples/symbol the caller reads back still maps symbol index → stream time unchanged.
      var pInner = p with
      {
        Modulation = Modulation.FSK,
        Deviation = p.Deviation ?? DefaultDeviationHz
      };
      return inner.DemodulateSegment(baseband, pInner);
    }

    /// <summary>
    /// FM-discriminate the RF burst to the real audio waveform (<c>arg(x[n]·conj(x[n−1]))</c>, the message
    /// signal), then complex-mix that audio down by the audio subcarrier so the 1200/2200 Hz tones move to
    /// ∓(tone half-spacing) about DC — the symmetric-about-DC form the FSK engine and its orthogonal detector
    /// expect. The real audio is carried in the real part of a complex buffer so the shared complex mixer can
    /// rotate it; the mixer's image (at −af_carrier−tone) lands well outside the FSK channel filter's passband
    /// and is dropped by the engine's own channel filter. The discriminator's constant scale is immaterial —
    /// the orthogonal detector is magnitude-ratio normalized.
    /// </summary>
    private static Complex32[] AudioToBaseband(Complex32[] seg, SignalParams p)
    {
      int n = seg.Length;
      var audio = new Complex32[n];
      for (int i = 1; i < n; i++)
      {
        // arg(seg[i] · conj(seg[i-1])) — instantaneous frequency (rad/sample) = the recovered audio sample
        float re = seg[i].Real * seg[i - 1].Real + seg[i].Imaginary * seg[i - 1].Imaginary;
        float im = seg[i].Imaginary * seg[i - 1].Real - seg[i].Real * seg[i - 1].Imaginary;
        audio[i] = new Complex32((float)Math.Atan2(im, re), 0f);
      }
      audio[0] = n > 1 ? audio[1] : default;

      // mix the audio subcarrier down to DC: multiply by exp(−j2π·fc·n). tones 1200/2200 → ∓(fc−tone) ≈ ∓500 Hz.
      double fc = (p.AfCarrier ?? DefaultAfCarrierHz) / p.SampleRate;
      global::VE3NEA.Dsp.Mix(audio, -fc);

      // low-pass the down-mixed baseband to ±2·deviation (the gr-satellites af-carrier filter cutoff), removing
      // the mixer image at −(af_carrier+tone) and the out-of-band FM-discriminator click noise before the tones
      // reach the FSK engine — the FM discriminator is nonlinear, so its noise is broadband and the tone
      // correlator sees a cleaner eye once it is filtered off here rather than only by the wider FSK channel filter.
      double dev = p.Deviation ?? DefaultDeviationHz;
      double cutoff = 2.0 * dev / p.SampleRate;
      if (cutoff < 0.5)
      {
        int taps = Math.Max(41, Math.Min((int)Math.Round(6 * (p.SampleRate / p.Baud)) | 1, 511));
        audio = LiquidFir.ConvolveSame(audio, KernelCache.BlackmanSinc(cutoff, taps));
      }
      return audio;
    }
  }
}
