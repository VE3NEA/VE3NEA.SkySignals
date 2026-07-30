using System;

namespace VE3NEA.SkyTlm.Deframing
{
  /// <summary>
  /// The CRC-16 built into the TI CC11xx radios (catalogued as CRC-16/CMS): polynomial 0x8005, initial value
  /// 0xFFFF, <b>no</b> input/output reflection, <b>no</b> final XOR. Used by the GEOSCAN framing, where it is
  /// transmitted big-endian after the frame body — distinct from both <see cref="Crc16CcittFalse"/> (poly
  /// 0x1021, the HADES checksum) and <see cref="Crc16Ccitt"/> (the reflected X-25 FCS).
  /// Canonical check: CRC("123456789") = 0xAEE7.
  /// </summary>
  public static class Crc16Cc11xx
  {
    /// <summary>CRC-16/CC11xx over <paramref name="data"/> (the value transmitted MSB-first after it).</summary>
    public static ushort Compute(ReadOnlySpan<byte> data)
    {
      ushort crc = 0xFFFF;
      foreach (byte d in data)
      {
        crc ^= (ushort)(d << 8);
        for (int b = 0; b < 8; b++)
          crc = (ushort)((crc & 0x8000) != 0 ? (crc << 1) ^ 0x8005 : crc << 1);
      }
      return crc;
    }
  }
}
