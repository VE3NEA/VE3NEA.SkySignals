using System;
using VE3NEA.SkyTlm.Core;

namespace VE3NEA.SkyTlm.Deframing
{
  /// <summary>
  /// Decode configuration for <see cref="CcsdsDeframer"/>. Distinct from <see cref="SignalParams"/>: the
  /// latter carries the resolved <i>facts</i> (nullable = "unknown unless satyaml said so"), this carries
  /// the <i>config</i> with the defaults applied (see <see cref="From"/>). The three CCSDS deframer flavours
  /// (uncoded, RS, concatenated) are one parameterized chain; <see cref="Convolutional"/> <c>null</c> ⇒ the
  /// RS/uncoded path, non-null ⇒ the concatenated (Viterbi) path, and <see cref="RsEnabled"/> distinguishes
  /// RS from uncoded.
  /// </summary>
  public sealed record CcsdsOptions
  {
    /// <summary>TM frame data length in bytes (default 223). Must be divisible by
    /// <see cref="RsInterleaving"/>.</summary>
    public int FrameSize { get; init; } = 223;

    /// <summary>NRZ-I differential precoding on/off (<c>precoding</c>; default None ⇒ false).</summary>
    public bool Precoding { get; init; }

    /// <summary>Reed-Solomon layer on/off (default true; false only for the uncoded block).</summary>
    public bool RsEnabled { get; init; } = true;

    /// <summary>RS field basis: <c>true</c> = dual (CCSDS, <c>decode_rs_ccsds</c>), <c>false</c> = conventional
    /// (<c>decode_rs_8</c>). Default is dual.</summary>
    public bool RsDualBasis { get; init; } = true;

    /// <summary>RS interleaving depth I (default 1).</summary>
    public int RsInterleaving { get; init; } = 1;

    /// <summary>CCSDS additive scrambler on/off (default true).</summary>
    public bool Scrambler { get; init; } = true;

    /// <summary>Convolutional convention for the concatenated path, or <c>null</c> for the RS/uncoded path.
    /// One of <see cref="Conventions"/>; default for the concatenated block is <c>"CCSDS"</c>.</summary>
    public string? Convolutional { get; init; }

    /// <summary>Max syncword bit errors to accept in the 32-bit ASM (default 4).</summary>
    public int SyncThreshold { get; init; } = 4;

    /// <summary>The four convolutional conventions and their libfec viterbi27 generator-polynomial
    /// pairs (a negative polynomial inverts that branch symbol).</summary>
    public static int[] PolysFor(string convolutional) => convolutional switch
    {
      "CCSDS" => new[] { 79, -109 },
      "NASA-DSN" => new[] { -109, 79 },
      "CCSDS uninverted" => new[] { 79, 109 },
      "NASA-DSN uninverted" => new[] { 109, 79 },
      _ => throw new ArgumentException($"unknown convolutional convention '{convolutional}'")
    };

    /// <summary>
    /// Build the decode config from the resolved <see cref="SignalParams"/> facts, substituting the
    /// default for every <c>null</c> — the single place "unknown → concrete default" happens.
    /// The framing-block-pinned facts (<see cref="SignalParams.RsEnabled"/> and whether
    /// <see cref="SignalParams.Convolutional"/> applies) are set by <c>ParamResolver.FramingFrom</c>.
    /// </summary>
    public static CcsdsOptions From(SignalParams p) => new()
    {
      FrameSize = p.FrameSize ?? 223,
      Precoding = p.Differential ?? false,
      RsEnabled = p.RsEnabled ?? true,
      RsDualBasis = !string.Equals(p.RsBasis, "conventional", StringComparison.OrdinalIgnoreCase),
      RsInterleaving = p.RsInterleaving ?? 1,
      Scrambler = p.Scrambler ?? true,
      Convolutional = p.Convolutional,
      SyncThreshold = 4
    };
  }
}
