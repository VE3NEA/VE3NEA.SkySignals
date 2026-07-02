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

    /// <summary>Stage-1 complex channel low-pass cutoff (Hz), i.e. half the pass-bandwidth. Must clear the
    /// FM's full occupied width (well beyond Carson ~dev+f_audio): a cutoff that clips the constant-envelope
    /// tails makes the discriminator spike (brightness noise). ±15 kHz passes typical NBFM SSTV (dev ≤ ~5 kHz)
    /// while rejecting far-out noise; P6 tunes it to the real deviation. 0 disables the stage.</summary>
    public double ChannelBwHz { get; init; } = 15000.0;


    /// <summary>When true (P2 default) the decoder acquires the image start automatically — VIS header if
    /// present (plan §4), otherwise the first 1200 Hz sync pulse (plan §7). When false it decodes at the
    /// fixed <see cref="StartSample"/> (P1 behavior, for closed-loop tests with known timing).</summary>
    public bool Acquire { get; init; } = true;

    /// <summary>Sample index at which the image (first line's sync) begins. Used when <see cref="Acquire"/>
    /// is false, or as the fallback when acquisition finds neither a VIS header nor a sync pulse.</summary>
    public int StartSample { get; init; } = 0;

    /// <summary>Length (samples) of the leading region searched for the VIS header / first sync during
    /// acquisition. Two seconds comfortably covers a header at the very start of the capture.</summary>
    public int AcquireSearchSamples { get; init; } = 96000;

    /// <summary>Half-bandwidth (Hz) of the Stage-3 complex low-pass that isolates the video subcarrier after
    /// the mix-to-baseband, i.e. the streaming analytic/brightness filter (plan §1.4/§6.1). Must clear the
    /// video deviation (±400 Hz around 1900) plus edge sidebands while rejecting the −3800 Hz mix image;
    /// wider = sharper pixel edges + more noise, narrower = smoother + less noise. Tuned in P6(c).</summary>
    public double BrightnessBwHz { get; init; } = 1800.0;

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
