using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Serilog;
using VE3NEA;
using VE3NEA.SkyTlm.Core;

namespace VE3NEA.SkyTlm.Deframing
{
  /// <summary>The two GOMspace AX100 framing protocols.</summary>
  public enum Ax100Mode
  {
    /// <summary>"ASM+Golay" (GomSpace mode 5, U482C heritage): ASM → Golay(24,12) length header →
    /// CCSDS-randomized RS(255,223) codeword.</summary>
    Asm,

    /// <summary>"Reed Solomon" (GOMX-1 style): G3RUH-scrambled stream → ASM → length byte →
    /// RS(255,223) codeword.</summary>
    Rs,
  }

  /// <summary>Tunables for <see cref="Ax100Deframer"/>.</summary>
  public sealed class Ax100Options
  {
    public Ax100Mode Mode { get; init; } = Ax100Mode.Asm;

    /// <summary>Max syncword bit errors to accept (default 4 for the 32-bit ASM).</summary>
    public int SyncThreshold { get; init; } = 4;

    /// <summary>ASM mode only: CCSDS-randomize the payload ('CCSDS' for nearly every AX100 bird; a rare
    /// few fly with 'none').</summary>
    public bool Scrambler { get; init; } = true;
  }

  /// <summary>
  /// GOMspace NanoCom AX100/U482C deframer — a C# port of the gr-satellites <c>ax100_deframer</c> chain,
  /// emitting the encapsulated (most likely CSP) frame bytes. Both protocols start with the 32-bit ASM
  /// <c>0x930B51DE</c> and end in an RS(255,223) codeword (conventional basis, libfec <c>decode_rs_8</c>),
  /// which is the integrity gate — there is no separate CRC, so <see cref="Frame.CrcValid"/> reflects RS
  /// success and <see cref="Frame.CorrectedBits"/> the RS byte corrections.
  /// <list type="bullet">
  /// <item><b>ASM+Golay</b>: sync → Golay(24,12) header (low 8 bits = coded length; the viterbi/scrambler/RS
  /// flag bits are ignored, with a fixed viterbi-off/scrambler-per-config/RS-on setup) →
  /// CCSDS derandomize → RS decode → drop 32 parity bytes.</item>
  /// <item><b>RS</b>: G3RUH descramble the whole stream (<see cref="SoftBits.G3ruhDescramble"/>, the same
  /// 1+x¹²+x¹⁷ self-synchronizing scrambler AX.25 G3RUH uses) → sync → length byte (1 + payload + 32) →
  /// RS decode → crop.</item>
  /// </list>
  /// Both stream polarities are absorbed by the inverted-sync match (the G3RUH descrambler has an even tap
  /// count, so it maps an inverted stream to an inverted output).
  /// On plain RS failure the decoder retries with the lowest-confidence
  /// codeword bytes erased (RS corrects <c>2e + f ≤ 32</c>, so erasing where <c>Σ|soft|</c> is weakest nearly
  /// doubles correction power), and in ASM mode falls back once to the un-derandomized copy (birds flying
  /// <c>scrambler: none</c> without config plumbing).
  /// </summary>
  public sealed class Ax100Deframer : IDeframer
  {
    // 32-bit ASM 0x930B51DE, MSB = first bit on air.
    private const ulong SyncWord = 0x930B51DE;
    private const int SyncLen = 32;
    private const int RsLen = RsCodeword.Len;          // full RS(255,223) codeword
    private const int RsParity = RsCodeword.ParityBytes;

    private readonly Ax100Options opt;
    private readonly int packLen;          // capture after sync: 3-byte Golay header + RS, or length byte + RS

    public Ax100Deframer(Ax100Options? options = null)
    {
      opt = options ?? new Ax100Options();
      packLen = opt.Mode == Ax100Mode.Rs ? 1 + RsLen : 3 + RsLen;   // 256 / 258
    }

    /// <summary>Sync + the full fixed-length capture (header + longest RS codeword).</summary>
    public int MaxFrameBits => SyncLen + packLen * 8;

    public IEnumerable<Frame> Deframe(SoftSymbols syms, SignalParams p)
    {
      // RS mode descrambles the raw stream before the sync search (the AX100 scrambles ASM and all).
      float[] soft = opt.Mode == Ax100Mode.Rs ? SoftBits.G3ruhDescramble(syms.Soft) : syms.Soft;
      int n = soft.Length;

      // explicit scan loop (not SyncToPacket) so a successful decode resumes past the consumed body —
      // mirrors UspDeframer — instead of re-scanning near-sync patterns inside it.
      var hard = new byte[n];
      for (int i = 0; i < n; i++) hard[i] = (byte)SoftBits.Hard(soft[i]);

      const ulong mask = (1UL << SyncLen) - 1;
      const ulong sync = SyncWord & mask;
      const ulong syncInv = ~SyncWord & mask;
      ulong window = 0;

      for (int i = 0; i < n; i++)
      {
        window = ((window << 1) | hard[i]) & mask;
        if (i + 1 < SyncLen) continue;                 // window not yet full

        bool normal = BitOperations.PopCount(window ^ sync) <= opt.SyncThreshold;
        bool inverted = BitOperations.PopCount(window ^ syncInv) <= opt.SyncThreshold;
        if (!normal && !inverted) continue;

        int start = i + 1;                              // first data bit after the syncword
        // capture up to packLen bytes, but settle for however many whole bytes are left — the deframer
        // crops by the coded length, so a short trailing capture is still recoverable.
        int take = Math.Min(packLen, (n - start) / 8);
        if (take < 1) continue;

        bool flip = inverted && !normal;                // prefer normal polarity when both somehow match
        var raw = new byte[take];
        for (int b = 0; b < take * 8; b++)
        {
          int bit = hard[start + b] ^ (flip ? 1 : 0);
          if (bit != 0) raw[b >> 3] |= (byte)(1 << (7 - (b & 7))); // MSB-first
        }

        int consumedBits;
        var frame = opt.Mode == Ax100Mode.Rs
          ? TryDeframeRs(raw, soft, start, out consumedBits)
          : TryDeframeAsm(raw, soft, start, out consumedBits);
        if (frame is null) continue;

        yield return frame with { SoftBitOffset = i + 1 - SyncLen };
        i = start + consumedBits - 1;                   // resume at the body end; the loop ++ lands just past it
      }
    }

    /// <summary>
    /// ASM+Golay capture: Golay-decode the 24-bit header, crop the coded body to its length, derandomize,
    /// RS-decode (with erasure retries, and a no-scrambler fallback), strip parity. Returns null on
    /// Golay/RS failure or a length that can't hold RS parity. <paramref name="dataStart"/> is the soft-bit
    /// index of <paramref name="raw"/>[0]; <paramref name="consumedBits"/> is set (on success) to the bits
    /// this frame consumed after the syncword, so the caller can resume the sync search past the body.
    /// </summary>
    internal Frame? TryDeframeAsm(byte[] raw, float[]? soft, int dataStart, out int consumedBits)
    {
      consumedBits = 0;
      if (raw.Length < 4) return null;
      uint header = (uint)((raw[0] << 16) | (raw[1] << 8) | raw[2]);
      if (Golay24.Decode(ref header) < 0) return null;

      int codedLen = (int)(header & 0xff);                 // payload + 32 RS parity bytes
      if (codedLen < RsParity + 1 || 3 + codedLen > raw.Length) return null;

      var onAir = raw[3..(3 + codedLen)];
      var cw = (byte[])onAir.Clone();
      if (opt.Scrambler) CcsdsScrambler.XorSequenceInPlace(cw);
      int pad = RsLen - codedLen;

      string? note = null;
      int erased = 0;
      var decoded = (byte[])cw.Clone();
      int rsResult = NativeFec.decode_rs_8(decoded, null, 0, pad);

      // scrambler auto-fallback: a rare few AX100 birds fly 'scrambler: none' (no scrambler field to plumb
      // it through) — one extra decode_rs_8 on the un-derandomized copy is negligible.
      if (rsResult < 0 && opt.Scrambler)
      {
        var plain = (byte[])onAir.Clone();
        int res = NativeFec.decode_rs_8(plain, null, 0, pad);
        if (res >= 0)
        {
          decoded = plain;
          rsResult = res;
          note = "scrambler:none fallback";
          Log.Information("AX100 ASM frame decoded via scrambler:none fallback ({Corrected} bytes corrected)", res);
        }
      }

      if (rsResult < 0 && soft != null)
        rsResult = RsCodeword.TryWithErasures(cw, soft, dataStart + 3 * 8, pad, dualBasis: false, out decoded, out erased);

      if (rsResult < 0)
      {
        if ((header & 0x100) != 0)
          Log.Warning("AX100 Golay header requests Viterbi (vit=1, len={CodedLen}) — convolutional bodies are unsupported, RS failed", codedLen);
        return null;
      }

      consumedBits = (3 + codedLen) * 8;                   // golay header + RS codeword
      return new Frame
      {
        Bytes = decoded[..(codedLen - RsParity)],
        CrcValid = true,                                   // RS is the AX100 integrity gate
        Framing = Framing.AX100ASM,
        CorrectedBits = rsResult,
        ErasedBytes = erased,
        Note = note,
      };
    }

    /// <summary>
    /// RS-mode capture: the (already descrambled) byte 0 is the total length — itself + payload + 32 RS
    /// parity — followed by the RS codeword. RS-decode (with erasure retries) and crop. Returns null on a
    /// bad length or RS failure. <paramref name="dataStart"/>/<paramref name="consumedBits"/> as in
    /// <see cref="TryDeframeAsm"/>.
    /// </summary>
    internal Frame? TryDeframeRs(byte[] raw, float[]? soft, int dataStart, out int consumedBits)
    {
      consumedBits = 0;
      if (raw.Length < 2) return null;
      int total = raw[0];                                  // length byte + payload + 32 RS parity
      int codedLen = total - 1;                            // the RS codeword (payload + parity)
      int frameLen = total - 1 - RsParity;
      if (frameLen < 1 || 1 + codedLen > raw.Length) return null;

      var cw = raw[1..(1 + codedLen)];
      int pad = RsLen - codedLen;

      int erased = 0;
      var decoded = (byte[])cw.Clone();
      int rsResult = NativeFec.decode_rs_8(decoded, null, 0, pad);
      if (rsResult < 0 && soft != null)
        rsResult = RsCodeword.TryWithErasures(cw, soft, dataStart + 8, pad, dualBasis: false, out decoded, out erased);
      if (rsResult < 0) return null;

      consumedBits = (1 + codedLen) * 8;                   // length byte + RS codeword
      return new Frame
      {
        Bytes = decoded[..frameLen],
        CrcValid = true,
        Framing = Framing.AX100RS,
        CorrectedBits = rsResult,
        ErasedBytes = erased,
      };
    }
  }
}
