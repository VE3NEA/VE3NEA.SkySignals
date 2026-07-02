using FluentAssertions;
using VE3NEA.SkyTlm.Core;
using VE3NEA.SkyTlm.Deframing;
using VE3NEA.SkyTlm.Dsp;
using VE3NEA.SkyTlm.Tests.Fixtures;
using Xunit;
using Xunit.Abstractions;

namespace VE3NEA.SkyTlm.Tests.Roundtrip
{
  /// <summary>
  /// Full round-trip: build a real AX.25 G3RUH frame, run it through the TX chain and the GMSK
  /// modulator, then demodulate (<see cref="GmskDemodulator"/>) and deframe
  /// (<see cref="Ax25G3ruhDeframer"/>). Proves the soft symbols the demod emits are good enough for the
  /// deframer to recover the exact bytes with a valid CRC across the GMSK baud rates.
  /// </summary>
  public class Ax25ModemRoundtripTests
  {
    private readonly ITestOutputHelper output;
    public Ax25ModemRoundtripTests(ITestOutputHelper o) => output = o;

    private const double Fs = 48000;
    private static SignalParams Params(double baud) => new(baud, Modulation.GMSK,  Framing.AX25G3RUH, Fs);

    [Theory]
    [InlineData(9600)]
    [InlineData(4800)]
    [InlineData(2400)]
    [InlineData(1200)]
    public void Gmsk_To_Ax25Frame_RoundTrips(double baud)
    {
      var frame = Ax25Tx.MakeUiFrame("CQ", "VE3NEA", $"AX.25 G3RUH over GMSK at {baud} Bd");
      int[] onair = Ax25Tx.OnAirBits(frame, flagsBefore: 24, flagsAfter: 12);
      var iq = GmskModulator.Modulate(onair, baud, Fs, bt: 0.5);

      var soft = new GmskDemodulator().DemodulateSegment(iq, Params(baud));
      var frames = new Ax25G3ruhDeframer().Deframe(soft, Params(baud)).ToList();

      output.WriteLine($"baud={baud} eye={soft.EyeSnrDb:0.0}dB syms={soft.Count} frames={frames.Count}");
      frames.Should().ContainSingle("a clean GMSK AX.25 burst must yield exactly one frame");
      frames[0].CrcValid.Should().BeTrue();
      frames[0].Bytes.Should().Equal(frame);
      Ax25Address.Describe(frames[0].Bytes).Should().Be("VE3NEA -> CQ");
    }

    [Fact]
    public void Gmsk_To_Ax25Frame_DecodesUnderMildNoise()
    {
      // vanilla AX.25 has no FEC, and the G3RUH self-synchronizing descrambler multiplies every channel
      // error by 3 (then NRZI by 2), so a noisy short frame is unrecoverable beyond a few isolated bits.
      // this pins the realistic claim: under mild noise (clean-ish eye) the soft chain still recovers the
      // exact frame. Hard sensitivity / FEC is out of scope for AX.25 (that is the USP/FX.25 territory).
      var frame = Ax25Tx.MakeUiFrame("CQ", "VE3NEA", "mild noise");
      int[] onair = Ax25Tx.OnAirBits(frame, flagsBefore: 24, flagsAfter: 12);
      var iq = GmskModulator.Modulate(onair, 9600, Fs, bt: 0.5, esN0Db: 25, seed: 3);

      var soft = new GmskDemodulator().DemodulateSegment(iq, Params(9600));
      var frames = new Ax25G3ruhDeframer().Deframe(soft, Params(9600)).ToList();

      output.WriteLine($"eye={soft.EyeSnrDb:0.0}dB frames={frames.Count}" +
                     (frames.Count > 0 ? $" corrected={frames[0].CorrectedBits}" : ""));
      frames.Should().ContainSingle().Which.Bytes.Should().Equal(frame);
    }
  }
}
