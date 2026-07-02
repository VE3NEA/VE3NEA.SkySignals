namespace VE3NEA.SkySSTV
{
  /// <summary>
  /// Supported SSTV modes: the YCrCb family (Robot + PD). RGB Martin/Scottie are deferred
  /// (rare from satellites) — see the mode plan §1.8.
  /// </summary>
  public enum SstvMode
  {
    Robot36,
    Robot72,
    Pd50,
    Pd90,
    Pd120,
    Pd160,
    Pd180,
    Pd240,
    Pd290
  }

  /// <summary>
  /// Color layout — how a transmitted line maps to image rows and chroma components. All three
  /// are YCrCb; they differ in chroma cadence:
  /// <list type="bullet">
  /// <item><see cref="Robot36"/>: one chroma component per line, alternating R-Y / B-Y on
  /// even / odd lines (chroma vertically subsampled); one image row per transmitted line.</item>
  /// <item><see cref="Robot72"/>: both R-Y and B-Y every line; one image row per transmitted line.</item>
  /// <item><see cref="Pd"/>: one R-Y/B-Y pair shared by two consecutive luma rows; two image rows
  /// per transmitted line (Y-even, R-Y, B-Y, Y-odd).</item>
  /// </list>
  /// </summary>
  public enum SstvColorLayout
  {
    Robot36,
    Robot72,
    Pd
  }
}
