using System;
using System.Collections.Generic;
using System.Linq;

namespace VE3NEA.SkyFM
{
  /// <summary>
  /// Cross-repeat (multi-mention) fusion at the identifier level (plan §5.4–5.5): identifiers recur
  /// within a pass, so mentions of the same identifier are clustered and corroboration raises
  /// confidence (soft-OR), while a lone mention keeps its raw confidence.
  ///
  /// <para>Clustering is deliberately conservative, tuned on the real ARISS pass: mentions cluster by
  /// EXACT text; only an *uncorroborated* singleton may then be absorbed — by text containment
  /// ("KR4JI" ⊂ "KR4JIQ", truncation is the common damage mode; overlap ≥ 4 chars), or by a single
  /// character slip ("KB3IW" → "KB2IW") *only* into a cluster with ≥ 2 mentions. Two independently
  /// repeated texts never merge: the pass really contained both AB2IW and KB2IW, one character apart —
  /// a plain edit-distance clustering falsely unified them. "Uncorroborated singleton" is
  /// utterance-based, not mention-based: in a hybrid run two engines hear the same utterance, and such
  /// same-time duplicates are one acoustic event, not independent repeats.</para>
  ///
  /// <para>The §5.4 rerank seam's first prior is callsign↔grid cross-validation: a callsign cluster
  /// whose text is a fused grid plus 1–2 leftover characters, every mention of which coincides with a
  /// mention of that grid, is the grid+junk glue artifact ("EM85KR" over the spoken "EM85 KR4JIQ" —
  /// an engine whose run held no separate grid span for the partition to anchor on), not a station.
  /// Cross-repeat corroboration cannot save it, because each repeat coincides with a grid repeat —
  /// the corroboration is FOR the grid. Precision-first: the residue ≤ 2 bound keeps any real
  /// grid-shaped callsign ("FN20ABC") out of the rule's reach.</para>
  /// </summary>
  public static class CandidateFusion
  {
    private const int MinContainmentOverlap = 4;
    private const int MaxGlueResidue = 2;
    private const double SameUtteranceS = 3.0;

    /// <summary>Fuse candidates gathered across all transmissions of a pass; returns one candidate per
    /// identifier cluster, in time order.</summary>
    public static IReadOnlyList<Candidate> Fuse(IEnumerable<Candidate> candidates)
    {
      var clusters = candidates.GroupBy(c => (c.Kind, c.Text)).Select(g => g.ToList()).ToList();
      var absorbed = new bool[clusters.Count];

      for (int i = 0; i < clusters.Count; i++)
      {
        if (absorbed[i] || !SingleUtterance(clusters[i])) continue;
        var s = clusters[i][0];

        List<Candidate>? target = null;
        for (int j = 0; j < clusters.Count; j++)
        {
          if (j == i || absorbed[j]) continue;
          var t = clusters[j];
          if (t[0].Kind != s.Kind) continue;
          bool ok = t.Any(m => Contained(m.Text, s.Text)) ||
                    (t.Count >= 2 && PhoneticDecoder.WithinEdit1(t[0].Text, s.Text));
          if (!ok) continue;
          if (target == null || Support(t) > Support(target)) target = t;
        }
        if (target != null) { target.AddRange(clusters[i]); absorbed[i] = true; }
      }

      // callsign↔grid cross-validation (see the class summary): glue artifacts die after absorption,
      // so any junk variants they soaked up die with them
      for (int i = 0; i < clusters.Count; i++)
      {
        if (absorbed[i] || clusters[i][0].Kind != CandidateKind.Callsign) continue;
        for (int j = 0; j < clusters.Count; j++)
          if (j != i && !absorbed[j] && IsGridGlue(clusters[i], clusters[j])) { absorbed[i] = true; break; }
      }

      return clusters.Where((_, i) => !absorbed[i]).Select(Merge).OrderBy(c => c.StartSeconds).ToList();
    }

    /// <summary>True when the callsign cluster is the grid+junk glue artifact of the grid cluster:
    /// its text is the grid's text plus a short residue, and each of its mentions coincides in time
    /// with a mention of the grid — no independent sighting exists.</summary>
    private static bool IsGridGlue(List<Candidate> callsign, List<Candidate> grid)
    {
      if (grid[0].Kind != CandidateKind.Grid) return false;
      string c = callsign[0].Text, g = grid[0].Text;
      if (c.Length - g.Length < 1 || c.Length - g.Length > MaxGlueResidue || !c.StartsWith(g)) return false;
      return callsign.All(m => grid.Any(gm => Math.Abs(m.StartSeconds - gm.StartSeconds) <= SameUtteranceS));
    }

    /// <summary>All mentions within one utterance window — one acoustic event (possibly heard by
    /// several engines), not independent corroboration.</summary>
    private static bool SingleUtterance(List<Candidate> cluster)
      => cluster.Max(c => c.StartSeconds) - cluster.Min(c => c.StartSeconds) <= SameUtteranceS;

    /// <summary>Distinct utterances among the mentions: times closer than the window collapse into
    /// one.</summary>
    private static int UtteranceCount(IEnumerable<Candidate> mentions)
    {
      int count = 0;
      double last = double.NegativeInfinity;
      foreach (var c in mentions.OrderBy(c => c.StartSeconds))
        if (c.StartSeconds - last > SameUtteranceS) { count++; last = c.StartSeconds; }
        else last = c.StartSeconds;
      return count;
    }

    /// <summary>One text inside the other with enough overlap to rule out coincidence.</summary>
    private static bool Contained(string a, string b)
      => Math.Min(a.Length, b.Length) >= MinContainmentOverlap && a != b && (a.Contains(b) || b.Contains(a));

    private static double Support(List<Candidate> cluster) => cluster.Sum(c => c.Confidence);

    private static Candidate Merge(List<Candidate> cluster)
    {
      if (cluster.Count == 1) return cluster[0];

      // the winning text: most distinct utterances (same-time engine duplicates are one vote), then
      // the longer string (truncation loses characters), then summed confidence
      var best = cluster.GroupBy(c => c.Text)
        .OrderByDescending(g => UtteranceCount(g))
        .ThenByDescending(g => g.Key.Length)
        .ThenByDescending(g => g.Sum(c => c.Confidence))
        .First();
      var rep = best.OrderByDescending(c => c.Confidence).First();

      // corroboration across independent mentions: soft-OR of member confidences
      double miss = 1.0;
      foreach (var c in cluster) miss *= 1.0 - Math.Min(0.99f, c.Confidence);

      return rep with
      {
        Confidence = (float)(1.0 - miss),
        StartSeconds = cluster.Min(c => c.StartSeconds),
        EndSeconds = cluster.Max(c => c.EndSeconds)
      };
    }
  }
}
