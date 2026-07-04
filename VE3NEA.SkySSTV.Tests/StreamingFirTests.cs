using System;
using FluentAssertions;
using MathNet.Numerics;
using Xunit;

namespace VE3NEA.SkySSTV.Tests
{
  /// <summary>
  /// P7.5(a): the stateful <see cref="StreamingFir"/>/<see cref="StreamingFirComplex"/> must reproduce the
  /// batch <see cref="LiquidFir.ConvolveSame(float[], float[])"/> output exactly (same native engine, same
  /// arithmetic), shifted by <see cref="StreamingFir.GroupDelay"/> — and the result must not depend on how
  /// the stream is split into blocks, or the streaming decoder would decode differently per block size.
  /// </summary>
  public class StreamingFirTests
  {
    private static float[] Kernel(int taps)
    {
      // an arbitrary centred symmetric (linear-phase) kernel like the BlackmanSinc stages use
      var h = new float[taps];
      int c = taps / 2;
      for (int i = 0; i < taps; i++)
      {
        double x = (i - c) / (double)taps;
        h[i] = (float)(Math.Cos(Math.PI * x) * Math.Exp(-8 * x * x));
      }
      return h;
    }

    [Theory]
    [InlineData(63, 1000)]
    [InlineData(255, 4096)]
    public void RealStream_MatchesConvolveSame_AcrossBlockSplits(int taps, int n)
    {
      var rng = new Random(7);
      var x = new float[n];
      for (int i = 0; i < n; i++) x[i] = (float)(rng.NextDouble() * 2 - 1);
      float[] h = Kernel(taps);
      float[] expected = LiquidFir.ConvolveSame(x, h);

      foreach (int block in new[] { 1, 17, 256, n })
      {
        using var fir = new StreamingFir(h);
        int delay = fir.GroupDelay;
        var y = new float[n + delay];

        // stream the signal in `block`-sized chunks, then flush the group delay with zeros
        var buf = new float[Math.Max(block, delay)];
        for (int at = 0; at < n; at += block)
        {
          int len = Math.Min(block, n - at);
          fir.Process(x.AsSpan(at, len), y.AsSpan(at, len));
        }
        Array.Clear(buf);
        fir.Process(buf.AsSpan(0, delay), y.AsSpan(n, delay));

        for (int i = 0; i < n; i++)
          y[i + delay].Should().BeApproximately(expected[i], 1e-4f,
            $"streamed output (block={block}) must equal batch 'same' output at sample {i}");
      }
    }

    [Fact]
    public void ComplexStream_MatchesConvolveSame()
    {
      var rng = new Random(11);
      int n = 2000;
      var x = new Complex32[n];
      for (int i = 0; i < n; i++)
        x[i] = new Complex32((float)(rng.NextDouble() * 2 - 1), (float)(rng.NextDouble() * 2 - 1));
      float[] h = Kernel(127);
      Complex32[] expected = LiquidFir.ConvolveSame(x, h);

      using var fir = new StreamingFirComplex(h);
      int delay = fir.GroupDelay;
      var y = new Complex32[n + delay];
      for (int at = 0; at < n; at += 300)
      {
        int len = Math.Min(300, n - at);
        fir.Process(x.AsSpan(at, len), y.AsSpan(at, len));
      }
      fir.Process(new Complex32[delay], y.AsSpan(n, delay));

      for (int i = 0; i < n; i++)
      {
        y[i + delay].Real.Should().BeApproximately(expected[i].Real, 1e-4f);
        y[i + delay].Imaginary.Should().BeApproximately(expected[i].Imaginary, 1e-4f);
      }
    }
  }
}
