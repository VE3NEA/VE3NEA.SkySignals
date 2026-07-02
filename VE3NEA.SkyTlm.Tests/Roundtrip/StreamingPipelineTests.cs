using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using MathNet.Numerics;
using VE3NEA.SkyTlm.Core;
using VE3NEA.SkyTlm.Deframing;
using VE3NEA.SkyTlm.Tests.Fixtures;
using Xunit;
using Xunit.Abstractions;

namespace VE3NEA.SkyTlm.Tests.Roundtrip
{
  /// <summary>
  /// Online path: drive <see cref="StreamingPipeline"/> with a GMSK AX.25 burst embedded in silence, pushed in
  /// arbitrarily-sized blocks (so a burst straddles block boundaries), and prove it recovers the exact frame the
  /// batch path would — i.e. the streaming segmenter + buffered per-burst decode is equivalent to a file pass.
  /// </summary>
  public class StreamingPipelineTests
  {
    private readonly ITestOutputHelper output;
    public StreamingPipelineTests(ITestOutputHelper o) => output = o;

    private const double Fs = 48000;
    private const double Baud = 9600;
    private static SignalParams Params() => new(Baud, Modulation.GMSK,  Framing.AX25G3RUH, Fs);

    // A burst long enough to clear the min-burst gate, with generous flags so onset/offset are unambiguous.
    private static Complex32[] ModulatedBurst(byte[] frame) =>
      GmskModulator.Modulate(Ax25Tx.OnAirBits(frame, flagsBefore: 64, flagsAfter: 32), Baud, Fs, bt: 0.5, esN0Db: 30, seed: 7);

    private static Complex32[] Concat(params Complex32[][] parts)
    {
      var o = new Complex32[parts.Sum(p => p.Length)];
      int n = 0;
      foreach (var p in parts) { Array.Copy(p, 0, o, n, p.Length); n += p.Length; }
      return o;
    }

    /// <summary>Push the whole signal through the pipeline in fixed-size blocks, collecting every emitted frame.</summary>
    private static List<Frame> StreamAll(Complex32[] signal, int blockSize, SignalParams p)
    {
      using var sp = new StreamingPipeline(p);
      var frames = new List<Frame>();
      for (int i = 0; i < signal.Length; i += blockSize)
        frames.AddRange(sp.Push(signal.AsSpan(i, Math.Min(blockSize, signal.Length - i))));
      frames.AddRange(sp.Flush());
      return frames;
    }

    [Theory]
    [InlineData(4096)]   // block aligns with the FFT/hop grid
    [InlineData(1000)]   // ragged block, straddles frames
    [InlineData(333)]    // tiny ragged block, many cross-boundary bursts
    public void SingleBurst_InSilence_DecodesExactFrame(int blockSize)
    {
      var frameBytes = Ax25Tx.MakeUiFrame("CQ", "VE3NEA", $"streaming online block={blockSize}");
      // 0.5 s leading silence clears the detector warm-up (~0.34 s) before the burst arrives.
      var silence = new Complex32[(int)(0.5 * Fs)];
      var signal = Concat(silence, ModulatedBurst(frameBytes), silence);

      var frames = StreamAll(signal, blockSize, Params());

      output.WriteLine($"block={blockSize} frames={frames.Count}");
      frames.Should().ContainSingle("one clean burst must yield exactly one frame regardless of block size");
      frames[0].CrcValid.Should().BeTrue();
      frames[0].Bytes.Should().Equal(frameBytes);
      Ax25Address.Describe(frames[0].Bytes).Should().Be("VE3NEA -> CQ");

      // the frame is stamped with the burst's absolute stream time (~0.5 s in, allowing detector guard slack).
      frames[0].TimeSeconds.Should().BeApproximately(0.5, 0.1);
    }

    [Fact]
    public void TwoBursts_SeparatedBySilence_DecodeAsTwoOrderedFrames()
    {
      var f1 = Ax25Tx.MakeUiFrame("CQ", "VE3NEA", "first burst");
      var f2 = Ax25Tx.MakeUiFrame("CQ", "VE3NEA", "second burst");
      var lead = new Complex32[(int)(0.5 * Fs)];
      var gap = new Complex32[(int)(0.4 * Fs)];   // silence > hangover, so the two bursts segment apart
      var signal = Concat(lead, ModulatedBurst(f1), gap, ModulatedBurst(f2), gap);

      var frames = StreamAll(signal, 1024, Params());

      frames.Should().HaveCount(2);
      frames.All(f => f.CrcValid == true).Should().BeTrue();
      frames[0].Bytes.Should().Equal(f1);
      frames[1].Bytes.Should().Equal(f2);
      frames[1].TimeSeconds.Should().BeGreaterThan(frames[0].TimeSeconds);
    }

    [Fact]
    public void PureSilence_ProducesNoFrames()
    {
      var signal = new Complex32[(int)(2.0 * Fs)];
      StreamAll(signal, 997, Params()).Should().BeEmpty();
    }
  }
}
