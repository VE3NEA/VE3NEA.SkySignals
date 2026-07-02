using System;
using System.Collections.Generic;

namespace VE3NEA.SkyTlm.Dsp
{
  /// <summary>
  /// Robust noise-floor statistic shared by the batch and streaming detection paths: the interquartile
  /// (25th–75th percentile) trimmed mean. Versus the median it keeps the robustness that matters here —
  /// up to a quarter of the samples may be contaminated by signal/interference at the top end — while
  /// roughly halving the estimator variance, so detection thresholds wobble less on short recordings.
  /// </summary>
  internal static class NoiseFloor
  {
    /// <summary>
    /// Rescales the interquartile mean of <b>exponentially distributed</b> values (raw periodogram bin
    /// powers) to the value the median estimator would have given: median = λ·ln2 ≈ 0.6931λ while the
    /// interquartile mean is ≈ 0.7384λ, so multiply by ln2/0.7384. The detection thresholds and
    /// noise-subtraction levels were calibrated against the median — this keeps that calibration intact.
    /// Per-frame <i>mean</i> OOB powers (already an average over many bins, hence near-symmetric) need no
    /// correction: there median ≈ mean ≈ trimmed mean.
    /// </summary>
    public const double ExponentialMedianScale = 0.93866;

    /// <summary>Interquartile trimmed mean of <paramref name="x"/>[0..count): sorts that range in place
    /// and returns the mean of the values between the 25th and 75th percentiles.</summary>
    public static double TrimmedMeanInPlace(float[] x, int count)
    {
      if (count <= 0) return 0;
      Array.Sort(x, 0, count);
      int lo = count / 4, hi = count - count / 4;   // empty trim for count < 4 → plain mean
      double s = 0;
      for (int i = lo; i < hi; i++) s += x[i];
      return s / (hi - lo);
    }

    /// <summary>Interquartile trimmed mean of a list (copies, so the caller's order is preserved).</summary>
    public static double TrimmedMean(List<float> x)
    {
      var copy = x.ToArray();
      return TrimmedMeanInPlace(copy, copy.Length);
    }
  }
}
