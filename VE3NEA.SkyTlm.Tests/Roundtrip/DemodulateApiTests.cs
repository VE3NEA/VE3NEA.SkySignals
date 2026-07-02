using FluentAssertions;
using VE3NEA.SkyTlm.Core;
using VE3NEA.SkyTlm.Dsp;
using VE3NEA.SkyTlm.Tests.Fixtures;
using Xunit;
using Xunit.Abstractions;

namespace VE3NEA.SkyTlm.Tests.Roundtrip
{
  /// <summary>
  /// Exercises the public <see cref="GmskDemodulator.Demodulate(MathNet.Numerics.Complex32[], Burst, SignalParams)"/>
  /// entry point — the one the app and headless runner actually call. It carves the burst out of a longer
  /// recording and CFO-corrects it via <see cref="Acquisition.Derotate"/> before the DSP chain, so this is
  /// the only path that covers framing offsets and carrier-offset removal end-to-end.
  /// </summary>
  public class DemodulateApiTests
  {
    private readonly ITestOutputHelper output;
    public DemodulateApiTests(ITestOutputHelper o) => output = o;

    private const double Fs = 48000;
    private static SignalParams Params(double baud) => new(baud, Modulation.GMSK,  Framing.USP, Fs);

    [Theory]
    [InlineData(0)]        // no carrier offset
    [InlineData(1500)]     // +1.5 kHz CFO baked into the burst
    [InlineData(-2200)]    // −2.2 kHz CFO
    public void Demodulate_BurstWithCfo_RoundTrips(double cfoHz)
    {
      double baud = 4800;
      var bits = GmskModulator.RandomBits(600, seed: 9);
      var burstIq = GmskModulator.Modulate(bits, baud, Fs, bt: 0.5, cfoHz: cfoHz);

      // place the burst inside a longer recording so Demodulate must slice [start,end) before demod.
      int start = 1234;
      var recording = Signals.Embed(burstIq, burstIq.Length + 4000, start);
      var burst = new Burst(start, start + burstIq.Length, Fs, CfoHz: cfoHz, SnrDb: 30);

      var sym = new GmskDemodulator().Demodulate(recording, burst, Params(baud));
      var (ber, off, sign) = BerTools.BestBer(bits, sym.Soft);
      output.WriteLine($"cfo={cfoHz}Hz eye={sym.EyeSnrDb:0.0}dB ber={ber:0.000} off={off} sign={sign}");

      ber.Should().BeLessThan(1e-3, "derotation must remove the burst CFO so the clean signal decodes error-free");
      sym.SamplesPerSymbol.Should().BeApproximately(Fs / baud, Fs / baud * 0.02);
    }
  }
}
