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

    /// <summary>Scan the finished symbol run for the single longest callsign and single longest grid
    /// full-match (a spoken ID is one contiguous sub-run, not every substring of a merge), then clear
    /// the run.</summary>
    private static void Flush(List<(char Sym, float Conf, double Start, double End)> run, List<Candidate> candidates)
    {
      if (run.Count == 0) return;

      var chars = new char[run.Count];
      for (int i = 0; i < run.Count; i++) chars[i] = run[i].Sym;
      string s = new string(chars);

      (int At, int Len) call = (0, 0), grid = (0, 0);
      for (int i = 0; i < s.Length; i++)
        for (int j = s.Length; j > i + 1; j--)
        {
          string sub = s[i..j];
          if (sub.Length > call.Len && CallsignParser.IsValid(sub)) call = (i, sub.Length);
          if (sub.Length > grid.Len && MaidenheadParser.IsValid(sub)) grid = (i, sub.Length);
        }

      if (call.Len > 0) candidates.Add(FromRun(run, call.At, call.Len, CandidateKind.Callsign));
      if (grid.Len > 0) candidates.Add(FromRun(run, grid.At, grid.Len, CandidateKind.Grid));
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
