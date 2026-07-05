using System.Reflection;
using System.Text.Json;
using Serilog;

namespace VE3NEA.SkyTlm.Telemetry
{
  /// <summary>
  /// Maps a satellite NORAD ID to its telemetry <see cref="TelemetryDefinition"/>. Definitions ship in-repo as
  /// embedded resources under <c>Telemetry/Definitions/</c>; each JSON carries its own <c>norad</c> list, so the
  /// NORAD → definition mapping lives entirely in the data (one file may serve a whole fleet, e.g. the Sputnix
  /// <c>usp.json</c>). The parameterless constructor loads the bundled definitions; the <see cref="TelemetryRegistry(string)"/>
  /// constructor mirrors them into a user folder at startup (adding missing files, refreshing outdated ones) and
  /// loads from there, so shipped updates land in the folder while user-only files are left untouched.
  /// </summary>
  public class TelemetryRegistry
  {
    // norad_id -> definition; built once at construction from the embedded resources or a user folder.
    private readonly Dictionary<int, TelemetryDefinition> byNorad = new();

    // resource-name segment that marks a bundled definition; the text after it is the on-disk file name.
    private const string Marker = ".Telemetry.Definitions.";

    /// <summary>Creates a registry backed by the bundled (embedded) definitions.</summary>
    public TelemetryRegistry() => LoadDefinitions(EnumerateEmbedded());

    /// <summary>Mirrors the bundled definitions into <paramref name="folder"/> (see <see cref="SyncFiles"/>) and
    /// then loads the definitions from that folder, so a user can inspect/extend them on disk.</summary>
    public TelemetryRegistry(string folder)
    {
      SyncFiles(folder);
      LoadFiles(folder);
    }

    /// <summary>Definition for a satellite by NORAD ID, or null when none is registered.</summary>
    public TelemetryDefinition? ForNorad(int? noradId) =>
      noradId is int nid && byNorad.TryGetValue(nid, out var def) ? def : null;

    /// <summary>Load every <c>*.json</c> in <paramref name="folder"/>, indexing each by the NORAD IDs in its
    /// <c>norad</c> array. Files are matched by content, not by name.</summary>
    public void LoadFiles(string folder)
    {
      Directory.CreateDirectory(folder);
      LoadDefinitions(Directory.GetFiles(folder, "*.json").Select(f => (f, File.ReadAllText(f))));
    }

    /// <summary>Mirror the bundled definitions into <paramref name="folder"/>: create it if needed, write any
    /// file that is missing, and overwrite any whose content differs from the bundled version (so upgrades add
    /// new satellites and refresh stale definitions). Files that exist only in the folder are left alone, as is
    /// any on-disk file that claims itself with <c>"readOnly": true</c> (a user edit that must survive upgrades).</summary>
    public static void SyncFiles(string folder)
    {
      Directory.CreateDirectory(folder);
      var asm = typeof(TelemetryRegistry).Assembly;
      foreach (var name in asm.GetManifestResourceNames())
      {
        int idx = name.IndexOf(Marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) continue;
        string fileName = name.Substring(idx + Marker.Length);
        if (!fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;

        string bundled = ReadResource(asm, name);
        string path = Path.Combine(folder, fileName);
        bool exists = File.Exists(path);
        if (exists && File.ReadAllText(path) == bundled) continue;   // already up to date
        if (exists && IsReadOnly(path))                              // user claimed this file (§2.6) — keep their edit
        {
          Log.Information("TelemetryRegistry: keeping read-only definition {File}", fileName);
          continue;
        }

        File.WriteAllText(path, bundled);
        Log.Information("TelemetryRegistry: {Action} definition {File}", exists ? "updated" : "added", fileName);
      }
    }

    // true when the on-disk definition claims itself with a top-level "readOnly": true; on any read/parse
    // error, returns false so a corrupt user file still gets refreshed rather than pinned forever.
    private static bool IsReadOnly(string path)
    {
      try
      {
        using var doc = JsonDocument.Parse(File.ReadAllText(path),
          new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;
        foreach (var prop in doc.RootElement.EnumerateObject())
          if (string.Equals(prop.Name, "readOnly", StringComparison.OrdinalIgnoreCase))
            return prop.Value.ValueKind == JsonValueKind.True;
        return false;
      }
      catch (Exception ex)
      {
        Log.Warning("TelemetryRegistry: could not check {Path} for readOnly — {Message}", path, ex.Message);
        return false;
      }
    }

    // index each parsed definition under every NORAD ID it lists; a definition with no norad list is inert.
    private void LoadDefinitions(IEnumerable<(string Source, string Json)> defs)
    {
      foreach (var (source, json) in defs)
      {
        TelemetryDefinition def;
        try { def = TelemetryDefinition.Parse(json); }
        catch (Exception ex)
        {
          Log.Warning("TelemetryRegistry: skipping {Source} — {Message}", source, ex.Message);
          continue;
        }
        if (def.Norad.Count == 0)
          Log.Warning("TelemetryRegistry: {Source} lists no norad IDs; it will never be resolved", source);
        foreach (int nid in def.Norad) byNorad[nid] = def;
      }
    }

    private static IEnumerable<(string, string)> EnumerateEmbedded()
    {
      var asm = typeof(TelemetryRegistry).Assembly;
      foreach (var name in asm.GetManifestResourceNames())
        if (name.Contains(Marker, StringComparison.OrdinalIgnoreCase) &&
            name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
          yield return (name, ReadResource(asm, name));
    }

    private static string ReadResource(Assembly asm, string name)
    {
      using var s = asm.GetManifestResourceStream(name)!;
      using var reader = new StreamReader(s);
      return reader.ReadToEnd();
    }
  }
}
