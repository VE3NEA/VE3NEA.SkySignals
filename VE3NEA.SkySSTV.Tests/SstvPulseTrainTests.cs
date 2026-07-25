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
      // 700 samples ≈ 14.6 ms: well past the early-train gate (~3σ of the 2.2 ms onset jitter + fit
      // variance ≈ 7-8 ms while the fit is young)
      var tr = Seed();
      tr.TryAddPulse(P((int)(Period * 3 + 700))).Should().BeFalse("14.6 ms off the predicted slot");
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




    // ----------------------------------------------------------------------------------------------------
    //                                        sync phase-step track
    // ----------------------------------------------------------------------------------------------------


    /// <summary>An in-lock train that steps its sync phase mid-image (the 2026-07-20 UmKA-1 defect: −11 ms,
    /// four times) must follow the step onto the new grid, and must keep the step out of the period
    /// estimate — fitted through the steps the period came out 0.11 % short, shearing every segment.</summary>
    [Fact]
    public void FollowsPhaseStep_WithoutDisturbingThePeriod()
    {
      int step = (int)(-0.011 * Fs);                      // −11 ms, as measured on the UmKA-1 burst
      var tr = Seed();
      for (int k = 3; k < 40; k++) tr.TryAddPulse(P((int)(Period * k))).Should().BeTrue();
      tr.State = SstvTrainState.Active;

      // the first two off-grid pulses only accumulate evidence; the third confirms the step
      tr.TryAddPulse(P((int)(Period * 40) + step)).Should().BeFalse("one off-grid pulse is not a step");
      tr.TryAddPulse(P((int)(Period * 41) + step)).Should().BeFalse("two are still not a step");
      tr.TryAddPulse(P((int)(Period * 42) + step)).Should().BeTrue("three consecutive confirm the step");

      tr.Offset(39).Should().Be(0, "lines before the step keep the original phase");
      tr.Offset(42).Should().BeApproximately(step, 0.5 * Fs / 1000, "the step is applied from line 40");
      tr.GetLineOnset(42).Should().BeApproximately(Period * 42 + step, 0.5 * Fs / 1000);
      tr.TakePhaseStep().Should().BeGreaterThan(0, "the stepped lines must be re-rendered");

      // the grid keeps tracking on the new phase, and the line clock is untouched by the step
      for (int k = 43; k < 60; k++)
        tr.TryAddPulse(P((int)(Period * k) + step)).Should().BeTrue($"line {k} sits on the stepped grid");
      tr.Regr.CorrFactor.Should().BeApproximately(1.0, 1e-4, "a phase step is not a clock error");
    }

    /// <summary>The lock-quality gate: a train that is not tracking may not declare a step. This is what
    /// separates real steps from noise — measured over the capture corpus, real steps sit at 0.90–1.00
    /// recent fill and every false candidate at 0.00–0.15, while tightening the confirmation count, the
    /// agreement window or the consecutiveness rule separates neither.</summary>
    [Fact]
    public void RejectsPhaseStep_WhenOutOfLock()
    {
      int step = (int)(-0.011 * Fs);
      var tr = Seed();
      for (int k = 3; k < 40; k++) tr.TryAddPulse(P((int)(Period * k))).Should().BeTrue();
      tr.State = SstvTrainState.Active;

      // 40 lines of silence — the fit stays tight, but the recent-fill window is empty — then three
      // consecutive agreeing off-grid pulses: the same evidence that confirms a step when in lock
      for (int k = 81; k < 84; k++) tr.TryAddPulse(P((int)(Period * k) + step)).Should().BeFalse();
      tr.Offset(83).Should().Be(0, "an out-of-lock train may not move its phase");
      tr.TakePhaseStep().Should().BeNegative();
    }
  }
}
