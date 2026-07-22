using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using MathNet.Numerics;
using Xunit;

namespace VE3NEA.SkyFM.Tests
{
  /// <summary>Closed-loop tests of the complete core pipeline (plan §6, A5): synthetic FM bursts +
  /// a scripted engine → fused, policy-gated candidates, with no file IO anywhere.</summary>
  public class SkyFmDecoderTests
  {
    private const double Fs = 48000.0;

    /// <summary>Depth influence disabled: both floors at 1 make every weight 1, so assertions do not
    /// depend on the exact synthetic quieting depth.</summary>
    private static readonly SkyFmOptions NoDepth = new()
    {
      Depth = new DepthConfidence { MinWeight = 1f, GridMinWeight = 1f }
    };

    private sealed class ScriptedEngine : IAsrEngine
    {
      private readonly Queue<AsrWord[]> script;
      public readonly List<(float Peak, int Rate)> Calls = new();

      public ScriptedEngine(params AsrWord[][] transmissions) => script = new(transmissions);
      public string Name => "scripted";
      public void Dispose() { }

      public IReadOnlyList<AsrHypothesis> Transcribe(ReadOnlySpan<float> audio, int sampleRate)
      {
        float peak = 0f;
        foreach (float v in audio) peak = Math.Max(peak, Math.Abs(v));
        Calls.Add((peak, sampleRate));
        var words = script.Count > 0 ? script.Dequeue() : [];
        return words.Length == 0 ? [] : [new AsrHypothesis { Words = words, Score = 1.0 }];
      }
    }

    private static AsrWord[] Words(float conf, params string[] texts)
      => texts.Select((t, i) => new AsrWord(t, 0.2 + 0.3 * i, 0.4 + 0.3 * i, conf)).ToArray();

    /// <summary>Noise — carrier burst (1 kHz tone) at 1.0–2.5 s — noise — burst at 5.0–6.5 s — noise.</summary>
    private static Complex32[] TwoBursts()
    {
      var audio = FmTestSignal.Tone(1000.0, 3000.0, 1.5, Fs);
      var burst = FmTestSignal.Modulate(audio, Fs);
      FmTestSignal.AddNoise(burst, 0.02f, seed: 2);

      var iq = new Complex32[(int)(8.0 * Fs)];
      var noise = new Complex32[iq.Length];
      FmTestSignal.AddNoise(noise, 0.5f, seed: 3);
      Array.Copy(noise, iq, iq.Length);
      Array.Copy(burst, 0, iq, (int)(1.0 * Fs), burst.Length);
      Array.Copy(burst, 0, iq, (int)(5.0 * Fs), burst.Length);
      return iq;
    }

    [Fact]
    public void RepeatedCallsign_FusesAcrossTransmissions_AndEmits()
    {
      var kb2iw = new[] { "kilo", "bravo", "two", "india", "whiskey" };
      var engine = new ScriptedEngine(Words(0.8f, kb2iw), Words(0.8f, kb2iw));
      var res = SkyFmDecoder.Decode(TwoBursts(), [engine], NoDepth);

      res.Fm.Transmissions.Should().HaveCount(2);
      engine.Calls.Should().HaveCount(2, "one engine call per transmission");
      engine.Calls.Should().OnlyContain(c => c.Rate == 16000);
      engine.Calls.Should().OnlyContain(c => Math.Abs(c.Peak - 0.7f) < 0.01f,
        "per-transmission audio must be true-peak normalized like the calibration clips");

      var c = res.Candidates.Should().ContainSingle().Which;
      c.Kind.Should().Be(CandidateKind.Callsign);
      c.Text.Should().Be("KB2IW");
      c.Confidence.Should().BeApproximately(1f - 0.2f * 0.2f, 1e-3f,
        "two independent mentions soft-OR past the emit gate");
    }

    [Fact]
    public void WordTimes_LandOnTheRecordingTimeline_AndPolicyGates()
    {
      var engine = new ScriptedEngine(
        Words(0.8f, "kilo", "bravo", "two", "india", "whiskey"),
        Words(0.8f, "echo", "mike", "eight", "five"));
      var res = SkyFmDecoder.Decode(TwoBursts(), [engine], NoDepth);

      res.Fused.Should().HaveCount(2);
      var call = res.Fused.Single(c => c.Kind == CandidateKind.Callsign);
      var grid = res.Fused.Single(c => c.Kind == CandidateKind.Grid);
      call.StartSeconds.Should().BeInRange(1.0, 1.5, "clip-relative word times shift by the segment start");
      grid.StartSeconds.Should().BeInRange(5.0, 5.5);

      // the policy keeps the grid (0.8 ≥ 0.75) but abstains on the uncorroborated callsign: 0.8 is
      // below both the emit gate and the per-char partial gate
      res.Candidates.Should().ContainSingle().Which.Text.Should().Be("EM85");
    }

    [Fact]
    public void Streaming_MatchesBatch_IsLive_AndBoundsTheVoiceBuffer()
    {
      var iq = TwoBursts();
      var kb2iw = new[] { "kilo", "bravo", "two", "india", "whiskey" };
      var em85 = new[] { "echo", "mike", "eight", "five" };

      var batch = SkyFmDecoder.Decode(iq, [new ScriptedEngine(Words(0.8f, kb2iw), Words(0.8f, em85))], NoDepth);

      using var sd = new SkyFmStreamingDecoder([new ScriptedEngine(Words(0.8f, kb2iw), Words(0.8f, em85))], NoDepth);
      int maxBuffered = 0, block = 4800;
      bool liveAfterFirstBurst = false;
      for (int at = 0; at < iq.Length; at += block)
      {
        sd.Process(new ReadOnlySpan<Complex32>(iq, at, Math.Min(block, iq.Length - at)));
        maxBuffered = Math.Max(maxBuffered, sd.BufferedVoiceSamples);
        if (at / Fs >= 4.0 && sd.Fused.Count > 0) liveAfterFirstBurst = true;
      }
      sd.Flush();

      liveAfterFirstBurst.Should().BeTrue("the first burst's candidate must appear before the pass ends");
      sd.Transmissions.Should().Equal(batch.Fm.Transmissions);
      sd.Fused.Should().BeEquivalentTo(batch.Fused, "streaming and batch are one pipeline");
      sd.Candidates.Should().BeEquivalentTo(batch.Candidates);
      maxBuffered.Should().BeLessThan(3 * 16000,
        "the voice buffer must stay bounded by the pending-transmission horizon, never the whole pass");
    }

    [Fact]
    public void QuietingDepth_ReachesTheGrammarLayer()
    {
      var kb2iw = new[] { "kilo", "bravo", "two", "india", "whiskey" };
      var engine = new ScriptedEngine(Words(0.9f, kb2iw), Words(0.9f, kb2iw));

      // an absurd ramp that demotes even the strong synthetic bursts to near zero: candidates can
      // only vanish if the per-transmission depth actually flows into the weighting
      var crush = new SkyFmOptions
      {
        Depth = new DepthConfidence { ShallowDb = 999, FullDb = 1000, MinWeight = 0.01f }
      };
      var res = SkyFmDecoder.Decode(TwoBursts(), [engine], crush);

      res.Fm.Transmissions.Should().OnlyContain(t => t.QuietingDepthDb > 10.0,
        "the strong synthetic carrier quiets the broadband noise deeply");
      res.Candidates.Should().BeEmpty();
      res.Fused.Single().Confidence.Should().BeLessThan(0.05f);
    }
  }
}
