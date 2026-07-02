using System;
using MathNet.Numerics;

namespace VE3NEA.SkyTlm.Tests.Fixtures
{
  /// <summary>
  /// Synthetic IQ / waveform builders for the per-stage unit tests — small, exactly-known signals
  /// (tones, two-level FM, impulses, DC) that isolate one demod stage without the full modulator.
  /// </summary>
  public static class Signals
  {
    /// <summary>Constant baseband level: <paramref name="n"/> identical samples (default unit on the real axis).</summary>
    public static Complex32[] Dc(int n, float re = 1f, float im = 0f)
    {
      var x = new Complex32[n];
      for (int i = 0; i < n; i++) x[i] = new Complex32(re, im);
      return x;
    }

    /// <summary>Complex tone exp(j(2π·f/fs·n + φ₀)) — a constant-frequency signal of <paramref name="n"/> samples.</summary>
    public static Complex32[] Tone(int n, double freqHz, double fs, double phase0 = 0)
    {
      var x = new Complex32[n];
      double w = 2.0 * Math.PI * freqHz / fs;
      for (int i = 0; i < n; i++)
      {
        double ph = phase0 + w * i;
        x[i] = new Complex32((float)Math.Cos(ph), (float)Math.Sin(ph));
      }
      return x;
    }

    /// <summary>
    /// Continuous-phase two-level FM: the first half deviates at +<paramref name="devHz"/>, the second at
    /// −devHz. Through the discriminator (after its DC-block) this reads +1 then −1, so it pins the
    /// frequency→amplitude scale and the sign convention.
    /// </summary>
    public static Complex32[] FmTwoLevel(int nPerLevel, double devHz, double fs)
    {
      int n = 2 * nPerLevel;
      var x = new Complex32[n];
      double wPos = 2.0 * Math.PI * devHz / fs;
      double ph = 0;
      for (int i = 0; i < n; i++)
      {
        ph += i < nPerLevel ? wPos : -wPos;   // integrate instantaneous frequency
        x[i] = new Complex32((float)Math.Cos(ph), (float)Math.Sin(ph));
      }
      return x;
    }

    /// <summary>Complex white Gaussian noise: <paramref name="n"/> samples, <paramref name="sigma"/> per component. Flat spectrum.</summary>
    public static Complex32[] Awgn(int n, double sigma, int seed = 1)
    {
      var rng = new Random(seed);
      var x = new Complex32[n];
      for (int i = 0; i < n; i++)
        x[i] = new Complex32((float)(Gauss(rng) * sigma), (float)(Gauss(rng) * sigma));
      return x;
    }

    private static double Gauss(Random r)
    {
      double u1 = 1.0 - r.NextDouble(), u2 = 1.0 - r.NextDouble();
      return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }

    /// <summary>Unit impulse of length <paramref name="n"/> with the 1 at <paramref name="at"/> (default center).</summary>
    public static float[] Impulse(int n, int at = -1)
    {
      var x = new float[n];
      x[at < 0 ? n / 2 : at] = 1f;
      return x;
    }

    /// <summary>Add a constant DC offset to every sample (tests the discriminator's DC-block).</summary>
    public static Complex32[] AddDc(Complex32[] x, float re, float im = 0f)
    {
      var y = new Complex32[x.Length];
      for (int i = 0; i < x.Length; i++) y[i] = new Complex32(x[i].Real + re, x[i].Imaginary + im);
      return y;
    }

    /// <summary>Add an out-of-band interfering tone (tests the channel filter / ChannelBwBaud).</summary>
    public static Complex32[] AddTone(Complex32[] x, double freqHz, double fs, double amp)
    {
      var t = Tone(x.Length, freqHz, fs);
      var y = new Complex32[x.Length];
      for (int i = 0; i < x.Length; i++)
        y[i] = new Complex32(x[i].Real + (float)amp * t[i].Real, x[i].Imaginary + (float)amp * t[i].Imaginary);
      return y;
    }

    /// <summary>
    /// Embed a modulated burst into a longer zero-padded recording at <paramref name="start"/>, returning the
    /// full buffer. The matching <c>Burst</c> (with the same CFO already baked into the burst by the
    /// modulator) exercises the public <c>Demodulate(iq, burst, p)</c> + <c>Acquisition.Derotate</c> path.
    /// </summary>
    public static Complex32[] Embed(Complex32[] burst, int totalLen, int start)
    {
      var buf = new Complex32[totalLen];
      Array.Copy(burst, 0, buf, start, Math.Min(burst.Length, totalLen - start));
      return buf;
    }
  }
}
