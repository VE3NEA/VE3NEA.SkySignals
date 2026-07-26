using System;
using System.IO;
using VE3NEA.SkyTlm.IO;
using Xunit;
using Xunit.Abstractions;

namespace VE3NEA.SkySSTV.Tests
{
  /// <summary>
  /// The §6.3 adaptive-bandwidth work-off (2026-07-25): the probes that isolated the over-smoothing to the
  /// Stage-3 low-pass rather than the §6.2 Wiener post-filter, calibrated the AdaptiveSigmaLow/High
  /// defaults, and hold the before/after corpus render. Reported as "high-SNR images are over-smoothed,
  /// small text unreadable" on 20260723_215206_UmKA-1_Robot36_4.png.
  /// </summary>
  public class SstvSmoothingProbe
  {
    private static readonly string RecordingsDir =
      @"C:\Users\alsho\AppData\Roaming\Afreet\Products\SkyRoof\Recordings\SSTV";
    private static readonly string OutDir =
      @"C:\Users\alsho\AppData\Local\Temp\claude\c--Proj-DSP-SkyRoof\973dba30-bf77-4496-8fc0-a737fc060714\scratchpad\probe";

    private readonly ITestOutputHelper output;
    public SstvSmoothingProbe(ITestOutputHelper o) => output = o;

    [ManualFact("Result 2026-07-25 — the DIAGNOSIS. Bandwidth × Wiener grid on the 07-23 21:42 capture's two "
      + "bursts (286 s strong, 566 s weak). The Wiener is NOT the cause: at 600 Hz the strong burst's caption "
      + "line is equally illegible with it on and off (its mean luma gain over that text is 0.82 — it is not "
      + "collapsing text to the local mean), while widening to 900/1200/1600 Hz makes the caption readable "
      + "with the Wiener left on. Decoded row noise, strong burst: 0.81 @600 (essentially noiseless, and STILL "
      + "smeared — the smoking gun) / 3.8 @900 / 7.1 @1200 / 12.9 @1600; weak burst 12.5 / — / 27.0 / 33.1, "
      + "where the narrow branch genuinely earns its keep. PixelWindowFraction 0.5→1.0 buys only ~13 % noise "
      + "(the brightness noise is already correlated over ~1 pixel), so it was left alone.")]
    public void BwWienerGrid()
    {
      Directory.CreateDirectory(OutDir);
      string wav = Path.Combine(RecordingsDir, "2026-07-23_21_42_02_UmKA-1.iq.wav");
      var (iq, sr) = WavIqReader.Read(wav);
      output.WriteLine($"{iq.Length / sr:0.0}s @ {sr}");

      var o = new SstvDecodeOptions { SampleRate = sr };
      double[] disc = SstvDecoder.Discriminator(iq, o);
      double[] sync = SstvDecoder.SyncAudio(disc, sr, o);
      var hits = SstvVisDetector.DetectAll(sync, sr);
      var extractor = SstvDecoder.ExtractTrains(sync, sr, hits);

      int id = 0;
      foreach (var train in extractor.Trains)
      {
        if (!extractor.IsImageTrain(train)) continue;
        var spec = SstvModes.Get(train.Format);
        int firstSync = (int)Math.Round(train.Regr.GetPulseTime(0));
        output.WriteLine($"image {id}: {train.Format} @ {firstSync / sr:0.0}s");
        id++;
        string tag = $"t{firstSync / sr:0}";

        int margin = (int)(0.5 * sr);
        int dur = (int)(spec.LineCount * spec.LinePeriodMs / 1000.0 * sr);
        int start = Math.Max(0, firstSync - margin);
        int end = Math.Min(iq.Length, firstSync + dur + margin);
        var seg = iq[start..end];

        foreach (double bw in new[] { 600.0, 900.0, 1200.0, 1600.0, 2000.0 })
          foreach (bool wiener in new[] { true, false })
          {
            var img = SstvDecoder.Decode(seg, train.Format, new SstvDecodeOptions
            {
              SampleRate = sr,
              Acquire = false,
              StartSample = firstSync - start,
              BrightnessBwHz = bw,
              WienerEnabled = wiener
            });
            string path = Path.Combine(OutDir, $"{tag}_bw{bw:0}_{(wiener ? "wien" : "raw")}.png");
            img.SavePng(path);
            output.WriteLine($"  bw={bw} wiener={wiener} -> {Path.GetFileName(path)}");
          }

        // pixel-window fraction, at the widest useful bandwidth
        foreach (double frac in new[] { 1.0, 0.5, 0.25 })
        {
          var img = SstvDecoder.Decode(seg, train.Format, new SstvDecodeOptions
          {
            SampleRate = sr,
            Acquire = false,
            StartSample = firstSync - start,
            BrightnessBwHz = 1600.0,
            WienerEnabled = false,
            PixelWindowFraction = frac
          });
          img.SavePng(Path.Combine(OutDir, $"{tag}_bw1600_raw_frac{frac:0.00}.png"));
        }
      }
    }

    // before/after on every decodable real burst: the locked adaptive defaults vs the fixed-narrow
    // decoder (BrightnessWideBwHz = 0 disables the second branch)
    [ManualFact("Result 2026-07-25 — the LOCK. Before/after over all 45 corpus bursts at the locked defaults "
      + "(wide 1200, ramp 35→65). Clear wins where σ is low: 07-23 286 s (caption 'MOON 2024/11/08 UMKA-1 "
      + "RS40S' smear → legible, spaceTV logo and lunar terminator crisp), UTMN2 182 s (SPUTNIX logo, face, "
      + "background stars), SAKHACUBE 184 s ('RS18S', figures, road markings). Unchanged where σ is high: the "
      + "07-23 566 s burst, Monitor-3 135 s and the below-FM-threshold 04-18 capture are pixel-identical to "
      + "the fixed-narrow decoder; the Monitor-3 285 s text card sits at ≈0.1 weight and is a wash (its text "
      + "is large enough that 600 Hz never hurt it).")]
    public void AdaptiveBeforeAfter()
    {
      Directory.CreateDirectory(OutDir);
      foreach (string wav in Directory.GetFiles(RecordingsDir, "*.iq.wav"))
      {
        var (iq, sr) = WavIqReader.Read(wav);
        string stem = Path.GetFileNameWithoutExtension(wav).Replace(".iq", "");
        var o = new SstvDecodeOptions { SampleRate = sr };
        double[] discDet = SstvDecoder.Discriminator(iq, o);
        double[] sync = SstvDecoder.SyncAudio(discDet, sr, o);
        var ex = SstvDecoder.ExtractTrains(sync, sr, SstvVisDetector.DetectAll(sync, sr));

        foreach (var train in ex.Trains)
        {
          if (!ex.IsImageTrain(train)) continue;
          var spec = SstvModes.Get(train.Format);
          int firstSync = (int)Math.Round(train.Regr.GetPulseTime(0));
          int margin = (int)(0.5 * sr);
          int dur = (int)(spec.LineCount * spec.LinePeriodMs / 1000.0 * sr);
          int start = Math.Max(0, firstSync - margin);
          int end = Math.Min(iq.Length, firstSync + dur + margin);
          var seg = iq[start..end];
          string tag = $"{stem}_{firstSync / sr:0}s";

          foreach (var (name, wideBw) in new[] { ("before", 0.0), ("after", 1200.0) })
          {
            var img = SstvDecoder.Decode(seg, train.Format, new SstvDecodeOptions
            {
              SampleRate = sr,
              Acquire = false,
              StartSample = firstSync - start,
              BrightnessWideBwHz = wideBw
            });
            img.SavePng(Path.Combine(OutDir, $"ba_{tag}_{name}.png"));
          }
          output.WriteLine($"{tag} {train.Format}");
        }
      }
    }

    // the production path: push the capture through the streaming decoder in odd blocks (dual brightness
    // ring, re-renders and all) and save what SkyRoof would have shown
    [ManualFact("Result 2026-07-25: the production streaming path (dual brightness rings, odd 7919-sample "
      + "pushes, live re-renders) reproduces the batch improvement on the 07-23 286 s burst. Streaming vs "
      + "batch differs by mean 2.4 / max 248 counts, against a pre-adaptive baseline of mean 1.2 / max 209 — "
      + "the two paths never matched exactly (segment-vs-stream FIR warm-up and a slightly different train "
      + "grid), and more video bandwidth turns the same timing difference into a larger pixel difference.")]
    public void StreamingAdaptive()
    {
      Directory.CreateDirectory(OutDir);
      string wav = Path.Combine(RecordingsDir, "2026-07-23_21_42_02_UmKA-1.iq.wav");
      var (iq, sr) = WavIqReader.Read(wav);

      using var dec = new SstvDecoder(new SstvDecodeOptions { SampleRate = sr });
      dec.ImageCompleted += e =>
      {
        e.Image.SavePng(Path.Combine(OutDir, $"stream_{e.StartSeconds:0}s_{e.Mode}.png"));
        output.WriteLine($"image {e.ImageId} {e.Mode} @{e.StartSeconds:0.0}s rows={e.ValidRows}");
      };
      for (int at = 0; at < iq.Length; at += 7919)
        dec.Process(iq.AsSpan(at, Math.Min(7919, iq.Length - at)));
      dec.Flush();
    }

    // per-line σ of the wide branch, as SstvDecoder.WideWeight measures it — calibrates the
    // AdaptiveSigmaLow/High defaults against real bursts of known quality
    [ManualFact("Result 2026-07-25 — the CALIBRATION behind AdaptiveSigmaLow/High. Per-burst σ (pre-integration, "
      + "0..255 luma units), median across x / p10..med..p90 across lines: 07-23 286 s 22/34/58, 07-23 566 s "
      + "41/78/136, Monitor-3 285 s 38/62/104, Monitor-3 135 s 114/208/262, 04-18 184/224/257. The 25th-"
      + "percentile variant tracks the median within 5 % on every burst, so the within-burst spread is real "
      + "fading and not the picture's own vertical detail — the estimator needs no content guard.")]
    public void SigmaCalibration()
    {
      foreach (string file in new[]
      {
        "2026-07-23_21_42_02_UmKA-1.iq.wav",       // 286 s strong (readable caption), 566 s weak
        "2026-07-01_11_02_25_Monitor-3.iq.wav",    // the P6(c) reference text card
        "2026-04-18_12_36_09_UmKA-1.iq.wav"        // below FM threshold
      })
      {
        string wav = Path.Combine(RecordingsDir, file);
        if (!File.Exists(wav)) continue;
        var (iq, sr) = WavIqReader.Read(wav);
        var o = new SstvDecodeOptions { SampleRate = sr };
        double[] disc = SstvDecoder.Discriminator(iq, o with { ChannelBwHz = o.VideoChannelBwHz });
        double[] discDet = SstvDecoder.Discriminator(iq, o);
        double[] sync = SstvDecoder.SyncAudio(discDet, sr, o);
        var ex = SstvDecoder.ExtractTrains(sync, sr, SstvVisDetector.DetectAll(sync, sr));
        SstvDecoder.Brightness(disc, sr, o, out double[]? wide);

        foreach (var train in ex.Trains)
        {
          if (!ex.IsImageTrain(train)) continue;
          var spec = SstvModes.Get(train.Format);
          var sigmas = new System.Collections.Generic.List<double>();
          for (int line = 1; line < spec.LineCount; line++)
          {
            double s = Sigma(wide!, spec, sr, train.Regr.CorrFactor,
              train.GetLineOnset(line), train.GetLineOnset(line - 1));
            if (s > 0) sigmas.Add(s);
          }
          if (sigmas.Count == 0) continue;
          var q25 = new System.Collections.Generic.List<double>();
          for (int line = 1; line < spec.LineCount; line++)
          {
            double s = Sigma(wide!, spec, sr, train.Regr.CorrFactor,
              train.GetLineOnset(line), train.GetLineOnset(line - 1), 0.25);
            if (s > 0) q25.Add(s);
          }
          sigmas.Sort();
          q25.Sort();
          output.WriteLine($"{file} @{train.Regr.GetPulseTime(0) / sr:0}s {train.Format}: " +
            $"med50: p10={sigmas[sigmas.Count / 10]:0.0} med={sigmas[sigmas.Count / 2]:0.0} " +
            $"p90={sigmas[sigmas.Count * 9 / 10]:0.0} | q25: p10={q25[q25.Count / 10]:0.0} " +
            $"med={q25[q25.Count / 2]:0.0} p90={q25[q25.Count * 9 / 10]:0.0}");
        }
      }
    }

    /// <summary>The <c>SstvDecoder.WideWeight</c> statistic, reproduced here so the probe can print it.</summary>
    private static double Sigma(double[] wide, SstvModeSpec spec, double sr, double corr,
      double onset, double prevOnset, double q = 0.5)
    {
      const int probes = 128;
      double step = spec.LinePeriodMs / 1000.0 * sr * corr / probes;
      var absd = new double[probes];
      int cnt = 0;
      for (int k = 0; k < probes; k++)
      {
        long a = (long)Math.Round(onset + k * step), b = (long)Math.Round(prevOnset + k * step);
        if (a >= 0 && a < wide.Length && b >= 0 && b < wide.Length)
          absd[cnt++] = Math.Abs(wide[a] - wide[b]);
      }
      if (cnt < probes / 2) return 0;
      Array.Sort(absd, 0, cnt);
      // |N(0,s)| quantile q sits at s*Phi^-1((1+q)/2): 0.6745 for the median, 0.3186 for the 25th
      double scale = q <= 0.25 ? 0.3186 : 0.6745;
      return absd[(int)(cnt * q)] / scale / Math.Sqrt(2.0) * 255.0 / SstvTones.Span;
    }

    // noise-free closed loop: how much of the resolution loss is the Stage-3 low-pass alone?
    [ManualFact("resolution probe")]
    public void SyntheticResolution()
    {
      Directory.CreateDirectory(OutDir);
      var spec = SstvModes.Get(SstvMode.Robot36);
      var src = TestCard(spec.Width, spec.Height);
      src.SavePng(Path.Combine(OutDir, "res_source.png"));
      var iq = SstvEncoder.Encode(src, SstvMode.Robot36, new SstvEncoderOptions { IncludeVis = true });

      foreach (double bw in new[] { 600.0, 900.0, 1200.0, 1600.0, 2400.0 })
      {
        var img = SstvDecoder.Decode(iq, SstvMode.Robot36, new SstvDecodeOptions
        {
          SampleRate = 48000.0,
          BrightnessBwHz = bw,
          WienerEnabled = false
        });
        img.SavePng(Path.Combine(OutDir, $"res_bw{bw:0}.png"));
        output.WriteLine($"bw={bw} psnr={Psnr(src, img):0.0}");
      }
    }

    /// <summary>Gray test card: a 1/2/3/4-pixel vertical-bar wedge plus small rendered text.</summary>
    private static RgbImage TestCard(int w, int h)
    {
      using var bmp = new System.Drawing.Bitmap(w, h);
      using (var g = System.Drawing.Graphics.FromImage(bmp))
      {
        g.Clear(System.Drawing.Color.Black);
        int y0 = 20;
        foreach (int period in new[] { 2, 4, 6, 8 })
        {
          for (int x = 0; x < w; x++)
            if ((x / (period / 2)) % 2 == 0)
              g.FillRectangle(System.Drawing.Brushes.White, x, y0, 1, 24);
          y0 += 30;
        }
        using var font = new System.Drawing.Font("Arial", 7);
        g.DrawString("ABCDEFGH 0123456789 the quick brown fox", font,
          System.Drawing.Brushes.White, 4, 150);
        using var font2 = new System.Drawing.Font("Arial", 10);
        g.DrawString("UMKA-1 RS40S 145.800", font2, System.Drawing.Brushes.White, 4, 180);
      }
      return RgbImage.FromBitmap(bmp);
    }

    private static double Psnr(RgbImage a, RgbImage b)
    {
      double se = 0;
      for (int i = 0; i < a.R.Length; i++)
      {
        se += (a.R[i] - b.R[i]) * (double)(a.R[i] - b.R[i]);
        se += (a.G[i] - b.G[i]) * (double)(a.G[i] - b.G[i]);
        se += (a.B[i] - b.B[i]) * (double)(a.B[i] - b.B[i]);
      }
      return 10 * Math.Log10(255.0 * 255.0 / (se / (3.0 * a.R.Length)));
    }
  }
}
