using System;
using System.Collections.Generic;
using FluentAssertions;
using MathNet.Numerics;
using Xunit;
using Xunit.Abstractions;

namespace VE3NEA.SkySSTV.Tests
{
  /// <summary>
  /// P7.5(d) closed loop for the push-based decoder: encoded IQ streamed in one-second blocks must emit
  /// progressive <see cref="SstvDecoder.ImageUpdated"/> events and exactly one final image per
  /// transmission, aligned with the source (PSNR gate, like the batch closed loop) and carrying the
  /// §6.2 per-pixel confidence alpha plane.
  /// </summary>
  public class SstvStreamingDecoderTests
  {
    private const double Fs = 48000.0;
    private readonly ITestOutputHelper output;
    public SstvStreamingDecoderTests(ITestOutputHelper o) => output = o;

    private static RgbImage ColorBars(int w, int h)
    {
      var img = new RgbImage(w, h);
      var colors = new (byte r, byte g, byte b)[]
      {
        (255, 255, 255), (255, 255, 0), (0, 255, 255), (0, 255, 0),
        (255, 0, 255), (255, 0, 0), (0, 0, 255), (0, 0, 0)
      };
      for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
          var c = colors[x * colors.Length / w];
          img.Set(x, y, c.r, c.g, c.b);
        }
      return img;
    }

    private static double Psnr(RgbImage a, RgbImage b, int rows)
    {
      double mse = 0;
      int n = 0;
      for (int y = 0; y < rows; y++)
        for (int x = 0; x < a.Width; x++)
        {
          var (r1, g1, b1) = a.Get(x, y);
          var (r2, g2, b2) = b.Get(x, y);
          mse += (r1 - r2) * (double)(r1 - r2) + (g1 - g2) * (double)(g1 - g2)
               + (b1 - b2) * (double)(b1 - b2);
          n += 3;
        }
      mse /= n;
      return mse <= 0 ? 99 : 10 * Math.Log10(255.0 * 255.0 / mse);
    }

    [Theory]
    [InlineData(SstvMode.Robot36)]
    [InlineData(SstvMode.Robot72)]
    public void StreamedDecode_EmitsProgressiveAndFinalImage(SstvMode mode)
    {
      var spec = SstvModes.Get(mode);
      var src = ColorBars(spec.Width, spec.Height);
      var iq = SstvEncoder.Encode(src, mode, new SstvEncoderOptions { IncludeVis = true });

      using var dec = new SstvDecoder(new SstvDecodeOptions());
      int updates = 0;
      var finals = new List<SstvImageEvent>();
      dec.ImageUpdated += e => updates++;
      dec.ImageCompleted += e => finals.Add(e);

      int block = (int)Fs;                                    // one-second pushes
      for (int at = 0; at < iq.Length; at += block)
        dec.Process(iq.AsSpan(at, Math.Min(block, iq.Length - at)));
      dec.Flush();

      output.WriteLine($"{mode}: {updates} progressive updates, {finals.Count} final image(s)");
      foreach (var f in finals)
        output.WriteLine($"  final id={f.ImageId} {f.Mode} fromVis={f.FromVis} rows={f.ValidRows} " +
          $"start={f.StartSeconds:0.00}s");
      finals.Should().HaveCount(1, "one transmission must finalize exactly one image");
      updates.Should().BeGreaterThan(3, "the image must be surfaced progressively while it builds");

      var final = finals[0];
      final.Mode.Should().Be(mode);
      final.ValidRows.Should().BeGreaterThan((int)(0.9 * spec.Height), "nearly every row must render");
      final.Image.A.Should().NotBeNull("the §6.2 confidence plane rides in the alpha channel");

      double psnr = Psnr(src, final.Image, final.ValidRows);
      output.WriteLine($"{mode}: fromVis={final.FromVis} rows={final.ValidRows} PSNR={psnr:0.0} dB");
      psnr.Should().BeGreaterThan(15.0, "the streamed decode must be aligned with the source");
    }
  }
}
