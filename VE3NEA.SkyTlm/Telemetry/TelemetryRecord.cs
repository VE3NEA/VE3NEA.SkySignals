using System.Collections.Generic;

namespace VE3NEA.SkyTlm.Telemetry
{
  /// <summary>
  /// One decoded telemetry field: a human-readable triple — name, formatted/calibrated
  /// value, and units (e.g. <c>("TCPU", "8.5", "°C")</c>). <see cref="Units"/> is empty when the field is
  /// unitless (counters, flags, callsigns).
  /// </summary>
  public sealed record TelemetryField(string Name, string Value, string Units);

  /// <summary>
  /// The result of parsing one <see cref="Core.Frame"/> against a satellite's telemetry definition: the
  /// frame <see cref="Type"/> (the dispatch key, e.g. 2 for a HADES type-2 packet; null when the definition
  /// has no dispatch), the <see cref="Layout"/> name that was selected (e.g. <c>"temps"</c>, or a label such
  /// as <c>"CODEC2"</c> for the field-less special types), and the ordered list of emitted
  /// <see cref="TelemetryField"/>s (hidden dispatch/padding fields are not included; empty for label-only
  /// layouts).
  /// </summary>
  public sealed record TelemetryRecord(string Layout, IReadOnlyList<TelemetryField> Fields, int? Type = null);
}
