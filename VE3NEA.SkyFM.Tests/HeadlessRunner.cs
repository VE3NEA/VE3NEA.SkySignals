using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace VE3NEA.SkyFM.Tests
{
  /// <summary>End-to-end result for one recording: the fused candidates and their per-kind scores
  /// against that recording's ground truth.</summary>
  public sealed record HeadlessResult
  {
    public required IReadOnlyList<Candidate> Fused { get; init; }
    public required EvalScore Callsigns { get; init; }
    public required EvalScore Grids { get; init; }
  }

  /// <summary>
  /// The headless batch scorer of plan §10 / Milestone 3, living in the Tests project until an app
  /// project exists (G6/A5): per-transmission ASR words → Assembler → CandidateFusion → Eval against
  /// the corpus truth. The engine host decision is still pending, so words currently come from a
  /// stored spike transcript via <see cref="LoadTranscript"/> — a real <see cref="IAsrEngine"/>
  /// plugs in later at the same seam.
  /// </summary>
  public static class HeadlessRunner
  {
    public static HeadlessResult Run(IEnumerable<IReadOnlyList<AsrWord>> transmissions, CorpusRecording truth)
    {
      var assembler = new Assembler();
      var candidates = new List<Candidate>();
      foreach (var words in transmissions) candidates.AddRange(assembler.Assemble(words));
      var fused = CandidateFusion.Fuse(candidates);

      return new HeadlessResult
      {
        Fused = fused,
        Callsigns = Eval.Score(fused, truth.Identifiers, CandidateKind.Callsign),
        Grids = Eval.Score(fused, truth.Identifiers, CandidateKind.Grid)
      };
    }

    /// <summary>Loads a spike faster-whisper transcript JSON (segments → words {w,s,e,p}) as
    /// per-transmission word sequences.</summary>
    public static List<AsrWord[]> LoadTranscript(string path)
    {
      using var doc = JsonDocument.Parse(File.ReadAllText(path));
      return doc.RootElement.GetProperty("segments").EnumerateArray()
        .Select(seg => seg.GetProperty("words").EnumerateArray()
          .Select(w => new AsrWord(
            w.GetProperty("w").GetString()!,
            w.GetProperty("s").GetDouble(),
            w.GetProperty("e").GetDouble(),
            (float)w.GetProperty("p").GetDouble()))
          .ToArray())
        .ToList();
    }
  }
}
