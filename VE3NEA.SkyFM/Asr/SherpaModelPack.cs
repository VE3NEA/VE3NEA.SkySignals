using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace VE3NEA.SkyFM
{
  /// <summary>
  /// Names and presence check for the sherpa GigaSpeech model pack (integration plan A4 / Phase C).
  /// The v1 product ships the <b>int8</b> pack (plan S0); the host downloads it into the app data tree
  /// and points <see cref="SherpaOnnxEngine.ModelDirectory"/> at the folder. The host gates the FM
  /// speech decoder on <see cref="IsPresent"/> so a missing/partial download degrades to "model not
  /// downloaded" rather than a native crash.
  /// </summary>
  public static class SherpaModelPack
  {
    /// <summary>Files the int8 pack must contain (the shipped set — plan S0).</summary>
    public static readonly IReadOnlyList<string> Int8Files = new[]
    {
      "encoder-epoch-30-avg-1.int8.onnx", "decoder-epoch-30-avg-1.int8.onnx",
      "joiner-epoch-30-avg-1.int8.onnx", "tokens.txt", "bpe.vocab"
    };

    /// <summary>Files the fp32 pack must contain (the lab/fallback set).</summary>
    public static readonly IReadOnlyList<string> Fp32Files = new[]
    {
      "encoder-epoch-30-avg-1.onnx", "decoder-epoch-30-avg-1.onnx",
      "joiner-epoch-30-avg-1.onnx", "tokens.txt", "bpe.vocab"
    };

    private static IReadOnlyList<string> Files(bool int8) => int8 ? Int8Files : Fp32Files;

    /// <summary>The pack files not present under <paramref name="dir"/> (empty = complete). An empty or
    /// null directory reports every file missing.</summary>
    public static IReadOnlyList<string> MissingFiles(string? dir, bool int8 = true)
    {
      if (string.IsNullOrEmpty(dir)) return Files(int8);
      return Files(int8).Where(f => !File.Exists(Path.Combine(dir, f))).ToList();
    }

    /// <summary>Whether <paramref name="dir"/> holds a complete pack.</summary>
    public static bool IsPresent(string? dir, bool int8 = true) => MissingFiles(dir, int8).Count == 0;
  }
}
