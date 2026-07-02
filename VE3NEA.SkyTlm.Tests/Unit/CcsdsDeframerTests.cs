using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using VE3NEA.SkyTlm.Core;
using VE3NEA.SkyTlm.Deframing;
using VE3NEA.SkyTlm.Tests.Fixtures;
using Xunit;

namespace VE3NEA.SkyTlm.Tests.Unit
{
  /// <summary>
  /// CCSDS deframer validation against the <see cref="CcsdsTx"/> reference encoder (the inverse of the
  /// gr-satellites CCSDS receive chain): the uncoded / Reed-Solomon / concatenated blocks across RS basis
  /// (conventional/dual), scrambler, NRZ-I precoding, interleaving depth, the four convolutional conventions,
  /// both stream polarities, and noise within RS capacity. The clean RS cases also cross-check the managed
  /// dual/conventional RS encoders against the native libfec decoder.
  /// </summary>
  public class CcsdsDeframerTests
  {
    private static readonly SignalParams P = new(9600, Modulation.GMSK, Framing.CCSDS, 48000);

    private static byte[] Payload(int len, int seed = 1)
    {
      var rnd = new Random(seed);
      var p = new byte[len];
      rnd.NextBytes(p);
      return p;
    }

    private static List<Frame> Decode(byte[] frame, CcsdsOptions opt, bool invert = false) =>
      new CcsdsDeframer(opt).Deframe(CcsdsTx.BuildSoft(frame, opt, invert), P).ToList();


    // ---- Reed-Solomon path: basis × scrambler × precoding × polarity -------------------------------

    [Theory]
    [InlineData(true, true, false, false)]
    [InlineData(false, true, false, false)]
    [InlineData(true, false, false, false)]
    [InlineData(false, false, false, false)]
    [InlineData(true, true, true, false)]
    [InlineData(false, true, true, false)]
    [InlineData(true, true, false, true)]
    [InlineData(false, true, false, true)]
    [InlineData(true, false, true, true)]
    public void Rs_CleanFrame_RoundTrips(bool dual, bool scrambler, bool precoding, bool invert)
    {
      var frame = Payload(223, seed: 7);
      var opt = new CcsdsOptions
      {
        FrameSize = 223,
        RsDualBasis = dual,
        Scrambler = scrambler,
        Precoding = precoding
      };

      var frames = Decode(frame, opt, invert);

      frames.Should().ContainSingle();
      var f = frames[0];
      f.Bytes.Should().Equal(frame);
      f.CrcValid.Should().BeTrue("RS is the integrity gate");
      f.Framing.Should().Be(Framing.CCSDS);
      f.CorrectedBits.Should().Be(0, "the codeword is clean");
    }


    // ---- Reed-Solomon interleaving -----------------------------------------------------------------

    [Theory]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(4, true)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(4, false)]
    public void Rs_Interleaving_RoundTrips(int interleave, bool dual)
    {
      var frame = Payload(40, seed: 3);
      var opt = new CcsdsOptions
      {
        FrameSize = 40,
        RsDualBasis = dual,
        RsInterleaving = interleave
      };

      Decode(frame, opt).Single().Bytes.Should().Equal(frame);
    }


    // ---- concatenated path: 4 conv conventions × basis × scrambler × polarity ----------------------

    [Theory]
    [InlineData("CCSDS")]
    [InlineData("NASA-DSN")]
    [InlineData("CCSDS uninverted")]
    [InlineData("NASA-DSN uninverted")]
    public void Concatenated_AllConventions_RoundTrip(string convolutional)
    {
      var frame = Payload(32, seed: 5);
      var opt = new CcsdsOptions { FrameSize = 32, Convolutional = convolutional };

      var f = Decode(frame, opt).Single();
      f.Bytes.Should().Equal(frame);
      f.CrcValid.Should().BeTrue();
      f.Framing.Should().Be(Framing.CCSDS);
    }

    [Theory]
    [InlineData(true, true, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    [InlineData(false, false, true)]
    public void Concatenated_BasisScramblerPolarity_RoundTrip(bool dual, bool scrambler, bool invert)
    {
      var frame = Payload(32, seed: 9);
      var opt = new CcsdsOptions
      {
        FrameSize = 32,
        RsDualBasis = dual,
        Scrambler = scrambler,
        Convolutional = "CCSDS"
      };

      Decode(frame, opt, invert).Single().Bytes.Should().Equal(frame);
    }


    // ---- uncoded path (no RS integrity gate) -------------------------------------------------------

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    public void Uncoded_RoundTrips(bool scrambler, bool precoding, bool invert)
    {
      var frame = Payload(64, seed: 4);
      var opt = new CcsdsOptions { FrameSize = 64, RsEnabled = false, Scrambler = scrambler, Precoding = precoding };

      var f = Decode(frame, opt, invert).Single();
      f.Bytes.Should().Equal(frame);
      f.CrcValid.Should().BeNull("the uncoded block has no integrity check — null means not applicable, not an error");
    }


    // ---- noise tolerance within RS capacity --------------------------------------------------------

    [Fact]
    public void Rs_CorrectableByteErrors_DecodeAndAreCounted()
    {
      var frame = Payload(223, seed: 7);
      var opt = new CcsdsOptions { FrameSize = 223, RsDualBasis = false };
      var soft = CcsdsTx.BuildSoft(frame, opt);

      // body begins after 16 preamble bits + the 32-bit ASM; flip one bit in each of 10 codeword bytes
      // (≤ 16 RS capacity).
      int body = 16 + 32;
      foreach (int byteIdx in new[] { 0, 9, 17, 28, 33, 50, 71, 90, 130, 200 })
        soft.Soft[body + byteIdx * 8] = -soft.Soft[body + byteIdx * 8];

      var f = new CcsdsDeframer(opt).Deframe(soft, P).Single();
      f.Bytes.Should().Equal(frame);
      f.CorrectedBits.Should().BeInRange(1, 16);
    }

    [Fact]
    public void Rs_UncorrectableErrors_YieldNoFrame()
    {
      var frame = Payload(223, seed: 7);
      var opt = new CcsdsOptions { FrameSize = 223, RsDualBasis = false };
      var soft = CcsdsTx.BuildSoft(frame, opt);

      // 20 byte errors > RS capacity (16). All soft values are full-confidence, so the erasure retry's
      // "weakest" ranking degenerates to byte order — keep the corruption past the first 16 bytes so no
      // erasure lands on an error and the retry can't (and shouldn't) rescue the codeword.
      int body = 16 + 32;
      for (int byteIdx = 100; byteIdx < 120; byteIdx++)
        soft.Soft[body + byteIdx * 8] = -soft.Soft[body + byteIdx * 8];

      new CcsdsDeframer(opt).Deframe(soft, P).Should().BeEmpty();
    }


    // ---- stream handling ---------------------------------------------------------------------------

    [Fact]
    public void Rs_TwoAdjacentFrames_BothDecode()
    {
      var opt = new CcsdsOptions { FrameSize = 40, RsDualBasis = true };
      var f1 = Payload(40, seed: 2);
      var f2 = Payload(40, seed: 3);
      var bits = CcsdsTx.BuildBits(f1, opt).Concat(CcsdsTx.BuildBits(f2, opt)).ToArray();

      var frames = new CcsdsDeframer(opt).Deframe(Ax100Tx.ToSoft(bits), P).ToList();

      frames.Should().HaveCount(2, "resuming past a decoded codeword must not skip the next frame");
      frames[0].Bytes.Should().Equal(f1);
      frames[1].Bytes.Should().Equal(f2);
    }

    [Fact]
    public void NoSync_YieldsNoFrames()
    {
      var soft = new SoftSymbols { Soft = Enumerable.Repeat(-1f, 6000).ToArray(), SymbolRate = 9600 };
      new CcsdsDeframer(new CcsdsOptions { FrameSize = 223 }).Deframe(soft, P).Should().BeEmpty();
      new CcsdsDeframer(new CcsdsOptions { FrameSize = 32, Convolutional = "CCSDS" }).Deframe(soft, P).Should().BeEmpty();
    }


    // ---- options resolution ------------------------------------------------------------------------

    [Fact]
    public void From_AppliesGrSatellitesDefaults()
    {
      var p = new SignalParams(9600, Modulation.GMSK, Framing.CCSDS, 48000);
      var opt = CcsdsOptions.From(p);

      opt.FrameSize.Should().Be(223);
      opt.Precoding.Should().BeFalse();
      opt.RsEnabled.Should().BeTrue();
      opt.RsDualBasis.Should().BeTrue("gr-satellites defaults to the dual basis");
      opt.RsInterleaving.Should().Be(1);
      opt.Scrambler.Should().BeTrue();
      opt.Convolutional.Should().BeNull();
      opt.SyncThreshold.Should().Be(4);
    }

    [Fact]
    public void From_CarriesResolvedFacts()
    {
      var p = new SignalParams(9600, Modulation.GMSK, Framing.CCSDS, 48000)
      {
        FrameSize = 892,
        RsBasis = "conventional",
        RsInterleaving = 4,
        Scrambler = false,
        Convolutional = "CCSDS uninverted",
        Differential = true
      };
      var opt = CcsdsOptions.From(p);

      opt.FrameSize.Should().Be(892);
      opt.RsDualBasis.Should().BeFalse();
      opt.RsInterleaving.Should().Be(4);
      opt.Scrambler.Should().BeFalse();
      opt.Convolutional.Should().Be("CCSDS uninverted");
      opt.Precoding.Should().BeTrue();
    }
  }
}
