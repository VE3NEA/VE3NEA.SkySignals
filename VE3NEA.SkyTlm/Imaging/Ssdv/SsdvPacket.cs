using System;
using VE3NEA.SkyTlm.Deframing;

namespace VE3NEA.SkyTlm.Imaging.Ssdv
{
  /// <summary>
  /// One validated SSDV packet: the header fields plus the entropy-coded payload, after the CRC-32 (and,
  /// where the variant carries it, RS(255,223)) have gated it. Only packets that pass come out of
  /// <see cref="TryParse"/> — off-air evidence is that a failing packet's header is garbage, not merely
  /// approximate (packet IDs of 4098 and 32778 turned up in the 2026-07-30 HADES-SA corpus), so a packet
  /// that cannot be validated must never reach the image map.
  /// </summary>
  public sealed record SsdvPacket
  {
    /// <summary>The variant this packet was parsed as.</summary>
    public required SsdvVariant Variant { get; init; }

    /// <summary>Raw 4-byte callsign field, or 0 when the variant carries none. This — not the decoded
    /// text — is what identifies the sender, because not every operator puts a callsign in it.</summary>
    public uint CallsignCode { get; init; }

    /// <summary>Base-40 decode of <see cref="CallsignCode"/>, empty when there is no callsign field or
    /// the code is out of range.</summary>
    public string Callsign { get; init; } = "";

    /// <summary>Image ID, incremented per image by the sender and reused freely — off air, HADES-SA sent
    /// IDs 99, 194, 225–238 and 253 within three hours, so this is not a monotonic counter.</summary>
    public required int ImageId { get; init; }

    /// <summary>Packet index within the image, from 0.</summary>
    public required int PacketId { get; init; }

    /// <summary>Image width in pixels (the on-air field counts 16-pixel MCU blocks).</summary>
    public required int Width { get; init; }

    /// <summary>Image height in pixels.</summary>
    public required int Height { get; init; }

    /// <summary>JPEG quality index 0–7, selecting the quantisation-table scaling.</summary>
    public required int Quality { get; init; }

    /// <summary>This is the last packet of the image.</summary>
    public required bool IsEoi { get; init; }

    /// <summary>MCU/subsampling mode, 0–3.</summary>
    public required int Subsampling { get; init; }

    /// <summary>Byte offset within <see cref="Payload"/> of the first MCU that starts inside it, or −1
    /// when no MCU starts here (on-air 0xFF) — the payload then continues an MCU begun in an earlier packet.</summary>
    public required int McuOffset { get; init; }

    /// <summary>Index of that first MCU within the image, or −1 when none (on-air 0xFFFF).</summary>
    public required int McuIndex { get; init; }

    /// <summary>Entropy-coded scan bytes, <see cref="SsdvVariant.PayloadLen"/> of them.</summary>
    public required byte[] Payload { get; init; }

    /// <summary>Bytes the RS decoder had to correct (0 = the packet arrived CRC-clean).</summary>
    public int CorrectedBytes { get; init; }

    /// <summary>
    /// Total MCUs in the image. The on-air width/height count 16×16 blocks, so a 2×2-subsampled image
    /// has exactly that many MCUs; coarser subsampling splits each block into 2 or 4 smaller MCUs.
    /// </summary>
    public int McuCount => Width / 16 * (Height / 16) * (Subsampling == 0 ? 1 : Subsampling == 3 ? 4 : 2);

    /// <summary>
    /// Parse and validate one canonical SSDV packet. The integrity order is fsphil's: check the CRC
    /// first and only fall back to RS to repair a packet that failed it, then re-check the CRC over the
    /// repaired bytes — an RS decode that "succeeds" on noise is common enough that its output has to be
    /// gated by the CRC, and running RS on packets that were already clean is wasted work. On the
    /// 2026-07-30 HADES-SA corpus that fallback recovered 38 % more packets than the CRC alone.
    /// </summary>
    /// <remarks>
    /// The erasure ladder in <see cref="RsCodeword.TryWithErasures"/> is deliberately not used: it needs
    /// per-byte soft confidence, which <see cref="Core.Frame"/> does not carry. It would be the next
    /// thing to try if plain RS ever proves to be leaving packets on the table.
    /// </remarks>
    public static bool TryParse(ReadOnlySpan<byte> packet, SsdvVariant v, out SsdvPacket? parsed)
    {
      parsed = null;
      if (packet.Length != v.PacketLen) return false;

      var bytes = packet.ToArray();
      int corrected = 0;
      bool syncTypeOk = !v.HasSyncType
        || (bytes[0] == SsdvVariant.SyncByte && bytes[1] == v.TypeByte);

      if (v.HasCrc)
      {
        if (!syncTypeOk || !CrcOk(bytes, v))
        {
          if (!v.HasRs) return false;

          // Both leading bytes are constants for a known variant, so overwrite them rather than gate
          // on them and let RS repair the rest — this is what ssdv_dec_is_packet does, and it means a
          // packet whose type byte was hit in flight is still recoverable. The CRC below is what
          // actually decides, and a false accept there is a 1-in-4-billion event.
          if (v.HasSyncType) { bytes[0] = SsdvVariant.SyncByte; bytes[1] = v.TypeByte; }

          // RS(255,223), conventional basis, over bytes[1..PacketLen] — the sync byte is outside the
          // codeword. libfec corrects in place on any claimed success, so decode a copy.
          var cw = bytes[1..];
          corrected = RsCodeword.Decode(cw, v.RsPad, dualBasis: false);
          if (corrected < 0) return false;
          cw.CopyTo(bytes, 1);
          if (!CrcOk(bytes, v)) return false;
        }
      }
      else if (v.HasRs)
      {
        if (!syncTypeOk) return false;
        var cw = bytes[1..];
        corrected = RsCodeword.Decode(cw, v.RsPad, dualBasis: false);
        if (corrected < 0) return false;
        cw.CopyTo(bytes, 1);
      }
      else if (!syncTypeOk) return false;

      int h = v.ImageIdOffset;
      byte flags = bytes[h + 5];
      int mcuOffset = bytes[h + 6];
      int mcuIndex = bytes[h + 7] << 8 | bytes[h + 8];
      uint callsign = v.HasCallsign
        ? (uint)(bytes[2] << 24 | bytes[3] << 16 | bytes[4] << 8 | bytes[5])
        : 0;

      // Sanity checks, from ssdv_dec_is_packet. A packet that passed the CRC has almost certainly not
      // been corrupted, but the transcoder trusts these fields absolutely — it seeks to McuOffset and
      // fills the gap up to McuIndex — so an implausible header must be rejected here rather than
      // become a malformed JPEG downstream.
      var candidate = new SsdvPacket
      {
        Variant = v,
        CallsignCode = callsign,
        Callsign = v.HasCallsign ? DecodeCallsign(callsign) : "",
        ImageId = bytes[h],
        PacketId = bytes[h + 1] << 8 | bytes[h + 2],
        Width = bytes[h + 3] * 16,
        Height = bytes[h + 4] * 16,
        Quality = (flags >> 3 & 7) ^ 4,
        IsEoi = (flags >> 2 & 1) != 0,
        Subsampling = flags & 3,
        McuOffset = mcuOffset == 0xFF ? -1 : mcuOffset,
        McuIndex = mcuIndex == 0xFFFF ? -1 : mcuIndex,
        Payload = bytes[v.HeaderLen..v.CrcOffset],
        CorrectedBytes = corrected
      };

      if (candidate.Width == 0 || candidate.Height == 0) return false;
      // note mcuOffset is the raw byte: 0xFF here means "index but no offset", which is contradictory
      // and is rejected, exactly as the reference rejects it.
      if (candidate.McuIndex >= 0 && (candidate.McuIndex >= candidate.McuCount || mcuOffset >= v.PayloadLen))
        return false;

      parsed = candidate;
      return true;
    }

    /// <summary>CRC-32 over bytes 1..<see cref="SsdvVariant.CrcOffset"/> against the stored value —
    /// which SSDV writes <b>big-endian</b>, unlike every .NET CRC-32 helper.</summary>
    private static bool CrcOk(byte[] bytes, SsdvVariant v)
    {
      int at = v.CrcOffset;
      uint stored = (uint)(bytes[at] << 24 | bytes[at + 1] << 16 | bytes[at + 2] << 8 | bytes[at + 3]);
      return Crc32.Compute(bytes.AsSpan(1, at - 1)) == stored;
    }

    /// <summary>
    /// Base-40 callsign decode, matching fsphil's <c>decode_callsign</c> byte for byte: the <b>least</b>
    /// significant digit is the <b>first</b> character, and digit values are 0 and 11–13 = '-',
    /// 1–10 = '0'–'9', 14–39 = 'A'–'Z'. Codes above 40^6 − 1 are not callsigns and decode to nothing.
    /// </summary>
    /// <remarks>
    /// The digit order is the one thing here with no off-air anchor — neither live variant puts a real
    /// callsign in the field (HADES-SA overloads it with constants, and the other variants have no
    /// callsign at all). It is pinned instead against the reference encoder, which produced
    /// <c>0x584C99F3</c> for "VE3NEA"; see <c>SsdvPacketTests.ReferenceCallsignEncoding</c>.
    /// </remarks>
    public static string DecodeCallsign(uint code)
    {
      if (code == 0 || code > 0xF423FFFF) return "";

      var chars = new char[7];
      int c = 0;
      while (code > 0)
      {
        uint s = code % 40;
        chars[c++] = s < 1 || s >= 11 && s < 14 ? '-' : s < 11 ? (char)('0' + s - 1) : (char)('A' + s - 14);
        code /= 40;
      }
      return new string(chars, 0, c);
    }
  }
}
