namespace VE3NEA.SkySSTV
{
  /// <summary>
  /// Tunables for the synthetic SSTV modulator. Defaults produce a clean 48 kHz FM-on-FM signal with
  /// a VIS header. The impairment knobs (Doppler, slant, noise) drive the closed-loop robustness tests
  /// and the P6 filter experiment.
  /// </summary>
  public sealed record SstvEncoderOptions
  {
    /// <summary>Output complex sample rate (Hz). Matches the decode chain: no resampling.</summary>
    public double SampleRate { get; init; } = 48000.0;

    /// <summary>Peak FM deviation of the RF carrier driven by the unit-amplitude audio (Hz). Default is the
    /// measured real-satellite value ≈ 3.3 kHz (Real_DeviationProbe 2026-07-02, Monitor-3/UTMN2 bursts), so
    /// the synthetic closed loop exercises the same Carson width the tuned decoder filters expect.</summary>
    public double DeviationHz { get; init; } = 3300.0;

    /// <summary>Constant residual carrier offset added to the RF (Hz). For FM-on-FM this is a DC
    /// offset on the recovered audio (plan §1.6) — the decoder removes it downstream.</summary>
    public double DopplerHz { get; init; } = 0.0;

    /// <summary>Linear drift rate of the carrier offset (Hz/s, plan §8). A real pass is not a constant
    /// offset but a ramp (worst near TCA: ~−30 Hz/s at 145 MHz, ~−90 Hz/s at 435 MHz for LEO); on the
    /// recovered audio it is a drifting DC term that stresses the brightness low-pass and the tone
    /// statistics' constant-mean assumption — exactly what the constant <see cref="DopplerHz"/> cannot
    /// exercise.</summary>
    public double DopplerRateHzPerSec { get; init; } = 0.0;

    /// <summary>Receiver sample-clock error in parts per million: stretches every segment uniformly,
    /// producing the constant line-rate slant that KF1 corrects (plan §1.6).</summary>
    public double SlantPpm { get; init; } = 0.0;

    /// <summary>Std dev of complex AWGN added per I/Q component (0 = clean). Signal amplitude is 1.
    ///
    /// <para>This is a <b>full-band</b> figure: the noise is white over the whole 48 kHz span, but the
    /// decoder keeps only <see cref="SstvDecodeOptions.ChannelBwHz"/> of it, so the CNR actually presented
    /// to the discriminator is ~6 dB better than the nominal <c>1/(2σ²)</c> — σ = 0.5 reads "3 dB" and is
    /// really ≈9 dB in-channel. Use <see cref="ChannelCnrDb"/> to specify the in-channel figure instead;
    /// this knob is kept as-is because the existing test floors are calibrated against it.</para></summary>
    public double NoiseStdDev { get; init; } = 0.0;

    /// <summary>Carrier-to-noise ratio (dB) measured in the receiver channel — the FmDenoiser
    /// <c>SignalChain</c> convention, where the requested ratio is the one the demodulator actually sees.
    /// Overrides <see cref="NoiseStdDev"/> when set (declick plan §5, step 0a).
    ///
    /// <para>The carrier is unit-amplitude (power 1) and the noise is white with total power 2σ² spread
    /// over <see cref="SampleRate"/>, of which a receiver of half-bandwidth <see cref="CnrRefBwHz"/> passes
    /// the fraction 2·<see cref="CnrRefBwHz"/>/<see cref="SampleRate"/>; setting that share to 1/cnr gives
    /// σ² = fs / (4·B·cnr). An ideal brick-wall convention — the BlackmanSinc channel filter's equivalent
    /// noise bandwidth is close to but not exactly its cutoff, which is why the ladder is validated by
    /// MEASURED click rate against the real captures (step 0b) rather than trusted from the knob.</para>
    ///
    /// <para>Why it matters: the FM threshold — where clicks are born — sits at a few dB of IN-CHANNEL
    /// CNR. Under <see cref="NoiseStdDev"/> the closed loop never reached it (σ ≤ 0.6 ⇒ ≈9 dB in-channel
    /// ⇒ ≤0.05 % clicks), which is why <c>SstvNoiseTests.Frontend_BlankerAndChannelSweep</c> concluded that
    /// synthetic AWGN cannot reproduce real impulsive FM noise. It can; the ladder was simply never taken
    /// below threshold.</para></summary>
    public double? ChannelCnrDb { get; init; } = null;

    /// <summary>Receiver half-bandwidth (Hz) that <see cref="ChannelCnrDb"/> is referred to. Defaults to
    /// the decoder's detection channel (<see cref="SstvDecodeOptions.ChannelBwHz"/> = 6 kHz); set it to
    /// <see cref="SstvDecodeOptions.VideoChannelBwHz"/> when the experiment is judged on image quality, so
    /// the quoted CNR is the one the video chain sees.</summary>
    public double CnrRefBwHz { get; init; } = 6000.0;

    /// <summary>RNG seed for the noise, for deterministic tests.</summary>
    public int NoiseSeed { get; init; } = 1;

    /// <summary>Prepend the standard 8-bit VIS header.</summary>
    public bool IncludeVis { get; init; } = true;
  }
}
