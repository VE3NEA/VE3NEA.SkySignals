using System;
using System.IO;

namespace VE3NEA.SkyFM.Tests
{
  /// <summary>Resolves repo-relative data files (corpus JSON, spike transcripts) for tests.</summary>
  internal static class RepoFiles
  {
    /// <summary>Ascend from the test assembly location to the repo root and resolve
    /// <paramref name="relative"/>.</summary>
    public static string Find(string relative)
    {
      for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
      {
        string p = Path.Combine(dir.FullName, relative);
        if (File.Exists(p)) return p;
      }
      throw new FileNotFoundException(relative);
    }
  }
}
