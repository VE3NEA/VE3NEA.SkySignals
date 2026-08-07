using System;

namespace VE3NEA.SkySSTV
{
  /// <summary>
  /// Non-local means denoiser (denoise plan §3), ported from <c>SstvDens/ImgDens.pas</c>. Each pixel
  /// is replaced by a noise-weighted average of the pixels whose surrounding PATCH resembles its own,
  /// searched over a window far larger than the patch — so repeated fine structure (text, edges,
  /// texture) reinforces itself instead of being averaged away. That is the property the local Wiener
  /// filter structurally cannot have: over its 9×5 aperture a one-pixel stroke needs ≈6.8 σ to
  /// survive where a broad edge passes at 2.0 σ (plan §1).
  ///
  /// <para>Four adaptations carried over from the reference, all of which matter here:</para>
  /// <list type="number">
  /// <item><b>Noise-normalized distance</b> — <c>d̄ = mean over patch of Δ²/(s₁+s₂)</c>, so the
  /// similarity scale is data-derived rather than a free bandwidth parameter.</item>
  /// <item><b>Flat-topped weight</b> — <c>w = exp(−max(0, d̄−1))</c>. Patches agreeing to within one
  /// noise unit draw FULL weight; decay begins only past that. The dead zone is what makes the weight
  /// robust to the noise in the distance estimate itself.</item>
  /// <item><b>Inverse-variance accumulation</b> — each donor enters at <c>w/s_donor</c>, so this is a
  /// noise-weighted estimator and not a plain average.</item>
  /// <item><b>Two-pass residual cleanup</b> — an isolated impulse resembles no patch anywhere, so no
  /// donor matches it and plain NLM PRESERVES it. See <see cref="BuildMask"/>.</item>
  /// </list>
  ///
  /// <para>What is deliberately NOT carried over is the reference's own noise estimator: its
  /// separable second difference is in the family measured to read several× low on post-Stage-3 FM
  /// noise, which is horizontally correlated by the ±600 Hz brightness low-pass. The absolute noise
  /// map comes from the vertical first-difference median instead (plan §5.3). Its per-pixel operator
  /// IS reused for the second-pass mask, where only relative ordering matters.</para>
  ///
  /// <para><b>The constants here are provisional</b> and are settled by the plan's §9 visual
  /// experiments, not by matching the reference implementation (plan §5.6, D20).</para>
  /// </summary>
  internal static class SstvNlmFilter
  {
    /// <summary>Distance past which a donor contributes nothing worth the arithmetic:
    /// <c>exp(−5.3) ≈ 0.005</c>.</summary>
    private const double Cutoff = 6.3;

    /// <summary>Distance substituted where a patch overlaps an unrendered row. One such entry lifts
    /// d̄ past <see cref="Cutoff"/> on its own (1e3/49 ≈ 20), so a patch straddling a black band is
    /// rejected rather than treated as a zero-variance perfect match (plan §7).</summary>
    private const double InvalidDist = 1e3;

    private const double Small = 1e-5;

    // The reference tabulates the weight at 0.1 steps in d̄ (64 entries), which quantizes the weight
    // to ~10 % steps. A finer table costs 8 KB and removes that, while keeping the exp() out of a
    // loop that runs ~34 M times per plane per pass.
    private const int LutSize = 1024;
    private static readonly double[] weightLut = BuildLut();

    private static double[] BuildLut()
    {
      var lut = new double[LutSize + 1];
      for (int i = 0; i <= LutSize; i++)
        lut[i] = Math.Exp(-Math.Max(0.0, Cutoff * i / LutSize - 1.0));
      return lut;
    }

    private static double Weight(double dbar)
    {
      int i = (int)(dbar * LutSize / Cutoff);
      return weightLut[i < 0 ? 0 : i > LutSize ? LutSize : i];
    }


    // ----------------------------------------------------------------------------------------------------
    //                                            entry point
    // ----------------------------------------------------------------------------------------------------


    /// <summary>Denoise one plane in place.</summary>
    /// <param name="plane">Values 0..255, row-major.</param>
    /// <param name="rowValid">Which rows were rendered; unrendered rows are neither filtered nor
    /// used as donors.</param>
    /// <param name="noiseK">Noise over-weight for this plane — 1 for luma, the chroma over-weight
    /// for chroma, mirroring the Wiener's asymmetry (colour speckle being more objectionable than
    /// luma grain).</param>
    /// <param name="noiseRowStep">Row spacing of the vertical-difference noise estimator. 1 once
    /// chroma sits on its native grid; the chroma duplication factor when it does not.</param>
    public static void Apply(double[] plane, int w, int h, bool[] rowValid, double noiseK,
      int noiseRowStep, SstvDenoiseOptions o)
    {
      int patchWing = Math.Max(1, o.NlmPatchWing);
      int searchWing = Math.Max(1, o.NlmSearchWing);
      if (w < 2 * patchWing + 1 || h < 2 * patchWing + 1) return;

      double[] noise = NoiseMap(plane, w, h, rowValid, noiseK, noiseRowStep, o);
      var ctx = new Context(plane, noise, w, h, rowValid, patchWing, searchWing);

      ctx.Accumulate(null);
      ctx.Resolve(null);

      if (o.NlmTwoPass)
      {
        bool[] mask = BuildMask(ctx.Output, w, h, rowValid, o.NlmSecondPassPercentile);
        ctx.ScaleNoise(o.NlmSecondPassNoise);
        ctx.Accumulate(mask);
        ctx.Resolve(mask);
      }

      Array.Copy(ctx.Output, plane, plane.Length);
    }

    /// <summary>Per-pixel noise variance: the plane's own per-row σ² (the estimator measured to be
    /// right on this noise), scaled by the strength setting and shaped by the Wiener detector's gain
    /// — plan §5.4/§9.1. The mapping is an open experiment; <see cref="SstvNlmNoiseMap.RowOnly"/> is
    /// the control that leaves the detector out entirely.</summary>
    private static double[] NoiseMap(double[] plane, int w, int h, bool[] rowValid, double noiseK,
      int noiseRowStep, SstvDenoiseOptions o)
    {
      double[] rowVar = SstvWienerFilter.RowNoiseVar(plane, w, h, noiseRowStep, rowValid);
      double scale = o.NlmSig * o.NlmSig * noiseK;

      double[]? gain = null;
      if (o.NlmNoiseMap is SstvNlmNoiseMap.GainInflate or SstvNlmNoiseMap.GainDeflate)
      {
        int detW = o.WienerDetectW > 0 ? o.WienerDetectW : o.WienerWindowW;
        int detH = o.WienerDetectH > 0 ? o.WienerDetectH : o.WienerWindowH;
        gain = SstvWienerFilter.GainMap(plane, w, h, rowVar, noiseK, detW, detH);
      }

      var noise = new double[w * h];
      for (int y = 0; y < h; y++)
      {
        double baseVar = Math.Max(Small, scale * rowVar[y]);
        for (int x = 0; x < w; x++)
        {
          int i = y * w + x;
          double v = baseVar;
          if (gain != null)
            v = o.NlmNoiseMap == SstvNlmNoiseMap.GainInflate
              ? baseVar * (1.0 + o.NlmGainK * (1.0 - gain[i]))
              : baseVar * Math.Max(0.05, gain[i]);
          noise[i] = Math.Max(Small, v);
        }
      }
      return noise;
    }

    /// <summary>Mark the pixels where pass 1 left residual noise, for a stronger second pass. The
    /// per-pixel residual is the reference's separable second difference — used here purely as a
    /// RELATIVE detector, thresholded at a percentile of its own distribution, so its known absolute
    /// miscalibration on this noise does not matter (plan §5.3). The 3×3 sum before thresholding
    /// makes the mask a neighbourhood decision rather than a per-pixel one, so an impulse is
    /// recomputed together with the skirt it contaminated.</summary>
    private static bool[] BuildMask(double[] img, int w, int h, bool[] rowValid, double percentile)
    {
      var residual = new double[w * h];
      int cnt = 0;
      for (int y = 1; y < h - 1; y++)
      {
        if (!rowValid[y] || !rowValid[y - 1] || !rowValid[y + 1]) continue;
        for (int x = 1; x < w - 1; x++)
        {
          int i = y * w + x;
          double d = img[i]
            - 0.5 * (img[i - 1] + img[i + 1] + img[i - w] + img[i + w])
            + 0.25 * (img[i - w - 1] + img[i - w + 1] + img[i + w - 1] + img[i + w + 1]);
          residual[i] = d * d;
          cnt++;
        }
      }
      if (cnt == 0) return new bool[w * h];

      var sorted = new double[cnt];
      int at = 0;
      foreach (double v in residual) if (v > 0) sorted[at < cnt ? at++ : cnt - 1] = v;
      Array.Sort(sorted, 0, at);
      double threshold = sorted[Math.Clamp((int)(percentile * at), 0, Math.Max(0, at - 1))];

      var mask = new bool[w * h];
      for (int y = 1; y < h - 1; y++)
        for (int x = 1; x < w - 1; x++)
        {
          int i = y * w + x;
          double sum = residual[i - w - 1] + residual[i - w] + residual[i - w + 1]
                     + residual[i - 1] + residual[i] + residual[i + 1]
                     + residual[i + w - 1] + residual[i + w] + residual[i + w + 1];
          mask[i] = sum > threshold;
        }
      return mask;
    }


    // ----------------------------------------------------------------------------------------------------
    //                                       accumulation context
    // ----------------------------------------------------------------------------------------------------


    /// <summary>
    /// The padded working state of one plane. Input, noise map and row validity are mirrored into a
    /// margin of <c>patchWing + searchWing</c> so neither the patch nor the search needs a bounds
    /// test in the inner loop.
    ///
    /// <para>The accumulation walks only the HALF-PLANE of offsets (<c>ny ≥ 0</c>, and <c>nx &gt; 0</c>
    /// when <c>ny = 0</c>) and credits BOTH partners of each pair, because the similarity weight is
    /// symmetric — 220 pair-offsets instead of 440 gather-offsets, exactly half the work. That the
    /// mirror write is always at a row ≥ the source row, and never more than <c>searchWing</c> below
    /// it, is what will let this parallelize over row bands with a bounded halo (plan §6).</para>
    /// </summary>
    private sealed class Context
    {
      private readonly int w, h, patchWing, searchWing, marg, pw, ph, patchSize;
      private readonly double invPatchArea;
      private readonly double[] pIn, pS, acc, wsum;
      private readonly bool[] pRowValid;
      private readonly bool[] rowValid;

      // sliding patch-distance state (one offset at a time)
      private readonly double[] sqrDiffs;
      private readonly double[][] avgX;
      private readonly double[] avgXY;
      private int newPos, oldPos;
      private int nx, ny;

      public double[] Output { get; }

      public Context(double[] plane, double[] noise, int w, int h, bool[] rowValid,
        int patchWing, int searchWing)
      {
        this.w = w; this.h = h; this.rowValid = rowValid;
        this.patchWing = patchWing; this.searchWing = searchWing;
        marg = patchWing + searchWing;
        pw = w + 2 * marg; ph = h + 2 * marg;
        patchSize = 2 * patchWing + 1;
        invPatchArea = 1.0 / (patchSize * patchSize);

        pIn = new double[pw * ph];
        pS = new double[pw * ph];
        pRowValid = new bool[ph];
        acc = new double[pw * ph];
        wsum = new double[pw * ph];
        Output = new double[w * h];

        Pad(plane, noise);

        sqrDiffs = new double[w + 2 * patchWing];
        avgX = new double[patchSize + 1][];
        for (int i = 0; i <= patchSize; i++) avgX[i] = new double[w];
        avgXY = new double[w];
      }

      /// <summary>Mirror the image, its noise map and its row validity into the margins (reflection
      /// about the edge, not edge repetition).</summary>
      private void Pad(double[] plane, double[] noise)
      {
        for (int y = 0; y < h; y++)
        {
          pRowValid[marg + y] = rowValid[y];
          for (int x = 0; x < w; x++)
          {
            int p = (marg + y) * pw + marg + x, i = y * w + x;
            pIn[p] = plane[i];
            pS[p] = Math.Max(Small, noise[i]);
          }
        }

        for (int d = 1; d <= marg; d++)
        {
          int loSrc = Math.Min(h - 1, d), hiSrc = Math.Max(0, h - 1 - d);
          pRowValid[marg - d] = rowValid[loSrc];
          pRowValid[marg + h - 1 + d] = rowValid[hiSrc];
          Array.Copy(pIn, (marg + loSrc) * pw, pIn, (marg - d) * pw, pw);
          Array.Copy(pS, (marg + loSrc) * pw, pS, (marg - d) * pw, pw);
          Array.Copy(pIn, (marg + hiSrc) * pw, pIn, (marg + h - 1 + d) * pw, pw);
          Array.Copy(pS, (marg + hiSrc) * pw, pS, (marg + h - 1 + d) * pw, pw);
        }

        for (int by = 0; by < ph; by++)
          for (int d = 1; d <= marg; d++)
          {
            int row = by * pw;
            int loSrc = row + marg + Math.Min(w - 1, d), hiSrc = row + marg + Math.Max(0, w - 1 - d);
            pIn[row + marg - d] = pIn[loSrc];
            pS[row + marg - d] = pS[loSrc];
            pIn[row + marg + w - 1 + d] = pIn[hiSrc];
            pS[row + marg + w - 1 + d] = pS[hiSrc];
          }
      }

      public void ScaleNoise(double factor)
      {
        for (int i = 0; i < pS.Length; i++) pS[i] = Math.Max(Small, pS[i] * factor);
      }

      /// <summary>Sweep every half-plane offset, crediting both partners of each similar pair.
      /// <paramref name="mask"/> null means "every pixel" (pass 1); otherwise only masked pixels are
      /// credited, which is what makes the second pass a targeted repair.</summary>
      public void Accumulate(bool[]? mask)
      {
        Array.Clear(acc);
        Array.Clear(wsum);

        for (int dy = 0; dy <= searchWing; dy++)
          for (int dx = -searchWing; dx <= searchWing; dx++)
          {
            if (dy == 0 && dx <= 0) continue;               // the centre, and the mirrored half
            ny = dy; nx = dx;
            SweepOffset(mask);
          }
      }

      private void SweepOffset(bool[]? mask)
      {
        InitAvg();
        for (int y = 0; y < h; y++)
        {
          int by = marg + y;
          if (pRowValid[by] && pRowValid[by + ny])
          {
            int rowP = by * pw + marg, rowQ = (by + ny) * pw + marg + nx;
            for (int x = 0; x < w; x++)
            {
              double dbar = avgXY[x] * invPatchArea;
              if (dbar >= Cutoff) continue;
              double wt = Weight(dbar < 0 ? 0 : dbar);

              int p = rowP + x, q = rowQ + x;
              // the pair is credited in both directions: P gains a donor at Q and vice versa
              if (mask == null || Masked(mask, by, marg + x))
              {
                double cw = wt / pS[q];
                acc[p] += pIn[q] * cw;
                wsum[p] += cw;
              }
              if (mask == null || Masked(mask, by + ny, marg + x + nx))
              {
                double cw = wt / pS[p];
                acc[q] += pIn[p] * cw;
                wsum[q] += cw;
              }
            }
          }
          if (y < h - 1) UpdateAvg(y + patchWing + 1);
        }
      }

      /// <summary>Is a padded coordinate a masked IMAGE pixel? Mirror pixels are never masked — their
      /// accumulation is discarded, the real pixel they reflect gathering its own contributions.</summary>
      private bool Masked(bool[] mask, int by, int bx)
      {
        int y = by - marg, x = bx - marg;
        return (uint)y < (uint)h && (uint)x < (uint)w && mask[y * w + x];
      }

      /// <summary>Prime the vertical sliding sum with the <c>patchSize</c> rows centred on row 0.</summary>
      private void InitAvg()
      {
        foreach (var row in avgX) Array.Clear(row);
        Array.Clear(avgXY);
        newPos = 0; oldPos = 1;
        for (int y = -patchWing; y <= patchWing; y++) UpdateAvg(y);
      }

      /// <summary>Fold row <paramref name="y"/> into the sliding sums: squared differences along the
      /// row, a sliding sum over x into a ring slot, then the ring's oldest slot swapped out of the
      /// vertical sum. The ring holds <c>patchSize + 1</c> rows so the slot being retired is still
      /// intact when the new one is written.</summary>
      private void UpdateAvg(int y)
      {
        int by = marg + y;
        bool ok = pRowValid[by] && pRowValid[by + ny];
        if (!ok)
        {
          for (int i = 0; i < sqrDiffs.Length; i++) sqrDiffs[i] = InvalidDist;
        }
        else
        {
          int rowP = by * pw + marg - patchWing, rowQ = (by + ny) * pw + marg - patchWing + nx;
          for (int i = 0; i < sqrDiffs.Length; i++)
          {
            int p = rowP + i, q = rowQ + i;
            double d = pIn[q] - pIn[p];
            sqrDiffs[i] = d * d / (pS[p] + pS[q]);
          }
        }

        double[] dest = avgX[newPos];
        double sum = 0;
        for (int i = 0; i < patchSize; i++) sum += sqrDiffs[i];
        dest[0] = sum;
        for (int x = 0; x < w - 1; x++) dest[x + 1] = dest[x] - sqrDiffs[x] + sqrDiffs[x + patchSize];

        double[] drop = avgX[oldPos];
        for (int x = 0; x < w; x++) avgXY[x] += dest[x] - drop[x];

        newPos = oldPos;
        if (++oldPos > patchSize) oldPos = 0;
      }

      /// <summary>Add the pixel's own contribution and normalize. The centre enters at half the
      /// weight a perfectly-matching donor would, so a pixel with no matches anywhere keeps its own
      /// value rather than being pulled toward nothing.</summary>
      public void Resolve(bool[]? mask)
      {
        for (int y = 0; y < h; y++)
        {
          if (!rowValid[y])
          {
            for (int x = 0; x < w; x++) Output[y * w + x] = pIn[(marg + y) * pw + marg + x];
            continue;
          }
          for (int x = 0; x < w; x++)
          {
            int i = y * w + x, p = (marg + y) * pw + marg + x;
            if (mask != null && !mask[i]) continue;          // second pass: leave pass-1 result
            double cw = 0.5 / pS[p];
            Output[i] = (acc[p] + pIn[p] * cw) / (Small + wsum[p] + cw);
          }
        }
      }
    }
  }
}
