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

    public SstvPulseTrain Train { get; }
    public int ImageId { get; }

    /// <summary>Rows [0, ValidRows) have been rendered at least once.</summary>
    public int ValidRows { get; private set; }

    /// <summary>Rows changed since the last <see cref="Snapshot"/>.</summary>
    public bool Dirty { get; private set; }

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
    }

    /// <summary>The absolute sample span one transmitted line occupies — the readiness gate for rendering
    /// it from the rolling brightness buffer.</summary>
    public (double start, double end) LineSpan(int pulseNo)
    {
      double onset = Train.Regr.GetPulseTime(pulseNo);
      return (onset, onset + spec.LinePeriodMs / 1000.0 * o.SampleRate * 1.05);
    }

    /// <summary>Render (or re-render) transmitted line <paramref name="pulseNo"/> from the brightness
    /// window onto the planes. Lines outside the image geometry are ignored.</summary>
    public void RenderLine(in BrightnessWindow bw, int pulseNo)
    {
      double onset = Train.Regr.GetPulseTime(pulseNo);
      double corr = Train.Regr.CorrFactor;
      if (spec.Layout == SstvColorLayout.Pd)
      {
        if (pulseNo < 0 || pulseNo >= spec.LineCount || 2 * pulseNo + 1 >= spec.Height) return;
        SstvDecoder.RenderPdLine(bw, spec, o, onset, corr, pulseNo, y, cr, cb);
        ValidRows = Math.Max(ValidRows, 2 * pulseNo + 2);
      }
      else
      {
        if (pulseNo < 0 || pulseNo >= Math.Min(spec.LineCount, spec.Height)) return;
        SstvDecoder.RenderRobotLine(bw, spec, o, onset, corr, pulseNo, y, cr, cb, hasCr, hasCb);
        ValidRows = Math.Max(ValidRows, pulseNo + 1);
      }
      Dirty = true;
    }

    /// <summary>Materialize the current image: fill missing chroma rows, apply the Wiener filter over the
    /// valid rows (its per-pixel luma gain becomes the alpha plane; unrendered rows get alpha 0), convert
    /// to RGB. The raw planes are copied, never modified.</summary>
    public RgbImage Snapshot()
    {
      Dirty = false;
      int w = spec.Width, h = spec.Height, rows = ValidRows;
      var img = new RgbImage(w, h);
      byte[] alpha = img.EnsureAlpha();
      Array.Clear(alpha);
      if (rows == 0) return img;

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
      double[]? gain = null;
      if (o.WienerEnabled)
      {
        gain = new double[rows * w];
        SstvWienerFilter.Apply(sy, sCr, sCb, w, rows, gain);
      }

      for (int row = 0; row < rows; row++)
        for (int x = 0; x < w; x++)
        {
          int i = row * w + x;
          var (r, g, b) = YCrCb.ToRgb(sy[i], sCr[i], sCb[i]);
          img.Set(x, row, (byte)Math.Round(r), (byte)Math.Round(g), (byte)Math.Round(b));
          alpha[i] = gain != null ? (byte)Math.Round(255 * Math.Clamp(gain[i], 0.0, 1.0)) : (byte)255;
        }
      return img;
    }
  }
}
