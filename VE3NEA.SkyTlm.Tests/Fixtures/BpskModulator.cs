using System;
using System.Linq;
using MathNet.Numerics;

namespace VE3NEA.SkyTlm.Tests.Fixtures
{
  /// <summary>
  /// Minimal linear-PSK modulator for tests: known bits → IQ at a given baud and sample rate, the inverse of
  /// <c>BpskDemodulator</c>. Symbols are RRC pulse-shaped (the demod's matched filter is the same RRC), so a
  /// clean round-trip with zero BER proves the PSK chain (matched filter → Gardner timing → Costas /
  /// differential detection → Manchester combine) independent of real recordings. Integer samples/symbol only
  /// (the test bauds divide 48 kHz).
  /// </summary>
  public static class BpskModulator
  {
    /// <summary>
    /// Coherent BPSK: <paramref name="bits"/> (0/1) → ±1 symbols → RRC pulse → carrier with phase
    /// <paramref name="phaseRad"/> and offset <paramref name="cfoHz"/>. Optional AWGN at <paramref name="esN0Db"/>.
    /// </summary>
    public static Complex32[] ModulateBpsk(int[] bits, double baud, double fs,
      double beta = 0.35, double cfoHz = 0, double phaseRad = 0, double esN0Db = double.PositiveInfinity, int seed = 1)
    {
      var sym = bits.Select(b => b == 1 ? 1.0 : -1.0).ToArray();
      return Shape(sym, baud, fs, beta, cfoHz, phaseRad, esN0Db, seed);
    }

    /// <summary>
    /// DBPSK: differentially encode <paramref name="bits"/> (bit 1 = π phase flip), then BPSK-modulate. The
    /// differential detector recovers the per-symbol transition, so it is robust to <paramref name="cfoHz"/>.
    /// </summary>
    public static Complex32[] ModulateDbpsk(int[] bits, double baud, double fs,
      double beta = 0.35, double cfoHz = 0, double esN0Db = double.PositiveInfinity, int seed = 1)
    {
      double[] sym = DifferentialEncode(bits.Select(b => b == 1 ? -1.0 : 1.0).ToArray());
      return Shape(sym, baud, fs, beta, cfoHz, 0, esN0Db, seed);
    }

    /// <summary>
    /// DBPSK + Manchester: each data bit → two opposite chips (1 → +1,−1; 0 → −1,+1), the chip stream is then
    /// differentially encoded and BPSK-modulated at the <b>chip</b> rate <paramref name="baud"/>. The demod runs
    /// at the chip rate and combines pairs back to data.
    /// </summary>
    public static Complex32[] ModulateDbpskManchester(int[] dataBits, double baud, double fs,
      double beta = 0.35, double cfoHz = 0, double esN0Db = double.PositiveInfinity, int seed = 1)
    {
      var chips = new double[dataBits.Length * 2];
      for (int i = 0; i < dataBits.Length; i++)
      {
        chips[2 * i] = dataBits[i] == 1 ? 1.0 : -1.0;
        chips[2 * i + 1] = -chips[2 * i];
      }
      double[] sym = DifferentialEncode(chips);
      return Shape(sym, baud, fs, beta, cfoHz, 0, esN0Db, seed);
    }

    /// <summary>s[0] = +1 reference; s[k] = s[k−1]·d[k]. The differential detector recovers d[k] (k ≥ 1).</summary>
    private static double[] DifferentialEncode(double[] d)
    {
      var s = new double[d.Length];
      double prev = 1.0;
      for (int k = 0; k < d.Length; k++) { prev = k == 0 ? 1.0 : prev * d[k]; s[k] = prev; }
      return s;
    }

    /// <summary>RRC pulse-shape a ±1 symbol stream and place it on a (optionally offset) carrier.</summary>
    private static Complex32[] Shape(double[] sym, double baud, double fs,
      double beta, double cfoHz, double phaseRad, double esN0Db, int seed)
    {
      int sps = (int)Math.Round(fs / baud);
      int n = sym.Length * sps;

      // impulse train at the symbol instants, RRC-filtered (TX matches the RX matched filter)
      var train = new double[n];
      for (int k = 0; k < sym.Length; k++) train[k * sps] = sym[k];
      double[] h = Rrc(sps, beta, 8);
      double[] shaped = Convolve(train, h);

      var iq = new Complex32[n];
      double w = 2 * Math.PI * cfoHz / fs;
      for (int i = 0; i < n; i++)
      {
        double ph = w * i + phaseRad;
        iq[i] = new Complex32((float)(shaped[i] * Math.Cos(ph)), (float)(shaped[i] * Math.Sin(ph)));
      }

      if (!double.IsPositiveInfinity(esN0Db)) AddNoise(iq, sps, esN0Db, seed);
      return iq;
    }

    /// <summary>Root-raised-cosine kernel sampled at <paramref name="sps"/>, unit energy (mirrors the demod's).</summary>
    private static double[] Rrc(double sps, double beta, int span)
    {
      int half = (int)Math.Round(span * sps / 2.0);
      int m = 2 * half + 1;
      var h = new double[m];
      double energy = 0;
      for (int i = 0; i < m; i++)
      {
        double t = (i - half) / sps;
        double v = RrcSample(t, beta);
        h[i] = v; energy += v * v;
      }
      double norm = energy > 1e-12 ? 1.0 / Math.Sqrt(energy) : 1.0;
      for (int i = 0; i < m; i++) h[i] *= norm;
      return h;
    }

    private static double RrcSample(double t, double beta)
    {
      if (Math.Abs(t) < 1e-9) return 1.0 - beta + 4.0 * beta / Math.PI;
      double fbt = 4.0 * beta * t;
      if (beta > 1e-9 && Math.Abs(Math.Abs(fbt) - 1.0) < 1e-9)
      {
        double a = Math.PI / (4.0 * beta);
        return (beta / Math.Sqrt(2.0)) *
          ((1.0 + 2.0 / Math.PI) * Math.Sin(a) + (1.0 - 2.0 / Math.PI) * Math.Cos(a));
      }
      double num = Math.Sin(Math.PI * t * (1.0 - beta)) + fbt * Math.Cos(Math.PI * t * (1.0 + beta));
      double den = Math.PI * t * (1.0 - fbt * fbt);
      return num / den;
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
          if ((uint)j < (uint)n) acc += x[j] * h[k];
        }
        y[i] = acc;
      }
      return y;
    }

    private static void AddNoise(Complex32[] iq, double sps, double esN0Db, int seed)
    {
      // calibrated to Eb/N0. The unit-energy RRC matched filter (Σh²=1) maps a ±1 symbol to a ±1 decision and
      // passes per-component input noise variance σ² unchanged, so the decision SNR is 1/σ² = 2·Eb/N0 ⇒
      // σ = 1/√(2·Eb/N0). (BPSK BER ≈ Q(√(2·Eb/N0)) then matches theory.) sps is unused.
      _ = sps;
      double ebN0 = Math.Pow(10, esN0Db / 10.0);
      double sigma = Math.Sqrt(1.0 / (2.0 * ebN0));          // per complex component, per sample
      var rng = new Random(seed);
      for (int i = 0; i < iq.Length; i++)
        iq[i] = new Complex32(iq[i].Real + (float)(Gauss(rng) * sigma), iq[i].Imaginary + (float)(Gauss(rng) * sigma));
    }

    private static double Gauss(Random r)
    {
      double u1 = 1.0 - r.NextDouble(), u2 = 1.0 - r.NextDouble();
      return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }
  }
}
