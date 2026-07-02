using MathNet.Numerics;
using VE3NEA;

namespace VE3NEA.SkyTlm.Dsp
{
  /// <summary>An STFT power spectrogram, ready for a ScottPlot heatmap.</summary>
  public sealed class SpectrogramResult
  {
    /// <summary>dB magnitudes indexed [freqBin, timeFrame]; bin 0 = −Fs/2, last = just below +Fs/2.</summary>
    public required double[,] Db { get; init; }
    public required int Bins { get; init; }
    public required int Frames { get; init; }
    public required double DurationSeconds { get; init; }
    public required double SampleRate { get; init; }

    /// <summary>Suggested heatmap color floor (dB) — noise level, mapped to the dark end for contrast.</summary>
    public required double DbLow { get; init; }
    /// <summary>Suggested heatmap color ceiling (dB) — signal peaks, mapped to the bright end.</summary>
    public required double DbHigh { get; init; }
  }

  /// <summary>
  /// Computes a centered (fftshift) STFT magnitude spectrogram of a complex IQ
  /// recording using the shared <see cref="Fft{T}"/> / FFTW wrapper.
  /// </summary>
  public static class Spectrogram
  {
    public static SpectrogramResult Compute(Complex32[] iq, double sampleRate, int fftSize = 1024)
    {
      if (iq.Length < fftSize)
        throw new ArgumentException($"Recording shorter than one FFT frame ({iq.Length} < {fftSize}).");

      // fixed 50%-overlap STFT (hop = fftSize/2): full, contiguous time coverage so the heatmap holds the
      // real detail and can be zoomed in, rather than decimating to a fixed frame budget. For a few-minute
      // pass this is on the order of 10⁴–10⁵ frames (~hundreds of MB at fftSize 1024), which is fine for the
      // in-memory recordings this tool works with.
      int hop = fftSize / 2;
      int frames = (iq.Length - fftSize) / hop + 1;
      int half = fftSize / 2;

      float[] window = global::VE3NEA.Dsp.BlackmanHarrisWindow(fftSize);
      var db = new double[fftSize, frames];

      using var fft = new Fft<Complex32>(fftSize);
      float scale = 1f / fftSize;

      for (int f = 0; f < frames; f++)
      {
        int start = f * hop;
        for (int i = 0; i < fftSize; i++)
          fft.InputData[i] = iq[start + i] * window[i];

        fft.Execute();

        for (int r = 0; r < fftSize; r++)
        {
          int bin = (r + half) % fftSize; // fftshift: row 0 = most-negative frequency
          float mag = fft.OutputData[bin].Magnitude * scale;
          db[r, f] = 20.0 * Math.Log10(mag + 1e-12);
        }
      }

      // color range from percentiles: noise floor (p75) → dark, signal peaks (p99.8) → bright.
      // the high low-percentile pushes most of the noise to the dark end (low brightness)
      // and the tight window stretches the signal for high contrast.
      var (low, high) = PercentileRange(db, 0.75, 0.998);

      return new SpectrogramResult
      {
        Db = db,
        Bins = fftSize,
        Frames = frames,
        DurationSeconds = iq.Length / sampleRate,
        SampleRate = sampleRate,
        DbLow = low,
        DbHigh = high
      };
    }

    /// <summary>Estimates two percentiles of the spectrogram from a subsample (cheap, ~uniform).</summary>
    private static (double low, double high) PercentileRange(double[,] db, double pLow, double pHigh)
    {
      int total = db.Length;
      int stride = Math.Max(1, total / 100_000); // cap the sort at ~100k samples
      var sample = new List<double>(total / stride + 1);
      int k = 0;
      foreach (double v in db)
      {
        if (k++ % stride == 0 && !double.IsNaN(v) && !double.IsInfinity(v))
          sample.Add(v);
      }
      sample.Sort();
      double At(double p) => sample[Math.Clamp((int)(p * (sample.Count - 1)), 0, sample.Count - 1)];
      double low = At(pLow), high = At(pHigh);
      if (high <= low) high = low + 1.0; // guard degenerate (silent) recordings
      return (low, high);
    }
  }
}
