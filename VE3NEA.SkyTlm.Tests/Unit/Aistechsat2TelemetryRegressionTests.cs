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
  /// AISTECHSAT-2 (NORAD 43768) telemetry decode regression: real CRC-OK off-air GomSpace CSP/PUS housekeeping
  /// beacons (captured from two SkyRoof recordings via the headless StreamingPipeline decoder — GFSK 4800 Bd,
  /// dev 1600, AX100 ASM+Golay mode 5) pinned to expected fields. This guards the byte-offset dispatch path in
  /// <c>Telemetry/Definitions/aistechsat-2.json</c> and the <see cref="TelemetryParser"/>: the 30-byte
  /// CSP + TM transfer + CCSDS Space Packet + PUS TM[3,25] housekeeping header, dispatch on the 16-bit big-endian
  /// <c>beacon_id</c> at byte 28 (1→obc, 2→eps, 3→ttc_gssb, 4→aocs, 5→temperatures), and the big-endian parameter
  /// blocks from byte 30 (Unix clock, battery mV, currents mA, RSSI dBm, sun-sensor degC, the NUL-terminated
  /// <c>string[32]</c> firmware version). One OBC frame comes from a later recording, proving the clock advances.
  /// Fixture: <c>Data/aistechsat2_telemetry_regression.json</c>.
  /// </summary>
  public class Aistechsat2TelemetryRegressionTests
  {
    public sealed record Pin(int Norad, string Sat, int Type, string Hex, string Layout, Dictionary<string, string> Expect);
    public sealed record PinSet(string Description, List<Pin> Frames);

    private readonly TelemetryRegistry registry = new();

    private static PinSet Load()
    {
      string path = Path.Combine(TestPaths.ProjectRoot, "Data", "aistechsat2_telemetry_regression.json");
      return JsonSerializer.Deserialize<PinSet>(File.ReadAllText(path),
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }

    [Fact]
    public void AllPinnedAistechsat2Frames_DecodeToExpectedFields()
    {
      var set = Load();
      set.Frames.Should().HaveCountGreaterThanOrEqualTo(5, "all five beacon types should be covered");

      foreach (var p in set.Frames)
      {
        var def = registry.ForNorad(p.Norad);
        def.Should().NotBeNull($"{p.Sat} ({p.Norad}) should resolve to the AISTECHSAT-2 definition");

        var rec = TelemetryParser.Parse(def!, Convert.FromHexString(p.Hex));
        rec.Should().NotBeNull($"{p.Sat} beacon {p.Type} frame should decode (dispatch on beacon_id)");
        rec!.Layout.Should().Be(p.Layout, $"{p.Sat} beacon {p.Type} layout");
        rec.Type.Should().Be(p.Type, $"{p.Sat} dispatch beacon id");

        foreach (var (name, expected) in p.Expect)
          rec.Fields.Single(f => f.Name == name).Value
            .Should().Be(expected, $"{p.Sat} beacon {p.Type} field {name}");
      }
    }

    /// <summary>
    /// A frame too short to contain the beacon id (the 29-byte non-beacon status frame the deframer also emits)
    /// must not decode to a bogus layout: dispatch reads past the frame end and returns no record.
    /// </summary>
    [Fact]
    public void ShortNonBeaconFrame_DoesNotMisdecode()
    {
      const string hex = "82F39D00001158000E0000E37C0007509A00981FB12B1900000000C6C1";
      var def = registry.ForNorad(43768);
      var rec = TelemetryParser.Parse(def!, Convert.FromHexString(hex));
      rec.Should().BeNull("a frame shorter than the 30-byte header has no beacon id to dispatch on");
    }
  }
}
