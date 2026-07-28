using VE3NEA.SkyTlm.Core;

namespace VE3NEA.SkyTlm.Discovery
{
  /// <summary>Tunables for a discovery search (discover_params_plan.md §4.3–§4.5).</summary>
  public sealed record DiscoveryOptions
  {
    /// <summary>
    /// Soft-bit quality (<see cref="SoftSymbols.EyeSnrDb"/>) below which the ~62-configuration CCSDS
    /// enumeration is skipped. <b>The only quality gate in the search</b>: everything cheaper runs
    /// unrestricted, because a hard quality bar fires hardest exactly where FEC earns its keep — a correct
    /// hypothesis at marginal SNR produces mediocre soft bits and still decodes through the coding gain, and
    /// silently losing marginal-but-correct answers is the worst failure mode available (§1.1, §4.3).
    ///
    /// The DB's own CCSDS option set is <b>never</b> gated — only the blind enumeration behind it.
    ///
    /// <c>null</c> (default) disables the gate. The intended value is the lowest soft-bit quality that has
    /// ever produced a CCSDS frame in the corpus, minus margin — measured, not guessed, and explicitly not
    /// <see cref="StreamingOptions.MinEyeSnrDb"/>, which is a burst-validation bar rather than a deframing one.
    /// </summary>
    public double? CcsdsQualityFloorDb { get; init; }

    /// <summary>Hard cap on CCSDS configurations tried per demod hypothesis, so a pathological case cannot
    /// monopolize the analysis slot. 0 disables the enumeration entirely.</summary>
    public int MaxCcsdsConfigurations { get; init; } = 62;

    /// <summary>True when the satellite belongs to the GENESIS/HADES family, which is the only case where
    /// <see cref="Framing.HADES"/> enters the framing sweep (§4.3).</summary>
    public bool GenesisFamily { get; init; }

    /// <summary>
    /// Range of the per-burst symbol-rate line scan, in Bd. Below
    /// <see cref="BaudScanLadderFrom"/> the scan is <b>not</b> restricted to the standard ladder, because
    /// that is where real transmitters sit off it: HADES-SA runs 800 and 200 Bd and GALAPAGOS-UTE-SWSU
    /// 1143 Bd, none of which the ladder or its {2x, 4x, 1/2} relatives can reach from a 1200 Bd starting
    /// point. A ladder-only search misses those outright — measured on the corpus, where stripping
    /// HADES-SA's parameters leaves discovery no reachable hypothesis at all.
    /// </summary>
    public (double Min, double Max) BaudScanRange { get; init; } = (200, 19200);

    /// <summary>
    /// Where the scan stops sweeping continuously and follows the standard rates instead: from here up it
    /// visits only 1200·2^n (1200, 2400, 4800, 9600, 19200), the rates fast telemetry actually runs at.
    /// The off-ladder transmitters that motivate a continuous sweep all live <i>below</i> this point
    /// (HADES-SA 200/800, GALAPAGOS-UTE-SWSU 1143), so the sweep is spent where it buys something instead
    /// of on ~90 candidates nothing transmits at — candidates that are not free of risk either, since a
    /// junk line that decodes one spurious CRC-valid frame is auto-applied (§4.5).
    /// </summary>
    public double BaudScanLadderFrom { get; init; } = 1200;

    /// <summary>Relative step of the baud scan below <see cref="BaudScanLadderFrom"/>.
    /// <see cref="Dsp.BaudVerifier"/> searches +/-2% around each candidate, so a step below 4% covers that
    /// part of the range continuously; 3% leaves margin.</summary>
    public double BaudScanStep { get; init; } = 0.03;

    /// <summary>How many of the scan's strongest lines head the baud order. More than one because a wide
    /// scan is not a clean measurement — the discriminator statistic's low-frequency structure can outscore
    /// the true symbol rate — and each extra line costs only one demodulation.</summary>
    public int MeasuredBaudCandidates { get; init; } = 6;

    /// <summary>
    /// Streaming options for the analysis pipeline. The inner per-burst retry ladder is <b>off</b>: the
    /// outer search varies those axes explicitly, so running both is redundant work that also muddies which
    /// hypothesis actually won (§4.4). The shape gate is off and rejected segments are decoded, because the
    /// segment handed to discovery is already known to hold a burst — re-gating it on a shape template built
    /// from the very parameters under suspicion would discard the burst before any hypothesis is tried.
    /// </summary>
    public StreamingOptions Analysis { get; init; } = new()
    {
      BlindFallback = false,
      DiscriminatorRetry = false,
      TimingRetry = false,
      GateByShape = false,
      DecodeRejected = true
    };
  }
}
