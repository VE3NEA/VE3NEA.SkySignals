using MathNet.Numerics;
using VE3NEA.SkyTlm.Core;
using VE3NEA;

namespace VE3NEA.SkyTlm.Dsp
{
  /// <summary>Tunables for the energy burst detector.</summary>
  public sealed record BurstDetectorOptions(
    double CfoMaxHz = 2000,      // worst-case residual carrier offset to keep in-band
    double OnThresholdDb = 3,      // schmitt onset, dB above noise floor
    double OffThresholdDb = 1.5,   // schmitt release, dB above noise floor
    double MinBurstMs = 30,      // reject spikes shorter than this
    double HangoverMs = 80,      // bridge brief fades within a burst
    double GuardMs = 20);        // pad each side so preamble/tail isn't clipped

  /// <summary>
  /// Energy burst detector. A coarse STFT gives a band-limited
  /// power envelope (sum of in-band bins), while the out-of-band bins provide a noise-floor
  /// reference that is robust to duty cycle (works for both short bursts and near-continuous
  /// signals). Schmitt-trigger hysteresis with min-length and hangover yields spans in
  /// original-rate sample indices, with guard samples.
  /// </summary>
  public static class BurstDetector
  {
    private const int FftSize = StftPsd.FftSize;

    public static List<(int start, int end, double snrDb)> Detect(
      Complex32[] iq, double fs, SignalParams p, BurstDetectorOptions? options = null)
    {
      var o = options ?? new BurstDetectorOptions();
      var result = new List<(int, int, double)>();
      if (iq.Length < FftSize) return result;

      double occHalfHz = StftPsd.OccupiedHalfHz(p, o.CfoMaxHz); // band that may contain the offset signal
      double binHz = fs / FftSize;
      int occBins = (int)Math.Ceiling(occHalfHz / binHz);
      int hop = FftSize / 2;
      int frames = (iq.Length - FftSize) / hop + 1;

      float[] window = global::VE3NEA.Dsp.BlackmanHarrisWindow(FftSize);
      var inband = new float[frames];   // in-band energy per frame
      var oobMean = new float[frames];  // mean out-of-band bin power per frame (noise)
      int inbandBins = 0;
      var q = new float[2 * occBins + 1];

      using (var fft = new Fft<Complex32>(FftSize, NativeFftw.FftwFlags.Estimate))
      {
        for (int f = 0; f < frames; f++)
        {
          (double oob, inbandBins) = StftPsd.Frame(fft, iq, f * hop, window, binHz, occHalfHz, occBins, q);
          double sumIn = 0;
          for (int j = 0; j < q.Length; j++) sumIn += q[j];
          inband[f] = (float)sumIn;
          oobMean[f] = (float)oob;
        }
      }

      // noise floor: interquartile-mean out-of-band bin power (always noise) scaled to the in-band width.
      // per-frame OOB means are near-symmetric (each already averages hundreds of bins), so the trimmed
      // mean estimates the same level the median did, at roughly half the variance.
      double noisePerBin = NoiseFloor.TrimmedMeanInPlace((float[])oobMean.Clone(), oobMean.Length);
      if (noisePerBin <= 0) noisePerBin = Percentile(inband, 0.15) / Math.Max(1, inbandBins);
      double noise = noisePerBin * inbandBins;
      double onT = noise * Math.Pow(10, o.OnThresholdDb / 10.0);
      double offT = noise * Math.Pow(10, o.OffThresholdDb / 10.0);

      // schmitt-trigger hysteresis over frames, with hangover and min length.
      double frameMs = hop / fs * 1000.0;
      int minFrames = Math.Max(1, (int)Math.Round(o.MinBurstMs / frameMs));
      int hangFrames = Math.Max(1, (int)Math.Round(o.HangoverMs / frameMs));
      int guard = (int)Math.Round(o.GuardMs / 1000.0 * fs);

      bool inBurst = false;
      int startF = 0, lastAbove = 0;
      void Close(int endF)
      {
        if (endF - startF < minFrames) return;
        double mean = MeanRange(inband, startF, endF + 1);
        double snr = 10.0 * Math.Log10(mean / noise);
        int s = Math.Clamp(startF * hop - guard, 0, iq.Length);
        int e = Math.Clamp(endF * hop + FftSize + guard, 0, iq.Length);
        if (e > s) result.Add((s, e, snr));
      }

      for (int f = 0; f < frames; f++)
      {
        double v = inband[f];
        if (!inBurst)
        {
          if (v > onT) { inBurst = true; startF = f; lastAbove = f; }
        }
        else
        {
          if (v > offT) lastAbove = f;
          if (f - lastAbove > hangFrames) { Close(lastAbove); inBurst = false; }
        }
      }
      if (inBurst) Close(lastAbove);

      return result;
    }

    private static double MeanRange(float[] x, int start, int end)
    {
      double s = 0;
      int n = Math.Max(1, end - start);
      for (int i = start; i < end && i < x.Length; i++) s += x[i];
      return s / n;
    }

    private static double Percentile(float[] x, double p)
    {
      var copy = (float[])x.Clone();
      Array.Sort(copy);
      return copy[Math.Clamp((int)(p * (copy.Length - 1)), 0, copy.Length - 1)];
    }
  }
}
