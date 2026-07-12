using System;
using MathNet.Numerics;

namespace VE3NEA.SkyTlm.Dsp
{
  /// <summary>
  /// Shared whole-burst feed-forward symbol-clock estimator — the core of <see cref="PskTiming.Feedforward"/>,
  /// used by both <see cref="BpskDemodulator.FeedforwardSync"/> and the CPM/discriminator port
  /// <see cref="CpmFskDemodulator.FeedforwardSync"/>. The caller supplies the real sequence <c>c[n]</c> that
  /// carries the symbol-rate spectral line (PSK: mean-removed <c>|y|²</c>; CPM: mean-removed, optionally
  /// envelope-weighted <c>mf²</c>); this estimates the line's <b>frequency</b> (the true symbol period — a
  /// feedback loop only tracks the line's phase and leaves a residual clock-<i>rate</i> error that walks the
  /// strobes ~½ symbol across a burst) and its <b>phase</b> (the Oerder–Meyr sampling offset).
  /// </summary>
  internal static class FeedforwardTiming
  {
    /// <summary>
    /// Estimate the symbol period (in samples) and the first-strobe offset τ ∈ [0, period) from the
    /// baud-line sequence <paramref name="c"/>: coarse chirp-Z grid search over
    /// ±<paramref name="maxClockError"/> around the nominal <paramref name="sps"/>, parabolic refine,
    /// then the O&amp;M phase at the refined frequency.
    /// </summary>
    internal static (double period, double tau) EstimateClock(double[] c, double sps, double maxClockError)
    {
      int n = c.Length;

      // search the baud line near f0 = 1/sps over ±maxClockError. Grid fine enough to land on the DTFT main
      // lobe (~1/n wide); a parabolic interpolation on |S|² then refines the period to sub-grid precision.
      double f0 = 1.0 / sps;
      double fLo = f0 * (1.0 - maxClockError), fHi = f0 * (1.0 + maxClockError);
      int grid = Math.Max(64, (int)(8 * n * (fHi - fLo)));   // ≈8 samples per main-lobe width

      // the grid search is O(grid·N) = O(N²) in burst length (grid grows with N), the demod hot spot. But the
      // baud line at f0 and the whole ±maxClockError band sit far below Nyquist, so anti-alias-decimate c by D
      // (block average, a length-D boxcar LPF) before the search: the observation duration — and hence the
      // frequency resolution (~1/N in absolute f) — is unchanged, only the per-DTFT length drops to N/D. The
      // boxcar's slowly-varying envelope can't move the peak across one ~1/N-wide lobe, so the rate estimate is
      // preserved; D≈sps/4 lands the line near a quarter of the decimated band. The O&M phase below stays on the
      // full-rate c (the symbol-timing offset must not pick up the decimation's group delay).
      int dec = Math.Max(1, (int)Math.Round(0.25 * sps));
      double[] cs = dec > 1 ? BoxDecimate(c, dec) : c;

      // evaluate the baud-line DTFT at all grid+1 frequencies in ONE chirp-Z (Bluestein) transform instead of
      // grid separate O(N) sums: the zoom-FFT (VE3NEA.ChirpZTransform) computes the same uniformly-spaced DTFT
      // samples via a single power-of-two FFT pair, collapsing the O(grid·N) ≈ O(N²) search — the demod hot
      // spot — to ~O(N log N). The mags match the per-point LineDtft loop, so the refined fStar (and the
      // decode) is unchanged. The decimated index m maps to full-rate sample m·dec, so the line at f sits at
      // frequency f·dec in cs.
      double phi0 = fLo * dec, dphi = (fHi - fLo) * dec / grid;
      var spectrum = new Complex32[grid + 1];
      using (var czt = new VE3NEA.ChirpZTransform(cs.Length, grid + 1, phi0, dphi))
        czt.Compute(cs, spectrum);

      double bestMag = -1; int bestG = 0;
      var mags = new double[grid + 1];
      for (int g = 0; g <= grid; g++)
      {
        double mag = (double)spectrum[g].Real * spectrum[g].Real + (double)spectrum[g].Imaginary * spectrum[g].Imaginary;
        mags[g] = mag;
        if (mag > bestMag) { bestMag = mag; bestG = g; }
      }
      // parabolic peak interpolation in grid units
      double delta = 0;
      if (bestG > 0 && bestG < grid)
      {
        double a = mags[bestG - 1], b = mags[bestG], cc = mags[bestG + 1];
        double den = a - 2 * b + cc;
        if (Math.Abs(den) > 1e-30) delta = 0.5 * (a - cc) / den;
      }
      double fStar = fLo + (fHi - fLo) * (bestG + delta) / grid;
      double period = 1.0 / fStar;

      // O&M phase at the refined frequency → offset of the first symbol instant, in [0, period).
      var (fr, fi) = LineDtft(c, fStar);
      double tau = -Math.Atan2(fi, fr) / (2.0 * Math.PI) * period;
      tau %= period; if (tau < 0) tau += period;

      return (period, tau);
    }

    /// <summary>Anti-alias decimate by <paramref name="d"/> via non-overlapping block averaging — a length-d
    /// boxcar LPF sampled every d samples. Used to shrink the O(grid·N) baud-line search; the boxcar passband
    /// keeps the near-DC band (line at f0 ≪ 0.5/d) while suppressing out-of-band noise that would otherwise alias.</summary>
    private static double[] BoxDecimate(double[] c, int d)
    {
      int m = c.Length / d;
      var outp = new double[m];
      double inv = 1.0 / d;
      for (int k = 0; k < m; k++)
      {
        double acc = 0; int b = k * d;
        for (int j = 0; j < d; j++) acc += c[b + j];
        outp[k] = acc * inv;
      }
      return outp;
    }

    /// <summary>Σ c[n]·e^{-j2πf·n} via a re-seeded rotating phasor (no per-sample sin/cos; re-seeded every 1024
    /// samples to bound rounding drift over a long burst).</summary>
    private static (double re, double im) LineDtft(double[] c, double f)
    {
      double w = -2.0 * Math.PI * f;
      double sr = 0, si = 0;
      double cr = 1, ci = 0;                 // current phasor e^{-j2πf·n}
      double dr = Math.Cos(w), di = Math.Sin(w);
      for (int nn = 0; nn < c.Length; nn++)
      {
        sr += c[nn] * cr; si += c[nn] * ci;
        if ((nn & 1023) == 1023) { double ph = w * (nn + 1); cr = Math.Cos(ph); ci = Math.Sin(ph); }
        else { double t = cr * dr - ci * di; ci = cr * di + ci * dr; cr = t; }
      }
      return (sr, si);
    }
  }
}
