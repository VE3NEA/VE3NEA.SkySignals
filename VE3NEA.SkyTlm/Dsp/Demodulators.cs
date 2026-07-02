using VE3NEA.SkyTlm.Core;

namespace VE3NEA.SkyTlm.Dsp
{
  /// <summary>
  /// Maps a recording's <see cref="Modulation"/> to the demodulator that handles it (the demod registry).
  /// A new FSK flavor — or a PSK sibling demodulator — is added by registering an entry here, without
  /// touching the <see cref="Core.StreamingPipeline"/>.
  /// </summary>
  public static class Demodulators
  {
    /// <summary>
    /// The GMSK/GFSK demod configuration the decode path uses — <b>coherent MLSE/PSP for h = 1/2, DF-DD (N=2)
    /// otherwise</b> (the MLSE detector defers to DF-DD internally for h ≠ 1/2 and continuous streams). The
    /// single source of truth for the detector defaults: <see cref="Core.StreamingPipeline"/> seeds
    /// <c>StreamingOptions.GmskOptions</c> from it, and the eye/constellation/soft-bit views render the
    /// <i>same</i> detector that produced the frames.
    /// </summary>
    public static GmskDemodOptions DefaultGmskOptions { get; } = new()
    {
      DifferentialOrder = 2,
      // oversample high-baud bursts toward this sps before the discriminator (nearest power of two:
      // 9600→8×, 19200→16×, 4800→4×, ≤1200→1×). FSKDEMOD_TARGETSPS overrides it (0 disables) for A/B.
      UpsampleTargetSps = double.TryParse(Environment.GetEnvironmentVariable("FSKDEMOD_TARGETSPS"), out var t) ? t : 40,
      // coherent MLSE/PSP detector for h = 1/2 profiles, on by default. FSKDEMOD_MLSE=0 selects DF-DD instead.
      UseMlse = Environment.GetEnvironmentVariable("FSKDEMOD_MLSE") != "0"
    };

    /// <summary>True if FskDemod currently has a demodulator for this modulation family.</summary>
    public static bool CanDemodulate(Modulation m) => Create(m, null) != null;

    /// <summary>
    /// True when a GFSK/GMSK transmitter has a known deviation wide enough that the modulation index
    /// h = 2·dev/Rs ≥ 0.75 — it is really unfiltered 2-FSK, not a near-h=0.5 Gaussian CPM (the SatNOGS
    /// "GFSK"/"GMSK" label only fits the latter; e.g. CUTE is tagged "GFSK9k6" but transmits h=1.0 FSK at
    /// dev=4800/9600). The single home of the GFSK→FSK reclassification that used to live in the param
    /// resolver: the resolver is now shared with SkyRoof and emits the raw label, so the pipeline decides.
    /// <see cref="Core.StreamingPipeline"/> normalizes such a signal's modulation to FSK at construction
    /// (detector template + shape gate + demod), and <see cref="Create(SignalParams, GmskDemodOptions?)"/>
    /// routes it to the orthogonal wide-FSK matched filter for direct callers.
    /// </summary>
    public static bool IsWideFsk(SignalParams p) =>
      p.Modulation is Modulation.GFSK or Modulation.GMSK &&
      p.Deviation is double dev && 2.0 * dev / p.Baud >= 0.75;

    /// <summary>
    /// Build the demodulator for <paramref name="p"/>'s modulation, or <c>null</c> if unsupported
    /// (CW/SSTV/QPSK today). GMSK and GFSK share the generic <see cref="CpmFskDemodulator"/> engine; GMSK pins
    /// h=0.5 while GFSK honors the signal's real modulation index/deviation in the discriminator scale and
    /// DF-DD phase step (h = 2·<see cref="SignalParams.Deviation"/>/Rs). At h=0.5 the two paths coincide byte-for-byte.
    /// BPSK is the PSK sibling (<see cref="BpskDemodulator"/>): it honors <see cref="SignalParams.Differential"/>
    /// (<c>true</c> → differential detection, <c>false</c>/<c>null</c> → coherent Costas), with
    /// <see cref="SignalParams.Manchester"/> threaded through. When <see cref="SignalParams.Differential"/> is
    /// <c>null</c> this builds the coherent default; the <see cref="Core.StreamingPipeline"/> tries both submodes
    /// per burst until one decodes. PSK ignores the GMSK <paramref name="options"/>.
    /// </summary>
    public static IDemodulator? Create(SignalParams p, GmskDemodOptions? options) => p.Modulation switch
    {
      Modulation.BPSK => new BpskDemodulator(new BpskDemodOptions { Differential = p.Differential == true, Manchester = p.Manchester == true }),
      // wide-h GFSK/GMSK is really unfiltered 2-FSK (see IsWideFsk): route it to the orthogonal wide-FSK
      // matched filter rather than the Gaussian demod that the "GFSK"/"GMSK" label would otherwise select.
      _ when IsWideFsk(p) => new CpmFskDemodulator(ModProfile.Fsk, options),
      _ => Create(p.Modulation, options)
    };

    private static IDemodulator? Create(Modulation m, GmskDemodOptions? options) => m switch
    {
      Modulation.GMSK => new CpmFskDemodulator(ModProfile.Gmsk, options),
      Modulation.GFSK => new CpmFskDemodulator(ModProfile.Gfsk, options),
      Modulation.FSK => new CpmFskDemodulator(ModProfile.Fsk, options),   // wide-h 2-FSK, orthogonal MF
      Modulation.BPSK => new BpskDemodulator(new BpskDemodOptions { Differential = false }),  // coherent default
      _ => null   // qpsk has no demodulator yet
    };
  }
}
