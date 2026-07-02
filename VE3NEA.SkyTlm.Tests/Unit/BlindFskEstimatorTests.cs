using FluentAssertions;
using VE3NEA.SkyTlm.Dsp;
using Xunit;
using Xunit.Abstractions;

namespace VE3NEA.SkyTlm.Tests.Unit
{
  /// <summary>
  /// Unit tests for <see cref="BlindFskEstimator"/>: synthetic two-tone PSD → recovered deviation,
  /// carrier, and gates.  No modulator or demodulator involved — just the estimator logic.
  /// </summary>
  public class BlindFskEstimatorTests
  {
    private readonly ITestOutputHelper output;
    public BlindFskEstimatorTests(ITestOutputHelper o) => output = o;

    private const double Fs = 192000;
    private const double Baud = 9600;
    private const int FftSize = 2048;
    private static readonly double BinHz = Fs / FftSize;   // ≈ 93.75 Hz

    /// <summary>Build a synthetic two-tone PSD (unit-amplitude Gaussians at ±dev from carrier).</summary>
    private static float[] MakeTwTonePsd(double devHz, double cfoHz = 0, double sigma = 300, double noise = 0.01)
    {
      // occBins must match what StreamingPipeline would allocate for blind FSK
      double devMax = Math.Min(3.0 * Baud, Fs / 2.0 - Baud / 2.0 - 2000);
      double occHalfHz = (Baud + 2 * devMax) / 2.0 + 2000;
      int occBins = (int)Math.Ceiling(occHalfHz / BinHz);
      int L = 2 * occBins + 1;
      var q = new float[L];
      double cfoBin = cfoHz / BinHz;
      double devBin = devHz / BinHz;
      for (int j = 0; j < L; j++)
      {
        double f = (j - occBins) - cfoBin;   // offset from carrier in bins
        double v = (float)(Math.Exp(-0.5 * Math.Pow((f - devBin) / (sigma / BinHz), 2))
                         + Math.Exp(-0.5 * Math.Pow((f + devBin) / (sigma / BinHz), 2)));
        q[j] = (float)Math.Max(0, v + noise);
      }
      return q;
    }

    private static int OccBins()
    {
      double devMax = Math.Min(3.0 * Baud, Fs / 2.0 - Baud / 2.0 - 2000);
      double occHalfHz = (Baud + 2 * devMax) / 2.0 + 2000;
      return (int)Math.Ceiling(occHalfHz / BinHz);
    }

    [Theory]
    [InlineData(0.5)]   // h = 1  → dev = baud/2
    [InlineData(1.0)]   // h = 2  → dev = baud
    [InlineData(2.0)]   // h = 4  → dev = 2×baud
    [InlineData(2.8)]   // h ≈ 5.6 → HADES-SA-like
    public void Estimate_TwTonePsd_RecoversDev(double devFracBaud)
    {
      double devHz = devFracBaud * Baud;
      var q = MakeTwTonePsd(devHz);
      int occBins = OccBins();

      var result = BlindFskEstimator.Estimate(q, occBins, BinHz, Baud, cfoMaxHz: 2000);

      output.WriteLine($"devFrac={devFracBaud} estimatedDev={result.DeviationHz:F0} Hz " +
                       $"cfo={result.CfoHz:F0} Hz confidence={result.Confidence:F3} isFsk={result.IsFsk}");

      result.IsFsk.Should().BeTrue("synthetic two-tone PSD must pass all FSK gates");
      result.DeviationHz.Should().BeApproximately(devHz, devHz * 0.15,
        $"estimated deviation should be within 15% of the true deviation ({devHz:F0} Hz)");
      result.CfoHz.Should().BeApproximately(0, Baud * 0.2,
        "carrier should be near DC for a zero-CFO PSD");
    }

    [Theory]
    [InlineData(+1500)]
    [InlineData(-800)]
    public void Estimate_CarrierOffset_RecoveredWithinTolerance(double cfoHz)
    {
      double devHz = Baud;   // h=2
      var q = MakeTwTonePsd(devHz, cfoHz: cfoHz);
      int occBins = OccBins();

      var result = BlindFskEstimator.Estimate(q, occBins, BinHz, Baud, cfoMaxHz: 2000);

      output.WriteLine($"trueCfo={cfoHz:F0} estimatedCfo={result.CfoHz:F0} dev={result.DeviationHz:F0}");

      result.IsFsk.Should().BeTrue();
      result.CfoHz.Should().BeApproximately(cfoHz, 300,
        "carrier should be recovered within ~3 bins");
    }

    [Fact]
    public void Estimate_CwSignal_ReturnsNotFsk()
    {
      // CW: single peak at the carrier, no two-sided structure
      double devMax = Math.Min(3.0 * Baud, Fs / 2.0 - Baud / 2.0 - 2000);
      double occHalfHz = (Baud + 2 * devMax) / 2.0 + 2000;
      int occBins = (int)Math.Ceiling(occHalfHz / BinHz);
      int L = 2 * occBins + 1;
      var q = new float[L];
      for (int j = 0; j < L; j++)
      {
        double f = j - occBins;
        q[j] = (float)Math.Exp(-0.5 * Math.Pow(f / (150 / BinHz), 2));   // narrow peak at DC
      }

      var result = BlindFskEstimator.Estimate(q, occBins, BinHz, Baud, cfoMaxHz: 2000);
      output.WriteLine($"CW test: isFsk={result.IsFsk} confidence={result.Confidence:F3}");

      result.IsFsk.Should().BeFalse("a single CW tone must fail the two-sidedness gate");
    }

    [Fact]
    public void EstimateCarrierFromKnownDev_PeaksAtCorrectCarrier()
    {
      double devHz = 9600;   // h=2
      double cfoHz = 1200;
      var q = MakeTwTonePsd(devHz, cfoHz: cfoHz);
      int occBins = OccBins();

      double estCfo = BlindFskEstimator.EstimateCarrierFromKnownDev(q, occBins, BinHz, cfoMaxHz: 2000, deviationHz: devHz);
      output.WriteLine($"trueCfo={cfoHz:F0} estimatedCfo={estCfo:F0}");

      estCfo.Should().BeApproximately(cfoHz, 300,
        "carrier-only search must locate the two-tone midpoint within ~3 bins");
    }
  }
}
