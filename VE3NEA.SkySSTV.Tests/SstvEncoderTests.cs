using System;
using FluentAssertions;
using MathNet.Numerics;
using Xunit;
using Xunit.Abstractions;

namespace VE3NEA.SkySSTV.Tests
{
  /// <summary>
  /// Closed-loop harness for the synthetic SSTV modulator (P0). There is no decoder yet, so the loop
  /// is closed at the signal level: FM-discriminate the encoder's IQ back to the subcarrier and verify
  /// the VIS header, the sync/porch tones, and the brightness-frequency mapping — plus that Doppler,
  /// slant and noise behave as the plan predicts. This is the scaffold the P1 PSNR test will extend.
  /// </summary>
  public class SstvEncoderTests
  {
    private const double Fs = 48000.0;
    private readonly ITestOutputHelper output;
    public SstvEncoderTests(ITestOutputHelper o) => output = o;

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(255, 255, 255)]
    [InlineData(255, 0, 0)]
    [InlineData(0, 255, 0)]
    [InlineData(0, 0, 255)]
    [InlineData(123, 45, 200)]
    public void YCrCb_RoundTripsWithinTolerance(int r, int g, int b)
    {
      var (y, cr, cb) = YCrCb.FromRgb(r, g, b);
      var (rr, gg, bb) = YCrCb.ToRgb(y, cr, cb);
      rr.Should().BeApproximately(r, 2.0);
      gg.Should().BeApproximately(g, 2.0);
      bb.Should().BeApproximately(b, 2.0);
    }

    [Theory]
    [InlineData(SstvMode.Robot36)]
    [InlineData(SstvMode.Robot72)]
    [InlineData(SstvMode.Pd120)]
    public void Vis_RoundTripsToTheEncodedMode(SstvMode mode)
    {
      var spec = SstvModes.Get(mode);
      var iq = SstvEncoder.Encode(SolidImage(spec, 100, 150, 60), mode,
        new SstvEncoderOptions { IncludeVis = true });
      var audio = SstvTestSignal.RecoverAudio(iq, Fs, 5000.0);

      var (code7, parityOk) = SstvTestSignal.DecodeVis(audio, Fs);
      code7.Should().Be(spec.VisCode, "the decoded VIS must name the encoded mode");
      parityOk.Should().BeTrue("the encoder emits an even-parity VIS");
    }

    [Fact]
    public void SyncAndPorch_HaveExpectedTones()
    {
      var spec = SstvModes.Get(SstvMode.Robot36);
      var iq = SstvEncoder.Encode(SolidImage(spec, 128, 128, 128), SstvMode.Robot36,
        new SstvEncoderOptions { IncludeVis = false });
      var audio = SstvTestSignal.RecoverAudio(iq, Fs, 5000.0);

      double syncF = MeasureMs(audio, 2.0, 7.0);            // inside the 0..9 ms sync
      double porchF = MeasureMs(audio, 9.6, 11.4);          // inside the 9..12 ms porch
      output.WriteLine($"sync={syncF:0} Hz porch={porchF:0} Hz");

      syncF.Should().BeApproximately(SstvTones.Sync, 40);
      porchF.Should().BeApproximately(SstvTones.Black, 40);
    }

    [Theory]
    [InlineData(0, 0, 255)]
    [InlineData(255, 0, 0)]
    [InlineData(200, 200, 40)]
    public void YScan_EncodesTheComponentBrightnessFrequency(int r, int g, int b)
    {
      var spec = SstvModes.Get(SstvMode.Robot36);
      var iq = SstvEncoder.Encode(SolidImage(spec, r, g, b), SstvMode.Robot36,
        new SstvEncoderOptions { IncludeVis = false });
      var audio = SstvTestSignal.RecoverAudio(iq, Fs, 5000.0);

      // Y scan spans 12..100 ms (sync 9 + porch 3 + Y 88); measure well inside it.
      double measured = MeasureMs(audio, 40.0, 90.0);
      var (y, _, _) = YCrCb.FromRgb(r, g, b);
      double expected = SstvTones.ValueToFreq(y);
      output.WriteLine($"Y={y:0.0} expected={expected:0} measured={measured:0} Hz");

      measured.Should().BeApproximately(expected, 35);
    }

    [Fact]
    public void Scan_SweepsFromBlackToWhiteAcrossAGradientRow()
    {
      var spec = SstvModes.Get(SstvMode.Robot36);
      var img = new RgbImage(spec.Width, spec.Height);
      for (int y = 0; y < spec.Height; y++)
        for (int x = 0; x < spec.Width; x++)
        {
          byte v = (byte)(x * 255 / (spec.Width - 1));   // horizontal grayscale ramp
          img.Set(x, y, v, v, v);
        }
      var iq = SstvEncoder.Encode(img, SstvMode.Robot36, new SstvEncoderOptions { IncludeVis = false });
      var audio = SstvTestSignal.RecoverAudio(iq, Fs, 5000.0);

      // Y scan 12..100 ms: sample near the start (dark) and near the end (bright).
      double startF = MeasureMs(audio, 20.0, 30.0);
      double endF = MeasureMs(audio, 82.0, 92.0);
      output.WriteLine($"ramp startF={startF:0} endF={endF:0} Hz");
      endF.Should().BeGreaterThan(startF + 200, "brightness rises left→right, so subcarrier freq must rise");
    }

    [Fact]
    public void Doppler_AppearsAsDcOffset_NotAsAudioShift()
    {
      var spec = SstvModes.Get(SstvMode.Robot36);
      const double doppler = 1000.0;
      var iq = SstvEncoder.Encode(SolidImage(spec, 128, 128, 128), SstvMode.Robot36,
        new SstvEncoderOptions { IncludeVis = false, DopplerHz = doppler });

      var inst = SstvTestSignal.InstantaneousFreq(iq, Fs);
      double mean = 0; for (int i = 0; i < inst.Length; i++) mean += inst[i]; mean /= inst.Length;
      mean.Should().BeApproximately(doppler, 50, "constant RF offset is a DC term on the discriminator output");

      // the audio-frequency content (sync tone) is unchanged by the offset.
      var audio = SstvTestSignal.RecoverAudio(iq, Fs, 5000.0);
      MeasureMs(audio, 2.0, 7.0).Should().BeApproximately(SstvTones.Sync, 40);
    }

    [Fact]
    public void Slant_StretchesTotalDurationUniformly()
    {
      var spec = SstvModes.Get(SstvMode.Robot36);
      var img = SolidImage(spec, 128, 128, 128);
      int clean = SstvEncoder.Encode(img, SstvMode.Robot36, new SstvEncoderOptions { IncludeVis = false }).Length;
      int slanted = SstvEncoder.Encode(img, SstvMode.Robot36,
        new SstvEncoderOptions { IncludeVis = false, SlantPpm = 5000 }).Length;   // +0.5 %

      ((double)slanted / clean).Should().BeApproximately(1.005, 5e-4);
    }

    [Fact]
    public void Slant_IsFaithfulAtLowPpm()
    {
      // retro item K: per-segment rounding used to quantize slants below ~120 ppm to exactly zero; the
      // continuous time cursor must render realistic tens-of-ppm clock errors faithfully.
      var spec = SstvModes.Get(SstvMode.Robot36);
      var img = SolidImage(spec, 128, 128, 128);
      int clean = SstvEncoder.Encode(img, SstvMode.Robot36, new SstvEncoderOptions { IncludeVis = false }).Length;
      int slanted = SstvEncoder.Encode(img, SstvMode.Robot36,
        new SstvEncoderOptions { IncludeVis = false, SlantPpm = 50 }).Length;

      double expected = clean * 50e-6;                       // ≈ 86 samples over a Robot36 image
      (slanted - clean).Should().BeInRange((int)(expected - 2), (int)(expected + 2),
        "a 50 ppm slant must stretch the stream by ~50 ppm, not be rounded away per segment");
    }

    [Fact]
    public void Noise_PerturbsIq_AndVisStillDecodes()
    {
      var spec = SstvModes.Get(SstvMode.Robot36);
      var img = SolidImage(spec, 128, 128, 128);
      // pinned to 5 kHz deviation: this P0 scaffold reads bits with an unfiltered zero-crossing probe
      // (SstvTestSignal), which needs the wider deviation's noise margin; deviation realism is the
      // decoder tests' concern, this test only proves the noise knob perturbs the IQ.
      var clean = SstvEncoder.Encode(img, SstvMode.Robot36,
        new SstvEncoderOptions { IncludeVis = true, DeviationHz = 5000.0 });
      var noisy = SstvEncoder.Encode(img, SstvMode.Robot36,
        new SstvEncoderOptions { IncludeVis = true, DeviationHz = 5000.0, NoiseStdDev = 0.02, NoiseSeed = 7 });

      // the noise option must actually change the samples.
      double rms = 0; int nn = Math.Min(clean.Length, noisy.Length);
      for (int i = 0; i < nn; i++)
      {
        double dr = noisy[i].Real - clean[i].Real, di = noisy[i].Imaginary - clean[i].Imaginary;
        rms += dr * dr + di * di;
      }
      Math.Sqrt(rms / nn).Should().BeGreaterThan(0.02, "AWGN must perturb the IQ");

      var (code7, _) = SstvTestSignal.DecodeVis(SstvTestSignal.RecoverAudio(noisy, Fs, 5000.0), Fs);
      code7.Should().Be(spec.VisCode, "VIS must survive mild noise");
    }

    [Fact]
    public void EncodedLength_MatchesModeTiming()
    {
      var spec = SstvModes.Get(SstvMode.Robot36);
      var iq = SstvEncoder.Encode(SolidImage(spec, 0, 0, 0), SstvMode.Robot36,
        new SstvEncoderOptions { IncludeVis = true });

      double visMs = SstvTones.VisLeaderMs * 2 + SstvTones.VisBreakMs + SstvTones.VisBitMs * 10;
      double totalMs = visMs + spec.LineCount * spec.LinePeriodMs;
      double expected = totalMs / 1000.0 * Fs;
      ((double)iq.Length).Should().BeApproximately(expected, expected * 0.001);
    }


    // ----------------------------------------------------------------------------------------------------
    //                                             helpers
    // ----------------------------------------------------------------------------------------------------


    private double MeasureMs(double[] audio, double startMs, double endMs)
    {
      int start = SstvTestSignal.MsToSamples(startMs, Fs);
      int count = SstvTestSignal.MsToSamples(endMs - startMs, Fs);
      return SstvTestSignal.ToneFreq(audio, start, count, Fs);
    }

    private static RgbImage SolidImage(SstvModeSpec spec, int r, int g, int b)
    {
      var img = new RgbImage(spec.Width, spec.Height);
      for (int y = 0; y < spec.Height; y++)
        for (int x = 0; x < spec.Width; x++)
          img.Set(x, y, (byte)r, (byte)g, (byte)b);
      return img;
    }
  }
}
