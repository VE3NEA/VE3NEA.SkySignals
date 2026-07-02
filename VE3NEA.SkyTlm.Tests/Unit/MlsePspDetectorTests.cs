using System;
using FluentAssertions;
using VE3NEA.SkyTlm.Core;
using VE3NEA.SkyTlm.Dsp;
using Xunit;
using Xunit.Abstractions;

namespace VE3NEA.SkyTlm.Tests.Unit
{
  /// <summary>
  /// Structural checks on the MLSE detector's trellis ingredients: the Laurent principal pulse C₀ and the
  /// ISI taps the branch metrics are built from. The round-trip BER suite (<c>MlsePspRoundtripTests</c>)
  /// proves the detector end-to-end; these pin the analytic shapes so a regression there is caught at the
  /// component, not as a mysterious BER loss.
  /// </summary>
  public class MlsePspDetectorTests
  {
    private readonly ITestOutputHelper output;
    public MlsePspDetectorTests(ITestOutputHelper o) => output = o;

    [Fact]
    public void Detector_IdealTiming_DecodesCleanGmsk()
    {
      // bypass the Gardner front end: hand the detector exact strobes (symbol centres) so a failure
      // here is a trellis/metric bug, not a timing-alignment one. Scan the fractional strobe offset
      // to also report where the detector's b-grid actually sits.
      const double fs = 48000, baud = 4800, sps = fs / baud;
      var bits = GmskModulator.RandomBits(600, seed: 7);
      var iq = GmskModulator.Modulate(bits, baud, fs, bt: 0.5);
      var p = new SignalParams(baud, Modulation.GMSK, Framing.USP, fs);
      var det = new MlsePspDetector(ModProfile.Gmsk, new GmskDemodOptions());

      double bestBer = 1, bestOff = 0;
      for (double off = 0; off < 1; off += 0.125)
      {
        int K = bits.Length - 2;
        var strobes = new double[K];
        for (int k = 0; k < K; k++) strobes[k] = (k + 0.5 + off) * sps;
        var ctx = new DetectorContext
        {
          Baseband = iq, GardnerSoft = new float[K], Strobes = strobes, Sps = sps, Params = p
        };
        var soft = det.Detect(ctx);
        var (ber, _, _) = Fixtures.BerTools.BestBer(bits, soft);
        output.WriteLine($"strobe offset {off:0.000} sym: ber={ber:0.0000}");
        if (ber < bestBer) { bestBer = ber; bestOff = off; }
      }
      output.WriteLine($"best: off={bestOff:0.000} ber={bestBer:0.0000}");
      bestBer.Should().BeLessThan(1e-3, "with ideal timing the trellis must decode clean GMSK error-free");
    }

    [Fact]
    public void LaurentC0_Msk_IsTheHalfSine()
    {
      // rectangular pulse, L=1: C₀(t) = sin(πt/2T) on [0, 2T] exactly (the Laurent expansion of MSK
      // has a single term). Compare shape against the analytic half-sine.
      double sps = 10;
      var c0 = MlsePspDetector.LaurentC0(bt: 0, sps, L: 1);
      c0.Length.Should().Be(21, "support is (L+1)·T = 2 symbols, odd-length kernel");

      double dot = 0, ee = 0, ea = 0;
      for (int i = 0; i < c0.Length; i++)
      {
        double t = i / sps;                          // symbols ∈ [0, 2]
        double a = Math.Sin(Math.PI * t / 2.0);
        dot += c0[i] * a; ee += (double)c0[i] * c0[i]; ea += a * a;
      }
      double corr = dot / Math.Sqrt(ee * ea);
      corr.Should().BeGreaterThan(0.999, "MSK's C₀ is exactly the half-sine");
    }

    [Fact]
    public void LaurentC0_IsSymmetricUnitEnergy()
    {
      var c0 = MlsePspDetector.LaurentC0(bt: 0.5, sps: 10, L: 3);
      double e = 0;
      foreach (var v in c0) e += (double)v * v;
      e.Should().BeApproximately(1.0, 1e-6, "the matched filter is normalized to unit energy");
      for (int i = 0; i < c0.Length / 2; i++)
        ((double)c0[i]).Should().BeApproximately(c0[c0.Length - 1 - i], 1e-4, "C₀ is even-symmetric (zero-phase kernel)");
    }

    [Fact]
    public void IsiTaps_Msk_HasTheLagOneOverlap()
    {
      // half-sine autocorrelation at lag T: ∫₀¹ cos(πu/2)·sin(πu/2) du / ∫₀² sin²(πt/2) dt = (1/π)/1.
      var c0 = MlsePspDetector.LaurentC0(bt: 0, sps: 10, L: 1);
      var taps = MlsePspDetector.IsiTaps(c0, sps: 10);
      taps[0].Should().Be(1.0);
      taps.Length.Should().Be(2, "the 2T-long half-sine overlaps one neighbour on each side");
      taps[1].Should().BeApproximately(1.0 / Math.PI, 0.02);
    }

    [Fact]
    public void EstimatorIntrinsic_SignFlipsBetweenMskAndGmsk()
    {
      // the squared-lag-product CFO estimator's intrinsic constant κ = E[(e_k·ē_{k−1})²]:
      // 1 − 8g₁² + 4g₁⁴ to leading order, so MSK (g₁ = 1/π) leaves it negative (the naive "κ = −1"
      // assumption holds) while GMSK BT 0.5 (g₁ ≈ 0.45) flips it positive. Assuming the MSK sign there
      // aliased the CFO estimate by exactly π/2/symbol — the bug this calibration exists to prevent.
      var (mskRe, mskIm) = MlsePspDetector.EstimatorIntrinsic(new[] { 1.0, 1.0 / Math.PI });
      var (gmskRe, gmskIm) = MlsePspDetector.EstimatorIntrinsic(new[] { 1.0, 0.447 });
      output.WriteLine($"MSK κ=({mskRe:0.000},{mskIm:0.000})  GMSK κ=({gmskRe:0.000},{gmskIm:0.000})");
      mskRe.Should().BeLessThan(0);
      gmskRe.Should().BeGreaterThan(0);
      Math.Abs(mskIm).Should().BeLessThan(1e-9, "κ is real by the ±bit symmetry");
      Math.Abs(gmskIm).Should().BeLessThan(1e-9);
    }

    [Fact]
    public void CoarseCfo_NearZeroOnCleanGmsk()
    {
      // regression for the κ-sign bug: on a clean zero-CFO burst the feed-forward estimate must be
      // ≈0, not the ±π/2 alias the trellis cannot absorb, and the Viterbi pass must decode clean.
      const double fs = 48000, baud = 4800, sps = fs / baud;
      var bits = GmskModulator.RandomBits(600, seed: 7);
      var iq = GmskModulator.Modulate(bits, baud, fs, bt: 0.5);
      var det = new MlsePspDetector(ModProfile.Gmsk, new GmskDemodOptions());
      int K = bits.Length - 2;
      var strobes = new double[K];
      for (int k = 0; k < K; k++) strobes[k] = (k + 0.5) * sps;
      det.Detect(new DetectorContext
      {
        Baseband = iq, GardnerSoft = new float[K], Strobes = strobes, Sps = sps,
        Params = new SignalParams(baud, Modulation.GMSK, Framing.USP, fs)
      });
      output.WriteLine($"dOmega={det.LastDOmega:0.0000} rad/sym");
      Math.Abs(det.LastDOmega).Should().BeLessThan(0.1);
      var hard = new float[det.LastViterbiBits.Length];
      for (int k = 0; k < hard.Length; k++) hard[k] = det.LastViterbiBits[k] == 1 ? 1f : -1f;
      var (berV, _, _) = Fixtures.BerTools.BestBer(bits, hard);
      berV.Should().BeLessThan(1e-3, "the PSP-Viterbi pass itself must decode clean GMSK error-free");
    }

    [Fact]
    public void IsiTaps_GmskBt05_KeepsOneSignificantNeighbour()
    {
      // BT=0.5 C₀ spans ~3T: the lag-1 tap is substantial (the ISI the trellis turns into evidence),
      // lag-2 falls under the 4% threshold and is truncated.
      var c0 = MlsePspDetector.LaurentC0(bt: 0.5, sps: 10, L: 3);
      var taps = MlsePspDetector.IsiTaps(c0, sps: 10);
      taps.Length.Should().Be(2);
      taps[1].Should().BeInRange(0.2, 0.5);
    }
  }
}
