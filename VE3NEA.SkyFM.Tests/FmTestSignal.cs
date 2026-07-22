using System;
using MathNet.Numerics;

namespace VE3NEA.SkyFM.Tests
{
  /// <summary>Synthetic FM signal builder for closed-loop tests: instantaneous-frequency track → complex
  /// baseband IQ (phase integration with wrapped phase — no unbounded <c>cos(w·i)</c> index), plus
  /// deterministic AWGN and a tone-amplitude probe.</summary>
  internal static class FmTestSignal
  {
    /// <summary>Instantaneous-frequency track (Hz) of a tone: <c>dev·sin(2π·freq·t)</c>.</summary>
    public static float[] Tone(double freqHz, double devHz, double seconds, double fs)
    {
      int n = (int)Math.Round(seconds * fs);
      var x = new float[n];
      double w = 2 * Math.PI * freqHz / fs, phase = 0;
      for (int i = 0; i < n; i++)
      {
        x[i] = (float)(devHz * Math.Sin(phase));
        phase += w;
        if (phase > Math.PI) phase -= 2 * Math.PI;
      }
      return x;
    }

    /// <summary>FM-modulate an instantaneous-frequency track (Hz) into unit-amplitude IQ.</summary>
    public static Complex32[] Modulate(ReadOnlySpan<float> freqHz, double fs, float amplitude = 1f)
    {
      var iq = new Complex32[freqHz.Length];
      double phase = 0;
      for (int i = 0; i < freqHz.Length; i++)
      {
        iq[i] = new Complex32(amplitude * (float)Math.Cos(phase), amplitude * (float)Math.Sin(phase));
        phase += 2 * Math.PI * freqHz[i] / fs;
        if (phase > Math.PI) phase -= 2 * Math.PI; else if (phase < -Math.PI) phase += 2 * Math.PI;
      }
      return iq;
    }

    /// <summary>Add complex AWGN with per-component sigma <paramref name="sigma"/> (Box–Muller over a
    /// seeded <see cref="Random"/> — deterministic).</summary>
    public static void AddNoise(Complex32[] iq, float sigma, int seed)
    {
      var rnd = new Random(seed);
      for (int i = 0; i < iq.Length; i++)
        iq[i] += new Complex32(sigma * Gauss(rnd), sigma * Gauss(rnd));
    }

    private static float Gauss(Random rnd)
    {
      double u1 = 1.0 - rnd.NextDouble(), u2 = rnd.NextDouble();
      return (float)(Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2));
    }

    /// <summary>Amplitude of the <paramref name="freqHz"/> component of <paramref name="x"/> — a
    /// Hann-windowed single-bin DFT probe.</summary>
    public static double ToneAmplitude(ReadOnlySpan<float> x, double freqHz, double fs)
    {
      int n = x.Length;
      double c = 0, s = 0, wsum = 0;
      double w0 = 2 * Math.PI * freqHz / fs;
      for (int i = 0; i < n; i++)
      {
        double w = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (n - 1));
        c += x[i] * w * Math.Cos(w0 * i);
        s += x[i] * w * Math.Sin(w0 * i);
        wsum += w;
      }
      return 2.0 * Math.Sqrt(c * c + s * s) / wsum;
    }

    /// <summary>RMS of a span.</summary>
    public static double Rms(ReadOnlySpan<float> x)
    {
      double sum = 0;
      for (int i = 0; i < x.Length; i++) sum += (double)x[i] * x[i];
      return Math.Sqrt(sum / Math.Max(1, x.Length));
    }
  }
}
