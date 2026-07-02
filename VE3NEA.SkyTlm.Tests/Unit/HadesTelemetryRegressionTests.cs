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
  /// HADES-SA telemetry decode regression: real CRC-OK off-air frames (captured from
  /// <c>2026-04-17_14_54_53_HADES-SA.iq.wav</c> via the headless StreamingPipeline decoder), pinned to
  /// expected fields. This is the off-air guard for the schema features that ONLY HADES exercises in a real
  /// recording — linear <c>scale</c>+<c>offset</c>, <c>special</c> (sentinel) values, <c>units</c>,
  /// <c>enum</c>, a visible <c>repeat</c> array (type 14 time_series), and the empty-BBS detection (type 15:
  /// every off-air BBS frame carries an empty message store, rendered as a single <c>empty (No data)</c>
  /// marker — the group/str/repeat mechanics are covered synthetically by
  /// <c>Hades_Type15_Bbs_GroupRepeatReproducesExplicitNames</c>).
  /// Fixture: <c>Data/hades_telemetry_regression.json</c>. (Type-1/4/9
  /// power frames are not pinned: their values are uncalibrated — see the hades-power-packing note.)
  /// </summary>
  public class HadesTelemetryRegressionTests
  {
    public sealed record Pin(int Norad, string Sat, int Type, string Hex, string Layout, Dictionary<string, string> Expect);
    public sealed record PinSet(string Description, List<Pin> Frames);

    private readonly TelemetryRegistry registry = new();

    private static PinSet Load()
    {
      string path = Path.Combine(TestPaths.ProjectRoot, "Data", "hades_telemetry_regression.json");
      return JsonSerializer.Deserialize<PinSet>(File.ReadAllText(path),
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }

    [Fact]
    public void AllPinnedHadesFrames_DecodeToExpectedFields()
    {
      var set = Load();
      set.Frames.Should().HaveCountGreaterThanOrEqualTo(3);

      foreach (var p in set.Frames)
      {
        var def = registry.ForNorad(p.Norad);
        def.Should().NotBeNull($"{p.Sat} ({p.Norad}) should resolve to the HADES definition");

        var rec = TelemetryParser.Parse(def!, Convert.FromHexString(p.Hex));
        rec.Should().NotBeNull($"{p.Sat} type {p.Type} frame should decode");
        rec!.Layout.Should().Be(p.Layout, $"{p.Sat} type {p.Type} layout");
        rec.Type.Should().Be(p.Type, $"{p.Sat} dispatch type");

        foreach (var (name, expected) in p.Expect)
          rec.Fields.Single(f => f.Name == name).Value
            .Should().Be(expected, $"{p.Sat} type {p.Type} field {name}");
      }
    }
  }
}
