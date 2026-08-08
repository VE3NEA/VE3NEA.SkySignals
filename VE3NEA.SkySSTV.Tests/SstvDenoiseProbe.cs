using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using VE3NEA.SkyTlm.IO;
using Xunit;
using Xunit.Abstractions;

namespace VE3NEA.SkySSTV.Tests
{
  /// <summary>
  /// The denoise work-off (plan §9, sequencing step 2): the non-local means filter has been ported and
  /// shown non-degenerate on synthetic noise, but every one of its constants is deliberately unsettled
  /// — the reference implementation's own values were tuned against a different noise estimator and a
  /// different measuring stick (D20), so they are a starting point and not an answer.
  ///
  /// <para>Each <c>[ManualFact]</c> here is one of the §9 experiments. They share a decode: the corpus
  /// capture is demodulated once, every image burst in it reconstructed once to RAW planes (no
  /// decode-time filter at all), and the arms then run as pure post-filters over those planes. That is
  /// both far cheaper than the Wiener probe's decode-per-variant and strictly more correct — every arm
  /// sees byte-identical input, so a difference in the sheet is the filter and nothing else.</para>
  ///
  /// <para><b>The numbers do not decide this; the pictures do.</b> The tabulated statistics are there
  /// to catch the two failures the eye cannot see: a mis-scaled noise map that has quietly turned NLM
  /// into a 21×21 box average (<c>flat%</c> near 100), and one that has rejected every donor and made
  /// the run an expensive no-op (<c>rej%</c> near 100) — plan §5.6. PSNR is absent on purpose, since it
  /// rewards exactly the over-smoothing being complained about.</para>
  /// </summary>
  public class SstvDenoiseProbe
  {
    private static readonly string RecordingsRoot =
      @"C:\Users\alsho\AppData\Roaming\Afreet\Products\SkyRoof\Recordings";
    private static readonly string OutDir =
      @"C:\Users\alsho\AppData\Local\Temp\claude\c--Proj-Try-FskDemod\dda525aa-d102-4df7-b574-b9d66ce04a02\scratchpad\denoise";

    private static readonly string RxSstvDir = @"C:\Ham\RX-SSTV-2\History";
    private static readonly string MmSstvDir = @"C:\Ham\MMSSTV\History";

    /// <summary>The §10 probe subset — eight bursts across five captures, chosen to span the regimes
    /// rather than to sample the corpus evenly. The σ figures are the per-burst medians the adaptive
    /// brightness ramp was calibrated on, so this list is also the documented σ ladder: 34, 62, 78, 208,
    /// 224.</summary>
    private static readonly (string Path, string Note)[] Corpus =
    {
      (@"Auto\2026-08-06_21_43_03_VIZARD-meteo.iq.wav",
        "three-way: ours + RX-SSTV + MMSSTV on the same 805 s — where the +0.489 texture target is re-established"),
      (@"SSTV\2026-07-23_21_42_02_UmKA-1.iq.wav",   "caption legibility, sigma 34 @286s and 78 @566s"),
      (@"SSTV\2026-07-01_11_02_25_Monitor-3.iq.wav", "text card, sigma 62 @285s; sigma 208 @135s"),
      (@"SSTV\2026-04-18_12_36_09_UmKA-1.iq.wav",   "sigma 224, below FM threshold — the stress case"),
      (@"Auto\2026-08-04_22_50_43_VIZARD-meteo.iq.wav", "MMSSTV-paired (Hist17..20), texture metrics"),
      (@"Auto\2026-08-07_00_50_55_VIZARD-meteo.iq.wav", "MMSSTV-paired (Hist23), texture metrics")
    };

    /// <summary>The strength the OTHER experiments hold fixed while they vary their own axis. It is the
    /// reference implementation's shipped value and is a placeholder, not a verdict — §9.3 is what
    /// settles it, and every other sheet should be re-read once it has.</summary>
    private const double ProbeSig = 0.4;

    private sealed record Arm(string Tag, SstvDenoiseOptions Options);

    private readonly ITestOutputHelper output;
    public SstvDenoiseProbe(ITestOutputHelper o) => output = o;


    // ----------------------------------------------------------------------------------------------------
    //                                        the §9 experiments
    // ----------------------------------------------------------------------------------------------------


    [ManualFact("Result 2026-08-07 (11 bursts, 6 captures). THE WORKING WINDOW IS 0.3..0.5 AND ITS EDGES "
      + "ARE ABRUPT. At Sig <= 0.2 the cutoff rejects 78-100 % of donors and the output is the raw image "
      + "to three decimals — an expensive no-op. At Sig >= 0.8 the flat top holds 50-100 % of donors and "
      + "the filter IS the 21x21 box average of §5.6: dx1 +0.98..+0.99, hf 0.1-0.2 %, which is WORSE than "
      + "the Wiener it replaces. Between them: 0.3 is the first arm that does anything (flat ~1 %, rej "
      + "42-53 %), 0.4 sits mid-window (flat 5-23 %, rej 20-46 %), 0.5 is already half-degenerate on the "
      + "weak bursts (flat 41 % at 04-18). SECOND FINDING, AND THE MORE IMPORTANT ONE: the window MOVES "
      + "WITH SNR — at Sig 0.5 the strong caption burst reads flat 21 % while the below-threshold 04-18 "
      + "burst reads 41 %, so no single Sig is right for the corpus. That is independent evidence for D14 "
      + "(image-dependent, manual only) and says the dialog's strength slider is the feature, not a "
      + "convenience. The reference's 0.4 does land in the window, but nearer its smoothing edge on weak "
      + "captures. Runtime 0.7-1.4 s per Robot36 image, serial. THE PICTURES STILL DECIDE between 0.3 and "
      + "0.5 — every arm in that range is non-degenerate, and no statistic here can rank them.")]
    public void Strength()
    {
      // log-spaced to find the working window, then linear through it: the first run showed the window
      // is narrow and its edges abrupt, so the decade ladder alone steps straight over the answer
      var arms = new[] { 0.05, 0.1, 0.2, 0.3, 0.4, 0.5, 0.6, 0.8, 1.6, 3.2 }
        .Select(s => new Arm($"sig{s:0.00}".Replace(".", ""), Nlm() with { NlmSig = s }))
        .ToArray();
      Run("strength", arms);
    }

    [ManualFact("Result 2026-08-07, run TWICE — and the second run reverses the first. At a fixed Sig the "
      + "sheet said the detector only rescales: arm B was monotone in k and B(k=3) at Sig 0.4 landed on "
      + "top of the control at Sig 0.8, while arm C was an outright no-op (rej 99.9 %, output = raw, "
      + "because g is 0 over most pixels so s collapses to 0.05*sigma^2). But that comparison was invalid "
      + "— each law rescales the noise map, so at one Sig the arms sit at different points on the "
      + "smoothing curve and the sheet compares STRENGTH, not law. RE-CENTRED to the control's flat-top "
      + "share (the probe now bisects Sig per arm), the answer inverts: (1) ARM B IS GENTLER THAN THE "
      + "CONTROL AT EQUAL SMOOTHING — dx1 +0.877 vs +0.905 and hf 5.4 % vs 3.7 % on VIZARD 21:43, the same "
      + "way round on every burst — so the detector does concentrate the smoothing rather than merely add "
      + "it, which is what D3 hoped for. It saturates at k=3; k=6 is within 0.002 dx1 of it, at a lower "
      + "Sig. (2) ARM D IS ARM B to within 0.004 dx1 everywhere: the distance shaping carries the effect "
      + "and shaping the inverse-variance weighting as well adds nothing. (3) ARM C IS OUT — matched on "
      + "flat% it rejects almost nothing (rej 2.5 % against 36 %) and flattens the picture (dx1 +0.984, "
      + "hf 0.3 %), which is the thin-stroke failure the plan predicted for it. Caveat on the method: C "
      + "matching flat% while missing rej% by an order of magnitude shows flat-top share alone does not "
      + "pin an operating point. Prefer B(k=3), pending the sheets.")]
    public void MappingLaw()
    {
      var arms = new[]
      {
        new Arm("A_rowonly",   Nlm() with { NlmNoiseMap = SstvNlmNoiseMap.RowOnly }),
        new Arm("B_inflate1",  Nlm() with { NlmNoiseMap = SstvNlmNoiseMap.GainInflate, NlmGainK = 1.0 }),
        new Arm("B_inflate3",  Nlm() with { NlmNoiseMap = SstvNlmNoiseMap.GainInflate, NlmGainK = 3.0 }),
        new Arm("B_inflate6",  Nlm() with { NlmNoiseMap = SstvNlmNoiseMap.GainInflate, NlmGainK = 6.0 }),
        new Arm("C_deflate",   Nlm() with { NlmNoiseMap = SstvNlmNoiseMap.GainDeflate }),
        new Arm("D_distonly",  Nlm() with { NlmNoiseMap = SstvNlmNoiseMap.DistanceOnly, NlmGainK = 3.0 })
      };
      Run("mapping", arms, matchOperatingPoint: true);
    }

    [ManualFact("Result 2026-08-07. THE §5.2 LANDMINE IS REAL AND MEASURABLE. Leaving the duplicated "
      + "chroma rows in place puts 2.4-2.7x more donors in the weight kernel's flat top (13.8 % vs 5.5 % "
      + "on VIZARD 21:43, 18.9 % vs 10.3 % on Monitor-3 146 s) — which is exactly the predicted mechanism, "
      + "a zero-distance twin one row away drawing FULL weight while carrying the identical noise sample. "
      + "The luma columns are of course identical between the arms; cdx1 is what separates them and moves "
      + "only 0.01-0.02, so the cost of the mistake is small in bulk statistics and shows up as false "
      + "confidence rather than as visible blur. Native is also 1.6-1.7x FASTER (1.0-1.3 s vs 1.8-2.2 s), "
      + "because half the chroma rows stop being filtered twice. Keep native on; the arm stays only as "
      + "the control. Caveat unchanged: all 11 bursts are Robot36, so the PD chroma path is still "
      + "field-untested and the numbers above say nothing about it.")]
    public void Chroma()
    {
      var arms = new[]
      {
        new Arm("native", Nlm() with { NlmNativeChroma = true }),
        new Arm("dupes",  Nlm() with { NlmNativeChroma = false })
      };
      Run("chroma", arms);
    }

    [ManualFact("Result 2026-08-07. THE PASS IS NOT VACUOUS, AND IT IS NOT CHEAP. The mask selects "
      + "6500-7550 pixels per burst against 153,600 plane-pixels (76,800 luma + 2x38,400 native chroma), "
      + "i.e. 4.2-4.9 % — two orders of magnitude above the 0.2 % the reference's percentile suggests, "
      + "because BuildMask thresholds a 3x3 SUM against the single-pixel distribution. So the §5.5 escape "
      + "hatch ('if the blanker already did the job, drop it') does NOT trigger. Cost is 2.1-2.3x as "
      + "predicted (1.0-1.3 s -> 2.4-2.8 s). The mask count is identical across the n1.5/n3/n6 arms, as "
      + "it must be — the mask comes from pass-1 output, which does not depend on the second-pass noise. "
      + "Bulk effect is small (dx1 +0.905 -> +0.917/+0.919/+0.917 on VIZARD 21:43, and n3 vs n6 is under "
      + "0.003 everywhere), so the decision is entirely whether the impulses it removes are VISIBLE — "
      + "which is what the sheets are for. If they are not, this is 2x runtime for nothing.")]
    public void TwoPass()
    {
      var arms = new[]
      {
        new Arm("off",     Nlm() with { NlmTwoPass = false }),
        new Arm("on_n1.5", Nlm() with { NlmTwoPass = true, NlmSecondPassNoise = 1.5 }),
        new Arm("on_n3",   Nlm() with { NlmTwoPass = true, NlmSecondPassNoise = 3.0 }),
        new Arm("on_n6",   Nlm() with { NlmTwoPass = true, NlmSecondPassNoise = 6.0 })
      };
      Run("twopass", arms);
    }

    [ManualFact("Result 2026-08-07. THE TWO FILTERS SEPARATE EXACTLY WHERE THE PLAN SAID THEY WOULD — on "
      + "the burst with fine detail to lose. On the 07-23 caption burst (sigma 34) NLM at Sig 0.4 keeps "
      + "MORE texture than any Wiener arm: dx1 +0.815 / hf 10.2 % against fl40's +0.850 / 8.0 % and "
      + "fl00's +0.854 / 7.7 %, from a raw +0.789 / 12.7 %. On the noise-dominated VIZARD 21:43 burst it "
      + "matches Wiener fl40 in dx1 (+0.905 vs +0.904) while smearing far less VERTICALLY (dy1 +0.585 vs "
      + "+0.705) — expected, since the Wiener's window is the only stage in the chain that smooths across "
      + "scan lines at all. Confirming the 2026-08-04 finding independently: the floor is the Wiener's "
      + "only effective lever, fl00 -> fl40 moving dx1 +0.937 -> +0.904 and hf 3.2 -> 5.2 %. COST: Wiener "
      + "18-30 ms, NLM 1.1-1.4 s, about 50x — which is the whole argument for D14/D15 in one number. Not "
      + "settled here: whether NLM's texture advantage is VISIBLE on the caption, which is the nominated "
      + "acceptance case (§9 tier 3).")]
    public void WienerFloor()
    {
      var arms = new[]
      {
        new Arm("wiener_fl00", new SstvDenoiseOptions { Method = SstvDenoiseMethod.Wiener, WienerGainFloor = 0.0 }),
        new Arm("wiener_fl25", new SstvDenoiseOptions { Method = SstvDenoiseMethod.Wiener, WienerGainFloor = 0.25 }),
        new Arm("wiener_fl40", new SstvDenoiseOptions { Method = SstvDenoiseMethod.Wiener, WienerGainFloor = 0.40 }),
        new Arm("nlm",         Nlm())
      };
      Run("floor", arms);
    }


    // ----------------------------------------------------------------------------------------------------
    //                                            the runner
    // ----------------------------------------------------------------------------------------------------


    /// <summary>The NLM baseline every experiment varies one axis of. Two-pass is OFF here so the other
    /// sheets measure one thing at a time; §9.4 is where it is switched back on.</summary>
    private static SstvDenoiseOptions Nlm() => new()
    {
      Method = SstvDenoiseMethod.Nlm,
      NlmSig = ProbeSig,
      NlmNoiseMap = SstvNlmNoiseMap.RowOnly,
      NlmTwoPass = false,
      NlmNativeChroma = true
    };

    /// <summary><paramref name="matchOperatingPoint"/> re-centres every arm to the FIRST arm's flat-top
    /// share before rendering it, by searching its <c>Sig</c>. Without that the §9.1 sheet cannot be
    /// read at all: each mapping law rescales the noise map, so at a fixed Sig the arms sit at wildly
    /// different points on the smoothing curve and the sheet compares strength rather than law — which
    /// is exactly how the 2026-08-07 run found arm B "confounded with the strength axis" and arm C an
    /// outright no-op. Matched, the question becomes the one worth asking: given the same amount of
    /// smoothing, does the detector put it in better places?</summary>
    private void Run(string experiment, Arm[] arms, bool matchOperatingPoint = false)
    {
      string dir = Path.Combine(OutDir, experiment);
      Directory.CreateDirectory(dir);
      output.WriteLine("reference — MMSSTV noise on the reported capture: dx1=+0.489 dy1=+0.180 hf>0.2c/px=25.4%");
      output.WriteLine($"holding Sig={ProbeSig} where this experiment does not vary it");
      if (matchOperatingPoint)
        output.WriteLine($"arms re-centred to {arms[0].Tag}'s flat-top share; the Sig each needed is reported");
      output.WriteLine("");

      foreach (var (relPath, note) in Corpus)
      {
        string wav = Path.Combine(RecordingsRoot, relPath);
        if (!File.Exists(wav)) { output.WriteLine($"MISSING {relPath}"); continue; }
        output.WriteLine($"### {relPath}  — {note}");
        var capture = Load(wav);
        foreach (string reference in ReferenceDecodes(capture)) output.WriteLine($"    ref: {reference}");

        foreach (var burst in capture.Bursts)
        {
          output.WriteLine($"=== {burst.Tag} {burst.Mode}");
          output.WriteLine($"    {"arm",-14} {"sig",5} {"dx1",7} {"dy1",7} {"hf%",6} {"cdx1",7} "
            + $"{"flat%",6} {"rej%",6} {"mask",7} {"ms",6}");

          var sheet = new List<(string, RgbImage)> { ("00_raw", burst.Planes.ToRgb()) };
          Report("00_raw", 0, sheet[0].Item2, burst.Planes, null, 0);

          double target = -1;
          foreach (var arm in arms)
          {
            var options = arm.Options;
            if (matchOperatingPoint && target >= 0) options = MatchFlatTop(burst.Planes, options, target);

            var stats = new SstvNlmStats();
            var clock = Stopwatch.StartNew();
            var filtered = burst.Planes.Denoise(options, stats);
            clock.Stop();
            if (matchOperatingPoint && target < 0) target = 100 * stats.FlatTopShare;

            var img = filtered.ToRgb();
            Report(arm.Tag, options.NlmSig, img, filtered, stats, clock.ElapsedMilliseconds);
            img.SavePng(Path.Combine(dir, $"{burst.Tag}_{arm.Tag}.png"));
            sheet.Add((arm.Tag, img));
          }
          SstvProbeSheet.Montage(sheet, Path.Combine(dir, $"SHEET_{burst.Tag}.png"));
          output.WriteLine("");
        }
      }
      output.WriteLine($"contact sheets in {dir}");
    }

    private void Report(string tag, double sig, RgbImage img, SstvImagePlanes planes, SstvNlmStats? stats,
      long ms)
    {
      var (dx1, dy1, hf) = SstvProbeSheet.Texture(img);
      double cdx1 = SstvProbeSheet.ChromaDx1(planes);
      string flat = stats == null || stats.Evaluated == 0 ? "-" : $"{100 * stats.FlatTopShare:0.0}";
      string rej = stats == null || stats.Evaluated == 0 ? "-" : $"{100 * stats.RejectedShare:0.0}";
      string mask = stats == null ? "-" : stats.MaskedPixels.ToString();
      output.WriteLine($"    {tag,-14} {sig,5:0.00} {dx1,7:+0.000} {dy1,7:+0.000} {hf,5:0.0}% "
        + $"{cdx1,7:+0.000} {flat,6} {rej,6} {mask,7} {ms,6}");
    }

    /// <summary>Find the <c>Sig</c> at which this arm reaches <paramref name="targetFlat"/> percent of
    /// donors in the weight kernel's flat top — a bisection on log Sig, the flat-top share being
    /// monotone in it. Flat-top share is the right thing to match because it is the direct measure of
    /// how much of the search window is being averaged with full weight, i.e. of smoothing strength as
    /// the filter itself sees it (§5.6). An arm that cannot reach the target inside the bracket comes
    /// back at the bracket edge, and the reported flat% is what says so.</summary>
    private static SstvDenoiseOptions MatchFlatTop(SstvImagePlanes planes, SstvDenoiseOptions arm,
      double targetFlat)
    {
      double lo = 0.02, hi = 20.0;
      var best = arm;
      double bestErr = double.MaxValue;

      for (int i = 0; i < 8; i++)
      {
        double sig = Math.Sqrt(lo * hi);
        var candidate = arm with { NlmSig = sig };
        var stats = new SstvNlmStats();
        planes.Denoise(candidate, stats);

        double flat = 100 * stats.FlatTopShare;
        double err = Math.Abs(flat - targetFlat);
        if (err < bestErr) { bestErr = err; best = candidate; }
        if (flat < targetFlat) lo = sig; else hi = sig;
      }
      return best;
    }


    // ----------------------------------------------------------------------------------------------------
    //                                       decode and pairing
    // ----------------------------------------------------------------------------------------------------


    private sealed record Burst(string Tag, SstvMode Mode, SstvImagePlanes Planes);
    private sealed record Capture(List<Burst> Bursts, DateTime Start, DateTime End);

    // one decode serves every experiment in a `dotnet test` run of this class; planes are ~230 KB apiece
    private static readonly Dictionary<string, Capture> captureCache = new();

    /// <summary>Every image burst in one capture, reconstructed to RAW planes — <c>Method = None</c>, so
    /// no decode-time filter has touched them and the post-filters are measured on what the
    /// reconstruction actually produced (§12: "if both ever run, NLM must see the raw planes").</summary>
    private static Capture Load(string wav)
    {
      if (captureCache.TryGetValue(wav, out var cached)) return cached;

      var (iq, sr) = WavIqReader.Read(wav);
      var o = new SstvDecodeOptions { SampleRate = sr };
      double[] disc = SstvDecoder.Discriminator(iq, o);
      double[] sync = SstvDecoder.SyncAudio(disc, sr, o);
      var hits = SstvVisDetector.DetectAll(sync, sr);
      var extractor = SstvDecoder.ExtractTrains(sync, sr, hits);

      var bursts = new List<Burst>();
      string shortName = Path.GetFileName(wav)[..16].Replace("_", "");
      foreach (var train in extractor.Trains)
      {
        if (!extractor.IsImageTrain(train)) continue;
        var spec = SstvModes.Get(train.Format);
        int firstSync = (int)Math.Round(train.Regr.GetPulseTime(0));
        int margin = (int)(0.5 * sr);
        int dur = (int)(spec.LineCount * spec.LinePeriodMs / 1000.0 * sr);
        int start = Math.Max(0, firstSync - margin);
        int end = Math.Min(iq.Length, firstSync + dur + margin);
        if (end - start < dur / 2) continue;

        var planes = SstvDecoder.DecodePlanes(iq[start..end], train.Format, new SstvDecodeOptions
        {
          SampleRate = sr,
          Acquire = false,
          StartSample = firstSync - start,
          Denoise = new SstvDenoiseOptions { Method = SstvDenoiseMethod.None }
        });
        bursts.Add(new Burst($"{shortName}_t{firstSync / sr:0}", train.Format, planes));
      }

      DateTime.TryParseExact(Path.GetFileName(wav)[..19], "yyyy-MM-dd_HH_mm_ss",
        CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime began);
      var capture = new Capture(bursts, began, began.AddSeconds(iq.Length / sr));
      captureCache[wav] = capture;
      return capture;
    }

    /// <summary>The reference decoders' output for the same signal (§10), paired by timestamp: our wavs
    /// are named by recording START and every reference file is stamped at image COMPLETION, so a
    /// reference matches when its write time falls inside the capture. The matches are copied next to
    /// the sheets, because the pairing PROPOSES and content disposes — concurrent passes make some
    /// MMSSTV matches ambiguous (MMSSTV was fed one receiver, so which satellite it was decoding cannot
    /// be settled by clock), and at least one known pairing is spurious.</summary>
    private static IEnumerable<string> ReferenceDecodes(Capture capture)
    {
      if (capture.Start == default) yield break;
      Directory.CreateDirectory(OutDir);

      foreach (string dir in new[] { RxSstvDir, MmSstvDir })
      {
        if (!Directory.Exists(dir)) continue;
        foreach (string bmp in Directory.GetFiles(dir, "*.bmp"))
        {
          DateTime stamp = File.GetLastWriteTime(bmp);
          if (stamp < capture.Start || stamp > capture.End) continue;
          string dest = Path.Combine(OutDir,
            $"REF_{capture.Start:yyyyMMdd_HHmmss}_{new DirectoryInfo(dir).Name}_{Path.GetFileName(bmp)}");
          try { File.Copy(bmp, dest, true); } catch (IOException) { }
          yield return $"{Path.GetFileName(bmp)} @ {stamp:HH:mm:ss} ({new DirectoryInfo(dir).Name})";
        }
      }
    }
  }
}
