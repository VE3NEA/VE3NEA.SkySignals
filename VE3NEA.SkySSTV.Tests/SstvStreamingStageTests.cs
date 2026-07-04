using System;
using System.Collections.Generic;
using FluentAssertions;
using MathNet.Numerics;
using Xunit;
using Xunit.Abstractions;

namespace VE3NEA.SkySSTV.Tests
{
  /// <summary>
  /// P7.5 streaming-refactor equivalence tests: each streaming stage must reproduce its batch reference
  /// (the whole-array methods in <see cref="SstvDecoder"/>) sample-for-sample, independent of how the
  /// stream is split into blocks — the batch methods become thin wrappers over these stages, so any
  /// divergence here is a silent batch-vs-streaming fork (the §6.0 A/B failure pattern).
  /// </summary>
  public class SstvStreamingStageTests
  {
    private const double Fs = 48000.0;
    private readonly ITestOutputHelper output;
    public SstvStreamingStageTests(ITestOutputHelper o) => output = o;

    /// <summary>FM-ish random IQ with injected envelope fades, so the blanker actually gates runs.</summary>
    private static Complex32[] FadedIq(int n, int seed)
    {
      var rng = new Random(seed);
      var iq = new Complex32[n];
      double ph = 0;
      for (int i = 0; i < n; i++)
      {
        ph += 0.2 * (rng.NextDouble() - 0.5);
        iq[i] = new Complex32((float)Math.Cos(ph), (float)Math.Sin(ph));
      }
      for (int k = 0; k < 30; k++)
      {
        int at = rng.Next(n - 400);
        int len = 3 + rng.Next(400);                         // some fades exceed the 20 ms gap bound
        for (int i = at; i < at + len; i++) iq[i] *= 0.01f;
      }
      return iq;
    }

    [Theory]
    [InlineData(997)]
    [InlineData(12000)]
    [InlineData(int.MaxValue)]
    public void StreamingDiscriminator_MatchesBatch(int block)
    {
      int n = (int)(3 * Fs);
      var iq = FadedIq(n, 5);
      var o = new SstvDecodeOptions();                        // defaults: channel FIR + blanker 0.5
      double[] expected = SstvDecoder.Discriminator(iq, o);

      var got = new List<double>(n);
      using var sd = new SstvStreamingDiscriminator(o, o.ChannelBwHz);
      for (int at = 0; at < n; at += Math.Min(block, n))
      {
        int len = Math.Min(Math.Min(block, n) , n - at);
        foreach (double v in sd.Process(iq.AsSpan(at, len))) got.Add(v);
      }
      foreach (double v in sd.Flush()) got.Add(v);

      got.Count.Should().Be(expected.Length, "the stream must emit exactly the batch sample count");
      int diffs = 0;
      for (int i = 0; i < n; i++)
        if (Math.Abs(got[i] - expected[i]) > 1e-9) diffs++;
      output.WriteLine($"block={block}: {diffs} samples differ beyond 1e-9");
      diffs.Should().Be(0, "streaming disc must equal the batch chain sample-for-sample");
    }

    [Theory]
    [InlineData(997)]
    [InlineData(48000)]
    public void StreamingBrightness_MatchesBatch(int block)
    {
      // brightness runs on the discriminated audio; a random-walk disc stream exercises the NCO + LPF
      var rng = new Random(21);
      int n = (int)(3 * Fs);
      var disc = new double[n];
      double v = 1900;
      for (int i = 0; i < n; i++) { v += 30 * (rng.NextDouble() - 0.5); disc[i] = v; }

      var o = new SstvDecodeOptions();
      double[] expected = SstvDecoder.Brightness(disc, Fs, o);

      var got = new List<double>(n);
      using var sb = new SstvStreamingBrightness(o);
      for (int at = 0; at < n; at += block)
      {
        int len = Math.Min(block, n - at);
        foreach (double s in sb.Process(disc.AsSpan(at, len))) got.Add(s);
      }
      foreach (double s in sb.Flush()) got.Add(s);

      got.Count.Should().Be(expected.Length, "the stream must emit exactly the batch sample count");
      int diffs = 0;
      for (int i = 0; i < n; i++)
        if (Math.Abs(got[i] - expected[i]) > 1e-9) diffs++;
      output.WriteLine($"block={block}: {diffs} samples differ beyond 1e-9");
      diffs.Should().Be(0, "streaming brightness must equal the batch chain sample-for-sample");
    }

    [Fact]
    public void DetectionChain_BlockFed_MatchesWholeArray()
    {
      // the detection chain must not care how the sync stream is split into pushes: a real-time feed
      // (odd small blocks) must produce exactly the trains the whole-array wrapper produces
      var spec = SstvModes.Get(SstvMode.Robot36);
      var src = new RgbImage(spec.Width, spec.Height);
      for (int y = 0; y < spec.Height; y++)
        for (int x = 0; x < spec.Width; x++)
          src.Set(x, y, (byte)(x * 255 / spec.Width), (byte)(y * 255 / spec.Height), 128);
      var iq = SstvEncoder.Encode(src, SstvMode.Robot36, new SstvEncoderOptions { IncludeVis = true });

      var o = new SstvDecodeOptions();
      double[] sync = SstvDecoder.SyncAudio(SstvDecoder.Discriminator(iq, o), Fs, o);
      var hits = SstvVisDetector.DetectAll(sync, Fs);

      var whole = SstvDecoder.ExtractTrains(sync, Fs, hits);

      var chain = new SstvDetectionChain(Fs);
      foreach (var hit in hits)
        if (hit.Found && hit.Mode is SstvMode vm) chain.SeedVis(vm, hit.HeaderEndSample);
      var rng = new Random(3);
      int at = 0;
      while (at < sync.Length)
      {
        int len = Math.Min(1 + rng.Next(20000), sync.Length - at);
        chain.Process(sync.AsSpan(at, len));
        at += len;
      }
      chain.Finish();
      var streamed = chain.Extractor;

      List<SstvPulseTrain> Promoted(SstvPulseTrainExtractor e)
      {
        var list = new List<SstvPulseTrain>();
        foreach (var t in e.Trains)
          if (t.State == SstvTrainState.Active || t.State == SstvTrainState.Retired) list.Add(t);
        return list;
      }
      var a = Promoted(whole);
      var b = Promoted(streamed);
      output.WriteLine($"whole: {a.Count} trains {whole.Lines.Count} lines; " +
        $"streamed: {b.Count} trains {streamed.Lines.Count} lines");
      b.Count.Should().Be(a.Count, "block splits must not change the promoted trains");
      for (int i = 0; i < a.Count; i++)
      {
        b[i].Format.Should().Be(a[i].Format);
        b[i].PulseCnt.Should().Be(a[i].PulseCnt);
        b[i].Regr.GetPulseTime(0).Should().BeApproximately(a[i].Regr.GetPulseTime(0), 1.0);
      }
      streamed.Lines.Count.Should().Be(whole.Lines.Count);
    }

    [Fact]
    public void StreamingDiscriminator_DeEmphasis_MatchesBatch()
    {
      int n = (int)(1 * Fs);
      var iq = FadedIq(n, 9);
      var o = new SstvDecodeOptions { DeEmphasisUs = 750.0 };
      double[] expected = SstvDecoder.Discriminator(iq, o);

      var got = new List<double>(n);
      using var sd = new SstvStreamingDiscriminator(o, o.ChannelBwHz);
      foreach (double v in sd.Process(iq)) got.Add(v);
      foreach (double v in sd.Flush()) got.Add(v);

      got.Count.Should().Be(expected.Length);
      for (int i = 0; i < n; i++)
        got[i].Should().BeApproximately(expected[i], 1e-9);
    }
  }
}
