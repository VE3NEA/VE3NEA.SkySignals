using System;
using System.Collections.Generic;
using VE3NEA.SkyTlm.Core;

namespace VE3NEA.SkyTlm.Dsp
{
  /// <summary>
  /// Time-varying carrier-offset model for <b>continuous</b> demodulation: the detected bursts are used only
  /// for parameter estimation, each contributing one <c>(time, CFO)</c> anchor (its center time + residual
  /// CFO). <see cref="Eval"/> returns the CFO to derotate by at any instant — <b>piecewise-linear</b> between
  /// anchors (Doppler is smooth), held flat before the first anchor (demod starts at the first burst), and
  /// <b>leaky-linearly extrapolated</b> past the last anchor so it works causally in the streaming path too
  /// (future bursts unknown): the last segment's slope is followed but its displacement saturates at
  /// <c>slope·τ</c> (<paramref name="leakSeconds"/> = τ), so a stale trend never runs away during a long gap.
  /// The result is clamped to ±<c>maxHz</c>. With one anchor it is constant; with none it is 0.
  /// </summary>
  public sealed class CfoTrajectory
  {
    private readonly List<(double T, double F)> anchors = new();
    private readonly double maxHz;
    private readonly double leakSeconds;

    /// <param name="maxHz">Clamp on the evaluated CFO (the acquisition CFO search span); ±∞ to disable.</param>
    /// <param name="leakSeconds">Extrapolation time constant past the last anchor; <see cref="double.PositiveInfinity"/>
    /// for pure linear extrapolation.</param>
    public CfoTrajectory(double maxHz = double.PositiveInfinity, double leakSeconds = 30.0)
    {
      this.maxHz = maxHz > 0 ? maxHz : double.PositiveInfinity;
      this.leakSeconds = leakSeconds;
    }

    public int Count => anchors.Count;

    /// <summary>Add a CFO anchor; anchors are kept sorted by time (a later <see cref="Eval"/> interpolates).</summary>
    public void Add(double timeSeconds, double cfoHz)
    {
      int i = anchors.Count - 1;
      while (i >= 0 && anchors[i].T > timeSeconds) i--;
      anchors.Insert(i + 1, (timeSeconds, cfoHz));
    }

    /// <summary>Build a trajectory from detected bursts, anchoring each at its center time and residual CFO.</summary>
    public static CfoTrajectory FromBursts(IReadOnlyList<Burst> bursts, double maxHz, double leakSeconds = 30.0)
    {
      var t = new CfoTrajectory(maxHz, leakSeconds);
      foreach (var b in bursts) t.Add(0.5 * (b.StartSeconds + b.EndSeconds), b.CfoHz);
      return t;
    }

    /// <summary>CFO (Hz) at time <paramref name="t"/> (seconds) — interpolated/extrapolated per the class summary.</summary>
    public double Eval(double t)
    {
      int n = anchors.Count;
      if (n == 0) return 0;
      if (n == 1 || t <= anchors[0].T) return Clamp(anchors[0].F);

      var (tl, fl) = anchors[n - 1];
      if (t >= tl)
      {
        var (tp, fp) = anchors[n - 2];
        double slope = tl - tp > 1e-9 ? (fl - fp) / (tl - tp) : 0;
        double dt = t - tl;
        double disp = double.IsInfinity(leakSeconds)
          ? slope * dt
          : slope * leakSeconds * (1.0 - Math.Exp(-dt / leakSeconds));
        return Clamp(fl + disp);
      }

      // interpolate within the anchor range
      int hi = 1;
      while (anchors[hi].T < t) hi++;
      var (ta, fa) = anchors[hi - 1];
      var (tb, fb) = anchors[hi];
      double u = tb - ta > 1e-9 ? (t - ta) / (tb - ta) : 0;
      return Clamp(fa + u * (fb - fa));
    }

    private double Clamp(double f) => double.IsInfinity(maxHz) ? f : Math.Clamp(f, -maxHz, maxHz);
  }
}
