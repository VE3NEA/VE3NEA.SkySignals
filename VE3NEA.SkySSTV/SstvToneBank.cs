using System;
using System.Numerics;

namespace VE3NEA.SkySSTV
{
  /// <summary>
  /// Windowed single-tone power over the FM-discriminated audio (plan §4). The discriminator output
  /// <c>disc = f_doppler + dev·a(t)</c> is a real sinusoid at the SSTV subcarrier frequency, so the VIS
  /// header (§4) and the horizontal-sync correlator (§7) are both read as tone energy at fixed
  /// frequencies (1100 / 1200 / 1300 / 1900 Hz). This helper precomputes prefix sums of the
  /// complex-mixed signal and of the DC-removed energy, so the coherent tone power over <b>any</b>
  /// window is O(1) — the VIS matched filter needs many windows of differing lengths (leader vs bit),
  /// and the sync correlator needs a sliding window over the whole region.
  ///
  /// <para><b>Coherence</b> = <c>|Σ m|² / (W · E)</c>, where <c>m[n] = (disc[n]−mean)·e^{−j2πf n/fs}</c>,
  /// <c>W</c> the window length and <c>E</c> the DC-removed energy. A pure matched tone gives 0.5 (a real
  /// tone splits between ±f); a wrong-frequency or broadband window gives ≈0. It is amplitude-invariant
  /// and bounded, which is exactly what the §4 normalization and the sync threshold need.</para>
  /// </summary>
  internal sealed class SstvToneBank
  {
    private readonly double[] preRe;   // prefix sum of Re(m); preRe[k] = Σ_{n<k}
    private readonly double[] preIm;    // prefix sum of Im(m)
    private readonly double[] preE;     // prefix sum of (disc−mean)²
    private readonly int offset;        // absolute sample index that array position 0 maps to
    private readonly int len;

    /// <summary>The target tone frequency (Hz) this bank was built for.</summary>
    public double FreqHz { get; }

    /// <summary>Build prefix sums for tone <paramref name="freqHz"/> over the absolute range
    /// [<paramref name="start"/>, start+<paramref name="length"/>) of <paramref name="disc"/>. The DC
    /// (Doppler) offset is estimated as the region mean and removed before the energy sum.</summary>
    public SstvToneBank(double[] disc, double fs, double freqHz, int start, int length)
    {
      FreqHz = freqHz;
      offset = Math.Max(0, start);
      int end = Math.Min(disc.Length, start + length);
      len = Math.Max(0, end - offset);

      double mean = 0;
      for (int i = 0; i < len; i++) mean += disc[offset + i];
      if (len > 0) mean /= len;

      preRe = new double[len + 1];
      preIm = new double[len + 1];
      preE = new double[len + 1];
      double w = 2 * Math.PI * freqHz / fs;
      for (int n = 0; n < len; n++)
      {
        double v = disc[offset + n] - mean;
        double c = Math.Cos(w * n), s = Math.Sin(w * n);
        preRe[n + 1] = preRe[n] + v * c;       // Re(v·e^{−jwn}) =  v·cos(wn)
        preIm[n + 1] = preIm[n] - v * s;        // Im(v·e^{−jwn}) = −v·sin(wn)
        preE[n + 1] = preE[n] + v * v;
      }
    }

    /// <summary>Coherent tone power over the absolute window [<paramref name="a"/>, <paramref name="b"/>),
    /// normalized to [0, 0.5]. Returns 0 for an empty or out-of-range window.</summary>
    public double Coherence(int a, int b)
    {
      int ia = a - offset, ib = b - offset;
      if (ia < 0) ia = 0;
      if (ib > len) ib = len;
      int w = ib - ia;
      if (w <= 0) return 0;
      double e = preE[ib] - preE[ia];
      if (e <= 0) return 0;
      double re = preRe[ib] - preRe[ia], im = preIm[ib] - preIm[ia];
      return (re * re + im * im) / (w * e);
    }

    /// <summary>Complex tone correlation over [<paramref name="a"/>, <paramref name="b"/>): <c>Σ m</c>,
    /// referenced to this bank's start. Its argument is the tone's sub-sample phase (KF1, P3).</summary>
    public Complex Corr(int a, int b)
    {
      int ia = a - offset, ib = b - offset;
      if (ia < 0) ia = 0;
      if (ib > len) ib = len;
      if (ib <= ia) return Complex.Zero;
      return new Complex(preRe[ib] - preRe[ia], preIm[ib] - preIm[ia]);
    }
  }
}
