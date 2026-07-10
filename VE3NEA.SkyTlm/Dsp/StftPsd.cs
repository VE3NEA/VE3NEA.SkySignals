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

    /// <summary>Largest fraction of the Nyquist band the occupied window may take; the remainder is the
    /// out-of-band CFAR noise reference. Without this margin a wide blind hypothesis (e.g. 9k6 at 48 kHz)
    /// drives occHalfHz to exactly fs/2, the OOB bin set becomes empty, the rolling noise floor collapses
    /// to ~0 and the detector fires on every noise frame (KNACKSAT-2 burst storm, 2026-07-10).</summary>
    public const double MaxOccupiedFrac = 0.8;

    /// <summary>Half-width (Hz) of the band that may hold the (possibly CFO-shifted) signal: the
    /// Carson-ish occupied width plus the worst-case residual carrier offset.
    /// For blind FSK/GFSK (deviation unknown) the band is sized to the maximum plausible deviation
    /// (h ≤ 6, dev ≤ 3·baud) so the tones are always captured regardless of the actual deviation.
    /// Always capped at <see cref="MaxOccupiedFrac"/>·fs/2 so a noise reference band remains.</summary>
    public static double OccupiedHalfHz(SignalParams p, double cfoMaxHz)
    {
      double occMaxHz = MaxOccupiedFrac * p.SampleRate / 2.0;
      double dev;
      if (p.IsBlind)
      {
        // size to max plausible deviation and keep occHalfHz within the capped band
        double devMax = Math.Min(3.0 * p.Baud, occMaxHz - p.Baud / 2.0 - cfoMaxHz);
        dev = Math.Max(devMax, p.Baud / 4.0);
      }
      else
        dev = p.Deviation ?? p.Baud / 4.0;
      return Math.Min((p.Baud + 2 * dev) / 2.0 + cfoMaxHz, occMaxHz);
    }

    /// <summary>
    /// Power spectrum of the frame starting at <paramref name="s0"/>: fills <paramref name="q"/>
    /// (length 2·<paramref name="occBins"/>+1) with the in-band bin powers indexed by offset from the
    /// centre bin, and returns the mean out-of-band bin power (the per-bin noise floor for that frame)
    /// plus the count of in-band bins (for noise-floor scaling). <paramref name="q"/> is cleared first.
    /// When <paramref name="fullShifted"/> is non-null (length <paramref name="size"/>) it is additionally
    /// filled with the whole fftshifted per-bin power (DC at index size/2) — the diagnostic Detection
    /// Inspector spectrum, un-notched and full-band; leave it null on the production path (no extra work).
    /// </summary>
    public static (double oobMean, int inbandBins) Frame(Fft<Complex32> fft, Complex32[] iq, int s0,
      float[] window, double binHz, double occHalfHz, int occBins, float[] q, float[]? fullShifted = null)
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
        if (fullShifted != null) fullShifted[(k + size / 2) % size] = pw;   // fftshift: DC → centre
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
