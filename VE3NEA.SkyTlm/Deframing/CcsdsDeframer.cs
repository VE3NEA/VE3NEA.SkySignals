using System;
using System.Collections.Generic;
using System.Numerics;
using VE3NEA;
using VE3NEA.SkyTlm.Core;

namespace VE3NEA.SkyTlm.Deframing
{
  /// <summary>
  /// CCSDS TM-frame deframer — a C# port of the gr-satellites CCSDS deframer family
  /// (<c>ccsds_uncoded_deframer</c> / <c>ccsds_rs_deframer</c> / <c>ccsds_concatenated_deframer</c>), which are
  /// one parameterized receive chain selected by <see cref="CcsdsOptions"/>. Consumes soft symbols and emits
  /// the recovered transfer-frame bytes (Space-Packet/telemetry parsing is a separate step).
  /// <para>
  /// Shared constants: 32-bit ASM <c>0x1ACFFC1D</c>; CCSDS additive scrambler <c>0xA9/0xFF/7</c>
  /// (<see cref="CcsdsScrambler"/>); RS(255,223) with 32 parity per codeword (<see cref="RsCodeword"/>);
  /// Viterbi r=1/2 k=7 (<see cref="NativeFec"/>).
  /// </para>
  /// <list type="bullet">
  /// <item><b>RS / uncoded</b> (<see cref="CcsdsOptions.Convolutional"/> <c>null</c>): hard-slice the soft bits →
  /// [NRZ-I diff decode if precoding] → 32-bit ASM sync scan (dual polarity, ≤ <see cref="CcsdsOptions.SyncThreshold"/>
  /// errors) → [CCSDS descramble] → [RS deinterleave/decode/reinterleave].</item>
  /// <item><b>Concatenated</b> (<see cref="CcsdsOptions.Convolutional"/> non-null): the ASM is itself
  /// convolutionally encoded, so <b>Viterbi decode the whole window first</b>, then run the RS/uncoded chain on
  /// the decoded bits. The unknown convolutional symbol phase is resolved by trying symbol phase ∈ {0,1} ×
  /// discriminator polarity ∈ {±1} sequentially and letting RS reject the wrong combinations (the concatenated
  /// block always has RS enabled).</item>
  /// </list>
  /// RS is the integrity gate ⇒ <see cref="Frame.CrcValid"/> = RS ok. The uncoded (no-RS) path has no integrity
  /// check, so it emits the descrambled frame bytes with <see cref="Frame.CrcValid"/> = null (not applicable).
  /// Conventions verified by roundtrip against the native libfec decoder; the dual-basis encoder uses the libfec
  /// Taltab/Tal1tab transform.
  /// </summary>
  public sealed class CcsdsDeframer : IDeframer
  {
    // 32-bit ASM 0x1ACFFC1D (CCSDS attached sync marker = 00011010110011111111110000011101), MSB first on air.
    private const uint Asm = 0x1ACFFC1D;
    private const int AsmLen = 32;

    private readonly CcsdsOptions opt;
    private readonly bool conv;          // concatenated (Viterbi) path
    private readonly int[]? polys;       // convolutional generator polynomials, when conv
    private readonly int rsNn;           // per-codeword length = frameSize/interleave + 32
    private readonly int rsPad;          // 255 − rsNn (libfec shortening)
    private readonly int msgBytes;       // bytes captured after the ASM: frameSize (+ 32·I when RS)
    private readonly int packBits;       // msgBytes · 8 (the descramble/sync_to_pdu packet length, in bits)

    public CcsdsDeframer(CcsdsOptions? options = null)
    {
      opt = options ?? new CcsdsOptions();
      int interleave = Math.Max(1, opt.RsInterleaving);
      if (opt.RsEnabled && opt.FrameSize % interleave != 0)
        throw new ArgumentException("CCSDS frame size must be divisible by the RS interleaving depth");

      msgBytes = opt.FrameSize + (opt.RsEnabled ? RsCodeword.ParityBytes * interleave : 0);
      rsNn = opt.RsEnabled ? opt.FrameSize / interleave + RsCodeword.ParityBytes : 0;
      rsPad = opt.RsEnabled ? RsCodeword.Len - rsNn : 0;
      packBits = msgBytes * 8;
      conv = opt.Convolutional != null;
      polys = conv ? CcsdsOptions.PolysFor(opt.Convolutional!) : null;
    }

    /// <summary>ASM + one captured packet, doubled on the concatenated path (rate-1/2 coded on air).</summary>
    public int MaxFrameBits => (AsmLen + packBits) * (conv ? 2 : 1);

    public IEnumerable<Frame> Deframe(SoftSymbols syms, SignalParams p) =>
      conv ? DeframeConcatenated(syms.Soft) : DeframeDirect(syms.Soft);


    // ----------------------------------------------------------------------------------------------------
    //                                       RS / uncoded path
    // ----------------------------------------------------------------------------------------------------


    /// <summary>Hard-slice the soft stream, optionally NRZ-I decode, then sync-scan and decode each frame. The
    /// original soft array feeds the erasure-assisted RS retry (skipped when precoding scrambles the bit↔soft
    /// alignment).</summary>
    private IEnumerable<Frame> DeframeDirect(float[] soft)
    {
      int n = soft.Length;
      var hard = new int[n];
      for (int i = 0; i < n; i++) hard[i] = SoftBits.Hard(soft[i]);
      if (opt.Precoding) SoftBits.DiffDecode(hard);
      return ScanAndDecode(hard, opt.Precoding ? null : soft);
    }


    // ----------------------------------------------------------------------------------------------------
    //                                       concatenated path
    // ----------------------------------------------------------------------------------------------------


    /// <summary>Viterbi-decode the whole window for each symbol phase (0/1), then run the RS/uncoded chain on the
    /// decoded bits; RS rejects the wrong phase. Only one Viterbi polarity is tried: the CCSDS convolutional code
    /// is transparent (both generator polynomials have odd weight), so an inverted stream decodes to the
    /// complement of the bits, which the dual-polarity ASM scan inside <see cref="ScanAndDecode"/> recovers — so
    /// trying both polarities would only double-count. Frame offsets are mapped from decoded-bit space back to
    /// coded-symbol space (phase + 2·bit).</summary>
    private IEnumerable<Frame> DeframeConcatenated(float[] soft)
    {
      var frames = new List<Frame>();
      foreach (int phase in new[] { 0, 1 })
      {
        int[] hard = ViterbiDecode(soft, phase, polarity: 1);
        if (hard.Length == 0) continue;
        if (opt.Precoding) SoftBits.DiffDecode(hard);
        foreach (var f in ScanAndDecode(hard, null))
          frames.Add(f with { SoftBitOffset = f.SoftBitOffset >= 0 ? phase + 2 * f.SoftBitOffset : -1 });
      }
      return frames;
    }

    /// <summary>Viterbi r=1/2 k=7 decode of the whole window from <paramref name="phase"/>, with the soft symbols
    /// scaled by <paramref name="polarity"/>, flushed with 6 erasure symbol-pairs (as <see cref="UspDeframer"/>
    /// does) so the terminated chainback is valid. Returns the decoded data bits (MSB-first).</summary>
    private int[] ViterbiDecode(float[] soft, int phase, int polarity)
    {
      int nsyms = ((soft.Length - phase) / 2) * 2;
      int nbits = nsyms / 2;
      if (nbits < 1) return Array.Empty<int>();

      var vsyms = new byte[nsyms + 12];                 // + 6 erasure symbol-pairs to flush the trellis
      for (int k = 0; k < nsyms; k++) vsyms[k] = ToSym(polarity * soft[phase + k]);
      for (int k = nsyms; k < vsyms.Length; k++) vsyms[k] = 128;

      IntPtr vp = NativeFec.create_viterbi27(nbits);
      if (vp == IntPtr.Zero) return Array.Empty<int>();
      var packed = new byte[(nbits + 7) / 8];
      try
      {
        NativeFec.set_viterbi27_polynomial(polys!);
        NativeFec.init_viterbi27(vp, 0);
        NativeFec.update_viterbi27_blk(vp, vsyms, nbits + 6);
        NativeFec.chainback_viterbi27(vp, packed, (uint)nbits, 0);
      }
      finally { NativeFec.delete_viterbi27(vp); }

      var bits = new int[nbits];
      for (int b = 0; b < nbits; b++) bits[b] = (packed[b >> 3] >> (7 - (b & 7))) & 1; // MSB-first
      return bits;
    }


    // ----------------------------------------------------------------------------------------------------
    //                                    shared sync scan + decode
    // ----------------------------------------------------------------------------------------------------


    /// <summary>Slide a 32-bit window over the hard bits, matching the ASM in both polarities; on each match
    /// decode the following packet and, on success, resume past it. <paramref name="erasureSoft"/> (or null)
    /// enables the soft erasure RS retry for the interleave-1 case.</summary>
    private IEnumerable<Frame> ScanAndDecode(int[] hard, float[]? erasureSoft)
    {
      int n = hard.Length;
      const ulong mask = 0xFFFFFFFFUL;
      ulong sync = Asm & mask;
      ulong syncInv = ~Asm & mask;
      ulong window = 0;
      int asmHits = 0, rsFails = 0;

      for (int i = 0; i < n; i++)
      {
        window = ((window << 1) | (uint)hard[i]) & mask;
        if (i + 1 < AsmLen) continue;

        bool normal = BitOperations.PopCount(window ^ sync) <= opt.SyncThreshold;
        bool inverted = BitOperations.PopCount(window ^ syncInv) <= opt.SyncThreshold;
        if (!normal && !inverted) continue;

        asmHits++;
        int asmStart = i + 1 - AsmLen;
        int bodyStart = i + 1;
        if (bodyStart + packBits > n) continue;

        bool flip = inverted && !normal;                // prefer normal polarity when both somehow match
        var bits = new int[packBits];
        for (int b = 0; b < packBits; b++) bits[b] = hard[bodyStart + b] ^ (flip ? 1 : 0);

        var frame = DecodeBody(bits, erasureSoft, bodyStart);
        if (frame is null) { rsFails++; continue; }

        yield return frame with { SoftBitOffset = asmStart };
        i = bodyStart + packBits - 1;                   // resume past the packet; the loop ++ lands just past it
      }
    }

    /// <summary>Descramble, pack to bytes, and (when RS is enabled) RS-decode the captured packet, returning the
    /// transfer-frame bytes or null. The no-RS path emits the descrambled frame bytes unverified.</summary>
    private Frame? DecodeBody(int[] bits, float[]? erasureSoft, int bodyStart)
    {
      if (opt.Scrambler) CcsdsScrambler.DescrambleInPlace(bits);

      var msg = new byte[msgBytes];
      for (int b = 0; b < packBits; b++)
        if (bits[b] == 1) msg[b >> 3] |= (byte)(1 << (7 - (b & 7)));

      if (!opt.RsEnabled)
        return new Frame { Bytes = msg, CrcValid = null, Framing = Framing.CCSDS };

      return RsDecode(msg, erasureSoft, bodyStart);
    }

    /// <summary>Deinterleave the I RS codewords (<c>cw[j][k] = msg[j + k·I]</c>), decode each (the integrity
    /// gate — any codeword failure rejects the frame), and
    /// reinterleave the recovered data into the <c>frameSize</c>-byte transfer frame.</summary>
    private Frame? RsDecode(byte[] msg, float[]? erasureSoft, int bodyStart)
    {
      int interleave = Math.Max(1, opt.RsInterleaving);
      int dataLen = rsNn - RsCodeword.ParityBytes;      // data bytes per codeword
      var outFrame = new byte[opt.FrameSize];
      int totalCorrected = 0, totalErased = 0;

      for (int j = 0; j < interleave; j++)
      {
        var cw = new byte[rsNn];
        for (int k = 0; k < rsNn; k++) cw[k] = msg[j + k * interleave];

        byte[] decoded;
        int erased = 0;
        int res;
        if (interleave == 1 && erasureSoft != null)
        {
          decoded = (byte[])cw.Clone();
          res = RsCodeword.Decode(decoded, rsPad, opt.RsDualBasis);
          if (res < 0)
            res = RsCodeword.TryWithErasures(cw, erasureSoft, bodyStart, rsPad, opt.RsDualBasis, out decoded, out erased);
        }
        else
        {
          decoded = (byte[])cw.Clone();
          res = RsCodeword.Decode(decoded, rsPad, opt.RsDualBasis);
        }
        if (res < 0) return null;                       // RS is the CCSDS integrity gate

        totalCorrected += res;
        totalErased += erased;
        for (int k = 0; k < dataLen; k++) outFrame[j + k * interleave] = decoded[k];
      }

      return new Frame
      {
        Bytes = outFrame,
        CrcValid = true,
        Framing = Framing.CCSDS,
        CorrectedBits = totalCorrected,
        ErasedBytes = totalErased
      };
    }

    /// <summary>Map a soft value (~±1) to a libfec symbol byte (255 = strong 1, 0 = strong 0, 128 = none).</summary>
    private static byte ToSym(float v)
    {
      int s = (int)Math.Round(127.5 + 127.5 * v);
      return (byte)Math.Clamp(s, 0, 255);
    }
  }
}
