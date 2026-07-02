using System.Linq;
using FluentAssertions;
using VE3NEA.SkyTlm.Core;
using VE3NEA.SkyTlm.Dsp;
using VE3NEA.SkyTlm.Tests.Fixtures;
using Xunit;

namespace VE3NEA.SkyTlm.Tests.Unit
{
  /// <summary>
  /// Unit tests for the symbol-timing stages: the Oerder–Meyr feed-forward phase estimate and the
  /// Gardner PI tracking loop (<see cref="GmskDemodulator.OerderMeyrPhase"/> / <see cref="GmskDemodulator.GardnerSync"/>).
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
  }
}
