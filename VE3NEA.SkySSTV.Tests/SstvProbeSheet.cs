using System;
using System.Collections.Generic;
using System.Drawing;

namespace VE3NEA.SkySSTV.Tests
{
  /// <summary>
  /// The shared apparatus of the visual work-off probes: the texture statistics a verdict is argued
  /// with, and the contact sheet it is judged on. Both were written for the 2026-08-04 Wiener window
  /// sweep and are reused unchanged by the denoise probe, so the two sets of numbers are directly
  /// comparable — <c>dx1</c> in particular is the standing texture yardstick, MMSSTV reading
  /// <b>+0.489</b> where the shipping Wiener reads +0.953.
  /// </summary>
  internal static class SstvProbeSheet
  {
    /// <summary>The texture statistics the over-smoothing complaint is really about, measured on the
    /// decoded luma the same way the reported PNGs were: horizontal/vertical lag-1 autocorrelation (how
    /// far a pixel's noise or detail is smeared) and the share of horizontal power above 0.2
    /// cycles/pixel.</summary>
    public static (double dx1, double dy1, double hf) Texture(RgbImage img)
    {
      int w = img.Width, h = img.Height;
      var y = new double[w * h];
      for (int i = 0; i < w * h; i++) y[i] = 0.299 * img.R[i] + 0.587 * img.G[i] + 0.114 * img.B[i];

      double mean = 0;
      for (int i = 0; i < y.Length; i++) mean += y[i];
      mean /= y.Length;

      double num1 = 0, num2 = 0, den = 0;
      for (int r = 0; r < h; r++)
        for (int c = 0; c < w; c++)
        {
          double v = y[r * w + c] - mean;
          den += v * v;
          if (c + 1 < w) num1 += v * (y[r * w + c + 1] - mean);
          if (r + 1 < h) num2 += v * (y[(r + 1) * w + c] - mean);
        }
      double dx1 = den > 0 ? num1 / den : 0, dy1 = den > 0 ? num2 / den : 0;

      // share of horizontal power above 0.2 cycles/pixel, from the row-detrended luma
      double hi = 0, tot = 0;
      int nb = w / 2;
      for (int r = 0; r < h; r++)
      {
        double rm = 0;
        for (int c = 0; c < w; c++) rm += y[r * w + c];
        rm /= w;
        for (int kf = 1; kf <= nb; kf++)
        {
          double re = 0, im = 0;
          for (int c = 0; c < w; c++)
          {
            double a = -2 * Math.PI * kf * c / w, v = y[r * w + c] - rm;
            re += v * Math.Cos(a); im += v * Math.Sin(a);
          }
          double p = re * re + im * im;
          tot += p;
          if ((double)kf / w > 0.2) hi += p;
        }
      }
      return (dx1, dy1, tot > 0 ? 100 * hi / tot : 0);
    }

    /// <summary>Horizontal lag-1 autocorrelation of the chroma planes, averaged over Cr and Cb. The luma
    /// statistics are blind to how chroma was handled, so this is the only number that can separate the
    /// §9.2 arms; and it has to be the HORIZONTAL lag, because both arms duplicate chroma rows on output
    /// and the vertical lag therefore reads ≈1 by construction whatever the filter did.</summary>
    public static double ChromaDx1(SstvImagePlanes planes) => ChromaAcf(planes, horizontal: true);

    /// <summary>The vertical companion of <see cref="ChromaDx1"/>, at the CHROMA row step so the
    /// duplicated rows do not make it read 1 by construction. Its ratio to the horizontal figure is what
    /// says whether a chroma artifact is elongated along the scan line.</summary>
    public static double ChromaDy1(SstvImagePlanes planes) => ChromaAcf(planes, horizontal: false);

    private static double ChromaAcf(SstvImagePlanes planes, bool horizontal)
    {
      double sum = 0;
      int lag = horizontal ? 1 : planes.ChromaRowStep;
      foreach (byte[] plane in new[] { planes.Cr, planes.Cb })
      {
        double mean = 0;
        int n = 0;
        for (int row = 0; row < planes.Height; row++)
        {
          if (!planes.RowRendered[row]) continue;
          for (int x = 0; x < planes.Width; x++) { mean += plane[row * planes.Width + x]; n++; }
        }
        if (n == 0) continue;
        mean /= n;

        double num = 0, den = 0;
        for (int row = 0; row < planes.Height; row++)
        {
          if (!planes.RowRendered[row]) continue;
          for (int x = 0; x < planes.Width; x++)
          {
            double v = plane[row * planes.Width + x] - mean;
            den += v * v;
            if (horizontal)
            {
              if (x + lag < planes.Width) num += v * (plane[row * planes.Width + x + lag] - mean);
            }
            else if (row + lag < planes.Height && planes.RowRendered[row + lag])
              num += v * (plane[(row + lag) * planes.Width + x] - mean);
          }
        }
        if (den > 0) sum += num / den;
      }
      return sum / 2.0;
    }

    /// <summary>Grid montage — <paramref name="cols"/> variants per row at 1:1, in the declared variant
    /// order. 1:1 and not fit-to-window on purpose: the artifact under judgment is one-pixel detail
    /// versus speckle, which a downscaled preview destroys.</summary>
    public static void Montage(List<(string tag, RgbImage img)> items, string path, int cols = 5)
    {
      if (items.Count == 0) return;
      int pad = 4, labelH = 14;
      int iw = items[0].img.Width, ih = items[0].img.Height;
      int rows = (items.Count + cols - 1) / cols;
      using var bmp = new Bitmap(cols * (iw + pad) + pad, rows * (ih + pad + labelH) + pad);
      using var g = Graphics.FromImage(bmp);
      g.Clear(Color.FromArgb(24, 24, 24));
      using var font = new Font("Consolas", 8);
      for (int i = 0; i < items.Count; i++)
      {
        int cx = i % cols, cy = i / cols;
        int x = pad + cx * (iw + pad), yy = pad + cy * (ih + pad + labelH);
        g.DrawString(items[i].tag, font, Brushes.White, x, yy);
        using var tile = ToBitmap(items[i].img);
        g.DrawImageUnscaled(tile, x, yy + labelH);
      }
      bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }

    public static Bitmap ToBitmap(RgbImage img)
    {
      var bmp = new Bitmap(img.Width, img.Height);
      for (int y = 0; y < img.Height; y++)
        for (int x = 0; x < img.Width; x++)
        {
          var (r, gg, b) = img.Get(x, y);
          bmp.SetPixel(x, y, Color.FromArgb(r, gg, b));
        }
      return bmp;
    }
  }
}
