using System;
using Xunit;
using Xunit.Abstractions;

namespace VE3NEA.SkySSTV.Tests
{
  /// <summary>The controlled half of the dash investigation (2026-08-07): are the ~10 px horizontal
  /// dashes in heavily filtered output a directional bug in the filter, or the input noise's own
  /// horizontal correlation being reinforced? Real captures cannot answer that — their noise is already
  /// anisotropic — so the filter is fed noise whose correlation structure we set.
  ///
  /// <para>Companion to <c>SstvDenoiseProbe.DashAnatomy</c>, which does the same job on real bursts by
  /// splitting luma from chroma. Kept manual: 18 filtered cases, well over a minute.</para></summary>
  public class SstvNlmDashDiag
  {
    private const int W = 256, H = 192;
    private readonly ITestOutputHelper output;
    public SstvNlmDashDiag(ITestOutputHelper o) => output = o;

    [ManualFact("Result 2026-08-07. NO DIRECTIONAL BUG: on WHITE noise the filtered output is isotropic "
      + "to two decimals (search 10, sig 0.8: h[1,2,5,10,15] = +0.55 +0.52 +0.45 +0.31 +0.17 against "
      + "v = +0.56 +0.53 +0.42 +0.27 +0.10), and the reach of that structure TRACKS NlmSearchWing — "
      + "search 5 dies by lag 10, search 20 is still +0.18 at lag 15. So heavy NLM builds blobs the size "
      + "of its search window, which is where '10 px' comes from, and it does so without preferring an "
      + "axis. The elongation is the INPUT: at rho = 0.8 along rows (the real captures measure dx1 "
      + "+0.70..+0.83 against dy1 +0.05..+0.12) the same settings give h = +0.97 +0.93 +0.74 +0.42 +0.21 "
      + "against v = +0.50 +0.53 +0.38 +0.29 +0.07. NLM reinforces whatever direction the input already "
      + "correlates along, and the Stage-3 600 Hz brightness low-pass is what makes that horizontal. "
      + "Note also that at sig 0.4 on white noise there is no structure at all (h = v = +0.01): the "
      + "artifact belongs to the heavy end of the strength range, exactly where it was reported.")]
    public void DashDiagnostic()
    {
      output.WriteLine("input rho = AR(1) coefficient applied ALONG rows (0 = white noise)");
      output.WriteLine("acf = horizontal / vertical autocorrelation of the FILTERED output at lags 1,2,5,10,15");
      output.WriteLine("");

      foreach (double rho in new[] { 0.0, 0.5, 0.8 })
        foreach (int searchWing in new[] { 5, 10, 20 })
          foreach (double sig in new[] { 0.4, 0.8 })
            Case(rho, searchWing, sig);
    }

    private void Case(double rho, int searchWing, double sig)
    {
      var img = Noise(rho, seed: 17);
      var planes = new SstvImagePlanes(W, H, 1);
      Array.Fill(planes.RowRendered, true);
      for (int i = 0; i < img.Length; i++)
        planes.Y[i] = planes.Cr[i] = planes.Cb[i] = (byte)Math.Clamp(Math.Round(img[i]), 0, 255);

      var result = planes.Denoise(new SstvDenoiseOptions
      {
        Method = SstvDenoiseMethod.Nlm,
        NlmNoiseMap = SstvNlmNoiseMap.RowOnly,
        NlmTwoPass = false,
        NlmSearchWing = searchWing,
        NlmSig = sig
      });

      var y = new double[W * H];
      for (int i = 0; i < y.Length; i++) y[i] = result.Y[i];

      output.WriteLine($"rho={rho:0.0} search={searchWing,2} sig={sig:0.0}  "
        + $"in dx1={Acf(img, 1, true):+0.00} dy1={Acf(img, 1, false):+0.00}   "
        + $"out h[1,2,5,10,15]={Row(y, true)}   v[1,2,5,10,15]={Row(y, false)}");
    }

    private static string Row(double[] p, bool horizontal)
    {
      var parts = new string[5];
      int at = 0;
      foreach (int lag in new[] { 1, 2, 5, 10, 15 }) parts[at++] = $"{Acf(p, lag, horizontal):+0.00}";
      return string.Join(" ", parts);
    }

    /// <summary>Flat mid-grey plus noise that is either white or first-order correlated ALONG rows —
    /// the structure the ±600 Hz brightness low-pass leaves in real decoded noise.</summary>
    private static double[] Noise(double rho, int seed)
    {
      var rng = new Random(seed);
      var img = new double[W * H];
      double gain = 12.0 * Math.Sqrt(1 - rho * rho);         // hold the marginal sigma at 12 whatever rho is
      for (int row = 0; row < H; row++)
      {
        double prev = 0;
        for (int x = 0; x < W; x++)
        {
          double u1 = 1.0 - rng.NextDouble(), u2 = 1.0 - rng.NextDouble();
          double g = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
          prev = rho * prev + gain * g;
          img[row * W + x] = Math.Clamp(128 + prev, 0, 255);
        }
      }
      return img;
    }

    private static double Acf(double[] p, int lag, bool horizontal)
    {
      double mean = 0;
      for (int i = 0; i < p.Length; i++) mean += p[i];
      mean /= p.Length;

      double num = 0, den = 0;
      for (int row = 0; row < H; row++)
        for (int x = 0; x < W; x++)
        {
          double v = p[row * W + x] - mean;
          den += v * v;
          if (horizontal) { if (x + lag < W) num += v * (p[row * W + x + lag] - mean); }
          else if (row + lag < H) num += v * (p[(row + lag) * W + x] - mean);
        }
      return den > 0 ? num / den : 0;
    }
  }
}
