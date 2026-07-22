using System.IO;
using System.Runtime.CompilerServices;

namespace VE3NEA.SkyFM.Tests
{
  /// <summary>Points the promoted <see cref="SherpaOnnxEngine.ModelDirectory"/> (integration plan A1)
  /// at the repo model dir once for the whole test assembly, so the harness call sites keep using the
  /// bare <c>SherpaOnnxEngine.Hotwords()</c> factories. Production instead sets the property to the
  /// downloaded model-pack folder.</summary>
  internal static class SherpaTestModel
  {
    public static readonly string Dir = Path.GetDirectoryName(RepoFiles.Find(
      Path.Combine("asr-spike", "sherpa", "sherpa-onnx-zipformer-gigaspeech-2023-12-12", "tokens.txt")))!;

    [ModuleInitializer]
    internal static void Init() => SherpaOnnxEngine.ModelDirectory = Dir;
  }
}
