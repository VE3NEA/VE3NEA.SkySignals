using VE3NEA.SkyTlm.Core;

namespace VE3NEA.SkyTlm.Dsp
{
  /// <summary>Tunables for <see cref="CpmFskDemodulator"/> / <see cref="GmskDemodulator"/>; defaults match the SkyRoof GMSK/GFSK corpus.</summary>
  public sealed class GmskDemodOptions
  {
    /// <summary>
    /// Channel low-pass cutoff before the discriminator, in <b>baud units</b> (cutoff = this·Rs Hz).
    /// The derotated burst is still the full recording bandwidth (48 kHz); without band-limiting to
    /// roughly the signal's Carson bandwidth, out-of-band noise makes the FM discriminator explode.
    /// 1.0·Rs comfortably passes the GMSK/GFSK spectrum (energy within ~±0.75·Rs) while cutting the rest.
    /// </summary>
    public double ChannelBwBaud { get; init; } = 1.0;

    /// <summary>Gaussian BT of the receive matched filter (the TX BT for these links is ~0.5).</summary>
    public double FilterBt { get; init; } = 0.5;

    /// <summary>Matched-filter span in symbols (the Gaussian frequency pulse is truncated to this).</summary>
    public int FilterSpanSymbols { get; init; } = 3;

    /// <summary>
    /// Width (in <b>symbols</b>) of the post-discriminator noise low-pass. The discriminator output is
    /// already the TX frequency pulse <c>NRZ⊛g</c>; filtering it again with a <i>full</i> frequency pulse
    /// (matched-filter span ≈ 1 symbol) convolves a second pulse in series and spreads each symbol's energy
    /// over its neighbours — partial-response ISI that closes the eye to ~25%. A short LP a fraction of a
    /// symbol wide removes discriminator noise without that ISI; 0.2 keeps the clean eye near its ~57%
    /// non-coherent ceiling. Set 0 to sample the raw discriminator output (max eye, no noise smoothing).
    /// </summary>
    public double RxSmoothingSymbols { get; init; } = 0.6;

    /// <summary>Gardner loop normalized bandwidth (cycles/symbol). Small = slow, stable tracking.</summary>
    public double LoopBandwidth { get; init; } = 0.01;

    /// <summary>Loop damping factor (≈0.707 critically damped).</summary>
    public double LoopDamping { get; init; } = 0.707;

    /// <summary>Max fractional clock deviation the loop may track away from nominal sps (±).</summary>
    public double MaxClockError { get; init; } = 0.02;

    /// <summary>
    /// Detection mode. <b>0</b> = FM discriminator + slicer (default — the non-coherent baseline the FSK
    /// tools use). <b>2</b> or <b>3</b> = decision-feedback differential detection of order <i>N</i>
    /// (DF-DD, <see cref="DifferentialDetector"/>): align the last <i>N</i> complex symbol-samples to a phase
    /// reference from past decisions, average them, then decide. DF-DD buys ~2.5–3 dB over the discriminator
    /// (N=2) while staying non-coherent (no carrier recovery → CFO/Doppler-robust) and outputs the data
    /// directly (no differential-decode penalty on non-precoded links). Timing recovery still runs on the
    /// discriminator path; DF-DD samples the complex baseband at the recovered strobes. For a profile whose
    /// <see cref="ModProfile.Detector"/> is set explicitly, this option is ignored.
    /// </summary>
    public int DifferentialOrder { get; init; } = 0;

    /// <summary>
    /// DF-DD complex sampling instant relative to the Gardner symbol centre, <b>in symbols</b>. The
    /// differential detector's optimum is the symbol <b>boundary</b> (≈0.5), a half-symbol off the
    /// frequency-detector's centre — at the centre DF-DD is near-random, so this offset is essential.
    /// </summary>
    public double DifferentialSampleOffset { get; init; } = 0.5;

    /// <summary>Width (in symbols) of the complex pre-detection Gaussian low-pass applied before DF-DD
    /// sampling (noise reduction). 0 disables it.</summary>
    public double DifferentialPredetSymbols { get; init; } = 0.5;

    /// <summary>
    /// Target samples/symbol the burst is oversampled <i>toward</i> before demodulation. The actual
    /// integer factor is the <b>nearest power of two to <c>TargetSps/sps</c></b> (clamped to
    /// <c>[1, <see cref="MaxUpsample"/>]</c>), so the oversampling scales with baud — high-baud bursts
    /// whose native sps is starved get the most: 9600 Bd (sps 5) → 8×, 19200 (2.5) → 16×, 4800 (10) → 4×,
    /// 2400 (20) → 2×, ≤1200 Bd (sps ≥ 40) → 1× (untouched). The default 40 ≡ the sps of 1200-baud at
    /// 48 kHz. Oversampling gives the <i>nonlinear</i> FM discriminator and the fractional-strobe
    /// interpolation headroom at low sps; it adds <b>no</b> information (48 kHz already satisfies Nyquist),
    /// so it only helps an sps-limited implementation, never the AWGN/eye ceiling.
    /// <see cref="CpmFskDemodulator.Upsample"/> is a polyphase zero-stuff + windowed-sinc anti-image
    /// interpolator; sps and the symbol-normalized loop gains follow the scaled rate automatically. 0 disables it.
    /// </summary>
    public double UpsampleTargetSps { get; init; } = 40;

    /// <summary>Hard cap on the oversampling factor (safety bound for very low sps, e.g. future high rates).</summary>
    public int MaxUpsample { get; init; } = 16;

    /// <summary>
    /// Use the coherent MLSE/PSP detector (<see cref="MlsePspDetector"/>) instead of the
    /// DF-DD/discriminator rule for profiles that don't pin a detector. Gains ~2–3 dB over DF-DD on
    /// h = 1/2 GMSK/MSK; h ≠ 1/2 signals and continuous streams fall back to DF-DD internally. Default
    /// <b>false at the class level</b> so bare options use the DF-DD chain (unit tests, detector
    /// comparisons); the production pipelines enable it via <see cref="Demodulators.DefaultGmskOptions"/>
    /// (<c>FSKDEMOD_MLSE=0</c> reverts per run).
    /// </summary>
    public bool UseMlse { get; init; } = false;

    /// <summary>
    /// Time constant (in <b>symbols</b>) of the leaky DC blocker used by the <b>continuous</b> demod path
    /// (<see cref="CpmFskDemodulator.TraceStream"/>). Per-burst demod centres the discriminator on the
    /// whole-burst cluster midpoint, but a non-stop stream has no single midpoint — a one-pole high-pass
    /// this wide tracks the slowly drifting baseline (residual CFO after trajectory derotation, data
    /// imbalance) while leaving the symbol-rate content untouched. Unused by the per-burst path.
    /// </summary>
    public double DcBlockSymbols { get; init; } = 32.0;
  }

  /// <summary>
  /// GMSK demodulator — the <see cref="CpmFskDemodulator"/> generic CPM/FSK engine specialized to the GMSK
  /// profile (<c>{Gaussian, BT=0.5, h=0.5, M=2}</c>). This thin shim preserves the GMSK construction
  /// surface (<c>new GmskDemodulator()</c> / <c>new GmskDemodulator(options)</c>) and, by inheritance, all of
  /// the engine's public/internal/static members, so call sites, tests and the live decode path are
  /// byte-identical. New FSK flavors do not
  /// subclass this — they construct <see cref="CpmFskDemodulator"/> with their own <see cref="ModProfile"/>
  /// (see <see cref="Demodulators"/>).
  /// </summary>
  public sealed class GmskDemodulator : CpmFskDemodulator
  {
    public GmskDemodulator(GmskDemodOptions? options = null) : base(ModProfile.Gmsk, options) { }
  }
}
