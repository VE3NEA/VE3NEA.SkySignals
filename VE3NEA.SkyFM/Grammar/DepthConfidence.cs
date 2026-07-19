using System;
using System.Collections.Generic;
using System.Linq;

namespace VE3NEA.SkyFM
{
  /// <summary>
  /// Squelch quieting depth as a confidence input (plan §5.2 role b, §5.5): candidates decoded from a
  /// shallow — weak-carrier — transmission carry less evidence than the same text heard under strong
  /// quieting, so their confidence is scaled down before fusion. Cross-repeat recovery is preserved by
  /// construction: a weak burst's mentions still join their cluster and the soft-OR, they just cannot
  /// push a candidate over the emit bar on their own — repeats at deeper bursts must corroborate.
  ///
  /// <para>The weight ramps linearly in dB from the kind's weight floor at <see cref="ShallowDb"/> to
  /// 1 at <see cref="FullDb"/>. The spike measured weak carriers dipping 4–5 dB against the noise
  /// ceiling; strong carriers quiet far deeper. An unknown depth (NaN, or the -1 file sentinel) never
  /// demotes. The floor is per kind, like the §5.5 emit thresholds and for the same reason: callsigns
  /// carry the precision problem, while the rigid grid structure already filters junk — the corpus
  /// sweep showed demoting grids only costs recall — so grids default to exempt
  /// (<see cref="GridMinWeight"/> = 1).</para>
  /// </summary>
  public sealed record DepthConfidence
  {
    /// <summary>Depth (dB) at or below which the weight bottoms out at the kind's floor.</summary>
    public double ShallowDb { get; init; } = 4.0;

    /// <summary>Depth (dB) at or above which the weight is 1 (full trust).</summary>
    public double FullDb { get; init; } = 14.0;

    /// <summary>Weight floor for callsign candidates from the shallowest transmissions
    /// (corpus-calibrated, sweep 2026-07-18).</summary>
    public float MinWeight { get; init; } = 0.7f;

    /// <summary>Weight floor for grid candidates; 1 exempts them (the calibrated default).</summary>
    public float GridMinWeight { get; init; } = 1.0f;

    /// <summary>Confidence multiplier for a candidate of the given kind heard in a transmission of the
    /// given quieting depth.</summary>
    public float Weight(double depthDb, CandidateKind kind = CandidateKind.Callsign)
    {
      float floor = kind == CandidateKind.Grid ? GridMinWeight : MinWeight;
      if (double.IsNaN(depthDb) || depthDb < 0) return 1f;
      if (depthDb >= FullDb) return 1f;
      if (depthDb <= ShallowDb) return floor;
      return floor + (1f - floor) * (float)((depthDb - ShallowDb) / (FullDb - ShallowDb));
    }

    /// <summary>Scale a transmission's candidates by their depth weight (overall and per-character
    /// confidence together, so the emit and partial-char gates see the same evidence).</summary>
    public IReadOnlyList<Candidate> Apply(IReadOnlyList<Candidate> candidates, double depthDb)
    {
      return candidates.Select(c =>
      {
        float w = Weight(depthDb, c.Kind);
        return w >= 1f ? c : c with
        {
          Confidence = c.Confidence * w,
          CharConfidence = c.CharConfidence.Select(p => p * w).ToArray()
        };
      }).ToList();
    }
  }
}
