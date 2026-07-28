using MathNet.Numerics;
using VE3NEA.SkyTlm.Core;
using VE3NEA.SkyTlm.Deframing;
using VE3NEA.SkyTlm.Dsp;

namespace VE3NEA.SkyTlm.Discovery
{
  /// <summary>
  /// A hypothesis that produced a CRC-valid frame from one burst, with the evidence used to rank it against
  /// the others that also qualified (discover_params_plan.md §4.5).
  /// </summary>
  public sealed record DiscoveryCandidate(
    SignalParams Params,
    string Label,
    int Tier,
    int CrcFrames,
    double EyeSnrDb,
    /// <summary>Total FEC work the deframer had to do — corrected bits plus erased bytes. Fewer corrections
    /// means a better-matched hypothesis.</summary>
    int FecWork,
    IReadOnlyList<Frame> Frames);

  /// <summary>
  /// Analyzes one already-segmented burst against the discovery hypothesis set and converges on a
  /// <b>single</b> answer (discover_params_plan.md §4.2–§4.5). Discovery does not detect: it re-analyzes
  /// bursts the live pipeline already segmented, which is what removes every hazard of running N hypotheses
  /// live (§2.1) — no shared detector, no cross-hypothesis duplicate frames, no second decode path.
  ///
  /// <b>Nothing produced here is ever published.</b> The pipeline instances built below have no sinks wired
  /// to them at all — the frames exist only to score hypotheses (§4.6). That is a structural guarantee, not
  /// a convention: there is no code path from this class to KISS output, SatNOGS upload or the frame tree.
  /// </summary>
  public static class BurstDiscovery
  {
    /// <summary>
    /// Try the hypothesis set against one burst segment and return the winning parameter set, or
    /// <c>null</c> when nothing produced a CRC-valid frame. "No parameters found" is a legitimate and useful
    /// outcome — it tells the operator the problem is not the parameters (§4.5).
    ///
    /// The search stops at the first baud that qualifies, but finishes that baud's remaining modulation
    /// variants before choosing: FSK and GFSK at the same rate routinely decode the same bits, and the tie
    /// is broken on eye opening and FEC work rather than on trial order (§4.5). Finishing one baud group is
    /// a handful of extra demodulations, and it is the difference between reporting the right modulation and
    /// reporting whichever one happened to be enumerated first.
    /// </summary>
    /// <param name="segment">The burst's samples. The caller passes the segment the live pipeline reported,
    /// with whatever guard it carries; discovery never re-cuts it.</param>
    /// <param name="sampleRate">Sample rate of <paramref name="segment"/>.</param>
    /// <param name="db">The DB-resolved parameters — the starting point and the tie-break anchor.</param>
    /// <param name="coChannel">Tier 1 hypotheses: the resolved co-channel transmitters (§4.2). May be null.</param>
    /// <param name="options">Search tunables; defaults are the plan's.</param>
    /// <param name="cancel">Checked between hypotheses so a cancelled session stops promptly.</param>
    /// <param name="onHypothesis">Called with each hypothesis label as it is tried, for the progress
    /// display (§7 P3). Called on the analysis thread; keep it cheap.</param>
    /// <param name="snrDb">The burst's reported SNR, passed through to the demodulators.</param>
    public static DiscoveryCandidate? Analyze(Complex32[] segment, double sampleRate, SignalParams db,
      IEnumerable<SignalParams>? coChannel = null, DiscoveryOptions? options = null,
      CancellationToken cancel = default, Action<string>? onHypothesis = null, double snrDb = 20)
    {
      var o = options ?? new DiscoveryOptions();
      var start = db with { SampleRate = sampleRate };

      var measuredBauds = MeasureBauds(segment, sampleRate, start, o);
      var hypotheses = DiscoveryHypotheses.Demodulation(start, coChannel, measuredBauds);
      var framings = DiscoveryHypotheses.Framings(start, o.GenesisFamily);

      var qualified = new List<DiscoveryCandidate>();
      double currentBaud = double.NaN;

      foreach (var h in hypotheses)
      {
        cancel.ThrowIfCancellationRequested();

        // a new baud group starts: if the previous one produced a winner, the search is over (§4.5).
        if (!double.IsNaN(currentBaud) && !SameBaud(currentBaud, h.Params.Baud) && qualified.Count > 0) break;
        currentBaud = h.Params.Baud;
        onHypothesis?.Invoke(h.Label);

        var demodulated = Demodulate(segment, sampleRate, h.Params, o, snrDb);
        if (demodulated == null) continue;
        var (soft, resolved) = demodulated.Value;

        var found = TryFramings(soft, h, resolved, framings, o, cancel);
        if (found != null) qualified.Add(found);
      }

      return Best(qualified, start);
    }

    /// <summary>True when two bauds are the same trial group (the measured rate is never exactly nominal).</summary>
    private static bool SameBaud(double a, double b) => Math.Abs(a - b) <= 0.02 * Math.Max(a, b);



    // ----------------------------------------------------------------------------------------------------
    //                                          demodulation stage
    // ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// Demodulate the whole segment under one hypothesis and return its soft symbols, or <c>null</c> when
    /// the hypothesis cannot be demodulated at all.
    ///
    /// <b>The segment is demodulated as one burst — it is never re-detected.</b> Discovery is handed a span
    /// the live pipeline already segmented and decoded over, including its lead overlap; running a detector
    /// over that span again re-cuts it from the inside and loses signal at both ends. Measured on the
    /// SAKHACUBE USP clip: 5,707 soft symbols live versus 3,852 after re-detection — enough to survive
    /// AX.25/AX100's short frames and to destroy every USP frame, whose FEC block spans most of the burst.
    /// Re-detection would also apply the very detector geometry discovery exists to correct.
    ///
    /// A blind FSK hypothesis still gets its deviation estimated from this burst's own spectrum, exactly as
    /// the pipeline does — that estimation is what makes the deviation axis of §4.2 implicit rather than a
    /// ladder of guesses.
    /// </summary>
    /// <returns>The soft symbols and the parameters they were actually demodulated under — which for a
    /// blind hypothesis carries the deviation estimated from this burst, not the <c>null</c> it started
    /// with. That resolved set is what a successful hypothesis reports, so the operator sees the deviation
    /// the decode used rather than "unknown".</returns>
    private static (SoftSymbols Soft, SignalParams Resolved)? Demodulate(Complex32[] segment,
      double sampleRate, SignalParams p, DiscoveryOptions o, double snrDb)
    {
      try
      {
        var pe = p with { SampleRate = sampleRate };
        double cfoHz = 0;

        using (var cfo = new CfoEstimator(sampleRate, o.Analysis.CfoMaxHz, pe))
        {
          var avgQ = cfo.AveragedSpectrum(segment, 0, segment.Length);
          if (pe.IsBlind)
          {
            // deviation unknown: read it off this burst's PSD (the same estimator, on the same wide blind
            // window, that the pipeline's own blind path uses).
            var est = BlindFskEstimator.Estimate(avgQ, cfo.CenterBin, cfo.BinHz, pe.Baud, o.Analysis.CfoMaxHz);
            cfoHz = est.CfoHz;
            if (est.DeviationHz > 0)
              pe = pe with { Deviation = est.DeviationHz, ResolvedDeviation = est.DeviationHz };
          }
          else
            cfoHz = cfo.AnalyzeSpectrum(avgQ).CfoHz;
        }

        var demod = Demodulators.Create(pe, o.Analysis.GmskOptions);
        if (demod == null) return null;   // a modulation with no demodulator is one hypothesis out of many

        var burst = new Burst(0, segment.Length, sampleRate, cfoHz, snrDb);
        var soft = demod.Demodulate(segment, burst, pe);
        return soft.Count > 0 ? (soft, pe) : null;
      }
      catch (OperationCanceledException) { throw; }
      // a geometry the sample rate cannot carry, a malformed hypothesis: one hypothesis out of many.
      catch { return null; }
    }

    /// <summary>
    /// The strongest symbol-rate lines this burst's own cyclostationary statistic carries, strongest first.
    ///
    /// The scan sweeps <b>continuously</b> up to <see cref="DiscoveryOptions.BaudScanLadderFrom"/> rather
    /// than taking <see cref="BaudVerifier.CandidateBauds"/>'s label-relative set: the verifier's job is to
    /// check a label, but discovery's is to find a rate the label may not be anywhere near. Real
    /// transmitters sit off the standard ladder (HADES-SA at 800 and 200 Bd, GALAPAGOS-UTE-SWSU at
    /// 1143 Bd), and no ladder of doublings from a wrong starting point reaches them. Above the crossover
    /// it follows the standard rates only — every off-ladder case in the corpus is a slow one.
    ///
    /// Several lines are kept rather than only the winner because a wide scan is not a clean measurement:
    /// the discriminator statistic carries low-frequency structure that can outscore the true symbol rate
    /// (measured on the corpus — the top line of a 9600 Bd USP burst lands near 340 Bd). Each extra line
    /// costs one demodulation, so keeping the runners-up buys the off-ladder rates without letting a single
    /// artifact decide the search.
    /// </summary>
    private static List<double> MeasureBauds(Complex32[] segment, double sampleRate, SignalParams db,
      DiscoveryOptions o)
    {
      try
      {
        var candidates = ScanLadder(sampleRate, o);
        if (candidates.Count == 0) return new List<double>();
        // the pre-filter must pass the outer tone plus the transition bandwidth of the fastest candidate,
        // or a faster-than-label signal's line is filtered away before the discriminator sees it.
        double cutoff = (db.Deviation ?? db.Baud / 2) + 0.75 * candidates[^1];
        // keep only readings that clear the "this is a line, not a noise peak" bar. Without it the ranking's
        // tail is junk, and a junk rate is not harmless: an absurd hypothesis that happens to produce one
        // CRC-valid frame is auto-applied (§4.5). Measured on GALAPAGOS-UTE-SWSU, where an unfiltered
        // 16816 Bd reading decoded a spurious AX.25 frame — a 16-bit FCS is thin proof across hundreds of
        // hypotheses, so the hypotheses themselves have to stay plausible.
        return BaudVerifier.AllLines(segment, sampleRate, 0, cutoff, candidates)
          .Where(l => l.Score >= BaudVerifier.MinLineScore)
          .Take(o.MeasuredBaudCandidates)
          .Select(l => l.MeasuredBaud)
          .ToList();
      }
      catch { return new List<double>(); }
    }

    /// <summary>Scan candidates over <see cref="DiscoveryOptions.BaudScanRange"/>, clipped to the rates the
    /// sample rate can carry at 2 samples/symbol: geometrically spaced below
    /// <see cref="DiscoveryOptions.BaudScanLadderFrom"/>, then the standard doublings 1200·2^n above it.
    /// The off-ladder rates the continuous sweep exists for are all slow ones, so above the crossover the
    /// sweep would only be paying for candidates nothing transmits at.</summary>
    private static List<double> ScanLadder(double sampleRate, DiscoveryOptions o)
    {
      var list = new List<double>();
      double max = Math.Min(o.BaudScanRange.Max, sampleRate / 2);

      double continuousMax = Math.Min(o.BaudScanLadderFrom, max);
      for (double b = o.BaudScanRange.Min; b < continuousMax; b *= 1 + o.BaudScanStep) list.Add(b);

      for (double b = Math.Max(o.BaudScanLadderFrom, o.BaudScanRange.Min); b <= max; b *= 2) list.Add(b);
      return list;
    }



    // ----------------------------------------------------------------------------------------------------
    //                                            framing stage
    // ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// Try every framing candidate over one hypothesis's soft bits and return the first that yields a
    /// CRC-valid frame. Deframing is cheap relative to demodulation and is completely independent of it, so
    /// the whole framing sweep runs on the soft bits demodulation already produced.
    /// </summary>
    private static DiscoveryCandidate? TryFramings(SoftSymbols soft, DemodHypothesis h,
      SignalParams resolved, IReadOnlyList<Framing> framings, DiscoveryOptions o, CancellationToken cancel)
    {
      foreach (var framing in framings)
      {
        cancel.ThrowIfCancellationRequested();
        var p = resolved with { Framing = framing };

        // the DB's own CCSDS option set first — it may well be right, and 62 blind configurations are too
        // many to try before the values the DB actually supplied (§4.3).
        var found = Score(soft, p, h);
        if (found != null) return found;

        if (framing != Framing.CCSDS || o.MaxCcsdsConfigurations <= 0) continue;
        // the one expensive sub-search, and therefore the only one behind a quality floor (§4.3).
        if (o.CcsdsQualityFloorDb is double floor && soft.EyeSnrDb < floor) continue;

        int tried = 0;
        foreach (var variant in DiscoveryHypotheses.CcsdsOptionSpace(p))
        {
          cancel.ThrowIfCancellationRequested();
          if (++tried > o.MaxCcsdsConfigurations) break;
          found = Score(soft, variant, h);
          if (found != null) return found;
        }
      }
      return null;
    }

    /// <summary>Deframe under one fully-specified parameter set and, if a CRC-valid frame comes out, package
    /// the evidence §4.5 ranks on. A hypothesis qualifies on <b>one</b> CRC-valid frame.</summary>
    private static DiscoveryCandidate? Score(SoftSymbols soft, SignalParams p, DemodHypothesis h)
    {
      var deframer = DeframerFactory.Create(p);
      if (deframer == null) return null;

      List<Frame> frames;
      try { frames = deframer.Deframe(soft, p).ToList(); }
      catch { return null; }   // a malformed configuration is one hypothesis out of many, never fatal

      var valid = frames.Where(f => f.CrcValid == true).ToList();
      if (valid.Count == 0) return null;

      int fecWork = valid.Sum(f => f.CorrectedBits + f.ErasedBytes);
      return new DiscoveryCandidate(p, $"{h.Label}+{p.Framing}", h.Tier, valid.Count, soft.EyeSnrDb, fecWork, valid);
    }



    // ----------------------------------------------------------------------------------------------------
    //                                             convergence
    // ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// Pick the single answer from the hypotheses that qualified (§4.5). Discovery yields one parameter set,
    /// never a ranked list for the operator to judge: eye opening first (the better-matched modulation
    /// resolves visibly cleaner rails), then FEC work (fewer corrections means a better-matched hypothesis),
    /// then frame count, and finally closeness to the DB — so a label that was right is not gratuitously
    /// contradicted when the metrics tie.
    /// </summary>
    private static DiscoveryCandidate? Best(List<DiscoveryCandidate> qualified, SignalParams db)
      => qualified
        .OrderByDescending(c => c.EyeSnrDb)
        .ThenBy(c => c.FecWork)
        .ThenByDescending(c => c.CrcFrames)
        .ThenBy(c => DbDistance(c.Params, db))
        .FirstOrDefault();

    /// <summary>How far a candidate sits from the DB row: one point per differing field, with baud measured
    /// in octaves so a 2x error is nearer than a family change.</summary>
    private static double DbDistance(SignalParams c, SignalParams db)
      => (c.Modulation == db.Modulation ? 0 : 1)
         + (c.Framing == db.Framing ? 0 : 1)
         + (db.Baud > 0 && c.Baud > 0 ? Math.Abs(Math.Log2(c.Baud / db.Baud)) : 1);
  }
}
