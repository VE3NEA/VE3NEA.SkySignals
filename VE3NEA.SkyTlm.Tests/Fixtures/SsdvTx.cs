using System;
using VE3NEA.SkyTlm.Deframing;
using VE3NEA.SkyTlm.Imaging.Ssdv;

namespace VE3NEA.SkyTlm.Tests.Fixtures
{
  /// <summary>
  /// Reference SSDV packet encoder — the inverse of <see cref="SsdvPacket.TryParse"/>, written from the
  /// format description rather than from the parser, so a round trip exercises both directions of the
  /// header layout, the big-endian CRC-32 and the RS parity. Builds any
  /// <see cref="SsdvVariant"/>; the payload is opaque here (entropy-coded scan data is the transcoder's
  /// problem, not the packet layer's).
  /// </summary>
  public static class SsdvTx
  {
    /// <summary>Pseudo-random payload of exactly the variant's payload length.</summary>
    public static byte[] Payload(SsdvVariant v, int seed = 1)
    {
      var p = new byte[v.PayloadLen];
      new Random(seed).NextBytes(p);
      return p;
    }

    /// <summary>
    /// One canonical SSDV packet. <paramref name="mcuOffset"/> and <paramref name="mcuIndex"/> take −1
    /// for "no MCU starts in this packet" (on air 0xFF / 0xFFFF).
    /// </summary>
    public static byte[] Build(SsdvVariant v, byte[] payload, int imageId = 0, int packetId = 0,
                               string callsign = "VE3NEA", int widthMcu = 20, int heightMcu = 15,
                               int quality = 7, bool eoi = false, int subsampling = 0,
                               int mcuOffset = 0, int mcuIndex = 0)
    {
      if (payload.Length != v.PayloadLen)
        throw new ArgumentException($"payload must be {v.PayloadLen} bytes for this variant", nameof(payload));

      var pkt = new byte[v.PacketLen];
      if (v.HasSyncType)
      {
        pkt[0] = SsdvVariant.SyncByte;
        pkt[1] = v.TypeByte;
      }
      if (v.HasCallsign) WriteBe32(pkt, 2, EncodeCallsign(callsign));

      int h = v.ImageIdOffset;
      pkt[h] = (byte)imageId;
      pkt[h + 1] = (byte)(packetId >> 8);
      pkt[h + 2] = (byte)packetId;
      pkt[h + 3] = (byte)widthMcu;
      pkt[h + 4] = (byte)heightMcu;
      pkt[h + 5] = (byte)(((quality ^ 4) & 7) << 3 | (eoi ? 4 : 0) | subsampling & 3);
      pkt[h + 6] = (byte)(mcuOffset < 0 ? 0xFF : mcuOffset);
      pkt[h + 7] = (byte)(mcuIndex < 0 ? 0xFF : mcuIndex >> 8);
      pkt[h + 8] = (byte)(mcuIndex < 0 ? 0xFF : mcuIndex);
      payload.CopyTo(pkt, v.HeaderLen);

      // CRC-32 over bytes 1..CrcOffset, stored big-endian; then RS(255,223) over everything before it.
      if (v.HasCrc) WriteBe32(pkt, v.CrcOffset, Crc32.Compute(pkt.AsSpan(1, v.CrcOffset - 1)));
      if (v.HasRs)
      {
        int dataEnd = v.CrcOffset + (v.HasCrc ? 4 : 0);
        Rs255.Parity(pkt.AsSpan(1, dataEnd - 1)).CopyTo(pkt, dataEnd);
      }
      return pkt;
    }

    /// <summary>
    /// Base-40 callsign encode — the exact inverse of <see cref="SsdvPacket.DecodeCallsign"/>: up to 6
    /// characters, the first the most significant digit, digits as 1–10 and letters as 14–39. Characters
    /// outside those two sets contribute nothing, which is why a decoded callsign is not always the one
    /// that went in.
    /// </summary>
    public static uint EncodeCallsign(string callsign)
    {
      uint code = 0;
      int n = Math.Min(callsign.Length, 6);
      for (int i = 0; i < n; i++)
      {
        char c = char.ToUpperInvariant(callsign[i]);
        code *= 40;
        if (c >= 'A' && c <= 'Z') code += (uint)(c - 'A' + 14);
        else if (c >= '0' && c <= '9') code += (uint)(c - '0' + 1);
      }
      return code;
    }

    private static void WriteBe32(byte[] buf, int at, uint value)
    {
      buf[at] = (byte)(value >> 24);
      buf[at + 1] = (byte)(value >> 16);
      buf[at + 2] = (byte)(value >> 8);
      buf[at + 3] = (byte)value;
    }
  }
}
