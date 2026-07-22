using System;
using System.Collections.Generic;
using System.Linq;

namespace VE3NEA.SkyFM.Tests
{
  /// <summary>
  /// The symbol-level scorer of plan §11: individually decoded symbols — NATO words, number words,
  /// and spoken single letters ("vee" → V) — get their own P/R/F1 against the letters/digits of the
  /// truth identifiers, independent of whether any full identifier assembles. A symbol is correct
  /// when its character occurs in a truth identifier mentioned within the time window; a Gold
  /// character is recalled when a matching symbol exists within the window of one of its mentions
  /// (multiplicity respected per mention). Partial-tier truths absorb symbols without penalty,
  /// Unintelligible ones are excluded — same tag policy as the identifier scorer.
  /// </summary>
  public static class SymbolEval
  {
    private const double TimeWindowS = 25.0;

    /// <summary>Unambiguous spoken-letter names, scored here although the production
    /// <see cref="PhoneticDecoder"/> deliberately drops them (its measured precision trap); the
    /// English homophones ("oh", "you", "are", "why", "see", "eye", "ay") stay excluded even for
    /// scoring — they would flood precision with ordinary words.</summary>
    private static readonly Dictionary<string, char> SpokenLetters = new()
    {
      ["bee"] = 'B', ["cee"] = 'C', ["dee"] = 'D', ["ee"] = 'E', ["eff"] = 'F', ["gee"] = 'G',
      ["aitch"] = 'H', ["jay"] = 'J', ["kay"] = 'K', ["ell"] = 'L', ["em"] = 'M', ["en"] = 'N',
      ["pee"] = 'P', ["cue"] = 'Q', ["ess"] = 'S', ["tee"] = 'T', ["vee"] = 'V', ["ex"] = 'X',
      ["zee"] = 'Z', ["zed"] = 'Z'
    };

    /// <summary>The symbols one transcript word emits for the symbol-level metric: the production
    /// mapping (NATO with fuzz, number words, digit strings, id-shaped fragments) extended with
    /// single-character words ("V", "3") and the unambiguous spoken letters.</summary>
    public static string WordToSymbols(string word)
    {
      string s = PhoneticDecoder.ToSymbols(word);
      if (s.Length > 0) return s;
      string w = PhoneticDecoder.Normalize(word);
      if (SpokenLetters.TryGetValue(w, out char ltr)) return ltr.ToString();
      if (w.Length == 1 && char.IsAsciiLetter(w[0])) return char.ToUpperInvariant(w[0]).ToString();
      return "";
    }

    /// <summary>Flattens per-transmission words into timed symbols via <see cref="WordToSymbols"/>.</summary>
    public static List<(char Symbol, double TimeSeconds)> ToSymbols(IEnumerable<IReadOnlyList<AsrWord>> transmissions)
      => transmissions.SelectMany(words => words)
        .SelectMany(w => WordToSymbols(w.Text).Select(c => (c, w.StartSeconds)))
        .ToList();

    /// <summary>Scores decoded symbols against symbol-level utterance truth — the primary form for
    /// operator-transcribed recordings. Precision: a symbol matching a known truth character in an
    /// in-window utterance is correct; one near an utterance containing <c>?</c> (uncopied words) is
    /// unverifiable and absorbed without penalty; the rest are unmatched. Recall: per utterance, its
    /// known (non-<c>?</c>) characters covered with multiplicity by in-window decoded symbols.</summary>
    public static EvalScore Score(IEnumerable<(char Symbol, double TimeSeconds)> symbols,
      IEnumerable<TruthUtterance> utterances)
    {
      var utts = utterances.ToList();
      var all = symbols.ToList();

      int correct = 0, absorbed = 0;
      var unmatched = new List<string>();
      foreach (var (sym, time) in all)
      {
        var near = utts.Where(u => Math.Abs(time - u.Time) <= TimeWindowS).ToList();
        if (near.Any(u => u.Symbols.Contains(sym))) correct++;
        else if (near.Any(u => u.Symbols.Contains('?'))) absorbed++;
        else unmatched.Add(sym.ToString());
      }

      int knownChars = 0, recalled = 0;
      var recovered = new List<string>();
      foreach (var u in utts)
      {
        var known = u.Symbols.Where(c => c != '?').ToList();
        knownChars += known.Count;
        var window = all.Where(s => Math.Abs(s.TimeSeconds - u.Time) <= TimeWindowS).ToList();
        int n = known.GroupBy(c => c).Sum(g => Math.Min(g.Count(), window.Count(s => s.Symbol == g.Key)));
        recalled += n;
        if (n == known.Count && known.Count > 0) recovered.Add(u.Symbols);
      }

      return new EvalScore
      {
        EmittedChars = all.Count - absorbed,
        CorrectChars = correct,
        GoldChars = knownChars,
        RecalledChars = recalled,
        RecoveredGold = recovered,
        Unmatched = unmatched
      };
    }

    public static EvalScore Score(IEnumerable<(char Symbol, double TimeSeconds)> symbols,
      IEnumerable<TruthIdentifier> truth)
    {
      var truths = truth.Where(t => t.Tag != TruthTag.Unintelligible).ToList();
      var gold = truths.Where(t => t.Tag == TruthTag.Gold).ToList();
      var all = symbols.ToList();

      int correct = 0;
      var unmatched = new List<string>();
      foreach (var (sym, time) in all)
      {
        bool ok = truths.Any(t => t.Text.Contains(sym) &&
          t.Times.Any(x => Math.Abs(time - x) <= TimeWindowS));
        if (ok) correct++; else unmatched.Add(sym.ToString());
      }

      // recall per Gold identifier: the best-covered single mention, chars counted with multiplicity
      int recalled = 0;
      var recovered = new List<string>();
      foreach (var t in gold)
      {
        int best = 0;
        foreach (double m in t.Times)
        {
          var window = all.Where(s => Math.Abs(s.TimeSeconds - m) <= TimeWindowS).ToList();
          int n = t.Text.GroupBy(c => c)
            .Sum(g => Math.Min(g.Count(), window.Count(s => s.Symbol == g.Key)));
          best = Math.Max(best, n);
        }
        recalled += best;
        if (best == t.Text.Length) recovered.Add(t.Text);
      }

      return new EvalScore
      {
        EmittedChars = all.Count,
        CorrectChars = correct,
        GoldChars = gold.Sum(t => t.Text.Length),
        RecalledChars = recalled,
        RecoveredGold = recovered,
        Unmatched = unmatched
      };
    }
  }
}
