using System;
using System.Collections.Generic;
using System.Numerics;

namespace VE3NEA.SkyTlm.Deframing
{
  /// <summary>
  /// Sync-word → fixed-length packet extractor for the non-HDLC custom framings (HADES, GEOSCAN).
  /// Slides a <paramref name="syncLen"/>-bit
  /// window over the hard-sliced soft bits, fires when the Hamming distance to <paramref name="syncBits"/>
  /// is within <paramref name="threshold"/>, and packs the following <paramref name="packLenBytes"/> bytes
  /// <b>MSB-first</b> (these framings are MSB-first, unlike the LSB-first AX.25 path). Both stream
  /// polarities are tested so the non-coherent discriminator's global sign ambiguity is absorbed (mirrors
  /// <see cref="UspDeframer"/>); on an inverted-syncword match the packed data bits are complemented too.
  /// </summary>
  public static class SyncToPacket
  {
    /// <summary>
    /// Yield <c>(packet, syncBit, dataBit)</c> per detected sync word: the raw bytes, the soft-symbol index
    /// where the syncword began (so the caller can map the frame to a stream time / promote it to a burst in
    /// the continuous path), and the index of the first data bit after the syncword — packet byte <i>k</i>
    /// maps to soft bits <c>[dataBit + 8k, dataBit + 8k + 8)</c>, which is what soft-assisted correction
    /// (erasures, Chase) needs. <paramref name="syncBits"/> holds the syncword in its low
    /// <paramref name="syncLen"/> bits (MSB = first bit on air). Each capture is up to
    /// <paramref name="packLenBytes"/> bytes, or fewer if the burst ends sooner (the caller crops by length
    /// and CRC-gates, so a short trailing packet is still recoverable).
    /// </summary>
    public static IEnumerable<(byte[] Packet, int SyncBit, int DataBit)> Extract(
      float[] soft, ulong syncBits, int syncLen, int packLenBytes, int threshold)
    {
      if (syncLen is <= 0 or > 64) throw new ArgumentOutOfRangeException(nameof(syncLen));
      if (packLenBytes <= 0) throw new ArgumentOutOfRangeException(nameof(packLenBytes));

      int n = soft.Length;

      // hard-slice once (sign is the bit, per the SoftBits convention).
      var hard = new byte[n];
      for (int i = 0; i < n; i++) hard[i] = (byte)SoftBits.Hard(soft[i]);

      ulong mask = syncLen == 64 ? ulong.MaxValue : (1UL << syncLen) - 1;
      ulong sync = syncBits & mask;
      ulong syncInv = ~syncBits & mask;
      ulong window = 0;

      for (int i = 0; i < n; i++)
      {
        window = ((window << 1) | hard[i]) & mask;
        if (i + 1 < syncLen) continue;                 // window not yet full

        bool normal = BitOperations.PopCount(window ^ sync) <= threshold;
        bool inverted = BitOperations.PopCount(window ^ syncInv) <= threshold;
        if (!normal && !inverted) continue;

        int start = i + 1;                              // first data bit after the syncword
        // capture up to packLenBytes, but settle for however many whole bytes are left — a short telemetry
        // packet near the end of a short burst has far fewer than packLenBytes after it, yet is still valid
        // (the deframer crops by length and CRC-gates). Requiring the full packLenBytes would drop it.
        int take = Math.Min(packLenBytes, (n - start) / 8);
        if (take < 1) continue;

        bool flip = inverted && !normal;                // prefer normal polarity when both somehow match
        var packet = new byte[take];
        for (int b = 0; b < take * 8; b++)
        {
          int bit = hard[start + b] ^ (flip ? 1 : 0);
          if (bit != 0) packet[b >> 3] |= (byte)(1 << (7 - (b & 7))); // MSB-first
        }
        yield return (packet, i + 1 - syncLen, start);   // syncBit = soft index where the syncword began
      }
    }
  }
}
