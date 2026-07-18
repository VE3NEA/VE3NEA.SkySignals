using System.IO;
using System.Linq;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace VE3NEA.SkyFM.Tests
{
  /// <summary>
  /// Runs the headless scorer on the single transcribed recording — the spike's real ARISS Whisper
  /// transcript — against the seed corpus truth: the free-transcription + grammar baseline number
  /// (M3) that the constrained Pass-B engines must beat. The spike's crude word-level scorer gave
  /// prec 0.62 / rec 0.59 on this capture.
  /// </summary>
  public class HeadlessRunnerTests
  {
    private readonly ITestOutputHelper output;
    public HeadlessRunnerTests(ITestOutputHelper o) => output = o;

    [Fact]
    public void Ariss_SingleTranscribedFile_ScoresAgainstCorpusTruth()
    {
      var corpus = Corpus.Load(RepoFiles.Find(Path.Combine("corpus", "ground-truth.json")));
      var rec = corpus.Recordings.Single(r => r.File == "2026-07-04_23_03_57_ARISS.iq.wav");
      string transcriptName = rec.File.Replace(".iq.wav", ".int8.novad.json");
      var transmissions = HeadlessRunner.LoadTranscript(
        RepoFiles.Find(Path.Combine("asr-spike", "transcripts", transcriptName)));

      var result = HeadlessRunner.Run(transmissions, rec);

      foreach (var c in result.Fused)
        output.WriteLine($"{c.StartSeconds,7:0.0}s  {c.Kind,-8} {c.Text,-9} conf {c.Confidence:0.00}");
      Report("callsigns", result.Callsigns);
      Report("grids", result.Grids);
      Report("symbols", result.Symbols);

      // measured 2026-07-18: callsigns P 0.75 R 0.62 F1 0.68 (6/8 Gold near), grids P 0.50 R 0.50
      // (EM85 FM18 recovered; JI23 FN20 artifacts) — floors just below guard against regression
      result.Callsigns.Precision.Should().BeGreaterThanOrEqualTo(0.74);
      result.Callsigns.Recall.Should().BeGreaterThanOrEqualTo(0.61);
      result.Grids.Precision.Should().BeGreaterThanOrEqualTo(0.49);
      result.Grids.Recall.Should().BeGreaterThanOrEqualTo(0.49);

      // symbol track measured 2026-07-18: P 0.82 R 0.95 F1 0.88 — 10/12 Gold identifiers fully
      // covered at the individual-symbol level, incl. all four the assembler misses
      result.Symbols.Precision.Should().BeGreaterThanOrEqualTo(0.81);
      result.Symbols.Recall.Should().BeGreaterThanOrEqualTo(0.94);
    }

    private void Report(string kind, EvalScore s)
      => output.WriteLine($"{kind}: P {s.Precision:0.00} R {s.Recall:0.00} F1 {s.F1:0.00}  " +
        $"recovered [{string.Join(" ", s.RecoveredGold)}]  unmatched [{string.Join(" ", s.Unmatched)}]");
  }
}
