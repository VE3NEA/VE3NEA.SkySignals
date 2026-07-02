using System;

namespace VE3NEA.SkySSTV
{
  /// <summary>
  /// Separable zero-mean, unit-variance matched filter for the horizontal sync pulse (plan §4 decision
  /// 2026-07-01; supersedes the P3 coherence-centroid). The sync is a rank-1 separable patch on the
  /// (time, frequency) surface — the outer product of a frequency profile (1200 Hz, zero-mean across
  /// frequency) and a time profile (+1 over the mode's pulse length, negative over the flanks, zero-mean
  /// across time). It is realized separably:
  /// <list type="bullet">
  /// <item><b>Frequency axis</b> — a short-window 1200 Hz coherence track <c>g(t) ∈ [0, 0.5]</c>
  /// (<see cref="SstvToneBank"/>, O(1) per point via prefix sums). Because coherence already divides by
  /// window energy it is amplitude-invariant and, being ~0.5 only for a pure 1200 tone, carries the
  /// frequency zero-mean / broadband rejection implicitly.</item>
  /// <item><b>Time axis</b> — a bipolar boxcar template of the mode's <b>sync length</b> convolved with
  /// <c>g</c> (again O(1), from prefix sums of <c>g</c>): <c>score = mean(g over the pulse) −
  /// ½·mean(g over the equal flanks before+after)</c>. The flanks make the template zero-mean in time,
  /// which converts the flat-topped coherence into a <b>triangular peak</b> at the true onset — no
  /// centroid needed — and drives a constant carrier (no time contrast) to ~0.</item>
  /// </list>
  /// The template is tuned to the mode-specific pulse length, so a small bank (Robot 9 ms vs PD 20 ms)
  /// scores the sync-duration family for free — the discriminant the MHT (<see cref="SstvModeDetector"/>)
  /// uses. Shared by the KF1 tracker and the mode detector.
  /// </summary>
  internal sealed class SstvSyncFilter
  {
    private readonly double[] preG;   // prefix sum of g; preG[k] = Σ_{t<k} g[t]
    private readonly int len;

    /// <summary>Short coherence window (samples) for the frequency axis.</summary>
    public int FreqWindow { get; }

    /// <summary>Last onset for which a full pulse window fits in range.</summary>
    public int MaxPos(int pulseLen) => len - pulseLen;

    /// <summary>Build the 1200 Hz coherence track and its prefix sums over <paramref name="disc"/>.</summary>
    public SstvSyncFilter(double[] disc, double fs)
    {
      len = disc.Length;
      FreqWindow = Math.Max(8, (int)Math.Round(0.004 * fs));   // ~4 ms: several 1200 Hz cycles, < any sync
      var bank = new SstvToneBank(disc, fs, SstvTones.Sync, 0, len);

      int half = FreqWindow / 2;
      var g = new double[len];
      for (int t = half; t < len - half; t++)                  // guard: a clamped partial window reads spurious
        g[t] = bank.Coherence(t - half, t - half + FreqWindow);

      preG = new double[len + 1];
      for (int t = 0; t < len; t++) preG[t + 1] = preG[t] + g[t];
    }

    /// <summary>Matched-filter score for a sync of length <paramref name="pulseLen"/> whose onset is at
    /// <paramref name="t"/>: mean coherence over the pulse minus half the mean over the equal-length flanks
    /// before and after (zero-mean in time). ≈0.5 for a clean, correctly-sized pulse; ≤0 for none.</summary>
    public double Score(int t, int pulseLen)
    {
      double pulse = MeanG(t, t + pulseLen);
      double left = MeanG(t - pulseLen, t);
      double right = MeanG(t + pulseLen, t + 2 * pulseLen);
      return pulse - 0.5 * (left + right);
    }

    /// <summary>Locate the sync onset (score peak, parabola-refined) for <paramref name="pulseLen"/> within
    /// ±<paramref name="searchRad"/> of <paramref name="pred"/>. Returns false when the peak fails
    /// <paramref name="threshold"/> (coast).</summary>
    public bool FindPeak(double pred, int searchRad, int pulseLen, double threshold,
      out double pos, out double score)
    {
      int maxPos = MaxPos(pulseLen);
      int lo = Math.Max(0, (int)Math.Round(pred) - searchRad);
      int hi = Math.Min(maxPos, (int)Math.Round(pred) + searchRad);
      double best = double.NegativeInfinity; int bestPos = lo;
      for (int p = lo; p <= hi; p++)
      {
        double s = Score(p, pulseLen);
        if (s > best) { best = s; bestPos = p; }
      }

      pos = bestPos;
      score = best;
      if (best < threshold) return false;

      if (bestPos > 0 && bestPos < maxPos)
      {
        double sm = Score(bestPos - 1, pulseLen), sp = Score(bestPos + 1, pulseLen);
        double denom = sm - 2 * best + sp;
        if (denom < 0) pos = bestPos + 0.5 * (sm - sp) / denom;   // parabola vertex of the triangular peak
      }
      return true;
    }

    /// <summary>Mean of <c>g</c> over the absolute span [<paramref name="a"/>, <paramref name="b"/>),
    /// clamped to range; 0 for an empty span.</summary>
    private double MeanG(int a, int b)
    {
      if (a < 0) a = 0;
      if (b > len) b = len;
      int w = b - a;
      return w <= 0 ? 0 : (preG[b] - preG[a]) / w;
    }
  }
}
