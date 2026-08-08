using System;

namespace VE3NEA.SkySSTV
{
  /// <summary>
  /// The raw, unfiltered reconstruction of one image as Y/Cr/Cb byte planes, plus the geometry a
  /// post-filter needs (denoise plan §4.1). This is the denoise dialog's source: it rides along on
  /// <see cref="SstvImageEvent"/> beside the filtered <see cref="RgbImage"/> and is never itself
  /// filtered, so re-applying at new parameters always starts from the original and filters can
  /// never compound however the operator drives the controls.
  ///
  /// <para><b>Byte, not double.</b> <see cref="SstvTones.FreqToValue"/> already clamps every stored
  /// value to 0..255 and the output is rounded to bytes regardless, so byte quantization costs about
  /// 0.3 LSB against row noise of ≥7 luma units. 3 B/px: 230 KB for Robot36, 1.5 MB for PD290.</para>
  ///
  /// <para><b>Chroma is stored filled.</b> The builder's raw chroma has row gaps (Robot36 alternates
  /// components; PD duplicates a pair across two rows), and the fill is part of reconstruction, not
  /// of denoising — so <c>hasCr</c>/<c>hasCb</c> and the alternating-line layout stay decoder
  /// internals. <see cref="ChromaRowStep"/> records what the fill duplicated, because a filter that
  /// does not know half the chroma rows are exact copies will draw false confidence from them
  /// (plan §5.2).</para>
  /// </summary>
  public sealed class SstvImagePlanes
  {
    public int Width { get; }
    public int Height { get; }

    /// <summary>Vertical duplication factor of the chroma planes: 2 for Robot36 and PD (one chroma
    /// per two image rows), 1 for Robot72. Rows within a group are byte-identical after the fill.</summary>
    public int ChromaRowStep { get; }

    public byte[] Y { get; }
    public byte[] Cr { get; }
    public byte[] Cb { get; }

    /// <summary>Which image rows were actually rendered. NOT the same as "below ValidRows": that is a
    /// high-water mark, so a train which begins rendering mid-image (a comb-seeded train is born
    /// back-dated ~100 line periods) leaves black rows INSIDE the valid region. The Wiener shrugs
    /// those off — a black row estimates σ 0, hence gain 1, hence passes through — but they are
    /// zero-variance patches that NLM would treat as perfectly-matching, high-confidence donors
    /// (plan §7).</summary>
    public bool[] RowRendered { get; }

    public SstvImagePlanes(int width, int height, int chromaRowStep)
    {
      Width = width;
      Height = height;
      ChromaRowStep = Math.Max(1, chromaRowStep);
      Y = new byte[width * height];
      Cr = new byte[width * height];
      Cb = new byte[width * height];
      RowRendered = new bool[height];
    }

    private SstvImagePlanes(SstvImagePlanes src)
    {
      Width = src.Width;
      Height = src.Height;
      ChromaRowStep = src.ChromaRowStep;
      Y = (byte[])src.Y.Clone();
      Cr = (byte[])src.Cr.Clone();
      Cb = (byte[])src.Cb.Clone();
      RowRendered = (bool[])src.RowRendered.Clone();
    }

    /// <summary>Wrap a reconstruction's working planes, rounding and clamping to bytes.
    /// <paramref name="rowRendered"/> null means every row was drawn — true of the batch decoder, which
    /// always walks the full line count.</summary>
    internal static SstvImagePlanes FromValues(double[] y, double[] cr, double[] cb, int width,
      int height, int chromaRowStep, bool[]? rowRendered = null)
    {
      var planes = new SstvImagePlanes(width, height, chromaRowStep);
      for (int i = 0; i < planes.Y.Length; i++)
      {
        planes.Y[i] = (byte)Math.Clamp(Math.Round(y[i]), 0, 255);
        planes.Cr[i] = (byte)Math.Clamp(Math.Round(cr[i]), 0, 255);
        planes.Cb[i] = (byte)Math.Clamp(Math.Round(cb[i]), 0, 255);
      }
      if (rowRendered != null) Array.Copy(rowRendered, planes.RowRendered, height);
      else Array.Fill(planes.RowRendered, true);
      return planes;
    }

    /// <summary>First rendered row, or −1 when nothing was rendered.</summary>
    public int FirstRenderedRow
    {
      get
      {
        for (int row = 0; row < Height; row++) if (RowRendered[row]) return row;
        return -1;
      }
    }

    /// <summary>Last rendered row, or −1 when nothing was rendered.</summary>
    public int LastRenderedRow
    {
      get
      {
        for (int row = Height - 1; row >= 0; row--) if (RowRendered[row]) return row;
        return -1;
      }
    }

    /// <summary>Rendered rows as a fraction of the image height — the &gt;0.90 gate that decides
    /// whether denoising is offered (plan §7). Measured over the saved corpus, 83 % of images are
    /// complete and 189 of 214 fall in the 90–100 % band, the largest "partials" missing a single
    /// row; genuinely partial images are 7 %.</summary>
    public double Coverage
    {
      get
      {
        if (Height == 0) return 0;
        int cnt = 0;
        foreach (bool rendered in RowRendered) if (rendered) cnt++;
        return (double)cnt / Height;
      }
    }

    /// <summary>Materialize the RGB image (BT.601, matching the encoder so the synthetic round trip
    /// stays exact). A method rather than a property: it allocates 3 B/px per call, and a property
    /// invites that inside a loop.</summary>
    public RgbImage ToRgb()
    {
      var img = new RgbImage(Width, Height);
      for (int row = 0; row < Height; row++)
        for (int x = 0; x < Width; x++)
        {
          int i = row * Width + x;
          var (r, g, b) = YCrCb.ToRgb(Y[i], Cr[i], Cb[i]);
          img.Set(x, row, (byte)Math.Round(r), (byte)Math.Round(g), (byte)Math.Round(b));
        }
      return img;
    }

    /// <summary>Apply a post-filter, returning a NEW set of planes — this instance is never modified,
    /// which is what lets the dialog re-apply at new parameters without compounding (plan §4.1).
    /// Only the rendered span is filtered; unrendered rows are copied through untouched.</summary>
    public SstvImagePlanes Denoise(SstvDenoiseOptions? options = null)
      => Denoise(options, null);

    /// <summary>The diagnostic form: <paramref name="stats"/> collects the plan §5.6 degeneracy counters
    /// summed over the three planes, which is what the tuning probe reads instead of the picture.</summary>
    internal SstvImagePlanes Denoise(SstvDenoiseOptions? options, SstvNlmStats? stats)
    {
      var o = options ?? new SstvDenoiseOptions();
      var result = new SstvImagePlanes(this);
      if (o.Method == SstvDenoiseMethod.None) return result;

      int first = FirstRenderedRow, last = LastRenderedRow;
      if (first < 0) return result;
      int rows = last - first + 1;

      double[] y = Extract(Y, first, rows);
      double[] cr = Extract(Cr, first, rows);
      double[] cb = Extract(Cb, first, rows);

      if (o.Method == SstvDenoiseMethod.Wiener)
      {
        SstvWienerFilter.Apply(y, cr, cb, Width, rows, null, o);
      }
      else
      {
        var valid = new bool[rows];
        Array.Copy(RowRendered, first, valid, 0, rows);
        SstvNlmFilter.Apply(y, Width, rows, valid, 1.0, 1, o, stats);
        DenoiseChroma(cr, valid, first, rows, o, stats);
        DenoiseChroma(cb, valid, first, rows, o, stats);
      }

      Store(result.Y, y, first, rows);
      Store(result.Cr, cr, first, rows);
      Store(result.Cb, cb, first, rows);
      return result;
    }


    // ----------------------------------------------------------------------------------------------------
    //                                        chroma resampling
    // ----------------------------------------------------------------------------------------------------


    /// <summary>Denoise one chroma plane. With <see cref="SstvDenoiseOptions.NlmNativeChroma"/> the
    /// plane is first collapsed to its native vertical resolution — the resolution it was actually
    /// transmitted at — filtered there, and re-duplicated; otherwise it is filtered as-is, with the
    /// duplicate rows present (the measurable arm B of plan §9.2).
    ///
    /// <para>Collapsing is what makes the noise samples independent again: on the native grid,
    /// adjacent rows are different transmitted lines, so the vertical-difference noise estimator
    /// works without the step-2 correction the Wiener needs on the duplicated plane.</para></summary>
    private void DenoiseChroma(double[] plane, bool[] valid, int first, int rows, SstvDenoiseOptions o,
      SstvNlmStats? stats)
    {
      int step = ChromaRowStep;
      if (!o.NlmNativeChroma || step <= 1)
      {
        // arm B: the duplicated rows are still present, so the noise estimator must step over them
        SstvNlmFilter.Apply(plane, Width, rows, valid, o.WienerChromaK, step, o, stats);
        return;
      }

      // native rows are indexed in ABSOLUTE image coordinates so the pair boundaries stay aligned
      // however the rendered span happens to start
      int firstGroup = first / step, lastGroup = (first + rows - 1) / step;
      int nRows = lastGroup - firstGroup + 1;
      var native = new double[nRows * Width];
      var nativeValid = new bool[nRows];

      for (int k = 0; k < nRows; k++)
      {
        int abs = Math.Clamp((firstGroup + k) * step, first, first + rows - 1);
        Array.Copy(plane, (abs - first) * Width, native, k * Width, Width);
        for (int d = 0; d < step; d++)                       // the group is valid if any row of it is
        {
          int r = (firstGroup + k) * step + d - first;
          if (r >= 0 && r < rows && valid[r]) nativeValid[k] = true;
        }
      }

      // on the native grid adjacent rows are different transmitted lines again, so step 1 is correct
      SstvNlmFilter.Apply(native, Width, nRows, nativeValid, o.WienerChromaK, 1, o, stats);

      for (int r = 0; r < rows; r++)
      {
        int k = (first + r) / step - firstGroup;
        Array.Copy(native, k * Width, plane, r * Width, Width);
      }
    }


    // ----------------------------------------------------------------------------------------------------
    //                                            utilities
    // ----------------------------------------------------------------------------------------------------


    private double[] Extract(byte[] plane, int first, int rows)
    {
      var values = new double[rows * Width];
      for (int i = 0; i < values.Length; i++) values[i] = plane[first * Width + i];
      return values;
    }

    private void Store(byte[] plane, double[] values, int first, int rows)
    {
      for (int i = 0; i < values.Length; i++)
        plane[first * Width + i] = (byte)Math.Clamp(Math.Round(values[i]), 0, 255);
    }
  }
}
