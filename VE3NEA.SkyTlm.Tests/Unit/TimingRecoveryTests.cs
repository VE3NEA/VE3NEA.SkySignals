using System.Linq;
using FluentAssertions;
using VE3NEA.SkyTlm.Core;
using VE3NEA.SkyTlm.Dsp;
using VE3NEA.SkyTlm.Tests.Fixtures;
using Xunit;

namespace VE3NEA.SkyTlm.Tests.Unit
{
  /// <summary>
  /// Unit tests for the symbol-timing stages: the Oerder–Meyr feed-forward phase estimate, the
  /// Gardner PI tracking loop (<see cref="GmskDemodulator.OerderMeyrPhase"/> / <see cref="GmskDemodulator.GardnerSync"/>),
  /// and the whole-burst feed-forward recovery (<see cref="GmskDemodulator.FeedforwardSync"/>).
  /// </summary>
  public class TimingRecoveryTests
  {
    private const double Fs = 48000, Baud = 4800, Sps = Fs / Baud;
    private static SignalParams Params() => new(Baud, Modulation.GMSK,  Framing.USP, Fs);

    /// <summary>Front-end output (discriminator → matched filter) for a clean signal, feeding the timing tests.</summary>
    private static float[] FrontEnd(int seed = 7, int nBits = 600)
    {
      var demod = new GmskDemodulator();
      var iq = GmskModulator.Modulate(GmskModulator.RandomBits(nBits, seed), Baud, Fs);
      return demod.MatchedFilter(GmskDemodulator.Discriminate(iq, Params()), Sps);
    }

    [Fact]
    public void OerderMeyrPhase_IsWithinOneSymbol()
    {
      double tau = GmskDemodulator.OerderMeyrPhase(FrontEnd(), Sps);
      tau.Should().BeInRange(0, Sps);
    }

    [Fact]
    public void OerderMeyrPhase_IsInvariantToSignFlip()
    {
      var y = FrontEnd();
      var neg = y.Select(v => -v).ToArray();
      // the estimate works on |y|², so negating the input must not move the symbol-clock phase.
      GmskDemodulator.OerderMeyrPhase(neg, Sps)
        .Should().BeApproximately(GmskDemodulator.OerderMeyrPhase(y, Sps), 1e-9);
    }

    [Fact]
    public void GardnerStrobes_AreStrictlyIncreasing_WithMeanSpacingOneSymbol()
    {
      var (soft, strobes, settled) = new GmskDemodulator().GardnerSync(FrontEnd(), Sps);

      soft.Should().HaveCount(strobes.Length);
      for (int i = 1; i < strobes.Length; i++)
        strobes[i].Should().BeGreaterThan(strobes[i - 1], "the symbol clock only ever advances");

      double meanSpacing = (strobes[^1] - strobes[0]) / (strobes.Length - 1);
      meanSpacing.Should().BeApproximately(Sps, 0.1);
      settled.Should().BeApproximately(Sps, 0.2);
    }

    [Fact]
    public void SettledClock_StaysWithinMaxClockErrorBudget()
    {
      var opt = new GmskDemodOptions { MaxClockError = 0.02 };
      var (_, _, settled) = new GmskDemodulator(opt).GardnerSync(FrontEnd(), Sps);
      settled.Should().BeInRange(Sps * (1 - opt.MaxClockError), Sps * (1 + opt.MaxClockError));
    }

    [Fact]
    public void FeedforwardStrobes_AreStrictlyIncreasing_WithMeanSpacingOneSymbol()
    {
      var (soft, strobes, settled) = new GmskDemodulator().FeedforwardSync(FrontEnd(), Sps);

      soft.Should().HaveCount(strobes.Length);
      for (int i = 1; i < strobes.Length; i++)
        strobes[i].Should().BeGreaterThan(strobes[i - 1], "the symbol clock only ever advances");

      double meanSpacing = (strobes[^1] - strobes[0]) / (strobes.Length - 1);
      meanSpacing.Should().BeApproximately(Sps, 0.05);
      settled.Should().BeApproximately(Sps, 0.05);
    }

    [Fact]
    public void Feedforward_TracksClockRateOffsetExactly()
    {
      // TX clock 1% fast (within the ±2% MaxClockError budget) — the block estimate must land on the
      // TRUE period, not the nominal one a feedback loop would lag toward.
      var demod = new GmskDemodulator();
      var iq = GmskModulator.Modulate(GmskModulator.RandomBits(600, 7), Baud * 1.01, Fs);
      var mf = demod.MatchedFilter(GmskDemodulator.Discriminate(iq, Params()), Sps);
      var (_, _, settled) = demod.FeedforwardSync(mf, Sps);
      settled.Should().BeApproximately(Fs / (Baud * 1.01), 0.01);
    }

    [Fact]
    public void Feedforward_EnvelopeWeight_SurvivesNoiseTail()
    {
      // half the window is post-burst noise at full discriminator amplitude; the envelope weight marks
      // it as a deep fade, so the clock estimate must stay on the signal half's line.
      var y = FrontEnd();
      int nTail = y.Length / 2;
      var yy = new float[y.Length + nTail];
      Array.Copy(y, yy, y.Length);
      var rng = new Random(3);
      for (int i = 0; i < nTail; i++) yy[y.Length + i] = (float)(rng.NextDouble() * 6 - 3);
      var w = new float[yy.Length];
      for (int i = 0; i < w.Length; i++) w[i] = i < y.Length ? 1f : 0.01f;

      var (_, strobes, settled) = new GmskDemodulator().FeedforwardSync(yy, Sps, w);
      settled.Should().BeApproximately(Sps, 0.05);
      strobes.Should().NotBeEmpty();
    }

    [Fact]
    public void Feedforward_EndToEnd_DecodesSkewedBurst()
    {
      // full per-burst demod path (Trace) with feed-forward timing selected: a clean burst with a 1%
      // TX clock-rate offset decodes with zero errors, and the reported symbol period is the true one.
      var demod = new GmskDemodulator(new GmskDemodOptions { Timing = PskTiming.Feedforward });
      var bits = GmskModulator.RandomBits(600, 5);
      var iq = GmskModulator.Modulate(bits, Baud * 1.01, Fs);
      var soft = demod.DemodulateSegment(iq, Params());

      var (ber, _, _) = Fixtures.BerTools.BestBer(bits, soft.Soft);
      ber.Should().BeLessThan(0.002);
      soft.SamplesPerSymbol.Should().BeApproximately(Fs / (Baud * 1.01), 0.02);
    }

    [Fact]
    public void Feedforward_DegenerateInput_ReturnsEmpty()
    {
      var (soft, strobes, settled) = new GmskDemodulator().FeedforwardSync(new float[2], Sps);
      soft.Should().BeEmpty();
      strobes.Should().BeEmpty();
      settled.Should().Be(Sps);
    }
  }
}
