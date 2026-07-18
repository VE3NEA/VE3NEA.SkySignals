using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SherpaOnnx;

namespace VE3NEA.SkyFM.Tests
{
  /// <summary>
  /// The Pass-B production favourite (plan §5.3 contender 1): a sherpa-onnx Zipformer transducer
  /// (GigaSpeech) with <b>hotword contextual biasing</b> toward the phonetic vocabulary — the
  /// ATCO2-proven decoding-time constraint (G7), running natively in C# with no Python sidecar.
  /// Hotwords require <c>modified_beam_search</c>; the biasing strength is a scalar to tune (§13:
  /// too strong hallucinates phonetic words from noise, too weak does nothing).
  /// <para>Bake-off caveat found here: the sherpa-onnx C API returns tokens + timestamps but <b>no
  /// per-token posteriors</b>, so words carry <see cref="PlaceholderConfidence"/> — the §5.3
  /// disqualification clause ("engines that cannot expose token confidence") currently bites the
  /// favourite; production adoption needs posteriors exposed upstream or confidence from an external
  /// scorer.</para>
  /// </summary>
  public sealed class SherpaOnnxEngine : IAsrEngine
  {
    /// <summary>Stand-in word confidence while the C API exposes no posteriors (see class remarks).</summary>
    public const float PlaceholderConfidence = 0.80f;

    private const string ModelDir = "sherpa-onnx-zipformer-gigaspeech-2023-12-12";
    /// <summary>End of the last word in a clip when no next-token timestamp bounds it.</summary>
    private const double TrailingWordSeconds = 0.3;
    /// <summary>A pause longer than this between spelled tokens ends the spelled sequence.</summary>
    private const double SpellingGapSeconds = 1.0;
    /// <summary>Decode window for long clips — the offline transducer is an utterance model.</summary>
    private const double MaxChunkSeconds = 25.0;

    private readonly OfflineRecognizer recognizer;
    private readonly string hotwordsFile;

    public string Name { get; }

    /// <summary>Biased decode: hotwords = the phonetic vocabulary, uppercased to match the GigaSpeech
    /// BPE. <paramref name="hotwordsScore"/> is the per-token boost — sherpa ships 1.5, but the §13
    /// corpus sweep picked 2.5 (callsign recall nearly doubles, grids hold; 3.0 starts trading grid
    /// recall away in the hybrid).</summary>
    public static SherpaOnnxEngine Hotwords(float hotwordsScore = 2.5f)
      => new($"sherpa zipformer hotwords {hotwordsScore:0.0}", hotwordsScore);

    /// <summary>Unbiased control — same model and beam search, no hotwords. Measures what the biasing
    /// itself buys (the ATCO2 question).</summary>
    public static SherpaOnnxEngine Unbiased() => new("sherpa zipformer unbiased", null);

    private SherpaOnnxEngine(string name, float? hotwordsScore)
    {
      Name = name;
      string modelPath = Path.GetDirectoryName(
        RepoFiles.Find(Path.Combine("asr-spike", "sherpa", ModelDir, "tokens.txt")))!;

      var config = new OfflineRecognizerConfig();
      config.FeatConfig.SampleRate = 16000;
      config.ModelConfig.Transducer.Encoder = Path.Combine(modelPath, "encoder-epoch-30-avg-1.onnx");
      config.ModelConfig.Transducer.Decoder = Path.Combine(modelPath, "decoder-epoch-30-avg-1.onnx");
      config.ModelConfig.Transducer.Joiner = Path.Combine(modelPath, "joiner-epoch-30-avg-1.onnx");
      config.ModelConfig.Tokens = Path.Combine(modelPath, "tokens.txt");
      config.ModelConfig.NumThreads = 4;
      config.DecodingMethod = "modified_beam_search";

      if (hotwordsScore.HasValue)
      {
        config.ModelConfig.ModelingUnit = "bpe";
        config.ModelConfig.BpeVocab = Path.Combine(modelPath, "bpe.vocab");
        hotwordsFile = Path.Combine(Path.GetTempPath(), $"skyfm_hotwords_{Guid.NewGuid():N}.txt");
        File.WriteAllLines(hotwordsFile,
          PhoneticDecoder.VocabularyWords.Select(w => w.ToUpperInvariant()));
        config.HotwordsFile = hotwordsFile;
        config.HotwordsScore = hotwordsScore.Value;
      }
      else hotwordsFile = "";

      recognizer = new OfflineRecognizer(config);
    }

    public IReadOnlyList<AsrHypothesis> Transcribe(ReadOnlySpan<float> audio, int sampleRate)
    {
      // the offline zipformer is an utterance model — a 78 s carrier segment crashes the native
      // decoder (SEHException), so long clips are decoded in fixed windows (a word straddling a cut
      // may be lost; acceptable for the bake-off)
      int chunk = (int)(MaxChunkSeconds * sampleRate);
      var words = new List<AsrWord>();
      for (int at = 0; at < audio.Length; at += chunk)
      {
        double offset = at / (double)sampleRate;
        using var stream = recognizer.CreateStream();
        stream.AcceptWaveform(sampleRate, audio.Slice(at, Math.Min(chunk, audio.Length - at)).ToArray());
        recognizer.Decode(stream);
        var result = stream.Result;
        foreach (var w in WordsFromTokens(result.Tokens, result.Timestamps))
          words.Add(new AsrWord(w.Text, w.StartSeconds + offset, w.EndSeconds + offset, w.Confidence));
      }

      words = CollapseSpelled(words);
      if (words.Count == 0) return Array.Empty<AsrHypothesis>();
      return new[] { new AsrHypothesis { Words = words, Score = PlaceholderConfidence } };
    }

    /// <summary>Groups BPE tokens (" A","L","P","HA" → "ALPHA") into words with time bounds: a token
    /// starting with a space (the C API renders sentencepiece "▁" as " ") or a raw "▁" opens a new
    /// word; a word ends where the next token starts.</summary>
    public static List<AsrWord> WordsFromTokens(string[] tokens, float[] timestamps)
    {
      var words = new List<AsrWord>();
      var text = new System.Text.StringBuilder();
      int first = 0;

      void Emit(int next)
      {
        if (text.Length == 0) return;
        double start = first < timestamps.Length ? timestamps[first] : 0;
        double end = next < timestamps.Length ? timestamps[next]
          : (next - 1 < timestamps.Length ? timestamps[next - 1] : start) + TrailingWordSeconds;
        words.Add(new AsrWord(text.ToString(), start, end, PlaceholderConfidence));
        text.Clear();
      }

      for (int i = 0; i < tokens.Length; i++)
      {
        string t = tokens[i];
        if (t.StartsWith(' ') || t.StartsWith('▁'))
        {
          Emit(i);
          first = i;
          t = t[1..];
        }
        if (t != "<unk>") text.Append(t);
      }
      Emit(tokens.Length);
      return words;
    }

    /// <summary>Engine-output adapter: GigaSpeech decodes spelled callsigns as single uppercase letter
    /// tokens ("A B TWO I W") and numbers as number words ("TWENTY TWO"), which the production
    /// <see cref="PhoneticDecoder"/> deliberately treats as separators (the spike's whisper precision
    /// trap, §5.4). Merge each gap-bounded sequence of two or more spelled tokens — a single letter,
    /// a digit word, a digit string, or a teens/tens number word ("F N TWENTY TWO" → "FN22") — into
    /// one collapsed fragment, the same form Whisper emits natively; isolated letters (the engine's
    /// junk "A"/"I" on noise) stay separators.</summary>
    public static List<AsrWord> CollapseSpelled(List<AsrWord> words)
    {
      var result = new List<AsrWord>(words.Count);
      var group = new List<(AsrWord Word, string Piece, bool Merged)>();

      void Emit()
      {
        // a lone spelled token stays as-is (junk "A"/"I" must remain a separator), but a merged
        // number pair ("EIGHTY FIVE" -> "85") is already a digit string worth keeping
        if (group.Count >= 2 || (group.Count == 1 && group[0].Merged))
          result.Add(new AsrWord(string.Concat(group.Select(g => g.Piece)),
            group[0].Word.StartSeconds, group[^1].Word.EndSeconds, PlaceholderConfidence));
        else
          foreach (var g in group) result.Add(g.Word);
        group.Clear();
      }

      for (int i = 0; i < words.Count; i++)
      {
        var word = words[i];
        string piece = SpelledPiece(word.Text);
        double end = word.EndSeconds;
        bool merged = false;

        // a tens word absorbs an immediately following units digit word: "TWENTY" "TWO" -> "22"
        if (piece.Length == 0 && Tens.TryGetValue(PhoneticDecoder.Normalize(word.Text), out char tens))
        {
          piece = $"{tens}0";
          if (i + 1 < words.Count && words[i + 1].StartSeconds - end <= SpellingGapSeconds)
          {
            string unit = PhoneticDecoder.ToSymbols(words[i + 1].Text);
            if (unit.Length == 1 && unit[0] is >= '1' and <= '9')
            {
              piece = $"{tens}{unit}";
              end = words[++i].EndSeconds;
              merged = true;
            }
          }
        }

        if (piece.Length == 0)
        {
          Emit();
          result.Add(word);
        }
        else
        {
          if (group.Count > 0 && word.StartSeconds - group[^1].Word.EndSeconds > SpellingGapSeconds) Emit();
          group.Add((new AsrWord(word.Text, word.StartSeconds, end, word.Confidence), piece, merged));
        }
      }
      Emit();
      return result;
    }

    /// <summary>Spoken teens ("SEVENTEEN" → "17") and tens digits ("EIGHTY" → '8'), the number-word
    /// forms the GigaSpeech LM prefers over digit-by-digit output.</summary>
    private static readonly Dictionary<string, string> Teens = new()
    {
      ["ten"] = "10", ["eleven"] = "11", ["twelve"] = "12", ["thirteen"] = "13", ["fourteen"] = "14",
      ["fifteen"] = "15", ["sixteen"] = "16", ["seventeen"] = "17", ["eighteen"] = "18", ["nineteen"] = "19"
    };

    private static readonly Dictionary<string, char> Tens = new()
    {
      ["twenty"] = '2', ["thirty"] = '3', ["forty"] = '4', ["fifty"] = '5',
      ["sixty"] = '6', ["seventy"] = '7', ["eighty"] = '8', ["ninety"] = '9'
    };

    /// <summary>The symbol a word contributes to a spelled sequence: a lone letter, a digit word
    /// ("TWO" → "2"), a digit string ("85"), or a teens word ("SEVENTEEN" → "17"); "" for everything
    /// else (phonetic words keep their own identity, ordinary English stays a separator; tens words
    /// are handled with lookahead in <see cref="CollapseSpelled"/>).</summary>
    private static string SpelledPiece(string text)
    {
      string au = PhoneticDecoder.Alnum(text);
      if (au.Length == 1 && char.IsAsciiLetter(au[0])) return au;
      if (Teens.TryGetValue(PhoneticDecoder.Normalize(text), out string? teen)) return teen;
      string sym = PhoneticDecoder.ToSymbols(text);
      return sym.Length > 0 && sym.All(char.IsAsciiDigit) ? sym : "";
    }

    public void Dispose()
    {
      recognizer.Dispose();
      if (hotwordsFile.Length > 0) File.Delete(hotwordsFile);
    }
  }
}
