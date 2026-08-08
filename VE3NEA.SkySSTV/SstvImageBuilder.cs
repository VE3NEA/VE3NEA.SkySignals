using System;

namespace VE3NEA.SkySSTV
{
  /// <summary>
  /// Incremental image assembly for one pulse train (P7.5): scan lines render onto persistent Y/Cr/Cb
  /// planes as the extractor claims them (and re-render when a dirty rewind revises the grid), and
  /// <see cref="Snapshot"/> materializes the current image — chroma fill, the §6.2 Wiener filter at line
  /// emission, per-pixel gain into the alpha plane — without disturbing the raw planes, so a later
  /// re-render always starts from unfiltered data.
  /// </summary>
  internal sealed class SstvImageBuilder
  {
    private readonly SstvDecodeOptions o;
    private readonly SstvModeSpec spec;
    private readonly double[] y, cr, cb;
    private readonly bool[] hasCr, hasCb;
    private readonly bool[] rowRendered;

    public SstvPulseTrain Train { get; }
    public int ImageId { get; }

    /// <summary>Rows [0, ValidRows) have been rendered at least once. A HIGH-WATER MARK, not a count:
    /// a train that begins rendering mid-image — a comb-seeded train is born back-dated ~100 line
    /// periods — sets this from its first rendered line, leaving black rows INSIDE the span. Use
    /// <see cref="Planes"/>'s per-row flags where that distinction matters (denoise plan §7).</summary>
    public int ValidRows { get; private set; }

    /// <summary>Rows changed since the last <see cref="Snapshot"/>.</summary>
    public bool Dirty { get; private set; }

    /// <summary>Whether a progressive image event has been emitted for this builder. Once true, the train
    /// must be finalized even if it never reaches the <see cref="SstvPulseTrainExtractor.IsImageTrain"/>
    /// completeness gate, so an image shown line-by-line is properly completed (and saved) at the end.</summary>
    public bool Emitted { get; set; }

    public SstvImageBuilder(SstvPulseTrain train, SstvDecodeOptions o, int imageId)
    {
      Train = train;
      this.o = o;
      ImageId = imageId;
      spec = SstvModes.Get(train.Format);
      y = new double[spec.Width * spec.Height];
      cr = new double[spec.Width * spec.Height];
      cb = new double[spec.Width * spec.Height];
      hasCr = new bool[spec.Height];
      hasCb = new bool[spec.Height];
      rowRendered = new bool[spec.Height];
    }

    /// <summary>The absolute sample span one transmitted line occupies — the readiness gate for rendering
    /// it from the rolling brightness buffer.</summary>
    public (double start, double end) LineSpan(int pulseNo)
    {
      double onset = Train.GetLineOnset(pulseNo);
      return (onset, onset + spec.LinePeriodMs / 1000.0 * o.SampleRate * 1.05);
    }

    /// <summary>Render (or re-render) transmitted line <paramref name="pulseNo"/> from the brightness
    /// window onto the planes. Lines outside the image geometry are ignored.</summary>
    public void RenderLine(in BrightnessWindow bw, int pulseNo)
    {
      double onset = Train.GetLineOnset(pulseNo);
      double corr = Train.Regr.CorrFactor;
      // the §6.3 branch weight reads the previous line straight from the ring, so a re-render reproduces
      // it exactly — nothing about it depends on what has already been rendered
      double prevOnset = pulseNo > 0 ? Train.GetLineOnset(pulseNo - 1) : double.NaN;
      double weight = SstvDecoder.WideWeight(bw, spec, o, corr, onset, prevOnset);

      if (spec.Layout == SstvColorLayout.Pd)
      {
        if (pulseNo < 0 || pulseNo >= spec.LineCount || 2 * pulseNo + 1 >= spec.Height) return;
        SstvDecoder.RenderPdLine(bw, spec, o, onset, corr, pulseNo, weight, y, cr, cb);
        ValidRows = Math.Max(ValidRows, 2 * pulseNo + 2);
        rowRendered[2 * pulseNo] = rowRendered[2 * pulseNo + 1] = true;
      }
      else
      {
        if (pulseNo < 0 || pulseNo >= Math.Min(spec.LineCount, spec.Height)) return;
        SstvDecoder.RenderRobotLine(bw, spec, o, onset, corr, pulseNo, weight, y, cr, cb, hasCr, hasCb);
        ValidRows = Math.Max(ValidRows, pulseNo + 1);
        rowRendered[pulseNo] = true;
      }
      Dirty = true;
    }

    /// <summary>Materialize the current image: fill missing chroma rows, apply the Wiener filter over the
    /// valid rows, convert to RGB. The raw planes are copied, never modified.
    ///
    /// <para>The per-pixel Wiener gain used to be written to the image's alpha plane. It is not any more
    /// (denoise plan D16): <see cref="RgbImage.ToBitmap"/> materializes 24bpp and ignores alpha,
    /// <c>SavePng</c> goes through it, and SkyRoof only ever calls <c>ToBitmap()</c> — so the map was
    /// computed, stored and discarded on every snapshot, once per rendered line. <see cref="RgbImage.A"/>
    /// and <c>EnsureAlpha()</c> remain as public API for a confidence overlay that may want them, and
    /// re-enabling the write is one line. The denoise dialog has no use for it either: its NLM derives
    /// reliability from the Wiener detector at denoise time, on the raw planes.</para></summary>
    public RgbImage Snapshot()
    {
      Dirty = false;
      int w = spec.Width, h = spec.Height, rows = ValidRows;
      var img = new RgbImage(w, h);
      if (rows == 0) return img;

      var (sy, sCr, sCb) = FilledPlanes(rows);
      if (o.Denoise.Method == SstvDenoiseMethod.Wiener)
        SstvWienerFilter.Apply(sy, sCr, sCb, w, rows, null, o.Denoise);

      for (int row = 0; row < rows; row++)
        for (int x = 0; x < w; x++)
        {
          int i = row * w + x;
          var (r, g, b) = YCrCb.ToRgb(sy[i], sCr[i], sCb[i]);
          img.Set(x, row, (byte)Math.Round(r), (byte)Math.Round(g), (byte)Math.Round(b));
        }
      return img;
    }

    /// <summary>The RAW reconstruction as Y/Cr/Cb byte planes — chroma filled, no post-filter of any kind
    /// — plus which rows were actually rendered. This is what the denoise dialog re-filters, and the
    /// reason it can re-filter at new parameters without ever compounding (denoise plan §4.1).</summary>
    public SstvImagePlanes Planes()
    {
      int w = spec.Width, h = spec.Height, rows = ValidRows;
      var fy = new double[w * h];
      var fCr = new double[w * h];
      var fCb = new double[w * h];
      if (rows > 0)
      {
        var (sy, sCr, sCb) = FilledPlanes(rows);
        Array.Copy(sy, fy, rows * w);
        Array.Copy(sCr, fCr, rows * w);
        Array.Copy(sCb, fCb, rows * w);
      }
      return SstvImagePlanes.FromValues(fy, fCr, fCb, w, h, spec.ChromaRowStep, rowRendered);
    }

    /// <summary>Copies of the first <paramref name="rows"/> plane rows with the chroma gaps filled — the
    /// common prefix of <see cref="Snapshot"/> and <see cref="Planes"/>. The fill is part of
    /// reconstruction rather than of filtering, which is why <c>hasCr</c>/<c>hasCb</c> and the
    /// alternating-line layout stay decoder internals and never reach the app.</summary>
    private (double[] y, double[] cr, double[] cb) FilledPlanes(int rows)
    {
      int w = spec.Width;
      var sy = new double[rows * w];
      var sCr = new double[rows * w];
      var sCb = new double[rows * w];
      Array.Copy(y, sy, rows * w);
      Array.Copy(cr, sCr, rows * w);
      Array.Copy(cb, sCb, rows * w);
      var sHasCr = new bool[rows];
      var sHasCb = new bool[rows];
      Array.Copy(hasCr, sHasCr, rows);
      Array.Copy(hasCb, sHasCb, rows);

      SstvDecoder.FillMissingChroma(sCr, sHasCr, w, rows);
      SstvDecoder.FillMissingChroma(sCb, sHasCb, w, rows);
      return (sy, sCr, sCb);
    }
  }
}
