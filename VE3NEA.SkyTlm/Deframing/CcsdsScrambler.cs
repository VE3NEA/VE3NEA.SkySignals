using System;

namespace VE3NEA.SkyTlm.Deframing
{
  /// <summary>
  /// CCSDS additive (synchronous) descrambler as used by the USP chain — a bit-for-bit reimplementation of
  /// GNU Radio's <c>digital.additive_scrambler_bb(mask=0xA9, seed=0xFF, len=7)</c>. The LFSR is reseeded at
  /// the start of each frame and its output is XORed with
  /// the data bit stream (MSB-first, transmission order). XOR is its own inverse, so the same routine
  /// scrambles and descrambles.
  /// </summary>
  public static class CcsdsScrambler
  {
    private const ulong Mask = 0xA9;
    private const ulong Seed = 0xFF;
    private const int RegLen = 7;

    /// <summary>XOR each bit (0/1, transmission order) with the LFSR PN sequence, in place.</summary>
    public static void DescrambleInPlace(int[] bits)
    {
      ulong sr = Seed;
      for (int i = 0; i < bits.Length; i++)
      {
        int outbit = (int)(sr & 1);
        bits[i] ^= outbit;
        ulong newbit = Parity(sr & Mask);
        sr = (sr >> 1) | (newbit << RegLen);
      }
    }

    /// <summary>
    /// Byte-wise variant for byte-aligned frames (AX100/U482C): XOR each byte with the same PN sequence
    /// packed MSB-first — the standard CCSDS randomizer bytes <c>FF 48 0E C0 9A 0D 70 BC …</c>. Self-inverse.
    /// </summary>
    public static void XorSequenceInPlace(Span<byte> data)
    {
      ulong sr = Seed;
      for (int i = 0; i < data.Length; i++)
      {
        int pn = 0;
        for (int k = 0; k < 8; k++)
        {
          pn = (pn << 1) | (int)(sr & 1);
          sr = (sr >> 1) | (Parity(sr & Mask) << RegLen);
        }
        data[i] ^= (byte)pn;
      }
    }

    private static ulong Parity(ulong x)
    {
      x ^= x >> 32; x ^= x >> 16; x ^= x >> 8; x ^= x >> 4; x ^= x >> 2; x ^= x >> 1;
      return x & 1;
    }
  }
}
