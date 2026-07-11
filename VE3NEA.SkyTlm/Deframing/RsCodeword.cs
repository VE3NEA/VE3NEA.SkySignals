using System;
using System.Linq;
using VE3NEA;

namespace VE3NEA.SkyTlm.Deframing
{
  /// <summary>
  /// Shared Reed–Solomon (255,223) codeword decode helpers over the native <b>libfec</b>
  /// (<see cref="NativeFec"/>), parameterized by field basis: <b>dual</b> (CCSDS, <c>decode_rs_ccsds</c>) or
  /// <b>conventional</b> (<c>decode_rs_8</c>). Used by both <see cref="Ax100Deframer"/> (conventional) and
  /// <see cref="CcsdsDeframer"/> (either basis). All decoders correct in place and return the corrected-symbol
  /// count or −1; <paramref name="pad"/> is the number of leading virtual zero pad symbols (255 − codeword
  /// length) for a shortened code.
  /// </summary>
  public static class RsCodeword
  {
    /// <summary>Full RS(255,223) codeword length.</summary>
    public const int Len = 255;

    /// <summary>RS parity bytes per codeword.</summary>
    public const int ParityBytes = 32;

    // erasure retry ladder: weakest-byte counts tried on plain RS failure. Capped at 16 — the off-air
    // diagnostics showed f = 24–32 "successes" are garbage (no syndrome margin left to verify them).
    private static readonly int[] ErasureCounts = { 4, 8, 12, 16 };

    // accept an erasure-assisted decode only with ~4 syndromes of margin: 2·e + f ≤ 28 (capacity is 32).
    private const int ErasureSyndromeBudget = 28;

    /// <summary>Plain RS decode (no erasures), choosing the basis-specific libfec entry point.</summary>
    public static int Decode(byte[] cw, int pad, bool dualBasis) =>
      dualBasis ? NativeFec.decode_rs_ccsds(cw, null, 0, pad) : NativeFec.decode_rs_8(cw, null, 0, pad);

    /// <summary>
    /// Erasure-assisted RS retry: rank the codeword bytes by confidence (<c>Σ|soft|</c> over each byte's 8
    /// bits, polarity-independent), then re-run the decode with the weakest <see cref="ErasureCounts"/> bytes
    /// erased. Erasure positions are full-codeword coordinates (<c>idx + pad</c> — verified against libfec
    /// <c>decode_rs.h</c>, whose locator init takes NN-relative positions). libfec returns <c>f + e</c>
    /// corrected symbols, so a decode is accepted only when <c>2e + f ≤ 28</c> — enough unused syndromes to
    /// trust it. Each attempt runs on a fresh copy because libfec corrects in place on any claimed success.
    /// Returns the corrected-symbol count and the decoded codeword, or −1.
    /// </summary>
    public static int TryWithErasures(byte[] cw, float[] soft, int bodyBit, int pad, bool dualBasis,
                                      out byte[] decoded, out int erased)
    {
      int codedLen = cw.Length;
      var conf = new float[codedLen];
      for (int k = 0; k < codedLen; k++)
        for (int b = 0; b < 8; b++)
          conf[k] += Math.Abs(soft[bodyBit + 8 * k + b]);
      return TryWithErasures(cw, conf, pad, dualBasis, out decoded, out erased);
    }

    /// <summary>
    /// Erasure-assisted RS retry with caller-supplied <b>per-byte</b> confidence (lowest = weakest).
    /// The bit-soft overload above computes Σ|soft| per byte; concatenated-coding callers rank bytes
    /// differently (USP: the RS codeword sits behind the Viterbi, so bit-level soft confidence no
    /// longer maps 1:1 onto RS symbols — bytes are scored by the Viterbi re-encode error profile
    /// instead). Ladder, erasure coordinates and the syndrome-margin acceptance gate are as documented
    /// on the overload above.
    /// </summary>
    public static int TryWithErasures(byte[] cw, float[] byteConf, int pad, bool dualBasis,
                                      out byte[] decoded, out int erased)
    {
      int[] weakest = Enumerable.Range(0, cw.Length).OrderBy(k => byteConf[k]).ToArray();

      foreach (int f in ErasureCounts)
      {
        var trial = (byte[])cw.Clone();
        var pos = new int[ParityBytes];                    // libfec writes all corrected positions back
        for (int j = 0; j < f; j++) pos[j] = weakest[j] + pad;

        int res = dualBasis
          ? NativeFec.decode_rs_ccsds(trial, pos, f, pad)
          : NativeFec.decode_rs_8(trial, pos, f, pad);
        if (res >= 0 && 2 * (res - f) + f <= ErasureSyndromeBudget)
        {
          decoded = trial;
          erased = f;
          return res;
        }
      }

      decoded = cw;
      erased = 0;
      return -1;
    }
  }
}
