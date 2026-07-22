using System;
using System.Collections.Generic;
using MathNet.Numerics;

namespace VE3NEA.SkyFM
{
  /// <summary>Options for the complete SkyFM pipeline: the front-end tunables plus the calibrated
  /// grammar-layer stages (all defaults are the corpus-calibrated operating point).</summary>
  public sealed record SkyFmOptions
  {
    public FmDecodeOptions Fm { get; init; } = new();
    public DepthConfidence Depth { get; init; } = new();
    public EmitPolicy Policy { get; init; } = new();
  }

  /// <summary>Everything the pipeline extracts from one recording: the front-end result, the fused
  /// (pre-policy) candidates for inspection, and the policy-gated candidates — the product
  /// output.</summary>
  public sealed record SkyFmResult
  {
    public required FmDecodeResult Fm { get; init; }
    public required IReadOnlyList<Candidate> Fused { get; init; }
    public required IReadOnlyList<Candidate> Candidates { get; init; }
  }

  /// <summary>
  /// Batch entry point of the complete SkyFM pipeline (plan §6, A5): I/Q + options + engines in,
  /// identifier candidates out — no file IO, no scoring. A thin wrapper over
  /// <see cref="SkyFmStreamingDecoder"/> so batch and streaming stay one implementation (the
  /// <see cref="FmDecoder"/> precedent). The grammar half is exposed as <see cref="Extract"/> so the
  /// scoring host enters the same code path from cached engine words.
  /// </summary>
  public static class SkyFmDecoder
  {
    private const int BlockSize = 65536;

    public static SkyFmResult Decode(Complex32[] iq, IReadOnlyList<IAsrEngine> engines,
      SkyFmOptions? options = null)
    {
      var o = options ?? new SkyFmOptions();
      using var decoder = new SkyFmStreamingDecoder(engines, o, keepAllVoice: true);
      for (int at = 0; at < iq.Length; at += BlockSize)
      {
        int n = Math.Min(BlockSize, iq.Length - at);
        decoder.Process(new ReadOnlySpan<Complex32>(iq, at, n));
      }
      decoder.Flush();

      return new SkyFmResult
      {
        Fm = new FmDecodeResult
        {
          Voice = decoder.VoiceSnapshot(),
          SampleRate = decoder.OutputSampleRate,
          Transmissions = new List<FmTransmission>(decoder.Transmissions),
          SquelchLevelDb = new List<float>(decoder.SquelchLevelDb),
          SquelchFrameS = o.Fm.SquelchFrameS
        },
        Fused = decoder.Fused,
        Candidates = decoder.Candidates
      };
    }

    /// <summary>The grammar half of the pipeline: per-transmission word sequences (recording-relative
    /// times) with their transmissions' quieting depths → fused and policy-gated candidates.</summary>
    public static (IReadOnlyList<Candidate> Fused, IReadOnlyList<Candidate> Gated) Extract(
      IReadOnlyList<IReadOnlyList<AsrWord>> transmissions, IReadOnlyList<double>? depths, SkyFmOptions o)
    {
      var assembler = new Assembler();
      var pool = new List<Candidate>();
      for (int i = 0; i < transmissions.Count; i++)
      {
        var candidates = assembler.Assemble(transmissions[i]);
        if (depths != null) candidates = o.Depth.Apply(candidates, depths[i]);
        pool.AddRange(candidates);
      }
      var fused = CandidateFusion.Fuse(pool);
      return (fused, o.Policy.Apply(fused));
    }
  }
}
