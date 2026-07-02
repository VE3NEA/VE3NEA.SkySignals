using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using MathNet.Numerics;
using VE3NEA.SkyTlm.Core;
using VE3NEA.SkyTlm.Tests.Fixtures;
using Xunit;
using Xunit.Abstractions;

namespace VE3NEA.SkyTlm.Tests.Roundtrip
{
  /// <summary>
  /// Streaming roundtrip for blind FSK — deviation unknown
  /// at pipeline construction time, estimated from the burst spectrum (when a two-tone structure is
  /// visible), or gracefully falling back to the baud/4 discriminator scale (for bell-shaped MSK-like
  /// spectra such as SNIPE B / GOMspace AX.100).
  ///
  /// Detection uses the existing broad-bell template for all FSK (including blind), which works well for
  /// h ≤ 0.47 (tones within the bell's positive DC-removed region). Signals with Gaussian pulse shaping
  /// (GFSK/GMSK-like modulation of the FSK transmitter) look bell-shaped and are detected correctly.
  /// </summary>
  public class BlindFskStreamingTests
  {
    private readonly ITestOutputHelper output;
    public BlindFskStreamingTests(ITestOutputHelper o) => output = o;

    // high sample rate keeps the detection band and estimation range well clear of Nyquist
    private const double Fs = 192000;
    private const double Baud = 9600;

    /// <summary>Blind FSK params — no Deviation → IsBlind = true.</summary>
    private static SignalParams BlindFskParams() => new(Baud, Modulation.FSK, Framing.AX25G3RUH, Fs);

    /// <summary>
    /// Modulate bits as GFSK at modulation index h (dev = h·Rs/2) with BT=0.5 (standard Gaussian
    /// pulse shaping). BT=0.5 produces a bell-shaped spectrum that the broad-bell detection template
    /// captures, matching how GOMspace AX.100 "MSK" transmitters actually look on the air.
    /// </summary>
    private static Complex32[] ModulateFsk(int[] bits, double h = 0.5, double esN0Db = 30) =>
      GmskModulator.Modulate(bits, Baud, Fs, bt: 0.5, h: h, esN0Db: esN0Db, seed: 7);

    private static Complex32[] Concat(params Complex32[][] parts)
    {
      var o = new Complex32[parts.Sum(p => p.Length)];
      int n = 0;
      foreach (var p in parts) { Array.Copy(p, 0, o, n, p.Length); n += p.Length; }
      return o;
    }

    /// <summary>Push signal through a StreamingPipeline in 4096-sample blocks.</summary>
    private static List<Frame> StreamAll(Complex32[] signal, SignalParams p)
    {
      using var sp = new StreamingPipeline(p);
      var frames = new List<Frame>();
      const int block = 4096;
      for (int i = 0; i < signal.Length; i += block)
        frames.AddRange(sp.Push(signal.AsSpan(i, Math.Min(block, signal.Length - i))));
      frames.AddRange(sp.Flush());
      return frames;
    }

    [Fact]
    public void BlindFsk_GmskLikeSpectrum_DecodesWithCrcValid()
    {
      // h=0.5 (GMSK-equivalent): bell-shaped spectrum detected by the broad-bell template; baud/4
      // discriminator scale is correct; illustrates the SNIPE-B / AX.100 MSK blind-fallback path.
      var frameBytes = Ax25Tx.MakeUiFrame("CQ", "VE3NEA", "blind FSK GMSK-like h=0.5");
      var lead = new Complex32[(int)(0.6 * Fs)];   // ≥ 0.34 s detector warm-up
      var burst = ModulateFsk(Ax25Tx.OnAirBits(frameBytes, flagsBefore: 64, flagsAfter: 32), h: 0.5);
      var signal = Concat(lead, burst, lead);

      var frames = StreamAll(signal, BlindFskParams());

      output.WriteLine($"blind h=0.5: {frames.Count} frame(s), crc={frames.Select(f => f.CrcValid).FirstOrDefault()}");
      frames.Should().ContainSingle("one clean GFSK-like FSK burst must yield exactly one frame");
      frames[0].CrcValid.Should().BeTrue("baud/4 fallback must decode GMSK-equivalent blind FSK correctly");
      frames[0].Bytes.Should().Equal(frameBytes);
    }

    [Fact]
    public void BlindFsk_TwoBursts_BothDecodeCorrectly()
    {
      // two bursts with different content; both must decode via the blind path (no learned deviation
      // since the spectrum is bell-shaped and IsBlind=true goes through the baud/4 fallback each time)
      var f1 = Ax25Tx.MakeUiFrame("CQ",    "VE3NEA", "blind FSK first burst");
      var f2 = Ax25Tx.MakeUiFrame("VE3NEA", "CQ",    "blind FSK second burst");
      var lead = new Complex32[(int)(0.6 * Fs)];
      var gap = new Complex32[(int)(0.5 * Fs)];
      var burst1 = ModulateFsk(Ax25Tx.OnAirBits(f1, flagsBefore: 64, flagsAfter: 32), h: 0.5);
      var burst2 = ModulateFsk(Ax25Tx.OnAirBits(f2, flagsBefore: 64, flagsAfter: 32), h: 0.5);
      var signal = Concat(lead, burst1, gap, burst2, lead);

      var frames = StreamAll(signal, BlindFskParams());

      output.WriteLine($"two blind FSK bursts: {frames.Count} frame(s), crcs={string.Join(",", frames.Select(f => f.CrcValid))}");
      frames.Should().HaveCountGreaterThanOrEqualTo(2,
        "both blind FSK bursts must decode regardless of the baud/4-fallback path");
      frames.All(f => f.CrcValid == true).Should().BeTrue("all frames must have valid CRC");
    }

    [Fact]
    public void IsBlind_FskWithNullDeviation_ReturnsTrue()
    {
      BlindFskParams().IsBlind.Should().BeTrue("FSK with null Deviation is blind");
    }

    [Fact]
    public void IsBlind_GfskWithNullDeviation_ReturnsFalse()
    {
      new SignalParams(9600, Modulation.GFSK, Framing.AX25G3RUH, Fs).IsBlind
        .Should().BeFalse("GFSK with null Deviation uses baud/4 as default — it is NOT blind");
    }

    [Fact]
    public void IsBlind_FskWithKnownDeviation_ReturnsFalse()
    {
      new SignalParams(9600, Modulation.FSK, Framing.AX25G3RUH, Fs, Deviation: 4800).IsBlind
        .Should().BeFalse("FSK with an explicit Deviation is not blind");
    }
  }
}
