namespace VE3NEA.SkyTlm.Core
{
  /// <summary>
  /// A detected signal burst: its sample span, the residual carrier offset estimated
  /// over it (CFO, Hz from DC), and a rough in-band SNR.
  /// </summary>
  public sealed record Burst(int StartSample, int EndSample, double SampleRate, double CfoHz, double SnrDb)
  {
    /// <summary>Cosine match of the burst PSD to the expected modulation shape (0..1); low for CW/SSTV.</summary>
    public double ShapeScore { get; init; }
    /// <summary>RMS bandwidth of the burst spectrum (Hz); narrow for CW, wide for a real FSK burst.</summary>
    public double BandwidthHz { get; init; }

    public double StartSeconds => StartSample / SampleRate;
    public double EndSeconds => EndSample / SampleRate;
    public double DurationSeconds => (EndSample - StartSample) / SampleRate;
  }
}
