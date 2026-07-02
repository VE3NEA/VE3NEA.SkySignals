namespace VE3NEA.SkySSTV
{
  /// <summary>
  /// Data-driven timing specification for one SSTV mode. All durations are milliseconds. The
  /// encoder and (later) the decoder read these constants rather than hard-coding per-mode logic;
  /// only the color-layout switch differs. See plan §2/§3.
  ///
  /// A transmitted line is: sync (@1200) → sync porch (@1500) → scans, where the scan structure
  /// depends on <see cref="Layout"/>:
  /// <list type="bullet">
  /// <item>Robot36: Y(<see cref="ScanYMs"/>) → separator/porch → one chroma(<see cref="ScanChromaMs"/>).</item>
  /// <item>Robot72: Y → sep/porch → R-Y → sep/porch → B-Y.</item>
  /// <item>PD: Y-even(<see cref="ScanYMs"/>) → R-Y(<see cref="ScanChromaMs"/>) → B-Y → Y-odd, no separators.</item>
  /// </list>
  /// </summary>
  public sealed record SstvModeSpec(
    SstvMode Mode,
    string Name,
    int VisCode,               // 7-bit VIS code; the 8-bit even-parity byte is derived (VisByte)
    SstvColorLayout Layout,
    int Width,
    int Height,
    double SyncMs,             // horizontal sync pulse @ 1200 Hz
    double SyncPorchMs,        // porch after sync @ 1500 Hz
    double SepMs,              // component separator (0 if none, e.g. PD)
    double SepPorchMs,         // porch after a separator @ 1900 Hz
    double ScanYMs,            // luma active scan
    double ScanChromaMs,       // chroma active scan (each of R-Y / B-Y)
    double LinePeriodMs)       // total transmitted-line period (sync..end); sanity + decoder clock
  {
    /// <summary>Image rows carried by one transmitted line: 2 for PD (shared chroma), else 1.</summary>
    public int RowsPerLine => Layout == SstvColorLayout.Pd ? 2 : 1;

    /// <summary>Number of transmitted lines in a full image.</summary>
    public int LineCount => Height / RowsPerLine;

    /// <summary>The 8-bit VIS byte actually transmitted: 7 data bits + even-parity bit in the MSB.</summary>
    public int VisByte => SstvModes.EvenParityByte(VisCode);
  }

  /// <summary>
  /// The mode table and lookups. Robot36/Robot72 constants are the well-known exact values (our real
  /// captures anchor these); PD constants are transcribed from the N7CXI table (line period =
  /// sync + porch + 4·scan) and are flagged for cross-check in plan §8.
  /// </summary>
  public static class SstvModes
  {
    // Common CPM/porch timings.
    private const double RobotSync = 9.0, RobotPorch = 3.0, RobotSep = 4.5, RobotSepPorch = 1.5;
    private const double PdSync = 20.0, PdPorch = 2.08;

    /// <summary>All supported modes, indexed by <see cref="SstvMode"/>.</summary>
    public static readonly IReadOnlyList<SstvModeSpec> All = new[]
    {
      // Robot36: 320×240, ~36 s. One chroma per line, alternating R-Y/B-Y. Line = 9+3+88+4.5+1.5+44 = 150.
      new SstvModeSpec(SstvMode.Robot36, "Robot 36", 0x08, SstvColorLayout.Robot36,
        320, 240, RobotSync, RobotPorch, RobotSep, RobotSepPorch, 88.0, 44.0, 150.0),

      // Robot72: 320×240, ~72 s. Full chroma per line. Line = 9+3+138+4.5+1.5+69+4.5+1.5+69 = 300.
      new SstvModeSpec(SstvMode.Robot72, "Robot 72", 0x0C, SstvColorLayout.Robot72,
        320, 240, RobotSync, RobotPorch, RobotSep, RobotSepPorch, 138.0, 69.0, 300.0),

      // PD modes: YCrCb, two rows share one chroma pair. Line = sync+porch+4·scan. Cross-check pending (§8).
      Pd(SstvMode.Pd50,  "PD 50",  0x5D, 320, 256, 91.520),
      Pd(SstvMode.Pd90,  "PD 90",  0x63, 320, 256, 170.240),
      Pd(SstvMode.Pd120, "PD 120", 0x5F, 640, 496, 121.600),
      Pd(SstvMode.Pd160, "PD 160", 0x62, 512, 400, 195.584),
      Pd(SstvMode.Pd180, "PD 180", 0x60, 640, 496, 183.040),
      Pd(SstvMode.Pd240, "PD 240", 0x61, 640, 496, 244.480),
      Pd(SstvMode.Pd290, "PD 290", 0x5E, 800, 616, 228.800),
    };

    /// <summary>Build a PD mode spec: 4 equal scans, no separators, line = sync + porch + 4·scan.</summary>
    private static SstvModeSpec Pd(SstvMode mode, string name, int vis, int w, int h, double scanMs)
      => new(mode, name, vis, SstvColorLayout.Pd, w, h,
             PdSync, PdPorch, 0.0, 0.0, scanMs, scanMs, PdSync + PdPorch + 4.0 * scanMs);

    /// <summary>Look up a mode's spec.</summary>
    public static SstvModeSpec Get(SstvMode mode) => All[(int)mode];

    /// <summary>Map an 8-bit VIS byte (with parity) to a supported mode, or null if unrecognized.</summary>
    public static SstvModeSpec? FromVisByte(int visByte)
    {
      foreach (var m in All) if (m.VisByte == visByte) return m;
      return null;
    }

    /// <summary>
    /// Compose the 8-bit VIS byte from a 7-bit code: even parity across the 7 data bits, parity in
    /// the MSB (bit 7). Even parity ⇒ the total number of 1s over all 8 bits is even.
    /// </summary>
    public static int EvenParityByte(int code7)
    {
      int code = code7 & 0x7F;
      int ones = System.Numerics.BitOperations.PopCount((uint)code);
      int parity = ones & 1;                 // 1 if odd number of data 1s → set MSB to make it even
      return code | (parity << 7);
    }
  }
}
