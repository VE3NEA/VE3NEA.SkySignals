using System.Collections.Generic;

namespace VE3NEA.SkyFM
{
  public enum CandidateKind { Callsign, Grid }

  /// <summary>
  /// One extracted identifier candidate (plan §5.6): the string with per-character certainty, an overall
  /// confidence, and the supporting time span — never a bare string. Confidence here is the raw acoustic
  /// path evidence (mean per-char word confidence); calibration and the emit/abstain/partial policy are
  /// the fusion stage's job.
  /// </summary>
  public sealed record Candidate
  {
    public required CandidateKind Kind { get; init; }
    public required string Text { get; init; }

    /// <summary>Per-character acoustic confidence, from the transcript words each character came
    /// from.</summary>
    public required IReadOnlyList<float> CharConfidence { get; init; }

    public required float Confidence { get; init; }
    public required double StartSeconds { get; init; }
    public required double EndSeconds { get; init; }
  }
}
