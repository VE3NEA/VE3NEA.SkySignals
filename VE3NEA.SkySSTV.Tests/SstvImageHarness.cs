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

    [ManualFact("Result 2026-07-03 late (soft-comb wired): 21 images from ALL 9 captures — the 04-18 " +
      "UmKA-1 transmission detects and decodes for the first time (comb-seeded train @0.1 s; video " +
      "still speckle = the P6(c) below-FM-threshold fidelity gap, but timing locks: the post-24 s " +
      "striping is vertical). Also new: the 12_37_50 @5 s candidate (likely-real, pending user " +
      "confirmation) and the 04-19 @505 post-dropout partial. Previous result 2026-07-02: 18 images " +
      "from 8 of 9, single discriminator pass, weak low-fill decodes are real transmissions.")]
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

    [ManualFact("Result 2026-07-03 late (soft-comb wired in): 20 of 20 matched, 0 false, 0 missed — " +
      "04-18 DETECTS AND DECODES FOR THE FIRST TIME (comb-seeded train 0.1-28 s), and the comb FOUND a " +
      "transmission missing from the truth list (12_37_50 1-38 s: cadence, RF-spectrogram stripes, " +
      "21-check persistence; user-confirmed — too weak for an image but detected). 1 DUP residual = " +
      "the pre-existing 04-19 ~505 post-dropout continuation. Comb guards proven here: family-ring " +
      "reset on train retirement (kills ridge-echo phantoms), the 12-check persistence gate (kills " +
      "noise extremes), tight comb-train regressor priors (periodPpm 200 / phaseMs 1.5 — without them " +
      "a soft noise first pulse wrenched the grid and 11_29's first transmission collapsed to 27 " +
      "pulses).")]
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


    [ManualFact("Result 2026-07-03 late (comb FINISHED and wired): burst first confirmed hit 10.5 s " +
      "z=5.2, 55 confirmed checks, anchor 77.1 ms = the batch phase; control and both noise-fire " +
      "segments clean; the 12_37 0-40 s segment fires z 3.9-4.1 for 21 checks — cadence + RF stripes " +
      "say it is a REAL Monitor-3 transmission (pending user ground-truth confirmation). What closed " +
      "the false fires: (a) touch-count variance normalization (1-lambda^2k)/(1-lambda^2) before " +
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
      // early 11_09 (noise, 3-check run), 11_29 ~450 (noise) and 11_09 ~170-208 (the Robot72 phantom)
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
            ChannelBwHz = chanBw,
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
      "then lock a decode-stage ChannelBwHz (or dev-matched rule) + BlankerThreshold default.")]
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
          foreach (double blank in new[] { 0.0, 0.3, 0.5 })
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
