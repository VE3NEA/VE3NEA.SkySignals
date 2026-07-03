using System;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace VE3NEA.SkySSTV.Tests
{
  /// <summary>
  /// Unit tests for the streaming soft-comb accumulator (plan §4.1 next action 1): a periodic score bump
  /// well below the single-pulse spawn threshold must produce a confirmed comb hit at the right mode and
  /// phase; pure score noise must never fire; and the true mode must out-score its half-rate harmonic.
  /// Score streams are synthesized directly — the comb consumes the detector's score tap, so no DSP is
  /// involved here.
  /// </summary>
  public class SstvSoftCombTests
  {
    private const double Fs = 48000.0;
    private const int Period = 7200;              // Robot36 line period in samples
    private const int Block = 12000;              // 0.25 s check cadence, as the extractor driver uses

    private readonly ITestOutputHelper output;
    public SstvSoftCombTests(ITestOutputHelper o) => output = o;

    [Fact]
    public void WeakPeriodicScore_FiresAtModeAndPhase()
    {
      // triangular bumps of peak 0.12 (below the 0.18 spawn tier, the 04-18 operating point) at phase
      // 2000, every Robot36 period, in zero-mean score noise of σ = 0.05
      var comb = new SstvSoftComb(Fs);
      var rng = new Random(7);
      SstvCombHit? hit = null;
      long firedAt = 0;
      for (long t = 0; t < 40 * Fs && hit == null; t++)
      {
        comb.Process(9.0, t, Score(t, rng, bumps: true));
        if (t % Block == Block - 1 && comb.Check(t) is SstvCombHit h) { hit = h; firedAt = t; }
      }

      hit.Should().NotBeNull("a weak-but-periodic score must accumulate a comb ridge");
      output.WriteLine($"hit at {firedAt / Fs:0.0}s: {hit!.Value.Mode} z={hit.Value.Z:0.0} " +
        $"anchor phase {hit.Value.AnchorSample % Period} (true 2000)");
      hit.Value.Mode.Should().Be(SstvMode.Robot36);
      hit.Value.Z.Should().BeGreaterThan(3.3, "the Robot36 ring's period-aware threshold");
      PhaseDist((int)(hit.Value.AnchorSample % Period), 2000).Should().BeLessThan(100,
        "the anchor must sit on the injected phase (±~2 ms)");
    }

    [Fact]
    public void TrueMode_OutScoresHalfRateHarmonic()
    {
      // after both the Robot36 and Robot72 rings are warm, the best hit for a Robot36-periodic score
      // must be Robot36 (the harmonic splits the same hits over two bins — √2 lower z)
      var comb = new SstvSoftComb(Fs);
      var rng = new Random(8);
      long t = 0;
      for (; t < 40 * Fs; t++) comb.Process(9.0, t, Score(t, rng, bumps: true));

      // arm the persistence gate: the ridge must stay over threshold for ConfirmChecks checks
      for (int k = 1; k < SstvSoftComb.ConfirmChecks; k++) comb.Check(t + k * Block);
      var hit = comb.Check(t + SstvSoftComb.ConfirmChecks * Block);
      hit.Should().NotBeNull();
      output.WriteLine($"best: {hit!.Value.Mode} z={hit.Value.Z:0.0}");
      hit.Value.Mode.Should().Be(SstvMode.Robot36, "the fundamental out-scores the half-rate harmonic");
    }

    [Fact]
    public void PureNoise_NeverFires()
    {
      var comb = new SstvSoftComb(Fs);
      var rng = new Random(9);
      for (long t = 0; t < 60 * Fs; t++)
      {
        comb.Process(9.0, t, Score(t, rng, bumps: false));
        if (t % Block == Block - 1)
          comb.Check(t).Should().BeNull($"pure score noise must not fire (t={t / Fs:0.0}s)");
      }
    }

    /// <summary>Zero-mean score noise of σ = 0.05, correlated over <b>2L = 864 samples</b> — the real
    /// detector stream's correlation (the bipolar template spans 2L), which is also what the comb's
    /// period-aware threshold assumes (white noise over 7200 bins would peak near 3.8σ and any fixed
    /// threshold would fail). Plus — when <paramref name="bumps"/> — a triangular bump of peak 0.12 and
    /// half-width 400 samples at phase 2000 of every period.</summary>
    private double Score(long t, Random rng, bool bumps)
    {
      double u1 = 1.0 - rng.NextDouble(), u2 = 1.0 - rng.NextDouble();
      double white = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
      maSum += white - maRing[maPos];
      maRing[maPos] = white;
      maPos = (maPos + 1) % maRing.Length;
      double score = 0.05 * maSum / Math.Sqrt(maRing.Length);   // σ = 0.05 after the moving average
      if (bumps)
      {
        long d = Math.Abs((t % Period) - 2000);
        if (d < 400) score += 0.12 * (1.0 - d / 400.0);
      }
      return score;
    }

    // moving-average state for the correlated score noise (fresh per test instance)
    private readonly double[] maRing = new double[864];
    private double maSum;
    private int maPos;

    private static int PhaseDist(int a, int b)
    {
      int d = Math.Abs(a - b);
      return Math.Min(d, Period - d);
    }
  }
}
