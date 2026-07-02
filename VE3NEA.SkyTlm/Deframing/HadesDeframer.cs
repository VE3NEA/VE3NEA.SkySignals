using System;
using System.Collections.Generic;
using System.Linq;
using VE3NEA.SkyTlm.Core;

namespace VE3NEA.SkyTlm.Deframing
{
  /// <summary>Tunables for <see cref="HadesDeframer"/>.</summary>
  public sealed class HadesOptions
  {
    /// <summary>Max syncword bit errors to accept (default 0 for the 16-bit 0xBF35 sync).</summary>
    public int SyncThreshold { get; init; } = 0;

    /// <summary>Bytes captured after each syncword before type-cropping. Large enough to hold the on-air SIZE
    /// byte plus the longest packet — the type-10 SSDV frame (1 + 251) — so the special types fit too.</summary>
    public int PacketLenBytes { get; init; } = 256;

    /// <summary>Which GENESIS-family bird selects the packet-type→length table. Only HADES-SA is live.</summary>
    public string Satellite { get; init; } = "HADES-SA";
  }

  /// <summary>
  /// HADES / GENESIS custom-frame deframer — a C# port of the gr-satellites
  /// <c>hades_deframer</c> chain for the AMSAT-EA SpinnyONE family. Consumes the demodulator's soft symbols
  /// and runs <b>sync (0xBF35) → fixed-length capture (<see cref="SyncToPacket"/>) → crop-by-type →
  /// CRC-16/CCITT-FALSE → GENESIS multiplicative descramble</b>. FEC-free (no RS/convolutional — unlike
  /// USP). The HADES-SA packet-type→length table and the descrambler quirks are from
  /// the AMSAT-EA "HADES-SA SpinnyONE — Transmissions description" spec. The telemetry
  /// types (1–5, 8, 9, 12, 14, 15) crop+CRC+descramble; the special types SSDV(10)/CODEC2(11)/PN9(13)
  /// crop+descramble only (no HADES CRC, no FEC) the bytes to emit over KISS — see
  /// <see cref="TryDeframeSpecial"/>. Decoded bytes only: no JPEG/voice/PN9 conversion.
  /// </summary>
  public sealed class HadesDeframer : IDeframer
  {
    // 0xBF35 = 1011111100110101, 16-bit MSB-first.
    private const ulong SyncWord = 0xBF35;
    private const int SyncLen = 16;

    // HADES-SA packet-type → cropped length in bytes (the spec's "size" field: type/address byte through
    // end of CRC). Types 6/7 are unused on HADES-SA; 10/11/13 are special-framed (see HadesSaSpecialLengths).
    private static readonly IReadOnlyDictionary<int, int> HadesSaLengths = new Dictionary<int, int>
    {
      [1] = 31, [2] = 17, [3] = 41, [4] = 35, [5] = 27,
      [8] = 31, [9] = 123, [12] = 64, [14] = 38, [15] = 73,
    };

    // the three special packet types, cropped to the length the reference modem emits over KISS.
    //   10 SSDV   → 251 B (type/address … 205-byte image data … 32-bit CRC … 256-bit RS), RS passed through
    //   11 CODEC2 → 37 B  (type/address, frame number, 35-byte = 280-bit raw codec2 payload), one per sub-frame
    //   13 PN9    → 249 B (type/address … 248-byte PN9 link-quality pattern)
    // none carry the HADES CRC-16, so unlike telemetry they are crop+descramble only (no CRC strip).
    // SSDV/CODEC2/PN9 are otherwise scrambled like telemetry (the GENESIS multiplicative
    // scrambler, first byte = type/address exempt). Decoded bytes only — no JPEG/voice/PN9 conversion.
    private static readonly IReadOnlyDictionary<int, int> HadesSaSpecialLengths = new Dictionary<int, int>
    {
      [10] = 251, [11] = 37, [13] = 249,
    };

    private readonly HadesOptions opt;
    private readonly IReadOnlyDictionary<int, int> lengths;

    public HadesDeframer(HadesOptions? options = null)
    {
      opt = options ?? new HadesOptions();
      lengths = opt.Satellite switch
      {
        "HADES-SA" => HadesSaLengths,
        _ => throw new ArgumentException($"unknown HADES satellite '{opt.Satellite}'", nameof(options)),
      };
    }

    /// <summary>Sync + the full fixed-length capture (size byte through the longest packet).</summary>
    public int MaxFrameBits => SyncLen + opt.PacketLenBytes * 8;

    public IEnumerable<Frame> Deframe(SoftSymbols syms, SignalParams p)
    {
      foreach (var (raw, syncBit, dataBit) in SyncToPacket.Extract(syms.Soft, SyncWord, SyncLen, opt.PacketLenBytes, opt.SyncThreshold))
        if (TryDeframe(raw, syms.Soft, dataBit) is { } frame)
        {
          // on-air cropped span = sync (SyncLen) + the unscrambled SIZE byte + `len` cropped bytes
          // (type/address … CRC). Telemetry strips the 2-byte CRC from Bytes, so len = Bytes.Length + 2;
          // the special types (CrcValid == null) keep the whole capture, so len = Bytes.Length.
          int len = frame.CrcValid == true ? frame.Length + 2 : frame.Length;
          yield return frame with { SoftBitOffset = syncBit, SoftBitEnd = syncBit + SyncLen + 8 + len * 8 };
        }
    }

    /// <summary>
    /// Crop one captured packet to its type's length, verify CRC over the on-air (scrambled) bytes, then —
    /// only on a CRC pass — descramble the payload and emit the plaintext frame. Returns null on unknown
    /// type or CRC failure. The order is crop → crc → descramble, with the CRC checked while the
    /// payload is still scrambled and the CRC itself transmitted unscrambled. When <paramref name="soft"/>
    /// is given (<paramref name="dataBit"/> = soft index of <paramref name="raw"/>[0]), a CRC failure is
    /// retried with Chase correction over the weakest captured bits before giving up.
    /// </summary>
    internal Frame? TryDeframe(byte[] raw, float[]? soft = null, int dataBit = 0)
    {
      // after the 0xBF35 sync the first (unscrambled) byte is the SIZE field; the type/address byte follows
      // it (spec: "sync → size → type → address"). So raw[0] = size, raw[1] = type/address, and the cropped
      // packet (type/address … CRC) starts at raw[1] and runs `size` bytes.
      if (raw.Length < 2) return null;
      int type = raw[1] >> 4;

      // SSDV(10) / CODEC2(11) / PN9(13): crop + descramble only, no HADES CRC (handled separately).
      if (HadesSaSpecialLengths.TryGetValue(type, out int slen))
        return TryDeframeSpecial(raw, slen);

      if (!lengths.TryGetValue(type, out int len)) return null;   // unknown / unused type
      if (len < 3 || 1 + len > raw.Length) return null;            // need type + ≥0 data + 2-byte CRC

      // cropped packet = type/address byte … 2-byte CRC (CRC is MSB-first, big-endian on air).
      var packet = raw[1..(1 + len)];
      int corrected = 0;
      if (!FcsOk(packet))
      {
        // chase correction (telemetry types only — the special types carry no HADES CRC to gate on): flip
        // combinations of up to 2 of the lowest-|soft| captured bits and re-check. The CRC covers the
        // on-air (scrambled) bytes and there is no bit stuffing, so flips apply directly to the captured
        // packet before descrambling. packet[0] sits 8 bits past dataBit (the unscrambled SIZE byte).
        if (soft == null || !Chase(packet, soft, dataBit + 8, out corrected)) return null;
      }
      ushort rx = (ushort)((packet[len - 2] << 8) | packet[len - 1]);

      // frame = type/address byte + data, CRC stripped. Descramble in place (first byte stays plaintext).
      var payload = packet[..(len - 2)];
      Descramble(payload);

      return new Frame
      {
        Bytes = payload,
        CrcValid = true,
        Fcs = rx,
        Framing = Framing.HADES,
        CorrectedBits = corrected,
      };
    }

    /// <summary>CRC-16/CCITT-FALSE over the packet body against its trailing big-endian FCS.</summary>
    private static bool FcsOk(byte[] packet)
    {
      int n = packet.Length;
      ushort rx = (ushort)((packet[n - 2] << 8) | packet[n - 1]);
      return Crc16CcittFalse.Compute(packet.AsSpan(0, n - 2)) == rx;
    }

    /// <summary>
    /// CRC-assisted soft correction (the <see cref="Ax25G3ruhDeframer"/> Chase pattern): try flipping
    /// combinations of up to 2 of the 16 lowest-confidence bits of the captured packet (bit <i>b</i> of the
    /// packet lives at <c>soft[packetBit + b]</c>, MSB-first) until the FCS passes. Mutates
    /// <paramref name="packet"/> in place on success. ≤136 CRC re-checks, so the cost is negligible.
    /// </summary>
    private static bool Chase(byte[] packet, float[] soft, int packetBit, out int corrected)
    {
      const int flipBits = 2, candidates = 16;
      corrected = 0;
      int nbits = packet.Length * 8;
      if (packetBit + nbits > soft.Length) return false;

      int[] cand = Enumerable.Range(0, nbits)
        .OrderBy(b => Math.Abs(soft[packetBit + b])).Take(candidates).ToArray();

      for (int k = 1; k <= flipBits; k++)
      {
        foreach (int[] combo in Combinations(cand, k))
        {
          foreach (int bit in combo) FlipBit(packet, bit);
          if (FcsOk(packet)) { corrected = k; return true; }
          foreach (int bit in combo) FlipBit(packet, bit); // revert
        }
      }
      return false;
    }

    /// <summary>Flip one packet bit, MSB-first (HADES bytes are MSB-first on air, unlike AX.25).</summary>
    private static void FlipBit(byte[] packet, int bit) => packet[bit >> 3] ^= (byte)(1 << (7 - (bit & 7)));

    /// <summary>All k-subsets of <paramref name="items"/> (k small; used only for Chase).</summary>
    private static IEnumerable<int[]> Combinations(int[] items, int k)
    {
      var idx = new int[k];
      for (int i = 0; i < k; i++) idx[i] = i;
      int nn = items.Length;
      if (k > nn) yield break;
      while (true)
      {
        var combo = new int[k];
        for (int i = 0; i < k; i++) combo[i] = items[idx[i]];
        yield return combo;

        int p = k - 1;
        while (p >= 0 && idx[p] == nn - k + p) p--;
        if (p < 0) yield break;
        idx[p]++;
        for (int i = p + 1; i < k; i++) idx[i] = idx[i - 1] + 1;
      }
    }

    /// <summary>
    /// Decode one SSDV(10) / CODEC2(11) / PN9(13) packet: just crop to the fixed per-type length 
    /// (type/address byte first). Unlike telemetry,
    /// these types are <b>not</b> GENESIS-descrambled and carry no HADES CRC-16. SSDV is standard
    /// SSDV with its own inner CRC-32 + Reed–Solomon (passed through uncorrected so reproducing the KISS bytes 
    /// needs no FEC), CODEC2 is raw 280-bit codec2 frames, PN9 is the link-
    /// quality pseudorandom pattern. <see cref="Frame.CrcValid"/> is left null (no HADES CRC to check); the
    /// recovered bytes are the deliverable — no JPEG/voice/PN9 conversion. Spurious 0xBF35 hits are rejected by
    /// requiring the exact type/address byte (source address 3) plus a full-length capture; returns null when
    /// the byte mismatches or the capture is too short to hold the whole packet.
    /// </summary>
    internal Frame? TryDeframeSpecial(byte[] raw, int len)
    {
      if (raw[1] != ((raw[1] >> 4) << 4 | 3)) return null;         // address nibble must be 3 (HADES-SA)
      if (1 + len > raw.Length) return null;                       // need the whole packet in this capture

      return new Frame
      {
        Bytes = raw[1..(1 + len)],                                 // type/address … end of packet, verbatim
        CrcValid = null,                                           // no HADES CRC on these types — not an error
        Framing = Framing.HADES,
      };
    }

    /// <summary>
    /// AMSAT-EA GENESIS multiplicative descrambler, G(x) = x¹⁷ + x¹² + 1 (taps at register bits 16 &amp; 11).
    /// A bug-for-bug port of the AMSAT-EA implementation (URESAT-1-decoder <c>genesis_scrambler.c</c>):
    /// the register state inits to 0x2C350000 but only
    /// its low 17 bits matter, so this is <c>1&lt;&lt;16</c>; the <b>first byte is not scrambled</b>; and within
    /// every subsequent byte the <b>LSB is passed through unscrambled and does not advance the register</b>
    /// (only bits 7..1 are processed). A clean textbook LFSR fails the CRC — these quirks are mandatory.
    /// </summary>
    internal static void Descramble(byte[] packet)
    {
      int state = 1 << 16;                              // 0x2C350000 masked to 17 bits
      for (int j = 1; j < packet.Length; j++)
      {
        int outb = 0;
        for (int k = 0; k < 7; k++)                     // bits 7..1; the LSB is handled separately below
        {
          int b = (packet[j] >> (7 - k)) & 1;
          int b0 = b;                                   // multiplicative descrambler feeds the input bit back
          b ^= ((state >> 16) ^ (state >> 11)) & 1;
          outb = (outb << 1) | b;
          state = ((state << 1) | b0) & 0x1ffff;
        }
        outb = (outb << 1) | (packet[j] & 1);           // LSB passthrough; register intentionally not advanced
        packet[j] = (byte)outb;
      }
    }
  }
}
