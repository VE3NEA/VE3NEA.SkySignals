using System;
using FluentAssertions;
using Xunit;

namespace VE3NEA.SkySSTV.Tests
{
  /// <summary>P6(b) unit tests for one MHT hypothesis (pulse-train association, gating, promotion).</summary>
  public class SstvPulseTrainTests
  {
    private const double Fs = 48000.0;
    private const double Period = 7200.0;                 // Robot36 line period in samples

    private static SstvPulseTrain Seed() => new SstvPulseTrain(SstvMode.Robot36,
      P(0), P((int)Period), P((int)(2 * Period)), Fs);

    private static SstvPulse P(int t) => new SstvPulse(t, 1.0f);

    [Fact]
    public void AcceptsInTrainPulses_AndPromotes()
    {
      var tr = Seed();
      tr.State.Should().Be(SstvTrainState.Candidate);
      for (int k = 3; k < 12; k++) tr.TryAddPulse(P((int)(Period * k))).Should().BeTrue($"pulse {k} is on the grid");
      tr.PulseCnt.Should().Be(12);
      tr.HasEnoughPulses.Should().BeTrue("12 on-grid pulses clear the promote threshold");
    }

    [Fact]
    public void RejectsOffGridPulse()
    {
      var tr = Seed();
      tr.TryAddPulse(P((int)(Period * 3 + 300))).Should().BeFalse("300 samples off the predicted slot");
      tr.PulseCnt.Should().Be(3);
    }

    [Fact]
    public void RetiresAfterInactivity()
    {
      var tr = Seed();
      int last = (int)(2 * Period);
      tr.CanRetire(last + (int)(5 * Fs)).Should().BeFalse("5 s idle is under the 6 s retire timeout");
      tr.CanRetire(last + (int)(7 * Fs)).Should().BeTrue("7 s idle exceeds the retire timeout");
    }
  }
}
