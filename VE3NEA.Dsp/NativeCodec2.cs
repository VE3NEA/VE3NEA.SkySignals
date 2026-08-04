using System;
using System.Runtime.InteropServices;

namespace VE3NEA
{
  /// <summary>
  /// P/Invoke bindings for <b>codec2</b> (David Rowe, LGPL-2.1) — the x64 <c>codec2.dll</c> built from
  /// drowe67/codec2 1.2.0 (see <c>Vendor/codec2/README.md</c> for the recipe; MSVC cannot build it).
  /// Used by <c>VE3NEA.SkyTlm.Audio</c> to decode the HADES-SA Codec2 700C voice downlink. Only the
  /// decode path is bound — nothing here encodes.
  ///
  /// Codec state lives in the handle from <see cref="codec2_create"/>, so give every decoder its own —
  /// decoding advances the synthesis filter, and one instance is not thread-safe.
  ///
  /// <para>
  /// <b>One piece of state is global, and it is not worth locking.</b> <c>codec2_rand()</c>
  /// (<c>sine.c</c>: "todo: this should probably be in some states rather than a static") is a
  /// process-wide LFSR that the phase synthesis draws from for unvoiced harmonics, so concurrent — or
  /// merely successive — decoders interleave draws. The audible result is nil: unvoiced phase is
  /// arbitrary by construction, and the same bits always give the same speech. What it does cost is
  /// <i>bit</i> reproducibility, so a decode is byte-identical to the reference tool's output only as
  /// the first decode in a fresh process. Measured on a 5 s clip, that shows up as 15 % relative RMS
  /// between two decodes of the same bits, against 116 % for a genuinely wrong bit unpacking — far
  /// enough apart that the differential tests assert a bound rather than equality. This is unlike
  /// <see cref="NativeFec.Viterbi27Gate"/>, where the shared state changes what the answer <i>means</i>
  /// and a lock is mandatory.
  /// </para>
  ///
  /// <para>
  /// <b>Bit packing is the caller's job.</b> <see cref="codec2_decode"/> reads
  /// <see cref="codec2_bytes_per_frame"/> bytes holding <see cref="codec2_bits_per_frame"/> bits
  /// MSB-first, left-aligned, with the tail of the last byte padded. For 700C that is 28 bits in 4
  /// bytes with 4 zero pad bits — <i>not</i> the tight 28-bit packing an on-air format may use.
  /// </para>
  /// </summary>
  public static class NativeCodec2
  {
    private const string CODEC2 = "codec2";
    private const CallingConvention cdecl = CallingConvention.Cdecl;

    /// <summary>Mode ids as defined in <c>codec2.h</c>. Only the ones we can be asked for are listed;
    /// the value is what <see cref="codec2_create"/> takes, and the gaps in the numbering are real.</summary>
    public const int MODE_3200 = 0;
    public const int MODE_2400 = 1;
    public const int MODE_1600 = 2;
    public const int MODE_1400 = 3;
    public const int MODE_1300 = 4;
    public const int MODE_1200 = 5;
    public const int MODE_700C = 8;

    /// <summary>Create a codec instance for one of the <c>MODE_*</c> constants. Returns
    /// <see cref="IntPtr.Zero"/> when the mode was compiled out or allocation failed. One instance
    /// serves both directions, but we only ever decode.</summary>
    [DllImport(CODEC2, CallingConvention = cdecl)]
    public static extern IntPtr codec2_create(int mode);

    [DllImport(CODEC2, CallingConvention = cdecl)]
    public static extern void codec2_destroy(IntPtr codec2State);

    /// <summary>Decode one frame: <paramref name="bits"/> holds <see cref="codec2_bytes_per_frame"/>
    /// bytes, <paramref name="speechOut"/> receives <see cref="codec2_samples_per_frame"/> samples of
    /// 16-bit PCM at 8 kHz.</summary>
    [DllImport(CODEC2, CallingConvention = cdecl)]
    public static extern void codec2_decode(IntPtr codec2State, short[] speechOut, byte[] bits);

    /// <summary>Decode one frame, telling the codec how bad the link is (0.0 = clean). The estimate
    /// only softens the postfilter and the voicing decision; it repairs nothing. Worth using on a
    /// downlink with no FEC and no CRC, which is exactly HADES-SA's voice path.</summary>
    [DllImport(CODEC2, CallingConvention = cdecl)]
    public static extern void codec2_decode_ber(IntPtr codec2State, short[] speechOut, byte[] bits,
                                                float berEst);

    /// <summary>Samples produced per decoded frame — 320 (40 ms at 8 kHz) for every mode we use.</summary>
    [DllImport(CODEC2, CallingConvention = cdecl)]
    public static extern int codec2_samples_per_frame(IntPtr codec2State);

    /// <summary>Bits consumed per decoded frame — 28 for 700C.</summary>
    [DllImport(CODEC2, CallingConvention = cdecl)]
    public static extern int codec2_bits_per_frame(IntPtr codec2State);

    /// <summary>Bytes <see cref="codec2_decode"/> reads per frame, i.e. the bit count rounded up — 4
    /// for 700C's 28 bits.</summary>
    [DllImport(CODEC2, CallingConvention = cdecl)]
    public static extern int codec2_bytes_per_frame(IntPtr codec2State);

    /// <summary>Select natural or Gray coding of the quantiser indices. 700C packs natural, which is
    /// the library default, so this is bound for completeness rather than because we call it.</summary>
    [DllImport(CODEC2, CallingConvention = cdecl)]
    public static extern void codec2_set_natural_or_gray(IntPtr codec2State, int gray);
  }
}
