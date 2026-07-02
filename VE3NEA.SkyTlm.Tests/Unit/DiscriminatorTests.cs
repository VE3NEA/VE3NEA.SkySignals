using System.Linq;
using FluentAssertions;
using VE3NEA.SkyTlm.Core;
using VE3NEA.SkyTlm.Dsp;
using VE3NEA.SkyTlm.Tests.Fixtures;
using Xunit;

namespace VE3NEA.SkyTlm.Tests.Unit
{
  /// <summary>
  /// Unit tests for the FM discriminator stage (<see cref="GmskDemodulator.Discriminate"/>) in isolation:
  /// frequency→amplitude scaling, sign convention, and the DC-block that centres the eye on zero.
  /// </summary>
  public class DiscriminatorTests
  {
    private const double Fs = 48000, Baud = 4800;
    private static SignalParams Params() => new(Baud, Modulation.GMSK,  Framing.USP, Fs);

    [Fact]
    public void NominalDeviation_MapsToUnitAmplitude_WithCorrectSign()
    {
      // first half deviates at +Rs/4 (the h=0.5 GMSK peak), second half at −Rs/4.
      double dev = Baud / 4.0;
      var iq = Signals.FmTwoLevel(nPerLevel: 200, devHz: dev, fs: Fs);

      var f = GmskDemodulator.Discriminate(iq, Params());

      f[100].Should().BeApproximately(1.0f, 0.02f, "a +Rs/4 tone is the nominal +1 symbol level");
      f[300].Should().BeApproximately(-1.0f, 0.02f, "a −Rs/4 tone is the nominal −1 symbol level");
    }

    [Fact]
    public void Output_IsDcCentred()
    {
      // A pure tone is a constant frequency offset (a residual CFO); the DC-block must zero its mean.
      var tone = Signals.Tone(2000, freqHz: 800, fs: Fs);
      var f = GmskDemodulator.Discriminate(tone, Params());
      f.Average().Should().BeApproximately(0f, 1e-3f, "the discriminator removes its own mean so the slicer threshold is 0");
    }

    [Fact]
    public void Output_LengthMatchesInput_AndFirstSampleSeeded()
    {
      var iq = Signals.Tone(500, freqHz: 600, fs: Fs);
      var f = GmskDemodulator.Discriminate(iq, Params());
      f.Should().HaveCount(iq.Length);
      f[0].Should().Be(f[1], "f[0] is seeded from f[1] (no valid difference exists at index 0)");
    }
  }
}
