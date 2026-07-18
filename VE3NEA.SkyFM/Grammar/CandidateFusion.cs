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
  /// a plain edit-distance clustering falsely unified them.</para>
  /// </summary>
  public static class CandidateFusion
  {
    private const int MinContainmentOverlap = 4;

    /// <summary>Fuse candidates gathered across all transmissions of a pass; returns one candidate per
    /// identifier cluster, in time order.</summary>
    public static IReadOnlyList<Candidate> Fuse(IEnumerable<Candidate> candidates)
    {
      var clusters = candidates.GroupBy(c => (c.Kind, c.Text)).Select(g => g.ToList()).ToList();
      var absorbed = new bool[clusters.Count];

      for (int i = 0; i < clusters.Count; i++)
      {
        if (absorbed[i] || clusters[i].Count != 1) continue;
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
        if (target != null) { target.Add(s); absorbed[i] = true; }
      }

      return clusters.Where((_, i) => !absorbed[i]).Select(Merge).OrderBy(c => c.StartSeconds).ToList();
    }

    /// <summary>One text inside the other with enough overlap to rule out coincidence.</summary>
    private static bool Contained(string a, string b)
      => Math.Min(a.Length, b.Length) >= MinContainmentOverlap && a != b && (a.Contains(b) || b.Contains(a));

    private static double Support(List<Candidate> cluster) => cluster.Sum(c => c.Confidence);

    private static Candidate Merge(List<Candidate> cluster)
    {
      if (cluster.Count == 1) return cluster[0];

      // the winning text: most mentions, then the longer string (truncation loses characters), then
      // summed confidence
      var best = cluster.GroupBy(c => c.Text)
        .OrderByDescending(g => g.Count())
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
