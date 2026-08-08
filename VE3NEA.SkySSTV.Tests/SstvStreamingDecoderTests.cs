using System;
using System.Collections.Generic;
using System.Linq;
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
      final.Image.A.Should().BeNull("the confidence plane is no longer written — it had no consumer, "
        + "ToBitmap being 24bpp (denoise plan D16)");

      // the raw planes ride along on the FINAL event only, because denoising is offered at completion
      final.Planes.Should().NotBeNull("the denoise dialog re-filters the raw reconstruction");
      final.Planes!.Coverage.Should().BeGreaterThan(0.9);
      final.Planes.ChromaRowStep.Should().Be(mode == SstvMode.Robot72 ? 1 : 2);
      final.Planes.FirstRenderedRow.Should().BeGreaterThanOrEqualTo(0);
      final.Planes.RowRendered.Count(r => r).Should()
        .BeLessThanOrEqualTo(final.ValidRows, "ValidRows is a high-water mark, so it bounds the count");

      double psnr = Psnr(src, final.Image, final.ValidRows);
      output.WriteLine($"{mode}: fromVis={final.FromVis} rows={final.ValidRows} PSNR={psnr:0.0} dB");
      psnr.Should().BeGreaterThan(15.0, "the streamed decode must be aligned with the source");
    }

    [Fact]
    public void DeepFade_YieldsOneImage_NotTwo()
    {
      // the fading defect (2026-07-28): a fade deeper than the retire timeout used to end the image and
      // start a second one from the returning signal. One transmission must stay ONE image whatever the
      // fade does — the grid coasts through it and the lines after it land on the same reconstruction.
      // No VIS here: the VIS-seeded train always ran to its predicted image end, the plain train did not.
      var spec = SstvModes.Get(SstvMode.Robot36);
      var src = ColorBars(spec.Width, spec.Height);
      var iq = SstvEncoder.Encode(src, SstvMode.Robot36, new SstvEncoderOptions { IncludeVis = false });

      int fadeAt = (int)(0.45 * iq.Length), fadeLen = (int)(9 * Fs);   // 9 s of dead carrier mid-image
      for (int i = fadeAt; i < Math.Min(fadeAt + fadeLen, iq.Length); i++) iq[i] = Complex32.Zero;

      using var dec = new SstvDecoder(new SstvDecodeOptions());
      var finals = new List<SstvImageEvent>();
      dec.ImageCompleted += e => finals.Add(e);

      int block = (int)Fs;
      for (int at = 0; at < iq.Length; at += block)
        dec.Process(iq.AsSpan(at, Math.Min(block, iq.Length - at)));
      dec.Flush();

      foreach (var f in finals)
        output.WriteLine($"final id={f.ImageId} rows={f.ValidRows} start={f.StartSeconds:0.00}s");
      finals.Should().HaveCount(1, "a fade must not split one transmission into two images");
      finals[0].ValidRows.Should().BeGreaterThan((int)(0.9 * spec.Height),
        "decoding must run on to the last scan line");

      // the rows after the fade must carry the picture again, not just the rows before it
      double psnr = Psnr(src, finals[0].Image, spec.Height);
      output.WriteLine($"rows={finals[0].ValidRows} PSNR over the whole image={psnr:0.0} dB");
      psnr.Should().BeGreaterThan(10.0, "the post-fade lines must render into the same image");
    }
  }
}
