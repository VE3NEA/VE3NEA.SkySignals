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
  /// CUTE (NORAD 49263) telemetry decode regression: real CRC-OK off-air frames (captured from
  /// <c>2026-06-30_14_06_24_CUTE.iq.wav</c> via the headless StreamingPipeline decoder — FSK 9600 Bd, dev 4800,
  /// AX.25 G3RUH) pinned to expected fields. This is the off-air guard for the CCSDS-over-AX.25 features the CUTE
  /// definition exercises: the 16-byte AX.25 skip, the 6-byte primary-header bitstruct, dispatch on the 10-bit
  /// <c>application_process_id</c> (86 → <c>cute_bct_soh_t</c>, 511 → <c>cute_payload</c>), the conditional outer
  /// secondary header, the nested inner CCSDS header, the inner-APID gate (<c>if payload_apid==1</c> → the
  /// <c>sw_stat</c> body), and the big-endian scale/expr calibrations + enums of the payload packet (Zynq/CCD
  /// rails, opcode enum, float32 TEC fields). Fixture: <c>Data/cute_telemetry_regression.json</c>.
  /// </summary>
  public class CuteTelemetryRegressionTests
  {
    public sealed record Pin(int Norad, string Sat, int Type, string Hex, string Layout, Dictionary<string, string> Expect);
    public sealed record PinSet(string Description, List<Pin> Frames);

    private readonly TelemetryRegistry registry = new();

    private static PinSet Load()
    {
      string path = Path.Combine(TestPaths.ProjectRoot, "Data", "cute_telemetry_regression.json");
      return JsonSerializer.Deserialize<PinSet>(File.ReadAllText(path),
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }

    [Fact]
    public void AllPinnedCuteFrames_DecodeToExpectedFields()
    {
      var set = Load();
      set.Frames.Should().HaveCountGreaterThanOrEqualTo(2);

      foreach (var p in set.Frames)
      {
        var def = registry.ForNorad(p.Norad);
        def.Should().NotBeNull($"{p.Sat} ({p.Norad}) should resolve to the CUTE definition");

        var rec = TelemetryParser.Parse(def!, Convert.FromHexString(p.Hex));
        rec.Should().NotBeNull($"{p.Sat} type {p.Type} frame should decode (dispatch on APID)");
        rec!.Layout.Should().Be(p.Layout, $"{p.Sat} type {p.Type} layout");
        rec.Type.Should().Be(p.Type, $"{p.Sat} dispatch type");

        foreach (var (name, expected) in p.Expect)
          rec.Fields.Single(f => f.Name == name).Value
            .Should().Be(expected, $"{p.Sat} type {p.Type} field {name}");
      }
    }

    /// <summary>
    /// The APID-511 payload wrapper carries more than one inner packet type; only inner APID 1 (sw_stat) is
    /// defined. A real inner-APID-30 frame must select <c>cute_payload</c>, surface <c>payload_apid = 30</c>, and
    /// be <b>gated</b> — the <c>if payload_apid==1</c> guard keeps the sw_stat body out, so the unknown packet is
    /// not mis-decoded into bogus voltages/temps.
    /// </summary>
    [Fact]
    public void Apid511_InnerApid30Frame_IsGated_NotMisdecoded()
    {
      const string hex = "8486A84040406086AAA88A4040E103F009FFDD4900571528D1250000081EFBA7004B31D6C2CB00A81528D12400000000444307D9C57E482E45A29B9F3E9B574F3EA4DFE5BF144CDB3F2F415DBF416091C0C5C846C096D5F837AA962937153714B855F4EF31D6C2CA00001E8922F5";
      var def = registry.ForNorad(49263);
      var rec = TelemetryParser.Parse(def!, Convert.FromHexString(hex));

      rec.Should().NotBeNull();
      rec!.Layout.Should().Be("cute_payload");
      rec.Type.Should().Be(511);
      rec.Fields.Single(f => f.Name == "payload_apid").Value.Should().Be("30");
      rec.Fields.Should().NotContain(f => f.Name == "shCoarse", "the sw_stat body is gated to inner APID 1");
      rec.Fields.Should().NotContain(f => f.Name == "zynqVccInt", "the sw_stat body is gated to inner APID 1");
    }
  }
}
