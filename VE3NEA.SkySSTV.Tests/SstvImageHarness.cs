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

    [Fact(Skip = "manual harness: processes multi-hundred-MB captures. Result 2026-07-02 after the P6(c) " +
      "real-tuned defaults (chan ±6 kHz, video ±600 Hz) + continuous VIS: 8 of 9 captures decode (was 6). " +
      "Monitor-3 text card essentially CLEAN (beats the RXSSTV reference); UTMN2 22:36 now picks the " +
      "stronger ~183 s burst (near-clean SPUTNIX); UmKA-1 anchors fromVis=True (continuous VIS found the " +
      "header at ~297 s) showing a Cyrillic card RXSSTV never got. Remaining: 12_37_50 Monitor-3 decodes " +
      "noise from a weak train (score 0.30) — the retro-D threshold-tuning case.")]
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


    [Fact(Skip = "manual probe: P6(c) filter sweep. Result 2026-07-02 on the real Monitor-3 text card: " +
      "the defaults (chan 15000 / video 1800) leave heavy speckle; narrowing to chan 4000–5000 + video " +
      "500–650 yields an essentially clean, fully readable image that BEATS the RXSSTV reference decode. " +
      "Brackets: chan 3000 clips the FM tails (speckle returns), video 350 over-smooths. Implication: the " +
      "real audio deviation is far below the synthetic 5 kHz — settle the deviation, re-baseline the " +
      "synthetic encoder, and lock new defaults in the P6(c) pass.")]
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


    [Fact(Skip = "manual probe: processes all captures. Result 2026-07-02: peak deviation ≈ 3.3 kHz on the " +
      "strong bursts (Monitor-3 3310, UTMN2 3303/3368 Hz); weaker bursts read 3.7–4.1 kHz (noise-inflated). " +
      "Corroborated by the FskDemod spectrogram (occupied width ≈ ±5 kHz, carrier centered). Basis for the " +
      "chan ±6 kHz default and the encoder's 3.3 kHz deviation.")]
    public void Real_DeviationProbe()
    {
      // P6(c): measure the real transmissions' peak FM deviation. The discriminator output inside a burst
      // is dev·a(t) with a(t) a unit sinusoid at the subcarrier frequency, so after the Stage-2 bandpass
      // (which also removes the Doppler DC) the peak deviation is simply RMS·√2 over the burst interior.
      foreach (string wav in Directory.GetFiles(RecordingsDir, "*.iq.wav"))
      {
        var (iq, sr) = WavIqReader.Read(wav);
        var o = new SstvDecodeOptions { SampleRate = sr };
        var res = SstvDecoder.DetectMode(iq, o);
        string stem = Path.GetFileNameWithoutExtension(wav);
        if (!res.Found || res.Mode is not SstvMode mode) { output.WriteLine($"{stem}: no burst"); continue; }

        var spec = SstvModes.Get(mode);
        int dur = (int)(spec.LineCount * spec.LinePeriodMs / 1000.0 * sr);
        int a = Math.Max(0, res.FirstSyncSample + dur / 10);
        int b = Math.Min(iq.Length, res.FirstSyncSample + dur - dur / 10);
        if (b <= a) { output.WriteLine($"{stem}: burst span empty"); continue; }

        double[] sync = SstvDecoder.SyncAudio(SstvDecoder.Discriminator(iq[a..b], o), sr, o);
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
