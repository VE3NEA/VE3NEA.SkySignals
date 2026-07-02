using System;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace VE3NEA.SkySSTV.Tests
{
  /// <summary>
  /// P6(b) unit tests for the RLS sync regressor (period/phase from pulse times). Feeds synthetic pulse
  /// trains with a known period, a slant (clock error), missed pulses, and jitter, and checks the recovered
  /// period, predictions, and CorrFactor.
  /// </summary>
  public class SstvSyncRegressorTests
  {
    private readonly ITestOutputHelper output;
    public SstvSyncRegressorTests(ITestOutputHelper o) => output = o;

    [Fact]
    public void RecoversPeriodAndPhase_FromCleanTrain()
    {
      double truePeriod = 7200.0, truePhase = 137.0;   // Robot36 line period; arbitrary phase
      var r = new SstvSyncRegressor(0, 7200.0);
      for (int k = 0; k < 60; k++) r.ProcessPulse((int)Math.Round(truePeriod * k + truePhase));

      r.Period.Should().BeApproximately(truePeriod, 1.0);
      r.GetPulseTime(100).Should().BeApproximately(truePeriod * 100 + truePhase, 3.0);
      r.CorrFactor.Should().BeApproximately(1.0, 2e-4);
    }

    [Fact]
    public void TracksSlant_AsCorrFactor()
    {
      double nominal = 7200.0, slant = 1.0 + 200e-6;   // +200 ppm sample-clock error
      var r = new SstvSyncRegressor(0, nominal);
      for (int k = 0; k < 120; k++) r.ProcessPulse((int)Math.Round(nominal * slant * k));

      r.CorrFactor.Should().BeApproximately(slant, 5e-5);
      output.WriteLine($"CorrFactor = {r.CorrFactor:0.000000} (true {slant:0.000000})");
    }

    [Fact]
    public void SurvivesMissedPulses_ViaPulseNumber()
    {
      // drop every 3rd pulse: regressing on pulse NUMBER means the gaps cost nothing.
      double period = 7200.0;
      var r = new SstvSyncRegressor(0, period);
      for (int k = 0; k < 90; k++) if (k % 3 != 0) r.ProcessPulse((int)Math.Round(period * k));

      r.Period.Should().BeApproximately(period, 1.0);
      r.GetPulseNo((int)Math.Round(period * 50)).Should().Be(50);
    }

    [Fact]
    public void ConvergesUnderJitter()
    {
      double period = 7200.0;
      var rng = new Random(11);
      var r = new SstvSyncRegressor(0, period);
      for (int k = 0; k < 200; k++)
        r.ProcessPulse((int)Math.Round(period * k + (rng.NextDouble() - 0.5) * 8));   // ±4 sample jitter

      r.Period.Should().BeApproximately(period, 1.0, "RLS averages out zero-mean pulse-time jitter");
    }
  }
}
