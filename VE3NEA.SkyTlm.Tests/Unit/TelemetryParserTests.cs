using System;
using System.Linq;
using FluentAssertions;
using VE3NEA.SkyTlm.Telemetry;
using VE3NEA.SkyTlm.Tests.Fixtures;
using Xunit;
using Xunit.Abstractions;

namespace VE3NEA.SkyTlm.Tests.Unit
{
  /// <summary>
  /// End-to-end telemetry field-decode. The oracle is the same off-air HADES-SA type-2 frame
  /// <see cref="HadesDeframerTests.Deframe_DecodesRealType2Frame"/> pins — decoded bytes
  /// <c>23DE5F5B00FF5F6260FF505A5EFF61</c> cross-checked against the UZ7HO and AMSAT-EA decoders — fed through
  /// the JSON-driven parser to the calibrated, unit-tagged fields. Dispatch (type→layout), the little-endian
  /// 32-bit <c>sclock</c>, linear calibration (<c>raw*0.5−40</c>), and the <c>255→ERROR</c> sentinel are all
  /// exercised here.
  /// </summary>
  public class TelemetryParserTests
  {
    private readonly ITestOutputHelper output;
    private readonly TelemetryRegistry registry = new();
    public TelemetryParserTests(ITestOutputHelper o) => output = o;

    // descrambled, CRC-stripped HADES-SA type-2 payload (== Core.Frame.Bytes the deframer emits).
    private static readonly byte[] Type2 = Convert.FromHexString("23DE5F5B00FF5F6260FF505A5EFF61");

    [Fact]
    public void Definitions_LoadFromEmbeddedResources()
    {
      registry.ForNorad(68446).Should().NotBeNull();
      registry.ForNorad(57172).Should().NotBeNull();
      registry.ForNorad(null).Should().BeNull();
      registry.ForNorad(-1).Should().BeNull();
    }

    [Fact]
    public void Hades_DispatchesType2_ToTempsLayout()
    {
      var rec = TelemetryParser.Parse(registry.ForNorad(68446)!, Type2);
      rec.Should().NotBeNull();
      rec!.Type.Should().Be(2);
      rec.Layout.Should().Be("temps");
    }

    [Theory]
    [InlineData(10, "SSDV")]
    [InlineData(11, "CODEC2")]
    [InlineData(13, "PN9 random data")]
    public void Hades_SpecialTypes_PrependFrameTypeField(int type, string label)
    {
      var frame = (byte[])Type2.Clone();
      frame[0] = (byte)((type << 4) | 3);   // type nibble + HADES-SA address 3
      var rec = TelemetryParser.Parse(registry.ForNorad(68446)!, frame);
      rec.Should().NotBeNull();
      rec!.Type.Should().Be(type);
      rec.Layout.Should().Be(label);

      // the frame type (id and name) is now the first emitted name-value pair
      rec.Fields[0].Name.Should().Be("frame type");
      rec.Fields[0].Value.Should().Be($"{type} ({label})");
    }

    [Fact]
    public void Hades_Pn9_HasOnlyFrameTypeField()
    {
      // PN9 carries no structured field telemetry — only the frame-type pair is emitted.
      var frame = (byte[])Type2.Clone();
      frame[0] = (byte)((13 << 4) | 3);
      var rec = TelemetryParser.Parse(registry.ForNorad(68446)!, frame)!;
      rec.Fields.Should().ContainSingle().Which.Name.Should().Be("frame type");
    }

    [Fact]
    public void Hades_Ssdv_DecodesPacketHeaderFields()
    {
      // type/address, image id, packet id (be), width/height (mcu blocks → pixels ×16), flags, mcuOffset,
      // mcuIndex (be). Field bytes chosen to reproduce the example values.
      byte[] ssdv = { 0xA3, 0x30, 0x00, 0x1A, 0x14, 0x0F, 0x18, 0x02, 0x00, 0x61 };
      var rec = TelemetryParser.Parse(registry.ForNorad(68446)!, ssdv)!;
      string Val(string name) => rec.Fields.Single(f => f.Name == name).Value;

      rec.Fields[0].Name.Should().Be("frame type");
      rec.Fields[0].Value.Should().Be("10 (SSDV)");
      Val("image id").Should().Be("48");
      Val("packet id").Should().Be("26");
      Val("width id").Should().Be("320");
      Val("height id").Should().Be("240");
      Val("flags").Should().Be("24");
      Val("mcuOffset").Should().Be("2");
      Val("mcuIndex").Should().Be("97");
    }

    [Fact]
    public void Hades_Ssdv_DecodesTrailingChecksumAndFec()
    {
      // a full 251-byte SSDV packet: 10-byte header + 205-byte payload, then the 4-byte checksum (offset 215)
      // and 32-byte Reed-Solomon FEC (offset 219). Header is type/address only; the checksum/FEC bytes are read
      // from their fixed absolute offsets, regardless of the (here zeroed) payload.
      var ssdv = new byte[251];
      ssdv[0] = 0xA3;       // type 10 (SSDV), address 3
      byte[] checksum = Convert.FromHexString("F88D71F2");
      byte[] fec = Convert.FromHexString("CA7708DA013DA23361FFA167D4E670341067F366D948BADFF11DA07141467FC9");
      checksum.CopyTo(ssdv, 215);
      fec.CopyTo(ssdv, 219);
      var rec = TelemetryParser.Parse(registry.ForNorad(68446)!, ssdv)!;
      rec.Fields.Single(f => f.Name == "checksum").Value.Should().Be("F88D71F2");
      rec.Fields.Single(f => f.Name == "FEC").Value
        .Should().Be("CA7708DA013DA23361FFA167D4E670341067F366D948BADFF11DA07141467FC9");
    }

    [Fact]
    public void Hades_Codec2_DecodesFrameNumber()
    {
      // type 11 / address 3, then the 1-byte frame number.
      byte[] c2 = { 0xB3, 0x0B };
      var rec = TelemetryParser.Parse(registry.ForNorad(68446)!, c2)!;
      rec.Fields[0].Value.Should().Be("11 (CODEC2)");
      rec.Fields.Single(f => f.Name == "frame number").Value.Should().Be("11");
    }

    [Fact]
    public void Hades_Type2_DecodesCalibratedTemperaturesAndClock()
    {
      var rec = TelemetryParser.Parse(registry.ForNorad(68446)!, Type2)!;
      foreach (var f in rec.Fields) output.WriteLine($"{f.Name} = {f.Value} {f.Units}");

      string Val(string name) => rec.Fields.Single(f => f.Name == name).Value;
      string Unit(string name) => rec.Fields.Single(f => f.Name == name).Units;

      // little-endian 32-bit system clock
      Val("sclock").Should().Be("5988318");
      Unit("sclock").Should().Be("s");

      // raw*0.5 - 40, one decimal, °C; 0xFF -> ERROR sentinel (units dropped)
      Val("tpa").Should().Be("ERROR");
      Unit("tpa").Should().BeEmpty();
      Val("tpb").Should().Be("7.5");
      Unit("tpb").Should().Be("°C");
      Val("tpc").Should().Be("9.0");
      Val("tpd").Should().Be("8.0");
      Val("tpe").Should().Be("ERROR");
      Val("teps").Should().Be("0.0");
      Val("ttx").Should().Be("5.0");
      Val("ttx2").Should().Be("7.0");
      Val("trx").Should().Be("ERROR");
      Val("tcpu").Should().Be("8.5");

      // the hidden type/address fields are not emitted
      rec.Fields.Should().NotContain(f => f.Name == "type" || f.Name == "address");
    }

    [Fact]
    public void Hades_UnknownType_ReturnsNull()
    {
      // type 7 is unused on HADES-SA and not in the dispatch table -> no telemetry record at all.
      var unused = (byte[])Type2.Clone();
      unused[0] = (byte)((7 << 4) | 3);
      TelemetryParser.Parse(registry.ForNorad(68446)!, unused).Should().BeNull();
    }

    // (UmKA-1 USP decode is pinned alongside the rest of the fleet in UspTelemetryRegressionTests.)


    // ----------------------------------------------------------------------------------------------------
    //                              schema v2: repeat / group / untilEof / expr / if
    // ----------------------------------------------------------------------------------------------------
    private static TelemetryRecord ParseJson(string json, byte[] bytes) =>
      TelemetryParser.Parse(TelemetryDefinition.Parse(json), bytes)!;

    [Fact]
    public void V2_Repeat_SingleLeaf_EmitsIndexedFields()
    {
      string def = """
      { "default": "main", "endian": "be", "layouts": {
        "main": { "fields": [ { "name": "b_", "bits": 8, "repeat": 3 } ] } } }
      """;
      var rec = ParseJson(def, new byte[] { 10, 20, 30 });
      rec.Fields.Select(f => f.Name).Should().Equal("b_0", "b_1", "b_2");
      rec.Fields.Select(f => f.Value).Should().Equal("10", "20", "30");
    }

    [Fact]
    public void V2_Repeat_Group_SuffixesNestedNames()
    {
      string def = """
      { "default": "main", "endian": "be", "layouts": { "main": { "fields": [
        { "name": "entry", "repeat": 2, "fields": [
          { "name": "a", "bits": 8 }, { "name": "b", "bits": 8 } ] } ] } } }
      """;
      var rec = ParseJson(def, new byte[] { 1, 2, 3, 4 });
      rec.Fields.Select(f => $"{f.Name}={f.Value}").Should().Equal("a0=1", "b0=2", "a1=3", "b1=4");
    }

    [Fact]
    public void V2_Repeat_UntilEof_ReadsEveryElement()
    {
      string def = """
      { "default": "main", "endian": "be", "layouts": { "main": { "fields": [
        { "name": "e", "repeat": "untilEof", "fields": [
          { "name": "a", "bits": 8 }, { "name": "b", "bits": 8 } ] } ] } } }
      """;
      var rec = ParseJson(def, new byte[] { 1, 2, 3, 4, 5, 6 });
      rec.Fields.Should().HaveCount(6);
      rec.Fields.Select(f => f.Name).Should().Equal("a0", "b0", "a1", "b1", "a2", "b2");
    }

    [Fact]
    public void V2_Repeat_UntilEof_RollsBackPartialElement()
    {
      // 5 bytes, 2-byte elements: two full elements, then the trailing byte cannot form a third → rolled back.
      string def = """
      { "default": "main", "endian": "be", "layouts": { "main": { "fields": [
        { "name": "e", "repeat": "untilEof", "fields": [
          { "name": "a", "bits": 8 }, { "name": "b", "bits": 8 } ] } ] } } }
      """;
      var rec = ParseJson(def, new byte[] { 1, 2, 3, 4, 5 });
      rec.Fields.Select(f => f.Name).Should().Equal("a0", "b0", "a1", "b1");
    }

    [Fact]
    public void V2_Expr_LinearCalibration()
    {
      string def = """
      { "default": "main", "endian": "be", "layouts": { "main": { "fields": [
        { "name": "v", "bits": 8, "expr": "x * 0.5 - 40", "decimals": 1, "units": "C" } ] } } }
      """;
      var rec = ParseJson(def, new byte[] { 100 });
      rec.Fields.Single(f => f.Name == "v").Value.Should().Be("10.0");
      rec.Fields.Single(f => f.Name == "v").Units.Should().Be("C");
    }

    [Fact]
    public void V2_Expr_CrossFieldReference()
    {
      // a 12-bit value packed as a hidden high byte and low nibble, recombined by expr (the HADES-style case).
      string def = """
      { "default": "main", "endian": "be", "layouts": { "main": { "fields": [
        { "name": "hi", "bits": 8, "hidden": true },
        { "name": "val", "bits": 4, "expr": "hi * 16 + x" } ] } } }
      """;
      var rec = ParseJson(def, new byte[] { 0x12, 0x30 });   // hi=0x12=18, low nibble=0x3 → 18*16+3 = 291
      rec.Fields.Single(f => f.Name == "val").Value.Should().Be("291");
    }

    [Fact]
    public void V2_Computed_ZeroWidthLeaf_DerivesFromPriorFields()
    {
      // the computed-leaf shape: a hidden repeated raw array, then computed leaves that index into it
      // by the repeat-suffixed names. Includes a ternary, as RANDEV's eps battery current does.
      string def = """
      { "default": "main", "endian": "be", "layouts": { "main": { "fields": [
        { "name": "raw", "bits": 16, "repeat": 3, "hidden": true },
        { "name": "bcr_current", "expr": "raw0 * 14.662757", "decimals": 2, "units": "mA" },
        { "name": "bat_current", "expr": "(raw2 < 512) ? -raw1 * 0.0146 : raw1 * 0.0146", "decimals": 3 } ] } } }
      """;
      // raw0=10 → 146.63 mA ; raw1=1000, raw2=100 (<512) → -14.6
      var rec = ParseJson(def, new byte[] { 0, 10, 0x03, 0xE8, 0, 100 });
      rec.Fields.Select(f => f.Name).Should().Equal("bcr_current", "bat_current");   // hidden raw not emitted
      rec.Fields.Single(f => f.Name == "bcr_current").Value.Should().Be("146.63");
      rec.Fields.Single(f => f.Name == "bcr_current").Units.Should().Be("mA");
      rec.Fields.Single(f => f.Name == "bat_current").Value.Should().Be("-14.600");
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(0, false)]
    public void V2_If_GatesFieldOnPriorValue(int flag, bool present)
    {
      string def = """
      { "default": "main", "endian": "be", "layouts": { "main": { "fields": [
        { "name": "flag", "bits": 8, "hidden": true },
        { "name": "opt", "bits": 8, "if": "flag == 1" } ] } } }
      """;
      var rec = ParseJson(def, new byte[] { (byte)flag, 99 });
      rec.Fields.Any(f => f.Name == "opt").Should().Be(present);
      if (present) rec.Fields.Single(f => f.Name == "opt").Value.Should().Be("99");
    }

    [Fact]
    public void Hades_Type14_TimeSeries_NonTempVariable_RawCountsNamedByMinute()
    {
      // header(1) + sclock(4) + variable(1) + 30 sample bytes. variable 0 (peak signal) is not a temperature,
      // so samples are raw counts. They are named by their T-minus minute (oldest T-87 first … newest T-0).
      var f = new byte[1 + 4 + 1 + 30];
      f[0] = 0xE3;          // type 14, address 3
      f[5] = 0;             // variable = peak signal (non-temperature)
      f[6] = 7;             // oldest sample → T-87
      f[35] = 99;           // newest sample → T-0
      var rec = TelemetryParser.Parse(registry.ForNorad(68446)!, f)!;
      rec.Layout.Should().Be("time_series");
      rec.Fields.Count(x => x.Name.StartsWith("T-")).Should().Be(30);
      var t87 = rec.Fields.Single(x => x.Name == "T-87");
      t87.Value.Should().Be("7");
      t87.Units.Should().BeEmpty();      // no calibration → raw counts, no unit
      rec.Fields.Single(x => x.Name == "T-0").Value.Should().Be("99");
    }

    [Fact]
    public void Hades_Type14_TimeSeries_TempVariable_CalibratedToCelsius()
    {
      // variable 5 (mean panel temp) is a temperature → each sample is raw/2 - 40 °C, matching the panel-temp
      // calibration the KissGenesis reference applies (raw 103 → +11.5 °C, raw 96 → +8.0 °C).
      var f = new byte[1 + 4 + 1 + 30];
      f[0] = 0xE3;          // type 14, address 3
      f[5] = 5;             // variable = (tpa+tpb+tpc+tpd)/4
      f[6] = 103;           // oldest sample → T-87
      f[35] = 96;           // newest sample → T-0
      var rec = TelemetryParser.Parse(registry.ForNorad(68446)!, f)!;
      rec.Fields.Single(x => x.Name == "variable").Value.Should().Be("(tpa+tpb+tpc+tpd)/4");
      var t87 = rec.Fields.Single(x => x.Name == "T-87");
      t87.Value.Should().Be("11.5");
      t87.Units.Should().Be("°C");
      rec.Fields.Single(x => x.Name == "T-0").Value.Should().Be("8.0");
    }

    [Fact]
    public void Hades_Type15_Bbs_GroupRepeatReproducesExplicitNames()
    {
      var f = new byte[1 + 5 * (6 + 7 + 1)];
      f[0] = 0xF3;          // type 15, address 3
      System.Text.Encoding.ASCII.GetBytes("ABCDEF").CopyTo(f, 1);   // callsign0
      var rec = TelemetryParser.Parse(registry.ForNorad(68446)!, f)!;
      rec.Layout.Should().Be("bbs");
      rec.Fields.Select(x => x.Name).Should().Contain(
        new[] { "callsign0", "message0", "codec2_frames0", "callsign4", "message4", "codec2_frames4" });
      rec.Fields.Single(x => x.Name == "callsign0").Value.Should().Be("ABCDEF");
    }

    [Fact]
    public void Hades_Type15_Bbs_EmptyStore_RendersSingleMarker()
    {
      // empty-store filler the firmware sends: '-' padding + repeated "No data" + NUL padding (the exact
      // off-air frame). The parser must detect it and emit one "bbs = empty (No data)" marker, not 15 fields.
      var f = Convert.FromHexString(
        "F32D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D" +
        "4E6F20646174614E6F20646174614E6F20646174614E6F20646174614E6F20646174610000000000");
      var rec = TelemetryParser.Parse(registry.ForNorad(68446)!, f)!;
      rec.Layout.Should().Be("bbs");
      rec.Fields.Should().NotContain(x => x.Name.StartsWith("callsign"));
      rec.Fields.Single(x => x.Name == "bbs").Value.Should().Be("empty (No data)");
    }


    // ----------------------------------------------------------------------------------------------------
    //                          USP (converter output, shared across the Sputnix fleet)
    // ----------------------------------------------------------------------------------------------------
    // The generated usp.json models the single-block USP byte contract: skip the 16-byte AX.25 header,
    // dispatch on the u16-le message id, then the per-message layout. Oracle: the gr-satellites QA
    // vectors the deframer is validated against (UspVectors.FrameLongOut / FrameShortOut).

    [Fact]
    public void Usp_SharedDefinition_ResolvesForAllSputnixNorads()
    {
      // UmKA-1 (57172) is included — it is USP-framed, not a special case.
      foreach (var norad in new[] { 57172, 57187, 61772, 57182, 98449, 67290 })
        registry.ForNorad(norad)!.Id.Should().Be("usp", $"NORAD {norad} should resolve to the shared USP def");
    }

    [Fact]
    public void Usp_Beacon_DispatchesAtAx25Offset_AndReadsFirstField()
    {
      var def = registry.ForNorad(57187)!;                  // Svyatobor-1 shares the USP definition
      var bytes = Convert.FromHexString(UspVectors.FrameLongOut);
      var rec = TelemetryParser.Parse(def, bytes)!;
      rec.Type.Should().Be(0x4216);                          // message id = BEACON
      rec.Layout.Should().Be("beacon");

      // Usb1 is the first payload field: u16-le at byte 24 (16 AX.25 header + 8 USP Data header).
      ushort usb1 = (ushort)(bytes[24] | (bytes[25] << 8));
      var f = rec.Fields.Single(x => x.Name == "Usb1");
      f.Value.Should().Be(usb1.ToString());
      f.Units.Should().Be("mV");
    }

    [Fact]
    public void Usp_VersionSw_DispatchesAndDecodesThreeBytes()
    {
      var def = registry.ForNorad(57187)!;
      var rec = TelemetryParser.Parse(def, Convert.FromHexString(UspVectors.FrameShortOut))!;
      rec.Type.Should().Be(0xFFE1);                          // message id = VERSION_SW
      rec.Layout.Should().Be("version_sw");
      rec.Fields.Single(x => x.Name == "major").Value.Should().Be("0");
      rec.Fields.Single(x => x.Name == "minor").Value.Should().Be("38");   // 0x26
      rec.Fields.Single(x => x.Name == "extra").Value.Should().Be("6");
    }
  }
}
