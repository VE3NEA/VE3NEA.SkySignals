using System;
using System.Collections.Generic;
using System.Linq;

namespace VE3NEA.SkyFM.Tests
{
  /// <summary>Character-level score of one candidate set against one recording's truth (plan §11).</summary>
  public sealed record EvalScore
  {
    public required int EmittedChars { get; init; }
    public required int CorrectChars { get; init; }
    public required int GoldChars { get; init; }
    public required int RecalledChars { get; init; }
    public required List<string> RecoveredGold { get; init; }
    public required List<string> Unmatched { get; init; }

    public double Precision => EmittedChars == 0 ? 1.0 : (double)CorrectChars / EmittedChars;
    public double Recall => GoldChars == 0 ? 1.0 : (double)RecalledChars / GoldChars;
    public double F1 => Precision + Recall == 0 ? 0.0 : 2 * Precision * Recall / (Precision + Recall);
  }

  /// <summary>
  /// The character-level scorer of plan §11, one kind at a time: each emitted candidate is aligned to
  /// the best truth identifier of the same kind within the time window (LCS ratio ≥ 0.5); precision
  /// counts correct emitted characters over all emitted characters (spurious emissions cost precision),
  /// recall counts recovered characters over Gold characters only — Partial-tier truths absorb
  /// candidates without penalty, Unintelligible ones are excluded entirely.
  /// </summary>
  public static class Eval
  {
    private const double TimeWindowS = 25.0;

    public static EvalScore Score(IEnumerable<Candidate> candidates, IEnumerable<TruthIdentifier> truth,
      CandidateKind kind)
    {
      var truths = truth.Where(t => t.Kind == kind && t.Tag != TruthTag.Unintelligible).ToList();
      var gold = truths.Where(t => t.Tag == TruthTag.Gold).ToList();

      int emitted = 0, correct = 0;
      var goldLcs = new Dictionary<string, int>();
      var recovered = new List<string>();
      var unmatched = new List<string>();

      foreach (var c in candidates.Where(c => c.Kind == kind))
      {
        emitted += c.Text.Length;

        TruthIdentifier? best = null;
        double bestR = 0;
        foreach (var t in truths)
        {
          if (!t.Times.Any(x => Math.Abs(c.StartSeconds - x) <= TimeWindowS)) continue;
          double r = (double)Lcs(c.Text, t.Text) / Math.Max(c.Text.Length, t.Text.Length);
          if (r > bestR) { bestR = r; best = t; }
        }

        if (best == null || bestR < 0.5) { unmatched.Add(c.Text); continue; }
        int lcs = Lcs(c.Text, best.Text);
        correct += lcs;
        if (best.Tag == TruthTag.Gold)
        {
          if (!recovered.Contains(best.Text)) recovered.Add(best.Text);
          goldLcs[best.Text] = Math.Max(goldLcs.GetValueOrDefault(best.Text), lcs);
        }
      }

      return new EvalScore
      {
        EmittedChars = emitted,
        CorrectChars = correct,
        GoldChars = gold.Sum(t => t.Text.Length),
        RecalledChars = gold.Sum(t => Math.Min(goldLcs.GetValueOrDefault(t.Text), t.Text.Length)),
        RecoveredGold = recovered,
        Unmatched = unmatched
      };
    }

    /// <summary>Longest common subsequence length — the char-alignment credit of §11.</summary>
    public static int Lcs(string a, string b)
    {
      var d = new int[a.Length + 1, b.Length + 1];
      for (int i = 0; i < a.Length; i++)
        for (int j = 0; j < b.Length; j++)
          d[i + 1, j + 1] = a[i] == b[j] ? d[i, j] + 1 : Math.Max(d[i, j + 1], d[i + 1, j]);
      return d[a.Length, b.Length];
    }
  }
}
