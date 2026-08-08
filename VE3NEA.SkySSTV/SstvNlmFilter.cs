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
  /// <summary>
  /// The degeneracy diagnostics of one <see cref="SstvNlmFilter.Apply"/> run (denoise plan §5.6).
  /// Neither failure mode announces itself in the picture, so the probe reads them rather than the
  /// image: an <see cref="FlatTopShare"/> near 1 means every donor drew full weight and the filter has
  /// become a 21×21 box average — smooth, plausible, and exactly the defect the plan exists to remove
  /// — while a <see cref="RejectedShare"/> near 1 means every donor was refused and the run was an
  /// expensive no-op.
  /// </summary>
  internal sealed class SstvNlmStats
  {
    /// <summary>Donor pairs whose distance was evaluated, over both passes.</summary>
    public long Evaluated;

    /// <summary>Of those, the ones inside the weight kernel's flat top (d̄ ≤ 1), which draw full weight.</summary>
    public long FlatTop;

    /// <summary>Of those, the ones past the cutoff, which contribute nothing.</summary>
    public long Rejected;

    /// <summary>Pixels the second pass recomputed, summed over the planes (plan §5.5): near zero means
    /// the pass costs its ~2× runtime for nothing.</summary>
    public int MaskedPixels;

    public double FlatTopShare => Evaluated > 0 ? (double)FlatTop / Evaluated : 0;
    public double RejectedShare => Evaluated > 0 ? (double)Rejected / Evaluated : 0;
  }

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
      int noiseRowStep, SstvDenoiseOptions o, SstvNlmStats? stats = null)
    {
      int patchWing = Math.Max(1, o.NlmPatchWing);
      int searchWing = Math.Max(1, o.NlmSearchWing);
      if (w < 2 * patchWing + 1 || h < 2 * patchWing + 1) return;

      var (dist, weight) = NoiseMap(plane, w, h, rowValid, noiseK, noiseRowStep, o);
      var ctx = new Context(plane, dist, weight, w, h, rowValid, patchWing, searchWing);

      ctx.Accumulate(null, stats, o.NlmBands);
      ctx.Resolve(null);

      if (o.NlmTwoPass)
      {
        bool[] mask = BuildMask(ctx.Output, w, h, rowValid, o.NlmSecondPassPercentile);
        if (stats != null) foreach (bool m in mask) if (m) stats.MaskedPixels++;
        ctx.ScaleNoise(o.NlmSecondPassNoise);
        ctx.Accumulate(mask, stats, o.NlmBands);
        ctx.Resolve(mask);
      }

      Array.Copy(ctx.Output, plane, plane.Length);
    }

    /// <summary>The two noise maps the filter needs: one normalizing the patch DISTANCE, one weighting
    /// each donor's contribution (<c>w/s</c>). Both start from the plane's own per-row σ² — the
    /// estimator measured to be right on this noise — scaled by the strength setting and then shaped
    /// by the Wiener detector's gain according to the §9.1 arm:
    ///
    /// <list type="bullet">
    /// <item><see cref="SstvNlmNoiseMap.RowOnly"/> — the control: the detector is unused, both maps are
    /// the plain per-row σ².</item>
    /// <item><see cref="SstvNlmNoiseMap.GainInflate"/> — inflate where the detector says noise. Its
    /// failure mode is survivable: a misjudged pixel is merely averaged a little harder.</item>
    /// <item><see cref="SstvNlmNoiseMap.GainDeflate"/> — deflate where the detector says signal. Riskier:
    /// a thin stroke the detector missed is scaled down and never recovers.</item>
    /// <item><see cref="SstvNlmNoiseMap.DistanceOnly"/> — the detector gates only the distance, so it
    /// decides which donors qualify while the inverse-variance weighting stays purely per-row.</item>
    /// </list>
    ///
    /// <para>The two returned arrays are the SAME instance whenever the arm does not separate them, and
    /// the accumulator relies on that to skip a second padded copy.</para></summary>
    private static (double[] dist, double[] weight) NoiseMap(double[] plane, int w, int h,
      bool[] rowValid, double noiseK, int noiseRowStep, SstvDenoiseOptions o)
    {
      double[] rowVar = SstvWienerFilter.RowNoiseVar(plane, w, h, noiseRowStep, rowValid);
      double scale = o.NlmSig * o.NlmSig * noiseK;

      var rowOnly = new double[w * h];
      for (int y = 0; y < h; y++)
      {
        double baseVar = Math.Max(Small, scale * rowVar[y]);
        for (int x = 0; x < w; x++) rowOnly[y * w + x] = baseVar;
      }
      if (o.NlmNoiseMap == SstvNlmNoiseMap.RowOnly) return (rowOnly, rowOnly);

      int detW = o.WienerDetectW > 0 ? o.WienerDetectW : o.WienerWindowW;
      int detH = o.WienerDetectH > 0 ? o.WienerDetectH : o.WienerWindowH;
      double[] gain = SstvWienerFilter.GainMap(plane, w, h, rowVar, noiseK, detW, detH);

      var shaped = new double[w * h];
      for (int i = 0; i < shaped.Length; i++)
        shaped[i] = Math.Max(Small, o.NlmNoiseMap == SstvNlmNoiseMap.GainDeflate
          ? rowOnly[i] * Math.Max(0.05, gain[i])
          : rowOnly[i] * (1.0 + o.NlmGainK * (1.0 - gain[i])));

      return o.NlmNoiseMap == SstvNlmNoiseMap.DistanceOnly ? (shaped, rowOnly) : (shaped, shaped);
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
      private readonly double[] pIn, pS, pSw, acc, wsum;
      private readonly bool[] pRowValid;
      private readonly bool[] rowValid;
      private readonly bool separateWeightNoise;

      /// <summary>Rows below which a band is not worth its own thread: each band re-warms the sliding
      /// sums over <c>patchWing</c> rows at its start, which is ~10 % overhead at 30 rows and grows as
      /// the band shrinks.</summary>
      private const int MinBandRows = 24;

      public double[] Output { get; }

      public Context(double[] plane, double[] noiseDist, double[] noiseWeight, int w, int h,
        bool[] rowValid, int patchWing, int searchWing)
      {
        this.w = w; this.h = h; this.rowValid = rowValid;
        this.patchWing = patchWing; this.searchWing = searchWing;
        marg = patchWing + searchWing;
        pw = w + 2 * marg; ph = h + 2 * marg;
        patchSize = 2 * patchWing + 1;
        invPatchArea = 1.0 / (patchSize * patchSize);

        pIn = new double[pw * ph];
        pS = new double[pw * ph];
        separateWeightNoise = !ReferenceEquals(noiseDist, noiseWeight);
        pSw = separateWeightNoise ? new double[pw * ph] : pS;
        pRowValid = new bool[ph];
        acc = new double[pw * ph];
        wsum = new double[pw * ph];
        Output = new double[w * h];

        Pad(plane, noiseDist, noiseWeight);
      }

      /// <summary>Mirror the image, its noise map and its row validity into the margins (reflection
      /// about the edge, not edge repetition).</summary>
      private void Pad(double[] plane, double[] noiseDist, double[] noiseWeight)
      {
        for (int y = 0; y < h; y++)
        {
          pRowValid[marg + y] = rowValid[y];
          for (int x = 0; x < w; x++)
          {
            int p = (marg + y) * pw + marg + x, i = y * w + x;
            pIn[p] = plane[i];
            pS[p] = Math.Max(Small, noiseDist[i]);
            if (separateWeightNoise) pSw[p] = Math.Max(Small, noiseWeight[i]);
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
          if (separateWeightNoise)
          {
            Array.Copy(pSw, (marg + loSrc) * pw, pSw, (marg - d) * pw, pw);
            Array.Copy(pSw, (marg + hiSrc) * pw, pSw, (marg + h - 1 + d) * pw, pw);
          }
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
            if (separateWeightNoise)
            {
              pSw[row + marg - d] = pSw[loSrc];
              pSw[row + marg + w - 1 + d] = pSw[hiSrc];
            }
          }
      }

      public void ScaleNoise(double factor)
      {
        for (int i = 0; i < pS.Length; i++) pS[i] = Math.Max(Small, pS[i] * factor);
        if (separateWeightNoise)
          for (int i = 0; i < pSw.Length; i++) pSw[i] = Math.Max(Small, pSw[i] * factor);
      }

      /// <summary>Sweep every half-plane offset, crediting both partners of each similar pair.
      /// <paramref name="mask"/> null means "every pixel" (pass 1); otherwise only masked pixels are
      /// credited, which is what makes the second pass a targeted repair.</summary>
      public void Accumulate(bool[]? mask, SstvNlmStats? stats, int bands)
      {
        Array.Clear(acc);
        Array.Clear(wsum);

        int n = bands > 0 ? bands : Environment.ProcessorCount;
        n = Math.Clamp(n, 1, Math.Max(1, h / MinBandRows));

        var sweepers = new BandSweeper[n];
        for (int b = 0; b < n; b++) sweepers[b] = new BandSweeper(this, h * b / n, h * (b + 1) / n);

        if (n == 1) sweepers[0].Run(mask, stats != null);
        else System.Threading.Tasks.Parallel.For(0, n, b => sweepers[b].Run(mask, stats != null));

        // folded back in BAND order, never in completion order, so a given options record always
        // produces the same image however the threads happened to interleave
        foreach (var sweeper in sweepers) sweeper.ReduceHalo(acc, wsum);
        if (stats != null)
          foreach (var sweeper in sweepers)
          {
            stats.Evaluated += sweeper.Evaluated;
            stats.FlatTop += sweeper.FlatTop;
            stats.Rejected += sweeper.Rejected;
          }
      }

      /// <summary>Is a padded coordinate a masked IMAGE pixel? Mirror pixels are never masked — their
      /// accumulation is discarded, the real pixel they reflect gathering its own contributions.</summary>
      private bool Masked(bool[] mask, int by, int bx)
      {
        int y = by - marg, x = bx - marg;
        return (uint)y < (uint)h && (uint)x < (uint)w && mask[y * w + x];
      }


      // ----------------------------------------------------------------------------------------------------
      //                                         one row band
      // ----------------------------------------------------------------------------------------------------


      /// <summary>
      /// One thread's share of the accumulation: the image rows <c>[r0, r1)</c>, swept over every
      /// half-plane offset, carrying its own sliding-sum state (plan §6).
      ///
      /// <para>The scheme rests on one structural fact: <b>the offset's row component is never
      /// negative</b>, so the mirror write that makes the symmetry saving possible always lands at a row
      /// at or below the source row, and never more than <c>searchWing</c> rows below it. A band
      /// therefore writes only into <c>[r0, r1 + searchWing)</c>: its own rows go straight into the
      /// shared accumulators, which no other band touches, and the bounded spill past <c>r1</c> goes to
      /// a private halo that is folded in once every band has finished. Both the 2× symmetry saving and
      /// linear speedup, rather than one or the other.</para>
      ///
      /// <para><b>Not bit-identical to the serial form.</b> The plan claimed it would be; that was
      /// wrong. Each output pixel's contributions are summed in a different ORDER here — its
      /// same-band donors interleaved by offset, its previous-band donors arriving in one lump at the
      /// reduction — and floating-point addition is not associative. The difference is at the last bits
      /// of a double and vanishes in the rounding to bytes, but the equivalence test has to be written
      /// as a tolerance, not an equality.</para>
      /// </summary>
      private sealed class BandSweeper
      {
        private readonly Context c;
        private readonly int r0, r1;

        // sliding patch-distance state (one offset at a time)
        private readonly double[] sqrDiffs;
        private readonly double[][] avgX;
        private readonly double[] avgXY;
        private int newPos, oldPos;
        private int nx, ny;

        // the spill into [r1, r1 + searchWing), full padded width; ~1 MB total for PD290 at 8 threads
        private readonly double[] haloAcc, haloWsum;

        public long Evaluated, FlatTop, Rejected;

        public BandSweeper(Context c, int r0, int r1)
        {
          this.c = c; this.r0 = r0; this.r1 = r1;
          sqrDiffs = new double[c.w + 2 * c.patchWing];
          avgX = new double[c.patchSize + 1][];
          for (int i = 0; i <= c.patchSize; i++) avgX[i] = new double[c.w];
          avgXY = new double[c.w];
          haloAcc = new double[c.searchWing * c.pw];
          haloWsum = new double[c.searchWing * c.pw];
        }

        /// <summary>Sweep every half-plane offset over this band, crediting both partners of each
        /// similar pair — 220 pair-offsets rather than 440 gather-offsets. <paramref name="mask"/> null
        /// means "every pixel" (pass 1); otherwise only masked pixels are credited, which is what makes
        /// the second pass a targeted repair.</summary>
        public void Run(bool[]? mask, bool count)
        {
          for (int dy = 0; dy <= c.searchWing; dy++)
            for (int dx = -c.searchWing; dx <= c.searchWing; dx++)
            {
              if (dy == 0 && dx <= 0) continue;             // the centre, and the mirrored half
              ny = dy; nx = dx;
              SweepOffset(mask, count);
            }
        }

        private void SweepOffset(bool[]? mask, bool count)
        {
          InitAvg();
          int w = c.w, marg = c.marg, pw = c.pw;
          for (int y = r0; y < r1; y++)
          {
            int by = marg + y;
            if (c.pRowValid[by] && c.pRowValid[by + ny])
            {
              // whether the mirror write leaves the band is decided by the offset and the row, so it is
              // resolved out here and costs nothing in the inner loop
              bool spill = y + ny >= r1;
              double[] qAcc = spill ? haloAcc : c.acc, qWsum = spill ? haloWsum : c.wsum;
              int rowP = by * pw + marg;
              int rowQPad = (by + ny) * pw + marg + nx;
              int rowQOut = spill ? (y + ny - r1) * pw + marg + nx : rowQPad;

              for (int x = 0; x < w; x++)
              {
                double dbar = avgXY[x] * c.invPatchArea;
                if (count)
                {
                  Evaluated++;
                  if (dbar <= 1.0) FlatTop++;               // inside the kernel's flat top: full weight
                  else if (dbar >= Cutoff) Rejected++;
                }
                if (dbar >= Cutoff) continue;
                double wt = Weight(dbar < 0 ? 0 : dbar);

                int p = rowP + x, q = rowQPad + x;
                // the pair is credited in both directions: P gains a donor at Q and vice versa
                if (mask == null || c.Masked(mask, by, marg + x))
                {
                  double cw = wt / c.pSw[q];
                  c.acc[p] += c.pIn[q] * cw;
                  c.wsum[p] += cw;
                }
                if (mask == null || c.Masked(mask, by + ny, marg + x + nx))
                {
                  double cw = wt / c.pSw[p];
                  qAcc[rowQOut + x] += c.pIn[p] * cw;
                  qWsum[rowQOut + x] += cw;
                }
              }
            }
            if (y < r1 - 1) UpdateAvg(y + c.patchWing + 1);
          }
        }

        /// <summary>Fold the spill into the shared accumulators. Halo rows past the bottom of the image
        /// are dropped, exactly as the serial form drops them: they land in the padded margin, which
        /// <see cref="Resolve"/> never reads.</summary>
        public void ReduceHalo(double[] acc, double[] wsum)
        {
          for (int k = 0; k < c.searchWing; k++)
          {
            int row = r1 + k;
            if (row >= c.h) break;
            int dst = (c.marg + row) * c.pw, src = k * c.pw;
            for (int i = 0; i < c.pw; i++)
            {
              acc[dst + i] += haloAcc[src + i];
              wsum[dst + i] += haloWsum[src + i];
            }
          }
        }

        /// <summary>Prime the vertical sliding sum with the <c>patchSize</c> rows centred on the band's
        /// first row. This re-warm is the whole cost of banding — <c>patchWing</c> extra rows per offset
        /// per band, about 10 % at 30-row bands.</summary>
        private void InitAvg()
        {
          foreach (var row in avgX) Array.Clear(row);
          Array.Clear(avgXY);
          newPos = 0; oldPos = 1;
          for (int y = r0 - c.patchWing; y <= r0 + c.patchWing; y++) UpdateAvg(y);
        }

        /// <summary>Fold row <paramref name="y"/> into the sliding sums: squared differences along the
        /// row, a sliding sum over x into a ring slot, then the ring's oldest slot swapped out of the
        /// vertical sum. The ring holds <c>patchSize + 1</c> rows so the slot being retired is still
        /// intact when the new one is written.</summary>
        private void UpdateAvg(int y)
        {
          int w = c.w, marg = c.marg, pw = c.pw, patchWing = c.patchWing, patchSize = c.patchSize;
          int by = marg + y;
          bool ok = c.pRowValid[by] && c.pRowValid[by + ny];
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
              double d = c.pIn[q] - c.pIn[p];
              sqrDiffs[i] = d * d / (c.pS[p] + c.pS[q]);
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
            double cw = 0.5 / pSw[p];
            Output[i] = (acc[p] + pIn[p] * cw) / (Small + wsum[p] + cw);
          }
        }
      }
    }
  }
}
