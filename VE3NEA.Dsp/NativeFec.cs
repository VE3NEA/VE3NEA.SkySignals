using System;
using System.Runtime.InteropServices;

namespace VE3NEA
{
  /// <summary>
  /// P/Invoke bindings for <b>libfec</b> (Phil Karn KA9Q, GPL/LGPL) — the x64 <c>fec.dll</c> built from
  /// the quiet/libfec fork (decode-only subset: CCSDS Reed–Solomon + r=1/2 k=7 Viterbi). Used by FskDemod's
  /// USP deframer for the CCSDS concatenated FEC stack. Only the functions the deframer needs are bound.
  ///
  /// Viterbi soft symbols are unsigned bytes (0..255), 128 ≈ no information; the decoder is the standard
  /// k=7 r=1/2 code, with the polynomials set via <see cref="set_viterbi27_polynomial"/>.
  /// </summary>
  public static class NativeFec
  {
    private const string FEC = "fec";
    private const CallingConvention cdecl = CallingConvention.Cdecl;

    // --- Reed–Solomon (255,223), CCSDS dual-basis -------------------------------------------------
    // Corrects in place; returns the number of symbols corrected, or -1 if uncorrectable.
    // pad = number of leading virtual zero padding symbols (255 - n for a shortened code).

    [DllImport(FEC, CallingConvention = cdecl)]
    public static extern int decode_rs_ccsds(byte[] data, int[]? eras_pos, int no_eras, int pad);

    [DllImport(FEC, CallingConvention = cdecl)]
    public static extern int decode_rs_8(byte[] data, int[]? eras_pos, int no_eras, int pad);

    // --- Viterbi, k=7 r=1/2 -----------------------------------------------------------------------

    /// <summary>Create a decoder for a frame of <paramref name="len"/> data bits. Returns an opaque handle.</summary>
    [DllImport(FEC, CallingConvention = cdecl)]
    public static extern IntPtr create_viterbi27(int len);

    /// <summary>Set the two r=1/2 generator polynomials (e.g. CCSDS V27POLYB/V27POLYA-inverted).</summary>
    [DllImport(FEC, CallingConvention = cdecl)]
    public static extern void set_viterbi27_polynomial(int[] polys);

    /// <summary>Reset the decoder for a new frame, biased toward <paramref name="starting_state"/>.</summary>
    [DllImport(FEC, CallingConvention = cdecl)]
    public static extern int init_viterbi27(IntPtr vp, int starting_state);

    /// <summary>Feed a block of soft symbol pairs. <paramref name="npairs"/> = number of decoded data bits.</summary>
    [DllImport(FEC, CallingConvention = cdecl)]
    public static extern int update_viterbi27_blk(IntPtr vp, byte[] syms, int npairs);

    /// <summary>Trace back the surviving path into <paramref name="data"/> (packed bits, MSB first per byte).</summary>
    [DllImport(FEC, CallingConvention = cdecl)]
    public static extern int chainback_viterbi27(IntPtr vp, byte[] data, uint nbits, uint endstate);

    [DllImport(FEC, CallingConvention = cdecl)]
    public static extern void delete_viterbi27(IntPtr vp);
  }
}
