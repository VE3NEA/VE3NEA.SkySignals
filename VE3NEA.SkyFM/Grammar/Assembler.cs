using System;
using System.Collections.Generic;

namespace VE3NEA.SkyFM
{
  /// <summary>
  /// Assembles identifier candidates from one transmission's recognized words (plan §5.4), ported from
  /// the spike-validated <c>grammar.py</c>: phonetic words map to a symbol run (<see
  /// cref="PhoneticDecoder"/>), a long pause or an ordinary English word ends the run, and each run is
  /// scanned for the longest structurally valid callsign and grid square. Collapsed identifier-shaped
  /// tokens ("AB2IW") are emitted directly. Per-character confidence is carried from the source words
  /// (plan §5.6). The spike measured this exact logic precision-safe: no Whisper noise hallucination
  /// survives the structural filter.
  /// </summary>
  public sealed class Assembler
  {
    private readonly double gapSeconds;

    /// <param name="gapSeconds">A silence longer than this (s) between phonetic words ends the spoken
    /// identifier (an over never pauses this long mid-callsign).</param>
    public Assembler(double gapSeconds = 1.5)
    {
      this.gapSeconds = gapSeconds;
    }

    /// <summary>Extract candidates from one hypothesis' word sequence, in time order, deduplicated
    /// (first occurrence of each distinct identifier wins).</summary>
    public IReadOnlyList<Candidate> Assemble(IReadOnlyList<AsrWord> words)
    {
      var candidates = new List<Candidate>();
      var run = new List<(char Sym, float Conf, double Start, double End)>();
      double lastT = double.NegativeInfinity;

      foreach (var word in words)
      {
        double t = (word.StartSeconds + word.EndSeconds) / 2;
        string au = PhoneticDecoder.Alnum(word.Text);
        bool complete = CallsignParser.IsValid(au) || MaidenheadParser.IsValid(au);

        if (complete)
        {
          // a whole callsign/grid token stands alone
          Flush(run, candidates);
          EmitDirect(au, word, candidates);
          lastT = t;
        }
        else
        {
          string sym = PhoneticDecoder.ToSymbols(word.Text);
          if (sym.Length > 0)
          {
            if (run.Count > 0 && t - lastT > gapSeconds) Flush(run, candidates);
            foreach (char c in sym) run.Add((c, word.Confidence, word.StartSeconds, word.EndSeconds));
            lastT = t;
          }
          else
            Flush(run, candidates);
        }
      }
      Flush(run, candidates);

      return Dedupe(candidates);
    }


    // ----------------------------------------------------------------------------------------------------
    //                                        candidate emission
    // ----------------------------------------------------------------------------------------------------

    /// <summary>Scan the finished symbol run for the best non-overlapping partition into valid
    /// identifiers — maximum covered characters, then fewest identifiers (precision-first) — then clear
    /// the run. A merged run holds identifiers back to back ("EM85 KR4JIQ", "AB2IW FN22",
    /// "KB2IW K2HZV"), so independent longest-match scans let a concatenation ("EM85KR", "AB2IWFN",
    /// "KB2IWK") shadow the true parse and emit junk-suffix candidates.</summary>
    private static void Flush(List<(char Sym, float Conf, double Start, double End)> run, List<Candidate> candidates)
    {
      if (run.Count == 0) return;

      var chars = new char[run.Count];
      for (int i = 0; i < run.Count; i++) chars[i] = run[i].Sym;
      string s = new string(chars);

      // all structurally valid spans of either kind (identifiers are ≥ 2 chars by both grammars), plus
      // run-end truncated-callsign spans ("KR4" — complete prefix, suffix cut off): a partial is
      // trusted only when it continues directly from a completed GRID (the glue-forward damage mode:
      // "EM85KR4…" truncated by a separator must parse EM85|KR4, not the junk callsign EM85KR), never
      // on its own — a bare noise run "AB2" still emits nothing, and never after a callsign, where the
      // extra coverage would fragment a longer valid parse (KR4JI must not split into KR4J|I23)
      var spans = new List<(int At, int Len, CandidateKind Kind, bool Partial)>();
      for (int i = 0; i < s.Length; i++)
        for (int j = s.Length; j > i + 1; j--)
        {
          string sub = s[i..j];
          if (CallsignParser.IsValid(sub)) spans.Add((i, j - i, CandidateKind.Callsign, false));
          if (MaidenheadParser.IsValid(sub)) spans.Add((i, j - i, CandidateKind.Grid, false));
          if (j == s.Length && i > 0 && CallsignParser.IsTruncated(sub))
            spans.Add((i, j - i, CandidateKind.Callsign, true));
        }

      // dynamic program over run positions: best[i] covers s[0..i), preferring more covered chars,
      // then fewer identifiers, then more grid-covered chars (the rigid 4-char grid outweighs the loose
      // callsign grammar: "EM85KR4JIQ" must split EM85|KR4JIQ, not EM85K|R4JIQ), then longer earlier
      // spans (larger Prev: junk glues forward onto the next identifier — "KB2IWK2HZV" must split
      // KB2IW|K2HZV, not KB2I|WK2HZV); Prev/Span chain the chosen partition for the walk-back
      var best = new (int Cover, int Count, int GridChars, int Span, int Prev)[s.Length + 1];
      for (int i = 1; i <= s.Length; i++)
      {
        best[i] = best[i - 1] with { Span = -1, Prev = i - 1 };
        for (int k = 0; k < spans.Count; k++)
        {
          var sp = spans[k];
          if (sp.At + sp.Len != i) continue;
          if (sp.Partial && (best[sp.At].Span < 0 ||
              spans[best[sp.At].Span].Kind != CandidateKind.Grid)) continue;
          int cover = best[sp.At].Cover + sp.Len, count = best[sp.At].Count + 1;
          int gridChars = best[sp.At].GridChars + (sp.Kind == CandidateKind.Grid ? sp.Len : 0);
          if (cover > best[i].Cover || (cover == best[i].Cover &&
              (count < best[i].Count || (count == best[i].Count &&
              (gridChars > best[i].GridChars || (gridChars == best[i].GridChars && sp.At > best[i].Prev))))))
            best[i] = (cover, count, gridChars, k, sp.At);
        }
      }

      var chosen = new List<(int At, int Len, CandidateKind Kind, bool Partial)>();
      for (int i = s.Length; i > 0; i = best[i].Prev)
        if (best[i].Span >= 0) chosen.Add(spans[best[i].Span]);
      for (int k = chosen.Count - 1; k >= 0; k--)
        candidates.Add(FromRun(run, chosen[k].At, chosen[k].Len, chosen[k].Kind));
      run.Clear();
    }

    private static Candidate FromRun(List<(char Sym, float Conf, double Start, double End)> run,
      int at, int len, CandidateKind kind)
    {
      Span<char> chars = stackalloc char[len];
      var conf = new float[len];
      double start = double.PositiveInfinity, end = double.NegativeInfinity, mean = 0;
      for (int i = 0; i < len; i++)
      {
        var c = run[at + i];
        chars[i] = c.Sym;
        conf[i] = c.Conf;
        mean += c.Conf;
        start = Math.Min(start, c.Start);
        end = Math.Max(end, c.End);
      }
      return new Candidate
      {
        Kind = kind,
        Text = new string(chars),
        CharConfidence = conf,
        Confidence = (float)(mean / len),
        StartSeconds = start,
        EndSeconds = end
      };
    }

    private static void EmitDirect(string au, AsrWord word, List<Candidate> candidates)
    {
      var conf = new float[au.Length];
      Array.Fill(conf, word.Confidence);
      candidates.Add(new Candidate
      {
        Kind = MaidenheadParser.IsValid(au) ? CandidateKind.Grid : CandidateKind.Callsign,
        Text = au,
        CharConfidence = conf,
        Confidence = word.Confidence,
        StartSeconds = word.StartSeconds,
        EndSeconds = word.EndSeconds
      });
    }

    /// <summary>First occurrence of each distinct (kind, text) wins; order preserved.</summary>
    private static IReadOnlyList<Candidate> Dedupe(List<Candidate> candidates)
    {
      var seen = new HashSet<(CandidateKind, string)>();
      var result = new List<Candidate>(candidates.Count);
      foreach (var c in candidates)
        if (seen.Add((c.Kind, c.Text))) result.Add(c);
      return result;
    }
  }
}
