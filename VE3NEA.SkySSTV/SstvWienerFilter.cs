using System;

namespace VE3NEA.SkySSTV
{
  /// <summary>
  /// Wiener (Lee) post-filter on the reconstructed Y/Cr/Cb planes (plan §6.2): local mean μ and
  /// variance σ²loc over a small window, gain g = max(0, σ²loc − k·σ²n)/σ²loc, output μ + g·(x − μ) —
  /// noise-dominated areas collapse to their local mean (contrast reduction, smoothly ramped) while
  /// real edges and text pass at g ≈ 1. Runs before the YCrCb→RGB conversion, where Robot36's
  /// alternating chroma is still separate.
  ///
  /// Defaults locked by the P6(d) visual judgment (2026-07-04, <c>Real_P6dWienerProbe</c>): window
  /// 9×5, chroma noise over-weight k = 4, no shrink-to-neutral, image-domain noise map — the row-wise
  /// vertical first-difference median estimator (scan lines are independent time slices, so inter-line
  /// differences carry the full noise power even where the post-LPF FM noise is horizontally
  /// correlated; the plan's Immerkær residual read several× low on exactly that noise and was
  /// dropped). Every operation is row-local with a ≤2-line lag (window rows + the 5-row median), so
  /// the P7.5 push-based decoder can run it at line emission.
  /// </summary>
  internal static class SstvWienerFilter
  {
    private const int WindowW = 9;                           // local window, pixels × lines
    private const int WindowH = 5;
    private const double ChromaK = 4.0;                      // chroma noise over-weight (plan §6.2)

    /// <summary>Filter the three planes in place. Chroma noise is estimated over a 2-row step because
    /// Robot36/PD chroma rows are nearest-neighbor duplicates (vertical upsampling).</summary>
    public static void Apply(double[] y, double[] cr, double[] cb, int w, int h)
      => Apply(y, cr, cb, w, h, null);

    /// <summary>Filter variant capturing the luma plane's per-pixel Wiener gain (plan §6.2: the
    /// confidence that goes into the image's alpha channel — g ≈ 1 where real detail passed, g ≈ 0
    /// where the pixel collapsed to its local mean). <paramref name="yGain"/> must hold w·h values.</summary>
    public static void Apply(double[] y, double[] cr, double[] cb, int w, int h, double[]? yGain)
    {
      Lee(y, w, h, RowNoiseVar(y, w, h, 1), 1.0, yGain);
      Lee(cr, w, h, RowNoiseVar(cr, w, h, 2), ChromaK, null);
      Lee(cb, w, h, RowNoiseVar(cb, w, h, 2), ChromaK, null);
    }

    /// <summary>Per-row noise variance: σ = median_x|p[y] − p[y−step]| / 0.6745 / √2 (the Gaussian
    /// median-absolute-deviation of a two-row difference), then median-of-5 smoothed across rows so
    /// content-heavy rows (horizontal edges) do not spike the estimate.</summary>
    private static double[] RowNoiseVar(double[] p, int w, int h, int step)
    {
      var sigma = new double[h];
      var absd = new double[w];
      for (int y = 0; y < h; y++)
      {
        int y2 = y >= step ? y - step : y + step;
        if (y2 >= h) continue;
        for (int x = 0; x < w; x++) absd[x] = Math.Abs(p[y * w + x] - p[y2 * w + x]);
        Array.Sort(absd);
        sigma[y] = absd[w / 2] / 0.6745 / Math.Sqrt(2.0);
      }

      var v = new double[h];
      var win = new double[5];
      for (int y = 0; y < h; y++)
      {
        int cnt = 0;
        for (int d = -2; d <= 2; d++)
          if (y + d >= 0 && y + d < h) win[cnt++] = sigma[y + d];
        Array.Sort(win, 0, cnt);
        double med = win[cnt / 2];
        v[y] = med * med;
      }
      return v;
    }

    private static void Lee(double[] p, int w, int h, double[] rowVar, double k, double[]? gain = null)
    {
      // 2D prefix sums of x and x² give O(1) window mean/variance
      var s1 = new double[(w + 1) * (h + 1)];
      var s2 = new double[(w + 1) * (h + 1)];
      for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
          double v = p[y * w + x];
          int i = (y + 1) * (w + 1) + x + 1;
          s1[i] = v + s1[i - 1] + s1[i - w - 1] - s1[i - w - 2];
          s2[i] = v * v + s2[i - 1] + s2[i - w - 1] - s2[i - w - 2];
        }

      int rx = WindowW / 2, ry = WindowH / 2;
      var outp = new double[w * h];
      for (int y = 0; y < h; y++)
      {
        double vn = k * rowVar[y];
        int y0 = Math.Max(0, y - ry), y1 = Math.Min(h - 1, y + ry);
        for (int x = 0; x < w; x++)
        {
          int x0 = Math.Max(0, x - rx), x1 = Math.Min(w - 1, x + rx);
          double n = (x1 - x0 + 1) * (y1 - y0 + 1);
          double mu = Box(s1, w, x0, y0, x1, y1) / n;
          double varLoc = Math.Max(0, Box(s2, w, x0, y0, x1, y1) / n - mu * mu);
          double g = varLoc > vn ? (varLoc - vn) / varLoc : 0.0;
          if (gain != null) gain[y * w + x] = g;
          outp[y * w + x] = mu + g * (p[y * w + x] - mu);
        }
      }
      Array.Copy(outp, p, p.Length);
    }

    private static double Box(double[] s, int w, int x0, int y0, int x1, int y1)
      => s[(y1 + 1) * (w + 1) + x1 + 1] - s[(y1 + 1) * (w + 1) + x0]
       - s[y0 * (w + 1) + x1 + 1] + s[y0 * (w + 1) + x0];
  }
}
