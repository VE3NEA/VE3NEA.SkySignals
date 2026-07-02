using System;
using System.Collections.Generic;
using System.Globalization;

namespace VE3NEA.SkyTlm.Telemetry
{
  /// <summary>
  /// Walks a <see cref="TelemetryDefinition"/> over a frame's bytes: resolve the layout (via
  /// <see cref="DispatchDef"/> or the single/<c>default</c> layout), read each field with a
  /// <see cref="BitReader"/>, then apply calibration / <c>enum</c> / <c>special</c> / named
  /// <c>transform</c> and format the value with its units. Schema v2 adds <c>repeat</c> (fixed count or
  /// <c>untilEof</c>), <c>fields</c> groups (repeatable sub-sequences), <c>if</c> conditional presence,
  /// and <c>expr</c> nonlinear/cross-field calibration (<see cref="ExprEvaluator"/>). Raw (pre-calibration)
  /// numeric values of every field are recorded as they are read so later <c>expr</c>/<c>if</c> can
  /// reference them by name. Returns <c>null</c> when dispatch matches no known layout.
  /// </summary>
  public static class TelemetryParser
  {
    private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;
    private static readonly DateTime UnixEpoch = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public static TelemetryRecord? Parse(TelemetryDefinition def, byte[] bytes)
    {
      string layoutName;
      int? type = null;
      if (def.Dispatch is { } d)
      {
        long? key = ReadDispatchKey(def, d.Field, bytes);
        if (key is not long k) return null;                                     // frame too short for the header
        if (!d.Cases.TryGetValue(k.ToString(Ci), out var ln)) return null;      // unknown/unsupported type
        layoutName = ln;
        type = (int)k;
      }
      else layoutName = def.Default ?? SoleLayout(def);

      var fields = EffectiveFields(def, layoutName);
      var reader = new BitReader(bytes);
      var emitted = new List<TelemetryField>();
      var raw = new Dictionary<string, double>();

      // frame type (id and name) as the first emitted name-value pair, for dispatch definitions
      if (type is int ft) emitted.Add(new TelemetryField("frame type", $"{ft} ({layoutName})", ""));

      // HADES fills an empty BBS store with filler ('-' padding + the repeated ASCII sentinel "No data")
      // rather than real records; render that as a single marker instead of slicing the filler into 15
      // meaningless callsign/message/count fields.
      if (layoutName == "bbs" && IsEmptyBbs(bytes))
        emitted.Add(new TelemetryField("bbs", "empty (No data)", ""));
      else
        // a frame truncated below the layout's length keeps the fields read so far rather than crashing the
        // caller; the untilEof repeat path tolerates the same overrun (see RepeatField).
        try { WalkFields(reader, fields, def, emitted, raw, suffix: ""); }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentOutOfRangeException) { }

      return new TelemetryRecord(layoutName, emitted, type);
    }

    /// <summary>
    /// Walk an ordered field list, appending emitted fields. <paramref name="suffix"/> carries the
    /// accumulated repeat-element index(es) appended to each emitted name; <paramref name="raw"/> holds
    /// the raw numeric value of every field read so far, for <c>expr</c>/<c>if</c> references.
    /// </summary>
    private static void WalkFields(BitReader r, List<FieldDef> fields, TelemetryDefinition def,
      List<TelemetryField> emitted, Dictionary<string, double> raw, string suffix)
    {
      foreach (var f in fields)
      {
        if (f.If is string cond && ExprEvaluator.Eval(cond, Lookup(raw, null)) == 0) continue;

        if (f.Repeat is { } rep) RepeatField(r, f, def, emitted, raw, suffix, rep);
        else ReadElement(r, f, def, emitted, raw, suffix);
      }
    }

    /// <summary>Repeat one field a fixed count or until the frame is exhausted.</summary>
    private static void RepeatField(BitReader r, FieldDef f, TelemetryDefinition def,
      List<TelemetryField> emitted, Dictionary<string, double> raw, string suffix, RepeatSpec rep)
    {
      if (rep.Count is int n)
      {
        for (int i = 0; i < n; i++) ReadElement(r, f, def, emitted, raw, suffix + ElementSuffix(f, raw, i));
        return;
      }

      // untilEof: read elements while bits remain; roll an element back if it overruns, then stop.
      for (int i = 0; r.BitPos < r.BitLength; i++)
      {
        int startPos = r.BitPos, startEmit = emitted.Count;
        try { ReadElement(r, f, def, emitted, raw, suffix + ElementSuffix(f, raw, i)); }
        catch (InvalidOperationException)
        {
          r.SeekBits(startPos);
          if (emitted.Count > startEmit) emitted.RemoveRange(startEmit, emitted.Count - startEmit);
          break;
        }
        if (r.BitPos == startPos) break;   // element consumed no bits — guard against an infinite loop
      }
    }

    /// <summary>
    /// The per-element name suffix: record the element index as <c>i</c> (so the element's <c>expr</c>/<c>if</c>
    /// can reference it), then return the suffix to append — the index itself, or, when <c>indexExpr</c> is set,
    /// that expression's integer value over <c>i</c> (e.g. HADES time-series samples named by their T-minus minute).
    /// </summary>
    private static string ElementSuffix(FieldDef f, Dictionary<string, double> raw, int i)
    {
      raw["i"] = i;
      if (f.IndexExpr is string e)
        return ((long)ExprEvaluator.Eval(e, Lookup(raw, null))).ToString(Ci);
      return i.ToString(Ci);
    }

    /// <summary>Read one element: recurse into a group's fields, or read a single leaf field.</summary>
    private static void ReadElement(BitReader r, FieldDef f, TelemetryDefinition def,
      List<TelemetryField> emitted, Dictionary<string, double> raw, string suffix)
    {
      if (f.Pos is int p) r.SeekBytes(p);
      else if (f.Skip is int s) r.SkipBytes(s);

      if (f.IsGroup) WalkFields(r, f.Fields!, def, emitted, raw, suffix);
      else if (f.IsComputed) ReadComputed(f, emitted, raw, suffix);
      else ReadLeaf(r, f, def, emitted, raw, suffix);
    }

    /// <summary>
    /// Emit a computed (zero-width) leaf: evaluate <c>expr</c> over already-read field names (no <c>x</c>, as
    /// the field reads no bits) without advancing the reader. The result is recorded as a raw value too, so a
    /// later computed field can chain off it. This is the runtime home for computed fields.
    /// </summary>
    private static void ReadComputed(FieldDef f, List<TelemetryField> emitted,
      Dictionary<string, double> raw, string suffix)
    {
      string name = f.Name + suffix;
      double v = ExprEvaluator.Eval(f.Expr!, Lookup(raw, null));
      raw[name] = v;
      if (f.Hidden) return;
      string text = f.Decimals is int d ? v.ToString("F" + d, Ci) : v.ToString("0.######", Ci);
      emitted.Add(new TelemetryField(name, text, f.Units ?? ""));
    }

    /// <summary>Read one scalar field, record its raw value, and (unless hidden) emit the formatted field.</summary>
    private static void ReadLeaf(BitReader r, FieldDef f, TelemetryDefinition def,
      List<TelemetryField> emitted, Dictionary<string, double> raw, string suffix)
    {
      bool le = ResolveLittleEndian(f.Endian ?? def.Endian);
      int bits = f.WidthBits;
      string type = f.Type.ToLowerInvariant();
      string name = f.Name + suffix;

      switch (type)
      {
        case "uint":
        case "int":
        {
          long v = type == "int" ? r.ReadInt(bits, le) : (long)r.ReadUInt(bits, le);
          raw[name] = v;
          if (f.Hidden) return;
          emitted.Add(new TelemetryField(name, FormatNumber(v, f, raw), Units(f, v)));
          return;
        }
        case "float":
        {
          double v = r.ReadFloat(bits, le);
          raw[name] = v;
          if (f.Hidden) return;
          emitted.Add(new TelemetryField(name, FormatCalibrated(v, f, raw, isFloat: true), f.Units ?? ""));
          return;
        }
        case "bool":
        {
          ulong v = r.ReadUInt(bits, le);
          raw[name] = v;
          if (f.Hidden) return;
          string s = f.Enum != null && f.Enum.TryGetValue(v.ToString(Ci), out var lbl) ? lbl
                   : v != 0 ? "ON" : "OFF";
          emitted.Add(new TelemetryField(name, s, ""));
          return;
        }
        case "str":
        {
          string s = r.ReadAscii(bits / 8);
          if (f.Hidden) return;
          emitted.Add(new TelemetryField(name, s, f.Units ?? ""));
          return;
        }
        case "bytes":
        {
          byte[] b = r.ReadBytes(bits / 8);
          if (f.Hidden) return;
          emitted.Add(new TelemetryField(name, Convert.ToHexString(b), f.Units ?? ""));
          return;
        }
        default:
          throw new NotSupportedException($"field '{f.Name}': unsupported type '{f.Type}'");
      }
    }

    /// <summary>Format an integer raw value: special sentinel → text, transform, enum label, else calibration.</summary>
    private static string FormatNumber(long raw, FieldDef f, Dictionary<string, double> vars)
    {
      string key = raw.ToString(Ci);
      if (f.Special != null && f.Special.TryGetValue(key, out var sp)) return sp;
      if (f.Transform != null) return ApplyTransform(f.Transform, raw);
      if (f.Enum != null && f.Enum.TryGetValue(key, out var lbl)) return lbl;
      return FormatCalibrated(raw, f, vars, isFloat: false);
    }

    /// <summary>Units to show: cleared for sentinel/transform/enum-mapped values, else the field's units.</summary>
    private static string Units(FieldDef f, long raw)
    {
      string key = raw.ToString(Ci);
      bool replaced = (f.Special != null && f.Special.ContainsKey(key))
                   || f.Transform != null
                   || (f.Enum != null && f.Enum.ContainsKey(key));
      return replaced ? "" : f.Units ?? "";
    }

    /// <summary>
    /// Calibration: <c>expr</c> (over <c>x</c> = raw and prior field names) when present, else linear
    /// <c>value = raw*scale + offset</c>; formatted to <c>decimals</c> places.
    /// </summary>
    private static string FormatCalibrated(double raw, FieldDef f, Dictionary<string, double> vars, bool isFloat)
    {
      if (f.Expr is string e)
      {
        double ev = ExprEvaluator.Eval(e, Lookup(vars, raw));
        return f.Decimals is int d ? ev.ToString("F" + d, Ci) : ev.ToString("0.######", Ci);
      }

      bool hasCal = f.Scale != null || f.Offset != null;
      double v = raw * (f.Scale ?? 1.0) + (f.Offset ?? 0.0);
      if (f.Decimals is int dec) return v.ToString("F" + dec, Ci);
      if (hasCal || isFloat) return v.ToString("0.######", Ci);
      return ((long)raw).ToString(Ci);   // raw integer, no calibration
    }

    /// <summary>
    /// Identifier resolver for <see cref="ExprEvaluator"/>: <c>x</c> is the current field's raw value
    /// (null in an <c>if</c> condition, where the field has not been read), any other name is a prior
    /// field's recorded raw value.
    /// </summary>
    private static Func<string, double> Lookup(Dictionary<string, double> vars, double? x) => id =>
    {
      if (id == "x")
        return x ?? throw new InvalidOperationException("expr: 'x' is not available in an 'if' condition");
      if (vars.TryGetValue(id, out var v)) return v;
      throw new KeyNotFoundException($"expr: unknown field '{id}'");
    };

    private static string ApplyTransform(string transform, long raw) => transform.ToLowerInvariant() switch
    {
      "unixtime" => UnixEpoch.AddSeconds(raw).ToString("yyyy-MM-dd HH:mm:ss", Ci) + " UTC",
      "unixtime_us" => UnixEpoch.AddSeconds(raw / 1_000_000.0).ToString("yyyy-MM-dd HH:mm:ss", Ci) + " UTC",
      _ => throw new NotSupportedException($"unsupported transform '{transform}'")
    };

    /// <summary>
    /// True when a HADES type-15 BBS frame carries an <i>empty</i> message store: the firmware fills it with
    /// '-' padding and the repeated ASCII sentinel "No data" (then NUL/zero padding) instead of real
    /// callsign/message records. A real BBS frame carries callsigns with digits/other letters outside this
    /// filler alphabet, so it is never misflagged.
    /// </summary>
    private static bool IsEmptyBbs(byte[] bytes)
    {
      if (bytes.Length < 2) return false;
      // every payload byte (after the 1-byte type/address header) must be filler: '-', NUL, or a letter of "No data".
      for (int i = 1; i < bytes.Length; i++)
      {
        byte b = bytes[i];
        if (b is 0x00 or 0x2D) continue;
        if (!"No data".Contains((char)b)) return false;
      }
      // require the sentinel itself, so a frame of only dashes/NULs is not mislabeled as the BBS-empty case.
      return System.Text.Encoding.ASCII.GetString(bytes, 1, bytes.Length - 1).Contains("No data");
    }

    // --- layout resolution ------------------------------------------------------------------------------

    /// <summary>
    /// Read the dispatch key by walking the <c>_header</c> layout to the named field. Returns <c>null</c> when
    /// the frame is too short to contain the header (a noise/truncated frame), so the caller treats it as
    /// un-parseable rather than crashing.
    /// </summary>
    private static long? ReadDispatchKey(TelemetryDefinition def, string fieldName, byte[] bytes)
    {
      var header = EffectiveFields(def, "_header");
      var r = new BitReader(bytes);
      foreach (var f in header)
      {
        if (f.Pos is int p && p * 8 > r.BitLength) return null;
        if (f.Pos is int pp) r.SeekBytes(pp);
        else if (f.Skip is int s) { if (r.BitPos + s * 8 > r.BitLength) return null; r.SkipBytes(s); }
        if (r.BitPos + f.WidthBits > r.BitLength) return null;
        bool le = ResolveLittleEndian(f.Endian ?? def.Endian);
        ulong v = r.ReadUInt(f.WidthBits, le);
        if (f.Name == fieldName) return (long)v;
      }
      throw new InvalidOperationException($"dispatch field '{fieldName}' not found in _header layout");
    }

    /// <summary>Expand a layout's <c>extends</c> chain into the full ordered field list.</summary>
    private static List<FieldDef> EffectiveFields(TelemetryDefinition def, string name)
    {
      if (!def.Layouts.TryGetValue(name, out var layout))
        throw new KeyNotFoundException($"layout '{name}' not found in definition '{def.Id}'");
      var list = new List<FieldDef>();
      if (layout.Extends != null) list.AddRange(EffectiveFields(def, layout.Extends));
      list.AddRange(layout.Fields);
      return list;
    }

    /// <summary>The single user-facing layout (name not starting with '_') for a dispatch-less definition.</summary>
    private static string SoleLayout(TelemetryDefinition def)
    {
      string? only = null;
      foreach (var key in def.Layouts.Keys)
      {
        if (key.StartsWith('_')) continue;
        if (only != null) throw new InvalidOperationException(
          $"definition '{def.Id}' has multiple layouts but no dispatch or default");
        only = key;
      }
      return only ?? throw new InvalidOperationException($"definition '{def.Id}' has no layout to walk");
    }

    private static bool ResolveLittleEndian(string endian) => endian.ToLowerInvariant() switch
    {
      "le" => true,
      "be" => false,
      _ => throw new ArgumentException($"unknown endian '{endian}' (expected 'le' or 'be')")
    };
  }
}
