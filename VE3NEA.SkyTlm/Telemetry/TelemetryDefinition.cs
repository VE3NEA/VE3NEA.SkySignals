using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VE3NEA.SkyTlm.Telemetry
{
  /// <summary>
  /// One satellite's hand-made telemetry layout (a JSON document), deserialized with
  /// <see cref="System.Text.Json"/>. A definition carries file-wide defaults (<see cref="BitOrder"/> /
  /// <see cref="Endian"/>), an optional <see cref="Dispatch"/> that maps a header field's value to a layout
  /// name, and the named <see cref="Layouts"/> themselves. This is data only — the walk lives in
  /// <see cref="TelemetryParser"/>, the bit mechanics in <see cref="BitReader"/>.
  /// </summary>
  public sealed class TelemetryDefinition
  {
    /// <summary>Satellite key (e.g. <c>"hades-sa"</c>); informational, the registry keys by <see cref="Norad"/>.</summary>
    [JsonPropertyName("id")] public string? Id { get; set; }

    /// <summary>NORAD catalog IDs this definition decodes. One file may serve a whole fleet (e.g. the Sputnix
    /// USP satellites all share <c>usp.json</c>). <see cref="TelemetryRegistry"/> indexes the definition by each
    /// of these IDs; an empty list means the definition is never resolved by <see cref="TelemetryRegistry.ForNorad"/>.</summary>
    [JsonPropertyName("norad")] public List<int> Norad { get; set; } = new();

    /// <summary>Bit cursor order; only <c>"msb"</c> (most-significant-bit-first) is supported in v1.</summary>
    [JsonPropertyName("bitOrder")] public string BitOrder { get; set; } = "msb";

    /// <summary>Default byte order for multi-byte integer/float fields: <c>"le"</c> or <c>"be"</c>.</summary>
    [JsonPropertyName("endian")] public string Endian { get; set; } = "be";

    /// <summary>When set, the parser reads <see cref="DispatchDef.Field"/> from the <c>_header</c> layout and
    /// maps its value to the layout to walk; null for single-layout definitions (see <see cref="Default"/>).</summary>
    [JsonPropertyName("dispatch")] public DispatchDef? Dispatch { get; set; }

    /// <summary>Layout name to walk when there is no <see cref="Dispatch"/>; defaults to the sole non-<c>_</c>
    /// layout when omitted.</summary>
    [JsonPropertyName("default")] public string? Default { get; set; }

    /// <summary>Named layouts, each an ordered list of fields. A layout may <c>extends</c> another (shared header).</summary>
    [JsonPropertyName("layouts")] public Dictionary<string, LayoutDef> Layouts { get; set; } = new();

    private static readonly JsonSerializerOptions Opts = new()
    {
      PropertyNameCaseInsensitive = true,
      ReadCommentHandling = JsonCommentHandling.Skip,
      AllowTrailingCommas = true
    };

    /// <summary>Parse a definition from its JSON text. Throws <see cref="JsonException"/> on malformed input.</summary>
    public static TelemetryDefinition Parse(string json) =>
      JsonSerializer.Deserialize<TelemetryDefinition>(json, Opts)
      ?? throw new JsonException("telemetry definition deserialized to null");
  }

  /// <summary>Dispatch rule: read the (hidden) <see cref="Field"/> and map its decimal value to a layout name.</summary>
  public sealed class DispatchDef
  {
    [JsonPropertyName("field")] public string Field { get; set; } = "";

    /// <summary>Value (as a decimal string) → layout name (e.g. <c>{"2":"temps"}</c>).</summary>
    [JsonPropertyName("cases")] public Dictionary<string, string> Cases { get; set; } = new();
  }

  /// <summary>One named layout: an ordered field list, optionally extending a shared base layout.</summary>
  public sealed class LayoutDef
  {
    /// <summary>Base layout whose fields are read first (e.g. a <c>_header</c> with type/address).</summary>
    [JsonPropertyName("extends")] public string? Extends { get; set; }

    [JsonPropertyName("fields")] public List<FieldDef> Fields { get; set; } = new();
  }

  /// <summary>
  /// One field — the entire "language": a sequential read of <see cref="Bits"/>/<see cref="Bytes"/>
  /// bits as <see cref="Type"/>, then optional linear calibration (<see cref="Scale"/>/<see cref="Offset"/>/
  /// <see cref="Decimals"/>), an <see cref="Enum"/>/<see cref="Special"/> map, or a named <see cref="Transform"/>,
  /// formatted with <see cref="Units"/>. <see cref="Pos"/>/<see cref="Skip"/> (in bytes) reposition the cursor;
  /// <see cref="Hidden"/> fields are read (for dispatch/padding) but not emitted.
  /// </summary>
  public sealed class FieldDef
  {
    [JsonPropertyName("name")] public string Name { get; set; } = "";

    /// <summary>Field width in bits (1…64). Mutually exclusive with <see cref="Bytes"/>.</summary>
    [JsonPropertyName("bits")] public int? Bits { get; set; }

    /// <summary>Field width in bytes (convenience for <c>bits = bytes*8</c>); also the length for str/bytes.</summary>
    [JsonPropertyName("bytes")] public int? Bytes { get; set; }

    /// <summary><c>uint</c> (default) | <c>int</c> | <c>float</c> | <c>str</c> | <c>bytes</c> | <c>bool</c>.</summary>
    [JsonPropertyName("type")] public string Type { get; set; } = "uint";

    /// <summary>Per-field byte-order override (<c>"le"</c>/<c>"be"</c>); defaults to the definition's <c>endian</c>.</summary>
    [JsonPropertyName("endian")] public string? Endian { get; set; }

    [JsonPropertyName("scale")] public double? Scale { get; set; }
    [JsonPropertyName("offset")] public double? Offset { get; set; }
    [JsonPropertyName("decimals")] public int? Decimals { get; set; }

    /// <summary>Integer value (decimal string) → label; renders the label instead of the number.</summary>
    [JsonPropertyName("enum")] public Dictionary<string, string>? Enum { get; set; }

    /// <summary>Sentinel raw value (decimal string) → text (e.g. <c>{"255":"ERROR"}</c>); takes precedence over calibration.</summary>
    [JsonPropertyName("special")] public Dictionary<string, string>? Special { get; set; }

    /// <summary>Named transform applied to the raw value: <c>unixtime</c> (extend as a target needs it).</summary>
    [JsonPropertyName("transform")] public string? Transform { get; set; }

    /// <summary>Absolute byte offset to seek to before reading this field.</summary>
    [JsonPropertyName("pos")] public int? Pos { get; set; }

    /// <summary>Bytes to advance the cursor by before reading this field.</summary>
    [JsonPropertyName("skip")] public int? Skip { get; set; }

    [JsonPropertyName("units")] public string? Units { get; set; }

    /// <summary>Read but do not emit (dispatch keys, padding).</summary>
    [JsonPropertyName("hidden")] public bool Hidden { get; set; }

    /// <summary>
    /// Schema v2: nonlinear/cross-field calibration as a constrained expression over <c>x</c>
    /// (this field's raw value) and previously-read field names, evaluated by <see cref="ExprEvaluator"/>.
    /// Takes precedence over <see cref="Scale"/>/<see cref="Offset"/> when present.
    /// </summary>
    [JsonPropertyName("expr")] public string? Expr { get; set; }

    /// <summary>
    /// Schema v2: presence condition — the field/group is read only when this expression (over prior field
    /// names) evaluates non-zero. Null means always present.
    /// </summary>
    [JsonPropertyName("if")] public string? If { get; set; }

    /// <summary>
    /// Schema v2: repeat this field a fixed <c>count</c> or <c>"untilEof"</c>. A leaf repeats its read,
    /// a group (<see cref="Fields"/>) repeats the whole block; emitted names get the element index appended.
    /// </summary>
    [JsonPropertyName("repeat")] public RepeatSpec? Repeat { get; set; }

    /// <summary>
    /// Schema v2: for a <c>repeat</c>ed field, an expression over the element index <c>i</c> whose integer value
    /// is the suffix appended to each element's name (instead of the bare index) — e.g. <c>"(29 - i) * 3"</c>
    /// names HADES time-series samples by their T-minus minute. Null appends the index itself.
    /// </summary>
    [JsonPropertyName("indexExpr")] public string? IndexExpr { get; set; }

    /// <summary>
    /// Schema v2: when present this field is a <i>group</i> — a named, repeatable sub-sequence of fields
    /// rather than a scalar read (USP message blocks, HADES bbs entries). A group has no bits/type of its
    /// own; its <see cref="Name"/> is a label and the nested fields carry the element index suffix.
    /// </summary>
    [JsonPropertyName("fields")] public List<FieldDef>? Fields { get; set; }

    /// <summary>True when this field is a group (a nested field list) rather than a scalar leaf.</summary>
    [JsonIgnore] public bool IsGroup => Fields != null;

    /// <summary>
    /// Schema v2: true when this field is a <i>computed</i> (zero-width) leaf — an <see cref="Expr"/> with no
    /// <see cref="Bits"/>/<see cref="Bytes"/>. It reads no wire bits; its value is the expression evaluated over
    /// previously-read field names (the home for derived calibrated values).
    /// </summary>
    [JsonIgnore] public bool IsComputed => !IsGroup && Expr != null && Bits == null && Bytes == null;

    /// <summary>Resolved width in bits (<see cref="Bits"/>, else <see cref="Bytes"/>×8).</summary>
    [JsonIgnore]
    public int WidthBits => Bits ?? (Bytes is int n ? n * 8 : throw new InvalidOperationException(
      $"field '{Name}' has neither bits nor bytes"));
  }

  /// <summary>
  /// A <c>repeat</c> value: either a fixed <see cref="Count"/> (JSON integer) or <see cref="UntilEof"/>
  /// (JSON string <c>"untilEof"</c>). Parsed by <see cref="RepeatSpecConverter"/>.
  /// </summary>
  [JsonConverter(typeof(RepeatSpecConverter))]
  public sealed class RepeatSpec
  {
    public int? Count { get; init; }
    public bool UntilEof { get; init; }
  }

  /// <summary>Accepts a JSON integer (fixed count) or the string <c>"untilEof"</c> for a <see cref="RepeatSpec"/>.</summary>
  public sealed class RepeatSpecConverter : JsonConverter<RepeatSpec>
  {
    public override RepeatSpec Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
      if (reader.TokenType == JsonTokenType.Number)
        return new RepeatSpec { Count = reader.GetInt32() };
      if (reader.TokenType == JsonTokenType.String)
      {
        string s = reader.GetString() ?? "";
        if (s.Equals("untilEof", StringComparison.OrdinalIgnoreCase)) return new RepeatSpec { UntilEof = true };
        throw new JsonException($"repeat: expected an integer or \"untilEof\", got \"{s}\"");
      }
      throw new JsonException("repeat: expected an integer or \"untilEof\"");
    }

    public override void Write(Utf8JsonWriter writer, RepeatSpec value, JsonSerializerOptions options)
    {
      if (value.UntilEof) writer.WriteStringValue("untilEof");
      else writer.WriteNumberValue(value.Count ?? 0);
    }
  }
}
