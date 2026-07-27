using VE3NEA.SkyTlm.Core;
using VE3NEA.SkyTlm.Dsp;

namespace VE3NEA.SkyTlm.Discovery
{
  /// <summary>
  /// One demodulation hypothesis: a fully-formed, internally consistent <see cref="SignalParams"/> plus the
  /// label it is reported under and the tier it came from. Not a point in a cartesian product — every
  /// hypothesis is built whole, so the detector/demod geometry it implies is coherent (discover_params_plan.md
  /// §4.2). <see cref="SignalParams.Framing"/> is not part of a demod hypothesis: framing is invisible to the
  /// demodulator and is swept separately over the soft bits it produces (§4.3).
  /// </summary>
  public sealed record DemodHypothesis(SignalParams Params, string Label, int Tier);

  /// <summary>
  /// Enumerates the hypothesis set a discovery session tries — the whole of
  /// <c>discover_params_plan.md</c> §4.2 (modulation/baud/deviation) and §4.3 (framing).
  ///
  /// <b>Coverage is the only real risk to the feature</b> (§1.1): discovery works if and only if the correct
  /// parameters are among those enumerated here, so every restriction below is a decision about whether the
  /// feature works at all, not a performance knob. Widening a tier is the prescribed fix for a miss.
  /// </summary>
  public static class DiscoveryHypotheses
  {
    /// <summary>The standard telemetry baud ladder (§4.2 tier 3), tried after the measured and label-derived
    /// rates.</summary>
    private static readonly double[] StandardLadder = { 1200, 2400, 4800, 9600, 19200, 38400 };

    /// <summary>Minimum samples per symbol: a rate the sample rate cannot carry is skipped rather than
    /// enumerated and failed (§4.2 tier 3).</summary>
    private const double MinSamplesPerSymbol = 2.0;

    /// <summary>Two bauds within this relative distance are the same hypothesis (the measured rate is never
    /// exactly the nominal one).</summary>
    private const double BaudDedupTolerance = 0.02;



    // ----------------------------------------------------------------------------------------------------
    //                                        demodulation hypotheses
    // ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// The ordered demodulation hypothesis list for one burst, by descending prior probability (§4.2):
    /// tier 1 the co-channel transmitters resolved from the DB, then for each candidate baud in measured →
    /// label-derived → standard-ladder order, the modulation/deviation variants of tiers 2 and 4.
    /// Deduplicated <b>after</b> <see cref="Demodulators.IsWideFsk"/> normalization, because that rewrite
    /// collapses nominally distinct hypotheses onto one detector/demod geometry.
    /// </summary>
    /// <param name="db">The DB-resolved parameters — the starting point, and the tie-break anchor (§4.5).</param>
    /// <param name="coChannel">Tier 1: parameters of every co-channel transmitter of the same satellite,
    /// already resolved by the caller (resolution lives in the app, not here). May be empty.</param>
    /// <param name="measuredBauds">The symbol-rate lines <see cref="BaudVerifier"/> found on this burst,
    /// strongest first. Tried before the label — they are evidence, not labels — and they are the only way
    /// an off-ladder rate is ever reached.</param>
    public static List<DemodHypothesis> Demodulation(SignalParams db, IEnumerable<SignalParams>? coChannel,
      IReadOnlyList<double>? measuredBauds)
    {
      var list = new List<DemodHypothesis>();
      var seen = new List<SignalParams>();

      void Add(SignalParams p, string label, int tier)
      {
        if (p.Baud <= 0 || db.SampleRate / p.Baud < MinSamplesPerSymbol) return;
        var norm = Normalize(p);
        if (seen.Any(s => Same(s, norm))) return;
        seen.Add(norm);
        list.Add(new DemodHypothesis(norm, label, tier));
      }

      // tier 1 — the co-channel transmitters, ignoring the DB's alive/status flags (the caller applies that
      // rule). Highest prior: the right answer is often already in the DB under a transmitter the operator
      // did not select.
      if (coChannel != null)
        foreach (var p in coChannel)
          Add(p with { SampleRate = db.SampleRate, Framing = Framing.Unknown }, $"cochannel/{Describe(p)}", 1);

      foreach (double baud in CandidateBauds(db, measuredBauds))
        foreach (var (p, label) in Variants(db, baud))
          Add(p, label, 2);

      return list;
    }

    /// <summary>Candidate bauds in trial order (§4.2 tier 3): the measured rates first, then the label and
    /// its {2x, 4x, 1/2} relatives, then the standard ladder. Deduplicated within
    /// <see cref="BaudDedupTolerance"/> and limited to rates the sample rate can carry.</summary>
    public static List<double> CandidateBauds(SignalParams db, IReadOnlyList<double>? measuredBauds)
    {
      var wanted = new List<double>();
      if (measuredBauds != null) wanted.AddRange(measuredBauds.Where(b => b > 0));
      if (db.Baud > 0) wanted.AddRange(new[] { db.Baud, 2 * db.Baud, 4 * db.Baud, 0.5 * db.Baud });
      wanted.AddRange(StandardLadder);

      var result = new List<double>();
      foreach (double b in wanted)
      {
        if (b <= 0 || db.SampleRate / b < MinSamplesPerSymbol) continue;
        if (result.Any(r => Math.Abs(r - b) <= BaudDedupTolerance * Math.Max(r, b))) continue;
        result.Add(b);
      }
      return result;
    }

    /// <summary>
    /// The modulation/deviation variants at one baud (§4.2 tiers 2 and 4), ordered so the DB's own family
    /// comes first — discovery must not gratuitously contradict a label that was right (§4.5).
    ///
    /// The deviation axis is mostly implicit rather than swept: FSK with a null deviation is
    /// <see cref="SignalParams.IsBlind"/>, which makes the pipeline estimate the deviation from this burst's
    /// own spectrum (<see cref="BlindFskEstimator"/>) — strictly better than any ladder of guesses, and the
    /// reason tier 4 needs no enumeration of its own. The explicit wide-FSK points below exist only as the
    /// fallback for when blind estimation does not lock.
    /// </summary>
    private static IEnumerable<(SignalParams P, string Label)> Variants(SignalParams db, double baud)
    {
      // the DB's own family at this baud, carrying its curated deviation — the highest-prior point.
      // AFSK is the exception: it is Bell-202, defined only at 1200 Bd with the 1200/2200 Hz tone pair, so
      // carrying an AFSK label to another rate produces a hypothesis that is not internally consistent —
      // which §4.2 forbids, and which is not merely untidy. Measured: an AFSK-labelled transmitter dragged
      // to a 16816 Bd measured line decoded a spurious CRC-valid AX.25 frame, and a spurious frame is
      // auto-applied (§4.5). The generic families below still cover this baud.
      if (db.Modulation != Modulation.AFSK || IsBell202Rate(baud))
        yield return (db with { Baud = baud, Framing = Framing.Unknown }, $"db/{db.Modulation}/{baud:0}");

      // blind FSK: deviation unknown, estimated per burst. The widest detector window and the single most
      // productive hypothesis in the corpus (the live pipeline's own blind fallback works the same way).
      yield return (Bare(db, baud, Modulation.FSK, null), $"FSK-blind/{baud:0}");

      // h = 1/2 CPM — GMSK's pinned index, and what GFSK with an unstated deviation resolves to.
      yield return (Bare(db, baud, Modulation.GMSK, null), $"GMSK/{baud:0}");

      // Gaussian FSK below the wide-FSK rewrite threshold (h = 0.6): a genuinely different demod profile
      // from both GMSK's pinned h = 1/2 and the orthogonal wide-FSK matched filter.
      yield return (Bare(db, baud, Modulation.GFSK, 0.3 * baud), $"GFSK-h0.6/{baud:0}");

      // explicit unfiltered 2-FSK at h = 1 and h = 2, for when blind estimation fails to lock.
      yield return (Bare(db, baud, Modulation.FSK, 0.5 * baud), $"FSK-h1/{baud:0}");
      yield return (Bare(db, baud, Modulation.FSK, 1.0 * baud), $"FSK-h2/{baud:0}");

      // AFSK is a pinned point, not a family sweep: Bell-202 is defined at 1200 Bd with the 1200/2200 Hz
      // tone pair (half-spacing 500 Hz) on a 1700 Hz audio subcarrier.
      if (IsBell202Rate(baud))
        yield return (Bare(db, baud, Modulation.AFSK, 500) with { AfCarrier = 1700 }, "AFSK/1200");
    }

    /// <summary>The one rate an AFSK hypothesis is defined at — Bell-202's 1200 Bd.</summary>
    private static bool IsBell202Rate(double baud) => Math.Abs(baud - 1200) <= BaudDedupTolerance * 1200;

    /// <summary>A hypothesis built whole from the DB's carry-through facts (frame size, RS basis, CCSDS
    /// options) but with the modulation geometry replaced — the carry-through fields describe framing, not
    /// modulation, so they survive a modulation change.</summary>
    private static SignalParams Bare(SignalParams db, double baud, Modulation mod, double? deviation)
      => db with
      {
        Baud = baud,
        Modulation = mod,
        Deviation = deviation,
        Framing = Framing.Unknown,
        AfCarrier = null,
        Manchester = null,
        // the run-time discoveries of a previous decode must not leak into a fresh hypothesis.
        ResolvedBaud = null,
        ResolvedDeviation = null
      };

    /// <summary>Mirrors <c>StreamingPipeline</c>'s construction-time <see cref="Demodulators.IsWideFsk"/>
    /// rewrite, so the list dedups on the geometry that will actually be built rather than on the label
    /// (§4.2 — "deduplicate after normalization").</summary>
    private static SignalParams Normalize(SignalParams p)
      => Demodulators.IsWideFsk(p) ? p with { Modulation = Modulation.FSK } : p;

    private static bool Same(SignalParams a, SignalParams b)
      => a.Modulation == b.Modulation
         && Math.Abs(a.Baud - b.Baud) <= BaudDedupTolerance * Math.Max(a.Baud, b.Baud)
         && Nullable.Equals(a.Deviation, b.Deviation);

    private static string Describe(SignalParams p)
      => $"{p.Modulation}/{p.Baud:0}" + (p.Deviation is double d ? $"/{d:0}" : "");



    // ----------------------------------------------------------------------------------------------------
    //                                          framing hypotheses
    // ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// The framing candidates to try over one hypothesis's soft bits, in cost order (§4.3).
    ///
    /// <b>Framing is not searched when the DB defines it.</b> At the family level the DB's errors are
    /// <i>missing</i> framing, not <i>wrong</i> framing, so sweeping families against a defined one would
    /// spend the most expensive part of the search re-litigating the one field the DB gets right. The
    /// sub-details within a family are the opposite case and are never trusted: AX100's mode is one extra
    /// deframe, so both are always tried; the self-selecting cases (AX.25 scrambled vs plain, the AX100
    /// randomizer) are left alone because the deframers already run both chains internally.
    /// </summary>
    /// <param name="db">The DB-resolved parameters; only <see cref="SignalParams.Framing"/> is read.</param>
    /// <param name="genesisFamily">True when the satellite is in the GENESIS/HADES family. <see
    /// cref="Framing.HADES"/> is excluded from the generic sweep: it is one operator's custom framing, and
    /// its 16-bit syncword plus CRC-16 chain is a plausible source of spurious matches on unrelated signals.</param>
    public static List<Framing> Framings(SignalParams db, bool genesisFamily)
    {
      if (db.Framing != Framing.Unknown)
        return db.Framing switch
        {
          // the DB's AX100 mode is a guess presented as a fact (a bare "AX100" row resolves to ASM+Golay),
          // and the alternative costs one extra deframe — so try both regardless of what the DB stated.
          Framing.AX100ASM => new List<Framing> { Framing.AX100ASM, Framing.AX100RS },
          Framing.AX100RS => new List<Framing> { Framing.AX100RS, Framing.AX100ASM },
          _ => new List<Framing> { db.Framing }
        };

      // framing unknown: sweep, cheapest first. AX25G3RUH is FEC-free and covers plain AX.25 as well as
      // G3RUH; AX100 is Golay+RS; USP is Viterbi+RS; CCSDS is the most expensive and goes last.
      var sweep = new List<Framing>
      {
        Framing.AX25G3RUH, Framing.AX100ASM, Framing.AX100RS, Framing.USP, Framing.CCSDS
      };
      if (genesisFamily) sweep.Insert(0, Framing.HADES);
      return sweep;
    }

    /// <summary>
    /// The CCSDS option space (§4.3), enumerated <b>after</b> the DB's own option set has failed: block
    /// variant (uncoded / RS / concatenated x the four convolutional conventions) x scrambler on/off, and on
    /// the RS-bearing variants also RS basis x interleaving depth — roughly 62 configurations.
    ///
    /// This is the single expensive sub-search in the plan, which is why it is the only one gated on
    /// soft-bit quality (<see cref="DiscoveryOptions.CcsdsQualityFloorDb"/>): everything cheaper is tried
    /// unrestricted so a marginal-but-correct answer is never lost to a quality bar (§4.3).
    /// <paramref name="basis"/> supplies <see cref="SignalParams.FrameSize"/> and the modulation geometry;
    /// its own CCSDS fields are replaced.
    /// </summary>
    public static IEnumerable<SignalParams> CcsdsOptionSpace(SignalParams basis)
    {
      var conventions = new string?[] { null, "CCSDS", "NASA-DSN", "CCSDS uninverted", "NASA-DSN uninverted" };
      foreach (bool rsEnabled in new[] { true, false })
        foreach (string? conv in conventions)
        {
          // the uncoded block has no convolutional layer; the concatenated block is exactly the RS block
          // with one, so "RS disabled + convolutional" is not a CCSDS configuration.
          if (!rsEnabled && conv != null) continue;
          foreach (bool scrambler in new[] { true, false })
          {
            if (!rsEnabled)
            {
              yield return basis with
              {
                Framing = Framing.CCSDS, RsEnabled = false, Convolutional = null,
                Scrambler = scrambler, RsBasis = null, RsInterleaving = null
              };
              continue;
            }
            foreach (string rsBasis in new[] { "dual", "conventional" })
              foreach (int interleaving in new[] { 1, 2, 4 })
              {
                // FrameSize must be divisible by the interleaving depth (CcsdsOptions.FrameSize), so a
                // curated frame size that is not gets the default rather than an unbuildable deframer.
                int frameSize = basis.FrameSize ?? 223;
                yield return basis with
                {
                  Framing = Framing.CCSDS, RsEnabled = true, Convolutional = conv, Scrambler = scrambler,
                  RsBasis = rsBasis, RsInterleaving = interleaving,
                  FrameSize = frameSize % interleaving == 0 ? frameSize : 223 - 223 % interleaving
                };
              }
          }
        }
    }
  }
}
