using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using MathNet.Numerics;
using VE3NEA.SkyTlm.IO;
using Xunit;
using Xunit.Abstractions;

namespace VE3NEA.SkySSTV.Tests
{
  /// <summary>
  /// The Wiener window work-off (2026-08-04): reported as "MMSSTV produces a sharper image with more
  /// detail than SkyRoof on the same signal". The §6.3 smoothing probe had already cleared the Wiener on
  /// STRONG bursts and pinned the blur on the Stage-3 low-pass, but that verdict does not carry to weak
  /// ones — the measured luma gain is 0 over 66–75 % of pixels there (median exactly 0), so the output IS
  /// the 9×5 local mean. This probe sweeps the smoothing window, a gain floor, and a decoupled detection
  /// aperture over both SNR regimes, and renders the contact sheets the judgment is made on.
  /// </summary>
  public class SstvWienerWindowProbe
  {
    private static readonly string RecordingsDir =
      @"C:\Users\alsho\AppData\Roaming\Afreet\Products\SkyRoof\Recordings\SSTV";
    private static readonly string OutDir =
      @"C:\Users\alsho\AppData\Local\Temp\claude\c--Proj-Try-FskDemod\c8681680-e5d2-42b9-a144-f823cceafe78\scratchpad\wiener";

    // the captures the §6.3 work-off calibrated on, spanning both regimes
    private static readonly string[] Captures =
    {
      "2026-07-23_21_42_02_UmKA-1.iq.wav",
      "2026-07-01_11_02_25_Monitor-3.iq.wav",
      "2026-04-18_12_36_09_UmKA-1.iq.wav"
    };

    private sealed record Variant(string Tag, Func<SstvDecodeOptions, SstvDecodeOptions> Make);

    private static readonly Variant[] Variants =
    {
      // every variant pins the floor explicitly so the grid stays a true before/after whatever the shipping
      // default is: 01_base9x5 is the pre-2026-08-04 build, 05_floor25 is the one that replaced it
      new("00_off",        b => b with { Denoise = new() { Method = SstvDenoiseMethod.None } }),
      new("01_base9x5",    b => b with { Denoise = b.Denoise with { WienerGainFloor = 0.0 } }),
      new("02_w7x3",       b => b with { Denoise = b.Denoise with { WienerGainFloor = 0.0, WienerWindowW = 7, WienerWindowH = 3 } }),
      new("03_w5x3",       b => b with { Denoise = b.Denoise with { WienerGainFloor = 0.0, WienerWindowW = 5, WienerWindowH = 3 } }),
      new("04_w5x1",       b => b with { Denoise = b.Denoise with { WienerGainFloor = 0.0, WienerWindowW = 5, WienerWindowH = 1 } }),
      new("05_floor25",    b => b with { Denoise = b.Denoise with { WienerGainFloor = 0.25 } }),
      new("06_floor40",    b => b with { Denoise = b.Denoise with { WienerGainFloor = 0.40 } }),
      new("07_det3x3",     b => b with { Denoise = b.Denoise with { WienerGainFloor = 0.0, WienerDetectW = 3, WienerDetectH = 3 } }),
      new("08_w5x3_fl25",  b => b with { Denoise = b.Denoise with { WienerGainFloor = 0.25, WienerWindowW = 5, WienerWindowH = 3 } }),
      new("09_w5x3_det3",  b => b with { Denoise = b.Denoise with { WienerGainFloor = 0.0, WienerWindowW = 5, WienerWindowH = 3,
                                         WienerDetectW = 3, WienerDetectH = 3 } })
    };

    private readonly ITestOutputHelper output;
    public SstvWienerWindowProbe(ITestOutputHelper o) => output = o;

    [ManualFact("Result 2026-08-04. Reference: MMSSTV's own noise reads dx1=+0.489 dy1=+0.180 hf=25.4 % on "
      + "the reported capture, the shipping 9x5 reads dx1=+0.953 dy1=+0.817 hf=1.4 %. Three findings. "
      + "(1) WINDOW SIZE IS NEARLY INERT: 9x5 -> 5x3 moves dx1 by 0.003 on the strong bursts and 0.020 on "
      + "the weak ones, because the window is both the smoothing kernel AND the detection aperture — "
      + "shrinking it makes the variance estimate noisier, so the share of pixels at g=0 RISES (56 -> 66 % "
      + "at Monitor-3 146 s) and cancels the smaller blur. (2) A DECOUPLED DETECTION APERTURE IS HARMFUL: "
      + "WienerDetect 3x3 sharpens 1-px strokes (hf 7.7 -> 9.4 % on the UmKA caption) but HOLLOWS thick "
      + "ones — a 3x3 window inside a letter body reads flat, so the pixel is replaced by a 9x5 mean that "
      + "straddles the edge and the stroke washes out, plainly visible on the RA3PPY card. Rejected; "
      + "keep detect == smooth. (3) THE GAIN FLOOR IS THE ONLY EFFECTIVE LEVER, and it is self-targeting: "
      + "at Monitor-3 146 s floor 0.25/0.40 takes dx1 0.954 -> 0.915/0.868, dy1 0.861 -> 0.738/0.591, "
      + "hf 2.7 -> 5.5/8.2 %, while on the strong 286 s burst it moves dx1 by under 0.005 — it only bites "
      + "where the gain had already collapsed. NOTE the Wiener cannot close the gap alone: with it fully "
      + "OFF dx1 is still +0.70..+0.83 vs MMSSTV's +0.489, the Stage-3 600 Hz low-pass being the floor.")]
    public void WindowSweep()
    {
      Directory.CreateDirectory(OutDir);
      output.WriteLine("reference — MMSSTV noise on the reported capture: dx1=+0.489 dy1=+0.180 hf>0.2c/px=25.4%");
      output.WriteLine("            SkyRoof shipping build, same capture: dx1=+0.953 dy1=+0.817 hf>0.2c/px= 1.4%");
      output.WriteLine("");

      foreach (string name in Captures)
      {
        string wav = Path.Combine(RecordingsDir, name);
        if (!File.Exists(wav)) { output.WriteLine($"MISSING {name}"); continue; }
        var (iq, sr) = WavIqReader.Read(wav);

        var o = new SstvDecodeOptions { SampleRate = sr };
        double[] disc = SstvDecoder.Discriminator(iq, o);
        double[] sync = SstvDecoder.SyncAudio(disc, sr, o);
        var hits = SstvVisDetector.DetectAll(sync, sr);
        var extractor = SstvDecoder.ExtractTrains(sync, sr, hits);

        foreach (var train in extractor.Trains)
        {
          if (!extractor.IsImageTrain(train)) continue;
          var spec = SstvModes.Get(train.Format);
          int firstSync = (int)Math.Round(train.Regr.GetPulseTime(0));
          int margin = (int)(0.5 * sr);
          int dur = (int)(spec.LineCount * spec.LinePeriodMs / 1000.0 * sr);
          int start = Math.Max(0, firstSync - margin);
          int end = Math.Min(iq.Length, firstSync + dur + margin);
          var seg = iq[start..end];

          string shortName = name.Substring(0, 16).Replace("_", "");
          string tag = $"{shortName}_t{firstSync / sr:0}";
          output.WriteLine($"=== {name} @ {firstSync / sr:0}s {train.Format}");
          output.WriteLine($"    {"variant",-14} {"dx1",7} {"dy1",7} {"hf%",7} {"gain",7} {"g=0%",7}");

          var baseOpts = new SstvDecodeOptions
          {
            SampleRate = sr,
            Acquire = false,
            StartSample = firstSync - start
          };

          var sheet = new List<(string, RgbImage)>();
          foreach (var v in Variants)
          {
            var img = SstvDecoder.Decode(seg, train.Format, v.Make(baseOpts));
            var (dx1, dy1, hf) = Texture(img);
            var (mg, z) = GainStats(seg, train.Format, v.Make(baseOpts));
            output.WriteLine($"    {v.Tag,-14} {dx1,7:+0.000} {dy1,7:+0.000} {hf,6:0.0}% {mg,7:0.000} {z,6:0.0}%");
            img.SavePng(Path.Combine(OutDir, $"{tag}_{v.Tag}.png"));
            sheet.Add((v.Tag, img));
          }
          Montage(sheet, Path.Combine(OutDir, $"SHEET_{tag}.png"));
          output.WriteLine("");
        }
      }
      output.WriteLine($"contact sheets in {OutDir}");
    }

    /// <summary>The texture statistics the complaint is really about, measured on the decoded luma the same
    /// way the reported PNGs were: horizontal/vertical lag-1 autocorrelation (how far a pixel's noise or
    /// detail is smeared) and the share of horizontal power above 0.2 cycles/pixel.</summary>
    private static (double dx1, double dy1, double hf) Texture(RgbImage img)
    {
      int w = img.Width, h = img.Height;
      var y = new double[w * h];
      for (int i = 0; i < w * h; i++) y[i] = 0.299 * img.R[i] + 0.587 * img.G[i] + 0.114 * img.B[i];

      double mean = y.Average();
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

    private static (double mean, double zero) GainStats(Complex32[] seg, SstvMode mode, SstvDecodeOptions o)
    {
      if (o.Denoise.Method != SstvDenoiseMethod.Wiener) return (1.0, 0.0);
      // the batch path does not surface the gain plane; recompute it over the same geometry. The planes
      // come back through the exact RGB inverse (the P6(d) prototype's route), so heavily clipped noise
      // reads slightly low here — an indicator, not the shipping gain.
      var probe = SstvDecoder.Decode(seg, mode, o with { Denoise = new() { Method = SstvDenoiseMethod.None } });
      int w = probe.Width, h = probe.Height;
      var y = new double[w * h]; var cr = new double[w * h]; var cb = new double[w * h];
      for (int i = 0; i < w * h; i++)
      {
        var (yy, rr, bb) = YCrCb.FromRgb(probe.R[i], probe.G[i], probe.B[i]);
        y[i] = yy; cr[i] = rr; cb[i] = bb;
      }
      var gain = new double[w * h];
      SstvWienerFilter.Apply(y, cr, cb, w, h, gain, o.Denoise);
      return (gain.Average(), 100.0 * gain.Count(v => v < 0.02) / gain.Length);
    }

    /// <summary>Label-free grid montage — 5 variants per row at 1:1, in the declared variant order.</summary>
    private static void Montage(List<(string tag, RgbImage img)> items, string path)
    {
      int cols = 5, pad = 4, labelH = 14;
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

    private static Bitmap ToBitmap(RgbImage img)
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
