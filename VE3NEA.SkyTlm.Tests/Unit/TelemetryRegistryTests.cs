using System.IO;
using FluentAssertions;
using VE3NEA.SkyTlm.Telemetry;
using Xunit;

namespace VE3NEA.SkyTlm.Tests.Unit
{
  /// <summary>
  /// <see cref="TelemetryRegistry"/> resolution and the startup folder-sync. Resolution is driven entirely by the
  /// <c>norad</c> arrays in the bundled JSON (no file-name or hardcoded mapping): a shared file (<c>usp.json</c>)
  /// resolves every NORAD it lists, and AISTECHSAT-2 (43768) resolves to its definition. The folder ctor mirrors
  /// the bundled definitions into a user folder, adding missing files and refreshing outdated ones while leaving
  /// user-only files untouched.
  /// </summary>
  public class TelemetryRegistryTests
  {
    [Theory]
    [InlineData(43768)]   // AISTECHSAT-2 (own file)
    [InlineData(68446)]   // HADES-SA (own file)
    [InlineData(57172)]   // UmKA-1 -> shared usp.json
    [InlineData(67290)]   // SAKHACUBE-CHOLBON -> shared usp.json
    public void ForNorad_ResolvesFromNoradListInJson(int norad)
    {
      new TelemetryRegistry().ForNorad(norad).Should().NotBeNull();
    }

    [Fact]
    public void ForNorad_UnknownOrNull_ReturnsNull()
    {
      var reg = new TelemetryRegistry();
      reg.ForNorad(null).Should().BeNull();
      reg.ForNorad(99999999).Should().BeNull();
    }

    [Fact]
    public void FolderCtor_AddsMissing_UpdatesOutdated_AndKeepsUserFiles()
    {
      string folder = Path.Combine(Path.GetTempPath(), "tlm_defs_" + Path.GetRandomFileName());
      try
      {
        // an outdated shipped file (wrong content) and an unrelated user-only file already present.
        Directory.CreateDirectory(folder);
        string aistech = Path.Combine(folder, "aistechsat-2.json");
        string userFile = Path.Combine(folder, "my-notes.json");
        File.WriteAllText(aistech, "{ \"stale\": true }");
        File.WriteAllText(userFile, "{ \"mine\": 1 }");

        var reg = new TelemetryRegistry(folder);

        // missing bundled files were added, and every bundled sat still resolves after loading from disk.
        File.Exists(Path.Combine(folder, "usp.json")).Should().BeTrue("missing bundled files are added");
        reg.ForNorad(43768).Should().NotBeNull("the outdated file was refreshed to the bundled content");
        reg.ForNorad(57172).Should().NotBeNull("a freshly added shared file resolves its fleet");

        // the stale shipped file was overwritten; the user-only file was left untouched.
        File.ReadAllText(aistech).Should().Contain("\"norad\": [43768]");
        File.ReadAllText(userFile).Should().Be("{ \"mine\": 1 }");
      }
      finally
      {
        if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
      }
    }
  }
}
