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
  /// AEPEX telemetry decode regression: a real CRC-OK off-air frame (captured from a SkyRoof recording via
  /// the headless StreamingPipeline decoder) pinned to a few expected <c>aepex_sw_stat</c> fields. This guards
  /// the CCSDS-dispatch path in <c>Telemetry/Definitions/aepex.json</c> and the
  /// <see cref="TelemetryParser"/>: 16-byte skip past
  /// AX.25 header + pid, the 6-byte primary-header bitstruct, dispatch on the b10 application_process_id
  /// (=1 -> aepex_sw_stat), the conditional secondary header (<c>if secondary_header_flag</c>), and the
  /// big-endian payload. Fixture: <c>Data/aepex_telemetry_regression.json</c>.
  /// </summary>
  public class AepexTelemetryRegressionTests
  {
    public sealed record Pin(int Norad, string Sat, string Hex, string Layout, Dictionary<string, string> Expect);
    public sealed record PinSet(string Description, List<Pin> Frames);

    private readonly TelemetryRegistry registry = new();

    private static PinSet Load()
    {
      string path = Path.Combine(TestPaths.ProjectRoot, "Data", "aepex_telemetry_regression.json");
      return JsonSerializer.Deserialize<PinSet>(File.ReadAllText(path),
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }

    [Fact]
    public void AllPinnedAepexFrames_DecodeToExpectedFields()
    {
      var set = Load();
      set.Frames.Should().NotBeEmpty();

      foreach (var p in set.Frames)
      {
        var def = registry.ForNorad(p.Norad);
        def.Should().NotBeNull($"{p.Sat} ({p.Norad}) should resolve to the AEPEX definition");

        var rec = TelemetryParser.Parse(def!, Convert.FromHexString(p.Hex));
        rec.Should().NotBeNull($"{p.Sat} frame should decode (dispatch on APID)");
        rec!.Layout.Should().Be(p.Layout, $"{p.Sat} layout");

        foreach (var (name, expected) in p.Expect)
          rec.Fields.Single(f => f.Name == name).Value
            .Should().Be(expected, $"{p.Sat}.{name}");
      }
    }
  }
}
