using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using FluentAssertions;
using VE3NEA.SkyTlm.Telemetry;
using VE3NEA.SkyTlm.Tests.Regression;
using Xunit;

namespace VE3NEA.SkyTlm.Tests.Unit
{
  /// <summary>
  /// USP telemetry decode regression: real CRC-OK off-air frames (captured from the SkyRoof recordings
  /// via the headless decoder) pinned to a few expected <c>uhf_beacon</c> fields. This guards the
  /// converter-generated <c>Telemetry/Definitions/usp.json</c> + <see cref="TelemetryParser"/> against
  /// drift across the whole decoded Sputnix fleet (UmKA-1, Svyatobor-1, Monitor-4, SAKHACUBE-CHOLBON,
  /// Luca, HyperView-1G). Fixture: <c>Data/usp_telemetry_regression.json</c>.
  /// </summary>
  public class UspTelemetryRegressionTests
  {
    public sealed record Pin(int Norad, string Sat, string Hex, string Layout, Dictionary<string, string> Expect);
    public sealed record PinSet(string Description, List<Pin> Frames);

    private readonly TelemetryRegistry registry = new();

    private static PinSet Load()
    {
      string path = Path.Combine(TestPaths.ProjectRoot, "Data", "usp_telemetry_regression.json");
      return JsonSerializer.Deserialize<PinSet>(File.ReadAllText(path),
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }

    [Fact]
    public void AllPinnedUspFrames_DecodeToExpectedFields()
    {
      var set = Load();
      set.Frames.Should().HaveCountGreaterThanOrEqualTo(6);

      foreach (var p in set.Frames)
      {
        var def = registry.ForNorad(p.Norad);
        def.Should().NotBeNull($"{p.Sat} ({p.Norad}) should resolve to the shared USP definition");

        var rec = TelemetryParser.Parse(def!, Convert.FromHexString(p.Hex));
        rec.Should().NotBeNull($"{p.Sat} frame should decode");
        rec!.Layout.Should().Be(p.Layout, $"{p.Sat} layout");

        foreach (var (name, expected) in p.Expect)
          rec.Fields.Single(f => f.Name == name).Value
            .Should().Be(expected, $"{p.Sat}.{name}");
      }
    }
  }
}
