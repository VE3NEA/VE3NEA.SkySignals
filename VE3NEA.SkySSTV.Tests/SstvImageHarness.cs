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


    [Fact(Skip = "manual probe: processes a 102 MB capture. Result 2026-07-01 (retro J validated): " +
      "raw burst 0.243 / clutter 0.163; bandpassed burst 0.420 (= synthetic level) / clutter 0.406 — " +
      "the Stage-2 bandpass lifts real syncs to synthetic scores, but band-limited noise coherence rises " +
      "too, so single-pulse thresholds remain non-separable and the §4.1 train integration stays required.")]
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
      int pulseLen = (int)Math.Round(spec.SyncMs / 1000.0 * sr);

      Report("raw ", disc);
      Report("band", band);

      void Report(string name, double[] audio)
      {
        var filter = new SstvSyncFilter(audio, sr);
        int maxPos = filter.MaxPos(pulseLen);
        double gMax = 0; int gArg = 0;
        for (int t = pulseLen; t < maxPos; t++)
        {
          double s = filter.Score(t, pulseLen);
          if (s > gMax) { gMax = s; gArg = t; }
        }
        double burst = RegionMax(filter, 185, 215);
        double clutter = RegionMax(filter, 20, 45);
        output.WriteLine($"{name}: burst max={burst:0.000}  clutter max={clutter:0.000}  " +
          $"global max={gMax:0.000} @ {gArg / sr:0.0}s");
      }

      double RegionMax(SstvSyncFilter filter, double t0Sec, double t1Sec)
      {
        int a = Math.Max(pulseLen, (int)(t0Sec * sr));
        int b = Math.Min(filter.MaxPos(pulseLen), (int)(t1Sec * sr));
        double best = 0;
        for (int t = a; t < b; t++) best = Math.Max(best, filter.Score(t, pulseLen));
        return best;
      }
    }

    [Fact(Skip = "manual harness: processes multi-hundred-MB captures and needs P6/P7 burst localization " +
      "(real sync ~0.24 vs 0.40 synthetic; carrier confirmed centered so no AFC). Run on demand.")]
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
          var decoded = DecodeToImage(iq, sr);
          string stem = Path.GetFileNameWithoutExtension(wav);   // strips .wav; keeps .iq
          if (decoded is (RgbImage img, SstvModeResult res))
          {
            string path = Path.Combine(OutDir, $"{stem}_{res.Mode}.png");
            img.SavePng(path);
            output.WriteLine($"{stem}: {res.Mode} fromVis={res.FromVis} firstSync={res.FirstSyncSample} " +
              $"period={res.LinePeriodMs:0.0}ms score={res.SyncScore:0.00} -> {Path.GetFileName(path)}");
          }
          else output.WriteLine($"{stem}: no SSTV mode detected ({iq.Length / sr}s @ {sr}Hz)");
        }
        catch (Exception ex) { output.WriteLine($"{Path.GetFileName(wav)}: {ex.GetType().Name} {ex.Message}"); }
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
      var res = SstvDecoder.DetectMode(iq, new SstvDecodeOptions { SampleRate = fs });
      if (!res.Found || res.Mode is not SstvMode mode) return null;

      var spec = SstvModes.Get(mode);
      int margin = (int)(0.5 * fs);
      int dur = (int)(spec.LineCount * spec.LinePeriodMs / 1000.0 * fs);
      int start = Math.Max(0, res.FirstSyncSample - margin);
      int end = Math.Min(iq.Length, res.FirstSyncSample + dur + margin);
      var seg = iq[start..end];

      var img = SstvDecoder.Decode(seg, mode,
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
