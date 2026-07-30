using System;

namespace VE3NEA.SkyTlm.Deframing
{
  /// <summary>
  /// The ordinary reflected CRC-32 (CRC-32/ISO-HDLC): polynomial 0x04C11DB7 reflected to 0xEDB88320,
  /// initial value 0xFFFFFFFF, reflected in and out, final XOR 0xFFFFFFFF — identical to
  /// <c>zlib.crc32</c> and <c>System.IO.Hashing.Crc32</c>. Canonical check: CRC("123456789") = 0xCBF43926.
  /// <para>
  /// Used by the SSDV packet layer, which transmits it <b>big-endian</b> — the opposite of the
  /// little-endian byte order <c>Crc32.Hash</c> produces, and the easy mistake to make here.
  /// </para>
  /// </summary>
  public static class Crc32
  {
    private static readonly uint[] Table = BuildTable();

    /// <summary>CRC-32 over <paramref name="data"/>.</summary>
    public static uint Compute(ReadOnlySpan<byte> data)
    {
      uint crc = 0xFFFFFFFF;
      foreach (byte d in data) crc = (crc >> 8) ^ Table[(crc ^ d) & 0xFF];
      return ~crc;
    }

    private static uint[] BuildTable()
    {
      var table = new uint[256];
      for (uint i = 0; i < 256; i++)
      {
        uint crc = i;
        for (int b = 0; b < 8; b++) crc = (crc >> 1) ^ (0xEDB88320u & (uint)-(crc & 1));
        table[i] = crc;
      }
      return table;
    }
  }
}
