using FluentAssertions;
using VE3NEA.SkyTlm.Core;
using VE3NEA.SkyTlm.Dsp;
using VE3NEA.SkyTlm.Tests.Fixtures;
using Xunit;

namespace VE3NEA.SkyTlm.Tests.Unit
{
  /// <summary>
  /// Verifies that the <see cref="GmskDemodOptions"/> tunables actually reach the DSP and move behaviour
  /// in the expected direction: channel bandwidth, the clock-error clamp, and clock acquisition.
  /// </summary>
  public class OptionsTests
  {
    private const double Fs = 48000, Baud = 4800;
    private static SignalParams Params(double baud = Baud) => new(baud, Modulation.GMSK,  Framing.USP, Fs);

    [Fact]
    public void TightChannelBandwidth_RejectsOutOfBandNoise()
    {
      // the channel filter exists to drop the wideband noise outside the signal band before the
      // discriminator (where it would otherwise dominate the per-sample phase difference).
      var bits = GmskModulator.RandomBits(800, seed: 4);
      var noisy = GmskModulator.Modulate(bits, Baud, Fs, bt: 0.5, esN0Db: 8);

      double tightEye = new GmskDemodulator(new GmskDemodOptions { ChannelBwBaud = 1.0 })
        .DemodulateSegment(noisy, Params()).EyeSnrDb;
      double wideEye = new GmskDemodulator(new GmskDemodOptions { ChannelBwBaud = 6.0 })  // ≥0.5·Nyquist ⇒ no filter
        .DemodulateSegment(noisy, Params()).EyeSnrDb;

      tightEye.Should().BeGreaterThan(wideEye, "band-limiting to ~Carson bandwidth removes most of the out-of-band noise power");
    }

    [Fact]
    public void ZeroMaxClockError_PinsClockToNominalSps()
    {
      var iq = GmskModulator.Modulate(GmskModulator.RandomBits(500, seed: 6), Baud, Fs);
      var sym = new GmskDemodulator(new GmskDemodOptions { MaxClockError = 0 }).DemodulateSegment(iq, Params());
      sym.SamplesPerSymbol.Should().BeApproximately(Fs / Baud, 1e-9, "a zero clamp forces the period to nominal sps every step");
    }

    [Fact]
    public void MaxClockError_BoundsTheSettledClock()
    {
      // signal is truly 4920 Bd (sps≈9.76) but demodulated against the nominal 4800 Bd (sps=10). A tiny
      // budget clamps the recovered period to a ±0.1% window around nominal — it cannot reach the true sps.
      var iq = GmskModulator.Modulate(GmskModulator.RandomBits(800, seed: 8), 4920, Fs);

      foreach (double budget in new[] { 0.001, 0.05 })
      {
        double sps = new GmskDemodulator(new GmskDemodOptions { MaxClockError = budget })
          .DemodulateSegment(iq, Params()).SamplesPerSymbol;
        sps.Should().BeInRange(10.0 * (1 - budget), 10.0 * (1 + budget),
          $"the loop may never leave the ±{budget:P1} clock-error clamp");
      }
    }
  }
}
