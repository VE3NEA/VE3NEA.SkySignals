using System;
using FluentAssertions;
using VE3NEA.SkyTlm.Dsp;
using Xunit;

namespace VE3NEA.SkyTlm.Tests.Unit
{
  /// <summary>
  /// Unit tests for the Gaussian frequency pulse used as the matched filter
  /// (<see cref="GmskDemodulator.FrequencyPulse"/>): unit DC gain, symmetry, length, and shape.
  /// </summary>
  public class FrequencyPulseTests
  {
    [Theory]
    [InlineData(10.0, 0.5, 3)]
    [InlineData(5.0, 0.3, 4)]
    [InlineData(40.0, 0.5, 3)]
    public void Pulse_HasUnitDcGain(double sps, double bt, int span)
    {
      var h = GmskDemodulator.FrequencyPulse(sps, bt, span);
      h.Sum().Should().BeApproximately(1.0f, 1e-4f, "taps are normalised so a steady ±1 run stays ±1");
    }

    [Theory]
    [InlineData(10.0, 0.5, 3)]
    [InlineData(5.0, 0.3, 4)]
    public void Pulse_IsSymmetric_AndPeaksAtCentre(double sps, double bt, int span)
    {
      var h = GmskDemodulator.FrequencyPulse(sps, bt, span);
      int half = h.Length / 2;
      for (int i = 0; i < half; i++)
        h[i].Should().BeApproximately(h[h.Length - 1 - i], 1e-6f, "the GMSK frequency pulse is even-symmetric");
      for (int i = 0; i < h.Length; i++)
      {
        h[i].Should().BeGreaterThan(0f, "the Q-function difference is positive everywhere");
        if (i != half) h[i].Should().BeLessThanOrEqualTo(h[half], "energy concentrates at the pulse centre");
      }
    }

    [Theory]
    [InlineData(10.0, 3)]
    [InlineData(7.0, 4)]
    public void Pulse_LengthIsOddAndSpansRequestedSymbols(double sps, int span)
    {
      var h = GmskDemodulator.FrequencyPulse(sps, 0.5, span);
      int expectedHalf = (int)Math.Round(span * sps / 2.0);
      h.Length.Should().Be(2 * expectedHalf + 1);
      (h.Length % 2).Should().Be(1, "an odd length gives a single, well-defined centre tap");
    }
  }
}
