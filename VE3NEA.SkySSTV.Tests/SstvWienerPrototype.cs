using System;

namespace VE3NEA.SkySSTV.Tests
{
  /// <summary>Knobs of the P6(d) prototype — the plan's experiment grid is window size × chroma
  /// over-weight × shrink-to-neutral (plan §6.2).</summary>
  public sealed record SstvWienerOptions
  {
    /// <summary>Local window width (pixels).</summary>
    public int WindowW { get; init; } = 7;

    /// <summary>Local window height (lines).</summary>
    public int WindowH { get; init; } = 3;

    /// <summary>Over-weighting factor of the noise term for the chroma planes (plan §6.2: the rainbow
    /// speckle is mostly Cr/Cb noise — the biggest visual win per dB — so chroma is filtered harder
    /// than luma, whose noise term stays at 1×).</summary>
    public double ChromaK { get; init; } = 3.0;

    /// <summary>Shrink chroma toward neutral (128) where the local chroma energy does not rise above
    /// the over-weighted noise level: noise-only color speckle collapses to gray, real color passes.</summary>
    public bool ShrinkToNeutral { get; init; } = true;
  }

  /// <summary>
  /// P6(d) batch prototype of the streaming Wiener (Lee) post-filter (plan §6.2). Test-harness only —
  /// nothing enters the production reconstruction until the <c>p6d_*.png</c> variants pass the user's
  /// visual judgment. Operates per plane (Y, Cr, Cb — recovered from the decoded RGB via the exact
  /// inverse <see cref="YCrCb.FromRgb"/>, zero decoder changes). Two noise maps:
  /// the image-domain row-wise vertical-difference estimate (row-local, 1–2 line lag — the plan's
  /// calibration/fallback map), and an externally supplied per-pixel map for the demod-domain A/B
  /// (the guard-band pilot, built by the probe). Every operation is window-local with a 1–2 line lag,
  /// so the accepted variant transfers directly to the streaming line-emission form.
  /// </summary>
  public static class SstvWienerPrototype
  {
    /// <summary>Filter with the image-domain noise map (row-wise vertical-difference estimate per
    /// plane; chroma uses a 2-row step because Robot36's alternating chroma rows are duplicates).</summary>
    public static RgbImage Apply(RgbImage img, SstvWienerOptions? options = null)
    {
      int w = img.Width, h = img.Height;
      var (y, cr, cb) = Planes(img);
      return Filter(y, cr, cb, w, h, options ?? new SstvWienerOptions(),
        ExpandRows(RowNoiseVar(y, w, h, 1), w, h),
        ExpandRows(RowNoiseVar(cr, w, h, 2), w, h),
        ExpandRows(RowNoiseVar(cb, w, h, 2), w, h));
    }

    /// <summary>Filter with an externally supplied per-pixel noise-variance map in pixel-value² units
    /// (the demod-domain path, plan §6.2 map (a)–(c)); the two chroma planes share one map.</summary>
    public static RgbImage Apply(RgbImage img, SstvWienerOptions options, double[] varY, double[] varC)
    {
      var (y, cr, cb) = Planes(img);
      return Filter(y, cr, cb, img.Width, img.Height, options, varY, varC, varC);
    }

    private static RgbImage Filter(double[] y, double[] cr, double[] cb, int w, int h,
      SstvWienerOptions o, double[] varY, double[] varCr, double[] varCb)
    {
      double[] fy = Lee(y, w, h, o.WindowW, o.WindowH, varY, 1.0, false);
      double[] fcr = Lee(cr, w, h, o.WindowW, o.WindowH, varCr, o.ChromaK, o.ShrinkToNeutral);
      double[] fcb = Lee(cb, w, h, o.WindowW, o.WindowH, varCb, o.ChromaK, o.ShrinkToNeutral);

      var outImg = new RgbImage(w, h);
      for (int row = 0; row < h; row++)
        for (int x = 0; x < w; x++)
        {
          int i = row * w + x;
          var (r, g, b) = YCrCb.ToRgb(fy[i], fcr[i], fcb[i]);
          outImg.Set(x, row, (byte)Math.Round(r), (byte)Math.Round(g), (byte)Math.Round(b));
        }
      return outImg;
    }

    /// <summary>Median per-row noise σ of each plane — reported by the probe, and the absolute scale
    /// the demod-domain map is calibrated to (the plan's "image-domain calibration" role).</summary>
    public static (double y, double cr, double cb) NoiseSigmas(RgbImage img)
    {
      var (y, cr, cb) = Planes(img);
      return (MedianSigma(RowNoiseVar(y, img.Width, img.Height, 1)),
              MedianSigma(RowNoiseVar(cr, img.Width, img.Height, 2)),
              MedianSigma(RowNoiseVar(cb, img.Width, img.Height, 2)));
    }

    private static double MedianSigma(double[] rowVar)
    {
      var s = (double[])rowVar.Clone();
      Array.Sort(s);
      return Math.Sqrt(s[s.Length / 2]);
    }

    private static (double[] y, double[] cr, double[] cb) Planes(RgbImage img)
    {
      int n = img.Width * img.Height;
      var y = new double[n];
      var cr = new double[n];
      var cb = new double[n];
      for (int i = 0; i < n; i++)
        (y[i], cr[i], cb[i]) = YCrCb.FromRgb(img.R[i], img.G[i], img.B[i]);
      return (y, cr, cb);
    }

    private static double[] ExpandRows(double[] rowVar, int w, int h)
    {
      var map = new double[w * h];
      for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++) map[y * w + x] = rowVar[y];
      return map;
    }


    // ----------------------------------------------------------------------------------------------------
    //                                     image-domain noise map
    // ----------------------------------------------------------------------------------------------------

    /// <summary>Per-row noise variance from vertical first differences: adjacent scan lines are
    /// independent time slices, so |p[y] − p[y−step]| carries the full noise power even when the
    /// post-LPF FM noise is horizontally correlated. (The plan's 3×3 Immerkær residual was tried
    /// first and read several times LOW on real bursts: its separable kernel needs a horizontal
    /// second difference, which vanishes on the horizontally-smooth noise blobs.) The median over x
    /// rejects content edges; σ = median|d| / 0.6745 / √2 for Gaussian noise. <paramref name="step"/>
    /// is 2 for chroma planes, whose Robot36 rows are nearest-neighbor duplicates.</summary>
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


    // ----------------------------------------------------------------------------------------------------
    //                                        Lee (local Wiener)
    // ----------------------------------------------------------------------------------------------------

    /// <summary>The Lee filter (plan §6.2): local mean μ and variance σ²loc over the window, gain
    /// g = max(0, σ²loc − k·σ²n)/σ²loc, output μ + g·(x − μ) — noise-dominated areas collapse to their
    /// local mean (the requested contrast reduction, smoothly ramped), real edges pass at g ≈ 1.
    /// With <paramref name="shrink"/> a second Wiener gain on the deviation-from-neutral pulls chroma
    /// whose window energy about 128 is at/below the noise level all the way to gray.</summary>
    private static double[] Lee(double[] p, int w, int h, int winW, int winH, double[] varMap,
      double k, bool shrink)
    {
      // 2D prefix sums of x and x² give O(1) window mean/variance at any size
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

      int rx = winW / 2, ry = winH / 2;
      var outp = new double[w * h];
      for (int y = 0; y < h; y++)
      {
        int y0 = Math.Max(0, y - ry), y1 = Math.Min(h - 1, y + ry);
        for (int x = 0; x < w; x++)
        {
          double vn = k * varMap[y * w + x];
          int x0 = Math.Max(0, x - rx), x1 = Math.Min(w - 1, x + rx);
          double n = (x1 - x0 + 1) * (y1 - y0 + 1);
          double sum = Box(s1, w, x0, y0, x1, y1);
          double mu = sum / n;
          double varLoc = Math.Max(0, Box(s2, w, x0, y0, x1, y1) / n - mu * mu);

          double g = varLoc > vn ? (varLoc - vn) / varLoc : 0.0;
          double v = mu + g * (p[y * w + x] - mu);
          if (shrink)
          {
            double meanSq = varLoc + (mu - 128.0) * (mu - 128.0);
            double gn = meanSq > vn ? 1.0 - vn / meanSq : 0.0;
            v = 128.0 + gn * (v - 128.0);
          }
          outp[y * w + x] = v;
        }
      }
      return outp;
    }

    private static double Box(double[] s, int w, int x0, int y0, int x1, int y1)
      => s[(y1 + 1) * (w + 1) + x1 + 1] - s[(y1 + 1) * (w + 1) + x0]
       - s[y0 * (w + 1) + x1 + 1] + s[y0 * (w + 1) + x0];
  }
}
