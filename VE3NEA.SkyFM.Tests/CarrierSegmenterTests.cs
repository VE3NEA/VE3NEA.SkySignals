using System;
using FluentAssertions;
using Xunit;

namespace VE3NEA.SkyFM.Tests
{
  /// <summary>Unit tests of the gate-track → transmissions logic: merge across speech gaps, drop noise
  /// blips, pad the edges.</summary>
  public class CarrierSegmenterTests
  {
    private const double Fs = 48000.0;

    private static byte[] Gates(double seconds, params (double S, double E)[] openSpans)
    {
      var g = new byte[(int)(seconds * Fs)];
      foreach (var (s, e) in openSpans)
        for (int i = (int)(s * Fs); i < (int)(e * Fs) && i < g.Length; i++) g[i] = 1;
      return g;
    }

    private static FmTransmission[] Run(byte[] gates, FmDecodeOptions? options = null)
    {
      var seg = new CarrierSegmenter(options ?? new FmDecodeOptions());
      // feed in uneven blocks to exercise the streaming state
      int at = 0;
      foreach (int len in new[] { 1000, 30000, gates.Length })
      {
        int n = Math.Min(len, gates.Length - at);
        seg.Process(gates.AsSpan(at, n));
        at += n;
        if (at >= gates.Length) break;
      }
      seg.Flush();
      return [.. seg.Transmissions];
    }

    [Fact]
    public void MergesAcrossShortGap_AndPads()
    {
      // a 100 ms fade inside one over must not split it
      var t = Run(Gates(3.0, (0.5, 1.0), (1.1, 1.6)));
      t.Should().HaveCount(1);
      t[0].StartSeconds.Should().BeApproximately(0.35, 0.01, "0.15 s pad before the 0.5 s open");
      t[0].EndSeconds.Should().BeApproximately(1.75, 0.01, "0.15 s pad after the 1.6 s close");
    }

    [Fact]
    public void DropsShortBlip()
    {
      Run(Gates(3.0, (2.0, 2.1))).Should().BeEmpty("a 100 ms opening is a noise blip, below SegmentMinS");
    }

    [Fact]
    public void SplitsOnLongGap()
    {
      var t = Run(Gates(4.0, (0.5, 1.5), (2.5, 3.5)));
      t.Should().HaveCount(2, "a 1 s closed gap separates two transmissions");
      t[0].EndSeconds.Should().BeLessThan(t[1].StartSeconds);
    }

    [Fact]
    public void ExtendsPreviousWhenPaddingOverlaps()
    {
      // gap of 0.4 s: beyond the 0.35 s merge gap, but with 0.25 s pads the padded segments would
      // overlap — the second must extend the first instead (with the default 0.15 s pad this branch is
      // unreachable: any bridgeable gap is already merged)
      var t = Run(Gates(4.0, (0.5, 1.5), (1.9, 2.9)), new FmDecodeOptions { SegmentPadS = 0.25 });
      t.Should().HaveCount(1);
      t[0].StartSeconds.Should().BeApproximately(0.25, 0.01);
      t[0].EndSeconds.Should().BeApproximately(3.15, 0.01);
    }

    [Fact]
    public void FlushClosesAnOpenSpan()
    {
      var t = Run(Gates(2.0, (1.5, 2.0)));
      t.Should().HaveCount(1, "a transmission still keyed at end-of-stream must be finalized by Flush");
      t[0].EndSeconds.Should().BeApproximately(2.15, 0.01);
    }
  }
}
