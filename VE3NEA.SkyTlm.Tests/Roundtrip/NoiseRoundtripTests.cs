using FluentAssertions;
using VE3NEA.SkyTlm.Core;
using VE3NEA.SkyTlm.Dsp;
using VE3NEA.SkyTlm.Tests.Fixtures;
using Xunit;
using Xunit.Abstractions;

namespace VE3NEA.SkyTlm.Tests.Roundtrip
{
  /// <summary>Round-trips over a noisy channel: BER stays within budget at a given Es/N0, and the eye opens as SNR rises.</summary>
  public class NoiseRoundtripTests
  {
    private readonly ITestOutputHelper output;
    public NoiseRoundtripTests(ITestOutputHelper o) => output = o;

    private const double Fs = 48000;
    private static SignalParams Params(double baud) => new(baud, Modulation.GMSK,  Framing.USP, Fs);

    [Theory]
    [InlineData(20, 1e-3)]   // strong: essentially error-free
    [InlineData(12, 3e-2)]   // moderate
    public void NoisyGmsk_DecodesWithinBerBudget(double esN0Db, double maxBer)
    {
      var bits = GmskModulator.RandomBits(1000, seed: 11);
      var iq = GmskModulator.Modulate(bits, 4800, Fs, bt: 0.5, esN0Db: esN0Db);
      var sym = new GmskDemodulator().DemodulateSegment(iq, Params(4800));
      var (ber, _, _) = BerTools.BestBer(bits, sym.Soft);
      output.WriteLine($"Es/N0={esN0Db}dB eye={sym.EyeSnrDb:0.0}dB ber={ber:0.000}");
      ber.Should().BeLessThanOrEqualTo(maxBer);
    }

    [Fact]
    public void EyeOpening_ImprovesWithSnr()
    {
      var bits = GmskModulator.RandomBits(1000, seed: 5);
      double Eye(double esn0) => new GmskDemodulator()
        .DemodulateSegment(GmskModulator.Modulate(bits, 4800, Fs, esN0Db: esn0), Params(4800)).EyeSnrDb;
      Eye(20).Should().BeGreaterThan(Eye(8), "the eye should open up as SNR improves");
    }

    [Theory]
    [InlineData(2)]   // 2-bit DF-DD
    [InlineData(3)]   // 3-bit DF-DD
    public void Dfdd_CleanGmsk_DecodesErrorFree(int order)
    {
      var bits = GmskModulator.RandomBits(600, seed: 7);
      var iq = GmskModulator.Modulate(bits, 4800, Fs, bt: 0.5);
      var opt = new GmskDemodOptions { DifferentialOrder = order };
      var sym = new GmskDemodulator(opt).DemodulateSegment(iq, Params(4800));
      var (ber, _, _) = BerTools.BestBer(bits, sym.Soft);
      output.WriteLine($"DF-DD N={order} clean ber={ber:0.0000}");
      ber.Should().BeLessThan(1e-3, "DF-DD must decode clean GMSK error-free");
    }

    [Fact]
    public void Dfdd_BeatsDiscriminator_UnderNoise()
    {
      // ~10 dB Es/N0: the discriminator makes a few % errors; DF-DD (N=2) should cut that several-fold.
      var bits = GmskModulator.RandomBits(2000, seed: 11);
      var iq = GmskModulator.Modulate(bits, 4800, Fs, bt: 0.5, esN0Db: 10);
      var disc = new GmskDemodulator().DemodulateSegment(iq, Params(4800));
      var dfdd = new GmskDemodulator(new GmskDemodOptions { DifferentialOrder = 2 })
        .DemodulateSegment(iq, Params(4800));
      var (berDisc, _, _) = BerTools.BestBer(bits, disc.Soft);
      var (berDfdd, _, _) = BerTools.BestBer(bits, dfdd.Soft);
      output.WriteLine($"Es/N0=10dB  discriminator ber={berDisc:0.0000}  DF-DD N=2 ber={berDfdd:0.0000}");
      berDfdd.Should().BeLessThan(berDisc, "DF-DD recovers ~2.5-3 dB over the frequency discriminator");
    }
  }
}
