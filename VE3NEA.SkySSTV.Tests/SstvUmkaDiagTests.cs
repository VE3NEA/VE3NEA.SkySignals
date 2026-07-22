using System;
using System.IO;
using VE3NEA.SkyTlm.IO;
using Xunit;
using Xunit.Abstractions;

namespace VE3NEA.SkySSTV.Tests
{
  // temporary diagnostic harness for the 2026-07-20 UmKA-1 sync failure
  public class SstvUmkaDiagTests
  {
    private static readonly string RecordingsDir =
      @"C:\Users\alsho\AppData\Roaming\Afreet\Products\SkyRoof\Recordings\SSTV";

    private readonly ITestOutputHelper output;
    private readonly System.Text.StringBuilder log = new();
    public SstvUmkaDiagTests(ITestOutputHelper o) => output = o;

    private void Log(string s) { output.WriteLine(s); log.AppendLine(s); }

    [ManualFact("survey")]
    public void Survey()
    {
      foreach (string wav in Directory.GetFiles(RecordingsDir, "*.iq.wav"))
      {
        try { SurveyFile(wav); }
        catch (Exception e) { Log($"{Path.GetFileName(wav)}: {e.GetType().Name} {e.Message}"); }
      }
      File.WriteAllText(@"C:\Users\alsho\AppData\Local\Temp\claude\c--Proj-DSP-SkyRoof\ab069f8d-3f83-4eb6-bde8-512165668d0b\scratchpad\umka_survey.txt", log.ToString());
    }

    private void SurveyFile(string wav)
    {
      var (iq, sr) = WavIqReader.Read(wav);
      double fs = sr;
      var o = new SstvDecodeOptions { SampleRate = fs };
      double[] disc = SstvDecoder.Discriminator(iq, o);
      double[] sync = SstvDecoder.SyncAudio(disc, fs, o);
      var hits = SstvVisDetector.DetectAll(sync, fs);
      var ex = SstvDecoder.ExtractTrains(sync, fs, hits);
      int imageTrains = 0; foreach (var t in ex.Trains) if (ex.IsImageTrain(t)) imageTrains++;
      Log($"=== {Path.GetFileName(wav)} {iq.Length / fs:0.0}s  VIS={hits.Count} trains={ex.Trains.Count} imageTrains={imageTrains} ===");

      foreach (double syncMs in new[] { 9.0, 20.0 })
      {
        var det = new SstvPulseDetector(fs, syncMs) { Threshold = 0.18 };
        var pulses = det.Detect(sync);
        int i0 = 0;
        for (int i = 1; i <= pulses.Count; i++)
        {
          bool end = i == pulses.Count || (pulses[i].Time - pulses[i - 1].Time) > 3 * fs;
          if (!end) continue;
          int n = i - i0;
          if (n >= 8)
          {
            var gaps = new System.Collections.Generic.List<double>();
            for (int k = i0 + 1; k < i; k++) gaps.Add(pulses[k].Time - pulses[k - 1].Time);
            gaps.Sort();
            double medGap = gaps[gaps.Count / 2];
            double t0 = pulses[i0].Time / fs, t1 = pulses[i - 1].Time / fs;
            string modeFit = "none"; double bestErr = 999;
            foreach (var spec in SstvModes.All)
            {
              double nomLine = spec.LinePeriodMs / 1000.0 * fs;
              for (int mult = 1; mult <= 4; mult++)
              {
                double err = (medGap / mult - nomLine) / nomLine;
                if (Math.Abs(err) < Math.Abs(bestErr)) { bestErr = err; modeFit = $"{spec.Mode}/x{mult}"; }
              }
            }
            Log($"  [{syncMs:0}ms] BURST {t0:0.0}-{t1:0.0}s n={n} med={medGap / fs * 1000:0.0}ms " +
                $"fit={modeFit} err={bestErr * 100:+0.0;-0.0}% {(Math.Abs(bestErr) > 0.03 ? "*OUT-OF-GATE*" : "")}");
          }
          i0 = i;
        }
      }
    }

    [ManualFact("decode-old-umka")]
    public void DecodeOldUmka()
    {
      foreach (string name in new[] { "2026-04-18_12_36_09_UmKA-1.iq.wav", "2026-04-19_12_19_50_UmKA-1.iq.wav" })
      {
        string wav = Path.Combine(RecordingsDir, name);
        if (!File.Exists(wav)) { Log($"absent {name}"); continue; }
        var (iq, sr) = WavIqReader.Read(wav);
        double fs = sr;
        using var dec = new SstvDecoder(new SstvDecodeOptions { SampleRate = fs });
        int idx = 0;
        dec.ImageCompleted += e =>
        {
          string p = $@"C:\Users\alsho\AppData\Local\Temp\claude\c--Proj-DSP-SkyRoof\ab069f8d-3f83-4eb6-bde8-512165668d0b\scratchpad\old_{name.Substring(5, 5)}_{idx}.png";
          e.Image.SavePng(p);
          Log($"  {name}: image {idx} {e.Mode} start={e.StartSeconds:0.0}s rows={e.ValidRows} -> {Path.GetFileName(p)}");
          idx++;
        };
        int block = (int)fs;
        for (int at = 0; at < iq.Length; at += block)
          dec.Process(iq.AsSpan(at, Math.Min(block, iq.Length - at)));
        dec.Flush();
        Log($"{name}: {idx} completed images");
      }
      File.WriteAllText(@"C:\Users\alsho\AppData\Local\Temp\claude\c--Proj-DSP-SkyRoof\ab069f8d-3f83-4eb6-bde8-512165668d0b\scratchpad\umka_old.txt", log.ToString());
    }

    [ManualFact("signal-presence")]
    public void SignalPresence()
    {
      string wav = Path.Combine(RecordingsDir, "2026-07-20_12_04_08_UmKA-1.iq.wav");
      var (iq, sr) = WavIqReader.Read(wav);
      double fs = sr;
      Log($"file {iq.Length / fs:0.0}s");
      var o = new SstvDecodeOptions { SampleRate = fs };
      double[] disc = SstvDecoder.Discriminator(iq, o);

      // per-second: mean |iq| (RF carrier presence) and discriminator std (FM modulation depth = a signal)
      int win = (int)fs;
      Log("sec  |iq|mag   discStd(Hz)");
      for (int s = 0; s + win <= iq.Length; s += win)
      {
        double mag = 0, mean = 0;
        for (int i = s; i < s + win; i++) { mag += iq[i].Magnitude; mean += disc[i]; }
        mag /= win; mean /= win;
        double var = 0;
        for (int i = s; i < s + win; i++) { double d = disc[i] - mean; var += d * d; }
        double std = Math.Sqrt(var / win);
        // only print seconds that look like a signal (elevated magnitude or modulation)
        if (mag > 0 || std > 0)
        {
          int sec = s / win;
          // compact: mark strong seconds
          string mark = std > 300 ? " <== modulated" : "";
          if (sec % 5 == 0 || std > 300)
            Log($"{sec,3}  {mag:0.0000}   {std:0}{mark}");
        }
      }
      File.WriteAllText(@"C:\Users\alsho\AppData\Local\Temp\claude\c--Proj-DSP-SkyRoof\ab069f8d-3f83-4eb6-bde8-512165668d0b\scratchpad\umka_presence.txt", log.ToString());
    }

    [ManualFact("compare-runtime")]
    public void CompareRuntime()
    {
      string dir = @"C:\Users\alsho\AppData\Local\Temp\claude\c--Proj-DSP-SkyRoof\ab069f8d-3f83-4eb6-bde8-512165668d0b\scratchpad\";
      var files = new (string tag, string path)[]
      {
        ("RUNTIME_1", @"C:\Users\alsho\AppData\Roaming\Afreet\Products\SkyRoof\SstvImages\20260720_120823_UmKA-1_Robot36_1.png"),
        ("streaming", dir + "umka_streaming.png"),
        ("batchFull", dir + "umka_batch.png"),
      };
      foreach (var (tag, path) in files)
      {
        if (!File.Exists(path)) { Log($"{tag}: absent {path}"); continue; }
        var img = RgbImage.FromBitmap(new System.Drawing.Bitmap(path));
        MarchFullWidth(tag, img);
      }
      File.WriteAllText(dir + "umka_compare.txt", log.ToString());
    }

    // full-width green-sync march: per row, the green-dominant column anywhere; fit slope over all rows
    private void MarchFullWidth(string tag, RgbImage img)
    {
      int w = img.Width, h = img.Height;
      var col = new int[h]; for (int i = 0; i < h; i++) col[i] = -1;
      int n = 0; double sx = 0, sy = 0, sxx = 0, sxy = 0;
      for (int row = 0; row < h; row++)
      {
        int best = -1, bestDom = 25;
        for (int x = 0; x < w; x++)
        {
          var (r, g, b) = img.Get(x, row);
          int dom = g - Math.Max(r, b);
          if (dom > bestDom) { bestDom = dom; best = x; }
        }
        if (best < 0) continue;
        col[row] = best;
        sx += row; sy += best; sxx += (double)row * row; sxy += (double)row * best; n++;
      }
      if (n < 10) { Log($"[{tag}] too few green cols ({n})"); return; }
      double slope = (n * sxy - sx * sy) / (n * sxx - sx * sx);
      Log($"[{tag}] full-width green march: slope={slope:0.000} px/row => {slope * h:0} px over {h} rows " +
          $"(cols r10={col[10]} r60={col[60]} r120={col[120]} r180={col[180]} r230={col[230]}, {n} rows)");
    }

    [ManualFact("convergence")]
    public void Convergence()
    {
      string wav = Path.Combine(RecordingsDir, "2026-07-20_12_04_08_UmKA-1.iq.wav");
      var (iq, sr) = WavIqReader.Read(wav);
      double fs = sr;
      var o = new SstvDecodeOptions { SampleRate = fs };
      double[] disc = SstvDecoder.Discriminator(iq, o);
      double[] sync = SstvDecoder.SyncAudio(disc, fs, o);
      var det = new SstvPulseDetector(fs, 9.0) { Threshold = 0.18 };
      var all = det.Detect(sync);

      // the VIS header end = anchor (line-0 onset), from the detector
      var hits = SstvVisDetector.DetectAll(sync, fs);
      int anchor = hits[0].HeaderEndSample;
      double nominal = 150.0 / 1000.0 * fs;

      // strong pulses from the anchor onward (what the VIS train would see, in arrival order)
      var pulseTimes = new System.Collections.Generic.List<int>();
      foreach (var p in all) { double ts = p.Time / fs; if (ts >= 213.6 && ts <= 251.5) pulseTimes.Add(p.Time); }

      foreach (var (ppm, phaseMs, label) in new[] {
        (10000.0, double.PositiveInfinity, "current (per1%, phase∞)"),
        (1e6, double.PositiveInfinity, "wide per, phase∞"),
        (10000.0, 3.0, "per1%, phase3ms"),
        (1e6, 3.0, "wide per, phase3ms") })
      {
        // mimic the VIS-train regressor + its association gate, fed pulses in time order
        var regr = new SstvSyncRegressor(anchor, nominal, fs, periodPpm: ppm, phaseMs: phaseMs);
        int used = 0, lastPn = int.MinValue;
        var checkpoints = new System.Collections.Generic.List<string>();
        int nextCheck = 0;
        int[] checks = { 3, 6, 10, 20, 40, 80, 160 };
        foreach (int t in pulseTimes)
        {
          int pn = regr.GetPulseNo(t);
          if (pn == lastPn) continue;
          double expected = regr.GetPulseTime(pn);
          if (Math.Abs(t - expected) > regr.GetMaxError(pn)) continue;   // association gate
          regr.ProcessPulse(t); lastPn = pn; used++;
          if (nextCheck < checks.Length && used == checks[nextCheck])
          {
            double slantPx = (regr.Period - 7190.96) * 240 / (88.0 / 1000.0 * fs) * 320;
            checkpoints.Add($"n={used}:{regr.Period:0.0}({slantPx:+0;-0}px)");
            nextCheck++;
          }
        }
        double finalSlant = (regr.Period - 7190.96) * 240 / (88.0 / 1000.0 * fs) * 320;
        Log($"{label,-26}: {string.Join("  ", checkpoints)}  FINAL={regr.Period:0.0} slant={finalSlant:+0;-0}px");
      }
      File.WriteAllText(@"C:\Users\alsho\AppData\Local\Temp\claude\c--Proj-DSP-SkyRoof\ab069f8d-3f83-4eb6-bde8-512165668d0b\scratchpad\umka_conv.txt", log.ToString());
    }

    [ManualFact("validate-fix")]
    public void ValidateFix()
    {
      string wav = Path.Combine(RecordingsDir, "2026-07-20_12_04_08_UmKA-1.iq.wav");
      var (iq, sr) = WavIqReader.Read(wav);
      double fs = sr;
      var o = new SstvDecodeOptions { SampleRate = fs };
      double[] disc = SstvDecoder.Discriminator(iq, o with { ChannelBwHz = o.VideoChannelBwHz });
      double[] discDet = SstvDecoder.Discriminator(iq, o);
      double[] sync = SstvDecoder.SyncAudio(discDet, fs, o);
      double[] brightness = SstvDecoder.Brightness(disc, fs, o);

      var hits = SstvVisDetector.DetectAll(sync, fs);
      var ex = SstvDecoder.ExtractTrains(sync, fs, hits);
      var train = ex.BestTrain(SstvMode.Robot36)!;
      var spec = SstvModes.Get(SstvMode.Robot36);
      double period = train.Regr.Period;
      double onset0 = train.Regr.GetPulseTime(0);
      double corr = train.Regr.CorrFactor;

      // detected strong sync pulses, sorted
      var det = new SstvPulseDetector(fs, 9.0) { Threshold = 0.18 };
      var pulses = det.Detect(sync);

      // per line, anchor to the nearest detected pulse within +-40% of a period; else the linear prediction
      var bw = new BrightnessWindow(brightness, 0, brightness.Length);
      int w = spec.Width, h = spec.Height;
      var y = new double[w * h]; var cr = new double[w * h]; var cb = new double[w * h];
      var hasCr = new bool[h]; var hasCb = new bool[h];
      double gate = 0.4 * period;
      for (int line = 0; line < spec.LineCount && line < h; line++)
      {
        double predicted = onset0 + line * period;
        double onset = predicted; double bestD = gate;
        foreach (var p in pulses)
        {
          double d = Math.Abs(p.Time - predicted);
          if (d < bestD) { bestD = d; onset = p.Time; }
        }
        SstvDecoder.RenderRobotLine(bw, spec, o, onset, corr, line, y, cr, cb, hasCr, hasCb);
      }
      SstvDecoder.FillMissingChroma(cr, hasCr, w, h);
      SstvDecoder.FillMissingChroma(cb, hasCb, w, h);
      var img = new RgbImage(w, h);
      for (int row = 0; row < h; row++)
        for (int x = 0; x < w; x++)
        {
          int i = row * w + x;
          var (r, g, b) = YCrCb.ToRgb(y[i], cr[i], cb[i]);
          img.Set(x, row, (byte)Math.Round(r), (byte)Math.Round(g), (byte)Math.Round(b));
        }
      img.SavePng(@"C:\Users\alsho\AppData\Local\Temp\claude\c--Proj-DSP-SkyRoof\ab069f8d-3f83-4eb6-bde8-512165668d0b\scratchpad\umka_pertrack.png");
      Log("saved umka_pertrack.png (each line anchored to its actual detected sync pulse)");
      File.WriteAllText(@"C:\Users\alsho\AppData\Local\Temp\claude\c--Proj-DSP-SkyRoof\ab069f8d-3f83-4eb6-bde8-512165668d0b\scratchpad\umka_validate.txt", log.ToString());
    }

    [ManualFact("clean-decode")]
    public void CleanDecode()
    {
      string wav = Path.Combine(RecordingsDir, "2026-07-20_12_04_08_UmKA-1.iq.wav");
      var (iq, sr) = WavIqReader.Read(wav);
      double fs = sr;
      var o = new SstvDecodeOptions { SampleRate = fs };
      double[] disc = SstvDecoder.Discriminator(iq, o);
      double[] sync = SstvDecoder.SyncAudio(disc, fs, o);
      var hits = SstvVisDetector.DetectAll(sync, fs);
      var ex = SstvDecoder.ExtractTrains(sync, fs, hits);
      var train = ex.BestTrain(SstvMode.Robot36);
      Log($"BestTrain: {(train == null ? "NULL" : train.GetType().Name)} " +
          $"period={(train == null ? 0 : train.Regr.Period):0.00} corr={(train == null ? 0 : train.Regr.CorrFactor):0.0000}");

      if (train != null)
      {
        // replicate LineOnsets(Track=true) to see the ACTUAL per-line render onsets
        int start = (int)Math.Round(train.Regr.GetPulseTime(0));
        int line0 = train.Regr.GetPulseNo(start);
        double onset0 = train.Regr.GetPulseTime(line0);
        double onset1 = train.Regr.GetPulseTime(line0 + 1);
        double onset239 = train.Regr.GetPulseTime(line0 + 239);
        Log($"render onsets: line0idx={line0} onset[0]={onset0:0} " +
            $"per-line step={onset1 - onset0:0.00}smp ({(onset1 - onset0) / fs * 1000:0.000}ms) " +
            $"onset[239]-onset[0]={onset239 - onset0:0}smp");
      }

      // phase of every strong sync pulse relative to the render grid — flat=synced, ramp=wrong period,
      // steps=timing jumps in the transmission
      if (train != null)
      {
        var det = new SstvPulseDetector(fs, 9.0) { Threshold = 0.18 };
        var pulses = det.Detect(sync);
        double period = train.Regr.Period;
        double onset0 = train.Regr.GetPulseTime(0);
        double pxPerSample = 320.0 / (88.0 / 1000.0 * fs);
        Log("  pulse#  t(s)   phase(px, wrapped to +-line)");
        int k = 0;
        foreach (var p in pulses)
        {
          double ts = p.Time / fs;
          if (ts < 213.6 || ts > 251.5) continue;
          double rel = p.Time - onset0;
          double ph = rel - Math.Round(rel / period) * period;   // wrap to [-period/2, period/2]
          if (k % 8 == 0) Log($"   {k,3}  {ts,6:0.0}   {ph * pxPerSample,7:0.0}");
          k++;
        }
      }

      // whole-file normal decode (Acquire=true, Track=true defaults) — the real path
      var img = SstvDecoder.Decode(iq, SstvMode.Robot36, new SstvDecodeOptions { SampleRate = fs });
      img.SavePng(@"C:\Users\alsho\AppData\Local\Temp\claude\c--Proj-DSP-SkyRoof\ab069f8d-3f83-4eb6-bde8-512165668d0b\scratchpad\umka_clean.png");
      Log("saved umka_clean.png (whole-file Decode, Acquire+Track)");
      File.WriteAllText(@"C:\Users\alsho\AppData\Local\Temp\claude\c--Proj-DSP-SkyRoof\ab069f8d-3f83-4eb6-bde8-512165668d0b\scratchpad\umka_clean.txt", log.ToString());
    }

    [ManualFact("streaming-repro")]
    public void StreamingRepro()
    {
      string wav = Path.Combine(RecordingsDir, "2026-07-20_12_04_08_UmKA-1.iq.wav");
      var (iq, sr) = WavIqReader.Read(wav);
      double fs = sr;

      using var dec = new SstvDecoder(new SstvDecodeOptions { SampleRate = fs });
      RgbImage? finalImg = null; int updates = 0;
      dec.ImageUpdated += e => updates++;
      dec.ImageCompleted += e =>
      {
        Log($"COMPLETED id={e.ImageId} {e.Mode} fromVis={e.FromVis} start={e.StartSeconds:0.00}s rows={e.ValidRows}");
        finalImg = e.Image;
      };

      int block = (int)fs;                       // 1-second pushes, like the live app
      for (int at = 0; at < iq.Length; at += block)
        dec.Process(iq.AsSpan(at, Math.Min(block, iq.Length - at)));
      dec.Flush();
      Log($"progressive updates: {updates}");

      if (finalImg != null)
      {
        finalImg.SavePng(@"C:\Users\alsho\AppData\Local\Temp\claude\c--Proj-DSP-SkyRoof\ab069f8d-3f83-4eb6-bde8-512165668d0b\scratchpad\umka_streaming.png");
        MeasureSlant("streaming ", finalImg);
      }

      // batch full-grid render of the same burst for comparison (all lines at the FINAL converged period)
      var o = new SstvDecodeOptions { SampleRate = fs };
      double[] disc = SstvDecoder.Discriminator(iq, o);
      double[] sync = SstvDecoder.SyncAudio(disc, fs, o);
      var hits2 = SstvVisDetector.DetectAll(sync, fs);
      var ex = SstvDecoder.ExtractTrains(sync, fs, hits2);
      var train = ex.BestTrain(SstvMode.Robot36);
      if (train != null)
      {
        var spec = SstvModes.Get(SstvMode.Robot36);
        int firstSync = (int)Math.Round(train.Regr.GetPulseTime(0));
        int margin = (int)(0.5 * fs);
        int durS = (int)(spec.LineCount * spec.LinePeriodMs / 1000.0 * fs);
        int s = Math.Max(0, firstSync - margin), e = Math.Min(iq.Length, firstSync + durS + margin);
        var bimg = SstvDecoder.Decode(iq[s..e], SstvMode.Robot36,
          new SstvDecodeOptions { SampleRate = fs, Acquire = false, StartSample = firstSync - s });
        bimg.SavePng(@"C:\Users\alsho\AppData\Local\Temp\claude\c--Proj-DSP-SkyRoof\ab069f8d-3f83-4eb6-bde8-512165668d0b\scratchpad\umka_batch.png");
        MeasureSlant("batchFull", bimg);
      }
      File.WriteAllText(@"C:\Users\alsho\AppData\Local\Temp\claude\c--Proj-DSP-SkyRoof\ab069f8d-3f83-4eb6-bde8-512165668d0b\scratchpad\umka_streaming.txt", log.ToString());
    }

    // locate the green sync artifact column per row (green-dominant) and fit its slope = the slant
    private void MeasureSlant(string tag, RgbImage img)
    {
      int w = img.Width, h = img.Height;
      var col = new double[h]; for (int i = 0; i < h; i++) col[i] = -1;
      int nrows = 0; double sx = 0, sy = 0, sxx = 0, sxy = 0;
      for (int row = 0; row < h; row++)
      {
        // strongest green-dominant column in the right half (the sync artifact rides the chroma green)
        int best = -1; int bestG = 40;
        for (int x = w / 2; x < w; x++)
        {
          var (r, g, b) = img.Get(x, row);
          int dom = g - Math.Max(r, b);
          if (dom > bestG) { bestG = dom; best = x; }
        }
        if (best < 0) continue;
        col[row] = best;
        sx += row; sy += best; sxx += (double)row * row; sxy += (double)row * best; nrows++;
      }
      if (nrows >= 5)
      {
        double slope = (nrows * sxy - sx * sy) / (nrows * sxx - sx * sx);
        double c0 = (sy - slope * sx) / nrows;
        Log($"[{tag}] green-sync slope = {slope:0.000} px/row => {slope * h:0} px over {h} rows " +
            $"(fit col row0={c0:0} row{h - 1}={c0 + slope * (h - 1):0}, {nrows} rows detected)");
      }

      // dark-sync sweep: the 1200Hz sync clamps to black; find the darkest column per row
      var dark = new int[h];
      for (int row = 0; row < h; row++)
      {
        int best = -1, bestV = 120;   // must be quite dark
        for (int x = 0; x < w; x++)
        {
          var (r, g, b) = img.Get(x, row);
          int v = r + g + b;
          if (v < bestV) { bestV = v; best = x; }
        }
        dark[row] = best;
      }
      Log($"[{tag}] dark-sync col @ rows 5,20,40,80,120,180,239 = " +
          $"{dark[5]} {dark[20]} {dark[40]} {dark[80]} {dark[120]} {dark[180]} {dark[239]}");
    }

    [ManualFact("period-fit")]
    public void PeriodFit()
    {
      string wav = Path.Combine(RecordingsDir, "2026-07-20_12_04_08_UmKA-1.iq.wav");
      var (iq, sr) = WavIqReader.Read(wav);
      double fs = sr;
      var o = new SstvDecodeOptions { SampleRate = fs };
      double[] disc = SstvDecoder.Discriminator(iq, o);
      double[] sync = SstvDecoder.SyncAudio(disc, fs, o);

      // strong sync pulses in the burst window
      var det = new SstvPulseDetector(fs, 9.0) { Threshold = 0.18 };
      var all = det.Detect(sync);
      var t = new System.Collections.Generic.List<double>();
      foreach (var p in all) { double ts = p.Time / fs; if (ts >= 213.5 && ts <= 251.5) t.Add(p.Time); }
      Log($"strong pulses in burst: {t.Count}");

      // robust iterative LS fit: time = P*pulseNo + C, pulseNo from current P; drop >2ms residual outliers
      double nominal = 150.0 / 1000.0 * fs;   // 7200
      double P = nominal, C = t[0];
      for (int iter = 0; iter < 6; iter++)
      {
        double sx = 0, sy = 0, sxx = 0, sxy = 0; int nn = 0;
        var kept = new System.Collections.Generic.List<(double x, double y)>();
        foreach (double y in t)
        {
          double no = Math.Round((y - C) / P);
          double pred = P * no + C;
          if (Math.Abs(y - pred) > 0.004 * fs && iter > 0) continue;   // >4ms outlier after first pass
          kept.Add((no, y));
        }
        foreach (var (x, y) in kept) { sx += x; sy += y; sxx += x * x; sxy += x * y; nn++; }
        double denom = nn * sxx - sx * sx;
        if (denom == 0) break;
        P = (nn * sxy - sx * sy) / denom;
        C = (sy - P * sx) / nn;
        // residual rms
        double rss = 0; foreach (var (x, y) in kept) { double e = y - (P * x + C); rss += e * e; }
        Log($"  iter{iter}: N={nn} P={P:0.00}smp ({P / fs * 1000:0.000}ms) rmsResid={Math.Sqrt(rss / nn):0.0}smp");
      }
      double errPct = (P - nominal) / nominal * 100;
      double slantSmp = (P - nominal) * 240;   // if reconstruction used nominal instead of P
      Log($"TRUE period = {P:0.00} smp = {P / fs * 1000:0.000} ms  ({errPct:+0.00;-0.00}% vs 150ms nominal)");
      Log($"if decoder used a period off by this much, slant over 240 lines = {slantSmp:0} smp = " +
          $"{slantSmp / (88.0 / 1000.0 * fs) * 320:0} px of a 320px scan");

      // what did the actual VIS-train regressor lock?
      var hits = SstvVisDetector.DetectAll(sync, fs);
      var ex = SstvDecoder.ExtractTrains(sync, fs, hits);
      foreach (var tr in ex.Trains)
      {
        double locked = (tr.Regr.GetPulseTime(100) - tr.Regr.GetPulseTime(0)) / 100.0;
        Log($"  train {tr.GetType().Name} lockedPeriod={locked:0.00}smp ({locked / fs * 1000:0.000}ms) " +
            $"pulses={tr.PulseCnt} vs TRUE {P:0.00}  drift@240={(locked - P) * 240:0}smp");
      }

      // exactly what the decoder renders: onset[line] vs the true sync, in samples and pixels
      var train = ex.BestTrain(SstvMode.Robot36);
      if (train != null)
      {
        int start = (int)Math.Round(train.Regr.GetPulseTime(0));
        int line0 = train.Regr.GetPulseNo(start);
        double pxPerSample = 320.0 / (88.0 / 1000.0 * fs);   // Y scan px per sample
        Log("  line  onset     nearestTrueSync   drift(smp)  drift(px)");
        foreach (int line in new[] { 0, 30, 60, 120, 180, 239 })
        {
          double onset = train.Regr.GetPulseTime(line0 + line);
          double trueSync = Math.Round((onset - C) / P) * P + C;
          double d = onset - trueSync;
          Log($"   {line,3}  {onset,9:0}  {trueSync,14:0}   {d,8:0}   {d * pxPerSample,7:0.0}");
        }
      }
      File.WriteAllText(@"C:\Users\alsho\AppData\Local\Temp\claude\c--Proj-DSP-SkyRoof\ab069f8d-3f83-4eb6-bde8-512165668d0b\scratchpad\umka_periodfit.txt", log.ToString());
    }

    [ManualFact("burst-probe")]
    public void BurstProbe()
    {
      // probe the two candidate period-out-of-gate bursts that failed to image
      Probe("2026-07-10_12_11_50_SAKHACUBE-CHOLBON.iq.wav", 118, 140);
      Probe("2026-07-07_23_51_01_SAKHACUBE-CHOLBON.iq.wav", 30, 70);
      File.WriteAllText(@"C:\Users\alsho\AppData\Local\Temp\claude\c--Proj-DSP-SkyRoof\ab069f8d-3f83-4eb6-bde8-512165668d0b\scratchpad\umka_probe.txt", log.ToString());
    }

    private void Probe(string name, double s0, double s1)
    {
      string wav = Path.Combine(RecordingsDir, name);
      if (!File.Exists(wav)) { Log($"absent {name}"); return; }
      var (iq, sr) = WavIqReader.Read(wav);
      double fs = sr;
      var o = new SstvDecodeOptions { SampleRate = fs };
      double[] disc = SstvDecoder.Discriminator(iq, o);
      double[] sync = SstvDecoder.SyncAudio(disc, fs, o);
      Log($"=== PROBE {name} region {s0}-{s1}s ===");
      var det = new SstvPulseDetector(fs, 9.0) { Threshold = 0.10 };  // associate tier, to see weak pulses
      var pulses = det.Detect(sync);
      double pmin = double.MaxValue, pmax = 0, psum = 0; int cnt = 0;
      int prev = -1;
      var gaps = new System.Collections.Generic.List<double>();
      foreach (var p in pulses)
      {
        double ts = p.Time / fs;
        if (ts < s0 || ts > s1) continue;
        if (prev >= 0) gaps.Add((p.Time - prev) / fs * 1000);
        prev = p.Time;
        pmin = Math.Min(pmin, p.Power); pmax = Math.Max(pmax, p.Power); psum += p.Power; cnt++;
      }
      Log($"  pulses in region: {cnt}  power min={pmin:0.00} max={pmax:0.00} mean={(cnt > 0 ? psum / cnt : 0):0.00}");
      if (gaps.Count > 0)
      {
        var sorted = new System.Collections.Generic.List<double>(gaps); sorted.Sort();
        Log($"  gap ms: min={sorted[0]:0.0} med={sorted[sorted.Count / 2]:0.0} max={sorted[^1]:0.0}");
        Log($"  gaps: {string.Join(" ", gaps.ConvertAll(g => g.ToString("0")))}");
      }
    }

    [ManualFact("diagnostic")]
    public void Diag()
    {
      string wav = Path.Combine(RecordingsDir, "2026-07-20_12_04_08_UmKA-1.iq.wav");
      if (!File.Exists(wav)) { Log($"absent: {wav}"); return; }

      var (iq, sr) = WavIqReader.Read(wav);
      double fs = sr;
      Log($"read {iq.Length} samples @ {sr} Hz = {iq.Length / fs:0.0}s");

      var o = new SstvDecodeOptions { SampleRate = fs };
      double[] disc = SstvDecoder.Discriminator(iq, o);
      double[] sync = SstvDecoder.SyncAudio(disc, fs, o);

      var hits = SstvVisDetector.DetectAll(sync, fs);
      Log($"VIS hits: {hits.Count}");
      foreach (var h in hits)
        Log($"  VIS found={h.Found} byte=0x{h.VisByte:X2} mode={h.Mode} " +
          $"t0={h.T0Sample / fs:0.00}s hdrEnd={h.HeaderEndSample / fs:0.00}s score={h.Score:0.00} parity={h.ParityOk}");

      var ex = SstvDecoder.ExtractTrains(sync, fs, hits);
      Log($"trains: {ex.Trains.Count}");
      foreach (var t in ex.Trains)
      {
        double t0 = t.Regr.GetPulseTime(0) / fs;
        double tN = t.Regr.LastPulseTime / fs;
        Log($"  {t.GetType().Name} {t.Format} state={t.State} pulses={t.PulseCnt} " +
          $"claimed={ex.ClaimedLines(t)} start={t0:0.00}s last={tN:0.00}s " +
          $"period={t.Regr.Period:0.1} corr={t.Regr.CorrFactor:0.0000} " +
          $"image={ex.IsImageTrain(t)} mean={t.MeanPower:0.000}");
      }

      var best = ex.BestTrain();
      if (best != null)
        Log($"BEST: {best.Format} start={best.Regr.GetPulseTime(0) / fs:0.00}s " +
          $"claimed={ex.ClaimedLines(best)} corr={best.Regr.CorrFactor:0.0000}");

      // ---- raw pulse survey: find bursts and measure their actual line period ----
      foreach (double syncMs in new[] { 9.0, 20.0 })
      {
        var det = new SstvPulseDetector(fs, syncMs) { Threshold = 0.18 };
        var pulses = det.Detect(sync);
        Log($"--- family syncMs={syncMs}: {pulses.Count} pulses (thr 0.18), maxScore={det.MaxScore:0.00} ---");

        // cluster into bursts: gap > 3s starts a new burst
        int i0 = 0;
        for (int i = 1; i <= pulses.Count; i++)
        {
          bool end = i == pulses.Count || (pulses[i].Time - pulses[i - 1].Time) > 3 * fs;
          if (!end) continue;
          int n = i - i0;
          if (n >= 5)
          {
            // median consecutive spacing within the burst
            var gaps = new System.Collections.Generic.List<double>();
            for (int k = i0 + 1; k < i; k++) gaps.Add(pulses[k].Time - pulses[k - 1].Time);
            gaps.Sort();
            double medGap = gaps[gaps.Count / 2];
            double t0 = pulses[i0].Time / fs, t1 = pulses[i - 1].Time / fs;
            // best-fitting mode line period and % error
            string modeFit = "none";
            double bestErr = 999;
            foreach (var spec in SstvModes.All)
            {
              double nomLine = spec.LinePeriodMs / 1000.0 * fs;
              // sync pulses come once per line; medGap should be ~nomLine (or a small multiple if pulses missed)
              for (int mult = 1; mult <= 4; mult++)
              {
                double err = (medGap / mult - nomLine) / nomLine;
                if (Math.Abs(err) < Math.Abs(bestErr)) { bestErr = err; modeFit = $"{spec.Mode}/x{mult}"; }
              }
            }
            Log($"  BURST {t0:0.0}-{t1:0.0}s n={n} medGap={medGap:0}smp ({medGap / fs * 1000:0.0}ms) " +
                $"bestFit={modeFit} err={bestErr * 100:+0.0;-0.0}% {(Math.Abs(bestErr) > 0.03 ? "OUT-OF-GATE" : "in-gate")}");
          }
          i0 = i;
        }
      }

      File.WriteAllText(@"C:\Users\alsho\AppData\Local\Temp\claude\c--Proj-DSP-SkyRoof\ab069f8d-3f83-4eb6-bde8-512165668d0b\scratchpad\umka_diag.txt", log.ToString());

      // decode the best train's image and save it, so we can SEE whether it is synchronized
      if (best != null)
      {
        var spec = SstvModes.Get(best.Format);
        int firstSync = (int)Math.Round(best.Regr.GetPulseTime(0));
        int margin = (int)(0.5 * fs);
        int dur = (int)(spec.LineCount * spec.LinePeriodMs / 1000.0 * fs);
        int startS = Math.Max(0, firstSync - margin);
        int endS = Math.Min(iq.Length, firstSync + dur + margin);
        var img = SstvDecoder.Decode(iq[startS..endS], best.Format,
          new SstvDecodeOptions { SampleRate = fs, Acquire = false, StartSample = firstSync - startS });
        img.SavePng(@"C:\Users\alsho\AppData\Local\Temp\claude\c--Proj-DSP-SkyRoof\ab069f8d-3f83-4eb6-bde8-512165668d0b\scratchpad\umka_0720.png");
        Log($"saved image {spec.Width}x{spec.Height}");
      }
    }
  }
}
