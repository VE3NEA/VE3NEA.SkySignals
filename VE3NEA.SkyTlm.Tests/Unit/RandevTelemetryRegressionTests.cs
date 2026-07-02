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
  /// RANDEV telemetry decode regression: a real CRC-OK off-air frame (captured from a SkyRoof recording via
  /// the headless StreamingPipeline decoder) pinned to a few expected fields. This guards the
  /// <c>Telemetry/Definitions/randev.json</c> definition and the
  /// schema-v2 <see cref="TelemetryParser"/> (computed zero-width instance fields + ternary <c>expr</c>) against
  /// drift. Fixture: <c>Data/randev_telemetry_regression.json</c>.
  /// </summary>
  public class RandevTelemetryRegressionTests
  {
    public sealed record Pin(int Norad, string Sat, string Hex, string Layout, Dictionary<string, string> Expect);
    public sealed record PinSet(string Description, List<Pin> Frames);

    private readonly TelemetryRegistry registry = new();

    private static PinSet Load()
    {
      string path = Path.Combine(TestPaths.ProjectRoot, "Data", "randev_telemetry_regression.json");
      return JsonSerializer.Deserialize<PinSet>(File.ReadAllText(path),
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }

    [Fact]
    public void AllPinnedRandevFrames_DecodeToExpectedFields()
    {
      var set = Load();
      set.Frames.Should().NotBeEmpty();

      foreach (var p in set.Frames)
      {
        var def = registry.ForNorad(p.Norad);
        def.Should().NotBeNull($"{p.Sat} ({p.Norad}) should resolve to the RANDEV definition");

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
