using System;
using System.Diagnostics;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace VE3NEA.SkySSTV.Tests
{
  /// <summary>
  /// The denoiser on PD geometry. Every burst in the tuning corpus is Robot36 (212 of 214 saved
  /// images), so PD had been through the DECODER but never through <see cref="SstvImagePlanes.Denoise"/>
  /// on real geometry — and PD is exactly where the chroma layout differs: it sends one chroma pair for
  /// a pair of luma lines from a single <c>RenderPdLine</c>, where Robot36 alternates one component per
  /// line. Both end at <c>ChromaRowStep</c> 2, but by different routes, and the native-resolution
  /// collapse depends on the pairs being aligned to ABSOLUTE row parity.
  ///
  /// <para>The second test is the number behind D12 (apply-once dialog rather than live re-filtering):
  /// PD290 carries 6.4× Robot36's pixels, and the plan's cost estimate for it was an extrapolation.</para>
  /// </summary>
  public class SstvPdDenoiseTests
  {
    private readonly ITestOutputHelper output;
    public SstvPdDenoiseTests(ITestOutputHelper o) => output = o;

    [Theory]
    [InlineData(SstvDenoiseMethod.Wiener)]
    [InlineData(SstvDenoiseMethod.Nlm)]
    public void PdReconstruction_DenoisesTowardTheSource(SstvDenoiseMethod method)
    {
      var spec = SstvModes.Get(SstvMode.Pd50);
      var src = SmoothColor(spec.Width, spec.Height);
      var iq = SstvEncoder.Encode(src, SstvMode.Pd50,
        new SstvEncoderOptions { IncludeVis = false, NoiseStdDev = 0.05, NoiseSeed = 11 });
      var planes = SstvDecoder.DecodePlanes(iq, SstvMode.Pd50,
        new SstvDecodeOptions { Acquire = false, Track = false, Denoise = new SstvDenoiseOptions { Method = SstvDenoiseMethod.None } });

      planes.ChromaRowStep.Should().Be(2, "PD shares one chroma pair between two luma rows");
      planes.Coverage.Should().BeGreaterThan(0.9);

      var filtered = planes.Denoise(new SstvDenoiseOptions { Method = method });
      double raw = Psnr(src, planes.ToRgb()), den = Psnr(src, filtered.ToRgb());
      output.WriteLine($"PD50 {method}: raw {raw:0.00} dB → denoised {den:0.00} dB");

      den.Should().BeGreaterThan(raw, "on a smooth image both filters must move the picture toward the source");
    }

    [Fact]
    public void PdChromaPairsSurviveTheNativeCollapse()
    {
      // the pairs are indexed in absolute image coordinates, so a reconstruction that starts on an odd
      // row must not shear them by one — this is the failure the Robot36-only corpus could never show
      var spec = SstvModes.Get(SstvMode.Pd50);
      var src = SmoothColor(spec.Width, spec.Height);
      var iq = SstvEncoder.Encode(src, SstvMode.Pd50,
        new SstvEncoderOptions { IncludeVis = false, NoiseStdDev = 0.05, NoiseSeed = 12 });
      var planes = SstvDecoder.DecodePlanes(iq, SstvMode.Pd50,
        new SstvDecodeOptions { Acquire = false, Track = false, Denoise = new SstvDenoiseOptions { Method = SstvDenoiseMethod.None } });

      int pairs = 0;
      for (int y = 0; y + 1 < planes.Height; y += 2)
        for (int x = 0; x < planes.Width; x++)
          if (planes.Cr[(y + 1) * planes.Width + x] == planes.Cr[y * planes.Width + x]) { pairs++; break; }
      pairs.Should().BeGreaterThan(planes.Height / 4, "the reconstruction must duplicate chroma across pairs");

      var filtered = planes.Denoise(new SstvDenoiseOptions { Method = SstvDenoiseMethod.Nlm });

      for (int y = 0; y + 1 < planes.Height; y += 2)
      {
        if (!planes.RowRendered[y] || !planes.RowRendered[y + 1]) continue;
        for (int x = 0; x < planes.Width; x++)
        {
          int a = y * planes.Width + x, b = (y + 1) * planes.Width + x;
          if (planes.Cr[a] != planes.Cr[b]) continue;                 // only pairs that started identical
          filtered.Cr[b].Should().Be(filtered.Cr[a], $"chroma pair at row {y} must stay a pair");
          filtered.Cb[b].Should().Be(filtered.Cb[a], $"chroma pair at row {y} must stay a pair");
        }
      }
    }

    [Fact]
    public void Pd290SizedImage_DenoiseRuntimeIsAcceptable()
    {
      // the D12 number. Timed on synthetic planes of PD290's geometry rather than a real decode: the
      // filter's cost depends only on the pixel count and the noise level, and encoding 229 s of IQ to
      // learn a filter runtime would cost far more than the thing being measured
      var spec = SstvModes.Get(SstvMode.Pd290);
      var planes = NoisyPlanes(spec.Width, spec.Height, chromaRowStep: 2, seed: 7);

      double wiener = TimeDenoise(planes, new SstvDenoiseOptions { Method = SstvDenoiseMethod.Wiener });
      double nlm = TimeDenoise(planes, new SstvDenoiseOptions { Method = SstvDenoiseMethod.Nlm });
      double nlm1 = TimeDenoise(planes, new SstvDenoiseOptions { Method = SstvDenoiseMethod.Nlm, NlmTwoPass = false });
      output.WriteLine($"PD290 {spec.Width}×{spec.Height} = {spec.Width * spec.Height / 1000} kpx: " +
        $"Wiener {wiener:0.000} s, NLM {nlm:0.000} s, NLM 1-pass {nlm1:0.000} s");

      wiener.Should().BeLessThan(2.0, "the Wiener runs on every decode and must stay invisible");
      nlm.Should().BeLessThan(30.0, "a manual filter may be slow, but not so slow the dialog needs a progress bar");
    }


    // ----------------------------------------------------------------------------------------------------
    //                                            helpers
    // ----------------------------------------------------------------------------------------------------


    private static double TimeDenoise(SstvImagePlanes planes, SstvDenoiseOptions options)
    {
      planes.Denoise(options);                                        // warm the JIT
      var sw = Stopwatch.StartNew();
      planes.Denoise(options);
      return sw.Elapsed.TotalSeconds;
    }

    /// <summary>A smooth ramp plus repeated thin strokes under Gaussian noise, at whatever geometry is
    /// asked for — enough structure that the filter has both something to keep and something to remove.</summary>
    private static SstvImagePlanes NoisyPlanes(int w, int h, int chromaRowStep, int seed)
    {
      var planes = new SstvImagePlanes(w, h, chromaRowStep);
      Array.Fill(planes.RowRendered, true);
      var rnd = new Random(seed);

      for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
          double v = 60 + 120.0 * x / w;
          if (x % 17 == 0) v = 235;
          int i = y * w + x;
          planes.Y[i] = Quantize(v + Gauss(rnd) * 12.0);
          int c = (y / chromaRowStep) * chromaRowStep * w + x;        // chroma duplicated across the group
          if (i == c)
          {
            planes.Cr[i] = Quantize(128 + 40.0 * y / h + Gauss(rnd) * 12.0);
            planes.Cb[i] = Quantize(128 - 40.0 * y / h + Gauss(rnd) * 12.0);
          }
          else
          {
            planes.Cr[i] = planes.Cr[c];
            planes.Cb[i] = planes.Cb[c];
          }
        }
      return planes;
    }

    private static byte Quantize(double v) => (byte)Math.Clamp(Math.Round(v), 0, 255);

    private static double Gauss(Random rnd)
      => Math.Sqrt(-2.0 * Math.Log(1.0 - rnd.NextDouble())) * Math.Cos(2.0 * Math.PI * rnd.NextDouble());

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
