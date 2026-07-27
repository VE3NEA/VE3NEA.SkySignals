using System;
using System.Linq;
using FluentAssertions;
using VE3NEA.SkyTlm.Core;
using VE3NEA.SkyTlm.Discovery;
using VE3NEA.SkyTlm.Dsp;
using Xunit;

namespace VE3NEA.SkyTlm.Tests.Discovery
{
  /// <summary>
  /// Coverage rules of the discovery hypothesis set (discover_params_plan.md §4.2 / §4.3). What is under
  /// test here is <b>search-space coverage</b>, which §1.1 identifies as the only real risk to the feature:
  /// discovery works if and only if the correct parameters are among those enumerated, so each rule that
  /// narrows the set is pinned by a test rather than left to inspection.
  /// </summary>
  public class DiscoveryHypothesesTests
  {
    private const double Fs = 48000;

    private static SignalParams Db(double baud = 9600, Modulation mod = Modulation.GMSK,
      Framing framing = Framing.Unknown, double? dev = null)
      => new(baud, mod, framing, Fs, dev);



    // ----------------------------------------------------------------------------------------------------
    //                                              baud order
    // ----------------------------------------------------------------------------------------------------

    [Fact]
    public void CandidateBauds_TriesTheMeasuredRateFirst()
    {
      // measured evidence from the burst itself outranks the DB label (§4.2 tier 3).
      var bauds = DiscoveryHypotheses.CandidateBauds(Db(baud: 4800), measuredBauds: new[] { 2388.0 });
      bauds[0].Should().Be(2388);
      bauds.Should().Contain(4800, "the label is still tried, just not first");
    }

    [Fact]
    public void CandidateBauds_CoversTheLabelRelativesAndTheStandardLadder()
    {
      var bauds = DiscoveryHypotheses.CandidateBauds(Db(baud: 2400), measuredBauds: null);
      bauds.Should().Contain(new[] { 2400.0, 4800.0, 9600.0, 1200.0 }, "label, 2x, 4x and 1/2 (§4.2)");
      // the ladder's top rung, 38400 Bd, is 1.25 samples/symbol at 48 kHz and is filtered out by the sps
      // floor — it stays in the ladder for higher sample rates but is unreachable on SkyRoof's audio rate.
      bauds.Should().Contain(19200.0, "the standard ladder follows the label relatives");
    }

    [Fact]
    public void CandidateBauds_SkipsRatesTheSampleRateCannotCarry()
    {
      // 38400 Bd at 48 kHz is 1.25 samples/symbol — below the 2 sps floor, so it is never enumerated.
      var bauds = DiscoveryHypotheses.CandidateBauds(Db(baud: 19200), measuredBauds: null);
      bauds.Should().NotContain(38400.0);
      bauds.Should().OnlyContain(b => Fs / b >= 2.0);
    }

    [Fact]
    public void CandidateBauds_DedupsNearDuplicates()
    {
      // a measured 1198 Bd and a labeled 1200 Bd are the same hypothesis, not two.
      var bauds = DiscoveryHypotheses.CandidateBauds(Db(baud: 1200), measuredBauds: new[] { 1198.0 });
      bauds.Count(b => Math.Abs(b - 1200) < 10).Should().Be(1);
    }



    // ----------------------------------------------------------------------------------------------------
    //                                       modulation / deviation
    // ----------------------------------------------------------------------------------------------------

    [Fact]
    public void Demodulation_CoversTheFamiliesTheDbConfuses()
    {
      var set = DiscoveryHypotheses.Demodulation(Db(baud: 1200, mod: Modulation.FSK), null, null);
      var atLabel = set.Where(h => Math.Abs(h.Params.Baud - 1200) < 1).ToList();

      atLabel.Should().Contain(h => h.Params.Modulation == Modulation.FSK && h.Params.Deviation == null,
        "blind FSK is the hypothesis that lets the pipeline estimate the deviation from the burst (§4.2 tier 4)");
      atLabel.Should().Contain(h => h.Params.Modulation == Modulation.GMSK);
      atLabel.Should().Contain(h => h.Params.Modulation == Modulation.GFSK);
      atLabel.Should().Contain(h => h.Params.Modulation == Modulation.AFSK,
        "AFSK is enumerated at 1200 Bd, its only defined rate");
    }

    [Fact]
    public void Demodulation_PinsBell202OnTheAfskHypothesis()
    {
      var afsk = DiscoveryHypotheses.Demodulation(Db(baud: 1200, mod: Modulation.FSK), null, null)
        .Single(h => h.Params.Modulation == Modulation.AFSK);
      afsk.Params.Deviation.Should().Be(500, "Bell-202 tone half-spacing, not an RF deviation");
      afsk.Params.AfCarrier.Should().Be(1700);
    }

    [Fact]
    public void Demodulation_DoesNotEnumerateAfskAwayFrom1200()
    {
      var set = DiscoveryHypotheses.Demodulation(Db(baud: 9600, mod: Modulation.GMSK), null, null);
      set.Where(h => Math.Abs(h.Params.Baud - 1200) > 1)
        .Should().NotContain(h => h.Params.Modulation == Modulation.AFSK);
    }

    [Fact]
    public void Demodulation_DoesNotCarryAnAfskLabelToOtherBauds()
    {
      // the "DB's own family at this baud" variant must not drag an AFSK label onto a rate Bell-202 does
      // not define: such a hypothesis is not internally consistent (§4.2), and one of them (AFSK at a
      // measured 16816 Bd line) decoded a spurious CRC-valid AX.25 frame that auto-apply would have taken.
      var set = DiscoveryHypotheses.Demodulation(Db(baud: 1143, mod: Modulation.AFSK), null,
        new[] { 16816.0 });

      set.Should().Contain(h => h.Params.Modulation == Modulation.AFSK,
        "AFSK is still enumerated at its own rate");
      set.Where(h => h.Params.Modulation == Modulation.AFSK)
        .Should().OnlyContain(h => Math.Abs(h.Params.Baud - 1200) < 25, "Bell-202 is a 1200 Bd standard");
      set.Should().Contain(h => Math.Abs(h.Params.Baud - 16816) < 1,
        "the measured rate is still covered — by the generic families, not by an AFSK label");
    }

    [Fact]
    public void Demodulation_DedupsAfterWideFskNormalization()
    {
      // IsWideFsk rewrites GFSK/GMSK with h >= 0.75 to FSK at construction, so a GFSK hypothesis at
      // dev = baud/2 and an FSK one at the same deviation are the same geometry — enumerated once (§4.2).
      var set = DiscoveryHypotheses.Demodulation(Db(baud: 4800, mod: Modulation.FSK), null, null);
      set.Should().NotContain(h => Demodulators.IsWideFsk(h.Params),
        "every wide GFSK/GMSK hypothesis must already be normalized to FSK");

      var geometries = set.Select(h => (h.Params.Modulation, h.Params.Baud, h.Params.Deviation)).ToList();
      geometries.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Demodulation_PutsTheCoChannelTransmittersFirst()
    {
      var co = new[] { new SignalParams(9600, Modulation.GMSK, Framing.USP, Fs) };
      var set = DiscoveryHypotheses.Demodulation(Db(baud: 1200, mod: Modulation.FSK), co, null);
      set[0].Tier.Should().Be(1, "a co-channel transmitter of the same satellite has the highest prior (§4.2)");
      set[0].Params.Baud.Should().Be(9600);
    }

    [Fact]
    public void Demodulation_LeavesFramingToTheFramingSweep()
    {
      var set = DiscoveryHypotheses.Demodulation(Db(framing: Framing.USP), null, null);
      set.Should().OnlyContain(h => h.Params.Framing == Framing.Unknown,
        "framing is invisible to the demodulator and is swept separately (§4.3)");
    }



    // ----------------------------------------------------------------------------------------------------
    //                                               framing
    // ----------------------------------------------------------------------------------------------------

    [Fact]
    public void Framings_AreNotSearchedWhenTheDbDefinesThem()
    {
      // at the family level the DB's errors are missing framing, not wrong framing (§4.3).
      DiscoveryHypotheses.Framings(Db(framing: Framing.USP), genesisFamily: false)
        .Should().Equal(Framing.USP);
      DiscoveryHypotheses.Framings(Db(framing: Framing.AX25G3RUH), genesisFamily: false)
        .Should().Equal(Framing.AX25G3RUH);
    }

    [Theory]
    [InlineData(Framing.AX100ASM)]
    [InlineData(Framing.AX100RS)]
    public void Framings_AlwaysTryBothAx100Modes(Framing stated)
    {
      // the DB's AX100 mode is a guess returned as a fact (a bare "AX100" resolves to ASM+Golay), and the
      // alternative costs one extra deframe — so the sub-detail is never trusted (§4.3).
      var f = DiscoveryHypotheses.Framings(Db(framing: stated), genesisFamily: false);
      f.Should().BeEquivalentTo(new[] { Framing.AX100ASM, Framing.AX100RS });
      f[0].Should().Be(stated, "the DB's own value is still tried first");
    }

    [Fact]
    public void Framings_SweepsInCostOrderWhenUnknown()
    {
      var f = DiscoveryHypotheses.Framings(Db(framing: Framing.Unknown), genesisFamily: false);
      f.Should().Equal(Framing.AX25G3RUH, Framing.AX100ASM, Framing.AX100RS, Framing.USP, Framing.CCSDS);
    }

    [Fact]
    public void Framings_ExcludeHadesFromTheGenericSweep()
    {
      // one operator's custom framing, and a 16-bit syncword + CRC-16 chain is a plausible source of
      // spurious matches on unrelated signals (§4.3).
      DiscoveryHypotheses.Framings(Db(framing: Framing.Unknown), genesisFamily: false)
        .Should().NotContain(Framing.HADES);
      DiscoveryHypotheses.Framings(Db(framing: Framing.Unknown), genesisFamily: true)
        .Should().Contain(Framing.HADES);
    }



    // ----------------------------------------------------------------------------------------------------
    //                                         ccsds option space
    // ----------------------------------------------------------------------------------------------------

    [Fact]
    public void CcsdsOptionSpace_EnumeratesTheDocumentedConfigurations()
    {
      var space = DiscoveryHypotheses.CcsdsOptionSpace(Db(framing: Framing.CCSDS)).ToList();

      // uncoded: scrambler on/off = 2. RS-bearing: (RS alone + the four convolutional conventions) x
      // scrambler on/off x RS basis x interleaving {1,2,4} = 5 x 2 x 2 x 3 = 60. Total 62 (§4.3).
      space.Should().HaveCount(62);
      space.Count(p => p.RsEnabled == false).Should().Be(2);
      space.Where(p => p.RsEnabled == true).Select(p => p.Convolutional).Distinct()
        .Should().BeEquivalentTo(new string?[] { null, "CCSDS", "NASA-DSN", "CCSDS uninverted", "NASA-DSN uninverted" });
      space.Should().OnlyContain(p => p.Framing == Framing.CCSDS);
    }

    [Fact]
    public void CcsdsOptionSpace_KeepsFrameSizeDivisibleByTheInterleavingDepth()
    {
      // CcsdsOptions.FrameSize must divide by RsInterleaving, or the deframer cannot be built.
      var space = DiscoveryHypotheses.CcsdsOptionSpace(Db(framing: Framing.CCSDS) with { FrameSize = 223 }).ToList();
      space.Where(p => p.RsInterleaving is int i && i > 1)
        .Should().OnlyContain(p => (p.FrameSize ?? 223) % p.RsInterleaving!.Value == 0);
    }

    [Fact]
    public void CcsdsOptionSpace_NeverPairsAConvolutionalLayerWithTheUncodedBlock()
    {
      DiscoveryHypotheses.CcsdsOptionSpace(Db(framing: Framing.CCSDS))
        .Where(p => p.RsEnabled == false)
        .Should().OnlyContain(p => p.Convolutional == null);
    }
  }
}
