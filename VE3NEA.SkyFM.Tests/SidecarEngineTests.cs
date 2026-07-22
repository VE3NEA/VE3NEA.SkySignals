using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using Xunit;

namespace VE3NEA.SkyFM.Tests
{
  /// <summary>Fast tests of the engine-host plumbing that need no Python or model: the sidecar reply
  /// parser, the grammar vocabulary, the clip-name time offset, and the WAV read-back the clip
  /// transcription depends on.</summary>
  public class SidecarEngineTests
  {
    [Fact]
    public void ParseReply_Words_BecomeSingleHypothesis()
    {
      var hyps = SidecarEngine.ParseReply(
        """{"words": [{"w": " Kilo", "s": 0.5, "e": 0.9, "p": 0.87}, {"w": " Bravo", "s": 0.9, "e": 1.4, "p": 0.91}], "score": -0.35}""");

      hyps.Should().HaveCount(1);
      hyps[0].Score.Should().BeApproximately(-0.35, 1e-9);
      hyps[0].Words.Should().HaveCount(2);
      hyps[0].Words[0].Should().Be(new AsrWord(" Kilo", 0.5, 0.9, 0.87f));
      hyps[0].Words[1].Text.Should().Be(" Bravo");
    }

    [Fact]
    public void ParseReply_NoWords_IsEmpty()
      => SidecarEngine.ParseReply("""{"words": [], "score": 0.0}""").Should().BeEmpty();

    [Fact]
    public void ParseReply_Error_Throws()
    {
      var act = () => SidecarEngine.ParseReply("""{"error": "no such file"}""");
      act.Should().Throw<InvalidOperationException>().WithMessage("*no such file*");
    }

    [Fact]
    public void VocabularyWords_CoverAlphabetAndDigits()
    {
      var vocab = PhoneticDecoder.VocabularyWords.ToList();
      vocab.Should().OnlyHaveUniqueItems();
      vocab.Should().Contain(["alpha", "zulu", "zero", "niner", "fife"]);
      // every vocabulary word must map to a symbol, or the grammar would emit separators only
      foreach (string w in vocab) PhoneticDecoder.ToSymbols(w).Should().NotBeEmpty(w);
    }

    [Fact]
    public void ClipStartSeconds_ParsesHarnessClipNames()
    {
      HeadlessRunner.ClipStartSeconds(@"C:\x\seg07_147.3s.wav").Should().BeApproximately(147.3, 1e-9);
      HeadlessRunner.ClipStartSeconds("seg00_000.0s.wav").Should().Be(0.0);
    }

    [Fact]
    public void Wav16_RoundTrip_PreservesShapeAndRate()
    {
      var samples = new float[320];
      for (int i = 0; i < samples.Length; i++) samples[i] = MathF.Sin(2 * MathF.PI * 440 * i / 16000f);
      string path = Path.Combine(Path.GetTempPath(), $"skyfm_wavtest_{Guid.NewGuid():N}.wav");
      try
      {
        Wav16.Write(path, samples, 16000);
        var (read, rate) = Wav16.Read(path);

        rate.Should().Be(16000);
        read.Should().HaveCount(samples.Length);
        // writer normalizes the true peak to 0.7 of full scale; the waveform shape must survive
        int peakIn = 0, peakOut = 0;
        for (int i = 1; i < samples.Length; i++)
        {
          if (MathF.Abs(samples[i]) > MathF.Abs(samples[peakIn])) peakIn = i;
          if (MathF.Abs(read[i]) > MathF.Abs(read[peakOut])) peakOut = i;
        }
        peakOut.Should().Be(peakIn);
        MathF.Abs(read[peakOut]).Should().BeApproximately(0.7f, 0.01f);
      }
      finally { File.Delete(path); }
    }
  }
}
