namespace VE3NEA.SkySSTV
{
  /// <summary>
  /// Tunables for the P1 fixed-timing decoder front-end. Defaults target the 48 kHz FM-on-FM chain
  /// the synthetic encoder produces. Filter bandwidths are provisional — the P6 experiment sweeps them
  /// against PSNR (plan §6).
  /// </summary>
  public sealed record SstvDecodeOptions
  {
    /// <summary>Complex input sample rate (Hz).</summary>
    public double SampleRate { get; init; } = 48000.0;

    /// <summary>Stage-1 complex channel low-pass cutoff (Hz), i.e. half the pass-bandwidth, for the
    /// DETECTION/timing chain. Must clear the FM's full occupied width (Carson half-width ~dev+f_audio):
    /// real satellite SSTV measured dev ≈ 3.3 kHz (Real_DeviationProbe 2026-07-02) → Carson ≈ ±5.6 kHz;
    /// ±6 kHz clears it with margin. Narrower LOOKS cleaner but loses weak-train pulses and splits trains
    /// (Real_DetectionChannelSweep 2026-07-02) — the video chain has its own narrower cutoff
    /// (<see cref="VideoChannelBwHz"/>). 0 disables the stage.</summary>
    public double ChannelBwHz { get; init; } = 6000.0;


    /// <summary>Stage-1 cutoff (Hz) for the VIDEO/decode chain, applied by
    /// <c>Decode(Complex32[],…)</c> in place of <see cref="ChannelBwHz"/>. The P6(c) real-capture grid +
    /// visual judgment (Real_P6cDecodeGridProbe 2026-07-03) put the best image quality at ±4 kHz with the
    /// impulse blanker on: the clipped Carson tails become envelope fades whose clicks the blanker excises,
    /// so the narrower channel's noise rejection is kept without the speckle.</summary>
    public double VideoChannelBwHz { get; init; } = 4000.0;


    /// <summary>Envelope-gated impulse-blanker threshold, as a fraction of the running mean envelope of the
    /// channel-filtered signal; 0 disables the blanker. Mined from Hopper's FmNoise experiment (plan §6.1):
    /// DevVsMag.txt shows the discriminator error std is ~6× larger where the instantaneous envelope fades
    /// toward zero — FM clicks live in envelope fades, so discriminator samples taken inside a fade are
    /// replaced by interpolation across it. Default locked by the P6(c) real grid + visual judgment
    /// (2026-07-03): wins or is neutral on every real burst in both chains (clicks 2.4→0 %, 04-18 sync
    /// maxScore 0.221→0.324); a no-op on clean signals (constant envelope ⇒ nothing gated).</summary>
    public double BlankerThreshold { get; init; } = 0.5;


    /// <summary>De-emphasis time constant (µs) applied to the discriminated audio; 0 (the default) disables
    /// the stage (plan §1.3: brightness is the subcarrier's instantaneous frequency, amplitude-independent,
    /// so de-emphasis can only reshape the post-FM noise). Default locked OFF by the P6(c) experiment
    /// (2026-07-04): the real transmitters do NOT pre-emphasize (`Real_PreEmphasisSlopeProbe`: subcarrier
    /// amplitude-vs-frequency tilt ≈ −1 dB flat at a clipping-free channel, vs +1.4/+3.2 dB a 75/750 µs
    /// pre-emphasis would imprint), so de-emphasis is the unmatched null case — it trades subcarrier-edge
    /// sharpness for noise, and the synthetic closed loop prices that at −0.8..−1.2 dB PSNR for 300/750 µs.
    /// Single-pole −6 dB/oct roll-off, corner f = 1/(2πτ); set it to the inverse of the transmitter's
    /// pre-emphasis if a future source does pre-emphasize (plan §6 item 2): 750 µs ≈ 212 Hz (EIA/amateur
    /// NBFM — e.g. the ISS Kenwood TM-D710GA in 1200-baud mode), or the broadcast values 75 µs ≈ 2122 Hz /
    /// 50 µs ≈ 3183 Hz (ITU-R BS.450).</summary>
    public double DeEmphasisUs { get; init; } = 0.0;


    /// <summary>When true (P2 default) the decoder acquires the image start automatically — VIS header if
    /// present (plan §4), otherwise the winning sync train (plan §4.1). When false it decodes at the
    /// fixed <see cref="StartSample"/> (P1 behavior, for closed-loop tests with known timing).</summary>
    public bool Acquire { get; init; } = true;

    /// <summary>Sample index at which the image (first line's sync) begins. Used when <see cref="Acquire"/>
    /// is false, or as the fallback when acquisition finds neither a VIS header nor a sync pulse.</summary>
    public int StartSample { get; init; } = 0;

    /// <summary>Half-bandwidth (Hz) of the Stage-3 complex low-pass that isolates the video subcarrier after
    /// the mix-to-baseband, i.e. the streaming analytic/brightness filter (plan §1.4/§6.1). Wider = sharper
    /// pixel edges + more noise, narrower = smoother + less noise. The real filter sweep (2026-07-02, vs the
    /// RXSSTV reference) put the sweet spot at 500–650 Hz — Hopper's ±500 Hz choice confirmed; 350 Hz
    /// over-smooths, 1800 Hz leaves heavy speckle on real signals.</summary>
    public double BrightnessBwHz { get; init; } = 600.0;

    /// <summary>Low edge (Hz) of the Stage-2 audio bandpass applied to the discriminated audio before ALL
    /// sync / VIS / mode statistics (plan §3, retro item J). The coherence statistic divides by total window
    /// energy, and post-discriminator FM noise is parabolic in frequency — without this band-limit the
    /// 2.4–15 kHz noise (~240× the in-band share) inflates the denominator and crushes real-signal sync
    /// scores. Also removes the DC Doppler term for every detection path. The brightness path has its own
    /// low-pass (<see cref="BrightnessBwHz"/>) and does not use this filter.</summary>
    public double SyncBandLowHz { get; init; } = 1000.0;

    /// <summary>High edge (Hz) of the Stage-2 audio bandpass (see <see cref="SyncBandLowHz"/>). Covers the
    /// full tone set (1100–2300) with margin. Set ≤ <see cref="SyncBandLowHz"/> to disable the stage.</summary>
    public double SyncBandHighHz { get; init; } = 2400.0;

    /// <summary>When true (P3 default) KF1 tracks each line's 1200 Hz sync onset (plan §1.6/§7), correcting
    /// slant (sample-clock error) and coasting through fades. When false the decoder lays every line at the
    /// fixed nominal period from the acquired/fixed start (P1/P2 behavior, for closed-loop tests with known,
    /// slant-free timing).</summary>
    public bool Track { get; init; } = true;

    /// <summary>Fraction of each pixel's sample span, centered, averaged by the matched integrator. &lt;1 trims
    /// the inter-pixel frequency-step transitions.</summary>
    public double PixelWindowFraction { get; init; } = 0.5;
  }
}
