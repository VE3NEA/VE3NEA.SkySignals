using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VE3NEA.SkyFM.Tests
{
  /// <summary>Ground-truth tag of one identifier (plan §11): Gold = clearly copyable (the recall
  /// denominator); Partial = upside, not penalized when missed; Unintelligible = excluded.</summary>
  public enum TruthTag { Gold, Partial, Unintelligible }

  /// <summary>One identifier the operator copied by ear, with coarse mention times.</summary>
  public sealed record TruthIdentifier
  {
    public required string Text { get; init; }
    public required CandidateKind Kind { get; init; }
    public required TruthTag Tag { get; init; }
    public required double[] Times { get; init; }
  }

  /// <summary>One transcribed utterance: the individual symbols the operator heard — phonetic words,
  /// number words, and spoken letters decoded to their letters/digits, with <c>?</c> where a word was
  /// not copied. This is the primary ground-truth form (the operator labels symbols, not whole
  /// identifiers); the verbatim ear transcripts live in <c>corpus\ear\</c>.</summary>
  public sealed record TruthUtterance
  {
    public required double Time { get; init; }
    public required string Symbols { get; init; }
  }

  /// <summary>Ground truth for one recording of the corpus.</summary>
  public sealed record CorpusRecording
  {
    public required string File { get; init; }
    public required string Satellite { get; init; }
    /// <summary>"train" or "test" — the held-out per-bird test recording is never tuned on (§11).</summary>
    public required string Role { get; init; }
    /// <summary>Whole-identifier truth (the spike's ARISS labels); empty when the recording is
    /// labeled at the symbol level only — then the identifier tracks are not scored on it.</summary>
    public List<TruthIdentifier> Identifiers { get; init; } = [];
    /// <summary>Symbol-level truth decoded from the operator's ear transcript.</summary>
    public List<TruthUtterance> Utterances { get; init; } = [];
  }

  /// <summary>The single consolidated ground-truth file (plan §4): all recordings, one JSON.</summary>
  public sealed record Corpus
  {
    public required List<CorpusRecording> Recordings { get; init; }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
      PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
      Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
      WriteIndented = true
    };

    public static Corpus Load(string path)
      => JsonSerializer.Deserialize<Corpus>(File.ReadAllText(path), JsonOptions)
         ?? throw new InvalidDataException(path);

    public void Save(string path)
      => File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
  }
}
