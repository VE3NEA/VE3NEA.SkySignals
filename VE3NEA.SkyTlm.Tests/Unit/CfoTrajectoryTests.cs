using System;
using FluentAssertions;
using MathNet.Numerics;
using VE3NEA.SkyTlm.Core;
using VE3NEA.SkyTlm.Dsp;
using Xunit;

namespace VE3NEA.SkyTlm.Tests.Unit
{
  /// <summary>
  /// <see cref="CfoTrajectory"/> — the time-varying CFO model that drives continuous demod from the detected
  /// bursts (the bursts' only job is parameter estimation). Pins the piecewise-linear interpolation, the flat
  /// hold before the first anchor, the leaky-linear extrapolation past the last (so the streaming path can
  /// derotate ahead of the next burst), the ±maxHz clamp, and that a single-anchor (constant) trajectory
  /// derotates exactly like the per-burst <see cref="Acquisition.Derotate"/>.
  /// </summary>
  public class CfoTrajectoryTests
  {
    [Fact]
    public void Empty_EvaluatesToZero()
        => new CfoTrajectory().Eval(123.0).Should().Be(0);

    [Fact]
    public void SingleAnchor_IsConstant()
    {
      var t = new CfoTrajectory();
      t.Add(10.0, -500.0);
      t.Eval(0.0).Should().Be(-500.0);
      t.Eval(10.0).Should().Be(-500.0);
      t.Eval(1e6).Should().Be(-500.0);
    }

    [Fact]
    public void Interpolates_LinearlyBetweenAnchors()
    {
      var t = new CfoTrajectory();
      t.Add(0.0, -1000.0);
      t.Add(10.0, -900.0);          // +10 Hz/s drift
      t.Eval(-5.0).Should().Be(-1000.0, "before the first anchor the CFO is held flat");
      t.Eval(5.0).Should().BeApproximately(-950.0, 1e-6);
      t.Eval(2.5).Should().BeApproximately(-975.0, 1e-6);
    }

    [Fact]
    public void ExtrapolatesPastLastAnchor_LeakySaturates()
    {
      // slope = +10 Hz/s, leak τ = 5 s: displacement saturates at slope·τ = 50 Hz.
      var t = new CfoTrajectory(maxHz: double.PositiveInfinity, leakSeconds: 5.0);
      t.Add(0.0, -1000.0);
      t.Add(10.0, -900.0);
      double justAfter = t.Eval(10.0 + 1e-3);
      justAfter.Should().BeApproximately(-900.0, 0.5, "extrapolation starts at the last anchor value");
      t.Eval(10.0 + 1.0).Should().BeGreaterThan(-900.0, "follows the upward (+Hz/s) trend at first");
      t.Eval(1e6).Should().BeApproximately(-850.0, 1e-3, "far-future displacement saturates at slope·τ = +50 Hz");
    }

    [Fact]
    public void ExtrapolatesLinearly_WhenLeakInfinite()
    {
      var t = new CfoTrajectory(maxHz: double.PositiveInfinity, leakSeconds: double.PositiveInfinity);
      t.Add(0.0, 0.0);
      t.Add(10.0, 100.0);           // +10 Hz/s
      t.Eval(20.0).Should().BeApproximately(200.0, 1e-6, "pure linear extrapolation");
    }

    [Fact]
    public void ClampsToMaxHz()
    {
      var t = new CfoTrajectory(maxHz: 2000.0, leakSeconds: double.PositiveInfinity);
      t.Add(0.0, 0.0);
      t.Add(1.0, 1000.0);           // +1000 Hz/s → would reach 5000 Hz at t=5 without the clamp
      t.Eval(5.0).Should().Be(2000.0);
    }

    [Fact]
    public void FromBursts_AnchorsAtBurstCenters()
    {
      double fs = 48000;
      var bursts = new[]
      {
        new Burst(0, (int)fs, fs, -1000, 10),          // center 0.5 s
        new Burst((int)(2 * fs), (int)(3 * fs), fs, -800, 10),  // center 2.5 s
      };
      var t = CfoTrajectory.FromBursts(bursts, maxHz: 2000);
      t.Eval(0.5).Should().BeApproximately(-1000, 1e-6);
      t.Eval(2.5).Should().BeApproximately(-800, 1e-6);
      t.Eval(1.5).Should().BeApproximately(-900, 1e-6, "midway between the two burst centers");
    }

    [Fact]
    public void DerotateVarying_ConstantCfo_MatchesLegacyDerotate()
    {
      // A pure tone at +300 Hz; a constant (single-anchor) trajectory must remove it exactly as the
      // per-burst Derotate does, leaving DC.
      double fs = 48000, f0 = 300; int n = 4096;
      var iq = new Complex32[n];
      for (int i = 0; i < n; i++)
      {
        double ph = 2 * Math.PI * f0 * i / fs;
        iq[i] = new Complex32((float)Math.Cos(ph), (float)Math.Sin(ph));
      }
      var burst = new Burst(0, n, fs, f0, 20);
      var legacy = Acquisition.Derotate(iq, burst);

      var traj = new CfoTrajectory();
      traj.Add(n / 2.0 / fs, f0);   // single anchor → constant f0 everywhere
      var varying = Acquisition.DerotateVarying(iq, 0, n, fs, traj);

      for (int i = 0; i < n; i += 256)
      {
        varying[i].Real.Should().BeApproximately(legacy[i].Real, 1e-3f);
        varying[i].Imaginary.Should().BeApproximately(legacy[i].Imaginary, 1e-3f);
      }
    }
  }
}
