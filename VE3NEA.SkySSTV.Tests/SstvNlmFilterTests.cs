using System;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace VE3NEA.SkySSTV.Tests
{
  /// <summary>
  /// Port-correctness tests for the non-local means filter (denoise plan §11 step 1). These are NOT
  /// the tuning experiments — those are visual and live in the probe (§9). What is asserted here is
  /// that the filter does the class of thing it claims: it removes noise, it does not degenerate into
  /// a box blur, it preserves an edge better than the box blur it must not become, and it leaves
  /// unrendered rows alone.
  ///
  /// <para>The degeneracy checks matter more than the PSNR one. A wrongly-scaled noise map makes
  /// every donor land in the weight kernel's flat top, at which point NLM IS a 21×21 box average —
  /// smooth, plausible, and precisely the defect the plan exists to remove (§5.6). Nothing in a PSNR
  /// number distinguishes that from the filter working.</para>
  /// </summary>
  public class SstvNlmFilterTests
  {
    private const int W = 96, H = 64;

    private readonly ITestOutputHelper output;
    public SstvNlmFilterTests(ITestOutputHelper o) => output = o;

    [Fact]
    public void Nlm_RemovesNoise_AndBeatsTheBoxBlurItMustNotBecome()
    {
      var clean = Pattern();
      var noisy = AddNoise(clean, 12.0, seed: 7);

      var filtered = Denoise(noisy);
      var boxed = BoxBlur(noisy, 21, 21);

      double before = Psnr(clean, noisy), after = Psnr(clean, filtered), box = Psnr(clean, boxed);
      output.WriteLine($"PSNR  noisy={before:0.0}  nlm={after:0.0}  box21x5={box:0.0} dB");

      after.Should().BeGreaterThan(before + 2.0, "the filter must actually remove noise");
      after.Should().BeGreaterThan(box, "a filter that loses to a plain box average has degenerated");
    }

    [Fact]
    public void Nlm_PreservesAStepEdge()
    {
      // a hard vertical edge: the classic thing a local smoother rounds off
      var clean = new double[W * H];
      for (int y = 0; y < H; y++)
        for (int x = 0; x < W; x++) clean[y * W + x] = x < W / 2 ? 40.0 : 210.0;

      var filtered = Denoise(AddNoise(clean, 10.0, seed: 3));

      // contrast measured a few pixels either side of the edge, clear of the transition itself
      double lo = 0, hi = 0;
      int n = 0;
      for (int y = 8; y < H - 8; y++, n++)
      {
        lo += filtered[y * W + W / 2 - 6];
        hi += filtered[y * W + W / 2 + 5];
      }
      double contrast = (hi - lo) / n;
      output.WriteLine($"edge contrast: {contrast:0.0} of 170 nominal");
      contrast.Should().BeGreaterThan(0.9 * 170.0, "NLM must not round off a step edge");
    }

    [Fact]
    public void Nlm_LeavesUnrenderedRowsUntouched()
    {
      var planes = new SstvImagePlanes(W, H, 1);
      var noisy = AddNoise(Pattern(), 12.0, seed: 11);
      for (int i = 0; i < noisy.Length; i++)
        planes.Y[i] = planes.Cr[i] = planes.Cb[i] = (byte)Math.Clamp(Math.Round(noisy[i]), 0, 255);
      for (int y = 0; y < H; y++) planes.RowRendered[y] = y is >= 8 and < H - 8;

      var result = planes.Denoise(new SstvDenoiseOptions { Method = SstvDenoiseMethod.Nlm });

      for (int y = 0; y < H; y++)
        for (int x = 0; x < W; x++)
          if (!planes.RowRendered[y])
            result.Y[y * W + x].Should().Be(planes.Y[y * W + x], $"row {y} was never rendered");

      bool changed = false;
      for (int i = 20 * W; i < 21 * W; i++) if (result.Y[i] != planes.Y[i]) changed = true;
      changed.Should().BeTrue("rendered rows must actually be filtered");
    }

    [Fact]
    public void Denoise_ReturnsNewPlanes_AndNoneIsACopy()
    {
      var planes = new SstvImagePlanes(W, H, 2);
      Array.Fill(planes.RowRendered, true);
      for (int i = 0; i < planes.Y.Length; i++) planes.Y[i] = (byte)(i % 251);

      var none = planes.Denoise(new SstvDenoiseOptions { Method = SstvDenoiseMethod.None });
      none.Should().NotBeSameAs(planes);
      none.Y.Should().NotBeSameAs(planes.Y);
      none.Y.Should().Equal(planes.Y, "None must be an exact copy");

      // re-applying can never compound: the source is untouched whatever was run on it
      var before = (byte[])planes.Y.Clone();
      planes.Denoise(new SstvDenoiseOptions { Method = SstvDenoiseMethod.Nlm });
      planes.Y.Should().Equal(before, "Denoise must not modify the planes it reads");
    }

    /// <summary>The §6 halo-band scheme against the serial sweep. It is NOT a bit-identity test, though
    /// the plan said it would be: the banded form sums each pixel's contributions in a different order —
    /// same-band donors interleaved by offset, previous-band donors arriving in one lump at the halo
    /// reduction — and floating-point addition is not associative. What is asserted is that the
    /// difference stays where it belongs, at the last bits of a double, and that the symmetry saving
    /// survived: a band that forgot to credit its spill would lose whole donors, not last bits.</summary>
    [Fact]
    public void Nlm_BandedAccumulationMatchesSerial()
    {
      const int Tall = 120;                                  // enough rows for several bands at MinBandRows
      var planes = new SstvImagePlanes(W, Tall, 2);
      Array.Fill(planes.RowRendered, true);
      var noisy = AddNoise(TallPattern(Tall), 12.0, seed: 23);
      for (int i = 0; i < noisy.Length; i++)
        planes.Y[i] = planes.Cr[i] = planes.Cb[i] = (byte)Math.Clamp(Math.Round(noisy[i]), 0, 255);

      var opts = new SstvDenoiseOptions { Method = SstvDenoiseMethod.Nlm, NlmTwoPass = true };
      var serial = planes.Denoise(opts with { NlmBands = 1 });
      var banded = planes.Denoise(opts with { NlmBands = 5 });

      int differing = 0, worst = 0;
      for (int i = 0; i < serial.Y.Length; i++)
      {
        int delta = Math.Abs(serial.Y[i] - banded.Y[i]);
        if (delta > 0) differing++;
        worst = Math.Max(worst, delta);
      }
      output.WriteLine($"serial vs 5 bands: {differing} of {serial.Y.Length} px differ, worst {worst} LSB");

      worst.Should().BeLessThanOrEqualTo(1, "only pixels sitting on a rounding boundary may move");
      differing.Should().BeLessThan(serial.Y.Length / 100, "a reordering must not change the picture");
    }

    [Fact]
    public void Nlm_ChromaRunsAtNativeResolution()
    {
      // duplicated chroma rows are the plan's §5.2 landmine: on the native grid they must collapse to
      // one row, so a duplicated pair stays a duplicated pair after filtering
      var planes = new SstvImagePlanes(W, H, 2);
      Array.Fill(planes.RowRendered, true);
      var noisy = AddNoise(Pattern(), 10.0, seed: 5);
      for (int y = 0; y < H; y++)
        for (int x = 0; x < W; x++)
        {
          byte v = (byte)Math.Clamp(Math.Round(noisy[(y & ~1) * W + x]), 0, 255);   // pairs are identical
          planes.Y[y * W + x] = planes.Cr[y * W + x] = planes.Cb[y * W + x] = v;
        }

      var result = planes.Denoise(new SstvDenoiseOptions { Method = SstvDenoiseMethod.Nlm });

      for (int y = 0; y + 1 < H; y += 2)
        for (int x = 0; x < W; x++)
          result.Cr[(y + 1) * W + x].Should().Be(result.Cr[y * W + x],
            "a chroma row pair must remain identical after a native-resolution pass");
    }


    // ----------------------------------------------------------------------------------------------------
    //                                            helpers
    // ----------------------------------------------------------------------------------------------------


    private static double[] Denoise(double[] plane)
    {
      var planes = new SstvImagePlanes(W, H, 1);
      Array.Fill(planes.RowRendered, true);
      for (int i = 0; i < plane.Length; i++)
        planes.Y[i] = (byte)Math.Clamp(Math.Round(plane[i]), 0, 255);

      var result = planes.Denoise(new SstvDenoiseOptions { Method = SstvDenoiseMethod.Nlm });
      var outp = new double[plane.Length];
      for (int i = 0; i < outp.Length; i++) outp[i] = result.Y[i];
      return outp;
    }

    /// <summary>Smooth background plus repeated thin strokes — the structure NLM is supposed to keep
    /// and a local filter is measured to lose.</summary>
    private static double[] Pattern()
    {
      var img = new double[W * H];
      for (int y = 0; y < H; y++)
        for (int x = 0; x < W; x++)
        {
          double v = 60 + 100.0 * x / W;
          if (x % 12 == 0 && y > 6 && y < H - 6) v = 235;            // repeated 1-px vertical strokes
          img[y * W + x] = v;
        }
      return img;
    }

    /// <summary><see cref="Pattern"/> at an arbitrary height, so the band scheme has rows to split.</summary>
    private static double[] TallPattern(int height)
    {
      var img = new double[W * height];
      for (int y = 0; y < height; y++)
        for (int x = 0; x < W; x++)
        {
          double v = 60 + 100.0 * x / W;
          if (x % 12 == 0 && y > 6 && y < height - 6) v = 235;
          img[y * W + x] = v;
        }
      return img;
    }

    private static double[] AddNoise(double[] img, double sigma, int seed)
    {
      var rng = new Random(seed);
      var outp = new double[img.Length];
      for (int i = 0; i < img.Length; i++)
      {
        double u1 = 1.0 - rng.NextDouble(), u2 = 1.0 - rng.NextDouble();
        double g = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
        outp[i] = Math.Clamp(img[i] + sigma * g, 0, 255);
      }
      return outp;
    }

    private static double[] BoxBlur(double[] img, int winW, int winH)
    {
      var outp = new double[img.Length];
      int rx = winW / 2, ry = winH / 2;
      for (int y = 0; y < H; y++)
        for (int x = 0; x < W; x++)
        {
          double sum = 0; int n = 0;
          for (int dy = -ry; dy <= ry; dy++)
            for (int dx = -rx; dx <= rx; dx++)
            {
              int yy = y + dy, xx = x + dx;
              if (yy < 0 || yy >= H || xx < 0 || xx >= W) continue;
              sum += img[yy * W + xx]; n++;
            }
          outp[y * W + x] = sum / n;
        }
      return outp;
    }

    private static double Psnr(double[] a, double[] b)
    {
      double mse = 0;
      for (int i = 0; i < a.Length; i++) { double d = a[i] - b[i]; mse += d * d; }
      mse /= a.Length;
      return mse <= 0 ? 99.0 : 10.0 * Math.Log10(255.0 * 255.0 / mse);
    }
  }
}
