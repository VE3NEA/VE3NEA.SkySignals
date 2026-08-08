namespace VE3NEA.SkySSTV
{
  /// <summary>Which post-filter runs on a reconstructed image (denoise plan §2 D2).</summary>
  public enum SstvDenoiseMethod
  {
    /// <summary>No post-filter: the raw reconstruction.</summary>
    None,

    /// <summary>The local Wiener (Lee) filter — cheap, row-local, the decode-path default (D15).</summary>
    Wiener,

    /// <summary>Non-local means — manual only, never applied unattended (D14).</summary>
    Nlm
  }

  /// <summary>
  /// How the Wiener detector's per-pixel gain becomes the non-local means noise variance
  /// (denoise plan §5.4 / §9.1). <b>Undecided</b>: these are the arms of a visual experiment, not
  /// a settled preference. <see cref="RowOnly"/> is the control that must always be measurable.
  /// </summary>
  public enum SstvNlmNoiseMap
  {
    /// <summary>Control arm: <c>s = σ²row</c>, the detector unused — plain NLM with a per-row σ.
    /// Without this arm we cannot tell whether the detector contributes anything at all.</summary>
    RowOnly,

    /// <summary>Inflate where the detector says noise: <c>s = σ²row·(1 + k·(1−g))</c>. The prior,
    /// because its failure mode (a misjudged pixel is averaged slightly more) is survivable, unlike
    /// <see cref="GainDeflate"/>'s.</summary>
    GainInflate,

    /// <summary>Deflate where the detector says signal: <c>s = σ²row·max(ε, g)</c>. Sharper in
    /// principle, but a detector false negative on a thin stroke scales it down permanently — which
    /// is precisely the failure this plan exists to avoid, so it is an arm and not the default.</summary>
    GainDeflate,

    /// <summary>The gain shapes only the patch distance, leaving the donor weighting purely
    /// inverse-variance.</summary>
    DistanceOnly
  }

  /// <summary>
  /// Tunables for the image post-filters. Embedded in <see cref="SstvDecodeOptions"/> as the
  /// decode-time default, and passed directly by the denoise dialog — one type for both, so the
  /// Wiener parameters are never duplicated (denoise plan §4.1, D11).
  ///
  /// <para><b>The NLM constants are provisional.</b> They are the subject of the plan's §9
  /// experiments and are settled by visual comparison against the RX-SSTV / MMSSTV decodes of the
  /// same signal, NOT by matching the Delphi reference implementation — its constants are
  /// calibrated against a noise estimator we deliberately replaced with a better one (D20).</para>
  /// </summary>
  public sealed record SstvDenoiseOptions
  {
    /// <summary>Which filter to run. <see cref="SstvDenoiseMethod.Wiener"/> is the decode-path
    /// default and reproduces the pre-2026-08 behavior.</summary>
    public SstvDenoiseMethod Method { get; init; } = SstvDenoiseMethod.Wiener;


    // ----------------------------------------------------------------------------------------------------
    //                                        Wiener (Lee) filter
    // ----------------------------------------------------------------------------------------------------


    /// <summary>Width (pixels) of the Wiener local window; P6(d)-locked value 9.
    ///
    /// <para>The window is BOTH the smoothing kernel and the detection aperture, and the second role
    /// is what makes the width critical: the gain test asks whether local variance rises above the
    /// noise, and a thin stroke is diluted across the whole window. Over 9×5 = 45 samples a one-pixel
    /// stroke needs ≈6.8 σ to reach g &gt; 0 while a broad edge passes at 2.0 σ — a selective bias
    /// against fine detail, present at any SNR. This is the bias that motivated the NLM work.</para></summary>
    public int WienerWindowW { get; init; } = 9;

    /// <summary>Height (lines) of the Wiener local window; P6(d)-locked value 5. The only stage in
    /// the whole chain that smooths ACROSS scan lines, so all vertical correlation in the output
    /// originates here.</summary>
    public int WienerWindowH { get; init; } = 5;

    /// <summary>Width of a SEPARATE, smaller aperture for measuring the local variance that decides
    /// the gain, while <see cref="WienerWindowW"/> still supplies the mean. 0 (the default) uses the
    /// smoothing window for both. Measured to hollow out thick strokes when used for SMOOTHING —
    /// that objection does not transfer to its use as an NLM noise map (plan §5.4).</summary>
    public int WienerDetectW { get; init; } = 0;

    /// <summary>Height of the separate detection aperture; see <see cref="WienerDetectW"/>.</summary>
    public int WienerDetectH { get; init; } = 0;

    /// <summary>Lower bound on the Wiener gain — the fraction of the original deviation from the
    /// local mean that survives even where the filter judges the pixel to be pure noise.
    ///
    /// <para>0.25 since 2026-08-04, in response to "MMSSTV produces a sharper image with more detail".
    /// On noise-dominated bursts the measured gain is 0 over 66–75 % of pixels, so the output there IS
    /// the local mean — a <see cref="WienerWindowW"/>×<see cref="WienerWindowH"/> box blur, confirmed
    /// by matching the output's noise autocorrelation to that box to three decimals. The floor keeps
    /// some texture rather than a flat wash. It restores NOISE as a texture cue; it recovers nothing,
    /// which is why NLM exists (plan §1).</para></summary>
    public double WienerGainFloor { get; init; } = 0.25;

    /// <summary>Chroma noise over-weight (plan §6.2). Chroma is smoothed harder than luma
    /// deliberately: colour speckle is far more objectionable than luma grain. Also the reason the
    /// NLM runs the three planes as INDEPENDENT passes rather than one jointly-weighted distance —
    /// the planes are not equal citizens (denoise plan §5.2).</summary>
    public double WienerChromaK { get; init; } = 4.0;


    // ----------------------------------------------------------------------------------------------------
    //                                        non-local means
    // ----------------------------------------------------------------------------------------------------


    /// <summary>Patch half-size: patches are (2·wing+1)² . The reference UI exposes 1/2/3 as
    /// "filter order" and defaults to 3.</summary>
    public int NlmPatchWing { get; init; } = 3;

    /// <summary>Search half-size: donors are sought over (2·wing+1)². The reference UI ties this to
    /// <c>3·PatchWing + 1</c>, i.e. 10 at the default patch wing.</summary>
    public int NlmSearchWing { get; init; } = 10;

    /// <summary>Strength: scales the measured noise variance as <c>s = Sig²·σ²</c>. Smaller ⇒ the
    /// filter assumes less noise ⇒ fewer donors qualify ⇒ gentler.
    ///
    /// <para><b>PROVISIONAL — plan §9.3.</b> 0.4 is the reference UI's default and is NOT expected to
    /// transfer: it is tuned against that implementation's own second-difference noise estimator,
    /// which reads several× low on post-Stage-3 FM noise, whereas we use the vertical
    /// first-difference median (plan §5.3). The value is chosen by a wide log-spaced sweep judged on
    /// denoising quality, with the §5.6 degeneracy diagnostics as guard rails.</para></summary>
    public double NlmSig { get; init; } = 0.4;

    /// <summary>Run the second pass over the pixels where pass 1 left residual impulses (plan §5.5).
    /// An isolated impulse has no similar patch anywhere, so no donor matches it and plain NLM
    /// preserves it; the second pass forces donors to qualify by tripling the assumed noise there.
    /// Costs ≈2× runtime — the sliding-sum machinery runs in both passes, only the accumulate is
    /// masked — so it is measured before it is trusted.</summary>
    public bool NlmTwoPass { get; init; } = true;

    /// <summary>Noise-variance multiplier applied on the second pass.</summary>
    public double NlmSecondPassNoise { get; init; } = 3.0;

    /// <summary>Percentile of the per-pixel residual-noise distribution above which a pixel is
    /// recomputed on the second pass. The threshold is relative to the operator's own distribution,
    /// which is why the reference implementation's (differently calibrated) noise operator can be
    /// reused for it unchanged (plan §5.3).</summary>
    public double NlmSecondPassPercentile { get; init; } = 0.998;

    /// <summary>How the Wiener detector's gain becomes the per-pixel noise variance — an open
    /// experiment (plan §9.1).</summary>
    public SstvNlmNoiseMap NlmNoiseMap { get; init; } = SstvNlmNoiseMap.GainInflate;

    /// <summary>Inflation/deflation strength of <see cref="NlmNoiseMap"/>. Provisional.</summary>
    public double NlmGainK { get; init; } = 3.0;

    /// <summary>Denoise chroma at its NATIVE vertical resolution (plan §5.2 arm A) rather than at
    /// full resolution with its duplicated rows (arm B, the direct transliteration of the RGB
    /// reference).
    ///
    /// <para>Arm B is poison in principle: after <c>FillMissingChroma</c> half the chroma rows are
    /// byte-exact copies, so a chroma patch has a ZERO-distance twin one row away that draws full
    /// weight from the flat-topped kernel while carrying the identical noise sample — the average
    /// gains nothing while the algorithm believes it found strong corroboration. Measured 2026-08-07
    /// to put 2.4–2.7× more donors in the flat top, and to cost 1.6× the runtime. It is retained only
    /// as a measurable arm.</para></summary>
    public bool NlmNativeChroma { get; init; } = true;

    /// <summary>Row bands the accumulation is split into, one per thread; 0 (the default) picks
    /// <see cref="System.Environment.ProcessorCount"/> bands subject to a minimum band height, and 1
    /// forces the serial path.
    ///
    /// <para>Present because the banded and serial forms sum each pixel's contributions in a DIFFERENT
    /// ORDER (plan §6), and floating-point addition is not associative — so the equivalence test needs
    /// to run both. It is not a user-facing setting.</para></summary>
    public int NlmBands { get; init; } = 0;
  }
}
