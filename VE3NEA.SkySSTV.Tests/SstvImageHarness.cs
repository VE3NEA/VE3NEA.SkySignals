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

    [ManualFact("Result 2026-07-04 (locked P6(c) defaults: video chain ±4000 + blanker 0.5, decode from " +
      "the IQ slice): 21 real images + 1 FALSE image from the 9 captures — the 21 baseline images " +
      "regenerate (strong decodes spot-checked visually clean: UTMN2 @27 s, Monitor-3 @285 s); the " +
      "11_09 @118 s Robot72 image is the telemetry-fed comb false positive (user-refuted — see " +
      "Real_TrainAccuracyProbe; a product-visible false image until the P7 guard lands). " +
      "Previous result 2026-07-03 late (soft-comb wired): 21 images from all 9 — 04-18 detects and " +
      "decodes for the first time (comb-seeded train @0.1 s; video still speckle = the below-FM-threshold " +
      "fidelity gap, but timing locks: the post-24 s striping is vertical); also new then: the 12_37_50 " +
      "@5 s candidate (since user-confirmed) and the 04-19 @505 post-dropout partial.")]
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
        var img = SstvDecoder.Decode(iq[start..end], train.Format,
          new SstvDecodeOptions { SampleRate = sr, Acquire = false, StartSample = firstSync - start });
        images.Add((img, train.Format, firstSync, train is SstvVisPulseTrain, train.MeanPower));
      }
      return images;
    }


    /// <summary>Ground truth: every SSTV transmission in the corpus, listed by the user (2026-07-03) from
    /// spectrogram/audio inspection, as (startSec, endSec) per file-name substring. The 11_29_08 entry
    /// "265-202" is a typo in the source list, read as 165-202 (matches the detections there); its 4th
    /// transmission 478-516 was found by the detector and user-confirmed (2026-07-03). The 12_37_50
    /// 1-38 s transmission was found by the soft-comb and user-confirmed (2026-07-03 late) — too weak
    /// for an image, but detected.</summary>
    private static readonly (string file, (double t0, double t1)[] spans)[] Truth =
    {
      ("2026-04-18_12_36_09_UmKA-1", new[] { (0.0, 24.0) }),
      ("2026-04-19_12_19_50_UmKA-1", new[] { (110.0, 150.0), (295.0, 335.0), (484.0, 515.0) }),
      ("2026-06-30_22_36_37_UTMN2_Robot36", new[] { (37.0, 63.0), (183.0, 218.0) }),
      ("2026-07-01_11_02_25_Monitor-3", new[] { (140.0, 167.0), (285.0, 325.0) }),
      ("2026-07-01_11_09_11_VIZARD-meteo", new[] { (50.0, 88.0), (208.0, 245.0) }),
      ("2026-07-01_11_15_34_VIZARD-meteo", new[] { (0.0, 18.0) }),
      ("2026-07-01_11_29_08_UTMN2", new[] { (5.0, 45.0), (165.0, 202.0), (323.0, 357.0), (478.0, 516.0) }),
      ("2026-07-01_12_37_50_Monitor-3", new[] { (1.0, 38.0), (155.0, 160.0) }),
      ("2026-07-01_12_41_24_VIZARD-meteo", new[] { (0.0, 15.0), (135.0, 175.0), (292.0, 328.0) }),
    };

    [ManualFact("Result 2026-07-04 (locked P6(c) defaults: blanker 0.5 in the detection chain): 20 of 20 " +
      "matched, 0 missed, 1 DUP (the accepted 04-19 ~505 post-dropout continuation), and 1 genuine " +
      "FALSE — a comb-seeded Robot72 train at 11_09 117.9-161 s (p=3, fill 0.02). It first looked like " +
      "a real discovery (VIZARD's tagged mode, its cadence slot, and real-regime comb persistence: " +
      "z 4.3-4.7 for 19 checks in the residue-free 95-215 s slice), but the USER REFUTED it " +
      "(2026-07-04): that span holds only TELEMETRY bursts — so burst telemetry under the blanked " +
      "chain can sustain a comb ridge that passes every current guard, and the 12-check persistence " +
      "gate alone cannot separate telemetry from SSTV. A comb false-positive guard (e.g. a pulse-" +
      "support floor for comb trains before image emission — real comb finds have p>=7, this has p=3 — " +
      "or a telemetry-burst veto) is an open P7 item. The blanker also strengthens pulse support on " +
      "nearly every weak burst (12_37_50 7->23, 04-19 tail 39->54, 11_29 first 119->139); blanker OFF " +
      "exactly reproduces the 2026-07-03 baseline (20 matched, 1 dup, 0 false). Previous result " +
      "2026-07-03 late (soft-comb wired in, no blanker): 20/20, 0 false, 0 missed; 04-18 " +
      "detected+decoded for the first time (comb-seeded 0.1-28 s); comb found 12_37_50 1-38 s " +
      "(user-confirmed); comb guards proven: family-ring reset on retirement, 12-check persistence, " +
      "tight comb-train priors (periodPpm 200 / phaseMs 1.5).")]
    public void Real_TrainAccuracyProbe()
    {
      int nMatch = 0, nDup = 0, nFalse = 0, nMiss = 0;
      foreach (var (file, spans) in Truth)
      {
        string wav = Path.Combine(RecordingsDir, file + ".iq.wav");
        if (!File.Exists(wav)) { output.WriteLine($"{file}: ABSENT"); continue; }

        var (iq, sr) = WavIqReader.Read(wav);
        var o = new SstvDecodeOptions { SampleRate = sr };
        double[] sync = SstvDecoder.SyncAudio(SstvDecoder.Discriminator(iq, o), sr, o);
        var hits = SstvVisDetector.DetectAll(sync, sr);
        var extractor = SstvDecoder.ExtractTrains(sync, sr, hits);

        output.WriteLine($"--- {file}");
        var matched = new bool[spans.Length];
        foreach (var train in extractor.Trains)
        {
          if (!extractor.IsImageTrain(train)) continue;
          double t0 = train.Regr.GetPulseTime(0) / sr;
          double t1 = train.Regr.LastPulseTime / sr;
          string desc = $"{train.Format}{(train is SstvVisPulseTrain ? " VIS" : "")} {t0:0.0}-{t1:0.0}s " +
            $"p={train.PulseCnt} s={train.MeanPower:0.00} fill={extractor.FillRatio(train):0.00}";

          int hit = -1;
          for (int i = 0; i < spans.Length; i++)
            if (t1 > spans[i].t0 - 5 && t0 < spans[i].t1 + 5) { hit = i; break; }

          if (hit < 0) { nFalse++; output.WriteLine($"  FALSE  {desc}"); }
          else if (matched[hit]) { nDup++; output.WriteLine($"  DUP    {desc} (of {spans[hit].t0:0}-{spans[hit].t1:0})"); }
          else
          {
            matched[hit] = true; nMatch++;
            output.WriteLine($"  match  {desc} (truth {spans[hit].t0:0}-{spans[hit].t1:0})");
          }
        }
        for (int i = 0; i < spans.Length; i++)
          if (!matched[i]) { nMiss++; output.WriteLine($"  MISS   truth {spans[i].t0:0}-{spans[i].t1:0}"); }
      }
      output.WriteLine($"=== TOTAL: {nMatch} matched, {nDup} duplicates, {nFalse} false, {nMiss} missed " +
        $"(of {Truth.Sum(t => t.spans.Length)} transmissions)");
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


    [ManualFact("Result 2026-07-02 (a useful NEGATIVE): no corpus-wide win from a narrower fixed " +
      "detection channel. ±5000 ≈ ±6000 on strong bursts (some pulse gains: 199→224, 158→189) but it " +
      "SPLITS Monitor-3's clean 285 s train into two partial image trains; ±4000 clips the 3.3 kHz-dev " +
      "bursts (199→180, 102→65 pulses; 12_37_50 lost); ±3500 clearly degrades. And 04-18 UmKA-1 yields " +
      "0 images at EVERY bandwidth at the standard threshold. Verdict: keep ChannelBwHz 6000; channel " +
      "adaptivity must be per-burst deviation-aware, and 04-18 needs longer coherent integration.")]
    public void Real_DetectionChannelSweep()
    {
      // per capture × channel BW: image-train count and each image train's pulse count / mean score —
      // if ±4000 dominates ±6000 everywhere, detection gets its own (narrower) channel constant
      foreach (string wav in Directory.GetFiles(RecordingsDir, "*.iq.wav"))
      {
        var (iq, sr) = WavIqReader.Read(wav);
        output.WriteLine($"--- {Path.GetFileNameWithoutExtension(wav)}");
        foreach (double chanBw in new[] { 6000.0, 5000.0, 4000.0, 3500.0 })
        {
          var o = new SstvDecodeOptions { SampleRate = sr, ChannelBwHz = chanBw };
          double[] sync = SstvDecoder.SyncAudio(SstvDecoder.Discriminator(iq, o), sr, o);
          var hits = SstvVisDetector.DetectAll(sync, sr);
          var extractor = SstvDecoder.ExtractTrains(sync, sr, hits);

          var parts = new List<string>();
          foreach (var train in extractor.Trains)
            if (extractor.IsImageTrain(train))
              parts.Add($"{train.Format}@{train.Regr.GetPulseTime(0) / sr:0}s " +
                $"p={train.PulseCnt} s={train.MeanPower:0.00}{(train is SstvVisPulseTrain ? " VIS" : "")}");
          output.WriteLine($"  chan ±{chanBw:0}: images={parts.Count}  {string.Join(" | ", parts)}");
        }
      }
    }


    [ManualFact("Result 2026-07-04 (locked P6(c) defaults, blanker 0.5): the blanker SHARPENS the comb — " +
      "burst 12.0 s z=5.3 49 checks (was 5.2/55), 12_37 17.2 s z=4.6 56 checks (was 3.9-4.1/21), anchor " +
      "still 77.1 ms; control and all noise segments stay clean, including 150-215 alone. The new " +
      "95-215 s slice confirms Robot72 z=4.3-4.7 for 19 checks at ~148 s absolute — the scorecard's " +
      "FALSE train, evidenced here without any 50-88 s residue. USER-REFUTED (2026-07-04): the span " +
      "holds only telemetry bursts, so this is a telemetry-fed false ridge with REAL-REGIME persistence " +
      "— persistence cannot separate burst telemetry from SSTV (see Real_TrainAccuracyProbe for the " +
      "open guard item). Previous result 2026-07-03 late (comb finished and " +
      "wired, no blanker): burst 10.5 s z=5.2, 55 checks, anchor 77.1 ms = the batch phase; control and " +
      "both noise-fire segments clean; 12_37 fires z 3.9-4.1 for 21 checks (real, user-confirmed). What " +
      "closed the false fires: (a) touch-count variance normalization (1-lambda^2k)/(1-lambda^2) before " +
      "pooling (z 3.6 -> 3.4); (b) HitFactor 1.6 -> 1.8 — the threshold must cover the max over a " +
      "pass's worth of ~memory-length ring redraws, sqrt(2 ln(Neff*redraws)) ~ 3.4, not the " +
      "instantaneous 2.06; (c) the 12-check persistence gate — noise extremes wander off within ~3 " +
      "checks, real ridges are re-fed every period (55/21 checks).")]
    public void Real_StreamingCombProbe()
    {
      string hardWav = Path.Combine(RecordingsDir, "2026-04-18_12_36_09_UmKA-1.iq.wav");
      string ctrlWav = Path.Combine(RecordingsDir, "2026-07-01_11_29_08_UTMN2.iq.wav");
      if (!File.Exists(hardWav) || !File.Exists(ctrlWav)) { output.WriteLine("captures absent; probe skipped"); return; }

      var (iqH, srH) = WavIqReader.Read(hardWav);
      var (iqC, srC) = WavIqReader.Read(ctrlWav);
      Report("burst  ", iqH[..(int)(24 * srH)], srH);
      Report("control", iqC[(int)(60 * srC)..(int)(84 * srC)], srC);

      // the segments where the scorecard runs showed comb false-fires (no real transmission per the
      // ground-truth cadence): early 12_37 (a REAL-transmission candidate — RF stripes on cadence),
      // early 11_09 (noise, 3-check run), 11_29 ~450 (noise) and 11_09 ~170-208 (the Robot72 phantom).
      // 11_09 95-215 isolates the blanker-era Robot72 false train at ~148 s (user-refuted 2026-07-04:
      // only telemetry bursts there — see the Real_TrainAccuracyProbe annotation): the slice carries no
      // residue of the 50-88 s transmission, yet the telemetry-fed ridge confirms with real-regime
      // persistence
      string m3Wav = Path.Combine(RecordingsDir, "2026-07-01_12_37_50_Monitor-3.iq.wav");
      string vzWav = Path.Combine(RecordingsDir, "2026-07-01_11_09_11_VIZARD-meteo.iq.wav");
      if (File.Exists(m3Wav))
      {
        var (iq3, sr3) = WavIqReader.Read(m3Wav);
        Report("12_37 0-40 s", iq3[..(int)(40 * sr3)], sr3);
      }
      if (File.Exists(vzWav))
      {
        var (iq4, sr4) = WavIqReader.Read(vzWav);
        Report("11_09 0-40 s", iq4[..(int)(40 * sr4)], sr4);
        Report("11_09 150-215", iq4[(int)(150 * sr4)..(int)(215 * sr4)], sr4);
        Report("11_09 95-215", iq4[(int)(95 * sr4)..(int)(215 * sr4)], sr4);
      }
      Report("11_29 400-470", iqC[(int)(400 * srC)..(int)(470 * srC)], srC);

      void Report(string name, Complex32[] iq, double sr)
      {
        var o = new SstvDecodeOptions { SampleRate = sr };
        double[] sync = SstvDecoder.SyncAudio(SstvDecoder.Discriminator(iq, o), sr, o);
        var spec = SstvModes.Get(SstvMode.Robot36);

        var comb = new SstvSoftComb(sr);
        var det = new SstvPulseDetector(sr, spec.SyncMs)
        { ScoreTap = (t, s) => comb.Process(spec.SyncMs, t, s) };

        int block = (int)(0.25 * sr);
        long firstHit = -1;
        double hitZ = 0, maxZ = 0;
        int nHits = 0;
        SstvMode? hitMode = null;
        var pulses = new List<SstvPulse>();
        for (int t = 0; t < sync.Length; t++)
        {
          det.Process(sync[t], pulses);
          if (t % block == block - 1 && comb.Check(t) is SstvCombHit h)
          {
            nHits++;
            if (h.Z > maxZ) maxZ = h.Z;
            if (firstHit < 0) { firstHit = t; hitZ = h.Z; hitMode = h.Mode; }
          }
        }

        // final ring scan for the trajectory summary (peak z regardless of the hit gate)
        comb.Check(sync.Length - 1);
        var final = comb.Check(sync.Length - 1);
        output.WriteLine($"{name}: first confirmed hit " +
          (firstHit < 0 ? "NONE" : $"at {firstHit / sr:0.0}s {hitMode} z={hitZ:0.0}") +
          $"; {nHits} confirmed checks, max z={maxZ:0.0}" +
          $"; final best {(final is SstvCombHit f ? $"{f.Mode} z={f.Z:0.0} anchor%P={(f.AnchorSample % 7200) / sr * 1000:0.0}ms" : "none ≥ HitZ")}");
      }
    }


    [ManualFact("Result 2026-07-03 — the soft-comb VALIDATES on the hardest case: combing the " +
      "un-thresholded score over 160 Robot36 periods of the 04-18 burst gives a coherent ridge at " +
      "z=4.5 (all top-20 phases within ±1 ms of one phase, identical at chan ±6000 and ±4000 — the comb " +
      "is insensitive to the FM-threshold clicking), vs z=2.3-2.6 on an equal-duration noise control. " +
      "Single-pulse scores on the same data are non-separable (burst max 0.221-0.286 vs control " +
      "0.181-0.202). Margin ~2 sigma at 24 s, grows as sqrt(N) with transmission length; the streaming " +
      "comb should add a shaped kernel / robust normalization to widen it.")]
    public void Real_SoftCombProbe()
    {
      string hardWav = Path.Combine(RecordingsDir, "2026-04-18_12_36_09_UmKA-1.iq.wav");
      string ctrlWav = Path.Combine(RecordingsDir, "2026-07-01_11_29_08_UTMN2.iq.wav");
      if (!File.Exists(hardWav) || !File.Exists(ctrlWav)) { output.WriteLine("captures absent; probe skipped"); return; }

      var (iqH, srH) = WavIqReader.Read(hardWav);
      var (iqC, srC) = WavIqReader.Read(ctrlWav);
      var burst = iqH[..(int)(24 * srH)];
      var control = iqC[(int)(60 * srC)..(int)(84 * srC)];

      foreach (double chanBw in new[] { 6000.0, 4000.0 })
      {
        Report("burst  ", burst, srH, chanBw);
        Report("control", control, srC, chanBw);
      }

      void Report(string name, Complex32[] iq, double sr, double chanBw)
      {
        var o = new SstvDecodeOptions { SampleRate = sr, ChannelBwHz = chanBw };
        double[] sync = SstvDecoder.SyncAudio(SstvDecoder.Discriminator(iq, o), sr, o);
        var spec = SstvModes.Get(SstvMode.Robot36);

        // tap the un-thresholded score stream from the Robot-family detector
        var trace = new double[sync.Length];
        var det = new SstvPulseDetector(sr, spec.SyncMs)
        { ScoreTap = (t, s) => { if (t >= 0 && t < trace.Length) trace[t] = s; } };
        det.Detect(sync);

        // the batch comb (measurement only, §1.13-exempt): A[phase] = Σ_k score(phase + k·P)
        int period = (int)Math.Round(spec.LinePeriodMs / 1000.0 * sr);
        var comb = new double[period];
        int periods = trace.Length / period;
        for (int k = 0; k < periods; k++)
          for (int ph = 0; ph < period; ph++)
            comb[ph] += trace[k * period + ph];

        double mean = 0; foreach (double v in comb) mean += v; mean /= period;
        double var = 0; foreach (double v in comb) var += (v - mean) * (v - mean); var /= period;
        double sd = Math.Sqrt(var);
        int peakPh = 0;
        for (int ph = 1; ph < period; ph++) if (comb[ph] > comb[peakPh]) peakPh = ph;

        // is the peak an isolated ridge? count phases within 1 ms of the peak among the top-20 phases
        var top = Enumerable.Range(0, period).OrderByDescending(ph => comb[ph]).Take(20).ToList();
        int nearPeak = top.Count(ph => Math.Abs(ph - peakPh) < 0.001 * sr || period - Math.Abs(ph - peakPh) < 0.001 * sr);

        output.WriteLine($"{name} chan ±{chanBw:0}: {periods} periods, comb peak z={(comb[peakPh] - mean) / sd:0.0} " +
          $"@ phase {peakPh / sr * 1000:0.0} ms, top-20 within ±1 ms of peak: {nearPeak}/20, " +
          $"single-pulse maxScore={det.MaxScore:0.000}");
      }
    }


    [ManualFact("Result 2026-07-02 (a decisive NEGATIVE): widening the coherence window HURTS everywhere " +
      "— hard case 0.286/0.239/0.200 and strong burst 0.420/0.377/0.329 at 4/6/8 ms. The 9 ms sync pulse " +
      "bounds single-pulse integration: a wider window eats the time template's flat top instead of " +
      "adding gain, so 4 ms is near-optimal. Clutter tracks the burst max at EVERY window (0.42/0.42, " +
      "0.38/0.37, 0.33/0.32): single-pulse scores are fundamentally non-separable — the only remaining " +
      "sensitivity path is CROSS-pulse soft-evidence accumulation (the plan's soft-comb option), not a " +
      "longer matched-filter window.")]
    public void Real_CoherenceWindowSweep()
    {
      string hardWav = Path.Combine(RecordingsDir, "2026-04-18_12_36_09_UmKA-1.iq.wav");
      string strongWav = Path.Combine(RecordingsDir, "2026-06-30_22_36_37_UTMN2_Robot36.iq.wav");
      if (!File.Exists(hardWav) || !File.Exists(strongWav)) { output.WriteLine("captures absent; probe skipped"); return; }

      var spec = SstvModes.Get(SstvMode.Robot36);

      // hard case: full 0–24 s burst at its matched channel
      var (iqH, srH) = WavIqReader.Read(hardWav);
      var oH = new SstvDecodeOptions { SampleRate = srH, ChannelBwHz = 4000.0 };
      double[] syncH = SstvDecoder.SyncAudio(SstvDecoder.Discriminator(iqH[..(int)(24 * srH)], oH), srH, oH);

      // strong case: burst interior vs a noise-only clutter region (the Real_SyncScoreProbe spans)
      var (iqS, srS) = WavIqReader.Read(strongWav);
      var oS = new SstvDecodeOptions { SampleRate = srS };
      double[] syncS = SstvDecoder.SyncAudio(SstvDecoder.Discriminator(iqS, oS), srS, oS);
      double[] burst = syncS[(int)(185 * srS)..(int)(215 * srS)];
      double[] clutter = syncS[(int)(20 * srS)..(int)(45 * srS)];

      foreach (double winMs in new[] { 4.0, 6.0, 8.0 })
      {
        var detH = new SstvPulseDetector(srH, spec.SyncMs, winMs);
        var pulsesH = detH.Detect(syncH);
        int grid = 0;
        double period = spec.LinePeriodMs / 1000.0 * srH;
        for (int i = 1; i < pulsesH.Count; i++)
        {
          double gap = pulsesH[i].Time - (double)pulsesH[i - 1].Time;
          double frac = gap / period;
          if (Math.Abs(frac - Math.Round(frac)) * period < 0.005 * srH && frac < 20) grid++;
        }

        var detBurst = new SstvPulseDetector(srS, spec.SyncMs, winMs);
        detBurst.Detect(burst);
        var detClutter = new SstvPulseDetector(srS, spec.SyncMs, winMs);
        detClutter.Detect(clutter);

        output.WriteLine($"win {winMs} ms: hard maxScore={detH.MaxScore:0.000} pulses={pulsesH.Count} " +
          $"onGrid={grid} | strong burst max={detBurst.MaxScore:0.000} clutter max={detClutter.MaxScore:0.000}");
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
            VideoChannelBwHz = chanBw,                       // Decode() runs the video chain (P6(c) lock)
            BrightnessBwHz = videoBw
          });
          string path = Path.Combine(OutDir, $"sweep_Monitor3_chan{chanBw:0}_vid{videoBw:0}.png");
          img.SavePng(path);
          output.WriteLine($"chan={chanBw} video={videoBw} -> {Path.GetFileName(path)}");
        }
    }


    [ManualFact("Result 2026-07-03 (first run; PNGs NOT yet visually judged — do that before locking): " +
      "the envelope-gated blanker wins on ALL four real bursts and never hurts — clicks 2.4→0.0 %, and " +
      "on the hardest burst (04-18) sync maxScore 0.221→0.324 at chan ±4000 + blank 0.5; rowNoise drops " +
      "monotonically with blanker everywhere (utmn2236 32.7→26.6, m3_1102 53.5→46.6). chan ±4000-4500 " +
      "beats ±6000 for DECODE on every case (detection keeps ±6000 per Real_DetectionChannelSweep). " +
      "12_37_50 stays noise-dominated (rowNoise ~70) — likely unrecoverable. NOTE the synthetic sweep " +
      "(Frontend_BlankerAndChannelSweep) shows the OPPOSITE (blanker mildly negative): synthetic AWGN at " +
      "σ≤0.6 produces ≤0.05 % clicks — real FM noise is impulsive in a way the closed loop does not " +
      "model; trust the real grid. Pending: visual PNG judgment, blanker threshold 0.3 vs 0.5 call, " +
      "then lock a decode-stage ChannelBwHz (or dev-matched rule) + BlankerThreshold default. " +
      "Re-run 2026-07-03 on the clean Monitor-3 285 s train only (m3_1102b, blanker grid extended to " +
      "0.7): the locked defaults hold — blanker removes all clicks (0.69→0.00 %) with no sync-score " +
      "cost; rowNoise bottoms at blank 0.3 (25.9 @ ±4000/±4500) with 0.5 within 0.1–0.3 of it; 0.7 " +
      "over-blanks (rowNoise up ~1.4–2.1 everywhere) — do not raise the default. chan ±4000 ≈ ±4500 " +
      "beat ±6000; chan4000+blank0.5 reads 26.0 vs the 25.9 grid optimum: no regression on the " +
      "cleanest burst.")]
    public void Real_P6cDecodeGridProbe()
    {
      // P6(c): the decode-stage front end (detection stays at the ±6000 default). Grid: Stage-1 channel BW ×
      // envelope-gated blanker threshold, on two strong bursts (fidelity must not regress) and the two
      // below-FM-threshold speckle residuals (04-18, 12_37_50). Quantities: discriminator click rate, sync
      // matched-filter max, decoded-image row-to-row luma noise; the PNGs are the real verdict.
      (string tag, string file, double t0, double t1)[] cases =
      {
        ("utmn2236", "2026-06-30_22_36_37_UTMN2_Robot36", 183.0, 218.0),
        ("m3_1102",  "2026-07-01_11_02_25_Monitor-3",     140.0, 167.0),
        ("umka0418", "2026-04-18_12_36_09_UmKA-1",          0.0,  24.0),
        ("m3_1237",  "2026-07-01_12_37_50_Monitor-3",       1.0,  38.0),
        ("m3_1102b", "2026-07-01_11_02_25_Monitor-3",     285.0, 325.0),
      };

      foreach (var (tag, file, t0, t1) in cases)
      {
        string wav = Path.Combine(RecordingsDir, file + ".iq.wav");
        if (!File.Exists(wav)) { output.WriteLine($"{tag}: capture absent"); continue; }
        var (iq, sr) = WavIqReader.Read(wav);
        var seg = iq[(int)(Math.Max(0, t0 - 1) * sr)..Math.Min(iq.Length, (int)((t1 + 1) * sr))];

        // locate the train once, at the fixed detection defaults, so every config decodes the same slice
        var oDet = new SstvDecodeOptions { SampleRate = sr };
        double[] discDet = SstvDecoder.Discriminator(seg, oDet);
        double[] syncDet = SstvDecoder.SyncAudio(discDet, sr, oDet);
        var hits = SstvVisDetector.DetectAll(syncDet, sr);
        var extractor = SstvDecoder.ExtractTrains(syncDet, sr, hits);
        SstvPulseTrain? best = null;
        foreach (var train in extractor.Trains)
          if (extractor.IsImageTrain(train) && (best == null || train.PulseCnt > best.PulseCnt)) best = train;
        if (best == null) { output.WriteLine($"{tag}: no image train at detection defaults"); continue; }
        int firstSync = (int)Math.Round(best.Regr.GetPulseTime(0));
        var spec = SstvModes.Get(best.Format);
        output.WriteLine($"--- {tag}: {best.Format} train @{firstSync / sr:0.0}s p={best.PulseCnt}");

        foreach (double chanBw in new[] { 6000.0, 4500.0, 4000.0 })
          foreach (double blank in new[] { 0.0, 0.3, 0.5, 0.7 })
          {
            var o = new SstvDecodeOptions
            { SampleRate = sr, ChannelBwHz = chanBw, BlankerThreshold = blank,
              Acquire = false, StartSample = firstSync };
            double[] disc = SstvDecoder.Discriminator(seg, o);

            int clicks = 0;
            for (int i = 0; i < disc.Length; i++) if (Math.Abs(disc[i]) > 15000) clicks++;
            var det = new SstvPulseDetector(sr, spec.SyncMs);
            det.Detect(SstvDecoder.SyncAudio(disc, sr, o));

            var img = SstvDecoder.Decode(disc, best.Format, o);
            string path = Path.Combine(OutDir, $"p6c_{tag}_chan{chanBw:0}_blk{blank * 10:0}.png");
            img.SavePng(path);
            output.WriteLine($"  chan ±{chanBw:0} blank {blank:0.0}: clicks={100.0 * clicks / disc.Length:0.00}% " +
              $"maxScore={det.MaxScore:0.000} rowNoise={RowNoise(img):0.0} -> {Path.GetFileName(path)}");
          }
      }
    }

    /// <summary>Mean absolute luma difference between vertically adjacent pixels — a reference-free
    /// speckle/noise proxy (image content correlates line-to-line; noise does not). Lower is quieter,
    /// but over-smoothing also lowers it: read together with the PNGs.</summary>
    private static double RowNoise(RgbImage img)
    {
      double sum = 0; long n = 0;
      for (int y = 1; y < img.Height; y++)
        for (int x = 0; x < img.Width; x++)
        {
          var (r1, g1, b1) = img.Get(x, y - 1);
          var (r2, g2, b2) = img.Get(x, y);
          double y1 = 0.299 * r1 + 0.587 * g1 + 0.114 * b1;
          double y2 = 0.299 * r2 + 0.587 * g2 + 0.114 * b2;
          sum += Math.Abs(y2 - y1); n++;
        }
      return n > 0 ? sum / n : 0;
    }


    [ManualFact("Result 2026-07-04 — DeEmphasisUs default LOCKED at 0 (off). Raw metrics mildly favor " +
      "tau: rowNoise falls monotonically (utmn2236 26.6->24.3, m3_1237 72.3->64.5 at 750 µs) and " +
      "maxScore ticks up (m3_1237 0.200->0.239). But Real_PreEmphasisSlopeProbe shows the TX does NOT " +
      "pre-emphasize (tilt ≈ -1 dB flat at ±15 kHz, vs +1.4/+3.2 dB predicted for 75/750 µs), so this " +
      "is the plan's null case — noise-vs-sharpness only; the synthetic closed loop (ground truth, " +
      "Frontend_DeEmphasisSweep) prices that trade NEGATIVE (-0.8..-1.2 dB PSNR at 300/750 µs, 75 µs a " +
      "wash), and visually the tau PNGs only smooth speckle slightly (strong bursts already clean, " +
      "umka0418 stays speckle at every tau). Keeping the stage as an option for future " +
      "pre-emphasizing transmitters.")]
    public void Real_P6cDeEmphasisProbe()
    {
      // same burst set and metrics as Real_P6cDecodeGridProbe, chain fixed at the locked defaults, only
      // the de-emphasis time constant varies (0 = off, 75 µs broadcast, 300 µs NBFM, 750 µs deep tilt)
      (string tag, string file, double t0, double t1)[] cases =
      {
        ("utmn2236", "2026-06-30_22_36_37_UTMN2_Robot36", 183.0, 218.0),
        ("m3_1102",  "2026-07-01_11_02_25_Monitor-3",     140.0, 167.0),
        ("umka0418", "2026-04-18_12_36_09_UmKA-1",          0.0,  24.0),
        ("m3_1237",  "2026-07-01_12_37_50_Monitor-3",       1.0,  38.0),
        ("m3_1102b", "2026-07-01_11_02_25_Monitor-3",     285.0, 325.0),
      };

      foreach (var (tag, file, t0, t1) in cases)
      {
        string wav = Path.Combine(RecordingsDir, file + ".iq.wav");
        if (!File.Exists(wav)) { output.WriteLine($"{tag}: capture absent"); continue; }
        var (iq, sr) = WavIqReader.Read(wav);
        var seg = iq[(int)(Math.Max(0, t0 - 1) * sr)..Math.Min(iq.Length, (int)((t1 + 1) * sr))];

        // locate the train once, at the fixed detection defaults, so every config decodes the same slice
        var oDet = new SstvDecodeOptions { SampleRate = sr };
        double[] discDet = SstvDecoder.Discriminator(seg, oDet);
        double[] syncDet = SstvDecoder.SyncAudio(discDet, sr, oDet);
        var hits = SstvVisDetector.DetectAll(syncDet, sr);
        var extractor = SstvDecoder.ExtractTrains(syncDet, sr, hits);
        SstvPulseTrain? best = null;
        foreach (var train in extractor.Trains)
          if (extractor.IsImageTrain(train) && (best == null || train.PulseCnt > best.PulseCnt)) best = train;
        if (best == null) { output.WriteLine($"{tag}: no image train at detection defaults"); continue; }
        int firstSync = (int)Math.Round(best.Regr.GetPulseTime(0));
        var spec = SstvModes.Get(best.Format);
        output.WriteLine($"--- {tag}: {best.Format} train @{firstSync / sr:0.0}s p={best.PulseCnt}");

        foreach (double tau in new[] { 0.0, 75.0, 300.0, 750.0 })
        {
          var o = new SstvDecodeOptions
          { SampleRate = sr, ChannelBwHz = 4000.0, BlankerThreshold = 0.5, DeEmphasisUs = tau,
            Acquire = false, StartSample = firstSync };
          double[] disc = SstvDecoder.Discriminator(seg, o);

          var det = new SstvPulseDetector(sr, spec.SyncMs);
          det.Detect(SstvDecoder.SyncAudio(disc, sr, o));

          var img = SstvDecoder.Decode(disc, best.Format, o);
          string path = Path.Combine(OutDir, $"p6c_deemph_{tag}_tau{tau:0}.png");
          img.SavePng(path);
          output.WriteLine($"  tau {tau,3:0} us: maxScore={det.MaxScore:0.000} " +
            $"rowNoise={RowNoise(img):0.0} -> {Path.GetFileName(path)}");
        }
      }
    }


    [ManualFact("USER'S VISUAL JUDGMENT 2026-07-04 — variant w9x5_k4_ns wins everywhere an image exists " +
      "(utmn2236, vz_1109, m3_1102 'best denoising', m3_1102b 'denoises and preserves fine structure'); " +
      "umka0418 is better RAW (the only version showing some detail through the below-FM-threshold " +
      "noise) and m3_1237 has no image at any setting. LOCKED into production as SstvWienerFilter " +
      "(window 9x5, chroma k=4, no shrink, image-domain vertical-diff map), default ON via " +
      "SstvDecodeOptions.WienerEnabled (off = the raw inspection path for the umka0418 class). " +
      "Probe/prototype kept for future re-judgment. Original run notes: " +
      "P6(d) steps 1+2 run 2026-07-04 (plan §6.2): " +
      "Lee-filter variants (window × chroma k × shrink) on the P6(c) burst set, A/B'd across two noise " +
      "maps — image-domain row-wise vertical-difference (p6d_*_w*.png) vs the demod-domain guard-band " +
      "pilot calibrated to it (p6d_*_pilot_*.png). Key finding: the plan's 3×3 Immerkær estimator reads " +
      "SEVERAL TIMES LOW on real bursts (its horizontal second difference vanishes on the horizontally-" +
      "correlated post-±600Hz-LPF noise blobs; utmn2236 σnY read 6.4 vs 12.4 true) — with it neither map " +
      "shrank anything. Replaced by the vertical first-difference median estimator (lines are independent " +
      "time slices, so inter-line diffs carry the full noise power; chroma steps 2 rows over Robot36's " +
      "duplicated rows) and the filter came alive: speckle collapses to local mean/neutral gray on every " +
      "fade (utmn2236 rowNoise 26.6→15.1, m3_1102 46.6→14.1, umka0418's below-threshold speckle → uniform " +
      "gray) while the m3_1102b text card stays fully readable. Visually the image-domain map beats the " +
      "median-calibrated pilot map (pilot leaves more speckle); k and shrink barely change rowNoise — " +
      "judge them on the chroma. Frontend_WienerSweep guards the closed loop: +5.2 dB at σ=0.5, no-op " +
      "when clean, edges pass (colorbars +0.3).")]
    public void Real_P6dWienerProbe()
    {
      // the P6(c) burst set + one strong VIZARD control (vz_1109); decode once per case at the locked
      // defaults, then apply each prototype variant to the decoded image (zero decoder changes). The
      // known mode is pinned per case so a wrong-format hypothesis in the slice cannot hijack the pick.
      // vz_1109 is pinned Robot36 despite the "Robot 72" transmitter tag: the real cadence is 150 ms
      // (a full 218-pulse Robot36 train; Robot72's 300 ms grid could hold at most ~110), and the
      // full-file harness likewise decodes every real 11_09 burst as Robot36.
      (string tag, string file, double t0, double t1, SstvMode mode)[] cases =
      {
        ("utmn2236", "2026-06-30_22_36_37_UTMN2_Robot36", 183.0, 218.0, SstvMode.Robot36),
        ("m3_1102",  "2026-07-01_11_02_25_Monitor-3",     140.0, 167.0, SstvMode.Robot36),
        ("umka0418", "2026-04-18_12_36_09_UmKA-1",          0.0,  24.0, SstvMode.Robot36),
        ("m3_1237",  "2026-07-01_12_37_50_Monitor-3",       1.0,  38.0, SstvMode.Robot36),
        ("m3_1102b", "2026-07-01_11_02_25_Monitor-3",     285.0, 325.0, SstvMode.Robot36),
        ("vz_1109",  "2026-07-01_11_09_11_VIZARD-meteo",   50.0,  88.0, SstvMode.Robot36),
      };

      foreach (var (tag, file, t0, t1, mode) in cases)
      {
        string wav = Path.Combine(RecordingsDir, file + ".iq.wav");
        if (!File.Exists(wav)) { output.WriteLine($"{tag}: capture absent"); continue; }
        var (iq, sr) = WavIqReader.Read(wav);
        var seg = iq[(int)(Math.Max(0, t0 - 1) * sr)..Math.Min(iq.Length, (int)((t1 + 1) * sr))];

        // locate the train once, at the fixed detection defaults, so every variant filters the same decode
        var oDet = new SstvDecodeOptions { SampleRate = sr };
        double[] discDet = SstvDecoder.Discriminator(seg, oDet);
        double[] syncDet = SstvDecoder.SyncAudio(discDet, sr, oDet);
        var hits = SstvVisDetector.DetectAll(syncDet, sr);
        var extractor = SstvDecoder.ExtractTrains(syncDet, sr, hits);
        SstvPulseTrain? best = null;
        foreach (var train in extractor.Trains)
          if (train.Format == mode && extractor.IsImageTrain(train) &&
              (best == null || train.PulseCnt > best.PulseCnt)) best = train;
        if (best == null) { output.WriteLine($"{tag}: no {mode} image train at detection defaults"); continue; }
        int firstSync = (int)Math.Round(best.Regr.GetPulseTime(0));

        var spec = SstvModes.Get(best.Format);
        var o = new SstvDecodeOptions
        { SampleRate = sr, ChannelBwHz = 4000.0, BlankerThreshold = 0.5, WienerEnabled = false,
          Acquire = false, StartSample = firstSync };
        double[] disc = SstvDecoder.Discriminator(seg, o);
        var raw = SstvDecoder.Decode(disc, best.Format, o);
        raw.SavePng(Path.Combine(OutDir, $"p6d_{tag}_raw.png"));
        var (sy, scr, scb) = SstvWienerPrototype.NoiseSigmas(raw);
        output.WriteLine($"--- {tag}: {best.Format} @{firstSync / sr:0.0}s p={best.PulseCnt} " +
          $"rawRowNoise={RowNoise(raw):0.0} σn Y={sy:0.0} Cr={scr:0.0} Cb={scb:0.0}");

        foreach ((int ww, int wh) in new[] { (7, 3), (9, 5) })
          foreach (double ck in new[] { 2.0, 4.0 })
            foreach (bool shrink in new[] { false, true })
            {
              var img = SstvWienerPrototype.Apply(raw, new SstvWienerOptions
              { WindowW = ww, WindowH = wh, ChromaK = ck, ShrinkToNeutral = shrink });
              string name = $"p6d_{tag}_w{ww}x{wh}_k{ck:0}_{(shrink ? "sh" : "ns")}.png";
              img.SavePng(Path.Combine(OutDir, name));
              output.WriteLine($"  w{ww}x{wh} k={ck:0} {(shrink ? "shrink" : "no-shrink")}: " +
                $"rowNoise={RowNoise(img):0.0} -> {name}");
            }

        // demod-domain σ²n A/B (plan §6.2 item 2, map (a)): guard-band pilot power at 2600–3400 Hz —
        // no SSTV energy lives there — averaged over each pixel's scan span on the train's line grid,
        // then calibrated to the image-domain absolute scale (median-to-median). The pilot supplies
        // the per-pixel localization (fades light up) that the Immerkær map cannot see in the
        // horizontally-correlated post-LPF FM noise.
        var (varY, varC) = PilotNoiseMaps(GuardBandPower(disc, sr), best, firstSync, spec, sr);
        CalibrateMap(varY, sy * sy);
        CalibrateMap(varC, (scr * scr + scb * scb) / 2);
        foreach (double ck in new[] { 2.0, 4.0 })
          foreach (bool shrink in new[] { false, true })
          {
            var img = SstvWienerPrototype.Apply(raw, new SstvWienerOptions
            { ChromaK = ck, ShrinkToNeutral = shrink }, varY, varC);
            string name = $"p6d_{tag}_pilot_k{ck:0}_{(shrink ? "sh" : "ns")}.png";
            img.SavePng(Path.Combine(OutDir, name));
            output.WriteLine($"  pilot w7x3 k={ck:0} {(shrink ? "shrink" : "no-shrink")}: " +
              $"rowNoise={RowNoise(img):0.0} -> {name}");
          }
      }
    }

    /// <summary>Per-sample power of the 2600–3400 Hz guard band of the discriminated audio — the SSTV
    /// tones end at 2300 Hz, so this band is pure post-FM noise; its power tracks the video-band noise
    /// (both scale with 1/CNR) and localizes fades per sample.</summary>
    private static double[] GuardBandPower(double[] disc, double sr)
    {
      float[] lp = global::VE3NEA.Dsp.BlackmanSincKernel(400.0 / sr, 401);
      var bp = new float[lp.Length];
      int center = lp.Length / 2;
      double w0 = 2 * Math.PI * 3000.0 / sr;
      for (int i = 0; i < lp.Length; i++) bp[i] = 2f * lp[i] * (float)Math.Cos(w0 * (i - center));

      var x = new float[disc.Length];
      for (int i = 0; i < disc.Length; i++) x[i] = (float)disc[i];
      float[] y = VE3NEA.LiquidFir.ConvolveSame(x, bp);
      var power = new double[disc.Length];
      for (int i = 0; i < power.Length; i++) power[i] = (double)y[i] * y[i];
      return power;
    }

    /// <summary>Per-pixel noise maps for the luma and (shared) chroma planes: mean pilot power over
    /// each pixel's scan span, lines laid on the train's tracked grid. Uncalibrated — relative shape
    /// only; <see cref="CalibrateMap"/> sets the absolute scale. Robot layouts only.</summary>
    private static (double[] varY, double[] varC) PilotNoiseMaps(double[] pilot, SstvPulseTrain train,
      int firstSync, SstvModeSpec spec, double sr)
    {
      int w = spec.Width, h = spec.Height;
      var mapY = new double[w * h];
      var mapC = new double[w * h];
      int line0 = train.Regr.GetPulseNo(firstSync);
      double Ms(double ms) => ms / 1000.0 * sr;

      void FillRow(double[] map, int row, double segStart, double segSamples)
      {
        for (int p = 0; p < w; p++)
        {
          int a = (int)Math.Round(segStart + p * segSamples / w);
          int b = (int)Math.Round(segStart + (p + 1) * segSamples / w);
          double sum = 0; int cnt = 0;
          for (int i = Math.Max(0, a); i < Math.Min(pilot.Length, b); i++) { sum += pilot[i]; cnt++; }
          map[row * w + p] = cnt > 0 ? sum / cnt : -1;       // -1 = out of range, filled below
        }
      }

      for (int line = 0; line < spec.LineCount && line < h; line++)
      {
        double yStart = train.Regr.GetPulseTime(line0 + line) + Ms(spec.SyncMs + spec.SyncPorchMs);
        FillRow(mapY, line, yStart, Ms(spec.ScanYMs));
        double c1 = yStart + Ms(spec.ScanYMs + spec.SepMs + spec.SepPorchMs);
        FillRow(mapC, line, c1, Ms(spec.ScanChromaMs));
        if (spec.Layout == SstvColorLayout.Robot72)
        {
          // both chroma scans carry noise for this row — average the second span in
          var second = new double[w * h];
          double c2 = c1 + Ms(spec.ScanChromaMs + spec.SepMs + spec.SepPorchMs);
          FillRow(second, line, c2, Ms(spec.ScanChromaMs));
          for (int p = 0; p < w; p++)
          {
            int i = line * w + p;
            if (mapC[i] >= 0 && second[i] >= 0) mapC[i] = (mapC[i] + second[i]) / 2;
          }
        }
      }

      FillMissing(mapY);
      FillMissing(mapC);
      return (mapY, mapC);

      static void FillMissing(double[] map)
      {
        var valid = new List<double>();
        foreach (double v in map) if (v >= 0) valid.Add(v);
        if (valid.Count == 0) return;
        valid.Sort();
        double med = valid[valid.Count / 2];
        for (int i = 0; i < map.Length; i++) if (map[i] < 0) map[i] = med;
      }
    }

    /// <summary>Scale a pilot map so its median equals the image-domain noise variance — the plan's
    /// "image-domain calibration" role: the pilot gives the shape, Immerkær the absolute level.</summary>
    private static void CalibrateMap(double[] map, double targetMedianVar)
    {
      var s = (double[])map.Clone();
      Array.Sort(s);
      double med = s[s.Length / 2];
      if (med <= 0) return;
      double k = targetMedianVar / med;
      for (int i = 0; i < map.Length; i++) map[i] *= k;
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


    [ManualFact("Result 2026-07-04 — NO transmitter pre-emphasis: at chan ±15000 (no sideband clipping) " +
      "all four strong bursts read tilt -0.7..-1.3 dB ≈ flat (75 µs would read +1.4, ≥300 µs +3.2); the " +
      "much steeper -2.4..-4.2 dB at chan ±4000 is the RX artifact — the narrow channel clips FM " +
      "sidebands harder at higher subcarrier frequencies. Settles plan §6 item 2 on the null case and " +
      "locks DeEmphasisUs = 0 together with Frontend_DeEmphasisSweep (see Real_P6cDeEmphasisProbe). " +
      "Method: if the TX pre-emphasized, the recovered subcarrier's AMPLITUDE rises with its " +
      "instantaneous frequency; measures the mean analytic amplitude per 100 Hz brightness bin over " +
      "strong-burst interiors, tilt = 1550->2250 Hz amplitude ratio.")]
    public void Real_PreEmphasisSlopeProbe()
    {
      // strong bursts only — amplitude bins need the subcarrier well above the noise
      (string tag, string file, double t0, double t1)[] cases =
      {
        ("utmn2236 ", "2026-06-30_22_36_37_UTMN2_Robot36", 183.0, 218.0),
        ("utmn2236a", "2026-06-30_22_36_37_UTMN2_Robot36",  37.0,  63.0),
        ("m3_1102b ", "2026-07-01_11_02_25_Monitor-3",     285.0, 325.0),
        ("vz_1109  ", "2026-07-01_11_09_11_VIZARD-meteo",   50.0,  88.0),
      };

      foreach (var (tag, file, t0, t1) in cases)
      {
        string wav = Path.Combine(RecordingsDir, file + ".iq.wav");
        if (!File.Exists(wav)) { output.WriteLine($"{tag}: capture absent"); continue; }
        var (iq, sr) = WavIqReader.Read(wav);
        var seg = iq[(int)(t0 * sr)..Math.Min(iq.Length, (int)(t1 * sr))];

        // NO de-emphasis — we are measuring the TX tilt itself. Two channel widths separate the TX tilt
        // from the RX artifact: a narrow channel clips FM sidebands harder at higher subcarrier
        // frequencies (Carson ≈ ±(dev + f_sub) > ±4000), faking a negative tilt; ±15000 clips nothing.
        foreach (double chanBw in new[] { 4000.0, 15000.0 })
        {
          var o = new SstvDecodeOptions { SampleRate = sr, ChannelBwHz = chanBw };
          double[] disc = SstvDecoder.Discriminator(seg, o);

          // subcarrier analytic signal: mix by 1900 Hz, ±900 Hz low-pass (wider than the brightness LPF
          // so the filter's own skirt does not tilt the 1500-2300 Hz band); amplitude = |z|, freq = dφ
          double w0 = 2 * Math.PI * 1900.0 / sr;
          var re = new float[disc.Length];
          var im = new float[disc.Length];
          for (int i = 0; i < disc.Length; i++)
          {
            re[i] = (float)(disc[i] * Math.Cos(w0 * i));
            im[i] = (float)(-disc[i] * Math.Sin(w0 * i));
          }
          float[] lp = global::VE3NEA.Dsp.BlackmanSincKernel(900.0 / sr, 401);
          float[] zr = VE3NEA.LiquidFir.ConvolveSame(re, lp);
          float[] zi = VE3NEA.LiquidFir.ConvolveSame(im, lp);

          // mean amplitude per 100 Hz bin, 1500-2300 Hz, burst interior (1 s edges skipped)
          var sumAmp = new double[8];
          var cnt = new long[8];
          int skip = (int)sr;
          for (int i = Math.Max(1, skip); i < disc.Length - skip; i++)
          {
            double dphi = Math.Atan2(zi[i], zr[i]) - Math.Atan2(zi[i - 1], zr[i - 1]);
            if (dphi > Math.PI) dphi -= 2 * Math.PI;
            if (dphi < -Math.PI) dphi += 2 * Math.PI;
            double freq = 1900.0 + dphi * sr / (2 * Math.PI);
            int bin = (int)Math.Floor((freq - 1500.0) / 100.0);
            if (bin < 0 || bin >= 8) continue;
            sumAmp[bin] += Math.Sqrt((double)zr[i] * zr[i] + (double)zi[i] * zi[i]);
            cnt[bin]++;
          }

          var bins = new string[8];
          for (int b = 0; b < 8; b++)
            bins[b] = cnt[b] > 0 ? $"{sumAmp[b] / cnt[b]:0}" : "-";
          double tilt = cnt[0] > 0 && cnt[7] > 0
            ? 20.0 * Math.Log10((sumAmp[7] / cnt[7]) / (sumAmp[0] / cnt[0])) : double.NaN;
          output.WriteLine($"{tag} chan ±{chanBw:0}: amp/bin [{string.Join(" ", bins)}]  " +
            $"tilt 1550->2250 = {tilt:+0.0;-0.0} dB");
        }
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

      var img = SstvDecoder.Decode(iq[start..end], mode,
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
