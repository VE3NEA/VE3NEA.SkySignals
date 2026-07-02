using System.IO;

namespace VE3NEA.SkyTlm.Tests.Regression
{
  /// <summary>
  /// Locates the committed regression fixtures in the <b>source</b> tree (walking up from the test
  /// assembly's output dir to the test project), so the corpus <c>.wav</c> files are read in place
  /// rather than copied into <c>bin/</c> on every build.
  /// </summary>
  internal static class TestPaths
  {
    /// <summary>The test project root (the folder holding <c>VE3NEA.SkyTlm.Tests.csproj</c>).</summary>
    public static string ProjectRoot { get; } = FindProjectRoot();

    /// <summary><c>Data/Wav</c> under the test project — the per-flavor regression corpus.</summary>
    public static string WavDir => Path.Combine(ProjectRoot, "Data", "Wav");

    private static string FindProjectRoot()
    {
      var dir = new DirectoryInfo(AppContext.BaseDirectory);
      while (dir != null)
      {
        if (File.Exists(Path.Combine(dir.FullName, "VE3NEA.SkyTlm.Tests.csproj")))
          return dir.FullName;
        dir = dir.Parent;
      }
      throw new DirectoryNotFoundException(
        "Could not locate VE3NEA.SkyTlm.Tests.csproj above " + AppContext.BaseDirectory);
    }
  }
}
