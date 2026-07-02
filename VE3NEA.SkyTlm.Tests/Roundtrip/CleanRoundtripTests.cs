using System;
using FluentAssertions;
using VE3NEA.SkyTlm.Core;
using VE3NEA.SkyTlm.Dsp;
using VE3NEA.SkyTlm.Tests.Fixtures;
using Xunit;
using Xunit.Abstractions;

namespace VE3NEA.SkyTlm.Tests.Roundtrip
{
  /// <summary>
  /// End-to-end round-trip tests on a clean (noiseless) channel: synthesize a known GMSK signal,
  /// demodulate it, and check the recovered soft symbols match the transmitted bits. Isolates the
  /// demod DSP from the real recordings (and their uncertain baud/framing), so a failure here is a
  /// demod bug, not the corpus.
  /// </summary>
  public class CleanRoundtripTests
  {
    private readonly ITestOutputHelper output;
    public CleanRoundtripTests(ITestOutputHelper o) => output = o;

    private const double Fs = 48000;
    private static SignalParams Params(double baud) => new(baud, Modulation.GMSK,  Framing.USP, Fs);

    [Theory]
    [InlineData(9600)]   // sps = 5
    [InlineData(4800)]   // sps = 10
    [InlineData(2400)]   // sps = 20
    [InlineData(1200)]   // sps = 40
    public void CleanGmsk_RoundTrips_WithoutErrors(double baud)
    {
      var bits = GmskModulator.RandomBits(600, seed: 7);
      var iq = GmskModulator.Modulate(bits, baud, Fs, bt: 0.5);

      var sym = new GmskDemodulator().DemodulateSegment(iq, Params(baud));
      var (ber, off, sign) = BerTools.BestBer(bits, sym.Soft);
      output.WriteLine($"baud={baud} sps={Fs / baud} eye={sym.EyeSnrDb:0.0}dB ber={ber:0.000} off={off} sign={sign}");

      ber.Should().BeLessThan(1e-3, "clean GMSK must decode error-free");
      sym.EyeSnrDb.Should().BeGreaterThan(6, "a clean BT=0.5 GMSK eye is wide open through the discriminator");
      sym.SamplesPerSymbol.Should().BeApproximately(Fs / baud, Fs / baud * 0.02);
    }

    [Fact]
    public void RecoveredClock_MatchesBaud()
    {
      var bits = GmskModulator.RandomBits(500, seed: 3);
      var iq = GmskModulator.Modulate(bits, 4800, Fs);
      var sym = new GmskDemodulator().DemodulateSegment(iq, Params(4800));
      sym.SamplesPerSymbol.Should().BeApproximately(10.0, 0.2);
    }

    /// <summary>The front end alone (discriminator + matched filter), sampled at the known symbol
    /// centres, must be error-free — pins demod failures to timing recovery vs. the front end.</summary>
    [Fact]
    public void FrontEnd_AtKnownTiming_IsErrorFree()
    {
      double baud = 4800;
      var bits = GmskModulator.RandomBits(300, seed: 7);
      var trace = new GmskDemodulator().Trace(GmskModulator.Modulate(bits, baud, Fs), Params(baud));
      var mf = trace.Filtered;
      double sps = trace.NominalSps;                 // upsampled-domain sps (the front end may oversample)

      int errs = 0, tot = 0;
      for (int k = 5; k < bits.Length - 5; k++)
      {
        double t = (k + 0.5) * sps;                 // modulator ZOH: symbol k centre = (k+0.5)·sps
        int i = (int)t; double mu = t - i;
        double v = mf[i] * (1 - mu) + mf[i + 1] * mu;
        if (Math.Sign(v) != (bits[k] == 1 ? 1 : -1)) errs++;
        tot++;
      }
      ((double)errs / tot).Should().Be(0, "the discriminator + matched filter is correct independent of the clock");
    }
  }
}
