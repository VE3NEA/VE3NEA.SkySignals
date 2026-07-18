using System.Linq;
using FluentAssertions;
using Xunit;

namespace VE3NEA.SkyFM.Tests
{
  public class EmitPolicyTests
  {
    private static Candidate Cand(string text, float[] charConf, float conf) => new()
    {
      Kind = CandidateKind.Callsign,
      Text = text,
      CharConfidence = charConf,
      Confidence = conf,
      StartSeconds = 100,
      EndSeconds = 102
    };

    private static Candidate Flat(string text, float conf)
      => Cand(text, Enumerable.Repeat(conf, text.Length).ToArray(), conf);

    [Fact]
    public void ConfidentCandidate_EmitsComplete()
    {
      var c = Flat("KB2IW", 0.95f);
      new EmitPolicy().Apply([c]).Should().Equal(c);
    }

    [Fact]
    public void CorroboratedFlatEngine_ClearsTheBar()
    {
      // soft-OR of two 0.80 mentions = 0.96: corroboration, not raw engine confidence, earns emission
      var c = Cand("KB2IW", Enumerable.Repeat(0.8f, 5).ToArray(), 0.96f);
      new EmitPolicy().Apply([c]).Single().Text.Should().Be("KB2IW");
    }

    [Fact]
    public void WeakCandidate_DegradesToPartial_KeepingConfidentChars()
    {
      var c = Cand("KQ4GIK", [0.95f, 0.9f, 0.92f, 0.4f, 0.3f, 0.9f], 0.72f);
      var p = new EmitPolicy().Apply([c]).Single();
      p.Text.Should().Be("KQ4??K");
      p.CharConfidence.Should().Equal(c.CharConfidence, "the partial keeps the original per-char certainty");
      p.Confidence.Should().Be(c.Confidence);
    }

    [Fact]
    public void UncorroboratedFlatEngine_Abstains()
    {
      // sherpa's placeholder 0.80 (plan §5.3 finding a): flat below both thresholds → no char survives
      new EmitPolicy().Apply([Flat("WN3Y", 0.8f)]).Should().BeEmpty();
    }

    [Fact]
    public void TooFewKnownChars_Abstains()
    {
      var c = Cand("EM85", [0.9f, 0.2f, 0.3f, 0.4f], 0.5f);
      new EmitPolicy().Apply([c]).Should().BeEmpty("a single known character is not worth emitting");
    }

    [Fact]
    public void Thresholds_AreInclusive()
    {
      new EmitPolicy { Callsigns = new(0.8f, 0.8f) }.Apply([Flat("WN3Y", 0.8f)])
        .Single().Text.Should().Be("WN3Y");
    }

    [Fact]
    public void GridThresholds_ApplySeparately()
    {
      // the calibrated grid bar is lower than the callsign bar: a 0.80 grid emits complete while a
      // 0.80 callsign abstains, and a floor-confidence grid artifact still dies
      var grid = Flat("MK25", 0.8f) with { Kind = CandidateKind.Grid };
      var junk = Flat("FN20", 0.51f) with { Kind = CandidateKind.Grid };
      var call = Flat("WN3Y", 0.8f);
      var kept = new EmitPolicy().Apply([grid, junk, call]);
      kept.Should().ContainSingle().Which.Text.Should().Be("MK25");
    }
  }
}
