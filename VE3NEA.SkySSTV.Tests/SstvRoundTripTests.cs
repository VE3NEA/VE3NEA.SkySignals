using System;
using FluentAssertions;
using MathNet.Numerics;
using Xunit;
using Xunit.Abstractions;

namespace VE3NEA.SkySSTV.Tests
{
  /// <summary>
  /// P1 closed-loop pixel test: encode a known image → clean FM-on-FM IQ → decode at fixed timing →
  /// compare pixels (PSNR). This exercises the whole front-end (channel FIR → discriminator →
  /// downconvert → brightness low-pass → per-pixel integrator → YCrCb reconstruction) against exact
  /// ground truth. Grayscale gives near-exact chroma (constant Cr/Cb) and isolates luma fidelity; a
  /// smooth color image adds chroma with a looser bar (Robot36 vertically subsamples chroma).
  ///
  /// These decode with <c>Acquire = false, Track = false</c> at the known <c>StartSample = 0</c>: they
  /// isolate reconstruction fidelity from timing acquisition (P2, tested in <see cref="SstvP2Tests"/>) and
  /// from KF1 slant tracking (P3, tested in <see cref="SstvP3Tests"/>). The signal here is slant-free with a
  /// known start, so fixed nominal-period timing is exactly right and these bars gauge only the front-end.
  ///
  /// P6(a) note: the streaming Stage-3 brightness filter (mix + complex low-pass, replacing the batch FFT)
  /// band-limits the video, so it trades a little <b>clean</b> edge sharpness (Robot36 ~37→32 dB) for a large
  /// <b>noise</b> gain (see <see cref="SstvNoiseTests"/>); these clean bars were relaxed accordingly.
  /// </summary>
  public class SstvRoundTripTests
  {
    private const double Fs = 48000.0;
    private readonly ITestOutputHelper output;
    public SstvRoundTripTests(ITestOutputHelper o) => output = o;

    [Theory]
    [InlineData(SstvMode.Robot36)]
    [InlineData(SstvMode.Robot72)]
    public void GrayscaleGradient_DecodesWithHighPsnr(SstvMode mode)
    {
      var spec = SstvModes.Get(mode);
      var src = GrayscaleGradient(spec.Width, spec.Height);
      var iq = SstvEncoder.Encode(src, mode, new SstvEncoderOptions { IncludeVis = false });
      var dec = SstvDecoder.Decode(iq, mode, new SstvDecodeOptions { Acquire = false, Track = false });

      double psnr = Psnr(src, dec);
      output.WriteLine($"{mode} grayscale PSNR = {psnr:0.0} dB");
      psnr.Should().BeGreaterThan(30.0, "a clean grayscale gradient must decode with high fidelity");
    }

    [Theory]
    [InlineData(SstvMode.Robot36)]
    [InlineData(SstvMode.Robot72)]
    public void SmoothColorImage_DecodesWithGoodPsnr(SstvMode mode)
    {
      var spec = SstvModes.Get(mode);
      var src = SmoothColor(spec.Width, spec.Height);
      var iq = SstvEncoder.Encode(src, mode, new SstvEncoderOptions { IncludeVis = false });
      var dec = SstvDecoder.Decode(iq, mode, new SstvDecodeOptions { Acquire = false, Track = false });

      double psnr = Psnr(src, dec);
      output.WriteLine($"{mode} color PSNR = {psnr:0.0} dB");
      psnr.Should().BeGreaterThan(30.0, "a clean smooth color image must decode recognizably");
    }

    [Fact]
    public void Decode_IsRobustToModestNoise()
    {
      var spec = SstvModes.Get(SstvMode.Robot36);
      var src = GrayscaleGradient(spec.Width, spec.Height);
      var iq = SstvEncoder.Encode(src, SstvMode.Robot36,
        new SstvEncoderOptions { IncludeVis = false, NoiseStdDev = 0.01, NoiseSeed = 3 });
      var dec = SstvDecoder.Decode(iq, SstvMode.Robot36, new SstvDecodeOptions { Acquire = false, Track = false });

      double psnr = Psnr(src, dec);
      output.WriteLine($"noisy grayscale PSNR = {psnr:0.0} dB");
      psnr.Should().BeGreaterThan(28.0, "comfortably above the FM threshold the image stays recognizable");
    }

    [Fact]
    public void PdMode_GrayscaleDecodesWithHighPsnr()
    {
      var spec = SstvModes.Get(SstvMode.Pd50);          // smallest PD (320×256) keeps the round trip fast
      var src = GrayscaleGradient(spec.Width, spec.Height);
      var iq = SstvEncoder.Encode(src, SstvMode.Pd50, new SstvEncoderOptions { IncludeVis = false });
      var dec = SstvDecoder.Decode(iq, SstvMode.Pd50, new SstvDecodeOptions { Acquire = false, Track = false });

      double psnr = Psnr(src, dec);
      output.WriteLine($"PD50 grayscale PSNR = {psnr:0.0} dB");
      psnr.Should().BeGreaterThan(30.0, "PD luma must decode with high fidelity");
    }

    [Fact]
    public void PdMode_ColorDecodesWithGoodPsnr()
    {
      var spec = SstvModes.Get(SstvMode.Pd50);
      var src = SmoothColor(spec.Width, spec.Height);
      var iq = SstvEncoder.Encode(src, SstvMode.Pd50, new SstvEncoderOptions { IncludeVis = false });
      var dec = SstvDecoder.Decode(iq, SstvMode.Pd50, new SstvDecodeOptions { Acquire = false, Track = false });

      double psnr = Psnr(src, dec);
      output.WriteLine($"PD50 color PSNR = {psnr:0.0} dB");
      psnr.Should().BeGreaterThan(26.0, "PD shares one chroma pair per two luma rows, so color is looser");
    }


    // ----------------------------------------------------------------------------------------------------
    //                                         images / metric
    // ----------------------------------------------------------------------------------------------------


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

    private static RgbImage SmoothColor(int w, int h)
    {
      var img = new RgbImage(w, h);
      for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
          byte r = (byte)(x * 255 / (w - 1));
          byte g = (byte)(y * 255 / (h - 1));
          byte b = (byte)(255 - x * 255 / (w - 1));
          img.Set(x, y, r, g, b);
        }
      return img;
    }

    /// <summary>Peak-SNR (dB) over all pixels and channels; the two images must share dimensions.</summary>
    private static double Psnr(RgbImage a, RgbImage b)
    {
      a.Width.Should().Be(b.Width);
      a.Height.Should().Be(b.Height);
      double se = 0; long n = (long)a.Width * a.Height * 3;
      for (int i = 0; i < a.R.Length; i++)
      {
        se += Sq(a.R[i] - b.R[i]) + Sq(a.G[i] - b.G[i]) + Sq(a.B[i] - b.B[i]);
      }
      double mse = se / n;
      return mse <= 1e-9 ? 100.0 : 10.0 * Math.Log10(255.0 * 255.0 / mse);
    }

    private static double Sq(int d) => (double)d * d;
  }
}
