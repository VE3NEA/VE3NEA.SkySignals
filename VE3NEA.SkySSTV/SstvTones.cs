namespace VE3NEA.SkySSTV
{
  /// <summary>
  /// Reference subcarrier tones and VIS-header timing shared by all modes (plan §2, cross-checked
  /// against mmsstv <c>sstv.cpp</c> and the N7CXI "Proposal for SSTV Mode Specifications").
  /// Brightness is encoded as the instantaneous subcarrier frequency: 1500 Hz = black, 2300 Hz =
  /// white, so a component value 0..255 maps linearly onto <see cref="Black"/>..<see cref="White"/>.
  /// </summary>
  public static class SstvTones
  {
    /// <summary>Horizontal sync tone (below black).</summary>
    public const double Sync = 1200.0;

    /// <summary>Black level = lowest brightness tone (also the porch tone after sync).</summary>
    public const double Black = 1500.0;

    /// <summary>Spectral center of the subcarrier (VIS leader tone, separator porch).</summary>
    public const double Center = 1900.0;

    /// <summary>White level = highest brightness tone.</summary>
    public const double White = 2300.0;

    /// <summary>Brightness span in Hz (<see cref="White"/> − <see cref="Black"/>).</summary>
    public const double Span = White - Black;

    // VIS header (standard 8-bit). 16-bit extended VIS is deferred (plan §2).

    /// <summary>Leader tone (1900 Hz), sent for <see cref="VisLeaderMs"/> before and after the break.</summary>
    public const double VisLeaderMs = 300.0;

    /// <summary>Break tone (1200 Hz) separating the two leader halves.</summary>
    public const double VisBreakMs = 10.0;

    /// <summary>Duration of each VIS bit window (start, 7 data, parity, stop).</summary>
    public const double VisBitMs = 30.0;

    /// <summary>VIS start and stop bits are the 1200 Hz tone.</summary>
    public const double VisStartStop = Sync;

    /// <summary>VIS data bit = 1 (1100 Hz).</summary>
    public const double VisBitOne = 1100.0;

    /// <summary>VIS data bit = 0 (1300 Hz).</summary>
    public const double VisBitZero = 1300.0;

    /// <summary>Total header duration: leader + break + leader + 10 bits (start, 7 data, parity, stop).</summary>
    public const double VisHeaderMs = 2 * VisLeaderMs + VisBreakMs + 10 * VisBitMs;

    /// <summary>Map a component value 0..255 to its subcarrier frequency (Hz), clamped.</summary>
    public static double ValueToFreq(double value)
    {
      double v = value < 0 ? 0 : value > 255 ? 255 : value;
      return Black + v / 255.0 * Span;
    }

    /// <summary>Inverse of <see cref="ValueToFreq"/>: subcarrier frequency (Hz) → value 0..255, clamped.</summary>
    public static double FreqToValue(double freq)
    {
      double v = (freq - Black) / Span * 255.0;
      return v < 0 ? 0 : v > 255 ? 255 : v;
    }
  }
}
