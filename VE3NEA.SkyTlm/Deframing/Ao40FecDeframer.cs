using System;
using System.Collections.Generic;
using VE3NEA;
using VE3NEA.SkyTlm.Core;

namespace VE3NEA.SkyTlm.Deframing
{
  /// <summary>Tunables for <see cref="Ao40FecDeframer"/>.</summary>
  public sealed class Ao40Options
  {
    /// <summary>Max syncword bit errors to accept (default 8 of 65, the gr-satellites default).</summary>
    public int SyncThreshold { get; init; } = 8;
  }

  /// <summary>
  /// AO-40 FEC deframer — a C# port of the gr-satellites <c>ao40_fec_deframer</c> chain (Phil Karn's AO-40
  /// telemetry FEC, still flown by the whole FUNcube family: AO-73, EO-88, JO-97/JY1Sat, …) at 1k2 DBPSK.
  /// Consumes the demodulator's soft symbols and runs <b>distributed syncframe → 80 × 65 matrix deinterleave
  /// → Viterbi r=1/2 k=7 → CCSDS additive descramble → Reed–Solomon (255,223) conventional basis,
  /// interleaving depth 2</b>, emitting the 256-byte AO-40 frame.
  /// <para>
  /// The block is 5200 channel symbols: 65 syncword bits distributed one every 80 symbols, interleaved with
  /// 5132 rate-1/2 symbols. Deinterleaving by <c>out[i] = in[80·(i mod 65) + i/65]</c> lands the 65 sync bits
  /// at the front, so dropping them leaves exactly the coded block — 2560 data bits (= two shortened
  /// RS(160,128) codewords, 320 bytes) plus the 6 flush bits of the terminated convolutional code.
  /// </para>
  /// <para>
  /// Only the distributed syncframe and the deinterleaver are new here: the Viterbi is the same libfec
  /// <c>viterbi27</c> with the same polynomials <see cref="UspDeframer"/> uses, the descrambler is the shared
  /// <see cref="CcsdsScrambler"/>, and the interleaved RS is <see cref="RsCodeword"/>. RS is the integrity
  /// gate ⇒ <see cref="Frame.CrcValid"/> = RS ok. Conventions verified bit-exactly against the gr-satellites
  /// QA vector (an off-air AO-73 packet).
  /// </para>
  /// </summary>
  public sealed class Ao40FecDeframer : IDeframer
  {
    // the 65-bit AO-40 syncword, one bit every Step symbols (MSB = first bit on air).
    private const string SyncStr = "11111110000111011110010110010010000001000100110001011101011011000";

    private const int Step = 80;                        // interleaver rows = syncword bit spacing, in symbols
    private const int Cols = 65;                        // interleaver columns = syncword length, in bits
    private const int BlockSyms = Step * Cols;          // 5200 channel symbols per frame
    private const int CodedSyms = BlockSyms - Cols;     // 5132 rate-1/2 symbols once the sync bits are dropped
    private const int DataBits = 2560;                  // decoded bits kept; the trailing 6 flush bits are not
    private const int RsBytes = DataBits / 8;           // 320 = 2 interleaved RS codewords
    private const int Interleave = 2;
    private const int RsNn = RsBytes / Interleave;      // 160 — RS(255,223) shortened to (160,128)
    private const int RsPad = RsCodeword.Len - RsNn;    // 95 leading virtual zero pad symbols
    private const int FrameBytes = RsBytes - Interleave * RsCodeword.ParityBytes;   // 256

    // CCSDS r=1/2 k=7: first symbol 0x4f, second 0x6d inverted (as UspDeframer / gr-satellites' [79, -109]).
    private static readonly int[] Polys = { 0x4f, -0x6d };

    private static readonly int[] Sync = ParseBits(SyncStr);

    private readonly Ao40Options opt;
    public Ao40FecDeframer(Ao40Options? options = null) => opt = options ?? new Ao40Options();

    /// <summary>The whole interleaved block — syncword bits and coded symbols are one span on air.</summary>
    public int MaxFrameBits => BlockSyms;

    public IEnumerable<Frame> Deframe(SoftSymbols syms, SignalParams p)
    {
      var frames = new List<Frame>();
      float[] soft = syms.Soft;

      for (int i = 0; i + BlockSyms <= soft.Length; i++)
      {
        if (SyncErrors(soft, i, out int polarity) > opt.SyncThreshold) continue;

        if (TryDeframe(soft, i, polarity) is { } f)
        {
          frames.Add(f with { SoftBitOffset = i, SoftBitEnd = i + BlockSyms });
          i += BlockSyms - 1;                   // resume past the whole block; the loop ++ lands just past it
        }
      }
      return frames;
    }

    /// <summary>Hamming distance of the hard-sliced distributed sync bits to the syncword; picks the better
    /// polarity. The convolutional code is transparent, so an inverted stream would Viterbi-decode to the
    /// complement of the frame — resolving the sign here instead is what keeps the RS gate meaningful.</summary>
    private static int SyncErrors(float[] soft, int off, out int polarity)
    {
      int errPos = 0;
      for (int j = 0; j < Cols; j++)
        if (SoftBits.Hard(soft[off + j * Step]) != Sync[j]) errPos++;

      int errNeg = Cols - errPos;                       // inverted polarity
      if (errPos <= errNeg) { polarity = 1; return errPos; }
      polarity = -1; return errNeg;
    }

    /// <summary>
    /// Decode the 5200-symbol block starting at <paramref name="start"/>: deinterleave, Viterbi, descramble,
    /// RS. Returns the 256-byte AO-40 frame or null when RS — the only integrity check in the chain — fails.
    /// </summary>
    private Frame? TryDeframe(float[] soft, int start, int polarity)
    {
      // --- matrix deinterleave (80 x 65) ------------------------------------------------------
      // out[m] = block[80·(m mod 65) + m/65]; m < 65 recovers the syncword bits, which are skipped.
      var vsyms = new byte[CodedSyms];
      for (int m = Cols; m < BlockSyms; m++)
        vsyms[m - Cols] = ToSym(polarity * soft[start + Step * (m % Cols) + m / Cols]);

      // --- Viterbi (libfec) -------------------------------------------------------------------
      // the code is terminated and its 6 flush bits are on air, so the trellis needs no erasure padding:
      // DataBits + 6 symbol pairs are consumed and DataBits bits are chained back at state 0.
      var packed = new byte[RsBytes];
      // the polynomial table is process-global (see NativeFec.Viterbi27Gate), so the whole sequence is held
      lock (NativeFec.Viterbi27Gate)
      {
        IntPtr vp = NativeFec.create_viterbi27(DataBits);
        if (vp == IntPtr.Zero) return null;
        try
        {
          NativeFec.set_viterbi27_polynomial(Polys);
          NativeFec.init_viterbi27(vp, 0);
          NativeFec.update_viterbi27_blk(vp, vsyms, DataBits + 6);
          NativeFec.chainback_viterbi27(vp, packed, (uint)DataBits, 0);
        }
        finally { NativeFec.delete_viterbi27(vp); }
      }

      // --- CCSDS descramble -------------------------------------------------------------------
      // the chainback already packed the bits MSB-first and DataBits is byte-aligned, so the byte-wise
      // descrambler applies directly — no unpack/repack round trip (see CcsdsScrambler).
      CcsdsScrambler.XorSequenceInPlace(packed);

      return RsDecode(packed);
    }

    /// <summary>Deinterleave the 2 RS codewords (<c>cw[j][k] = msg[j + 2k]</c>), decode each (any failure
    /// rejects the frame), and reinterleave the recovered data into the 256-byte AO-40 frame.</summary>
    private static Frame? RsDecode(byte[] msg)
    {
      const int dataLen = RsNn - RsCodeword.ParityBytes;         // 128 data bytes per codeword
      var outFrame = new byte[FrameBytes];
      int totalCorrected = 0;

      for (int j = 0; j < Interleave; j++)
      {
        var cw = new byte[RsNn];
        for (int k = 0; k < RsNn; k++) cw[k] = msg[j + k * Interleave];

        // post-Viterbi the bit-level soft confidence no longer maps onto RS symbols, so this is the plain
        // errors-only decode — no erasure ladder (the same reason UspDeframer needs its re-encode profile).
        int res = RsCodeword.Decode(cw, RsPad, dualBasis: false);
        if (res < 0) return null;

        totalCorrected += res;
        for (int k = 0; k < dataLen; k++) outFrame[j + k * Interleave] = cw[k];
      }

      return new Frame
      {
        Bytes = outFrame,
        CrcValid = true,
        Framing = Framing.AO40FEC,
        CorrectedBits = totalCorrected
      };
    }

    /// <summary>Map a soft value (~±1) to a libfec symbol byte (255 = strong 1, 0 = strong 0, 128 = none).</summary>
    private static byte ToSym(float v)
    {
      int s = (int)Math.Round(127.5 + 127.5 * v);
      return (byte)Math.Clamp(s, 0, 255);
    }

    private static int[] ParseBits(string s)
    {
      var b = new int[s.Length];
      for (int i = 0; i < s.Length; i++) b[i] = s[i] == '1' ? 1 : 0;
      return b;
    }
  }
}
