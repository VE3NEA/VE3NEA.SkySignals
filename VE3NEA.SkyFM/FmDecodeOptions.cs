namespace VE3NEA.SkyFM
{
  /// <summary>
  /// Tunables for the FM voice front-end (ASR-plan.md §5.1–5.2). Defaults are the spike-locked settings
  /// (plan §8.7): ±15 kHz channel, flat (no de-emphasis) canonical audio, blanker on, broadband squelch.
  /// </summary>
  public sealed record FmDecodeOptions
  {
    /// <summary>Complex input sample rate (Hz). Must be an integer multiple of
    /// <see cref="OutputSampleRate"/>.</summary>
    public double SampleRate { get; init; } = 48000.0;

    /// <summary>Stage-1 complex channel low-pass cutoff (Hz), i.e. half the pass-bandwidth. Spike-locked
    /// at ±15 kHz: clears the full FM Carson width for wide-deviation voice (ISS); narrower A/Bs
    /// (±10/±7 kHz) truncate the sidebands and lose on both the speech-presence proxy and weak-burst
    /// decoding (plan §5.1). 0 disables the stage.</summary>
    public double ChannelBwHz { get; init; } = 15000.0;

    /// <summary>Envelope-gated impulse-blanker threshold, as a fraction of the running mean envelope of
    /// the channel-filtered signal; 0 disables the blanker. Same statistic as the SSTV decoder it is
    /// ported from: FM clicks live in envelope fades, so discriminator samples inside a fade are replaced
    /// by interpolation across it.</summary>
    public double BlankerThreshold { get; init; } = 0.5;

    /// <summary>De-emphasis time constant (µs) applied to the discriminated audio; 0 (the default)
    /// disables the stage. Spike-measured neutral for recognition (recovered no new words) — the flat
    /// variant is canonical, 750 µs (EIA/amateur NBFM) optional for listening (plan §5.1).</summary>
    public double DeEmphasisUs { get; init; } = 0.0;

    /// <summary>Low edge (Hz) of the Stage-2 voice bandpass on the discriminated audio. The high-pass
    /// skirt also removes the DC Doppler term and the 67 Hz CTCSS (plan §5.1).</summary>
    public double VoiceLowHz { get; init; } = 250.0;

    /// <summary>High edge (Hz) of the Stage-2 voice bandpass. Also the anti-alias band-limit for the
    /// decimation to <see cref="OutputSampleRate"/>.</summary>
    public double VoiceHighHz { get; init; } = 3400.0;

    /// <summary>Output audio sample rate (Hz) — 16 kHz mono, the rate the ASR engines consume.</summary>
    public int OutputSampleRate { get; init; } = 16000;

    /// <summary>Squelch OPEN threshold: the squelch opens (carrier present) when the smoothed
    /// above-voice-band noise amplitude of the discriminator output, in cycles/sample units, falls below
    /// this level. Default from the SkyRoof <c>SoftSquelch</c> this stage reuses.</summary>
    public double SquelchOpenLevel { get; init; } = 0.08;

    /// <summary>Squelch CLOSE threshold (hysteresis pair of <see cref="SquelchOpenLevel"/>): the squelch
    /// closes when the smoothed noise amplitude rises above this level.</summary>
    public double SquelchCloseLevel { get; init; } = 0.095;

    /// <summary>Frame length (s) of the exported squelch noise-level track (a per-frame confidence input
    /// for the abstention policy, plan §5.2).</summary>
    public double SquelchFrameS { get; init; } = 0.02;

    /// <summary>Segments whose squelch-open gap is shorter than this (s) are merged into one
    /// transmission (speech pauses and fades must not split an over).</summary>
    public double SegmentMergeGapS { get; init; } = 0.35;

    /// <summary>Minimum transmission duration (s); shorter squelch openings are noise blips and are
    /// dropped.</summary>
    public double SegmentMinS { get; init; } = 0.3;

    /// <summary>Padding (s) added to each side of a detected transmission, so the squelch's smoothing
    /// latency cannot clip word onsets.</summary>
    public double SegmentPadS { get; init; } = 0.15;
  }
}
