using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using MathNet.Numerics;
using Xunit;

namespace VE3NEA.SkyFM.Tests
{
  /// <summary>Closed-loop tests of the §10 transcript decoder (integration plan A3): synthetic FM bursts
  /// + a scripted engine → pause-formatted transcript lines on the recording timeline, streaming line
  /// events, and click-to-play audio spans — no file IO.</summary>
  public class SkySpeechDecoderTests
  {
    private const double Fs = 48000.0;

    private sealed class ScriptedEngine : IAsrEngine
    {
      private readonly Queue<AsrWord[]> script;
      public ScriptedEngine(params AsrWord[][] transmissions) => script = new(transmissions);
      public string Name => "scripted";
      public void Dispose() { }

      public IReadOnlyList<AsrHypothesis> Transcribe(ReadOnlySpan<float> audio, int sampleRate)
      {
        var words = script.Count > 0 ? script.Dequeue() : [];
        return words.Length == 0 ? [] : [new AsrHypothesis { Words = words, Score = 1.0 }];
      }
    }

    // words 0.3 s apart within a clip — one squelch-open interval, so one line, single-spaced
    private static AsrWord[] Words(params string[] texts)
      => texts.Select((t, i) => new AsrWord(t, 0.2 + 0.3 * i, 0.4 + 0.3 * i, 0.8f)).ToArray();

    /// <summary>Noise — carrier burst (1 kHz tone) at 1.0–2.5 s — noise — burst at 5.0–6.5 s — noise.</summary>
    private static Complex32[] TwoBursts()
    {
      var burst = FmTestSignal.Modulate(FmTestSignal.Tone(1000.0, 3000.0, 1.5, Fs), Fs);
      FmTestSignal.AddNoise(burst, 0.02f, seed: 2);

      var iq = new Complex32[(int)(8.0 * Fs)];
      FmTestSignal.AddNoise(iq, 0.5f, seed: 3);
      Array.Copy(burst, 0, iq, (int)(1.0 * Fs), burst.Length);
      Array.Copy(burst, 0, iq, (int)(5.0 * Fs), burst.Length);
      return iq;
    }

    private static void Feed(SkySpeechDecoder sd, Complex32[] iq, int block = 4800)
    {
      for (int at = 0; at < iq.Length; at += block)
        sd.Process(new ReadOnlySpan<Complex32>(iq, at, Math.Min(block, iq.Length - at)));
      sd.Flush();
    }

    [Fact]
    public void Transcript_ProducesOneLinePerSquelchInterval_OnRecordingTimeline()
    {
      // a junk word ("thank") is outside the display vocabulary and must be dropped from the line
      var engine = new ScriptedEngine(
        Words("kilo", "bravo", "two", "india", "whiskey"),
        Words("echo", "mike", "thank", "eight", "five"));
      using var sd = new SkySpeechDecoder(engine);
      Feed(sd, TwoBursts());

      // the two bursts are ~2.5 s apart — far beyond the 0.5 s merge gap — so each is its own line
      sd.Lines.Should().HaveCount(2);
      sd.Lines[0].Text.Should().Be("kilo bravo 2 india whiskey");
      sd.Lines[1].Text.Should().Be("echo mike 8 5", "the non-vocabulary word is ignored");
      sd.Pending.Should().BeNull("the last line closed on Flush");

      // the line span is the padded squelch-open interval, on the recording timeline
      sd.Lines[0].StartSeconds.Should().BeInRange(0.5, 2.0);
      sd.Lines[1].StartSeconds.Should().BeInRange(4.5, 6.0);
      sd.Lines[0].EndSeconds.Should().BeGreaterThan(sd.Lines[0].StartSeconds);
    }

    [Fact]
    public void LineCompleted_FiresMidStream_ThenTheLastLineOnFlush()
    {
      var engine = new ScriptedEngine(
        Words("kilo", "bravo", "two", "india", "whiskey"),
        Words("echo", "mike", "eight", "five"));
      var completed = new List<(int Index, string Text)>();
      int completedBeforeFlush = -1;

      using var sd = new SkySpeechDecoder(engine);
      sd.LineCompleted += (line, i) => completed.Add((i, line.Text));

      var iq = TwoBursts();
      for (int at = 0; at < iq.Length; at += 4800)
        sd.Process(new ReadOnlySpan<Complex32>(iq, at, Math.Min(4800, iq.Length - at)));
      completedBeforeFlush = completed.Count;   // the first line must have closed when the second burst opened
      sd.Flush();

      completedBeforeFlush.Should().Be(1, "line 0 closes when the second burst's word opens a new line, before the pass ends");
      completed.Select(c => c.Index).Should().Equal(0, 1);
      completed[0].Text.Should().Be("kilo bravo 2 india whiskey");
      completed[1].Text.Should().Be("echo mike 8 5");
    }

    [Fact]
    public void GetAudio_ReturnsNormalizedSpanForALine()
    {
      var engine = new ScriptedEngine(Words("kilo", "bravo", "two", "india", "whiskey"), []);
      using var sd = new SkySpeechDecoder(engine);
      Feed(sd, TwoBursts());

      var line = sd.Lines[0];
      var audio = sd.GetAudio(line.StartSeconds, line.EndSeconds);

      audio.Should().NotBeEmpty();
      audio.Length.Should().BeCloseTo(
        (int)((line.EndSeconds - line.StartSeconds) * sd.OutputSampleRate), 32);
      audio.Max(Math.Abs).Should().BeApproximately(0.7f, 0.01f, "click-to-play audio is true-peak normalized");
      sd.GetAudio(100.0, 101.0).Should().BeEmpty("a range past the retained audio yields nothing");
    }

    [Fact]
    public void NoSpeech_ProducesQuestionMarkLines_OnePerSquelchInterval()
    {
      var engine = new ScriptedEngine([], []);   // engine hears nothing in either burst
      using var sd = new SkySpeechDecoder(engine);
      Feed(sd, TwoBursts());

      // a squelch-open interval with no recognized text is still a line — it prints "???"
      sd.Lines.Should().HaveCount(2);
      sd.Lines.Should().OnlyContain(l => l.Text == FmTranscriptBuilder.NoText);
      sd.Pending.Should().BeNull();
    }
  }
}
