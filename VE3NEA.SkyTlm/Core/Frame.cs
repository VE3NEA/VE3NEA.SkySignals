using System;
using System.Linq;

namespace VE3NEA.SkyTlm.Core
{
  /// <summary>
  /// One decoded link-layer frame: the frame bytes (excluding the 2-byte FCS), whether the
  /// CRC checked out, and the acquisition metadata a UI/report needs (time, CFO, SNR, source burst). The
  /// deframer fills the content fields; the pipeline stamps the per-burst metadata via <c>with</c>.
  /// </summary>
  public sealed record Frame
  {
    /// <summary>Frame contents between the flags, FCS removed (Address…Info for AX.25, header+payload for USP).</summary>
    public required byte[] Bytes { get; init; }

    /// <summary>True when the recomputed FCS matched the transmitted one, false when it did not, and
    /// <c>null</c> when this framing carries no integrity check for the frame (e.g. HADES SSDV/CODEC2/PN9,
    /// uncoded CCSDS) — <c>null</c> means "not applicable", not a decode error.</summary>
    public required bool? CrcValid { get; init; }

    /// <summary>Which deframer produced this.</summary>
    public Framing Framing { get; init; }

    /// <summary>The 16-bit FCS (recomputed over <see cref="Bytes"/>); equals the transmitted FCS when <see cref="CrcValid"/>.</summary>
    public ushort Fcs { get; init; }

    /// <summary>Symbols corrected by FEC / soft correction to make the integrity check pass (0 = decoded
    /// clean). The unit is per-framing: RS <b>byte</b> corrections for USP and AX100 (including erased
    /// bytes, see <see cref="ErasedBytes"/>), Chase <b>bit</b> flips for AX.25 G3RUH and HADES.</summary>
    public int CorrectedBits { get; init; }

    /// <summary>AX100 only: how many of the weakest bytes were declared as RS erasures to recover the frame
    /// (a subset of the <see cref="CorrectedBits"/> count); 0 = plain errors-only decode.</summary>
    public int ErasedBytes { get; init; }

    /// <summary>Optional decode annotation (e.g. "scrambler:none fallback" when the AX100 ASM deframer
    /// recovered the frame without CCSDS derandomization).</summary>
    public string? Note { get; init; }

    /// <summary>
    /// Symbol index in the demodulated soft-symbol stream where this frame's sync word was found, or −1 when
    /// the deframer doesn't report it. Continuous demod uses it to map the frame to a stream time and to
    /// promote a frame decoded outside any detected burst into a new burst.
    /// </summary>
    public int SoftBitOffset { get; init; } = -1;

    /// <summary>
    /// Symbol index in the demodulated soft-symbol stream one past this frame's last on-air bit (the first
    /// symbol that no longer belongs to the frame), or −1 when the deframer doesn't report it. With
    /// SoftBitOffset it gives the frame's absolute on-air span, which the streaming pipeline uses to drop a
    /// frame re-decoded across an overlapping window (interval-overlap dedup), so the same frame seen in two
    /// adjacent decode windows is emitted once even when their independent CFO/timing flipped a few bits.
    /// </summary>
    public int SoftBitEnd { get; init; } = -1;

    // --- acquisition metadata (stamped by the pipeline) ---
    public int BurstIndex { get; init; }
    public double TimeSeconds { get; init; }
    public double CfoHz { get; init; }
    public double SnrDb { get; init; }

    public int Length => Bytes.Length;

    /// <summary>Uppercase hex of the frame bytes.</summary>
    public string Hex => Convert.ToHexString(Bytes);

    /// <summary>ASCII view of the frame bytes; non-printable bytes shown as '.'.</summary>
    public string Ascii => new string(Bytes.Select(b => b >= 0x20 && b < 0x7f ? (char)b : '.').ToArray());
  }

  /// <summary>
  /// A deframer: turns a soft-symbol stream into zero or more <see cref="Frame"/>s. One per
  /// framing flavor (AX.25 G3RUH, USP). Decoupled from the demodulator — it consumes only the
  /// soft symbols and the resolved params, so any modulation that yields soft bits can be deframed.
  /// </summary>
  public interface IDeframer
  {
    IEnumerable<Frame> Deframe(SoftSymbols syms, SignalParams p);

    /// <summary>Worst-case on-air length of one frame in bits (= symbols for the binary modulations here),
    /// first sync bit through the last coded bit. The streaming pipeline extends a burst's soft-bit decode
    /// window this far past the detected burst end, so a frame that begins inside the burst is decoded to
    /// completion even when its tail outlives detection; parameter estimation stays strictly in-burst.</summary>
    int MaxFrameBits { get; }
  }
}
