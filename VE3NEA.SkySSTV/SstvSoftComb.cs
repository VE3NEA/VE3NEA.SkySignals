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
  /// <see cref="HitZ"/> on <see cref="ConfirmChecks"/> consecutive checks at a stable phase — the
  /// persistence gate: a real ridge is re-fed every line period and stays confirmed for tens of checks
  /// (04-18 burst: 55+; the 12_37 weak-transmission candidate: 21), while a noise extreme grazing the
  /// threshold wanders off within a few (measured: 3 checks at z 3.8–4.0 on the corpus' noise fires
  /// at 11_09 0–26 s and 11_29 ~450 s). The true mode out-scores its
  /// half-rate harmonic by √2 (the harmonic splits the same hits over two bins with half the per-bin
  /// noise averaging), so the caller seeds only the best-z hit.
  ///
  /// <para>All state is bounded: the rings (Σ periods over the mode table) and two running scalars per
  /// mode. The leak keeps every bin finite, so no re-anchoring is needed (retro N).</para>
  /// </summary>
  internal sealed class SstvSoftComb
  {
    internal const double LeakPerPeriod = 0.99;  // ring memory ≈ 100 line periods
    internal const double HitFactor = 1.8;       // threshold = HitFactor·E[noise max], see HitZ below
    private const double WarmupPeriods = 30;     // bins need this many periods before z is meaningful
    internal const int ConfirmChecks = 12;       // consecutive over-threshold checks before a hit (persistence)

    /// <summary>The comb's integration span in line periods (= 1/(1−λ)): a confirmed hit's evidence was
    /// accumulated over this many periods before its anchor, so the extractor back-dates the seeded train
    /// by this span (plan §4.1).</summary>
    internal static int MemoryPeriods => (int)Math.Round(1.0 / (1.0 - LeakPerPeriod));

    private sealed class ModeComb
    {
      public required SstvModeSpec Spec;
      public required double[] Ring;
      public required double SyncMs;             // the family whose score stream feeds this ring
      public required double HitZ;               // period-aware detection threshold (see ctor)
      public long Samples;                       // family samples folded in so far
      public int RunLength;                      // consecutive over-threshold checks (the persistence gate)
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
        // threshold). The per-ring threshold is HitFactor × that expectation; the factor also absorbs
        // the TIME exposure the instantaneous count misses — the leak redraws the ring every ~memory,
        // so over a whole pass the noise max is √(2·ln(N_eff·redraws)) (measured on the 24 s noise
        // control after variance normalization: z = 3.4 ≈ that prediction, vs 2.06 instantaneous).
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
    /// pooling multiplies the degrees of freedom. Before pooling, each ring's bins are divided by its
    /// touch-count variance factor √((1−λ^{2k})/(1−λ²)) (k = periods folded so far): a bin's leaky-sum
    /// variance fills toward the steady state at a rate set by its own period, so between family warm-up
    /// and leak steady state the rings' raw variances differ and a raw pool underestimates σ (measured:
    /// a z = 3.6 noise grazer at 13.2 s).</summary>
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
          double norm = TouchNorm(c);
          foreach (double v in c.Ring) { double nv = v / norm; sum += nv; sumSq += nv * nv; }
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
        double norm = TouchNorm(c);

        int peak = 0;
        for (int i = 1; i < period; i++) if (c.Ring[i] > c.Ring[peak]) peak = i;
        double z = (c.Ring[peak] / norm - mean) / sd;

        // half-rate-harmonic suppression: a ring whose period is 2× the true one shows TWO ridges — at
        // the phase and at phase + P/2 (in the leaky form both saturate to the fundamental's height, so
        // z alone cannot discriminate). A comparable mirror ridge marks this ring as the harmonic; the
        // true mode's own ring (single ridge) reports the hit.
        int mirror = (peak + period / 2) % period;
        if (c.Ring[mirror] / norm - mean > 0.6 * (c.Ring[peak] / norm - mean)) continue;

        // persistence gate: the peak must clear the threshold at a stable phase for ConfirmChecks
        // consecutive checks — a real ridge is re-fed every period and persists, a noise extreme
        // grazing the threshold wanders off within a few checks
        bool over = z >= c.HitZ
          && (c.RunLength == 0 || PhaseDist(peak, c.PendingPhase, period) < 0.05 * period);
        c.RunLength = over ? c.RunLength + 1 : 0;
        c.PendingPhase = peak;
        if (c.RunLength < ConfirmChecks) continue;

        long anchor = now - ((now - peak) % period + period) % period;   // latest onset at the peak phase
        if (best == null || z > best.Value.Z) best = new SstvCombHit(c.Spec.Mode, anchor, z);
      }
      return best;
    }

    /// <summary>Zero every ring of a sync family — called when a promoted train of that family retires.
    /// The rings then hold mostly that transmission's residue, and a strong burst's decaying ridge stays
    /// over threshold for ~ln(z₀/HitZ) comb memories after it ends (measured: Robot72-ring echoes firing
    /// 44–55 s after strong Robot36 bursts ended), re-seeding phantom trains. Touch counts reset too, so
    /// the warm-up gate re-engages while the bins refill from live noise.</summary>
    public void ResetFamily(double familyDurMs)
    {
      foreach (var c in combs)
      {
        if (Math.Abs(c.SyncMs - familyDurMs) > 0.5) continue;
        Array.Clear(c.Ring);
        c.Samples = 0;
        c.RunLength = 0;
        c.PendingPhase = 0;
      }
    }

    /// <summary>Touch-count variance normalizer for a ring's bins: after k touches a leaky sum of
    /// unit-variance noise has variance (1−λ^{2k})/(1−λ²) — dividing by its square root brings every
    /// ring to unit per-touch variance regardless of how far its leak has filled.</summary>
    private static double TouchNorm(ModeComb c)
    {
      double k = (double)c.Samples / c.Ring.Length;
      return Math.Sqrt((1 - Math.Pow(LeakPerPeriod, 2 * k)) / (1 - LeakPerPeriod * LeakPerPeriod));
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
