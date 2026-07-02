using System;
using MathNet.Numerics;

namespace VE3NEA.SkyTlm.Tests
{
  /// <summary>
  /// Minimal GMSK modulator for tests: known bits → IQ at a given baud and sample rate. This is the
  /// inverse of <c>GmskDemodulator</c>, so a clean round-trip with zero BER proves the demod chain
  /// (discriminator → matched filter → Gardner) is correct independent of the messy real recordings.
  /// GMSK: NRZ symbols → Gaussian frequency pulse (BT) → phase integration (h=0.5) → exp(jφ).
  /// </summary>
  public static class GmskModulator
  {
    /// <summary>
    /// Modulate <paramref name="bits"/> (0/1) to complex baseband at <paramref name="baud"/> over
    /// <paramref name="fs"/>. The modulation index <paramref name="h"/> sets the peak deviation
    /// (dev = h·Rs/2); the default h=0.5 is GMSK (dev = Rs/4). Larger h widens the tone separation —
    /// the GFSK case must decode against its real h. Optionally add a CFO and white Gaussian
    /// noise at the given Es/N0 (dB). Returns unit-ish amplitude IQ.
    /// </summary>
    public static Complex32[] Modulate(int[] bits, double baud, double fs,
      double bt = 0.5, double cfoHz = 0, double esN0Db = double.PositiveInfinity, int seed = 1, double h = 0.5)
    {
      double sps = fs / baud;

      // fractional sps (e.g. 19200 Bd at 48 kHz → 2.5): the ZOH NRZ below can only switch on sample
      // boundaries, which injects up to ±half-sample symbol-timing jitter into the phase trajectory —
      // harmless to the discriminator/Gardner chain but fatal to the coherent MLSE model (clean BER ~0.16).
      // synthesize at the smallest integer multiple U where sps·U is integral, then anti-alias + decimate
      // back: integer-sps calls take U = 1 and stay byte-identical to the original path.
      int U = 1;
      while (U <= 16 && Math.Abs(sps * U - Math.Round(sps * U)) > 1e-9) U++;
      if (U > 16) U = 1;   // pathological fs/baud ratio: synthesize directly at fs
      if (U > 1)
      {
        var hi = Modulate(bits, baud, fs * U, bt, cfoHz, double.PositiveInfinity, seed, h);
        var lo = Decimate(hi, U);
        if (!double.IsPositiveInfinity(esN0Db)) AddNoise(lo, sps, esN0Db, seed);
        return lo;
      }

      int nSym = bits.Length;
      int n = (int)Math.Round(nSym * sps);

      // NRZ symbol waveform upsampled to fs (zero-order hold).
      var nrz = new double[n];
      for (int i = 0; i < n; i++)
      {
        int k = (int)(i / sps);
        if (k >= nSym) k = nSym - 1;
        nrz[i] = bits[k] == 1 ? 1.0 : -1.0;
      }

      // gaussian frequency pulse (same BT formulation as the receiver's matched filter).
      double[] g = GaussianPulse(sps, bt, 4);
      double[] fsm = Convolve(nrz, g);   // smoothed instantaneous frequency, still ≈±1

      // phase integration: per-sample increment for a sustained ±1 symbol is π·h·a/sps (h=0.5 ⇒ π·a/(2·sps)).
      var iq = new Complex32[n];
      double phase = 0;
      double cfoStep = 2 * Math.PI * cfoHz / fs;
      double phaseStep = Math.PI * h / sps;
      for (int i = 0; i < n; i++)
      {
        phase += phaseStep * fsm[i] + cfoStep;
        iq[i] = new Complex32((float)Math.Cos(phase), (float)Math.Sin(phase));
      }

      if (!double.IsPositiveInfinity(esN0Db)) AddNoise(iq, sps, esN0Db, seed);
      return iq;
    }

    /// <summary>Random 0/1 bits (PRBS-ish via a simple LCG) — deterministic per seed.</summary>
    public static int[] RandomBits(int count, int seed = 1)
    {
      var bits = new int[count];
      uint s = (uint)seed * 2654435761u + 1u;
      for (int i = 0; i < count; i++) { s = s * 1664525u + 1013904223u; bits[i] = (int)((s >> 16) & 1); }
      return bits;
    }

    /// <summary>U:1 decimation with a windowed-sinc anti-alias low-pass (cutoff 0.45 of the output Nyquist).</summary>
    private static Complex32[] Decimate(Complex32[] x, int U)
    {
      double fc = 0.45 / U;                              // cycles/input-sample
      int taps = (32 * U) | 1, half = taps / 2;
      var h = new double[taps];
      double sum = 0;
      for (int i = 0; i < taps; i++)
      {
        double t = i - half;
        double sinc = t == 0 ? 2 * Math.PI * fc : Math.Sin(2 * Math.PI * fc * t) / t;
        double w = 0.42 - 0.5 * Math.Cos(2 * Math.PI * i / (taps - 1)) + 0.08 * Math.Cos(4 * Math.PI * i / (taps - 1));
        h[i] = sinc * w; sum += h[i];
      }
      for (int i = 0; i < taps; i++) h[i] /= sum;

      var y = new Complex32[x.Length / U];
      for (int o = 0; o < y.Length; o++)
      {
        double re = 0, im = 0;
        int c = U * o;
        for (int i = 0; i < taps; i++)
        {
          int j = c + i - half;
          if (j < 0) j = 0; else if (j >= x.Length) j = x.Length - 1;   // edge-clamp, like Convolve
          re += h[i] * x[j].Real; im += h[i] * x[j].Imaginary;
        }
        y[o] = new Complex32((float)re, (float)im);
      }
      return y;
    }

    /// <summary>Normalized GMSK frequency pulse g(t) sampled at <paramref name="sps"/>, unit area per symbol.</summary>
    private static double[] GaussianPulse(double sps, double bt, int spanSymbols)
    {
      int half = (int)Math.Round(spanSymbols * sps / 2.0);
      int m = 2 * half + 1;
      var h = new double[m];
      double k = 2.0 * Math.PI * bt / Math.Sqrt(Math.Log(2.0));
      double sum = 0;
      for (int i = 0; i < m; i++)
      {
        double t = (i - half) / sps;                 // symbols
        double v = 0.5 * (Q(k * (t - 0.5)) - Q(k * (t + 0.5)));
        h[i] = v; sum += v;
      }
      // normalize so a sustained run yields |fsm|→1 (DC gain 1)
      if (sum > 1e-12) for (int i = 0; i < m; i++) h[i] /= sum;
      return h;
    }

    private static double[] Convolve(double[] x, double[] h)
    {
      int n = x.Length, m = h.Length, half = m / 2;
      var y = new double[n];
      for (int i = 0; i < n; i++)
      {
        double acc = 0;
        for (int k = 0; k < m; k++)
        {
          int j = i + k - half;
          if (j < 0) j = 0; else if (j >= n) j = n - 1;   // edge-clamp (avoids spurious transitions)
          acc += x[j] * h[k];
        }
        y[i] = acc;
      }
      return y;
    }

    private static void AddNoise(Complex32[] iq, double sps, double esN0Db, int seed)
    {
      // es/N0: symbol energy ≈ sps (unit-amplitude samples). N0 = Es / 10^(EsN0/10).
      double es = sps;
      double n0 = es / Math.Pow(10, esN0Db / 10.0);
      double sigma = Math.Sqrt(n0 / 2.0);           // per complex component
      var rng = new Random(seed);
      for (int i = 0; i < iq.Length; i++)
      {
        double gr = Gauss(rng) * sigma, gi = Gauss(rng) * sigma;
        iq[i] = new Complex32(iq[i].Real + (float)gr, iq[i].Imaginary + (float)gi);
      }
    }

    private static double Gauss(Random r)
    {
      double u1 = 1.0 - r.NextDouble(), u2 = 1.0 - r.NextDouble();
      return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }

    private static double Q(double x) => 0.5 * Erfc(x / Math.Sqrt(2.0));

    private static double Erfc(double x)
    {
      double z = Math.Abs(x), t = 1.0 / (1.0 + 0.5 * z);
      double ans = t * Math.Exp(-z * z - 1.26551223 + t * (1.00002368 + t * (0.37409196 +
        t * (0.09678418 + t * (-0.18628806 + t * (0.27886807 + t * (-1.13520398 +
        t * (1.48851587 + t * (-0.82215223 + t * 0.17087277)))))))));
      return x >= 0 ? ans : 2.0 - ans;
    }
  }
}
