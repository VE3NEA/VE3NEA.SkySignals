namespace VE3NEA.SkyTlm.Dsp
{
  /// <summary>
  /// A spectral shape profile: the normalized power-spectrum (peak = 1) on a baud-normalized frequency grid,
  /// plus the tone deviation and RMS bandwidth measured from it. Carries both the <b>modeled</b> expected
  /// spectrum (<c>CpmTemplate.Synthesize</c>) and a burst's <b>measured</b> averaged spectrum
  /// (<c>CfoEstimator.EstimateShape*</c>), so the two can be correlated for burst validation and rendered in
  /// the shape view. (Historically also persisted by an auto-learning store — removed: the modeled shape
  /// plus the curated transmitter deviation override proved sufficient for detection.)
  /// </summary>
  public sealed record LearnedShape
  {
    /// <summary>Grid half-width in baud units: the profile spans [−4·Rs, +4·Rs] about the carrier (covers
    /// tones out to h≈8).</summary>
    public const double GridHalfWidthBaud = 4.0;
    /// <summary>Profile sample count over the grid (0.05·Rs spacing).</summary>
    public const int GridPoints = 161;

    public required double DeviationHz { get; init; }
    public required double BandwidthHz { get; init; }
    /// <summary>Half-width (in baud units) over which the profile carries <b>real measured data</b>: a measured
    /// burst spectrum is only built across the detector's occupied band, so beyond ±this the profile is hard
    /// zero (no data, NOT a true spectral floor). A shape correlation must not treat that band-limit edge as a
    /// modulation skirt — see <c>CpmTemplate.Match</c>. Defaults to the full grid (a synthesized template is
    /// valid everywhere).</summary>
    public double ValidHalfBaud { get; init; } = GridHalfWidthBaud;
    /// <summary>Normalized PSD (peak = 1), index <c>i</c> ↔ offset <see cref="BaudAt"/>(i)·Rs from the carrier.</summary>
    public required float[] Profile { get; init; }
    /// <summary>Number of measurements blended into this shape (1 for a single burst or a synthesized template).</summary>
    public int Count { get; init; }

    /// <summary>Frequency (in baud units) at profile index <paramref name="i"/>.</summary>
    public static double BaudAt(int i) => -GridHalfWidthBaud + 2.0 * GridHalfWidthBaud * i / (GridPoints - 1);

    /// <summary>Sample the normalized profile at a baud-offset <paramref name="fb"/> (linear interp; 0 outside the grid).</summary>
    public double SampleAtBaud(double fb)
    {
      double x = (fb + GridHalfWidthBaud) / (2.0 * GridHalfWidthBaud) * (GridPoints - 1);
      if (x < 0 || x > GridPoints - 1) return 0;
      int i = (int)Math.Floor(x);
      if (i >= GridPoints - 1) return Profile[GridPoints - 1];
      double mu = x - i;
      return Profile[i] * (1 - mu) + Profile[i + 1] * mu;
    }
  }
}
