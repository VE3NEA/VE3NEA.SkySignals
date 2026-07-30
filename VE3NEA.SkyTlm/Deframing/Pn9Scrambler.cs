using System;

namespace VE3NEA.SkyTlm.Deframing
{
  /// <summary>
  /// PN9 data whitening of the TI CC11xx / CC112x radio family, as used by the GEOSCAN framing — a
  /// bit-for-bit reimplementation of GNU Radio's
  /// <c>digital.additive_scrambler_bb(mask = 0x21, seed = 0x1FF, len = 8, bits_per_byte = 8)</c>, the
  /// gr-satellites <c>pn9_scrambler</c> hier block. The LFSR is x⁹ + x⁵ + 1 (taps at register bits 0 and 5,
  /// feedback into bit 8) seeded all-ones and restarted at the beginning of every frame; the eight bits it
  /// emits per byte are packed <b>LSB-first</b> — unlike <see cref="CcsdsScrambler.XorSequenceInPlace"/>,
  /// which packs the same kind of sequence MSB-first. That packing is what produces the familiar CC1101
  /// whitening bytes <c>FF E1 1D 9A ED 85 33 24 …</c> (cross-checked against SatDump's
  /// <c>PN9_MASK_Generator</c>). XOR is its own inverse, so one routine whitens and de-whitens.
  /// </summary>
  public static class Pn9Scrambler
  {
    private const ulong Mask = 0x21;
    private const ulong Seed = 0x1FF;
    private const int RegLen = 8;

    /// <summary>XOR <paramref name="data"/> with the PN9 sequence, restarted from the seed, in place.</summary>
    public static void XorSequenceInPlace(Span<byte> data)
    {
      ulong sr = Seed;
      for (int i = 0; i < data.Length; i++)
      {
        int pn = 0;
        for (int k = 0; k < 8; k++)
        {
          pn |= (int)(sr & 1) << k;                       // LSB-first within the byte
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
