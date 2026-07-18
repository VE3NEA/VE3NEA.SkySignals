using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace VE3NEA.SkyFM.Tests
{
  /// <summary>
  /// Runs the C# grammar layer over the spike's REAL Whisper transcript of the ARISS capture
  /// (asr-spike/transcripts/…int8.novad.json) and scores it against the step-1 Gold ground truth —
  /// the C# port must reproduce spike step 3 (asr-spike/step3-ARISS.md): ~8/12 identifiers recovered,
  /// zero hallucinated identifiers from the filler segments.
  /// </summary>
  public class AssemblerTranscriptHarness
  {
    private readonly ITestOutputHelper output;
    public AssemblerTranscriptHarness(ITestOutputHelper o) => output = o;

    // step-1 ground truth Gold identifiers: (text, kind, times s)
    private static readonly (string Text, CandidateKind Kind, double[] Times)[] Gold =
    [
      ("KQ4GIK", CandidateKind.Callsign, [137, 145, 229]),
      ("KR4JIQ", CandidateKind.Callsign, [207, 229]),
      ("KQ4RGI", CandidateKind.Callsign, [247]),
      ("AB2IW", CandidateKind.Callsign, [286, 290]),
      ("KD0QPC", CandidateKind.Callsign, [310]),
      ("KB2IW", CandidateKind.Callsign, [338, 344]),
      ("K2HZV", CandidateKind.Callsign, [344, 430]),
      ("KC1ZFD", CandidateKind.Callsign, [407, 424]),
      ("EM85", CandidateKind.Grid, [137, 145, 229, 290]),
      ("FM18", CandidateKind.Grid, [247, 275]),
      ("FM17", CandidateKind.Grid, [266, 332]),
      ("FM22", CandidateKind.Grid, [286])
    ];

    [ManualFact("2026-07-18: 8/12 near (4 exact) — same as grammar.py; missed KQ4GIK KQ4RGI FM17 FM22 " +
      "(the spike's acoustic misses); unmatched JI23 WN3Y W3NY KF4UJ FN20 (the spike's artifact set); no filler IDs")]
    public void Real_AristTranscript_ReproducesSpikeStep3()
    {
      string path = FindRepoFile(Path.Combine("asr-spike", "transcripts", "2026-07-04_23_03_57_ARISS.int8.novad.json"));
      using var doc = JsonDocument.Parse(File.ReadAllText(path));

      var assembler = new Assembler();
      var candidates = new List<Candidate>();
      foreach (var seg in doc.RootElement.GetProperty("segments").EnumerateArray())
      {
        var words = seg.GetProperty("words").EnumerateArray()
          .Select(w => new AsrWord(
            w.GetProperty("w").GetString()!,
            w.GetProperty("s").GetDouble(),
            w.GetProperty("e").GetDouble(),
            (float)w.GetProperty("p").GetDouble()))
          .ToArray();
        candidates.AddRange(assembler.Assemble(words));
      }

      // cross-repeat fusion over the raw per-transmission candidates (repeats are its signal)
      var fused = CandidateFusion.Fuse(candidates);
      output.WriteLine("fused (cross-repeat):");
      foreach (var f in fused)
        output.WriteLine($"  {f.StartSeconds,7:0.0}s  {f.Kind,-8} {f.Text,-9} conf {f.Confidence:0.00}");
      fused.Should().Contain(c => c.Text == "KB2IW" && c.Confidence > 0.9f,
        "the KB3IW garble and the KB2IW mentions must fuse into a corroborated KB2IW");
      output.WriteLine("");

      // global dedupe, first occurrence wins (matches grammar.py)
      var seen = new HashSet<(CandidateKind, string)>();
      candidates = candidates.Where(c => seen.Add((c.Kind, c.Text))).ToList();

      var recovered = new Dictionary<string, (string By, double R)>();
      var unmatched = new List<Candidate>();
      foreach (var c in candidates)
      {
        string? best = null;
        double bestR = 0;
        foreach (var (text, kind, times) in Gold)
        {
          if (kind != c.Kind) continue;
          if (!times.Any(t => Math.Abs(c.StartSeconds - t) <= 25)) continue;
          double r = (double)Lcs(c.Text, text) / Math.Max(c.Text.Length, text.Length);
          if (r > bestR) { bestR = r; best = text; }
        }
        if (best != null && bestR >= 0.5)
        {
          if (!recovered.TryGetValue(best, out var prev) || bestR > prev.R) recovered[best] = (c.Text, bestR);
        }
        else unmatched.Add(c);
        output.WriteLine($"{c.StartSeconds,7:0.0}s  {c.Kind,-8} {c.Text,-9} conf {c.Confidence:0.00}  " +
          (best != null && bestR >= 0.5 ? $"-> {best} (LCS {bestR:0.00})" : "(unmatched)"));
      }

      int exact = recovered.Count(kv => kv.Value.By == kv.Key);
      output.WriteLine($"\nrecovered near {recovered.Count}/12, exact {exact}/12");
      output.WriteLine($"missed: {string.Join(" ", Gold.Select(g => g.Text).Where(g => !recovered.ContainsKey(g)))}");
      output.WriteLine($"unmatched emissions: {string.Join(" ", unmatched.Select(c => c.Text))}");

      // the spike's step-3 result: 8/12 near; unmatched = assembly artifacts + real-but-unlogged calls,
      // never the noise filler ("Thank you." etc. must contribute nothing)
      recovered.Count.Should().BeGreaterThanOrEqualTo(7, "the C# port must be in line with grammar.py's 8/12");
      unmatched.Count.Should().BeLessThan(10, "precision drag must stay at the spike's artifact level");
    }

    private static int Lcs(string a, string b)
    {
      var d = new int[a.Length + 1, b.Length + 1];
      for (int i = 0; i < a.Length; i++)
        for (int j = 0; j < b.Length; j++)
          d[i + 1, j + 1] = a[i] == b[j] ? d[i, j] + 1 : Math.Max(d[i, j + 1], d[i + 1, j]);
      return d[a.Length, b.Length];
    }

    /// <summary>Ascend from the test assembly location to the repo root and resolve
    /// <paramref name="relative"/>.</summary>
    private static string FindRepoFile(string relative)
    {
      for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
      {
        string p = Path.Combine(dir.FullName, relative);
        if (File.Exists(p)) return p;
      }
      throw new FileNotFoundException(relative);
    }
  }
}
