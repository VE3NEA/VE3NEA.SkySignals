using System;
using System.Collections.Generic;
using System.IO;
using FluentAssertions;
using MathNet.Numerics;
using VE3NEA.SkyTlm.IO;
using Xunit;
using Xunit.Abstractions;

namespace VE3NEA.SkySSTV.Tests
{
  /// <summary>
  /// Phase 0 of the declick plan (design-docs/SSTV/sstv-declick-plan.md §5): calibrate the synthetic CNR
  /// ladder so the closed loop actually reaches the FM threshold, then measure the CEILING of click repair
  /// with <see cref="SstvClickOracle"/> before any blind detector is written.
  ///
  /// <para>Everything runs at fixed timing (<c>Acquire = false, Track = false</c>, no VIS) so the front end
  /// is isolated from acquisition, and on the VIDEO channel (±4 kHz) because the metric is image quality.
  /// The source is the same smooth gradient the existing noise tests use: content-free, so PSNR and the
  /// per-line σ that drives <c>WideWeight</c> both measure noise and nothing else.</para>
  /// </summary>
  public class SstvDeclickProbe
  {
    private const double Fs = 48000.0;

    // the video chain's Stage-1 channel — what the images are judged through, and the bandwidth the
    // requested in-channel CNR is referred to
    private const double VideoBw = 4000.0;

    // the repo's existing amplitude proxy for a click: far outside the ±4 kHz channel, so an excursion this
    // large is unambiguously impulsive. Kept for comparability with the P6(c) tables; the honest statistic
    // is SstvClickOracle.ResidualAreaCycles (plan §4).
    private const double ClickAmplitudeHz = 15000.0;

    // the detection chain's Stage-1 channel, reported alongside the video one: the real-capture click
    // figures are quoted at BOTH (P6(c): "clicks 2.4→1.2 %" is ±6 kHz → ±4 kHz), and a click rate is only
    // comparable at equal bandwidth
    private const double DetectBw = 6000.0;

    // in-channel CNR rungs (dB), bracketing the FM threshold. The bottom sits well BELOW 0 dB because that
    // is where the real captures' click rates live (measured 0b, 2026-07-25): the reported bursts are
    // at/below threshold and fading, so their instantaneous CNR dips under their average.
    private static readonly double[] Ladder = { -10, -8, -6, -4, -2, 0, 2, 4, 6, 8, 10, 12 };

    // the real corpus, for the arms that have to survive contact with it
    private static readonly string RecordingsDir =
      @"C:\Users\alsho\AppData\Roaming\Afreet\Products\SkyRoof\Recordings\SSTV";
    private static readonly string OutDir =
      @"C:\Users\alsho\AppData\Local\Temp\claude\C--Proj-DSP-VE3NEA-SkySignals\2133a453-2405-4e22-aafc-f4a15544f883\scratchpad\probe";

    private readonly ITestOutputHelper output;
    public SstvDeclickProbe(ITestOutputHelper o) => output = o;




    // ----------------------------------------------------------------------------------------------------
    //                                    0b — ladder validation
    // ----------------------------------------------------------------------------------------------------


    /// <summary>
    /// Step 0b: the calibrated ladder must reproduce the real captures' impulsiveness. The old full-band
    /// <c>NoiseStdDev</c> convention never took the loop below the FM threshold (σ ≤ 0.6 ⇒ ≈9 dB in-channel
    /// ⇒ ≤0.05 % clicks), which is why <c>Frontend_BlankerAndChannelSweep</c> recorded a synthetic/real
    /// mismatch. Asserted here: the ladder brackets the click rates the real bursts measure — at each
    /// channel bandwidth separately, since a click rate is only comparable at equal bandwidth (P6(c)
    /// measured the same captures at 2.4 % through ±6 kHz and 1.2 % through ±4 kHz).
    ///
    /// <para>The quoted CNR stays referred to <see cref="VideoBw"/> whichever chain reads it: it is a
    /// property of the transmitted signal, not of the receiver, so the wider detection chain simply admits
    /// more noise — exactly as it does on the real captures.</para>
    /// </summary>
    [Fact]
    public void Ladder_BracketsRealClickRates()
    {
      var spec = SstvModes.Get(SstvMode.Robot36);
      var src = GrayscaleGradient(spec.Width, spec.Height);
      var o = DecodeOptions();
      var video = new List<double>();
      var detect = new List<double>();
      var encirclements = new List<double>();

      foreach (double cnrDb in Ladder)
      {
        var iq = Encode(src, cnrDb);
        double[] disc = SstvDecoder.Discriminator(iq, o with { BlankerThreshold = 0.0 });
        double[] discDet = SstvDecoder.Discriminator(iq,
          o with { BlankerThreshold = 0.0, ChannelBwHz = DetectBw });

        var clicks = Oracle(iq);
        video.Add(100.0 * AmplitudeClickSamples(disc) / disc.Length);
        detect.Add(100.0 * AmplitudeClickSamples(discDet) / discDet.Length);
        encirclements.Add(clicks.Count * Fs / disc.Length);

        output.WriteLine($"cnr={cnrDb,3:0} dB: clicks ±4k={video[^1]:0.00} % ±6k={detect[^1]:0.00} % " +
          $"encirclements={clicks.Count * Fs / disc.Length:0} /s " +
          $"(grouped {SstvClickOracle.Group(clicks).Count * Fs / disc.Length:0} /s) " +
          $"residual area={SstvClickOracle.ResidualAreaCycles(disc, clicks, Fs):0.00} cycle " +
          $"PSNR={Psnr(src, SstvDecoder.Decode(disc, SstvMode.Robot36, o)):0.0} dB");
      }

      detect[0].Should().BeGreaterThan(2.4,
        "the bottom rung must be at least as impulsive as the real bursts read through the same ±6 kHz channel");
      video[0].Should().BeGreaterThan(1.15,
        "…and likewise through the ±4 kHz video channel, where the proxy's noise-only asymptote (≈1.20 %) " +
        "essentially IS the real captures' 1.2 % — those bursts sit at or below the FM threshold");
      video[^1].Should().BeLessThan(0.1,
        "the top rung must be essentially click-free, so the ladder brackets the real band rather than sitting inside it");

      // the statistic that actually resolves the ladder. The amplitude proxy saturates below ≈0 dB — as the
      // carrier vanishes the discriminator output becomes the instantaneous frequency of filtered noise
      // alone, whose distribution depends only on the channel bandwidth, not on CNR — which is a further
      // reason it is the wrong statistic (plan §4). Encirclements keep resolving all the way down.
      for (int i = 1; i < encirclements.Count; i++)
        encirclements[i].Should().BeLessThan(encirclements[i - 1],
          $"the encirclement rate must fall monotonically with CNR (rung {Ladder[i]} dB)");
    }




    // ----------------------------------------------------------------------------------------------------
    //                                     0d — the repair ceiling
    // ----------------------------------------------------------------------------------------------------


    /// <summary>
    /// Step 0d: the ceiling table. Five decodes per rung — today's two chains, then the oracle repairs that
    /// bound what perfect detection could buy: the rectangle correction FmDenoiser used, the matched-shape
    /// correction §3.2 argues for, and the matched one stacked on the production blanker.
    ///
    /// <para>Reported per §4: PSNR, mean <c>WideWeight</c> (the resolution channel — impulse noise inflates
    /// the per-line σ and pushes lines onto the narrow branch), the brightness-domain error against the
    /// noise-free decode (the honest whole-event measure), and the residual in-window click area. The
    /// per-rung header carries the click rate and the near-miss share, which bounds what slip logic can ever
    /// reach — both are properties of the received signal, not of an arm.</para>
    /// </summary>
    [ManualFact("Result 2026-07-25 — the CEILING, and it is large. Oracle repair over today's blanker-on "
      + "chain: +8.1 dB PSNR at −10 dB CNR, +8.8 at −4, +7.3 at 0, +4.7 at +2, then +0.8..+2.6 above that; "
      + "brightness error against the noise-free decode halves (226→118 luma at −10 dB, 156→88 at 0). The "
      + "RESOLUTION channel moves too, which was the thesis: mean WideWeight 0.00→0.46 at +4 dB CNR and "
      + "0.33→0.79 at +6, i.e. declicking hands lines back to the wide branch. Phase 0e gate (>1 dB PSNR, "
      + ">0.1 WideWeight) is cleared by a wide margin — CONTINUE.\n"
      + "MATCHED SHAPE DOES NOT PAY HERE: oracle-matched vs oracle-rect is +0.1..+0.2 dB PSNR and slightly "
      + "WORSE on brightness error (118 vs 115 at −10 dB) — the plan's §3.2 item 4 prediction confirmed, so "
      + "Phase 3b keeps the rectangle and the SM5BSZ template stays a Phase-2 (subcarrier-stage) lever.\n"
      + "BLANKER CROSSOVER ≈5 dB in-channel CNR: below it the blanker helps (brightness error 175→156 at 0 "
      + "dB, 138→115 at +2), above it it HURTS (60→64 at +6, 36→46 at +8, 22→28 at +10, and −0.7 dB PSNR at "
      + "+12 with zero clicks present). FmDenoiser's 'blanker harmful above ~7 dB' transfers — Phase 1b has "
      + "its number.\n"
      + "NEAR-MISS SHARE 0.51–0.76 in the click-dense region (vs FmDenoiser's 0.43 on FM speech): over half "
      + "the loud impulses carry no encirclement, so slip logic alone is capped — yet the encircling half "
      + "carries most of the damage, which is why the ceiling is +8 dB anyway.\n"
      + "CAVEATS: the source is a smooth gradient, so PSNR rewards smoothing — read it alongside WideWeight "
      + "(the blanker scores +3 dB PSNR at +8 dB CNR while making the pre-integration brightness error "
      + "worse, purely by pushing lines onto the narrow branch). And the oracle knows the click times "
      + "exactly; a blind detector gets a fraction of this.")]
    public void ClickRepairCeiling()
    {
      var spec = SstvModes.Get(SstvMode.Robot36);
      var src = GrayscaleGradient(spec.Width, spec.Height);
      var o = DecodeOptions();
      var template = SstvClickOracle.ClickTemplate(VideoBw, Fs);
      double[] cleanDisc = SstvDecoder.Discriminator(CleanIq, o);

      output.WriteLine($"template {template.Length} taps");
      output.WriteLine("arm             PSNR   wide  brerr  resid");
      foreach (double cnrDb in Ladder)
      {
        var iq = Encode(src, cnrDb);
        double[] raw = SstvDecoder.Discriminator(iq, o with { BlankerThreshold = 0.0 });
        double[] blanked = SstvDecoder.Discriminator(iq, o);
        var clicks = Oracle(iq);
        double[] matched = SstvClickOracle.RepairMatched(raw, clicks, template);

        output.WriteLine($"--- cnr {cnrDb:0} dB: {clicks.Count * Fs / raw.Length:0} clicks/s, " +
          $"near-miss {NearMissShare(raw, clicks):0.00}");
        Report("raw", raw, clicks, src, o, cleanDisc);
        Report("blanker", blanked, clicks, src, o, cleanDisc);
        Report("oracle-rect", SstvClickOracle.RepairSteps(raw, clicks, Fs), clicks, src, o, cleanDisc);
        Report("oracle-matched", matched, clicks, src, o, cleanDisc);
        Report("matched+blanker", SstvClickOracle.ApplyBlankerMask(matched, raw, blanked), clicks, src, o,
          cleanDisc);
      }
    }

    private void Report(string arm, double[] disc, IReadOnlyList<SstvClickOracle.OracleClick> clicks,
      RgbImage src, SstvDecodeOptions o, double[] cleanDisc)
    {
      var img = SstvDecoder.Decode(disc, SstvMode.Robot36, o);
      output.WriteLine($"{arm,-15} {Psnr(src, img),5:0.0}  {MeanWideWeight(disc, o),5:0.00}  " +
        $"{BrightnessErrorLuma(disc, cleanDisc, o),5:0.0}  " +
        $"{SstvClickOracle.ResidualAreaCycles(disc, clicks, Fs),5:0.00}");
    }




    // ----------------------------------------------------------------------------------------------------
    //                                     1a — the amplitude gate
    // ----------------------------------------------------------------------------------------------------


    /// <summary>
    /// Step 1a: the amplitude gate against the envelope gate it is meant to replace, on the same calibrated
    /// ladder, with the 0d rectangle oracle carried along as the budget each arm is spending against.
    ///
    /// <para>The hypothesis is FmDenoiser's: the envelope is a proxy, and at the CNRs that matter it is a
    /// poor one — it dips on a pulse's flanks and not at its peak, so the envelope gate interpolates the
    /// shoulders and leaves the spike. Gating on the discriminator output itself removes the quantity that
    /// actually damages the picture. Judged per §4 on the brightness error first (the metric the smooth
    /// gradient cannot game) and on mean <c>WideWeight</c> second, with PSNR read last.</para>
    /// </summary>
    [ManualFact("Result 2026-07-25 — NULL, and structurally so: the amplitude gate is DOMINATED at every "
      + "rung. Brightness error, raw / envelope / amplitude(41 taps): −10 dB 227/226/229, −4 214/208/214, "
      + "0 175/156/168, +2 138/114/132, +4 98/82/93, then above the crossover +6 60/64/62, +8 36/45/40, "
      + "+10 22/28/24, +12 15.1/16.7/15.6. So the envelope gate wins everywhere below ≈+5 dB and 'no gate at "
      + "all' wins everywhere above it — the amplitude gate is second in both regimes and first in neither. "
      + "Same ordering on the resolution channel (+6 dB mean WideWeight raw 0.51 / env 0.33 / amp 0.47).\n"
      + "WHY, and it is not a tuning failure: for SSTV the median-detrended discriminator output is dominated "
      + "by the MODULATION, not by the noise. The subcarrier steps every ~13 samples at 3636 px/s, so no "
      + "window both follows the modulation and outlasts a pulse, and a 4×rms threshold ends up set by "
      + "pixel-step residual. Sweeping the window only trades sensitivity against false alarms — samples "
      + "gated at +12 dB CNR with ZERO clicks present: 4.19 % at 9 taps, 0.63 % at 21 (the FM-speech value), "
      + "0.06 % at 41 — and no length beats the envelope gate below the crossover or 'off' above it. 41 is "
      + "kept because it is the one that is nearly a no-op on a clean signal.\n"
      + "APPETITE is the one real difference: at −10 dB the envelope gate alters 23.4 % of samples and the "
      + "amplitude gate 7.6 %, against the oracle's 29.1 % — and at +6 dB, 11.0 % vs 4.3 % against the "
      + "oracle's 1.3 %. Both gates are wildly over-eager where it hurts; the amplitude gate is merely less "
      + "so, which is exactly why it is the milder loss above the crossover.\n"
      + "Neither gate is close to the 0d ceiling: at 0 dB CNR the 91-luma budget (175→84) buys 20 luma with "
      + "the envelope gate and 7 with the amplitude gate. Phase 2 remains the lever.")]
    public void AmplitudeGateLadder()
    {
      var spec = SstvModes.Get(SstvMode.Robot36);
      var src = GrayscaleGradient(spec.Width, spec.Height);
      var o = DecodeOptions();
      double[] cleanDisc = SstvDecoder.Discriminator(CleanIq, o);

      output.WriteLine("arm             PSNR   wide  brerr  resid  gated%");
      foreach (double cnrDb in Ladder)
      {
        var iq = Encode(src, cnrDb);
        double[] raw = SstvDecoder.Discriminator(iq, o with { BlankerThreshold = 0.0 });
        var clicks = Oracle(iq);

        output.WriteLine($"--- cnr {cnrDb:0} dB: {clicks.Count * Fs / raw.Length:0} clicks/s");
        ReportGate("raw", raw, raw, clicks, src, o, cleanDisc);
        ReportGate("envelope", SstvDecoder.Discriminator(iq, o), raw, clicks, src, o, cleanDisc);
        ReportGate("amplitude",
          SstvDecoder.Discriminator(iq, o with { BlankerGate = BlankerGateMode.Amplitude }),
          raw, clicks, src, o, cleanDisc);
        ReportGate("oracle-rect", SstvClickOracle.RepairSteps(raw, clicks, Fs), raw, clicks, src, o, cleanDisc);
      }
    }

    /// <summary>
    /// Step 1a on the corpus. No ground truth here, so the ladder's two best metrics are unavailable and the
    /// judgment runs on what the real captures can supply: the resolution channel (mean <c>WideWeight</c>),
    /// the acquisition channel (sync <c>maxScore</c>), the reference-free row-noise proxy, the gate's
    /// appetite — and the PNGs, which are the verdict. Cases and the train-location protocol are
    /// <c>SstvImageHarness.Real_P6cDecodeGridProbe</c>'s, so the numbers line up with the P6(c) table that
    /// locked the envelope default.
    /// </summary>
    [ManualFact("Result 2026-07-25 — the corpus agrees with the ladder; the envelope default stands. "
      + "rowNoise raw / envelope / amplitude: utmn2236 20.1/17.0/18.6, m3_1102 18.5/17.1/18.6, umka0418 "
      + "15.8/15.7/16.6, m3_1237 23.7/23.2/23.6, m3_1102b 24.6/20.0/22.8 — the envelope gate is quietest on "
      + "all five, the amplitude gate is between raw and envelope (and slightly WORSE than raw on m3_1102 and "
      + "umka0418). Visually confirmed on m3_1102b: the amplitude arm carries clear residual speckle across "
      + "the sky and the caption block where the envelope arm is clean.\n"
      + "THE DECIDING CHANNEL IS ACQUISITION: on the below-threshold 04-18 capture the envelope gate's known "
      + "P6(c) win holds (maxScore 0.286→0.324) and the amplitude gate LOSES it — 0.281, below raw. That win "
      + "is discrete (acquire / don't acquire), so it alone rules the amplitude gate out as a replacement.\n"
      + "Its only edge is the resolution channel on the strong bursts (mean WideWeight m3_1102b raw 0.54 / "
      + "amp 0.53 / env 0.49, utmn2236 0.50/0.50/0.49) — i.e. it is less destructive than the envelope gate "
      + "where the envelope gate should not be running at all. Bypassing the stage beats it there, which is "
      + "Phase 1b's job, not a reason for a second gate. Appetite raw/env/amp: 0 / 11–24 % / 2.6–7.1 %.")]
    public void AmplitudeGateCorpus()
    {
      (string tag, string file, double t0, double t1)[] cases =
      {
        ("utmn2236", "2026-06-30_22_36_37_UTMN2_Robot36", 183.0, 218.0),
        ("m3_1102",  "2026-07-01_11_02_25_Monitor-3",     140.0, 167.0),
        ("umka0418", "2026-04-18_12_36_09_UmKA-1",          0.0,  24.0),
        ("m3_1237",  "2026-07-01_12_37_50_Monitor-3",       1.0,  38.0),
        ("m3_1102b", "2026-07-01_11_02_25_Monitor-3",     285.0, 325.0),
      };

      Directory.CreateDirectory(OutDir);
      foreach (var (tag, file, t0, t1) in cases)
      {
        string wav = Path.Combine(RecordingsDir, file + ".iq.wav");
        if (!File.Exists(wav)) { output.WriteLine($"{tag}: capture absent"); continue; }
        var (iq, sr) = WavIqReader.Read(wav);
        var seg = iq[(int)(Math.Max(0, t0 - 1) * sr)..Math.Min(iq.Length, (int)((t1 + 1) * sr))];

        // locate the train once, at the detection defaults, so every arm decodes the same slice
        var oDet = new SstvDecodeOptions { SampleRate = sr };
        double[] discDet = SstvDecoder.Discriminator(seg, oDet);
        var extractor = SstvDecoder.ExtractTrains(SstvDecoder.SyncAudio(discDet, sr, oDet), sr,
          SstvVisDetector.DetectAll(SstvDecoder.SyncAudio(discDet, sr, oDet), sr));
        SstvPulseTrain? best = null;
        foreach (var train in extractor.Trains)
          if (extractor.IsImageTrain(train) && (best == null || train.PulseCnt > best.PulseCnt)) best = train;
        if (best == null) { output.WriteLine($"{tag}: no image train at detection defaults"); continue; }

        int firstSync = (int)Math.Round(best.Regr.GetPulseTime(0));
        var spec = SstvModes.Get(best.Format);
        var baseOpts = new SstvDecodeOptions
        {
          SampleRate = sr,
          ChannelBwHz = new SstvDecodeOptions().VideoChannelBwHz,
          Acquire = false,
          StartSample = firstSync
        };
        double[] rawDisc = SstvDecoder.Discriminator(seg, baseOpts with { BlankerThreshold = 0.0 });
        output.WriteLine($"--- {tag}: {best.Format} train @{firstSync / (double)sr:0.0}s p={best.PulseCnt}");

        foreach (var (arm, o) in new[]
        {
          ("raw", baseOpts with { BlankerThreshold = 0.0 }),
          ("envelope", baseOpts),
          ("amplitude", baseOpts with { BlankerGate = BlankerGateMode.Amplitude })
        })
        {
          double[] disc = SstvDecoder.Discriminator(seg, o);
          var det = new SstvPulseDetector(sr, spec.SyncMs);
          det.Detect(SstvDecoder.SyncAudio(disc, sr, o));

          var img = SstvDecoder.Decode(disc, best.Format, o);
          string path = Path.Combine(OutDir, $"gate_{tag}_{arm}.png");
          img.SavePng(path);
          output.WriteLine($"  {arm,-9} maxScore={det.MaxScore:0.000} rowNoise={RowNoise(img):0.0} " +
            $"wide={MeanWideWeight(disc, o, sr, best.Format),4:0.00} " +
            $"gated={100.0 * AlteredShare(disc, rawDisc):0.00}% -> {Path.GetFileName(path)}");
        }
      }
    }

    /// <summary>Mean absolute luma difference between vertically adjacent pixels — a reference-free
    /// speckle proxy (image content correlates line-to-line; noise does not). Over-smoothing lowers it too,
    /// so read it beside the PNGs. Same statistic as <c>SstvImageHarness.RowNoise</c>.</summary>
    private static double RowNoise(RgbImage img)
    {
      double sum = 0;
      int n = 0;
      for (int y = 1; y < img.Height; y++)
        for (int x = 0; x < img.Width; x++)
        {
          sum += Math.Abs(img.R[y * img.Width + x] - img.R[(y - 1) * img.Width + x]);
          n++;
        }
      return n == 0 ? 0 : sum / n;
    }

    /// <summary>As <see cref="Report"/>, plus the share of samples the arm actually altered — the gate's
    /// appetite, which is the cost side of every arm here (interpolation over a sample the gate was wrong
    /// about is itself an error).</summary>
    private void ReportGate(string arm, double[] disc, double[] raw,
      IReadOnlyList<SstvClickOracle.OracleClick> clicks, RgbImage src, SstvDecodeOptions o, double[] cleanDisc)
    {
      var img = SstvDecoder.Decode(disc, SstvMode.Robot36, o);
      output.WriteLine($"{arm,-15} {Psnr(src, img),5:0.0}  {MeanWideWeight(disc, o),5:0.00}  " +
        $"{BrightnessErrorLuma(disc, cleanDisc, o),5:0.0}  " +
        $"{SstvClickOracle.ResidualAreaCycles(disc, clicks, Fs),5:0.00}  " +
        $"{100.0 * AlteredShare(disc, raw),5:0.00}");
    }

    /// <summary>Share of samples an arm changed relative to the ungated chain.</summary>
    private static double AlteredShare(double[] disc, double[] raw)
    {
      int n = Math.Min(disc.Length, raw.Length);
      if (n == 0) return 0;
      int changed = 0;
      for (int i = 0; i < n; i++) if (disc[i] != raw[i]) changed++;
      return (double)changed / n;
    }




    // ----------------------------------------------------------------------------------------------------
    //                                          measurements
    // ----------------------------------------------------------------------------------------------------


    /// <summary>Mean of the §6.3 wide-branch weight over the image's lines — the resolution channel. The
    /// production statistic itself (<see cref="SstvDecoder.WideWeight"/>), on the fixed-timing line grid.</summary>
    private static double MeanWideWeight(double[] disc, SstvDecodeOptions o)
      => MeanWideWeight(disc, o, Fs, SstvMode.Robot36);

    private static double MeanWideWeight(double[] disc, SstvDecodeOptions o, double fs, SstvMode mode)
    {
      var spec = SstvModes.Get(mode);
      double[] narrow = SstvDecoder.Brightness(disc, fs, o, out double[]? wide);
      var bw = new BrightnessWindow(narrow, wide, 0, narrow.Length);
      double period = spec.LinePeriodMs / 1000.0 * fs;

      double sum = 0;
      int count = 0;
      for (int line = 1; line < spec.LineCount; line++)
      {
        sum += SstvDecoder.WideWeight(bw, spec, o, 1.0, line * period, (line - 1) * period);
        count++;
      }
      return count == 0 ? 0 : sum / count;
    }

    /// <summary>
    /// RMS error of the narrow brightness branch against the noise-free decode, in 0..255 luma units — the
    /// whole-event measure the ±3-sample area statistic cannot be: it is taken after the ±600 Hz low-pass,
    /// so it counts exactly what reaches the picture, tails included, and it needs no baseline estimate
    /// because simulation owns the noise-free reference.
    /// </summary>
    private static double BrightnessErrorLuma(double[] disc, double[] cleanDisc, SstvDecodeOptions o)
    {
      double[] a = SstvDecoder.Brightness(disc, Fs, o);
      double[] b = SstvDecoder.Brightness(cleanDisc, Fs, o);

      int n = Math.Min(a.Length, b.Length);
      double sum = 0;
      for (int i = 0; i < n; i++) sum += (a[i] - b[i]) * (a[i] - b[i]);
      return n == 0 ? 0 : Math.Sqrt(sum / n) * 255.0 / SstvTones.Span;
    }

    /// <summary>Samples the repo's amplitude proxy calls clicks.</summary>
    private static int AmplitudeClickSamples(double[] disc)
    {
      int n = 0;
      for (int i = 0; i < disc.Length; i++) if (Math.Abs(disc[i]) > ClickAmplitudeHz) n++;
      return n;
    }

    /// <summary>
    /// Share of the loud impulse events that carry NO origin encirclement — the phasor passing close to the
    /// origin without going around it. These are invisible to slip repair by construction, so this bounds
    /// what any 2π-step lever can reach (FmDenoiser measured 43 % on FM speech at 7 dB CNR).
    /// </summary>
    private static double NearMissShare(double[] disc, IReadOnlyList<SstvClickOracle.OracleClick> clicks)
    {
      var hasClick = new bool[disc.Length];
      foreach (var click in clicks)
        for (int k = Math.Max(0, click.Index - SstvClickOracle.RepairHalfWidth);
             k <= Math.Min(disc.Length - 1, click.Index + SstvClickOracle.RepairHalfWidth); k++)
          hasClick[k] = true;

      int events = 0, misses = 0;
      int i = 0;
      while (i < disc.Length)
      {
        if (Math.Abs(disc[i]) <= ClickAmplitudeHz) { i++; continue; }

        int end = i;
        bool matched = false;
        while (end < disc.Length && Math.Abs(disc[end]) > ClickAmplitudeHz)
        {
          matched |= hasClick[end];
          end++;
        }
        events++;
        if (!matched) misses++;
        i = end;
      }
      return events == 0 ? 0 : (double)misses / events;
    }




    // ----------------------------------------------------------------------------------------------------
    //                                            fixtures
    // ----------------------------------------------------------------------------------------------------


    /// <summary>The fixed-timing video chain: no acquisition, no tracking, image at sample 0.</summary>
    private static SstvDecodeOptions DecodeOptions() => new()
    {
      SampleRate = Fs,
      Acquire = false,
      Track = false,
      ChannelBwHz = VideoBw
    };

    private static Complex32[] Encode(RgbImage src, double cnrDb) =>
      SstvEncoder.Encode(src, SstvMode.Robot36, new SstvEncoderOptions
      {
        SampleRate = Fs,
        IncludeVis = false,
        ChannelCnrDb = cnrDb,
        CnrRefBwHz = VideoBw,
        NoiseSeed = 7
      });

    /// <summary>The true encirclements of the channel-filtered phasor, index-aligned with the
    /// discriminator output. The clean reference is the same signal without noise — the encoder is
    /// deterministic and the noise is added last, so the noiseless encode IS the transmitted phasor; it is
    /// filtered once and cached, since every rung refers to the same one.</summary>
    private static List<SstvClickOracle.OracleClick> Oracle(Complex32[] noisy)
      => SstvClickOracle.Detect(CleanChannel, SstvClickOracle.ChannelFilter(noisy, VideoBw, Fs));

    private static Complex32[]? cleanIq;
    private static Complex32[]? cleanChannel;

    /// <summary>The transmitted phasor: the same encode without noise. Every rung refers to this one.</summary>
    private static Complex32[] CleanIq
    {
      get
      {
        if (cleanIq == null)
        {
          var spec = SstvModes.Get(SstvMode.Robot36);
          cleanIq = SstvEncoder.Encode(GrayscaleGradient(spec.Width, spec.Height), SstvMode.Robot36,
            new SstvEncoderOptions { SampleRate = Fs, IncludeVis = false });
        }
        return cleanIq;
      }
    }

    private static Complex32[] CleanChannel
      => cleanChannel ??= SstvClickOracle.ChannelFilter(CleanIq, VideoBw, Fs);

    private static RgbImage GrayscaleGradient(int w, int h)
    {
      var img = new RgbImage(w, h);
      for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
          byte v = (byte)((x * 255 / (w - 1) + y * 255 / (h - 1)) / 2);
          img.Set(x, y, v, v, v);
        }
      return img;
    }

    private static double Psnr(RgbImage a, RgbImage b)
    {
      double se = 0; long n = (long)a.Width * a.Height * 3;
      for (int i = 0; i < a.R.Length; i++)
        se += Sq(a.R[i] - b.R[i]) + Sq(a.G[i] - b.G[i]) + Sq(a.B[i] - b.B[i]);
      double mse = se / n;
      return mse <= 1e-9 ? 100.0 : 10.0 * Math.Log10(255.0 * 255.0 / mse);
    }

    private static double Sq(int d) => (double)d * d;
  }
}
