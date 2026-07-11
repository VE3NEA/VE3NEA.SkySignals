namespace VE3NEA.SkyTlm.Core
{
  /// <summary>
  /// Transmit frequency-pulse shape of a CPM/FSK flavor.
  /// <b>Gaussian</b> = GMSK/GFSK (Gaussian-filtered rectangle, <see cref="ModProfile.Bt"/> sets the BT);
  /// <b>Rectangular</b> = MSK / CPFSK (full-symbol rectangle, no pre-filter); <b>None</b> = plain 2-FSK
  /// where the pulse is irrelevant because the detector works per-tone (orthogonal matched filter).
  /// </summary>
  public enum PulseShape { Gaussian, Rectangular, None }

  /// <summary>
  /// The decision stage a CPM/FSK demodulator uses (the pluggable <c>IDetector</c> seam).
  /// <b>Discriminator</b> = the non-coherent FM-discriminator slicer
  /// (Gardner soft output). <b>Differential</b> = decision-feedback differential detection (DF-DD, the GMSK
  /// path, ~2.5–3 dB over the slicer). <b>OrthogonalMatchedFilter</b> = dual-tone correlator for wide-<i>h</i>
  /// 2-FSK. <b>CoherentLinear</b> = exact OQPSK-style coherent receiver for MSK. <b>MlsePsp</b> = coherent
  /// MLSE (Viterbi) over the CPM trellis with per-survivor
  /// phase/CFO tracking, for h = 1/2 GMSK/MSK and (full-response, rectangular-pulse) rational
  /// h = m/p like Bell-202 AFSK's h = 5/6. A <c>null</c> detector on a profile
  /// means "resolve from <see cref="Dsp.GmskDemodOptions"/>" (the GMSK/GFSK rule: DF-DD when
  /// <c>DifferentialOrder ≥ 2</c>, else the discriminator).
  /// </summary>
  public enum DetectorKind { Discriminator, Differential, OrthogonalMatchedFilter, CoherentLinear, MlsePsp }

  /// <summary>
  /// Per-flavor description of a CPM/FSK modulation — the knobs that distinguish GMSK / GFSK / MSK / plain
  /// 2-FSK, separate from the algorithmic tunables in <see cref="Dsp.GmskDemodOptions"/>. Consumed by the
  /// generic <see cref="Dsp.CpmFskDemodulator"/> engine so a new binary FSK flavor is a new profile (plus,
  /// where its structure pays off, a new <c>IDetector</c>) rather than a new demodulator.
  /// </summary>
  public sealed record ModProfile
  {
    /// <summary>Transmit frequency-pulse shape.</summary>
    public PulseShape Pulse { get; init; } = PulseShape.Gaussian;

    /// <summary>Gaussian BT (Gaussian pulse only); <c>null</c> for rectangular/no pulse.</summary>
    public double? Bt { get; init; } = 0.5;

    /// <summary>Modulation index <i>h</i> = 2·(peak deviation)/Rs. GMSK/MSK = 0.5; large for wide 2-FSK.</summary>
    public double ModIndex { get; init; } = 0.5;

    /// <summary>Symbol alphabet size (2 = binary, today). Reserved for the deferred M-ary FSK cycle.</summary>
    public int MaryOrder { get; init; } = 2;

    /// <summary>
    /// Detector to use, or <c>null</c> to let the engine pick from <see cref="Dsp.GmskDemodOptions"/>
    /// (the GMSK/GFSK rule). Future flavors pin it explicitly (e.g. 2-FSK → OrthogonalMatchedFilter).
    /// </summary>
    public DetectorKind? Detector { get; init; }

    /// <summary>GMSK: Gaussian BT≈0.5, h=0.5, binary; detector resolved from options (DF-DD in the pipeline).</summary>
    public static ModProfile Gmsk { get; } = new();

    /// <summary>
    /// GFSK: Gaussian-pulse binary CPFSK like GMSK, but the modulation index is <b>not</b> pinned to 0.5.
    /// the engine honors the real <i>h</i> per signal: the discriminator scales by the looked-up
    /// <see cref="SignalParams.Deviation"/> and DF-DD advances its phase reference by ±π<i>h</i>
    /// (h = 2·<see cref="SignalParams.Deviation"/>/Rs), falling back to this profile's <see cref="ModIndex"/> default
    /// only when the deviation is unknown. The Gaussian BT likewise comes from the signal/options; the static
    /// values here are the defaults. When a GFSK signal happens to be h=0.5 the path is identical to GMSK.
    /// </summary>
    public static ModProfile Gfsk { get; } = new();

    /// <summary>
    /// Wide-<i>h</i> binary 2-FSK: no transmit pulse filter and a non-coherent <b>orthogonal
    /// matched-filter</b> detector (dual-tone correlator). The frequency pulse is irrelevant — the detector
    /// works per tone — so <see cref="Bt"/> is null. The real <i>h</i> (often ≫ 0.5, e.g. HADES-SA 2.0 / 5.6)
    /// comes from the signal's <see cref="SignalParams.Deviation"/>; the <see cref="ModIndex"/> here is only a
    /// fallback when no deviation is resolved.
    /// </summary>
    public static ModProfile Fsk { get; } = new()
    {
      Pulse = PulseShape.None,
      Bt = null,
      ModIndex = 1.0,
      Detector = DetectorKind.OrthogonalMatchedFilter,
    };
  }
}
