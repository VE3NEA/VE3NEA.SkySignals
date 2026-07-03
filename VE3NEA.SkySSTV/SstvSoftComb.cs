using System;
using System.Collections.Generic;

namespace VE3NEA.SkySSTV
{
  /// <summary>One soft-comb detection: the mode whose comb fired, the absolute sample of the most recent
  /// line onset at the comb's peak phase, and the peak's z-score over the ring bins.</summary>
  internal readonly record struct SstvCombHit(SstvMode Mode, long AnchorSample, double Z);

  /// <summary>
  /// Streaming soft-comb accumulator (plan §4.1 "next action 1"; statistic validated offline 2026-07-03,
  /// <c>Real_SoftCombProbe</c>: burst z = 4.5 vs noise ≤ 2.6 on the 04-18 hardest case, where single-pulse
  /// scores are non-separable). Integrates the pulse detector's <b>un-thresholded</b> matched-filter score
  /// across line periods: one leaky ring of <c>period</c> bins per mode, fed by that mode's sync-family
  /// score stream — <c>ring[t mod P] = λ·ring + score</c>. Each bin is touched exactly once per period, so
  /// the touch-time leak is a per-period decay: memory ≈ 1/(1−λ) ≈ 100 periods, the scale the offline
  /// validation used. A periodic train buried below the single-pulse threshold accumulates a coherent
  /// ridge at its phase; noise stays flat. <see cref="Check"/> scans each ring for its peak z-score
  /// (O(P) per mode per block — cheap at block rate) and reports a hit when the peak clears
  /// <see cref="HitZ"/> on two consecutive checks (stability gate). The true mode out-scores its
  /// half-rate harmonic by √2 (the harmonic splits the same hits over two bins with half the per-bin
  /// noise averaging), so the caller seeds only the best-z hit.
  ///
  /// <para>All state is bounded: the rings (Σ periods over the mode table) and two running scalars per
  /// mode. The leak keeps every bin finite, so no re-anchoring is needed (retro N).</para>
  /// </summary>
  internal sealed class SstvSoftComb
  {
    internal const double LeakPerPeriod = 0.99;  // ring memory ≈ 100 line periods
    internal const double HitFactor = 1.6;       // threshold = HitFactor·E[noise max], see HitZ below
    private const double WarmupPeriods = 30;     // bins need this many periods before z is meaningful

    private sealed class ModeComb
    {
      public required SstvModeSpec Spec;
      public required double[] Ring;
      public required double SyncMs;             // the family whose score stream feeds this ring
      public required double HitZ;               // period-aware detection threshold (see ctor)
      public long Samples;                       // family samples folded in so far
      public double PendingZ;                    // last check's peak z (the 2-consecutive stability gate)
      public int PendingPhase;
    }

    private readonly List<ModeComb> combs = new();

    public SstvSoftComb(double fs)
    {
      foreach (var spec in SstvModes.All)
      {
        int period = (int)Math.Round(spec.LinePeriodMs / 1000.0 * fs);
        // the score stream is correlated over ~2× the sync-template length, so a ring holds
        // N_eff ≈ period / (2L) independent bins and its noise maximum scales as √(2·ln N_eff)
        // (extreme-value statistics — measured: Robot36 noise max ≈ 2.4, Robot72 ≈ 3.6 under a flat 3.5
        // threshold). The per-ring threshold is HitFactor × that expectation.
        double corr = 2.0 * spec.SyncMs / 1000.0 * fs;
        double nEff = Math.Max(4.0, period / corr);
        combs.Add(new ModeComb
        {
          Spec = spec,
          Ring = new double[period],
          SyncMs = spec.SyncMs,
          HitZ = HitFactor * Math.Sqrt(2.0 * Math.Log(nEff))
        });
      }
    }

    /// <summary>Fold one score sample from the <paramref name="familyDurMs"/> detector into every mode
    /// ring of that sync family. <paramref name="t"/> is the absolute input-sample onset the score is
    /// for (the detectors' fixed latency cancels in the phase arithmetic as long as it is constant).</summary>
    public void Process(double familyDurMs, long t, double score)
    {
      if (t < 0) return;
      foreach (var c in combs)
      {
        if (Math.Abs(c.SyncMs - familyDurMs) > 0.5) continue;
        int bin = (int)(t % c.Ring.Length);
        c.Ring[bin] = LeakPerPeriod * c.Ring[bin] + score;
        c.Samples++;
      }
    }

    /// <summary>Scan every warm ring for its peak z-score; return the best hit at or above the ring's
    /// period-aware threshold, confirmed by two consecutive checks (peak z over threshold twice, at a
    /// stable phase), or null. <paramref name="now"/> is the absolute sample time of the scan.
    /// The z normalization (mean/σ) is <b>pooled across each family's rings</b>: one ring holds only
    /// ~period/2L independent noise samples, so its self-estimated σ has Student-t tails that fire falsely;
    /// the family's rings accumulate identically-distributed noise (same leak, same per-touch σ), and
    /// pooling multiplies the degrees of freedom.</summary>
    public SstvCombHit? Check(long now)
    {
      // pooled noise statistics per sync family — only once EVERY ring of the family is warm: a partial
      // pool degenerates to one ring's few effective samples, whose Student-t σ tails fire falsely
      // (measured: a z = 3.3 noise grazer at 7.0 s, between the Robot36 and Robot72 warm-up times)
      var famStats = new Dictionary<double, (double mean, double sd)>();
      foreach (double fam in FamilyKeys())
      {
        double sum = 0, sumSq = 0;
        long n = 0;
        bool allWarm = true;
        foreach (var c in combs)
        {
          if (Math.Abs(c.SyncMs - fam) > 0.5) continue;
          if (c.Samples < WarmupPeriods * c.Ring.Length) { allWarm = false; break; }
          foreach (double v in c.Ring) { sum += v; sumSq += v * v; }
          n += c.Ring.Length;
        }
        if (!allWarm || n == 0) continue;
        double mean = sum / n;
        famStats[fam] = (mean, Math.Sqrt(Math.Max(0, sumSq / n - mean * mean)));
      }

      SstvCombHit? best = null;
      foreach (var c in combs)
      {
        int period = c.Ring.Length;
        if (c.Samples < WarmupPeriods * period) continue;
        if (!famStats.TryGetValue(c.SyncMs, out var stats) || stats.sd <= 0) continue;
        double mean = stats.mean, sd = stats.sd;

        int peak = 0;
        for (int i = 1; i < period; i++) if (c.Ring[i] > c.Ring[peak]) peak = i;
        double z = (c.Ring[peak] - mean) / sd;

        // half-rate-harmonic suppression: a ring whose period is 2× the true one shows TWO ridges — at
        // the phase and at phase + P/2 (in the leaky form both saturate to the fundamental's height, so
        // z alone cannot discriminate). A comparable mirror ridge marks this ring as the harmonic; the
        // true mode's own ring (single ridge) reports the hit.
        int mirror = (peak + period / 2) % period;
        if (c.Ring[mirror] - mean > 0.6 * (c.Ring[peak] - mean)) continue;

        // stability gate: the previous check must have cleared the threshold at (nearly) the same phase
        bool confirmed = z >= c.HitZ && c.PendingZ >= c.HitZ
          && PhaseDist(peak, c.PendingPhase, period) < 0.05 * period;
        c.PendingZ = z;
        c.PendingPhase = peak;
        if (!confirmed) continue;

        long anchor = now - ((now - peak) % period + period) % period;   // latest onset at the peak phase
        if (best == null || z > best.Value.Z) best = new SstvCombHit(c.Spec.Mode, anchor, z);
      }
      return best;
    }

    private IEnumerable<double> FamilyKeys()
    {
      var seen = new List<double>();
      foreach (var c in combs)
        if (!seen.Contains(c.SyncMs)) { seen.Add(c.SyncMs); yield return c.SyncMs; }
    }

    private static int PhaseDist(int a, int b, int period)
    {
      int d = Math.Abs(a - b);
      return Math.Min(d, period - d);
    }
  }
}
