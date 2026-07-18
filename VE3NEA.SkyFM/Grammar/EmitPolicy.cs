using System.Collections.Generic;

namespace VE3NEA.SkyFM
{
  /// <summary>Per-kind thresholds: fused confidence at or above <paramref name="Emit"/> emits the
  /// complete identifier; below it, characters under <paramref name="Char"/> become '?'.</summary>
  public sealed record KindThresholds(float Emit, float Char);

  /// <summary>
  /// The emit/abstain/partial policy of plan §5.5, applied after <see cref="CandidateFusion"/>: a
  /// complete identifier is emitted only when its fused confidence clears the emit threshold; below it
  /// the candidate degrades to a partial that keeps only the individually confident characters
  /// ('?' elsewhere — "W1A?", "?N03"), and with fewer than <see cref="MinKnownChars"/> confident
  /// characters left it abstains entirely. Cross-repeat / cross-engine corroboration (soft-OR) is what
  /// lifts a candidate over the emit bar, so an uncorroborated flat-confidence engine hypothesis
  /// (e.g. sherpa's placeholder 0.80, plan §5.3 finding a) cannot emit a complete callsign.
  ///
  /// <para>Thresholds are per kind because the corpus calibrates them apart (§5.5 as-built): callsigns
  /// carry the precision problem and want a high bar; grids are precise already (the rigid AA00
  /// structure filters junk) and only need enough policy to drop floor-confidence artifacts.</para>
  /// </summary>
  public sealed class EmitPolicy
  {
    /// <summary>Corpus-calibrated callsign operating point (sweep 2026-07-18).</summary>
    public KindThresholds Callsigns { get; init; } = new(0.85f, 0.85f);

    /// <summary>Corpus-calibrated grid operating point (sweep 2026-07-18).</summary>
    public KindThresholds Grids { get; init; } = new(0.75f, 0.70f);

    /// <summary>A partial with fewer known (non-'?') characters than this abstains.</summary>
    public int MinKnownChars { get; init; } = 2;

    /// <summary>Apply the policy to fused candidates, preserving order; abstained candidates are
    /// dropped, partials keep their original per-character confidences.</summary>
    public IReadOnlyList<Candidate> Apply(IEnumerable<Candidate> candidates)
    {
      var result = new List<Candidate>();
      foreach (var c in candidates)
      {
        var t = c.Kind == CandidateKind.Grid ? Grids : Callsigns;
        if (c.Confidence >= t.Emit) { result.Add(c); continue; }

        var chars = c.Text.ToCharArray();
        int known = 0;
        for (int i = 0; i < chars.Length; i++)
          if (c.CharConfidence[i] >= t.Char) known++;
          else chars[i] = '?';

        if (known >= MinKnownChars) result.Add(c with { Text = new string(chars) });
      }
      return result;
    }
  }
}
