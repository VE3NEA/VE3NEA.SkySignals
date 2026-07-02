using FluentAssertions;
using VE3NEA.SkyTlm.Core;
using VE3NEA.SkyTlm.Dsp;
using VE3NEA.SkyTlm.Tests.Fixtures;
using Xunit;

namespace VE3NEA.SkyTlm.Tests.Unit
{
  /// <summary>
  /// Degenerate-input robustness for the full demod chain: tiny buffers, no signal, and a steady carrier
  /// must not throw or emit NaN/Inf, and must report a closed eye rather than a bogus one.
  /// </summary>
  public class EdgeCaseTests
  {
    private const double Fs = 48000, Baud = 4800;
    private static SignalParams Params() => new(Baud, Modulation.GMSK,  Framing.USP, Fs);

    [Theory]
    [InlineData(1)]    // single sample
    [InlineData(5)]    // shorter than one symbol (sps=10)
    public void SubSymbolInput_ProducesNoSymbols_WithoutThrowing(int n)
    {
      var sym = new GmskDemodulator().DemodulateSegment(Signals.Dc(n), Params());
      sym.Soft.Should().BeEmpty();
      sym.EyeSnrDb.Should().Be(0);
      sym.AmbiguousFraction.Should().Be(1);
    }

    [Fact]
    public void AllZeroSignal_YieldsFiniteOutput_AndClosedEye()
    {
      var sym = new GmskDemodulator().DemodulateSegment(new MathNet.Numerics.Complex32[2000], Params());
      sym.Soft.Should().OnlyContain(v => float.IsFinite(v));
      sym.EyeSnrDb.Should().Be(0, "no signal means no eye");
      sym.AmbiguousFraction.Should().Be(1);
    }

    [Fact]
    public void SteadyCarrier_YieldsFiniteOutput()
    {
      // A constant phasor has zero instantaneous frequency: the chain must stay finite, not divide by zero.
      var sym = new GmskDemodulator().DemodulateSegment(Signals.Dc(2000), Params());
      sym.Soft.Should().OnlyContain(v => float.IsFinite(v));
    }

    [Fact]
    public void NormalSignal_HasNoNaNOrInfInTheTrace()
    {
      var iq = GmskModulator.Modulate(GmskModulator.RandomBits(400, seed: 2), Baud, Fs);
      var trace = new GmskDemodulator().Trace(iq, Params());
      trace.Filtered.Should().OnlyContain(v => float.IsFinite(v));
      trace.Symbols.Soft.Should().OnlyContain(v => float.IsFinite(v));
    }
  }
}
