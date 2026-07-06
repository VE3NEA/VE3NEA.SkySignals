using System;
using MathNet.Numerics;

namespace VE3NEA.SkyTlm.Tests.Fixtures
{
  /// <summary>
  /// Minimal AFSK-over-FM modulator for tests: on-air channel bits → Bell-202 audio (mark 1200 Hz for a 1,
  /// space 2200 Hz for a 0, continuous phase) → FM-modulate that audio onto the RF carrier. This is the inverse
  /// of <c>AfskDemodulator</c> (RF discriminate → mix by af_carrier → FSK engine), so a clean round-trip proves
  /// the AFSK demod + plain (unscrambled) AX.25 deframer chain end to end, independent of the messy real
  /// recordings. Deliberately the same shape as <c>GmskModulator</c>.
  /// </summary>
  public static class AfskModulator
  {
    /// <summary>
    /// Modulate on-air <paramref name="channelBits"/> (0/1, already HDLC+NRZI encoded) to complex baseband at
    /// <paramref name="baud"/> over <paramref name="fs"/>: a continuous-phase mark/space tone pair carried on an
    /// FM subcarrier of <paramref name="fmDeviationHz"/>. Optionally add a carrier offset and white Gaussian
    /// noise at the given Es/N0 (dB).
    /// </summary>
    public static Complex32[] Modulate(int[] channelBits, double baud, double fs,
      double markHz = 1200, double spaceHz = 2200, double fmDeviationHz = 3000,
      double cfoHz = 0, double esN0Db = double.PositiveInfinity, int seed = 1)
    {
      double sps = fs / baud;
      int n = (int)Math.Round(channelBits.Length * sps);

      // Bell-202 audio: a continuous-phase tone, mark (1200 Hz) for a 1 and space (2200 Hz) for a 0.
      var audio = new double[n];
      double audioPhase = 0;
      for (int i = 0; i < n; i++)
      {
        int k = Math.Min((int)(i / sps), channelBits.Length - 1);
        double f = channelBits[k] == 1 ? markHz : spaceHz;
        audioPhase += 2 * Math.PI * f / fs;
        audio[i] = Math.Sin(audioPhase);
      }

      // FM-modulate the audio onto the carrier (peak deviation fmDeviationHz), plus an optional constant CFO.
      var iq = new Complex32[n];
      double phase = 0;
      double kf = 2 * Math.PI * fmDeviationHz / fs;
      double cfoStep = 2 * Math.PI * cfoHz / fs;
      for (int i = 0; i < n; i++)
      {
        phase += kf * audio[i] + cfoStep;
        iq[i] = new Complex32((float)Math.Cos(phase), (float)Math.Sin(phase));
      }

      if (!double.IsPositiveInfinity(esN0Db)) AddNoise(iq, sps, esN0Db, seed);
      return iq;
    }

    private static void AddNoise(Complex32[] iq, double sps, double esN0Db, int seed)
    {
      double es = sps;
      double n0 = es / Math.Pow(10, esN0Db / 10.0);
      double sigma = Math.Sqrt(n0 / 2.0);
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
  }
}
