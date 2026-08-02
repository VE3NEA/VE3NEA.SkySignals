using System;
using System.Collections.Generic;
using System.IO;
using VE3NEA.SkyTlm.IO;
using Xunit;
using Xunit.Abstractions;

namespace VE3NEA.SkySSTV.Tests
{
  // temporary diagnostic harness for the 2026-08-01 SAKHACUBE-CHOLBON out-of-sync image (Robot36_3)
  public class SstvSakhaDiagTests
  {
    private const string Wav =
      @"C:\Users\alsho\AppData\Roaming\Afreet\Products\SkyRoof\Recordings\Auto\2026-08-01_00_03_24_SAKHACUBE-CHOLBON.iq.wav";
    private const string Out =
      @"C:\Users\alsho\AppData\Local\Temp\claude\c--Proj-Try-FskDemod\8e013171-4b78-44fb-929b-1a7fe89ad9e5\scratchpad\";

    private readonly ITestOutputHelper output;
    private readonly System.Text.StringBuilder log = new();
    public SstvSakhaDiagTests(ITestOutputHelper o) => output = o;

    private void Log(string s) { output.WriteLine(s); log.AppendLine(s); }

    [ManualFact("sakha-replay")]
    public void Replay()
    {
      var (iq, sr) = WavIqReader.Read(Wav);
      double fs = sr;
      Log($"read {iq.Length} samples @ {sr} Hz = {iq.Length / fs:0.0}s");

      using var dec = new SstvDecoder(new SstvDecodeOptions { SampleRate = fs });
      dec.ImageCompleted += e =>
      {
        string p = Out + $"sakha_{e.ImageId}.png";
        e.Image.SavePng(p);
        Log($"COMPLETED id={e.ImageId} {e.Mode} fromVis={e.FromVis} start={e.StartSeconds:0.00}s rows={e.ValidRows}");
      };

      int block = (int)fs;
      for (int at = 0; at < iq.Length; at += block)
        dec.Process(iq.AsSpan(at, Math.Min(block, iq.Length - at)));
      dec.Flush();

      File.WriteAllText(Out + "sakha_replay.txt", log.ToString());
    }

    [ManualFact("sakha-trains")]
    public void Trains()
    {
      var (iq, sr) = WavIqReader.Read(Wav);
      double fs = sr;
      var o = new SstvDecodeOptions { SampleRate = fs };
      double[] disc = SstvDecoder.Discriminator(iq, o);
      double[] sync = SstvDecoder.SyncAudio(disc, fs, o);

      var hits = SstvVisDetector.DetectAll(sync, fs);
      Log($"VIS hits: {hits.Count}");
      foreach (var h in hits)
        Log($"  VIS byte=0x{h.VisByte:X2} mode={h.Mode} t0={h.T0Sample / fs:0.00}s " +
            $"hdrEnd={h.HeaderEndSample / fs:0.00}s score={h.Score:0.00} parity={h.ParityOk}");

      var ex = SstvDecoder.ExtractTrains(sync, fs, hits);
      Log($"trains: {ex.Trains.Count}");
      foreach (var t in ex.Trains)
        Log($"  {t.GetType().Name} {t.Format} state={t.State} pulses={t.PulseCnt} claimed={ex.ClaimedLines(t)} " +
            $"start={t.Regr.GetPulseTime(0) / fs:0.00}s last={t.Regr.LastPulseTime / fs:0.00}s " +
            $"period={t.Regr.Period:0.1} corr={t.Regr.CorrFactor:0.0000} image={ex.IsImageTrain(t)} " +
            $"mean={t.MeanPower:0.000}");

      // raw pulse survey: bursts and their true line period
      var det = new SstvPulseDetector(fs, 9.0) { Threshold = 0.18 };
      var pulses = det.Detect(sync);
      Log($"--- 9ms family: {pulses.Count} pulses ---");
      int i0 = 0;
      for (int i = 1; i <= pulses.Count; i++)
      {
        bool end = i == pulses.Count || (pulses[i].Time - pulses[i - 1].Time) > 3 * fs;
        if (!end) continue;
        int n = i - i0;
        if (n >= 8)
        {
          var gaps = new List<double>();
          for (int k = i0 + 1; k < i; k++) gaps.Add(pulses[k].Time - pulses[k - 1].Time);
          gaps.Sort();
          double med = gaps[gaps.Count / 2];
          Log($"  BURST {pulses[i0].Time / fs:0.0}-{pulses[i - 1].Time / fs:0.0}s n={n} " +
              $"medGap={med / fs * 1000:0.00}ms (nominal Robot36 150.00)");
        }
        i0 = i;
      }

      File.WriteAllText(Out + "sakha_trains.txt", log.ToString());
    }

    [ManualFact("sakha-fit")]
    public void Fit()
    {
      var (iq, sr) = WavIqReader.Read(Wav);
      double fs = sr;
      var o = new SstvDecodeOptions { SampleRate = fs };
      double[] disc = SstvDecoder.Discriminator(iq, o);
      double[] sync = SstvDecoder.SyncAudio(disc, fs, o);
      var hits = SstvVisDetector.DetectAll(sync, fs);
      var ex = SstvDecoder.ExtractTrains(sync, fs, hits);

      // the true line clock of the 279..316 s transmission: robust LS through the strong pulses
      var det = new SstvPulseDetector(fs, 9.0) { Threshold = SstvPulseDetector.ScoreThreshold };
      var strong = det.Detect(sync);
      var t = new List<double>();
      foreach (var p in strong) { double ts = p.Time / fs; if (ts >= 278.9 && ts <= 316.5) t.Add(p.Time); }
      double P = 150.0 / 1000.0 * fs, C = t[0];
      for (int iter = 0; iter < 8; iter++)
      {
        double sx = 0, sy = 0, sxx = 0, sxy = 0; int n = 0;
        foreach (double y in t)
        {
          double no = Math.Round((y - C) / P);
          if (iter > 0 && Math.Abs(y - (P * no + C)) > 0.004 * fs) continue;
          sx += no; sy += y; sxx += no * no; sxy += no * y; n++;
        }
        P = (n * sxy - sx * sy) / (n * sxx - sx * sx);
        C = (sy - P * sx) / n;
        if (iter == 7) Log($"TRUE clock: N={n} period={P:0.00}smp ({P / fs * 1000:0.000}ms) t0={C / fs:0.000}s");
      }

      foreach (var tr in ex.Trains)
      {
        if (tr.Regr.GetPulseTime(0) / fs is < 270 or > 290) continue;
        Log($"=== {tr.GetType().Name} {tr.Format} pulses={tr.PulseCnt} period={tr.Regr.Period:0.00} " +
            $"corr={tr.Regr.CorrFactor:0.00000} first={tr.Regr.FirstPulseTime / fs:0.000}s ===");
        Log("  i  t(s)     lineNo  trueLineNo  resid(ms)  onset-regr(smp)");
        int i = 0;
        foreach (var p in tr.Pulses)
        {
          int lineNo = tr.GetLineNo(p.Time);
          double trueNo = (p.Time - C) / P;
          double resid = (p.Time - tr.GetLineOnset(lineNo)) / fs * 1000;
          double off = tr.GetLineOnset(lineNo) - tr.Regr.GetPulseTime(lineNo);
          Log($"  {i,3} {p.Time / fs,8:0.000} {lineNo,7} {trueNo,11:0.00} {resid,10:0.00} {off,10:0} pw={p.Power:0.00}");
          i++;
        }
      }
      File.WriteAllText(Out + "sakha_fit.txt", log.ToString());
    }

    // the A/B casualty: 2026-07-30_10_42_47 @ ~77 s, where the VIS train stops promoting under the
    // tight period prior and a plain train takes the transmission 7.7 s later
    [ManualFact("sakha-0730")]
    public void Probe0730()
    {
      string wav = @"C:\Users\alsho\AppData\Roaming\Afreet\Products\SkyRoof\Recordings\SSTV\2026-07-30_10_42_47_SAKHACUBE-CHOLBON.iq.wav";
      var (iq, sr) = WavIqReader.Read(wav);
      double fs = sr;
      var o = new SstvDecodeOptions { SampleRate = fs };
      double[] sync = SstvDecoder.SyncAudio(SstvDecoder.Discriminator(iq, o), fs, o);

      foreach (var h in SstvVisDetector.DetectAll(sync, fs))
        Log($"VIS byte=0x{h.VisByte:X2} mode={h.Mode} t0={h.T0Sample / fs:0.00}s " +
            $"hdrEnd={h.HeaderEndSample / fs:0.000}s score={h.Score:0.00} parity={h.ParityOk}");

      var det = new SstvPulseDetector(fs, 9.0) { Threshold = SstvPulseDetector.ScoreThreshold };
      var strong = det.Detect(sync);
      Log("strong 9ms pulses 74-120 s (t, gap ms, power):");
      int prev = -1;
      foreach (var p in strong)
      {
        double ts = p.Time / fs;
        if (ts < 74 || ts > 120) continue;
        Log($"  {ts,8:0.000} {(prev < 0 ? 0 : (p.Time - prev) / fs * 1000),8:0.0} {p.Power:0.00}");
        prev = p.Time;
      }
      File.WriteAllText(Out + "sakha_0730.txt", log.ToString());
    }
  }
}
