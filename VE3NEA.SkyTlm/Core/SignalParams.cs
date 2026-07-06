using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace VE3NEA.SkyTlm.Core
{
  /// <summary>Deframing flavor present in the test corpus.</summary>
  public enum Framing
  {
    Unknown,
    USP,
    AX25G3RUH,
    HADES,
    AX100ASM,   // GOMspace AX100 "ASM+Golay" (mode 5)
    AX100RS,    // GOMspace AX100 "Reed Solomon" (GOMX-1 style)
    CCSDS       // CCSDS TM (uncoded / Reed-Solomon / concatenated; the three blocks differ only by options)
  }

  /// <summary>Modulation family, classified from the SatNOGS mode/description.</summary>
  public enum Modulation
  {
    Unknown,
    GMSK,
    GFSK,
    FSK,
    AFSK,
    BPSK,
    QPSK,
    CW,
    SSTV,
    Other
  }

  /// <summary>
  /// Per-recording demod parameters. Most are looked up (baud, framing) from
  /// the SatNOGS DB. Stores only source-of-truth values; presentational
  /// and derived quantities (samples-per-symbol, modulation index, mode text) are
  /// computed by the consumers that need them.
  /// </summary>
  public record SignalParams(
    double Baud,

    [property: JsonConverter(typeof(StringEnumConverter))]
    Modulation Modulation,

    [property: JsonConverter(typeof(StringEnumConverter))]
    Framing Framing,

    [property: JsonIgnore]
    double SampleRate,
    /// <summary>
    /// Peak deviation in Hz, looked up from satyaml metadata when available, else <c>null</c>.
    /// GMSK (h=0.5) leaves this <c>null</c>; the CPM demodulators fall back to <see cref="Baud"/>/4.
    /// For FSK/GFSK it is not baud/4, so the explicit value is carried here when known.
    /// </summary>
    double? Deviation = null)
  {
    /// <summary>
    /// Manchester (bi-phase-L) line coding present on top of the modulation — set for the
    /// <c>DBPSK Manchester</c> transmitters (AMSAT/FUNcube telemetry; <see cref="ParamResolver"/> reads it from
    /// the SatNOGS modulation string). When true the demodulator runs the symbol loop at the
    /// channel <b>chip</b> rate (= <see cref="Baud"/>) and combines chip pairs into half-rate data soft symbols.
    /// Irrelevant to the FSK/GMSK families, which never set it.
    /// </summary>
    public bool? Manchester { get; init; }

    /// <summary>
    /// For the linear-PSK families (<see cref="Modulation.BPSK"/>/<see cref="Modulation.QPSK"/>): whether the
    /// link is <b>differentially encoded</b> (<c>true</c> = differential detection, <c>false</c> = coherent), or
    /// <c>null</c> when not yet known. SatNOGS parsing leaves it <c>null</c> — the DB's BPSK/DBPSK labels proved
    /// unreliable (e.g. "BPSK" Eaglet-1 is differential, "BPSK" TEVEL2-1 is coherent). When <c>null</c> the
    /// decoder tries <b>both</b> submodes per burst and, on the first CRC-valid frame, sets this to the mode that
    /// worked and stops trying both. <b>Mutable</b> so the decoder can record the discovered value (and the
    /// caller can read it back / cache it). Irrelevant to the FSK/GMSK families.
    /// </summary>
    public bool? Differential { get; set; }

    /// <summary>
    /// The peak deviation the streaming decoder actually used once it resolved a blind/learned FSK burst
    /// (locked from the first CRC-valid frame) — the run-time counterpart to the curated <see cref="Deviation"/>.
    /// <c>null</c> on the curated path (where <see cref="Deviation"/> is already the actual value) and until the
    /// blind estimator locks. <b>Mutable</b> so the decoder can write the discovered value back into the caller's
    /// params object for display.
    /// </summary>
    public double? ResolvedDeviation { get; set; }

    /// <summary>
    /// AFSK only: audio subcarrier centre frequency in Hz (Bell-202 = 1700, midway between the 1200 Hz mark and
    /// 2200 Hz space tones), from the satyaml <c>af_carrier</c> field. The AFSK demodulator FM-discriminates the
    /// RF to recover this audio, then mixes it down by this frequency so the two tones straddle DC at ±<see
    /// cref="Deviation"/> and the shared FSK engine can demodulate them. <c>null</c> ⇒ the Bell-202 default.
    /// Irrelevant to every non-AFSK family.
    /// </summary>
    public double? AfCarrier { get; init; }

    /// <summary>
    /// Reed-Solomon field basis for CCSDS/RS framing, from the satyaml <c>RS basis</c> field
    /// (<c>"conventional"</c> or <c>"dual"</c>); <c>null</c> when unknown or not RS-framed. Informational —
    /// carried through so an RS deframer can pick the correct Galois-field representation.
    /// </summary>
    public string? RsBasis { get; init; }

    /// <summary>
    /// RS/CCSDS frame length in bytes, from the satyaml <c>frame size</c> field (e.g. 223);
    /// <c>null</c> when unknown. Informational — carried through for RS/CCSDS deframer configuration.
    /// </summary>
    public int? FrameSize { get; init; }

    /// <summary>
    /// CCSDS concatenated convolutional convention from the satyaml <c>convolutional</c> field —
    /// one of <c>"CCSDS"</c>, <c>"NASA-DSN"</c>, <c>"CCSDS uninverted"</c>, <c>"NASA-DSN uninverted"</c>; <c>null</c>
    /// for the RS/uncoded blocks (no Viterbi). Pinned by the framing block: present (default <c>"CCSDS"</c>) only
    /// for the Concatenated deframer. Carried through to <see cref="Deframing.CcsdsOptions"/>.
    /// </summary>
    public string? Convolutional { get; init; }

    /// <summary>
    /// CCSDS Reed-Solomon interleaving depth I, from the satyaml <c>RS interleaving</c> field
    /// (I interleaved RS codewords, +32·I parity); <c>null</c> when unknown (defaults to 1).
    /// </summary>
    public int? RsInterleaving { get; init; }

    /// <summary>
    /// CCSDS additive scrambler on/off, from the satyaml <c>scrambler</c> field
    /// (<c>"CCSDS"</c> ⇒ true, <c>"none"</c> ⇒ false); <c>null</c> when unknown (defaults to true).
    /// </summary>
    public bool? Scrambler { get; init; }

    /// <summary>
    /// CCSDS Reed-Solomon layer on/off — pinned by the framing block (<c>false</c> only for <c>CCSDS Uncoded</c>,
    /// <c>true</c> for Reed-Solomon and Concatenated); <c>null</c> when unknown (defaults to true).
    /// </summary>
    public bool? RsEnabled { get; init; }

    /// <summary>
    /// True when the deviation is unknown and must be estimated from the burst spectrum.
    /// Blind ⇔ Modulation == FSK AND <see cref="Deviation"/> == null.
    /// GMSK/GFSK with null deviation are NOT blind — h ≈ 0.5 ⇒ baud/4 is the correct
    /// deviation for them; GFSK with large h always has an explicit Deviation in the DB.
    /// </summary>
    [property: JsonIgnore]
    public bool IsBlind => Modulation == Modulation.FSK && Deviation == null;
  }
}
