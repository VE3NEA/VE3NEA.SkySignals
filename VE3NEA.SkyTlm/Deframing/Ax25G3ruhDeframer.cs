using System;
using System.Collections.Generic;
using System.Linq;
using VE3NEA.SkyTlm.Core;

namespace VE3NEA.SkyTlm.Deframing
{
  /// <summary>Tunables for <see cref="Ax25G3ruhDeframer"/>.</summary>
  public sealed class Ax25Options
  {
    /// <summary>Smallest acceptable frame incl. FCS: 14 addr + 1 ctrl + 2 FCS = 17 bytes.</summary>
    public int MinFrameBytes { get; init; } = 17;

    /// <summary>Largest acceptable frame incl. FCS (guards against runaway misframing).</summary>
    public int MaxFrameBytes { get; init; } = 1024;

    /// <summary>
    /// Soft CRC-assisted correction depth: when a frame fails CRC, flip up to this many of the least-reliable
    /// bits and re-check. 0 disables it (hard-decision only). 1–2 recovers the common single/double bit
    /// errors for ~1–2 dB.
    /// </summary>
    public int ChaseFlipBits { get; init; } = 2;

    /// <summary>How many lowest-confidence bit positions the Chase search considers.</summary>
    public int ChaseCandidates { get; init; } = 16;
  }

  /// <summary>
  /// AX.25 G3RUH soft-decision deframer: consumes the demodulator's soft symbols and
  /// runs the soft receive chain —
  /// <b>soft G3RUH descramble → soft NRZI decode → HDLC (flag-sync, de-stuff) → FCS</b> — keeping LLRs
  /// through the linear stages instead of slicing early, then a hard HDLC pass and optional CRC-assisted
  /// bit-flipping (Chase) on the weakest bits. Decoupled from the modulation: anything that emits
  /// soft bits can be deframed. Bit-level conventions match direwolf (NRZI "1 = no transition", scrambler
  /// 1+x¹²+x¹⁷, CRC-16/X-25 FCS, octets LSB-first).
  /// </summary>
  public sealed class Ax25G3ruhDeframer : IDeframer
  {
    private readonly Ax25Options opt;

    public Ax25G3ruhDeframer(Ax25Options? options = null) => opt = options ?? new Ax25Options();

    /// <summary>Opening flag + the largest frame at worst-case bit stuffing (6 on-air bits per 5 data) + closing flag.</summary>
    public int MaxFrameBits => 8 + opt.MaxFrameBytes * 8 * 6 / 5 + 8;

    public IEnumerable<Frame> Deframe(SoftSymbols syms, SignalParams p)
    {
      // the on-air channel bits are NRZI(scramble(data)). For a receiver that recovers those channel bits
      // directly — coherent PSK, and the FM/GMSK discriminator — invert the TX chain: descramble, then
      // NRZI-decode (both linear over GF(2), so the order does not change the hard decisions).
      float[] des = SoftBits.G3ruhDescramble(syms.Soft);
      var frames = ExtractFrames(SoftBits.NrziDecode(des));

      // DIFFERENTIAL PSK is different: the differential detector reads phase TRANSITIONS, which already IS the
      // NRZI decode — so its soft bits are the scrambled data directly and the deframer must descramble ONLY.
      // applying NRZI a second time corrupts the frame. The differential detector's sign is not pinned by an
      // NRZI reference (no absolute phase), so try both polarities; the FCS gates false matches. Restricted to
      // linear PSK so the FM families pay nothing.
      if (p.Modulation is Modulation.BPSK or Modulation.QPSK)
      {
        var diff = ExtractFrames(des).Concat(ExtractFrames(Negate(des)));
        frames = Dedupe(frames.Concat(diff));
      }
      return frames;
    }

    private static float[] Negate(float[] x)
    {
      var y = new float[x.Length];
      for (int i = 0; i < x.Length; i++) y[i] = -x[i];
      return y;
    }

    /// <summary>Drop frames whose payload bytes (and FCS) duplicate one already kept — the same frame can be
    /// recovered by more than one chain/polarity.</summary>
    private static List<Frame> Dedupe(IEnumerable<Frame> frames)
    {
      var kept = new List<Frame>();
      foreach (var f in frames)
        if (!kept.Any(k => k.Fcs == f.Fcs && k.Bytes.AsSpan().SequenceEqual(f.Bytes)))
          kept.Add(f);
      return kept;
    }

    /// <summary>
    /// HDLC framing over the decoded soft data bits: locate 0x7E flags, take the bits between consecutive
    /// flags as a candidate, de-stuff (drop the 0 after five 1s), assemble LSB-first octets, and accept on
    /// a valid FCS (after optional Chase). Scanning every flag pair favours recall — empty/short
    /// gaps and misframes simply fail the CRC.
    /// </summary>
    internal List<Frame> ExtractFrames(float[] data)
    {
      int n = data.Length;
      var hard = new int[n];
      var conf = new float[n];
      for (int i = 0; i < n; i++) { hard[i] = SoftBits.Hard(data[i]); conf[i] = Math.Abs(data[i]); }

      // flag end-positions (index of the last bit of each 0x7E)
      var flags = new List<int>();
      int window = 0;
      for (int i = 0; i < n; i++)
      {
        window = ((window << 1) | hard[i]) & 0xff;
        if (window == 0x7e) flags.Add(i);
      }

      var frames = new List<Frame>();
      int minBits = opt.MinFrameBytes * 8;
      for (int f = 0; f + 1 < flags.Count; f++)
      {
        int start = flags[f] + 1;          // first bit after this flag
        int endExcl = flags[f + 1] - 7;    // first bit of the next flag
        if (endExcl - start < minBits) continue;

        if (TryDeframe(hard, conf, start, endExcl) is { } frame)
          frames.Add(frame with { SoftBitOffset = flags[f] - 7 });   // first bit of the opening flag
      }
      return frames;
    }

    /// <summary>De-stuff [start,end), assemble octets, and return a CRC-valid frame (with Chase) or null.</summary>
    private Frame? TryDeframe(int[] hard, float[] conf, int start, int endExcl)
    {
      var rb = new List<int>(endExcl - start);
      var rl = new List<float>(endExcl - start);
      int ones = 0;
      for (int i = start; i < endExcl; i++)
      {
        int b = hard[i];
        if (ones == 5)
        {
          if (b == 0) { ones = 0; continue; } // stuffed 0 -> drop
          return null;                        // six 1s inside a frame => misframe
        }
        rb.Add(b); rl.Add(conf[i]);
        ones = b == 1 ? ones + 1 : 0;
      }

      if (rb.Count % 8 != 0) return null;
      int nbytes = rb.Count / 8;
      if (nbytes < opt.MinFrameBytes || nbytes > opt.MaxFrameBytes) return null;

      var bytes = new byte[nbytes];
      for (int i = 0; i < rb.Count; i++)
        if (rb[i] == 1) bytes[i >> 3] |= (byte)(1 << (i & 7));

      int corrected = 0;
      if (!FcsOk(bytes))
      {
        if (opt.ChaseFlipBits <= 0 || !Chase(bytes, rb, rl.ToArray(), out corrected)) return null;
      }

      ushort fcs = (ushort)(bytes[nbytes - 2] | (bytes[nbytes - 1] << 8));
      return new Frame
      {
        Bytes = bytes[..(nbytes - 2)],
        CrcValid = true,
        Fcs = fcs,
        Framing = Framing.AX25G3RUH,
        CorrectedBits = corrected
      };
    }

    private static bool FcsOk(byte[] frame)
    {
      int n = frame.Length;
      ushort rx = (ushort)(frame[n - 2] | (frame[n - 1] << 8));
      return Crc16Ccitt.Compute(frame.AsSpan(0, n - 2)) == rx;
    }

    /// <summary>
    /// CRC-assisted soft correction: flip combinations of up to <see cref="Ax25Options.ChaseFlipBits"/> of
    /// the lowest-confidence bit positions until the FCS passes. Mutates <paramref name="frame"/> in place
    /// on success. Cheap because only the few weakest bits (by LLR magnitude) are candidates.
    /// </summary>
    private bool Chase(byte[] frame, List<int> bits, float[] llr, out int corrected)
    {
      corrected = 0;
      int[] cand = Enumerable.Range(0, bits.Count)
        .OrderBy(i => llr[i]).Take(opt.ChaseCandidates).ToArray();

      for (int k = 1; k <= opt.ChaseFlipBits; k++)
      {
        foreach (int[] combo in Combinations(cand, k))
        {
          foreach (int bit in combo) FlipBit(frame, bit);
          if (FcsOk(frame)) { corrected = k; return true; }
          foreach (int bit in combo) FlipBit(frame, bit); // revert
        }
      }
      return false;
    }

    private static void FlipBit(byte[] frame, int bitIndex) => frame[bitIndex >> 3] ^= (byte)(1 << (bitIndex & 7));

    /// <summary>All k-subsets of <paramref name="items"/> (k small; used only for Chase).</summary>
    private static IEnumerable<int[]> Combinations(int[] items, int k)
    {
      var idx = new int[k];
      for (int i = 0; i < k; i++) idx[i] = i;
      int nn = items.Length;
      if (k > nn) yield break;
      while (true)
      {
        var combo = new int[k];
        for (int i = 0; i < k; i++) combo[i] = items[idx[i]];
        yield return combo;

        int p = k - 1;
        while (p >= 0 && idx[p] == nn - k + p) p--;
        if (p < 0) yield break;
        idx[p]++;
        for (int i = p + 1; i < k; i++) idx[i] = idx[i - 1] + 1;
      }
    }
  }
}
