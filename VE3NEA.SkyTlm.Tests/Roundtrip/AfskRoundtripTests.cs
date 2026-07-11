using System.Linq;
using FluentAssertions;
using MathNet.Numerics;
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

    [Fact]
    public void AfskMlse_To_Ax25Frame_RoundTrips()
    {
      // the coherent MLSE decision stage (generalized h = 5/6 trellis over the analytic subcarrier)
      // must recover the exact bytes on a clean burst, same as the correlator path.
      var frame = Ax25Tx.MakeUiFrame("CQ", "CUBEB2-6", "Manolito ad astra! 73");
      int[] onair = Ax25Tx.OnAirBitsPlain(frame, flagsBefore: 32, flagsAfter: 16);
      var iq = AfskModulator.Modulate(onair, 1200, Fs);

      var soft = new AfskDemodulator { UseMlseDetector = true }.DemodulateSegment(iq, Params());
      var frames = new Ax25G3ruhDeframer().Deframe(soft, Params()).ToList();

      output.WriteLine($"eye={soft.EyeSnrDb:0.0}dB syms={soft.Count} frames={frames.Count}");
      frames.Should().ContainSingle("a clean AFSK burst must yield exactly one frame via MLSE");
      frames[0].CrcValid.Should().BeTrue();
      frames[0].Bytes.Should().Equal(frame);
    }

    [Fact]
    public void AfskMlse_DecodesWhereTheCorrelatorCannot()
    {
      // the point of Phase-3 MLSE: the Es/N0 sweep (2026-07-11) put the CRC-decode threshold at
      // ≈18–19 dB for the correlator and ≈15–16 dB for the coherent trellis — a ~3 dB lever. Pin the
      // gap at 16 dB: the correlator decodes nothing, MLSE recovers the exact frame, every seed.
      var frame = Ax25Tx.MakeUiFrame("CQ", "CUBEB2-6", "coherent gain");
      int[] onair = Ax25Tx.OnAirBitsPlain(frame, flagsBefore: 32, flagsAfter: 16);
      for (int seed = 1; seed <= 3; seed++)
      {
        var iq = AfskModulator.Modulate(onair, 1200, Fs, esN0Db: 16, seed: seed);
        var corr = new AfskDemodulator().DemodulateSegment(iq, Params());
        var mlse = new AfskDemodulator { UseMlseDetector = true }.DemodulateSegment(iq, Params());
        var corrFrames = new Ax25G3ruhDeframer().Deframe(corr, Params()).ToList();
        var mlseFrames = new Ax25G3ruhDeframer().Deframe(mlse, Params()).ToList();
        output.WriteLine($"seed={seed}  correlator frames={corrFrames.Count}  mlse frames={mlseFrames.Count}");
        corrFrames.Should().BeEmpty("at 16 dB the non-coherent correlator is below its decode threshold");
        mlseFrames.Should().ContainSingle().Which.Bytes.Should().Equal(frame);
      }
    }

    [Fact]
    public void AfskMlseRetry_RecoversFrameInThePipeline()
    {
      // end-to-end proof of the pipeline wiring: at 16 dB the default correlator chain decodes zero
      // frames (see AfskMlse_DecodesWhereTheCorrelatorCannot), so the burst must be recovered by the
      // CRC-gated AFSK MLSE detector retry inside StreamingPipeline.DecodeBurst.
      var frame = Ax25Tx.MakeUiFrame("CQ", "CUBEB2-6", "pipeline retry");
      int[] onair = Ax25Tx.OnAirBitsPlain(frame, flagsBefore: 32, flagsAfter: 16);
      var lead = new Complex32[(int)(0.6 * Fs)];   // ≥ 0.34 s detector warm-up
      var burst = AfskModulator.Modulate(onair, 1200, Fs, esN0Db: 16, seed: 1);
      var signal = new Complex32[lead.Length * 2 + burst.Length];
      System.Array.Copy(lead, 0, signal, 0, lead.Length);
      System.Array.Copy(burst, 0, signal, lead.Length, burst.Length);

      using var sp = new StreamingPipeline(Params());
      var frames = new System.Collections.Generic.List<Frame>();
      const int block = 4096;
      for (int i = 0; i < signal.Length; i += block)
        frames.AddRange(sp.Push(signal.AsSpan(i, System.Math.Min(block, signal.Length - i))));
      frames.AddRange(sp.Flush());

      output.WriteLine($"pipeline frames={frames.Count} crc={string.Join(",", frames.Select(f => f.CrcValid))}");
      frames.Should().ContainSingle("the MLSE retry must recover the burst the correlator loses");
      frames[0].CrcValid.Should().BeTrue();
      frames[0].Bytes.Should().Equal(frame);
    }
  }
}
