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
  /// GEOSCAN-2 beacon telemetry decode regression: real CRC-OK off-air frames archived by SkyRoof's
  /// telemetry panel, pinned to the values the fleet's published protocol yields (SatsDecoder's
  /// <c>geoscan2_tlm</c>). This guards <c>Telemetry/Definitions/geoscan2.json</c> and, with it, the
  /// <see cref="TelemetryParser"/> paths it leans on: absolute <c>pos</c> seeks past the AX.25 header and
  /// the protocol's reserved gaps, little-endian words, signed bytes, single-bit <c>bool</c> flags, and an
  /// <c>if</c>-gated group.
  /// <para>
  /// The gate is the point of the second test. This definition is resolved by NORAD, and the same
  /// satellite sends native Geoscan frames down the same downlink; without the control/pid/mayak_id
  /// check those would be sliced into two dozen meaningless fields. Fixture:
  /// <c>Data/geoscan2_telemetry_regression.json</c>.
  /// </para>
  /// </summary>
  public class Geoscan2TelemetryRegressionTests
  {
    public sealed record Pin(int Norad, string Sat, string Hex, string Layout, Dictionary<string, string> Expect);
    public sealed record NonBeacon(int Norad, string Sat, string Hex);
    public sealed record PinSet(string Description, List<Pin> Frames, List<NonBeacon> NonBeaconFrames);

    private readonly TelemetryRegistry registry = new();

    private static PinSet Load()
    {
      string path = Path.Combine(TestPaths.ProjectRoot, "Data", "geoscan2_telemetry_regression.json");
      return JsonSerializer.Deserialize<PinSet>(File.ReadAllText(path),
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }

    [Fact]
    public void AllPinnedBeacons_DecodeToExpectedFields()
    {
      var set = Load();
      set.Frames.Should().NotBeEmpty();

      foreach (var p in set.Frames)
      {
        var def = registry.ForNorad(p.Norad);
        def.Should().NotBeNull($"{p.Sat} ({p.Norad}) should resolve to the GEOSCAN-2 definition");

        var rec = TelemetryParser.Parse(def!, Convert.FromHexString(p.Hex));
        rec.Should().NotBeNull($"{p.Sat} beacon should decode");
        rec!.Layout.Should().Be(p.Layout, $"{p.Sat} layout");

        foreach (var (name, expected) in p.Expect)
          rec.Fields.Single(f => f.Name == name).Value
            .Should().Be(expected, $"{p.Sat}.{name}");
      }
    }

    [Fact]
    public void NativeGeoscanFrames_DecodeToNothing()
    {
      var set = Load();
      set.NonBeaconFrames.Should().NotBeEmpty();

      foreach (var p in set.NonBeaconFrames)
      {
        var def = registry.ForNorad(p.Norad);
        def.Should().NotBeNull($"{p.Sat} ({p.Norad}) should resolve to the GEOSCAN-2 definition");

        var rec = TelemetryParser.Parse(def!, Convert.FromHexString(p.Hex));
        rec!.Fields.Should().BeEmpty($"{p.Sat} is not a beacon and must not be sliced into fields");
      }
    }
  }
}
