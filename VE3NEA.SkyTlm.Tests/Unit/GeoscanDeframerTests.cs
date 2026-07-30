using System;
using System.Linq;
using FluentAssertions;
using VE3NEA.SkyTlm.Core;
using VE3NEA.SkyTlm.Deframing;
using VE3NEA.SkyTlm.Tests.Fixtures;
using Xunit;

namespace VE3NEA.SkyTlm.Tests.Unit
{
  /// <summary>
  /// GEOSCAN deframer validation: the PN9 whitening and CC11xx CRC-16 against their published known answers,
  /// then the whole chain against the <see cref="GeoscanTx"/> reference encoder — both stream polarities, a
  /// non-default frame size, stream handling, and the CRC gate.
  /// </summary>
  public class GeoscanDeframerTests
  {
    private static readonly SignalParams P = new(9600, Modulation.GFSK, Framing.GEOSCAN, 48000);

    // 72 = the payload of the default 74-byte frame (the size every live Geoscan bird uses).
    private static byte[] Payload(int len = 72, int seed = 1)
    {
      var rnd = new Random(seed);
      var p = new byte[len];
      rnd.NextBytes(p);
      return p;
    }


    // ---- primitives --------------------------------------------------------------------------------

    [Fact]
    public void Pn9_MatchesCc1101WhiteningSequence()
    {
      var seq = new byte[16];
      Pn9Scrambler.XorSequenceInPlace(seq);      // XOR into zeros = the raw sequence

      Convert.ToHexString(seq).Should().Be("FFE11D9AED853324EA7AD2397097570A",
        "the canonical CC1101 PN9 whitening bytes");
    }

    [Fact]
    public void Pn9_IsSelfInverse()
    {
      var data = Payload(66, seed: 11);
      var work = (byte[])data.Clone();

      Pn9Scrambler.XorSequenceInPlace(work);
      work.Should().NotEqual(data);
      Pn9Scrambler.XorSequenceInPlace(work);
      work.Should().Equal(data);
    }

    [Fact]
    public void Crc16Cc11xx_MatchesCanonicalCheckValue() =>
      Crc16Cc11xx.Compute("123456789"u8).Should().Be(0xAEE7);


    // ---- round trip --------------------------------------------------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CleanFrame_RoundTrips(bool invert)
    {
      var payload = Payload(seed: 7);

      var frames = new GeoscanDeframer().Deframe(GeoscanTx.BuildSoft(payload, invert), P).ToList();

      frames.Should().ContainSingle();
      var f = frames[0];
      f.Bytes.Should().Equal(payload, "the 64-byte payload is what the framing delivers");
      f.CrcValid.Should().BeTrue();
      f.Framing.Should().Be(Framing.GEOSCAN);
    }

    [Fact]
    public void FrameSpan_CoversSyncwordThroughCrc()
    {
      var f = new GeoscanDeframer().Deframe(GeoscanTx.BuildSoft(Payload(seed: 8)), P).Single();

      f.SoftBitOffset.Should().Be(8 * 8, "the 8-byte preamble precedes the syncword");
      f.SoftBitEnd.Should().Be(f.SoftBitOffset + 32 + 74 * 8);
    }

    [Fact]
    public void NonDefaultFrameSize_RoundTrips()
    {
      var payload = Payload(len: 30, seed: 2);
      var opt = new GeoscanOptions { FrameSize = 32 };

      new GeoscanDeframer(opt).Deframe(GeoscanTx.BuildSoft(payload), P)
        .Single().Bytes.Should().Equal(payload);
    }

    [Fact]
    public void Default_IsTheOnlyFrameSizeStillFlying()
    {
      // 74 on every live bird; 66 only on Geoscan-Edelveis and StratoSat TK-1, both re-entered. So an
      // uncatalogued Geoscan-platform bird (no satyaml ⇒ no resolved FrameSize) must guess 74, not 66.
      var payload = Payload(len: 72, seed: 10);

      new GeoscanDeframer().Deframe(GeoscanTx.BuildSoft(payload), P)
        .Single().Bytes.Should().Equal(payload);
      DeframerFactory.Create(P)!.Deframe(GeoscanTx.BuildSoft(payload), P)
        .Single().Bytes.Should().Equal(payload, "P carries no FrameSize, so the factory falls back to 74");
    }

    [Fact]
    public void Factory_CarriesTheSatelliteFrameSize()
    {
      // satyaml states the per-satellite length, so it has to reach the deframer: a 66-byte bird must not be
      // deframed at the 74-byte default (and vice versa) — the CRC covers the whole frame, so a wrong length
      // fails every frame and merely looks like a broken deframer.
      var payload = Payload(len: 64, seed: 11);
      var p66 = P with { FrameSize = 66 };

      DeframerFactory.Create(p66)!.Deframe(GeoscanTx.BuildSoft(payload), p66)
        .Single().Bytes.Should().Equal(payload);
      DeframerFactory.Create(P)!.Deframe(GeoscanTx.BuildSoft(payload), P)
        .Should().BeEmpty("the 74-byte fallback cannot gate a 66-byte frame");
    }


    // ---- CRC gate ----------------------------------------------------------------------------------

    [Fact]
    public void CorruptedPayload_YieldsNoFrame()
    {
      var soft = GeoscanTx.BuildSoft(Payload(seed: 3));
      int body = 8 * 8 + 32;                                  // preamble + syncword
      foreach (int bit in new[] { 5, 40, 130, 300 }) soft.Soft[body + bit] = -soft.Soft[body + bit];

      new GeoscanDeframer().Deframe(soft, P).Should().BeEmpty("the CRC is the only integrity gate");
    }

    [Fact]
    public void TruncatedCapture_YieldsNoFrame()
    {
      var soft = GeoscanTx.BuildSoft(Payload(seed: 4));
      var cut = new SoftSymbols { Soft = soft.Soft[..^16], SymbolRate = soft.SymbolRate };

      new GeoscanDeframer().Deframe(cut, P).Should().BeEmpty("a frame short of its CRC cannot be gated");
    }

    [Fact]
    public void NoSync_YieldsNoFrames()
    {
      var soft = new SoftSymbols { Soft = Enumerable.Repeat(-1f, 4000).ToArray(), SymbolRate = 9600 };

      new GeoscanDeframer().Deframe(soft, P).Should().BeEmpty();
    }


    // ---- stream handling ---------------------------------------------------------------------------

    [Fact]
    public void TwoAdjacentFrames_BothDecode()
    {
      var p1 = Payload(seed: 5);
      var p2 = Payload(seed: 6);
      var bits = GeoscanTx.BuildBits(p1).Concat(GeoscanTx.BuildBits(p2)).ToArray();

      var frames = new GeoscanDeframer().Deframe(Ax100Tx.ToSoft(bits), P).ToList();

      frames.Should().HaveCount(2);
      frames[0].Bytes.Should().Equal(p1);
      frames[1].Bytes.Should().Equal(p2);
    }

    [Fact]
    public void SyncBitErrors_WithinThreshold_StillDecode()
    {
      var payload = Payload(seed: 9);
      var soft = GeoscanTx.BuildSoft(payload);
      int sync = 8 * 8;
      foreach (int bit in new[] { 1, 9, 20, 31 }) soft.Soft[sync + bit] = -soft.Soft[sync + bit];

      new GeoscanDeframer().Deframe(soft, P).Single().Bytes.Should().Equal(payload);
    }
  }
}
