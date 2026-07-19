using System.Linq;
using FluentAssertions;
using Xunit;

namespace VE3NEA.SkyFM.Tests
{
  public class DepthConfidenceTests
  {
    private static readonly DepthConfidence Dc = new() { ShallowDb = 4, FullDb = 10, MinWeight = 0.6f };

    [Fact]
    public void Weight_RampsBetweenShallowAndFull()
    {
      Dc.Weight(2).Should().Be(0.6f, "at or below the shallow bound the weight bottoms out");
      Dc.Weight(4).Should().Be(0.6f);
      Dc.Weight(7).Should().BeApproximately(0.8f, 1e-3f, "midpoint of the linear dB ramp");
      Dc.Weight(10).Should().Be(1f);
      Dc.Weight(25).Should().Be(1f);
    }

    [Fact]
    public void Weight_GridsExemptByDefault()
    {
      Dc.Weight(2, CandidateKind.Grid).Should().Be(1f,
        "the default GridMinWeight of 1 exempts grids — their structure already filters junk");
      new DepthConfidence { ShallowDb = 4, GridMinWeight = 0.5f }.Weight(2, CandidateKind.Grid)
        .Should().Be(0.5f);
    }

    [Fact]
    public void Weight_UnknownDepth_NeverDemotes()
    {
      Dc.Weight(double.NaN).Should().Be(1f);
      Dc.Weight(-1).Should().Be(1f, "-1 is the file sentinel for unknown");
    }

    [Fact]
    public void Apply_ScalesOverallAndPerCharConfidenceTogether()
    {
      var c = new Candidate
      {
        Kind = CandidateKind.Callsign,
        Text = "KB2IW",
        CharConfidence = [0.9f, 0.9f, 0.9f, 0.9f, 0.9f],
        Confidence = 0.9f,
        StartSeconds = 100,
        EndSeconds = 103
      };
      var scaled = Dc.Apply([c], 4).Should().ContainSingle().Which;
      scaled.Confidence.Should().BeApproximately(0.54f, 1e-3f);
      scaled.CharConfidence.Should().OnlyContain(p => System.Math.Abs(p - 0.54f) < 1e-3);
    }

    [Fact]
    public void Apply_FullDepth_ReturnsCandidatesUnchanged()
    {
      var c = new Candidate
      {
        Kind = CandidateKind.Grid,
        Text = "EM85",
        CharConfidence = [0.9f, 0.9f, 0.9f, 0.9f],
        Confidence = 0.9f,
        StartSeconds = 100,
        EndSeconds = 102
      };
      Dc.Apply([c], 20).Single().Should().BeSameAs(c);
    }
  }
}
