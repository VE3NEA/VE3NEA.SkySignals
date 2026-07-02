using System;
using System.Collections.Generic;
using System.Linq;
using VE3NEA.SkyTlm.Core;
using VE3NEA.SkyTlm.Deframing;

namespace VE3NEA.SkyTlm.Tests.Fixtures
{
  /// <summary>
  /// Reference AX100 transmitter for deframer tests: builds clean on-air bit streams for both GOMspace
  /// framings (ASM+Golay and RS), the exact inverse of the <see cref="Ax100Deframer"/> receive chain.
  /// Includes a C# RS(255,223) <b>encoder</b> with libfec's <c>decode_rs_8</c> parameters (GF(2⁸) poly
  /// 0x187, fcr 112, prim 11, 32 roots, conventional basis) because the bundled fec.dll is decode-only;
  /// the clean-codeword tests double as a cross-check of this encoder against the native decoder.
  /// </summary>
  public static class Ax100Tx
  {
    public const uint SyncWord = 0x930B51DE;

    // ---- frame builders ------------------------------------------------------------------------

    /// <summary>ASM+Golay on-air bits: preamble + ASM + Golay(len|flags) + [CCSDS-randomized] (payload + RS parity).</summary>
    public static int[] BuildAsmFrame(byte[] payload, bool scrambler = true, int preambleBytes = 8)
    {
      int codedLen = payload.Length + 32;
      if (codedLen > 255) throw new ArgumentException("payload too long");

      var cw = payload.Concat(Rs255.Parity(payload)).ToArray();
      if (scrambler) CcsdsScrambler.XorSequenceInPlace(cw);

      // header: low 8 bits = coded length; flag bits as the real radio sets them (decoder ignores them)
      uint golay = Golay24.Encode((uint)(codedLen | (scrambler ? 0x200 : 0) | 0x400));

      var bytes = new List<byte>();
      bytes.AddRange(Enumerable.Repeat((byte)0xAA, preambleBytes));
      bytes.AddRange(new[] { (byte)(SyncWord >> 24), (byte)(SyncWord >> 16 & 0xff), (byte)(SyncWord >> 8 & 0xff), (byte)(SyncWord & 0xff) });
      bytes.AddRange(new[] { (byte)(golay >> 16), (byte)(golay >> 8), (byte)golay });
      bytes.AddRange(cw);
      return ToBits(bytes.ToArray());
    }

    /// <summary>RS-mode on-air bits: G3RUH-scramble(preamble + ASM + length byte + payload + RS parity).</summary>
    public static int[] BuildRsFrame(byte[] payload, int preambleBytes = 8)
    {
      int total = 1 + payload.Length + 32;                 // length byte counts itself
      if (total - 1 > 255) throw new ArgumentException("payload too long");

      var bytes = new List<byte>();
      bytes.AddRange(Enumerable.Repeat((byte)0xAA, preambleBytes));
      bytes.AddRange(new[] { (byte)(SyncWord >> 24), (byte)(SyncWord >> 16 & 0xff), (byte)(SyncWord >> 8 & 0xff), (byte)(SyncWord & 0xff) });
      bytes.Add((byte)total);
      bytes.AddRange(payload);
      bytes.AddRange(Rs255.Parity(payload));
      return G3ruhScramble(ToBits(bytes.ToArray()));
    }

    /// <summary>Bits (0/1) → bipolar soft symbols, optionally inverted (discriminator sign ambiguity).</summary>
    public static SoftSymbols ToSoft(int[] bits, bool invert = false, double symbolRate = 9600)
    {
      var soft = new float[bits.Length];
      for (int i = 0; i < bits.Length; i++)
        soft[i] = (bits[i] == 1) != invert ? 1f : -1f;
      return new SoftSymbols { Soft = soft, SymbolRate = symbolRate };
    }

    /// <summary>Pack bytes MSB-first into a bit array.</summary>
    public static int[] ToBits(byte[] bytes)
    {
      var bits = new int[bytes.Length * 8];
      for (int i = 0; i < bits.Length; i++)
        bits[i] = (bytes[i >> 3] >> (7 - (i & 7))) & 1;
      return bits;
    }

    /// <summary>
    /// G3RUH multiplicative scrambler, s[n] = x[n] ⊕ s[n−12] ⊕ s[n−17] with a zero-filled register — the
    /// exact inverse of <see cref="SoftBits.G3ruhDescramble"/> (and of GNU Radio's
    /// <c>descrambler_bb(0x21, 0, 16)</c> used by the gr-satellites AX100 RS chain).
    /// </summary>
    public static int[] G3ruhScramble(int[] bits)
    {
      var s = new int[bits.Length];
      for (int n = 0; n < bits.Length; n++)
      {
        int b = bits[n];
        if (n >= 12) b ^= s[n - 12];
        if (n >= 17) b ^= s[n - 17];
        s[n] = b;
      }
      return s;
    }
  }

  /// <summary>
  /// RS(255,223) systematic encoder over GF(2⁸) with libfec's CCSDS conventional-basis parameters
  /// (<c>init_rs_char(8, 0x187, 112, 11, 32)</c>): shortened codes are encoded by simply omitting the
  /// virtual leading zeros, which contribute nothing to the parity feedback.
  /// </summary>
  public static class Rs255
  {
    private const int Nn = 255, NRoots = 32, Fcr = 112, Prim = 11, GfPoly = 0x187;
    private const int A0 = Nn;                          // index_of[0] sentinel ("log of zero")

    private static readonly byte[] AlphaTo = new byte[Nn + 1];
    private static readonly int[] IndexOf = new int[Nn + 1];
    private static readonly int[] GenPoly = new int[NRoots + 1];   // index form

    static Rs255()
    {
      IndexOf[0] = A0;
      AlphaTo[A0] = 0;
      int sr = 1;
      for (int i = 0; i < Nn; i++)
      {
        IndexOf[sr] = i;
        AlphaTo[i] = (byte)sr;
        sr <<= 1;
        if ((sr & 0x100) != 0) sr ^= GfPoly;
        sr &= Nn;
      }

      // generator polynomial from its roots alpha^((fcr+i)*prim), as libfec init_rs.h does
      var gen = new int[NRoots + 1];
      gen[0] = 1;
      for (int i = 0, root = Fcr * Prim; i < NRoots; i++, root += Prim)
      {
        gen[i + 1] = 1;
        for (int j = i; j > 0; j--)
          gen[j] = gen[j] != 0 ? gen[j - 1] ^ AlphaTo[ModNn(IndexOf[gen[j]] + root)] : gen[j - 1];
        gen[0] = AlphaTo[ModNn(IndexOf[gen[0]] + root)];
      }
      for (int i = 0; i <= NRoots; i++) GenPoly[i] = IndexOf[gen[i]];
    }

    /// <summary>The 32 parity bytes for <paramref name="data"/> (≤ 223 bytes; shorter = shortened code).</summary>
    public static byte[] Parity(ReadOnlySpan<byte> data)
    {
      if (data.Length > Nn - NRoots) throw new ArgumentException("data too long for RS(255,223)");
      var parity = new byte[NRoots];
      foreach (byte d in data)
      {
        int feedback = IndexOf[d ^ parity[0]];
        if (feedback != A0)
          for (int j = 1; j < NRoots; j++)
            parity[j] ^= AlphaTo[ModNn(feedback + GenPoly[NRoots - j])];
        Array.Copy(parity, 1, parity, 0, NRoots - 1);
        parity[NRoots - 1] = feedback != A0 ? AlphaTo[ModNn(feedback + GenPoly[0])] : (byte)0;
      }
      return parity;
    }

    private static int ModNn(int x)
    {
      while (x >= Nn)
      {
        x -= Nn;
        x = (x >> 8) + (x & Nn);
      }
      return x;
    }
  }
}
