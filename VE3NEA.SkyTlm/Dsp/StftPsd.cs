using MathNet.Numerics;
using VE3NEA.SkyTlm.Core;
using VE3NEA;

namespace VE3NEA.SkyTlm.Dsp
{
  /// <summary>
  /// Shared short-time-FFT power machinery for the burst detectors: one windowed FFT per frame,
  /// classified into the in-band PSD (by bin offset from the centre) and an out-of-band noise
  /// reference. Both the batch <see cref="BurstDetector"/> and the streaming detector
  /// (<see cref="Core.StreamingPipeline"/>) need the same per-frame loop; keeping it here stops
  /// them drifting apart.
  /// </summary>
  internal static class StftPsd
  {
    public const int FftSize = 2048;

    /// <summary>Half-width (Hz) of the band that may hold the (possibly CFO-shifted) signal: the
    /// Carson-ish occupied width plus the worst-case residual carrier offset.
    /// For blind FSK/GFSK (deviation unknown) the band is sized to the maximum plausible deviation
    /// (h ≤ 6, dev ≤ 3·baud) so the tones are always captured regardless of the actual deviation.</summary>
    public static double OccupiedHalfHz(SignalParams p, double cfoMaxHz)
    {
      double dev;
      if (p.IsBlind)
      {
        // size to max plausible deviation and keep occHalfHz < fs/2
        double devMax = Math.Min(3.0 * p.Baud, p.SampleRate / 2.0 - p.Baud / 2.0 - cfoMaxHz);
        dev = Math.Max(devMax, p.Baud / 4.0);
      }
      else
        dev = p.Deviation ?? p.Baud / 4.0;
      return (p.Baud + 2 * dev) / 2.0 + cfoMaxHz;
    }

    /// <summary>
    /// Power spectrum of the frame starting at <paramref name="s0"/>: fills <paramref name="q"/>
    /// (length 2·<paramref name="occBins"/>+1) with the in-band bin powers indexed by offset from the
    /// centre bin, and returns the mean out-of-band bin power (the per-bin noise floor for that frame)
    /// plus the count of in-band bins (for noise-floor scaling). <paramref name="q"/> is cleared first.
    /// </summary>
    public static (double oobMean, int inbandBins) Frame(Fft<Complex32> fft, Complex32[] iq, int s0,
      float[] window, double binHz, double occHalfHz, int occBins, float[] q)
    {
      int size = window.Length;
      for (int i = 0; i < size; i++) fft.InputData[i] = iq[s0 + i] * window[i];
      fft.Execute();

      Array.Clear(q);
      double sumOut = 0; int nOut = 0, nIn = 0;
      for (int k = 0; k < size; k++)
      {
        double hz = (k <= size / 2 ? k : k - size) * binHz;
        var c = fft.OutputData[k];
        float pw = c.Real * c.Real + c.Imaginary * c.Imaginary;
        if (Math.Abs(hz) <= occHalfHz)
        {
          int j = (int)Math.Round(hz / binHz) + occBins;
          if ((uint)j < (uint)q.Length) q[j] = pw;
          nIn++;
        }
        else { sumOut += pw; nOut++; }
      }
      return (nOut > 0 ? sumOut / nOut : 0, nIn);
    }
  }
}
