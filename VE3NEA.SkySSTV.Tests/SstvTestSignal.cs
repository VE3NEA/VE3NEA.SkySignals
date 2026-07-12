using System;
using MathNet.Numerics;

namespace VE3NEA.SkySSTV.Tests
{
  /// <summary>
  /// Decoder-independent signal probes for the closed-loop encoder harness (P0). These are the minimum
  /// needed to prove the modulator: an outer FM discriminator recovers the subcarrier audio, and a
  /// zero-crossing estimator reads the subcarrier frequency over a window. Deliberately simple (no
  /// VE3NEA.Dsp) so a failure points at the encoder, not a decode stage that does not exist yet.
  /// </summary>
  internal static class SstvTestSignal
  {
    /// <summary>FM-discriminate complex IQ to instantaneous frequency (Hz) per sample; result[0] = 0.</summary>
    public static double[] InstantaneousFreq(Complex32[] iq, double fs)
    {
      var f = new double[iq.Length];
      for (int i = 1; i < iq.Length; i++)
      {
        // Im(conj(z[i-1])·z[i]) → phase advance; ·fs/2π → Hz.
        double re0 = iq[i - 1].Real, im0 = iq[i - 1].Imaginary;
        double re1 = iq[i].Real, im1 = iq[i].Imaginary;
        double dphase = Math.Atan2(re0 * im1 - im0 * re1, re0 * re1 + im0 * im1);
        f[i] = dphase * fs / (2 * Math.PI);
      }
      f[0] = f.Length > 1 ? f[1] : 0;
      return f;
    }

    /// <summary>Recover the subcarrier audio from IQ: FM-discriminate, subtract the DC (Doppler) offset,
    /// divide by the deviation. Returns ≈ the ±1 audio the encoder built.</summary>
    public static double[] RecoverAudio(Complex32[] iq, double fs, double deviationHz)
    {
      var inst = InstantaneousFreq(iq, fs);
      double mean = 0; for (int i = 0; i < inst.Length; i++) mean += inst[i]; mean /= Math.Max(1, inst.Length);
      var audio = new double[inst.Length];
      for (int i = 0; i < inst.Length; i++) audio[i] = (inst[i] - mean) / deviationHz;
      return audio;
    }

    /// <summary>Estimate the dominant frequency (Hz) of a real single tone over [start, start+count) from
    /// its zero crossings. Crossing positions are linearly interpolated and the frequency taken from the
    /// mean first-to-last spacing (half a period apart), which stays accurate even for short (~3 ms) tones
    /// where an integer crossing count would quantize badly.</summary>
    public static double ToneFreq(double[] signal, int start, int count, double fs)
    {
      int end = Math.Min(signal.Length - 1, start + count);
      double first = double.NaN, last = 0; int n = 0;
      for (int i = start + 1; i < end; i++)
      {
        double a = signal[i - 1], b = signal[i];
        if ((a <= 0 && b > 0) || (a >= 0 && b < 0))
        {
          double frac = a == b ? 0 : a / (a - b);       // interpolated sub-sample crossing
          double pos = (i - 1) + frac;
          if (double.IsNaN(first)) first = pos;
          last = pos; n++;
        }
      }
      if (n < 2) return 0;
      double meanSpacing = (last - first) / (n - 1);     // consecutive crossings are half a period apart
      return 0.5 * fs / meanSpacing;
    }

    /// <summary>Convert milliseconds to a sample count at <paramref name="fs"/>.</summary>
    public static int MsToSamples(double ms, double fs) => (int)Math.Round(ms / 1000.0 * fs);

    /// <summary>Decode the 7-bit VIS code from recovered subcarrier audio by reading the ten 30 ms bit
    /// windows after the leader/break/leader. Returns the code and whether even parity held; -1 code on
    /// a malformed start/stop bit. Mirrors the encoder's LSB-first data order.</summary>
    public static (int code7, bool parityOk) DecodeVis(double[] audio, double fs)
    {
      double firstBitMs = SstvTones.VisLeaderMs + SstvTones.VisBreakMs + SstvTones.VisLeaderMs;
      double BitFreq(int k)
      {
        double startMs = firstBitMs + k * SstvTones.VisBitMs;
        int start = MsToSamples(startMs + SstvTones.VisBitMs * 0.2, fs);   // skip window edges
        int count = MsToSamples(SstvTones.VisBitMs * 0.6, fs);
        return ToneFreq(audio, start, count, fs);
      }

      // start (k=0) and stop (k=9) must be ~1200 Hz.
      if (Math.Abs(BitFreq(0) - SstvTones.VisStartStop) > 100) return (-1, false);
      if (Math.Abs(BitFreq(9) - SstvTones.VisStartStop) > 100) return (-1, false);

      int code = 0, ones = 0;
      for (int b = 0; b < 7; b++)
      {
        double f = BitFreq(1 + b);
        int bit = Math.Abs(f - SstvTones.VisBitOne) < Math.Abs(f - SstvTones.VisBitZero) ? 1 : 0;
        code |= bit << b;
        ones += bit;
      }
      double pf = BitFreq(8);
      int parityBit = Math.Abs(pf - SstvTones.VisBitOne) < Math.Abs(pf - SstvTones.VisBitZero) ? 1 : 0;
      bool parityOk = ((ones + parityBit) & 1) == 0;
      return (code, parityOk);
    }
  }
}
