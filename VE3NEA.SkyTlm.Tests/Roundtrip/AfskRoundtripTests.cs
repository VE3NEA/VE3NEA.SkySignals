using System.Linq;
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
  /// Full round-trip for AFSK-over-FM (Bell-202, CUBEBUG-2's 1k2 downlink): build a real PLAIN AX.25 frame (no
  /// G3RUH scrambler), modulate it as an FM audio subcarrier (<see cref="AfskModulator"/>), then demodulate
  /// (<see cref="AfskDemodulator"/>: RF discriminate → mix by af_carrier → FSK orthogonal detector) and deframe
  /// (<see cref="Ax25G3ruhDeframer"/>, whose plain-NRZI path handles the unscrambled link). Proves the AFSK
  /// front end feeds the shared FSK engine cleanly and the deframer recovers the exact bytes — the part the
  /// marginal real recording can only hint at.
  /// </summary>
  public class AfskRoundtripTests
  {
    private readonly ITestOutputHelper output;
    public AfskRoundtripTests(ITestOutputHelper o) => output = o;

    private const double Fs = 48000;

    // AFSK at 1200 Bd; Deviation = the 500 Hz tone half-spacing, AfCarrier = the 1700 Hz Bell-202 audio centre.
    private static SignalParams Params() =>
      new(1200, Modulation.AFSK, Framing.AX25G3RUH, Fs, 500) { AfCarrier = 1700 };

    [Fact]
    public void Afsk_To_Ax25Frame_RoundTrips()
    {
      var frame = Ax25Tx.MakeUiFrame("CQ", "CUBEB2-6", "Manolito ad astra! 73");
      int[] onair = Ax25Tx.OnAirBitsPlain(frame, flagsBefore: 32, flagsAfter: 16);
      var iq = AfskModulator.Modulate(onair, 1200, Fs);

      var soft = new AfskDemodulator().DemodulateSegment(iq, Params());
      var frames = new Ax25G3ruhDeframer().Deframe(soft, Params()).ToList();

      output.WriteLine($"eye={soft.EyeSnrDb:0.0}dB syms={soft.Count} frames={frames.Count}");
      frames.Should().ContainSingle("a clean AFSK-over-FM AX.25 burst must yield exactly one frame");
      frames[0].CrcValid.Should().BeTrue();
      frames[0].Bytes.Should().Equal(frame);
      Ax25Address.Describe(frames[0].Bytes).Should().Be("CUBEB2-6 -> CQ");
    }

    [Fact]
    public void Afsk_To_Ax25Frame_DecodesUnderMildNoise()
    {
      var frame = Ax25Tx.MakeUiFrame("CQ", "CUBEB2-6", "mild noise");
      int[] onair = Ax25Tx.OnAirBitsPlain(frame, flagsBefore: 32, flagsAfter: 16);
      var iq = AfskModulator.Modulate(onair, 1200, Fs, esN0Db: 22, seed: 3);

      var soft = new AfskDemodulator().DemodulateSegment(iq, Params());
      var frames = new Ax25G3ruhDeframer().Deframe(soft, Params()).ToList();

      output.WriteLine($"eye={soft.EyeSnrDb:0.0}dB frames={frames.Count}" +
                       (frames.Count > 0 ? $" corrected={frames[0].CorrectedBits}" : ""));
      frames.Should().ContainSingle().Which.Bytes.Should().Equal(frame);
    }
  }
}
