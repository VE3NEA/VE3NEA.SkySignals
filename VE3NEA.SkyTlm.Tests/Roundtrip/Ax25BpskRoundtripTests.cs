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
  /// Full round-trip for the PSK demod: build a real AX.25 G3RUH frame, push it through the TX chain
  /// and the BPSK modulator, then demodulate (<see cref="BpskDemodulator"/>) and deframe
  /// (<see cref="Ax25G3ruhDeframer"/>). Proves the soft symbols the PSK demod emits are good enough for the
  /// real deframer to recover the exact bytes with a valid CRC — the BPSK analogue of
  /// <see cref="Ax25ModemRoundtripTests"/>, and the validation that the off-air "BPSK1k2 AX.25" links target.
  /// </summary>
  public class Ax25BpskRoundtripTests
  {
    private readonly ITestOutputHelper output;
    public Ax25BpskRoundtripTests(ITestOutputHelper o) => output = o;

    private const double Fs = 48000;
    private static SignalParams Params(double baud, Modulation m) => new(baud, m, Framing.AX25G3RUH, Fs);

    [Theory]
    [InlineData(1200)]
    [InlineData(2400)]
    public void CoherentBpsk_To_Ax25Frame_RoundTrips(double baud)
    {
      var frame = Ax25Tx.MakeUiFrame("CQ", "VE3NEA", $"AX.25 G3RUH over BPSK at {baud} Bd");
      int[] onair = Ax25Tx.OnAirBits(frame, flagsBefore: 24, flagsAfter: 12);
      // a static carrier phase the Costas loop must recover
      var iq = BpskModulator.ModulateBpsk(onair, baud, Fs, phaseRad: 0.6);

      var soft = new BpskDemodulator(new BpskDemodOptions { Differential = false }).DemodulateSegment(iq, Params(baud, Modulation.BPSK));
      var frames = new Ax25G3ruhDeframer().Deframe(soft, Params(baud, Modulation.BPSK)).ToList();

      output.WriteLine($"BPSK baud={baud} eye={soft.EyeSnrDb:0.0}dB syms={soft.Count} frames={frames.Count}");
      frames.Should().Contain(f => f.CrcValid == true && f.Bytes.SequenceEqual(frame),
        "the coherent BPSK soft symbols must let the AX.25 deframer recover the exact frame");
      Ax25Address.Describe(frames.First(f => f.CrcValid == true).Bytes).Should().Be("VE3NEA -> CQ");
    }

    [Fact]
    public void DifferentialBpsk_To_Ax25Frame_RoundTrips()
    {
      var frame = Ax25Tx.MakeUiFrame("CQ", "VE3NEA", "AX.25 G3RUH over DBPSK 1k2");
      int[] onair = Ax25Tx.OnAirBits(frame, flagsBefore: 24, flagsAfter: 12);
      var iq = BpskModulator.ModulateDbpsk(onair, 1200, Fs, cfoHz: 80);   // differential is CFO-robust

      var soft = new BpskDemodulator(new BpskDemodOptions { Differential = true }).DemodulateSegment(iq, Params(1200, Modulation.BPSK));
      var frames = new Ax25G3ruhDeframer().Deframe(soft, Params(1200, Modulation.BPSK)).ToList();

      output.WriteLine($"DBPSK eye={soft.EyeSnrDb:0.0}dB syms={soft.Count} frames={frames.Count}");
      frames.Should().Contain(f => f.CrcValid == true && f.Bytes.SequenceEqual(frame),
        "differential detection must recover the exact frame through the AX.25 deframer, CFO and all");
    }

    [Fact]
    public void CoherentBpsk_DecodesUnderMildNoise()
    {
      // vanilla AX.25 has no FEC and the G3RUH descrambler multiplies channel errors, so this only pins the
      // realistic claim: at a clean-ish eye the soft chain still recovers the exact frame.
      var frame = Ax25Tx.MakeUiFrame("CQ", "VE3NEA", "mild noise");
      int[] onair = Ax25Tx.OnAirBits(frame, flagsBefore: 24, flagsAfter: 12);
      var iq = BpskModulator.ModulateBpsk(onair, 1200, Fs, phaseRad: 0.4, esN0Db: 12, seed: 3);

      var soft = new BpskDemodulator(new BpskDemodOptions { Differential = false }).DemodulateSegment(iq, Params(1200, Modulation.BPSK));
      var frames = new Ax25G3ruhDeframer().Deframe(soft, Params(1200, Modulation.BPSK)).ToList();

      output.WriteLine($"BPSK noisy eye={soft.EyeSnrDb:0.0}dB frames={frames.Count}");
      frames.Should().Contain(f => f.CrcValid == true && f.Bytes.SequenceEqual(frame));
    }
  }
}
