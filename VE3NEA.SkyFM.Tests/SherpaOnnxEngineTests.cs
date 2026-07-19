using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace VE3NEA.SkyFM.Tests
{
  /// <summary>Fast tests of the sherpa-onnx host plumbing that need no model: BPE token → word
  /// grouping with time bounds. Plus a manual smoke decode of the model's bundled LibriSpeech WAV.</summary>
  public class SherpaOnnxEngineTests
  {
    private readonly ITestOutputHelper output;

    public SherpaOnnxEngineTests(ITestOutputHelper o) => output = o;

    [ManualFact("probe: per-clip decode of the directory in SKYFM_PROBE_DIR — isolates native crashes")]
    public void Probe_DecodeClipsOneByOne()
    {
      string dir = Environment.GetEnvironmentVariable("SKYFM_PROBE_DIR")!;
      using var engine = SherpaOnnxEngine.Hotwords();
      foreach (string path in Directory.GetFiles(dir, "seg*.wav").OrderBy(p => p))
      {
        var (samples, rate) = Wav16.Read(path);
        output.WriteLine($"{Path.GetFileName(path)}  {samples.Length / (double)rate:0.00}s");
        var hyps = engine.Transcribe(samples, rate);
        output.WriteLine($"  -> {(hyps.Count == 0 ? "(empty)" : string.Join(" ", hyps[0].Words.Select(w => w.Text)))}");
      }
    }

    [ManualFact("smoke: decode the model's own LibriSpeech test WAV — validates model load + config")]
    public void Smoke_DecodesBundledTestWav()
    {
      using var engine = SherpaOnnxEngine.Hotwords();
      var (samples, rate) = Wav16.Read(RepoFiles.Find(Path.Combine("asr-spike", "sherpa",
        "sherpa-onnx-zipformer-gigaspeech-2023-12-12", "test_wavs", "1089-134686-0001.wav")));
      var hyps = engine.Transcribe(samples, rate);

      hyps.Should().NotBeEmpty();
      foreach (var w in hyps[0].Words) output.WriteLine($"{w.StartSeconds,6:0.00}s {w.Text}");
      hyps[0].Words.Count.Should().BeGreaterThan(5);
    }

    [ManualFact("integration Phase D: load the int8 pack from the DEPLOYED SkyRoof ASR_models folder (what " +
      "the download command installs and TelemetryPanel.EnsureFmEngine loads) and decode the test wav — " +
      "proves the hosted/extracted pack is complete and loadable via the production ModelDirectory path")]
    public void DeployedPack_LoadsAndDecodes()
    {
      string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Afreet", "Products", "SkyRoof", "ASR_models");
      SherpaModelPack.IsPresent(dir).Should().BeTrue($"the deployed int8 pack must be under {dir}");

      using var engine = SherpaOnnxEngine.Hotwords(int8: true, modelDir: dir);
      var (samples, rate) = Wav16.Read(RepoFiles.Find(Path.Combine("asr-spike", "sherpa",
        "sherpa-onnx-zipformer-gigaspeech-2023-12-12", "test_wavs", "1089-134686-0001.wav")));
      var hyps = engine.Transcribe(samples, rate);

      hyps.Should().NotBeEmpty();
      hyps[0].Words.Count.Should().BeGreaterThan(5);
      output.WriteLine($"decoded {hyps[0].Words.Count} words from the deployed pack: " +
        string.Join(" ", hyps[0].Words.Select(w => w.Text)));
    }

    [Fact]
    public void WordsFromTokens_GroupsBpePiecesIntoWords()
    {
      // the C API renders sentencepiece "▁" as a leading space; both forms must group
      foreach (string mark in new[] { " ", "▁" })
      {
        string[] tokens = [$"{mark}A", "L", "P", "HA", $"{mark}B", "RA", "VO"];
        float[] timestamps = [0.5f, 0.6f, 0.7f, 0.8f, 1.2f, 1.3f, 1.4f];

        var words = SherpaOnnxEngine.WordsFromTokens(tokens, timestamps);

        words.Should().HaveCount(2);
        words[0].Text.Should().Be("ALPHA");
        words[0].StartSeconds.Should().BeApproximately(0.5, 1e-6);
        words[0].EndSeconds.Should().BeApproximately(1.2, 1e-6);       // bounded by the next word start
        words[1].Text.Should().Be("BRAVO");
        words[1].EndSeconds.Should().BeApproximately(1.4 + 0.3, 1e-6); // trailing word gets the fixed tail
      }
    }

    [Fact]
    public void WordsFromTokens_EmptyAndUnkOnly_YieldNoWords()
    {
      SherpaOnnxEngine.WordsFromTokens([], []).Should().BeEmpty();
      SherpaOnnxEngine.WordsFromTokens(["▁<unk>"], [0.1f]).Should().BeEmpty();
    }

    [Fact]
    public void CollapseSpelled_MergesLetterAndDigitSequences()
    {
      var words = new List<AsrWord>
      {
        new("COPY", 0.0, 0.4, 0.8f),
        new("A", 1.0, 1.2, 0.8f), new("B", 1.2, 1.4, 0.8f), new("TWO", 1.4, 1.7, 0.8f),
        new("I", 1.7, 1.9, 0.8f), new("W", 1.9, 2.1, 0.8f),
        new("KILO", 2.5, 2.9, 0.8f),                         // phonetic word keeps its identity
        new("A", 5.0, 5.2, 0.8f)                             // isolated junk letter stays a separator
      };

      var collapsed = SherpaOnnxEngine.CollapseSpelled(words);

      collapsed.Select(w => w.Text).Should().Equal("COPY", "AB2IW", "KILO", "A");
      var id = collapsed[1];
      id.StartSeconds.Should().BeApproximately(1.0, 1e-6);
      id.EndSeconds.Should().BeApproximately(2.1, 1e-6);
      PhoneticDecoder.ToSymbols(id.Text).Should().Be("AB2IW");
      PhoneticDecoder.ToSymbols(collapsed[3].Text).Should().BeEmpty();
    }

    [Fact]
    public void CollapseSpelled_LongPauseSplitsSequences()
    {
      var words = new List<AsrWord>
      {
        new("K", 1.0, 1.2, 0.8f), new("TWO", 1.2, 1.5, 0.8f),
        new("H", 3.5, 3.7, 0.8f), new("B", 3.7, 3.9, 0.8f)   // 2 s pause: a new spelled sequence
      };

      SherpaOnnxEngine.CollapseSpelled(words).Select(w => w.Text).Should().Equal("K2", "HB");
    }

    [Fact]
    public void CollapseSpelled_NumberWords_JoinSpelledSequences()
    {
      var grid = new List<AsrWord>
      {
        new("F", 1.0, 1.2, 0.8f), new("N", 1.2, 1.4, 0.8f),
        new("TWENTY", 1.4, 1.8, 0.8f), new("TWO", 1.8, 2.1, 0.8f)
      };
      SherpaOnnxEngine.CollapseSpelled(grid).Select(w => w.Text).Should().Equal("FN22");

      var teens = new List<AsrWord>
      {
        new("F", 1.0, 1.2, 0.8f), new("M", 1.2, 1.4, 0.8f), new("SEVENTEEN", 1.4, 2.0, 0.8f)
      };
      SherpaOnnxEngine.CollapseSpelled(teens).Select(w => w.Text).Should().Equal("FM17");

      // a tens word with no units digit stands for the round number, isolated it stays a separator
      var bare = new List<AsrWord>
      {
        new("E", 1.0, 1.2, 0.8f), new("M", 1.2, 1.4, 0.8f), new("EIGHTY", 1.4, 1.8, 0.8f),
        new("OVER", 2.2, 2.6, 0.8f), new("TWENTY", 3.0, 3.4, 0.8f)
      };
      SherpaOnnxEngine.CollapseSpelled(bare).Select(w => w.Text).Should().Equal("EM80", "OVER", "TWENTY");

      // a merged number pair survives alone: "ECHO MIKE EIGHTY FIVE" keeps its digits for the run
      var pair = new List<AsrWord>
      {
        new("ECHO", 0.5, 0.9, 0.8f), new("MIKE", 0.9, 1.3, 0.8f),
        new("EIGHTY", 1.4, 1.8, 0.8f), new("FIVE", 1.8, 2.1, 0.8f)
      };
      SherpaOnnxEngine.CollapseSpelled(pair).Select(w => w.Text).Should().Equal("ECHO", "MIKE", "85");
    }
  }
}
