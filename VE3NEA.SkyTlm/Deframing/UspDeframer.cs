using System;
using System.Collections.Generic;
using VE3NEA.SkyTlm.Core;
using VE3NEA;

namespace VE3NEA.SkyTlm.Deframing
{
  /// <summary>Tunables for <see cref="UspDeframer"/>.</summary>
  public sealed class UspOptions
  {
    /// <summary>Max syncword bit errors to accept (default 13 of 64).</summary>
    public int SyncThreshold { get; init; } = 13;
  }

  /// <summary>
  /// Unified SPUTNIX Protocol (USP) deframer, using the native <b>libfec</b> (<see cref="NativeFec"/>) 
  /// for the heavy FEC:
  /// <b>64-bit syncword search → PLS decode (frame length) → Viterbi r=1/2 k=7 (CCSDS, polys 79/-109) →
  /// CCSDS additive descramble → Reed–Solomon (255,223) dual-basis → AX.25 crop</b>. Consumes the GMSK
  /// demodulator's soft symbols and emits the encapsulated AX.25 frame bytes. USP is CCSDS
  /// concatenated coding (no NRZI/G3RUH — unlike the AX.25 G3RUH path). Conventions verified against the
  /// gr-satellites QA vectors. The non-coherent discriminator's polarity ambiguity is resolved by the
  /// syncword correlation sign.
  /// </summary>
  public sealed class UspDeframer : IDeframer
  {
    private readonly UspOptions opt;
    public UspDeframer(UspOptions? options = null) => opt = options ?? new UspOptions();

    // 64-bit USP syncword.
    private const string SyncStr =
      "0101000001110010111101100100101100101101100100001011000111110101";
    private static readonly int[] Sync = ParseBits(SyncStr);

    // CCSDS r=1/2 k=7: first symbol 0x4f, second 0x6d inverted.
    private static readonly int[] Polys = { 0x4f, -0x6d };

    // PLS (64,7) code generator + scrambler — used to read the frame length.
    private const string GStr =
      "0011001100110011001100110011001100110011001100110011001" +
      "1001100110000111100001111000011110000111100001111000011" +
      "1100001111000011110000000011111111000000001111111100000" +
      "0001111111100000000111111110000000000000000111111111111" +
      "1111000000000000000011111111111111110000000000000000000" +
      "0000000000000111111111111111111111111111111111111111111" +
      "1111111111111111111111111111111111111111111111111111110" +
      "1010101010101010101010101010101010101010101010101010101" +
      "01010101";
    private const string ScrambleStr =
      "0111000110011101100000111100100101010011010000100010110111111010";

    // two scrambled PLS codewords (bipolar ±1) the received PLS is correlated against.
    private static readonly float[] Pls0 = BuildPls(0);
    private static readonly float[] Pls1 = BuildPls(1);

    /// <summary>Sync (64) + PLS (64) + the longest rate-1/2-coded RS(255) body.</summary>
    public int MaxFrameBits => 64 + 64 + 255 * 8 * 2;

    public IEnumerable<Frame> Deframe(SoftSymbols syms, SignalParams p)
    {
      var frames = new List<Frame>();
      float[] soft = syms.Soft;
      int n = soft.Length;

      // search for the syncword in both polarities (the discriminator may invert the stream).
      for (int i = 0; i + 64 <= n; i++)
      {
        int err = SyncErrors(soft, i, out int polarity);
        if (err > opt.SyncThreshold) continue;

        int dataStart = i + 64;                  // first bit after the syncword (PLS begins here)
        if (TryDeframe(soft, dataStart, polarity, out int frameEnd) is { } f)
        {
          frames.Add(f with { SoftBitOffset = i });   // first bit of the syncword
          i = frameEnd - 1;                      // resume past the whole frame (PLS + FEC body); for-loop ++ lands on frameEnd
        }
      }
      return frames;
    }

    /// <summary>
    /// Decode one frame starting at the PLS; returns the AX.25 frame or null. <paramref name="frameEnd"/>
    /// is set to the soft index just past everything this frame consumes (PLS + Viterbi symbols), so the
    /// caller can resume the syncword search after the frame instead of re-scanning its FEC body.
    /// </summary>
    private Frame? TryDeframe(float[] soft, int plsStart, int polarity, out int frameEnd)
    {
      frameEnd = plsStart;                        // nothing consumed yet (used only on the success path)
      if (plsStart + 64 > soft.Length) return null;

      // PLS: correlate the 64 soft bits against the two known codewords -> data length.
      double c0 = 0, c1 = 0;
      for (int k = 0; k < 64; k++)
      {
        float v = polarity * soft[plsStart + k];
        c0 += v * Pls0[k];
        c1 += v * Pls1[k];
      }
      int dataLen = c1 >= c0 ? 223 : 48;          // PLS code 1 -> 223 bytes, code 0 -> 48 (per QA data)

      int rsBytes = dataLen + 32;                 // RS(255,223) codeword length (shortened for 48)
      int nbits = rsBytes * 8;
      int nsyms = nbits * 2;                       // rate 1/2
      int symStart = plsStart + 64;
      if (symStart + nsyms > soft.Length) return null;
      frameEnd = symStart + nsyms;                 // PLS (64) + rate-1/2 coded body

      // --- Viterbi (libfec) -------------------------------------------------------------------
      var vsyms = new byte[nsyms + 12];           // + 6 erasure symbol-pairs to flush the trellis
      for (int k = 0; k < nsyms; k++)
        vsyms[k] = ToSym(polarity * soft[symStart + k]);
      for (int k = nsyms; k < vsyms.Length; k++) vsyms[k] = 128; // erasure

      var bits = new int[nbits];
      IntPtr vp = NativeFec.create_viterbi27(nbits);
      if (vp == IntPtr.Zero) return null;
      try
      {
        NativeFec.set_viterbi27_polynomial(Polys);
        NativeFec.init_viterbi27(vp, 0);
        NativeFec.update_viterbi27_blk(vp, vsyms, nbits + 6);
        var packed = new byte[(nbits + 7) / 8];
        NativeFec.chainback_viterbi27(vp, packed, (uint)nbits, 0);
        for (int b = 0; b < nbits; b++) bits[b] = (packed[b >> 3] >> (7 - (b & 7))) & 1; // MSB-first
      }
      finally { NativeFec.delete_viterbi27(vp); }

      // --- CCSDS descramble + pack to RS codeword bytes ---------------------------------------
      CcsdsScrambler.DescrambleInPlace(bits);
      var rs = new byte[rsBytes];
      for (int b = 0; b < nbits; b++)
        if (bits[b] == 1) rs[b >> 3] |= (byte)(1 << (7 - (b & 7)));

      // --- Reed–Solomon (255,223) dual-basis, shortened by pad --------------------------------
      int pad = 255 - rsBytes;
      int rsResult = NativeFec.decode_rs_ccsds(rs, null, 0, pad);
      if (rsResult < 0) return null;              // RS is the USP integrity gate (no inner CRC)

      // --- AX.25 crop: bytes[2:4] little-endian length, frame at offset 4 ---------------------
      if (dataLen < 4) return null;
      int length = rs[2] | (rs[3] << 8);
      if (length < 1 || 4 + length > dataLen) return null;
      var ax25 = new byte[length];
      Array.Copy(rs, 4, ax25, 0, length);

      // the cropped bytes are the encapsulated AX.25 frame
      // USP's integrity check is the RS codeword, not an X.25 FCS, so CrcValid reflects RS success.
      return new Frame
      {
        Bytes = ax25,
        CrcValid = true,
        Framing = Framing.USP,
        CorrectedBits = rsResult
      };
    }

    /// <summary>Map a soft value (~±1) to a libfec symbol byte (255 = strong 1, 0 = strong 0, 128 = none).</summary>
    private static byte ToSym(float v)
    {
      int s = (int)Math.Round(127.5 + 127.5 * v);
      return (byte)Math.Clamp(s, 0, 255);
    }

    /// <summary>Hamming distance of the hard-sliced soft bits to the syncword; picks the better polarity.</summary>
    private static int SyncErrors(float[] soft, int off, out int polarity)
    {
      int errPos = 0;
      for (int k = 0; k < 64; k++)
      {
        int bit = soft[off + k] >= 0 ? 1 : 0;
        if (bit != Sync[k]) errPos++;
      }
      int errNeg = 64 - errPos;                   // inverted polarity
      if (errPos <= errNeg) { polarity = 1; return errPos; }
      polarity = -1; return errNeg;
    }

    private static int[] ParseBits(string s)
    {
      var b = new int[s.Length];
      for (int i = 0; i < s.Length; i++) b[i] = s[i] == '1' ? 1 : 0;
      return b;
    }

    /// <summary>Build a scrambled PLS codeword (bipolar) for the given PLS code (0 or 1).</summary>
    private static float[] BuildPls(int code)
    {
      // G is (64 x 7): G_cs[i][j] = GStr[j*64 + i]. PLS code 1 selects column 6 (bit set), code 0 -> zero.
      var scramble = ParseBits(ScrambleStr);
      var outp = new float[64];
      for (int i = 0; i < 64; i++)
      {
        int enc = code == 1 ? (GStr[6 * 64 + i] == '1' ? 1 : 0) : 0;
        int scrambled = enc ^ scramble[i];
        outp[i] = 2f * scrambled - 1f;            // bipolar ±1
      }
      return outp;
    }
  }
}
