namespace VE3NEA.SkySSTV
{
  /// <summary>
  /// RGB ↔ YCrCb color transform used by SSTV, matching mmsstv (BT.601 studio-swing coefficients).
  /// Components are 0..255; Y is luma, Cr = R-Y, Cb = B-Y, each transmitted on the same
  /// 1500..2300 Hz brightness scale. Keeping encode and decode on identical coefficients makes the
  /// synthetic round-trip exact up to rounding.
  /// </summary>
  public static class YCrCb
  {
    /// <summary>RGB (0..255) → (Y, Cr, Cb), each 0..255.</summary>
    public static (double y, double cr, double cb) FromRgb(double r, double g, double b)
    {
      double y  =  16.0 + (0.003906 * (65.738 * r + 129.057 * g + 25.064 * b));
      double cr = 128.0 + (0.003906 * (112.439 * r + -94.154 * g + -18.285 * b));
      double cb = 128.0 + (0.003906 * (-37.945 * r + -74.494 * g + 112.439 * b));
      return (Clamp(y), Clamp(cr), Clamp(cb));
    }

    /// <summary>(Y, Cr, Cb) (0..255) → RGB, each 0..255 (inverse of <see cref="FromRgb"/>).</summary>
    public static (double r, double g, double b) ToRgb(double y, double cr, double cb)
    {
      double yy = y - 16.0, u = cb - 128.0, v = cr - 128.0;
      double r = 0.003906 * (298.082 * yy + 408.583 * v);
      double g = 0.003906 * (298.082 * yy + -100.291 * u + -208.120 * v);
      double b = 0.003906 * (298.082 * yy + 516.412 * u);
      return (Clamp(r), Clamp(g), Clamp(b));
    }

    private static double Clamp(double v) => v < 0 ? 0 : v > 255 ? 255 : v;
  }
}
