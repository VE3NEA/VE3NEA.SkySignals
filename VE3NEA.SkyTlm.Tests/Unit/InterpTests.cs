using System.Linq;
using FluentAssertions;
using Xunit;

namespace VE3NEA.SkyTlm.Tests.Unit
{
  /// <summary>
  /// Unit tests for the 4-point cubic interpolator and its bounds helper
  /// (<see cref="VE3NEA.Dsp.Interp"/> / <see cref="VE3NEA.Dsp.SampleOrZero"/>).
  /// </summary>
  public class InterpTests
  {
    private static readonly float[] Ramp = Enumerable.Range(0, 20).Select(i => (float)i).ToArray();

    [Fact]
    public void AtIntegerPosition_ReturnsExactSample()
    {
      VE3NEA.Dsp.Interp(Ramp, 5.0).Should().BeApproximately(5f, 1e-5f);
      VE3NEA.Dsp.Interp(Ramp, 12.0).Should().BeApproximately(12f, 1e-5f);
    }

    [Theory]
    [InlineData(5.5, 5.5)]
    [InlineData(7.25, 7.25)]
    [InlineData(10.9, 10.9)]
    public void OnALinearRamp_ReproducesTheLineExactly(double pos, double expected)
    {
      // A cubic Lagrange interpolator fits any linear signal with zero error.
      VE3NEA.Dsp.Interp(Ramp, pos).Should().BeApproximately((float)expected, 1e-4f);
    }

    [Fact]
    public void ConstantSignal_InterpolatesToTheConstant()
    {
      var c = Enumerable.Repeat(3.5f, 16).ToArray();
      VE3NEA.Dsp.Interp(c, 8.3).Should().BeApproximately(3.5f, 1e-5f);
    }

    [Fact]
    public void SampleOrZero_OutOfRange_ReturnsZero()
    {
      VE3NEA.Dsp.SampleOrZero(Ramp, -1).Should().Be(0f);
      VE3NEA.Dsp.SampleOrZero(Ramp, Ramp.Length).Should().Be(0f);
      VE3NEA.Dsp.SampleOrZero(Ramp, 3).Should().Be(3f);
    }
  }
}
