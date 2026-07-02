using System;

namespace VE3NEA.SkyTlm.Deframing
{
  /// <summary>
  /// CRC-16/X-25 — the AX.25 / HDLC frame-check sequence and the USP CRC: reflected
  /// CRC-CCITT, polynomial 0x1021 (reflected 0x8408), initial value 0xFFFF, input/output reflected,
  /// final XOR 0xFFFF. Byte-for-byte identical to direwolf's <c>fcs_calc</c> (its 0x8408 table). The
  /// canonical check value for "123456789" is 0x906E.
  /// </summary>
  public static class Crc16Ccitt
  {
    private static readonly ushort[] Table = BuildTable();

    private static ushort[] BuildTable()
    {
      var t = new ushort[256];
      for (int i = 0; i < 256; i++)
      {
        ushort crc = (ushort)i;
        for (int b = 0; b < 8; b++)
          crc = (ushort)((crc & 1) != 0 ? (crc >> 1) ^ 0x8408 : crc >> 1);
        t[i] = crc;
      }
      return t;
    }

    /// <summary>FCS over <paramref name="data"/>: the value transmitted (low byte first) after the frame.</summary>
    public static ushort Compute(ReadOnlySpan<byte> data)
    {
      ushort crc = 0xFFFF;
      foreach (byte d in data)
        crc = (ushort)((crc >> 8) ^ Table[(crc ^ d) & 0xff]);
      return (ushort)(crc ^ 0xFFFF);
    }
  }
}
