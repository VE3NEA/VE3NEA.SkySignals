using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Xunit;

namespace VE3NEA.SkyFM.Tests
{
  public class CandidateFusionTests
  {
    private static Candidate Cand(string text, CandidateKind kind, float conf, double t) => new()
    {
      Kind = kind,
      Text = text,
      CharConfidence = Enumerable.Repeat(conf, text.Length).ToArray(),
      Confidence = conf,
      StartSeconds = t,
      EndSeconds = t + 2
    };

    [Fact]
    public void RepeatedMention_Corroborates()
    {
      var fused = CandidateFusion.Fuse(new List<Candidate>
      {
        Cand("EM85", CandidateKind.Grid, 0.6f, 100),
        Cand("EM85", CandidateKind.Grid, 0.7f, 220)
      });
      var c = fused.Should().ContainSingle().Which;
      c.Text.Should().Be("EM85");
      c.Confidence.Should().BeApproximately(1f - 0.4f * 0.3f, 1e-3f, "independent mentions soft-OR");
      c.StartSeconds.Should().Be(100);
      c.EndSeconds.Should().Be(222);
    }

    [Fact]
    public void SubstringMention_PrefersTheBetterSupportedText()
    {
      // a truncated parse and the full callsign are the same station
      var fused = CandidateFusion.Fuse(new List<Candidate>
      {
        Cand("KR4JI", CandidateKind.Callsign, 0.5f, 100),
        Cand("KR4JIQ", CandidateKind.Callsign, 0.8f, 200)
      });
      fused.Should().ContainSingle().Which.Text.Should().Be("KR4JIQ");
    }

    [Fact]
    public void SingleCharSlip_MajorityWins()
    {
      // the spike's real pair: KB3IW (garbled mention) + KB2IW ×2
      var fused = CandidateFusion.Fuse(new List<Candidate>
      {
        Cand("KB3IW", CandidateKind.Callsign, 0.78f, 321),
        Cand("KB2IW", CandidateKind.Callsign, 0.87f, 339),
        Cand("KB2IW", CandidateKind.Callsign, 0.50f, 344)
      });
      var c = fused.Should().ContainSingle().Which;
      c.Text.Should().Be("KB2IW");
      c.Confidence.Should().BeGreaterThan(0.9f);
    }

    [Fact]
    public void DistinctStationsOneCharApart_DoNotMerge()
    {
      // the real ARISS pass contains BOTH AB2IW and KB2IW — each independently repeated; edit-distance
      // clustering must not unify two corroborated stations
      var fused = CandidateFusion.Fuse(new List<Candidate>
      {
        Cand("AB2IW", CandidateKind.Callsign, 0.6f, 280),
        Cand("AB2IW", CandidateKind.Callsign, 0.6f, 285),
        Cand("KB2IW", CandidateKind.Callsign, 0.9f, 339),
        Cand("KB2IW", CandidateKind.Callsign, 0.5f, 344)
      });
      fused.Should().HaveCount(2);
      fused.Select(c => c.Text).Should().BeEquivalentTo(["AB2IW", "KB2IW"]);
    }

    [Fact]
    public void TwoUncorroboratedSlips_DoNotMerge()
    {
      // a single KB3IW next to a single KB2IW: no way to tell garble from distinct station — keep both
      var fused = CandidateFusion.Fuse(new List<Candidate>
      {
        Cand("KB3IW", CandidateKind.Callsign, 0.78f, 321),
        Cand("KB2IW", CandidateKind.Callsign, 0.87f, 339)
      });
      fused.Should().HaveCount(2);
    }

    [Fact]
    public void UnrelatedIdentifiers_StayApart()
    {
      var fused = CandidateFusion.Fuse(new List<Candidate>
      {
        Cand("EM85", CandidateKind.Grid, 0.7f, 100),
        Cand("FM18", CandidateKind.Grid, 0.7f, 200),
        Cand("AB2IW", CandidateKind.Callsign, 0.7f, 300)
      });
      fused.Should().HaveCount(3);
    }

    [Fact]
    public void LoneMention_KeepsRawConfidence()
    {
      var fused = CandidateFusion.Fuse([Cand("KF4UJ", CandidateKind.Callsign, 0.72f, 333)]);
      fused.Should().ContainSingle().Which.Confidence.Should().Be(0.72f);
    }
  }
}
