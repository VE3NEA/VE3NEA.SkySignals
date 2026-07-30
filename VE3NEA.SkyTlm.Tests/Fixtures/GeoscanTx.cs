using System.Collections.Generic;
using System.Linq;
using VE3NEA.SkyTlm.Core;
using VE3NEA.SkyTlm.Deframing;

namespace VE3NEA.SkyTlm.Tests.Fixtures
{
  /// <summary>
  /// Reference GEOSCAN (CC1125) transmitter for <see cref="GeoscanDeframer"/> tests: the exact inverse of the
  /// gr-satellites receive chain — <c>preamble + syncword + PN9-whiten(payload + CC11xx CRC-16)</c>. There is
  /// no QA vector for this framing upstream, so the round-trip against this encoder plus the standalone
  /// PN9/CRC known-answer tests are what pin the conventions.
  /// </summary>
  public static class GeoscanTx
  {
    public const uint SyncWord = 0x930B51DE;

    /// <summary>On-air bits for one frame carrying <paramref name="payload"/> (64 bytes on the real fleet).</summary>
    public static int[] BuildBits(byte[] payload, int preambleBytes = 8)
    {
      var frame = payload.Concat(new byte[2]).ToArray();          // room for the trailing big-endian CRC
      ushort crc = Crc16Cc11xx.Compute(payload);
      frame[payload.Length] = (byte)(crc >> 8);
      frame[payload.Length + 1] = (byte)crc;
      Pn9Scrambler.XorSequenceInPlace(frame);                     // whitening is self-inverse

      var bytes = new List<byte>();
      bytes.AddRange(Enumerable.Repeat((byte)0xAA, preambleBytes));
      bytes.AddRange(new[] { (byte)(SyncWord >> 24), (byte)(SyncWord >> 16 & 0xff), (byte)(SyncWord >> 8 & 0xff), (byte)(SyncWord & 0xff) });
      bytes.AddRange(frame);
      return Ax100Tx.ToBits(bytes.ToArray());
    }

    /// <summary>On-air soft symbols for one frame, optionally polarity-inverted.</summary>
    public static SoftSymbols BuildSoft(byte[] payload, bool invert = false, double symbolRate = 9600) =>
      Ax100Tx.ToSoft(BuildBits(payload), invert, symbolRate);
  }
}
