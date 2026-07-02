using FluentAssertions;
using VE3NEA.SkyTlm.Dsp;
using Xunit;

namespace VE3NEA.SkyTlm.Tests.Unit
{
  /// <summary>
  /// The proportional oversampling map (<see cref="GmskDemodulator.UpsampleFactorFor"/>): the factor is the
  /// nearest power of two toward the target sps (default 40 ≡ 1200-baud sps at 48 kHz), so it scales with
  /// baud and leaves already-comfortable low-baud bursts untouched, all under the MaxUpsample cap.
  /// </summary>
  public class UpsampleTests
  {
    [Theory]
    [InlineData(2.5, 16)]   // 19200 Bd  (sps 2.5 → ×16 → 40)
    [InlineData(5, 8)]      //  9600 Bd  (sps 5   → ×8  → 40)
    [InlineData(10, 4)]     //  4800 Bd  (sps 10  → ×4  → 40)
    [InlineData(20, 2)]     //  2400 Bd  (sps 20  → ×2  → 40)
    [InlineData(40, 1)]     //  1200 Bd  (already at target)
    [InlineData(60, 1)]     //   800 Bd  (above target → untouched)
    [InlineData(160, 1)]    //   300 Bd  (far above target)
    [InlineData(1, 16)]     //  hypothetical sps 1 → ratio 40 → 32, clamped to MaxUpsample 16
    public void UpsampleFactor_IsNearestPowerOfTwoTowardTarget(double sps, int expected)
    {
      new GmskDemodulator().UpsampleFactorFor(sps).Should().Be(expected);
    }

    [Fact]
    public void ZeroTarget_DisablesUpsampling()
    {
      new GmskDemodulator(new GmskDemodOptions { UpsampleTargetSps = 0 })
        .UpsampleFactorFor(5).Should().Be(1);
    }
  }
}
