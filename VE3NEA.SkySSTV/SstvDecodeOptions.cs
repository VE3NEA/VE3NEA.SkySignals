namespace VE3NEA.SkySSTV
{
  /// <summary>Which signal the impulse blanker gates on (declick plan §6, item 1a).</summary>
  public enum BlankerGateMode
  {
    /// <summary>The envelope of the channel-filtered signal, i.e. the fade proxy. FM clicks live in
    /// envelope fades, so a fade marks the discriminator samples that are unreliable.</summary>
    Envelope,

    /// <summary>The discriminator output itself, with the modulation removed by a short running median.
    /// Gates on the quantity actually being removed rather than on a proxy for it.</summary>
    Amplitude
  }

  /// <summary>What the impulse blanker does with a run it has condemned (declick plan §3.2 item 1).</summary>
  public enum BlankerRepairMode
  {
    /// <summary>Replace the run by a straight line between its neighbours — the P6(c)-locked behavior.
    /// Removes the slip along with everything else the run carried.</summary>
    Interpolate,

    /// <summary>Subtract the run's excess AREA, leaving its samples otherwise intact. §3.2 item 1's
    /// prescription: replacing samples is a multiplicative operation that amplitude-modulates everything
    /// else in the passband and manufactures its own artifacts, whereas removing the offending area takes
    /// the slip out — including the part below the noise floor — without touching the wanted signal.</summary>
    SubtractArea
  }

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


    /// <summary>The envelope-gate threshold used on STRONG signals, i.e. the far end of the ramp described
    /// under <see cref="BlankerCvStrong"/>; <see cref="BlankerThreshold"/> is the weak-signal end. Locked by
    /// the declick plan's 1b sweep (2026-07-25) — see that item for the table. Ignored when
    /// <see cref="BlankerThreshold"/> is 0, which disables the whole stage, ramp included.</summary>
    public double BlankerStrongThreshold { get; init; } = 0.2;


    /// <summary>Measured envelope coefficient of variation at or below which <see
    /// cref="BlankerStrongThreshold"/> is used alone; the threshold ramps linearly up to
    /// <see cref="BlankerThreshold"/> at <see cref="BlankerCvWeak"/>. Set <see cref="BlankerCvWeak"/> ≤ this
    /// to disable the ramp and gate at the fixed <see cref="BlankerThreshold"/> (the pre-2026-07-25
    /// behavior).
    ///
    /// <para>Rationale (declick plan 1b, <c>SstvDeclickProbe.BlankerThresholdLadder</c>): the best fixed
    /// threshold falls monotonically with CNR, and no constant is close to right at both ends — brightness
    /// error at 0.5 vs 0.2 reads 155.6 / 164.9 luma at 0 dB in-channel CNR but 45.5 / 37.0 at +8, and at
    /// +6 dB the smaller threshold takes mean <c>WideWeight</c> from 0.33 to 0.54 while ALSO lowering the
    /// brightness error. The gate is not the thing to switch off above the crossover: at +10 dB, 0.2 gates
    /// 0.14 % of samples for +1.6 dB PSNR over the bypass with the resolution channel already saturated at
    /// 1.00, and at +12 dB with zero clicks present it is a no-op (0.02 % gated). So the policy is a smaller
    /// threshold, not no threshold.</para></summary>
    public double BlankerCvStrong { get; init; } = 0.34;


    /// <summary>Envelope coefficient of variation at or above which <see cref="BlankerThreshold"/> is used
    /// alone (see <see cref="BlankerCvStrong"/>). σ(|z|)/mean(|z|) of the channel-filtered signal, tracked on
    /// the same τ = 100 ms single pole as the gate's mean envelope, so it costs one extra accumulator and
    /// adapts about once per line.
    ///
    /// <para>The pair brackets the 1b crossover (≈+5 dB in-channel CNR) on the measured curve: the ladder
    /// reads 0.443 at +2 dB, 0.402 at +4, 0.355 at +6, 0.310 at +8. The statistic is bounded at both ends and
    /// so cannot run away — as the carrier vanishes the envelope becomes Rayleigh and it saturates at
    /// √(4/π − 1) = 0.523 (measured 0.522 at −10 dB), and the floor is the transmitted signal's own ripple,
    /// 0.168 noise-free, since the ±4 kHz channel clips the FM's Carson tails. Deliberately NOT the per-line
    /// σ that drives <see cref="AdaptiveSigmaLow"/>: that one is estimated two stages downstream of the gate,
    /// so feeding it back would couple the blanker to the reconstruction.</para></summary>
    public double BlankerCvWeak { get; init; } = 0.42;


    /// <summary>Which signal the blanker gates on (declick plan §6 item 1a). Default
    /// <see cref="BlankerGateMode.Envelope"/> — the P6(c)-locked behavior. The alternative gates on the
    /// discriminator output itself, because the envelope is only a proxy and the two decorrelate exactly
    /// where it matters: the FM-speech experiment measured the mean envelope at the centre of a pulse at
    /// 0.89 of its running mean (above any usable fade threshold) while dipping to 0.43 on the flanks, so
    /// an envelope gate blanks the shoulders of each pulse and leaves its peak. Selecting
    /// <see cref="BlankerGateMode.Amplitude"/> makes <see cref="BlankerRmsMultiple"/> the threshold and
    /// leaves <see cref="BlankerThreshold"/> unused.</summary>
    public BlankerGateMode BlankerGate { get; init; } = BlankerGateMode.Envelope;


    /// <summary>Impulse-blanker threshold for <see cref="BlankerGateMode.Amplitude"/>, in multiples of the
    /// running rms of the detrended discriminator output; 0 disables the blanker. The magnitude
    /// distribution is bimodal — a Gaussian core out to about 2 rms, then a separate impulse population —
    /// and 4 sits in the valley between them. Deliberately the rms and not a robust (MAD-based) sigma: the
    /// distribution's whole point is that it is heavy-tailed, so its MAD sits far below its rms and a
    /// "3 sigma" threshold built that way lands near 1 rms and blanks 40 % of the signal.</summary>
    public double BlankerRmsMultiple { get; init; } = 4.0;


    /// <summary>What the blanker does with a condemned run (declick plan 3b). Default
    /// <see cref="BlankerRepairMode.Interpolate"/> — the P6(c)-locked behavior.
    ///
    /// <para><see cref="BlankerRepairMode.SubtractArea"/> instead removes the run's area above the line
    /// interpolation would have drawn, leaving the samples otherwise untouched. The area is the only part of
    /// a slip that survives downstream (0d), so this removes the whole of what matters while keeping the
    /// run's noise — and it is self-limiting in a way interpolation is not: a fade that carried no slip has
    /// little excess area, so little is subtracted, whereas interpolation flattens the run either way.</para>
    ///
    /// <para>This pairs §3.2 item 1's repair with the one detector measured to work on SSTV — the envelope
    /// gate. 1a and 3a both found that no short-window statistic on the disc stream separates slips from the
    /// 1500–2300 Hz subcarrier; the envelope has no such problem, being constant-amplitude by
    /// construction.</para>
    ///
    /// <para>Measured a wash (3b) and therefore not the default: ≤2.0 luma of brightness error either way at
    /// every CNR rung, with no consistent winner, and interpolation smoother on all five corpus captures.
    /// Only the removed AREA survives the ±600 Hz brightness filter, and both modes remove the same
    /// area.</para></summary>
    public BlankerRepairMode BlankerRepair { get; init; } = BlankerRepairMode.Interpolate;


    // NOTE (declick plan 3a, removed 2026-07-26): a `ClickRepair` option and its two thresholds used to sit
    // here, driving a disc-domain area detector ported from the FM-speech experiment. It was measured and
    // deleted, not merely defaulted off: the 1500-2300 Hz subcarrier is only 21-32 samples per period against
    // a ~6-sample slip, so the modulation ate 37-73 % of the detection threshold and REVERSED the sign of
    // real slips (of 24 arrivals: 12 repaired, 5 missed, 7 made worse). It barely triggered on real captures,
    // so it was dead weight that looked like a feature. The finding is kept in the plan and findings docs;
    // do not re-add a short-window robust statistic on this stream without reading them first.


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
    /// the mix-to-baseband, i.e. the streaming analytic/brightness filter (plan §1.4/§6.1) — the NARROW
    /// branch of the §6.3 adaptive pair, used at full weight on noise-dominated lines. Wider = sharper pixel
    /// edges + more noise, narrower = smoother + less noise. The real filter sweep (2026-07-02, vs the
    /// RXSSTV reference) put the sweet spot at 500–650 Hz — Hopper's ±500 Hz choice confirmed; 350 Hz
    /// over-smooths, 1800 Hz leaves heavy speckle on real signals. That sweep was judged on a NOISY capture,
    /// which is why it is the narrow end of the pair and not a global default: see
    /// <see cref="BrightnessWideBwHz"/>.</summary>
    public double BrightnessBwHz { get; init; } = 600.0;

    /// <summary>Half-bandwidth (Hz) of the WIDE Stage-3 branch (plan §6.3), used at full weight on
    /// noise-free lines; the reconstruction blends the two branches per line against the measured noise
    /// (<see cref="AdaptiveSigmaLow"/>/<see cref="AdaptiveSigmaHigh"/>). Set ≤ <see cref="BrightnessBwHz"/>
    /// to disable the second branch entirely (the pre-2026-07-25 fixed-narrow behavior).
    ///
    /// Rationale (2026-07-25, <c>SstvSmoothingProbe</c>): Robot36 carries 320 pixels in 88 ms → 3636 px/s,
    /// pixel Nyquist 1818 Hz, and at ±400 Hz deviation the subcarrier's Carson half-width is ≈2.2 kHz — so
    /// ±600 Hz keeps about a quarter of the video band and smears 1-pixel strokes UNCONDITIONALLY, at any
    /// SNR. The noise-free closed loop proves it is the filter and not the Wiener post-filter: a rendered
    /// text card decodes illegibly at 600 Hz (PSNR 11.4 dB) and cleanly at 1200–1600 Hz (13.4/14.1 dB) with
    /// zero noise present. Ceiling: the 1900 Hz mix puts the spectral mirror at −3800 Hz, whose upper edge
    /// sits near −1580 Hz, so a cutoff above ~1500 Hz folds the mirror in as diagonal cross-hatch (visible
    /// at 2400 Hz in the same probe). 1200 rather than 1600 because the caption line is already fully
    /// legible there while the decoded row noise is nearly half (7.1 vs 12.9 luma units on the 286 s
    /// burst) — the last 400 Hz buys sharpness that the extra speckle immediately spends.</summary>
    public double BrightnessWideBwHz { get; init; } = 1200.0;

    /// <summary>Per-line noise σ (in 0..255 luma units, measured on the wide branch by
    /// <see cref="SstvDecoder.WideWeight"/>) at or below which the wide branch is used alone. Below this the
    /// line is clean enough that the extra bandwidth costs nothing visible and buys back the small text.
    /// Note this σ is taken BEFORE pixel integration, so it runs several times the post-integration row
    /// noise the Wiener filter sees.</summary>
    public double AdaptiveSigmaLow { get; init; } = 35.0;

    /// <summary>Per-line noise σ (0..255 luma units) at or above which the narrow branch is used alone —
    /// the line is noise-dominated and the resolution is unrecoverable, so take the noise rejection. The
    /// blend ramps linearly between <see cref="AdaptiveSigmaLow"/> and this value.
    ///
    /// The 35/65 pair is anchored on the corpus (2026-07-25, <c>SstvSmoothingProbe.SigmaCalibration</c>,
    /// per-burst median σ) against the visual before/after: the 07-23 21:42 burst at 286 s reads 34 and is
    /// the case that must go fully wide (its caption line is illegible at 600 Hz and clean at 1200); the
    /// Monitor-3 285 s text card reads 62 and gains nothing legible from the extra bandwidth (its text is
    /// large) while picking up speckle, so it sits at ≈0.1; the 566 s burst (78), the Monitor-3 135 s burst
    /// (208) and the below-threshold 04-18 capture (224) stay fully narrow, i.e. bit-identical to the
    /// pre-adaptive decoder. The estimator is content-robust as measured — replacing the across-line median
    /// with the 25th percentile moves every burst by under 5 %, so the p10..p90 spread WITHIN a burst
    /// (22..58 on the 286 s case) is real fading, and the per-line ramp tracks it deliberately.</summary>
    public double AdaptiveSigmaHigh { get; init; } = 65.0;

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

    /// <summary>Enable the Wiener (Lee) post-filter on the reconstructed Y/Cr/Cb planes
    /// (<see cref="SstvWienerFilter"/>, plan §6.2). Default ON per the P6(d) visual judgment
    /// (2026-07-04): the w9×5 / chroma-k4 / no-shrink variant gave the best denoising on every
    /// decodable real burst while preserving fine structure (text). Disable to inspect the raw
    /// reconstruction — on below-FM-threshold bursts (the umka0418 class) the raw image shows
    /// marginally more detail through the noise.</summary>
    public bool WienerEnabled { get; init; } = true;
  }
}
