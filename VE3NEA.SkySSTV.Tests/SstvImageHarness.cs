using System;
using System.IO;
using FluentAssertions;
using MathNet.Numerics;
using VE3NEA.SkyTlm.IO;
using Xunit;
using Xunit.Abstractions;

namespace VE3NEA.SkySSTV.Tests
{
  /// <summary>
  /// End-to-end decode-to-PNG harness (plan §7 P4.5): run the full engine — <see cref="SstvDecoder.DetectMode"/>
  /// → locate the burst → <see cref="SstvDecoder.Decode"/> → <see cref="RgbImage.SavePng"/> — on both synthetic
  /// signals (asserted round trip) and the real <c>.iq.wav</c> captures (best-effort; writes images for visual
  /// inspection, no ground-truth assertion). Filter fine-tuning is P6; this just proves the image-out path and
  /// exercises real-signal decoding before the SkyRoof UI exists.
  /// </summary>
  public class SstvImageHarness
  {
    private const double Fs = 48000.0;
    private static readonly string RecordingsDir =
      @"C:\Users\alsho\AppData\Roaming\Afreet\Products\SkyRoof\Recordings\SSTV";
    private static readonly string OutDir = Path.Combine(RecordingsDir, "decoded");

    private readonly ITestOutputHelper output;
    public SstvImageHarness(ITestOutputHelper o) => output = o;


    // ----------------------------------------------------------------------------------------------------
    //                                         synthetic -> PNG
    // ----------------------------------------------------------------------------------------------------


    [Theory]
    [InlineData(SstvMode.Robot36)]
    [InlineData(SstvMode.Robot72)]
    [InlineData(SstvMode.Pd120)]
    public void Synthetic_DecodesToPng(SstvMode mode)
    {
      var spec = SstvModes.Get(mode);
      var src = ColorBars(spec.Width, spec.Height);
      var iq = SstvEncoder.Encode(src, mode, new SstvEncoderOptions { IncludeVis = true });

      var decoded = DecodeToImage(iq, Fs);
      decoded.Should().NotBeNull("the engine must detect the mode and produce an image");
      var (img, res) = decoded!.Value;
      res.Mode.Should().Be(mode);

      // a real fidelity gate (retro item L): a misaligned decode (e.g. locking a VIS bit instead of
      // line 0) collapses colorbars to single digits of PSNR, while an aligned decode scores well above.
      double psnr = Psnr(src, img);
      output.WriteLine($"{mode}: detected={res.Mode} fromVis={res.FromVis} PSNR={psnr:0.0} dB");
      psnr.Should().BeGreaterThan(15.0, "the decoded image must actually be aligned with the source");

      string path = Path.Combine(OutDir, $"synthetic_{mode}.png");
      img.SavePng(path);
      src.SavePng(Path.Combine(OutDir, $"synthetic_{mode}_source.png"));
      File.Exists(path).Should().BeTrue();
    }


    // ----------------------------------------------------------------------------------------------------
    //                                       real captures -> PNG
    // ----------------------------------------------------------------------------------------------------


    [ManualFact("Result 2026-07-01 (retro J validated): raw burst 0.243 / clutter 0.163; bandpassed burst " +
      "0.420 (= synthetic level) / clutter 0.406 — the Stage-2 bandpass lifts real syncs to synthetic " +
      "scores, but band-limited noise coherence rises too, so single-pulse thresholds remain " +
      "non-separable and the §4.1 train integration stays required.")]
    public void Real_SyncScoreProbe()
    {
      // retro item J validation: the P4.5 probe measured the real sync matched-filter score at ~0.24 with a
      // 0.18 clutter peak (not threshold-separable) — WITHOUT the Stage-2 band-limit. Re-measure raw vs
      // bandpassed on the same capture (burst ≈ 196.9 s, clutter peak was at 32.9 s).
      string wav = Path.Combine(RecordingsDir, "2026-06-30_22_36_37_UTMN2_Robot36.iq.wav");
      if (!File.Exists(wav)) { output.WriteLine("capture absent; probe skipped"); return; }

      var (iq, sr) = WavIqReader.Read(wav);
      var o = new SstvDecodeOptions { SampleRate = sr };
      double[] disc = SstvDecoder.Discriminator(iq, o);
      double[] band = SstvDecoder.SyncAudio(disc, sr, o);
      var spec = SstvModes.Get(SstvMode.Robot36);

      Report("raw ", disc);
      Report("band", band);

      void Report(string name, double[] audio)
      {
        double burst = RegionMax(audio, 185, 215);
        double clutter = RegionMax(audio, 20, 45);
        double gMax = RegionMax(audio, 0, audio.Length / sr);
        output.WriteLine($"{name}: burst max={burst:0.000}  clutter max={clutter:0.000}  global max={gMax:0.000}");
      }

      double RegionMax(double[] audio, double t0Sec, double t1Sec)
      {
        int a = Math.Max(0, (int)(t0Sec * sr));
        int b = Math.Min(audio.Length, (int)(t1Sec * sr));
        if (b <= a) return 0;
        var detector = new SstvPulseDetector(sr, spec.SyncMs);
        detector.Detect(audio[a..b]);
        return detector.MaxScore;
      }
    }

    [ManualFact("Result 2026-07-02 late (retro D+E+O in): 18 images from 8 of 9 captures — every " +
      "promoted ≥¼-lines train emits (the weak low-fill decodes ARE real transmissions, e.g. 12_37_50 " +
      "@157 s, user-confirmed on the FskDemod spectrogram), plus a NEW real image: the UmKA-1 ~133 s " +
      "SpacePi/Earth burst the VIS-hijack bug had swallowed (its old '@318 s second burst' was an " +
      "artifact of that hijack). Single discriminator pass per capture. Still undetected: the 04-18 " +
      "UmKA-1 transmission (the longer-coherent-integration case).")]
    public void Real_DecodesToPng()
    {
      if (!Directory.Exists(RecordingsDir))
      {
        output.WriteLine($"recordings folder absent ({RecordingsDir}); skipping real-capture harness");
        return;
      }

      foreach (string wav in Directory.GetFiles(RecordingsDir, "*.iq.wav"))
      {
        try
        {
          var (iq, sr) = WavIqReader.Read(wav);
          string stem = Path.GetFileNameWithoutExtension(wav);   // strips .wav; keeps .iq
          int count = 0;
          foreach (var (img, mode, firstSync, fromVis, score) in DecodeAllImages(iq, sr))
          {
            string path = Path.Combine(OutDir, $"{stem}_{firstSync / sr:0}s_{mode}.png");
            img.SavePng(path);
            output.WriteLine($"{stem}: {mode} fromVis={fromVis} burst@{firstSync / sr:0.0}s " +
              $"score={score:0.00} -> {Path.GetFileName(path)}");
            count++;
          }
          if (count == 0) output.WriteLine($"{stem}: no SSTV images ({iq.Length / sr}s @ {sr}Hz)");
        }
        catch (Exception ex) { output.WriteLine($"{Path.GetFileName(wav)}: {ex.GetType().Name} {ex.Message}"); }
      }
    }

    /// <summary>Decode one image per promoted pulse train (a pass carries several transmissions — plan
    /// §1.10/§4.1; e.g. the 22:36 UTMN2 capture holds bursts at ~30 s and ~183 s). The image-emission
    /// gate is the extractor's <see cref="SstvPulseTrainExtractor.IsImageTrain"/> (retro item D).</summary>
    private List<(RgbImage img, SstvMode mode, double firstSync, bool fromVis, double score)>
      DecodeAllImages(Complex32[] iq, double sr)
    {
      var o = new SstvDecodeOptions { SampleRate = sr };
      double[] disc = SstvDecoder.Discriminator(iq, o);      // ONE discriminator pass per capture (retro O)
      double[] sync = SstvDecoder.SyncAudio(disc, sr, o);
      var hits = SstvVisDetector.DetectAll(sync, sr);
      var extractor = SstvDecoder.ExtractTrains(sync, sr, hits);

      var images = new List<(RgbImage, SstvMode, double, bool, double)>();
      foreach (var train in extractor.Trains)
      {
        if (!extractor.IsImageTrain(train)) continue;
        var spec = SstvModes.Get(train.Format);

        int firstSync = (int)Math.Round(train.Regr.GetPulseTime(0));
        int margin = (int)(0.5 * sr);
        int dur = (int)(spec.LineCount * spec.LinePeriodMs / 1000.0 * sr);
        int start = Math.Max(0, firstSync - margin);
        int end = Math.Min(disc.Length, firstSync + dur + margin);
        var img = SstvDecoder.Decode(disc[start..end], train.Format,
          new SstvDecodeOptions { SampleRate = sr, Acquire = false, StartSample = firstSync - start });
        images.Add((img, train.Format, firstSync, train is SstvVisPulseTrain, train.MeanPower));
      }
      return images;
    }


    [ManualFact("Result 2026-07-02 (the retro-D measurement, conclusion REVISED same day): a fill-ratio " +
      "gate (pulses/claimed, low ≤ 0.34 vs high ≥ 0.46) first looked like a noise/real separator, but the " +
      "low-fill trains are REAL weak transmissions (user-confirmed on the FskDemod spectrogram: 12_37_50 " +
      "Monitor-3 has a genuine burst at ~157 s = our @158.2 train) — so no train that promotes in this " +
      "corpus is noise, the fill ratio is only a quality metric, and no rejection gate was added. The " +
      "probe's lasting catch is the VIS triplet-adoption hijack: the UmKA VIS train held 100 pulses at " +
      "pulseNo -1125..-777 (a pre-anchor triplet passed the ±18 ms extrapolation gate) — fixed with the " +
      "anchor-forward span gate, which uncovered a real hidden burst at ~133 s (80 pulses, mean 0.367).")]
    public void Real_TrainStatsProbe()
    {
      // retro D: dump every promoted train's evidence statistics so the noise-train / real-train margin is
      // measurable, then pick the gate from the data (fixed vs relative threshold, plan §9 D)
      foreach (string wav in Directory.GetFiles(RecordingsDir, "*.iq.wav"))
      {
        var (iq, sr) = WavIqReader.Read(wav);
        var o = new SstvDecodeOptions { SampleRate = sr };
        double[] sync = SstvDecoder.SyncAudio(SstvDecoder.Discriminator(iq, o), sr, o);
        var hits = SstvVisDetector.DetectAll(sync, sr);
        var extractor = SstvDecoder.ExtractTrains(sync, sr, hits);

        output.WriteLine($"--- {Path.GetFileNameWithoutExtension(wav)} ({iq.Length / sr:0}s)");
        foreach (var train in extractor.Trains)
        {
          if (train.State != SstvTrainState.Active && train.State != SstvTrainState.Retired) continue;
          var spec = SstvModes.Get(train.Format);
          int claimed = 0;
          foreach (var line in extractor.Lines) if (line.Train == train) claimed++;

          var powers = new List<float>();
          foreach (var p in train.Pulses) powers.Add(p.Power);
          powers.Sort();
          float median = powers[powers.Count / 2];

          // pulse-number span density (extractor-independent) + the claimed lines' PulseNo range
          int spanLo = train.Regr.GetPulseNo(train.Pulses[0].Time);
          int spanHi = train.Regr.GetPulseNo(train.Pulses[^1].Time);
          double density = (double)train.PulseCnt / (spanHi - spanLo + 1);
          int claimLo = int.MaxValue, claimHi = int.MinValue;
          foreach (var line in extractor.Lines)
            if (line.Train == train)
            { claimLo = Math.Min(claimLo, line.PulseNo); claimHi = Math.Max(claimHi, line.PulseNo); }

          output.WriteLine($"  {train.Format} {train.State}{(train is SstvVisPulseTrain ? " VIS" : "")} " +
            $"@{train.Regr.GetPulseTime(0) / sr:0.0}s pulses={train.PulseCnt}/{spec.LineCount} " +
            $"span={spanLo}..{spanHi} density={density:0.00} claimed={claimed} " +
            $"claimNo={(claimed > 0 ? $"{claimLo}..{claimHi}" : "-")} " +
            $"mean={train.MeanPower:0.000} median={median:0.000} " +
            $"min={powers[0]:0.000} max={powers[^1]:0.000} corr={train.Regr.CorrFactor:0.00000}");
        }
      }
    }


    [ManualFact("Result 2026-07-02: the 04-18 UmKA-1 transmission is LOW-deviation FM (spectrogram: weak " +
      "carrier + first-order sideband pair tracing the 1.2-2.3 kHz subcarrier; devEst 1.3-2.1 kHz, " +
      "noise-inflated). Channel ±4000 is the matched sweet spot: clicks 2.4→1.2 %, maxScore 0.221→0.286, " +
      "on-grid sync gaps 3→11 (±6000 vs ±4000). Per-pulse threshold sweep at ±4000: thr 0.10 yields 156 " +
      "pulses / 24 on-grid and PROMOTES an 11-pulse Robot36 train — the decode locks the line rate " +
      "(vertical, unslanted stripes) but the video is unusable: a lock, not an image. Confirms the " +
      "sensitivity-floor diagnosis; needs longer coherent integration + per-burst adaptive channel BW " +
      "(threshold alone also mis-locks Robot72 at thr 0.12).")]
    public void Real_UmKa0418ChannelSweep()
    {
      // sweep the Stage-1 channel BW over the known 0–24 s burst; per BW report: the discriminator click
      // rate (|disc| near Nyquist-scale = FM-threshold saturation), the sync matched-filter MaxScore, the
      // detected pulse count, how many inter-pulse gaps sit on the Robot36 150 ms grid, and the apparent
      // deviation (RMS·√2 of the Stage-2 audio — meaningful only once the clicking stops)
      string wav = Path.Combine(RecordingsDir, "2026-04-18_12_36_09_UmKA-1.iq.wav");
      if (!File.Exists(wav)) { output.WriteLine("capture absent; probe skipped"); return; }

      var (iq, sr) = WavIqReader.Read(wav);
      var burst = iq[..(int)(24 * sr)];
      var spec = SstvModes.Get(SstvMode.Robot36);

      foreach (double chanBw in new[] { 6000.0, 4000.0, 3000.0, 2500.0, 2000.0 })
      {
        var o = new SstvDecodeOptions { SampleRate = sr, ChannelBwHz = chanBw };
        double[] disc = SstvDecoder.Discriminator(burst, o);

        int clicks = 0;
        for (int i = 0; i < disc.Length; i++) if (Math.Abs(disc[i]) > 15000) clicks++;

        double[] sync = SstvDecoder.SyncAudio(disc, sr, o);
        double sum = 0;
        for (int i = 0; i < sync.Length; i++) sum += sync[i] * sync[i];
        double dev = Math.Sqrt(sum / sync.Length) * Math.Sqrt(2.0);

        var detector = new SstvPulseDetector(sr, spec.SyncMs);
        var pulses = detector.Detect(sync);
        int onGrid = 0;
        double period = spec.LinePeriodMs / 1000.0 * sr;
        for (int i = 1; i < pulses.Count; i++)
        {
          double gap = pulses[i].Time - (double)pulses[i - 1].Time;
          double frac = gap / period;
          if (Math.Abs(frac - Math.Round(frac)) * period < 0.005 * sr && frac < 20) onGrid++;
        }

        output.WriteLine($"chan ±{chanBw:0}: clicks={100.0 * clicks / disc.Length:0.0}% " +
          $"maxScore={detector.MaxScore:0.000} pulses={pulses.Count} onGrid={onGrid} devEst={dev:0} Hz");

        // per-pulse threshold sweep at this bandwidth: pulse yield + grid consistency + extractor lock
        foreach (double thr in new[] { 0.15, 0.12, 0.10 })
        {
          var det = new SstvPulseDetector(sr, spec.SyncMs) { Threshold = thr };
          var p = det.Detect(sync);
          int grid = 0;
          for (int i = 1; i < p.Count; i++)
          {
            double gap = p[i].Time - (double)p[i - 1].Time;
            double frac = gap / period;
            if (Math.Abs(frac - Math.Round(frac)) * period < 0.005 * sr && frac < 20) grid++;
          }
          var extractor = new SstvPulseTrainExtractor(sr);
          extractor.Process(p, sync.Length);
          extractor.Finish(sync.Length);
          int promoted = 0;
          foreach (var t in extractor.Trains)
            if (t.State == SstvTrainState.Active || t.State == SstvTrainState.Retired) promoted++;
          output.WriteLine($"  thr={thr:0.00}: pulses={p.Count} onGrid={grid} promoted={promoted}" +
            (extractor.BestTrain() is SstvPulseTrain bt
              ? $" best={bt.Format}@{bt.Regr.GetPulseTime(0) / sr:0.0}s pulses={bt.PulseCnt} fill={extractor.FillRatio(bt):0.00}"
              : ""));

          if (extractor.BestTrain() is SstvPulseTrain best && best.Format == SstvMode.Robot36)
          {
            var img = SstvDecoder.Decode(disc, best.Format, new SstvDecodeOptions
            { SampleRate = sr, ChannelBwHz = chanBw, Acquire = false,
              StartSample = (int)Math.Max(0, Math.Round(best.Regr.GetPulseTime(0))) });
            string path = Path.Combine(OutDir, $"umka0418_chan{chanBw:0}_thr{thr * 100:0}.png");
            img.SavePng(path);
            output.WriteLine($"  -> {Path.GetFileName(path)}");
          }
        }
      }
    }


    [ManualFact("Result 2026-07-02 on the real Monitor-3 text card: the (then-)defaults (chan 15000 / " +
      "video 1800) left heavy speckle; narrowing to chan 4000–5000 + video 500–650 yielded an essentially " +
      "clean, fully readable image that BEATS the RXSSTV reference decode. Brackets: chan 3000 clips the " +
      "FM tails (speckle returns), video 350 over-smooths. This drove the ChannelBwHz/BrightnessBwHz " +
      "defaults now in SstvDecodeOptions.")]
    public void Real_FilterSweepProbe()
    {
      // P6(c): sweep the Stage-1 channel BW and the Stage-3 video BW over the matched real reference image
      // (the Monitor-3 text card — RXSSTV's decode of the same transmission, C:\Ham\RX-SSTV-2\History
      // 2026-07-01_11.07.49, is the quality target; readable text makes differences obvious at a glance).
      string wav = Path.Combine(RecordingsDir, "2026-07-01_11_02_25_Monitor-3.iq.wav");
      if (!File.Exists(wav)) { output.WriteLine("capture absent; probe skipped"); return; }

      var (iq, sr) = WavIqReader.Read(wav);
      var res = SstvDecoder.DetectMode(iq, new SstvDecodeOptions { SampleRate = sr });
      res.Found.Should().BeTrue("the burst is known to be present");
      var spec = SstvModes.Get(res.Mode!.Value);

      int margin = (int)(0.5 * sr);
      int dur = (int)(spec.LineCount * spec.LinePeriodMs / 1000.0 * sr);
      int start = Math.Max(0, res.FirstSyncSample - margin);
      int end = Math.Min(iq.Length, res.FirstSyncSample + dur + margin);
      var seg = iq[start..end];

      foreach (double chanBw in new[] { 5000.0, 4000.0, 3000.0 })
        foreach (double videoBw in new[] { 650.0, 500.0 })
        {
          var img = SstvDecoder.Decode(seg, res.Mode.Value, new SstvDecodeOptions
          {
            SampleRate = sr,
            Acquire = false,
            StartSample = res.FirstSyncSample - start,
            ChannelBwHz = chanBw,
            BrightnessBwHz = videoBw
          });
          string path = Path.Combine(OutDir, $"sweep_Monitor3_chan{chanBw:0}_vid{videoBw:0}.png");
          img.SavePng(path);
          output.WriteLine($"chan={chanBw} video={videoBw} -> {Path.GetFileName(path)}");
        }
    }


    [ManualFact("Result 2026-07-02: peak deviation ≈ 3.3 kHz on the strong bursts (Monitor-3 3310, UTMN2 " +
      "3303/3368 Hz); weaker bursts read 3.7–4.1 kHz (noise-inflated). Corroborated by the FskDemod " +
      "spectrogram (occupied width ≈ ±5 kHz, carrier centered). Basis for the chan ±6 kHz default and the " +
      "encoder's 3.3 kHz deviation.")]
    public void Real_DeviationProbe()
    {
      // P6(c): measure the real transmissions' peak FM deviation. The discriminator output inside a burst
      // is dev·a(t) with a(t) a unit sinusoid at the subcarrier frequency, so after the Stage-2 bandpass
      // (which also removes the Doppler DC) the peak deviation is simply RMS·√2 over the burst interior.
      foreach (string wav in Directory.GetFiles(RecordingsDir, "*.iq.wav"))
      {
        var (iq, sr) = WavIqReader.Read(wav);
        var o = new SstvDecodeOptions { SampleRate = sr };
        double[] disc = SstvDecoder.Discriminator(iq, o);    // ONE discriminator pass (retro O)
        var res = SstvDecoder.DetectMode(disc, o);
        string stem = Path.GetFileNameWithoutExtension(wav);
        if (!res.Found || res.Mode is not SstvMode mode) { output.WriteLine($"{stem}: no burst"); continue; }

        var spec = SstvModes.Get(mode);
        int dur = (int)(spec.LineCount * spec.LinePeriodMs / 1000.0 * sr);
        int a = Math.Max(0, res.FirstSyncSample + dur / 10);
        int b = Math.Min(disc.Length, res.FirstSyncSample + dur - dur / 10);
        if (b <= a) { output.WriteLine($"{stem}: burst span empty"); continue; }

        double[] sync = SstvDecoder.SyncAudio(disc[a..b], sr, o);
        double sum = 0;
        int n0 = sync.Length / 10, n1 = sync.Length - sync.Length / 10;
        for (int i = n0; i < n1; i++) sum += sync[i] * sync[i];
        double dev = Math.Sqrt(sum / (n1 - n0)) * Math.Sqrt(2.0);
        output.WriteLine($"{stem}: mode={mode} burst@{res.FirstSyncSample / sr:0.0}s  peak deviation ≈ {dev:0} Hz");
      }
    }


    // ----------------------------------------------------------------------------------------------------
    //                                            engine glue
    // ----------------------------------------------------------------------------------------------------


    /// <summary>Detect the mode, extract the burst around the first detected sync, and decode it at the
    /// detector's line-0 onset. No re-acquisition inside the slice (retro item L): the 0.5 s margin is
    /// shorter than the 0.91 s VIS header, so a second acquisition would lock a VIS bit instead.</summary>
    private static (RgbImage img, SstvModeResult res)? DecodeToImage(Complex32[] iq, double fs)
    {
      var o = new SstvDecodeOptions { SampleRate = fs };
      double[] disc = SstvDecoder.Discriminator(iq, o);      // ONE discriminator pass (retro O)
      var res = SstvDecoder.DetectMode(disc, o);
      if (!res.Found || res.Mode is not SstvMode mode) return null;

      var spec = SstvModes.Get(mode);
      int margin = (int)(0.5 * fs);
      int dur = (int)(spec.LineCount * spec.LinePeriodMs / 1000.0 * fs);
      int start = Math.Max(0, res.FirstSyncSample - margin);
      int end = Math.Min(disc.Length, res.FirstSyncSample + dur + margin);

      var img = SstvDecoder.Decode(disc[start..end], mode,
        new SstvDecodeOptions { SampleRate = fs, Acquire = false, StartSample = res.FirstSyncSample - start });
      return (img, res);
    }

    /// <summary>Peak-SNR (dB) over all pixels and channels.</summary>
    private static double Psnr(RgbImage a, RgbImage b)
    {
      double se = 0; long n = (long)a.Width * a.Height * 3;
      for (int i = 0; i < a.R.Length; i++)
        se += Sq(a.R[i] - b.R[i]) + Sq(a.G[i] - b.G[i]) + Sq(a.B[i] - b.B[i]);
      double mse = se / n;
      return mse <= 1e-9 ? 100.0 : 10.0 * Math.Log10(255.0 * 255.0 / mse);
    }

    private static double Sq(int d) => (double)d * d;

    /// <summary>Saturated color bars + a vertical luma ramp — a pattern whose slant/skew/color errors are
    /// obvious at a glance in the decoded PNG.</summary>
    private static RgbImage ColorBars(int w, int h)
    {
      (byte r, byte g, byte b)[] bars =
      {
        (255,255,255),(255,255,0),(0,255,255),(0,255,0),(255,0,255),(255,0,0),(0,0,255),(0,0,0)
      };
      var img = new RgbImage(w, h);
      for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
          var (r, g, b) = bars[x * bars.Length / w];
          double k = 0.4 + 0.6 * y / (h - 1);                // top-to-bottom brightness ramp
          img.Set(x, y, (byte)(r * k), (byte)(g * k), (byte)(b * k));
        }
      return img;
    }
  }
}
