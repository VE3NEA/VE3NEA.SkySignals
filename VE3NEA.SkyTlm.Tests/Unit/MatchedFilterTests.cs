using System.Linq;
using FluentAssertions;
using VE3NEA.SkyTlm.Dsp;
using VE3NEA.SkyTlm.Tests.Fixtures;
using Xunit;

namespace VE3NEA.SkyTlm.Tests.Unit
{
  /// <summary>
  /// Unit tests for the matched-filter convolution (<see cref="GmskDemodulator.MatchedFilter"/>):
  /// unit DC gain on a steady input, the centred impulse response, and length/finiteness preservation.
  /// </summary>
  public class MatchedFilterTests
  {
    private const double Sps = 10.0;
    private static GmskDemodulator Demod() => new();

    [Fact]
    public void SteadyInput_PassesThroughAtUnitGain()
    {
      var ones = Enumerable.Repeat(1f, 200).ToArray();
      var y = Demod().MatchedFilter(ones, Sps);
      y[100].Should().BeApproximately(1f, 1e-3f, "unit DC gain leaves a sustained run unchanged in the interior");
    }

    [Fact]
    public void ImpulseResponse_IsTheCentredPulse_WithUnitArea()
    {
      var x = Signals.Impulse(200, at: 100);
      var y = Demod().MatchedFilter(x, Sps);
      var h = GmskDemodulator.FrequencyPulse(Sps, bt: 0.5, spanSymbols: 3);

      y[100].Should().BeApproximately(h[h.Length / 2], 1e-6f, "the peak of the impulse response is the centre tap");
      y.Sum().Should().BeApproximately(1f, 1e-3f, "the impulse response integrates to the unit-DC-gain pulse");
    }

    [Fact]
    public void Output_PreservesLength_AndIsFinite()
    {
      var x = Signals.Impulse(64, at: 30);
      var y = Demod().MatchedFilter(x, Sps);
      y.Should().HaveCount(x.Length);
      y.Should().OnlyContain(v => float.IsFinite(v));
    }
  }
}
