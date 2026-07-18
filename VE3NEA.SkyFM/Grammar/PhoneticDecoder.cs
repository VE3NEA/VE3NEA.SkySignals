using System;
using System.Collections.Generic;
using System.Text;

namespace VE3NEA.SkyFM
{
  /// <summary>
  /// Maps one raw transcript word to its identifier-symbol contribution (plan §5.4), ported from the
  /// spike's validated <c>grammar.py</c>: NATO words (with edit-distance-1 fuzz), spoken digits
  /// ("niner", "fife"), digit strings, and collapsed callsign/grid-shaped fragments become symbols;
  /// everything else is a separator. Bare spoken letters ("bee", "you", "oh") are deliberately NOT
  /// mapped — treating them as symbols swallows ordinary English words, the spike's measured precision
  /// trap.
  /// </summary>
  public static class PhoneticDecoder
  {
    private static readonly Dictionary<string, char> Nato = new()
    {
      ["alpha"] = 'A', ["alfa"] = 'A', ["bravo"] = 'B', ["charlie"] = 'C', ["delta"] = 'D',
      ["echo"] = 'E', ["foxtrot"] = 'F', ["fox"] = 'F', ["golf"] = 'G', ["hotel"] = 'H',
      ["india"] = 'I', ["juliet"] = 'J', ["juliett"] = 'J', ["kilo"] = 'K', ["lima"] = 'L',
      ["mike"] = 'M', ["november"] = 'N', ["oscar"] = 'O', ["papa"] = 'P', ["quebec"] = 'Q',
      ["romeo"] = 'R', ["sierra"] = 'S', ["tango"] = 'T', ["uniform"] = 'U', ["victor"] = 'V',
      ["whiskey"] = 'W', ["xray"] = 'X', ["yankee"] = 'Y', ["zulu"] = 'Z'
    };

    private static readonly Dictionary<string, char> Digits = new()
    {
      ["zero"] = '0', ["one"] = '1', ["two"] = '2', ["three"] = '3', ["four"] = '4',
      ["five"] = '5', ["fife"] = '5', ["six"] = '6', ["seven"] = '7', ["eight"] = '8',
      ["nine"] = '9', ["niner"] = '9'
    };

    /// <summary>The closed spoken vocabulary the decoder maps to symbols — NATO words and spoken
    /// digits. This is the source of truth for Pass-B constrained grammars / hotword lists (§5.3).</summary>
    public static IEnumerable<string> VocabularyWords
    {
      get
      {
        foreach (string w in Nato.Keys) yield return w;
        foreach (string w in Digits.Keys) yield return w;
      }
    }

    /// <summary>The symbols (upper-case letters/digits) <paramref name="word"/> contributes to an
    /// identifier, or "" when the word is a separator (ordinary English).</summary>
    public static string ToSymbols(string word)
    {
      string w = Normalize(word);
      if (w.Length == 0) return "";
      if (Nato.TryGetValue(w, out char ltr)) return ltr.ToString();
      if (Digits.TryGetValue(w, out char dig)) return dig.ToString();

      // fuzzy NATO (edit distance <= 1) — catches 'juliett', 'quebeck', minor slips
      foreach (var (name, c) in Nato)
        if (name.Length >= 4 && WithinEdit1(w, name)) return c.ToString();

      string au = Alnum(word);
      if (au.Length == 0) return "";
      bool allDigits = true;
      bool anyDigit = false;
      foreach (char c in au)
      {
        if (char.IsDigit(c)) anyDigit = true; else allDigits = false;
      }
      if (allDigits) return au;                              // '85' -> '85'
      // collapsed callsign/grid fragment ('AB2IW', 'K2', 'HZV' is excluded: no digit, not id-shaped)
      if (CallsignParser.IsValid(au) || MaidenheadParser.IsValid(au) || anyDigit) return au;
      return "";                                             // separator
    }

    /// <summary>Lower-case, with leading/trailing punctuation and whitespace stripped (interior
    /// characters kept).</summary>
    public static string Normalize(string word)
    {
      int s = 0, e = word.Length;
      while (s < e && !char.IsLetterOrDigit(word[s])) s++;
      while (e > s && !char.IsLetterOrDigit(word[e - 1])) e--;
      return word[s..e].ToLowerInvariant();
    }

    /// <summary>Upper-case alphanumeric characters of <paramref name="word"/> only.</summary>
    public static string Alnum(string word)
    {
      var sb = new StringBuilder(word.Length);
      foreach (char c in word)
        if (char.IsAsciiLetterOrDigit(c)) sb.Append(char.ToUpperInvariant(c));
      return sb.ToString();
    }

    /// <summary>True when the edit distance between <paramref name="a"/> and <paramref name="b"/> is
    /// at most 1 (one substitution, insertion, or deletion).</summary>
    public static bool WithinEdit1(string a, string b)
    {
      if (Math.Abs(a.Length - b.Length) > 1) return false;
      if (a == b) return true;
      if (a.Length > b.Length) (a, b) = (b, a);
      int i = 0, j = 0, diff = 0;
      while (i < a.Length && j < b.Length)
      {
        if (a[i] == b[j]) { i++; j++; }
        else
        {
          if (++diff > 1) return false;
          j++;
          if (a.Length == b.Length) i++;
        }
      }
      return true;
    }
  }
}
