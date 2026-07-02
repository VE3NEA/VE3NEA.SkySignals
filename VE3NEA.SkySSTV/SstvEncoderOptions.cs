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

    /// <summary>Receiver sample-clock error in parts per million: stretches every segment uniformly,
    /// producing the constant line-rate slant that KF1 corrects (plan §1.6).</summary>
    public double SlantPpm { get; init; } = 0.0;

    /// <summary>Std dev of complex AWGN added per I/Q component (0 = clean). Signal amplitude is 1.</summary>
    public double NoiseStdDev { get; init; } = 0.0;

    /// <summary>RNG seed for the noise, for deterministic tests.</summary>
    public int NoiseSeed { get; init; } = 1;

    /// <summary>Prepend the standard 8-bit VIS header.</summary>
    public bool IncludeVis { get; init; } = true;
  }
}
