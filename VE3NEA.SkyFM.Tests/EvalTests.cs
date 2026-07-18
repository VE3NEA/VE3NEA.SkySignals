using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Xunit;

namespace VE3NEA.SkyFM.Tests
{
  public class EvalTests
  {
    private static Candidate Cand(string text, CandidateKind kind, double t) => new()
    {
      Kind = kind,
      Text = text,
      CharConfidence = Enumerable.Repeat(0.8f, text.Length).ToArray(),
      Confidence = 0.8f,
      StartSeconds = t,
      EndSeconds = t + 2
    };

    private static TruthIdentifier Truth(string text, CandidateKind kind, TruthTag tag, params double[] times)
      => new() { Text = text, Kind = kind, Tag = tag, Times = times };

    [Fact]
    public void SpuriousEmission_CostsPrecision_NotRecall()
    {
      var score = Eval.Score(
        [Cand("EM85", CandidateKind.Grid, 100), Cand("JI23", CandidateKind.Grid, 200)],
        [Truth("EM85", CandidateKind.Grid, TruthTag.Gold, 100)],
        CandidateKind.Grid);

      score.Precision.Should().BeApproximately(4.0 / 8.0, 1e-9, "4 of 8 emitted chars are correct");
      score.Recall.Should().Be(1.0);
      score.Unmatched.Should().Equal("JI23");
    }

    [Fact]
    public void TruncatedCandidate_GetsPartialRecall()
    {
      var score = Eval.Score(
        [Cand("KR4JI", CandidateKind.Callsign, 230)],
        [Truth("KR4JIQ", CandidateKind.Callsign, TruthTag.Gold, 229)],
        CandidateKind.Callsign);

      score.Precision.Should().Be(1.0, "every emitted char is in the truth");
      score.Recall.Should().BeApproximately(5.0 / 6.0, 1e-9, "the Q was never emitted — abstention costs recall only");
    }

    [Fact]
    public void PartialTierTruth_AbsorbsWithoutPenaltyEitherWay()
    {
      var truth = new[] { Truth("KF4UJ", CandidateKind.Callsign, TruthTag.Partial, 333) };

      // missed: no recall penalty (recall denominator is Gold only)
      Eval.Score([], truth, CandidateKind.Callsign).Recall.Should().Be(1.0);

      // found: counts as correct chars, no unmatched-FP precision hit
      var score = Eval.Score([Cand("KF4UJ", CandidateKind.Callsign, 333)], truth, CandidateKind.Callsign);
      score.Precision.Should().Be(1.0);
      score.Unmatched.Should().BeEmpty();
    }

    [Fact]
    public void FarAwayInTime_DoesNotMatch()
    {
      var score = Eval.Score(
        [Cand("EM85", CandidateKind.Grid, 400)],
        [Truth("EM85", CandidateKind.Grid, TruthTag.Gold, 100)],
        CandidateKind.Grid);
      score.Unmatched.Should().ContainSingle("a match 300 s away is a different exchange")
        .Which.Should().Be("EM85");
      score.Recall.Should().Be(0.0);
    }

    [Fact]
    public void SeedCorpus_Loads()
    {
      // the consolidated ground-truth file (plan §4), seeded with the spike's ARISS ear truth
      for (var dir = new System.IO.DirectoryInfo(System.AppContext.BaseDirectory); dir != null; dir = dir.Parent)
      {
        string p = System.IO.Path.Combine(dir.FullName, "corpus", "ground-truth.json");
        if (!System.IO.File.Exists(p)) continue;
        var corpus = Corpus.Load(p);
        var ariss = corpus.Recordings.Should().ContainSingle(r => r.Satellite == "ARISS").Which;
        ariss.Identifiers.Count(i => i.Tag == TruthTag.Gold).Should().Be(12);
        return;
      }
      Assert.Fail("corpus/ground-truth.json not found above the test directory");
    }

    [Fact]
    public void CorpusJson_RoundTrips()
    {
      var corpus = new Corpus
      {
        Recordings =
        [
          new CorpusRecording
          {
            File = "2026-07-04_23_03_57_ARISS.iq.wav",
            Satellite = "ARISS",
            Role = "train",
            Identifiers = [Truth("EM85", CandidateKind.Grid, TruthTag.Gold, 137, 229)]
          }
        ]
      };
      string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "skyfm-corpus-roundtrip.json");
      corpus.Save(path);
      var loaded = Corpus.Load(path);
      loaded.Should().BeEquivalentTo(corpus);
      System.IO.File.Delete(path);
    }
  }
}
