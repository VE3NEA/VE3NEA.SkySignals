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
  /// 9×5, chroma noise over-weight k = 4, image-domain noise map — the row-wise vertical
  /// first-difference median estimator (scan lines are independent time slices, so inter-line
  /// differences carry the full noise power even where the post-LPF FM noise is horizontally
  /// correlated; the plan's Immerkær residual read several× low on exactly that noise and was
  /// dropped).
  ///
  /// <para>Its two halves are separable and are used separately: the <b>detector</b>
  /// (<see cref="GainMap"/>) answers "is there signal above the noise here", and the <b>smoother</b>
  /// applies the shrinkage. The non-local means filter consumes the detector while discarding the
  /// smoother, because the smoother is what over-flattens noise areas into a box blur — the defect
  /// that motivated the denoise plan (§5.4).</para>
  /// </summary>
  internal static class SstvWienerFilter
  {
    /// <summary>Filter the three planes in place. Chroma noise is estimated over a 2-row step because
    /// Robot36/PD chroma rows are nearest-neighbor duplicates (vertical upsampling).</summary>
    /// <param name="yGain">Optional capture of the luma plane's per-pixel gain, UNFLOORED — the
    /// confidence quantity (g ≈ 1 where real detail passed, 0 where the pixel collapsed to its local
    /// mean), kept independent of <see cref="SstvDenoiseOptions.WienerGainFloor"/> so an aesthetic
    /// setting cannot masquerade as confidence.</param>
    public static void Apply(double[] y, double[] cr, double[] cb, int w, int h, double[]? yGain,
      SstvDenoiseOptions o)
    {
      int ww = o.WienerWindowW, wh = o.WienerWindowH;
      int dw = o.WienerDetectW > 0 ? o.WienerDetectW : ww;
      int dh = o.WienerDetectH > 0 ? o.WienerDetectH : wh;
      double k = o.WienerChromaK, floor = o.WienerGainFloor;

      // the noise-only gate is decided ONCE, on luma, and applied to all three planes: whether a band
      // of the image carries a picture is a property of the band, not of a colour component, and a
      // chroma plane is too sparsely sampled to decide it on its own
      double[] yVar = RowNoiseVar(y, w, h, 1);
      bool[] gate = RowHasSignal(y, w, h, yVar, o.SkipNoiseOnlyBands);

      Lee(y, w, h, yVar, 1.0, yGain, ww, wh, dw, dh, floor, gate);
      Lee(cr, w, h, RowNoiseVar(cr, w, h, 2), k, null, ww, wh, dw, dh, floor, gate);
      Lee(cb, w, h, RowNoiseVar(cb, w, h, 2), k, null, ww, wh, dw, dh, floor, gate);
    }

    /// <summary>Per-row noise variance: σ = median_x|p[y] − p[y−step]| / 0.6745 / √2 (the Gaussian
    /// median-absolute-deviation of a two-row difference), then median-of-5 smoothed across rows so
    /// content-heavy rows (horizontal edges) do not spike the estimate.
    ///
    /// <para>The vertical difference is the whole point: scan lines are independent time slices, so
    /// their difference carries the full noise power even where the post-Stage-3 FM noise is
    /// horizontally correlated by the ±600 Hz brightness low-pass. Any operator that differences
    /// ALONG the row sees correlated samples and underreads — which is why the reference NLM's own
    /// estimator is not used for the absolute noise map (denoise plan §5.3).</para>
    ///
    /// <para><paramref name="rowValid"/>, when supplied, excludes unrendered rows: they are black,
    /// so they would report σ 0 and drag the estimate down (denoise plan §7).</para></summary>
    public static double[] RowNoiseVar(double[] p, int w, int h, int step, bool[]? rowValid = null)
    {
      var sigma = new double[h];
      var absd = new double[w];
      for (int y = 0; y < h; y++)
      {
        int y2 = y >= step ? y - step : y + step;
        if (y2 >= h) continue;
        if (rowValid != null && (!rowValid[y] || !rowValid[y2])) continue;
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
          if (y + d >= 0 && y + d < h && sigma[y + d] > 0) win[cnt++] = sigma[y + d];
        if (cnt == 0) { v[y] = 0; continue; }
        Array.Sort(win, 0, cnt);
        double med = win[cnt / 2];
        v[y] = med * med;
      }
      return v;
    }

    /// <summary>Per-row signal-to-noise ratio: the row's own variance measured against the noise
    /// variance, <c>var_row/σ²n − 1</c>, median-of-5 smoothed across rows.
    ///
    /// <para>This answers a question the per-pixel gain cannot: <b>is there an image in this row at
    /// all?</b> Where the pass dropped below the FM threshold the row is pure noise of high RMS, so
    /// <c>var_row ≈ σ²n</c> and the ratio falls to ≈0; where an image is present the row's variance
    /// carries the picture on top of the noise and the ratio rises well above it. Both quantities are
    /// measured on the same clipped data, so the clipping that flattens high-σ noise scales them
    /// together and largely cancels in the ratio.</para>
    ///
    /// <para>The distinction matters because the two regimes want OPPOSITE treatment, which no
    /// monotone shrinkage curve can express. In a noise-only band there is no detail to protect and
    /// smoothing it produces a soft grey wash that reads as a blurred PICTURE — the strongest single
    /// contributor to "the whole image has no small detail" — where leaving the noise alone reads
    /// honestly as "no signal here". In a medium-SNR band the speckle is exactly what should go. So
    /// the filter wants to work in the middle of this statistic and keep its hands off both ends.</para></summary>
    public static double[] RowSnr(double[] p, int w, int h, double[] rowVar, int smoothRows = 9)
    {
      var raw = new double[h];
      for (int y = 0; y < h; y++)
      {
        double mean = 0;
        for (int x = 0; x < w; x++) mean += p[y * w + x];
        mean /= w;

        double v = 0;
        for (int x = 0; x < w; x++) { double d = p[y * w + x] - mean; v += d * d; }
        v /= w;
        // NOT clamped at 0: on pure noise the ratio fluctuates symmetrically about zero, and clipping
        // that away costs exactly the resolution the low end of this scale is judged on. Gating is
        // unaffected either way, every useful threshold being positive
        raw[y] = rowVar[y] > 1e-9 ? v / rowVar[y] - 1.0 : 0.0;
      }

      // One fluke row must not flip the gate under a band of otherwise uniform character. The window
      // also sets the statistic's OWN noise floor: a row variance over w samples carries a relative
      // error of ≈√(2/w), so a single row resolves the ratio only to ≈±0.11 at w = 320, and a median
      // over `smoothRows` rows is what buys the resolution needed to separate pure noise from a weak
      // but real picture. Bands run to tens of rows, so 9 costs nothing at their edges.
      var snr = new double[h];
      var win = new double[Math.Max(1, smoothRows)];
      int wing = win.Length / 2;
      for (int y = 0; y < h; y++)
      {
        int cnt = 0;
        for (int d = -wing; d <= wing; d++) if (y + d >= 0 && y + d < h) win[cnt++] = raw[y + d];
        Array.Sort(win, 0, cnt);
        snr[y] = win[cnt / 2];
      }
      return snr;
    }

    /// <summary>How much of the filtered result each row should keep: 0 where the row is pure noise,
    /// 1 where it plainly carries a picture, ramped linearly in <see cref="RowSnr"/> between
    /// <paramref name="minSnr"/> and <paramref name="fullSnr"/>. <paramref name="minSnr"/> ≤ 0 disables
    /// the whole mechanism and returns all-ones.
    ///
    /// <para><b>A ramp, not a gate.</b> The first attempt was a hard per-row on/off, and it produced
    /// exactly the artifact its structure invites: measured on genuinely pure noise the statistic has a
    /// spread of ≈±0.16 (04-18, p05…p95 −0.161…+0.16), so any threshold inside that band decides
    /// individual rows near-arbitrarily, and the output came out in HARD HORIZONTAL STRIPES of filtered
    /// and unfiltered rows — far more objectionable than either treatment applied uniformly. A ramp
    /// makes the transition invisible, and it is also the truer model: signal-to-noise across a fade is
    /// continuous, not binary. The decode chain already resolves the same tension the same way, blending
    /// its narrow and wide brightness branches over a σ 35→65 ramp rather than switching between
    /// them.</para></summary>
    public static bool[] RowHasSignal(double[] p, int w, int h, double[] rowVar, bool enabled,
      bool[]? rowValid = null)
    {
      var gate = new bool[h];
      if (!enabled)
      {
        for (int y = 0; y < h; y++) gate[y] = rowValid == null || rowValid[y];
        return gate;
      }

      // A plain per-row decision that EXCLUDES the row rather than fading it. Two attempts to soften it
      // were made and both were wrong, in the same way: they filtered every row and mixed the result
      // back per row afterwards, which leaves the noise-only rows donating patches and inflating the
      // noise estimate, so the picture rows change too — 43 % of pixels moved against the plain gate on
      // a burst whose noise rows are interleaved with picture rows. Excluding a noise-only row is the
      // whole point (plan §7): it is not a donor, it is not in the estimate, and it is not filtered.
      double[] snr = RowSnr(p, w, h, rowVar);
      for (int y = 0; y < h; y++)
        gate[y] = (rowValid == null || rowValid[y]) && snr[y] >= NoiseBandSnr;
      return gate;
    }

    /// <summary>The row SNR below which a row is taken to carry no picture.
    ///
    /// <para>0.02 is where the threshold search stopped: below it the statistic is inside its own spread
    /// on pure noise (≈±0.16) and decides nothing at all. It is not a tunable — see
    /// <see cref="SstvDenoiseOptions.SkipNoiseOnlyBands"/>, which is why this is a switch.</para></summary>
    public const double NoiseBandSnr = 0.02;

    /// <summary>The detector alone: the per-pixel gain, UNFLOORED, over the detection aperture.
    /// Its known bias is against thin strokes — over a 9×5 = 45-sample aperture a one-pixel stroke
    /// needs ≈6.8 σ to reach g &gt; 0 while a broad edge passes at 2.0 σ. That bias is harmful when
    /// it drives a smoother (the stroke is erased) and far milder when it only shapes a noise map
    /// (the stroke is merely a weaker donor), which is what makes it reusable by the NLM.</summary>
    public static double[] GainMap(double[] p, int w, int h, double[] rowVar, double k,
      int detW, int detH)
    {
      var (s1, s2) = PrefixSums(p, w, h);
      var gain = new double[w * h];
      int dx = detW / 2, dy = detH / 2;

      for (int y = 0; y < h; y++)
      {
        double vn = k * rowVar[y];
        int dy0 = Math.Max(0, y - dy), dy1 = Math.Min(h - 1, y + dy);
        for (int x = 0; x < w; x++)
        {
          int dx0 = Math.Max(0, x - dx), dx1 = Math.Min(w - 1, x + dx);
          double dn = (dx1 - dx0 + 1) * (dy1 - dy0 + 1);
          double dmu = Box(s1, w, dx0, dy0, dx1, dy1) / dn;
          double varLoc = Math.Max(0, Box(s2, w, dx0, dy0, dx1, dy1) / dn - dmu * dmu);
          gain[y * w + x] = varLoc > vn ? (varLoc - vn) / varLoc : 0.0;
        }
      }
      return gain;
    }

    private static void Lee(double[] p, int w, int h, double[] rowVar, double k, double[]? gain,
      int winW, int winH, int detW, int detH, double gainFloor, bool[]? rowHasSignal = null)
    {
      var (s1, _) = PrefixSums(p, w, h);
      double[] g = GainMap(p, w, h, rowVar, k, detW, detH);
      if (gain != null) Array.Copy(g, gain, Math.Min(g.Length, gain.Length));

      int rx = winW / 2, ry = winH / 2;                      // smoothing aperture: supplies the local mean
      var outp = new double[w * h];
      for (int y = 0; y < h; y++)
      {
        // a noise-only band is left exactly as received: there is no detail in it to preserve, and
        // averaging it is what turns "no signal here" into "a blurred picture"
        if (rowHasSignal != null && !rowHasSignal[y])
        {
          Array.Copy(p, y * w, outp, y * w, w);
          continue;
        }
        int y0 = Math.Max(0, y - ry), y1 = Math.Min(h - 1, y + ry);
        for (int x = 0; x < w; x++)
        {
          int x0 = Math.Max(0, x - rx), x1 = Math.Min(w - 1, x + rx);
          double n = (x1 - x0 + 1) * (y1 - y0 + 1);
          double mu = Box(s1, w, x0, y0, x1, y1) / n;

          int i = y * w + x;
          double gi = g[i] < gainFloor ? gainFloor : g[i];
          outp[i] = mu + gi * (p[i] - mu);
        }
      }
      Array.Copy(outp, p, p.Length);
    }

    /// <summary>2D prefix sums of x and x², giving O(1) window mean and variance.</summary>
    private static (double[] s1, double[] s2) PrefixSums(double[] p, int w, int h)
    {
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
      return (s1, s2);
    }

    private static double Box(double[] s, int w, int x0, int y0, int x1, int y1)
      => s[(y1 + 1) * (w + 1) + x1 + 1] - s[(y1 + 1) * (w + 1) + x0]
       - s[y0 * (w + 1) + x1 + 1] + s[y0 * (w + 1) + x0];
  }
}
