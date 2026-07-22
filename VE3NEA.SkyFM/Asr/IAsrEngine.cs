using System;
using System.Collections.Generic;

namespace VE3NEA.SkyFM
{
  /// <summary>One recognized word with time alignment and confidence — the unit the grammar layer
  /// consumes (plan §5.3: engines must expose per-token confidence, never a bare 1-best string).</summary>
  public readonly record struct AsrWord(string Text, double StartSeconds, double EndSeconds, float Confidence);

  /// <summary>One N-best entry for a transmission: a word sequence with the engine's path score.</summary>
  public sealed record AsrHypothesis
  {
    public required IReadOnlyList<AsrWord> Words { get; init; }
    public required double Score { get; init; }
  }

  /// <summary>
  /// The pluggable recognizer seam (plan G5/§5.3): audio of one carrier-segmented transmission in,
  /// N-best word sequences with per-word confidence out. Engines that cannot expose word confidence are
  /// disqualified (the abstention policy has no input without it).
  /// </summary>
  public interface IAsrEngine : IDisposable
  {
    string Name { get; }

    /// <summary>Transcribe one per-transmission audio segment (mono, <paramref name="sampleRate"/> Hz,
    /// any amplitude scale). Returns hypotheses best-first; empty list = no speech recognized.</summary>
    IReadOnlyList<AsrHypothesis> Transcribe(ReadOnlySpan<float> audio, int sampleRate);
  }
}
