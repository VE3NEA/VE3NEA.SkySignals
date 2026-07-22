using System;
using System.Collections.Generic;
using MathNet.Numerics;

namespace VE3NEA.SkyFM
{
  /// <summary>Everything the front-end extracts from one recording: the 16 kHz voice audio (Hz units,
  /// zero-phase timeline), the per-transmission segments, and the per-frame squelch noise level.</summary>
  public sealed record FmDecodeResult
  {
    public required float[] Voice { get; init; }
    public required int SampleRate { get; init; }
    public required IReadOnlyList<FmTransmission> Transmissions { get; init; }
    public required IReadOnlyList<float> SquelchLevelDb { get; init; }
    public required double SquelchFrameS { get; init; }
  }

  /// <summary>
  /// Batch entry point of the FM voice front-end: I/Q + options in, audio + segments out (plan G6 — the
  /// core does no file IO). A thin wrapper over the streaming <see cref="FmFrontEnd"/> so batch and
  /// streaming stay one implementation.
  /// </summary>
  public static class FmDecoder
  {
    private const int BlockSize = 65536;

    public static FmDecodeResult Decode(Complex32[] iq, FmDecodeOptions? options = null)
    {
      var o = options ?? new FmDecodeOptions();
      using var frontEnd = new FmFrontEnd(o);

      var voice = new List<float>(iq.Length / (int)Math.Max(1.0, o.SampleRate / o.OutputSampleRate) + 16);
      for (int at = 0; at < iq.Length; at += BlockSize)
      {
        int n = Math.Min(BlockSize, iq.Length - at);
        voice.AddRange(frontEnd.Process(new ReadOnlySpan<Complex32>(iq, at, n)));
      }
      voice.AddRange(frontEnd.Flush());

      return new FmDecodeResult
      {
        Voice = voice.ToArray(),
        SampleRate = frontEnd.OutputSampleRate,
        Transmissions = new List<FmTransmission>(frontEnd.Transmissions),
        SquelchLevelDb = new List<float>(frontEnd.SquelchLevelDb),
        SquelchFrameS = o.SquelchFrameS
      };
    }
  }
}
