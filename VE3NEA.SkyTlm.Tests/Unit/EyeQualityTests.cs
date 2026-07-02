using System;
using FluentAssertions;
using VE3NEA.SkyTlm.Dsp;
using Xunit;

namespace VE3NEA.SkyTlm.Tests.Unit
{
  /// <summary>
  /// Unit tests for the eye-quality metric (<see cref="GmskDemodulator.EyeQuality"/>): the SNR-like eye
  /// opening in dB and the ambiguous (near-threshold) fraction, checked against hand-built soft streams.
  /// </summary>
  public class EyeQualityTests
  {
    [Fact]
    public void TwoCleanClusters_WithKnownSpread_GiveExpectedEyeDb()
    {
      // clusters at ±1 each spread by ±0.1 ⇒ sep=2, σ=0.1 ⇒ 20·log10(2/(2·0.1)) = 20 dB.
      var soft = new[] { 1.1f, 0.9f, -0.9f, -1.1f };
      var (eyeDb, ambig) = GmskDemodulator.EyeQuality(soft);
      eyeDb.Should().BeApproximately(20.0, 0.5);
      ambig.Should().Be(0, "all symbols sit far from the slicer threshold");
    }

    [Fact]
    public void PerfectlyTightClusters_SaturateTheEye()
    {
      var soft = new[] { 1f, 1f, -1f, -1f };
      var (eyeDb, ambig) = GmskDemodulator.EyeQuality(soft);
      eyeDb.Should().Be(60.0, "zero within-cluster spread saturates the metric");
      ambig.Should().Be(0);
    }

    [Fact]
    public void AmbiguousFraction_CountsNearThresholdSymbols()
    {
      // μ₊=0.7, μ₋=−0.7 ⇒ threshold = 0.25·0.7 = 0.175; the two ±0.1 symbols fall inside it.
      var soft = new[] { 1f, 1f, 0.1f, -1f, -1f, -0.1f };
      var (_, ambig) = GmskDemodulator.EyeQuality(soft);
      ambig.Should().BeApproximately(2.0 / 6.0, 1e-6);
    }

    [Fact]
    public void OneSidedClusters_ReportNoEye()
    {
      var (eyeDb, ambig) = GmskDemodulator.EyeQuality(new[] { 1f, 0.5f, 0.8f, 1.2f });
      eyeDb.Should().Be(0);
      ambig.Should().Be(1, "with no negative cluster there is no open eye");
    }

    [Fact]
    public void TooFewSymbols_ReportNoEye()
    {
      GmskDemodulator.EyeQuality(new[] { 1f, -1f }).Should().Be((0.0, 1.0));
    }
  }
}
