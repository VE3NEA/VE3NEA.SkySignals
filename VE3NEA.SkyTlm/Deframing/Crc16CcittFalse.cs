using System;

namespace VE3NEA.SkyTlm.Deframing
{
  /// <summary>
  /// CRC-16/CCITT-FALSE (a.k.a. CRC-16/IBM-3740): polynomial 0x1021, initial value 0xFFFF,
  /// <b>no</b> input/output reflection, <b>no</b> final XOR. This is the HADES/GENESIS packet checksum —
  /// distinct from <see cref="Crc16Ccitt"/>, which is the reflected X-25 FCS (poly 0x8408, xorout 0xFFFF).
  /// The on-air CRC is big-endian (MSB-first), consistent with HADES being MSB-first throughout.
  /// Canonical check: CRC("123456789") = 0x29B1; HADES spec golden vector: CRC("EASAT-2") = 0x7D58.
  /// </summary>
  public static class Crc16CcittFalse
  {
    /// <summary>CRC-16/CCITT-FALSE over <paramref name="data"/> (the value transmitted MSB-first after it).</summary>
    public static ushort Compute(ReadOnlySpan<byte> data)
    {
      ushort crc = 0xFFFF;
      foreach (byte d in data)
      {
        crc ^= (ushort)(d << 8);
        for (int b = 0; b < 8; b++)
          crc = (ushort)((crc & 0x8000) != 0 ? (crc << 1) ^ 0x1021 : crc << 1);
      }
      return crc;
    }
  }
}
