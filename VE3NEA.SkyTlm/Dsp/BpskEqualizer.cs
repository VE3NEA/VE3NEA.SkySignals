using System;

namespace VE3NEA.SkyTlm.Dsp
{
  /// <summary>Adaptive-equalizer selection for <see cref="BpskDemodulator"/>.</summary>
  public enum PskEqualizer
  {
    /// <summary>No equalizer (the matched-filter strobes feed the decision stage directly).</summary>
    Off,
    /// <summary>Fractionally-spaced (T/2) linear FIR equalizer, blind CMA → decision-directed LMS, adapted
    /// per-burst over multiple offline passes (<see cref="BpskEqualizer"/>).</summary>
    Fse
  }

  /// <summary>
  /// Fractionally-spaced (T/2) linear adaptive equalizer for BPSK. The real
  /// G3RUH-satellite error floor was pinned to <b>amplitude ISI</b> — symbols pulled toward the origin by linear
  /// channel memory — which a fixed matched filter cannot remove. This learns and inverts that channel:
  /// <list type="bullet">
  ///   <item><b>Blind CMA</b> (Godard p=2): BPSK is constant-modulus, so minimizing
  ///   <c>E[(|y|²−R₂)²]</c> (R₂=1) opens the eye with <i>no</i> training symbols, on the scrambled HDLC
  ///   preamble. Phase-blind (converges up to a rotation — absorbed by the downstream carrier recovery /
  ///   deframer polarity search).</item>
  ///   <item><b>Decision-directed LMS</b>: once the eye is open, <c>e = d − y</c> with <c>d = sign(Re y)</c>
  ///   gives lower steady-state MSE. A small carrier-phase tracker derotates <c>y</c> first so the real-axis
  ///   decision survives a residual CFO.</item>
  /// </list>
  /// Because the whole burst is buffered, adaptation is <b>multi-pass / offline</b>: the taps converge across
  /// the burst over several passes, then the burst is re-filtered from the start with the converged taps — so
  /// there is no convergence dead-zone at the frame head. Center-spike init = identity, so a clean signal is
  /// passed through untouched.
  /// </summary>
  public sealed class BpskEqualizer
  {
    private readonly int taps;       // T/2-spaced FIR length (odd)
    private readonly double muCma;   // CMA step size
    private readonly double muDd;    // decision-directed LMS step size
    private readonly int passes;     // adaptation passes over the buffered burst

    /// <param name="taps">T/2 FIR length (forced odd; ~2·symbols of channel memory).</param>
    /// <param name="muCma">CMA step size.</param>
    /// <param name="muDd">decision-directed LMS step size.</param>
    /// <param name="passes">offline adaptation passes (pass 0 is blind CMA, the rest are decision-directed).</param>
    public BpskEqualizer(int taps = 11, double muCma = 1e-3, double muDd = 3e-3, int passes = 4)
    {
      this.taps = Math.Max(3, taps | 1);   // odd, ≥3
      this.muCma = muCma;
      this.muDd = muDd;
      this.passes = Math.Max(1, passes);
    }

    /// <summary>
    /// Equalize one buffered burst. <paramref name="hr"/>/<paramref name="hi"/> are the <b>T/2-spaced</b> complex
    /// matched-filter samples — interleaved <c>[mid₀, strobe₀, mid₁, strobe₁, …]</c>, two per symbol (the
    /// half-symbol midpoint and the strobe instant). Returns one equalized complex sample per symbol, aligned to
    /// the strobes. Center-spike init means a clean signal comes back essentially unchanged.
    /// </summary>
    public (float[] yr, float[] yi) Equalize(float[] hr, float[] hi)
    {
      int K = hr.Length / 2;
      if (K < 8 || hr.Length != hi.Length)
        return Passthrough(hr, hi, K);

      // T/2 input as doubles; normalize by the STROBE-sample RMS so the equalizer output target modulus is 1
      // (R₂=1) and the center-spike init is already at the CMA fixed point on a clean signal — no identity drift.
      int n = 2 * K;
      var xr = new double[n]; var xi = new double[n];
      double strobePow = 0;
      for (int k = 0; k < K; k++) { double r = hr[2 * k + 1], i = hi[2 * k + 1]; strobePow += r * r + i * i; }
      double scale = strobePow > 1e-20 ? 1.0 / Math.Sqrt(strobePow / K) : 1.0;
      for (int j = 0; j < n; j++) { xr[j] = hr[j] * scale; xi[j] = hi[j] * scale; }

      int N = taps, C = N / 2;
      var wr = new double[N]; var wi = new double[N];
      wr[C] = 1.0;                                   // center spike → identity at start

      // offline adaptation: pass 0 blind CMA (opens the eye), later passes decision-directed (lowers MSE).
      for (int pass = 0; pass < passes; pass++)
      {
        bool dd = pass >= 1;
        double ph = 0, freq = 0;                     // per-pass carrier-phase tracker for the DD decision
        for (int k = 0; k < K; k++)
        {
          int center = 2 * k + 1;                    // strobe-aligned window center in the T/2 stream
          var (yr, yi) = Filter(wr, wi, xr, xi, center, C, n);
          double mag2 = yr * yr + yi * yi;

          double er, ei;
          if (!dd)
          {
            // CMA: w ← w − μ·(|y|²−R₂)·y·conj(x). e = −(|y|²−R₂)·y for the shared conj(x) update below.
            double g = mag2 - 1.0;
            er = -muCma * g * yr; ei = -muCma * g * yi;
          }
          else
          {
            // derotate y by the tracked phase, decide on the real axis, error e = (d − z) rotated back to y.
            double cph = Math.Cos(ph), sph = Math.Sin(ph);
            double zr = yr * cph + yi * sph;         // z = y·e^{−jφ}
            double zi = -yr * sph + yi * cph;
            double d = zr >= 0 ? 1.0 : -1.0;
            double ezr = d - zr, ezi = -zi;          // e_z = d − z (d is real)
            double err = ezr * cph - ezi * sph;      // e = e_z·e^{jφ}
            double eii = ezr * sph + ezi * cph;
            er = muDd * err; ei = muDd * eii;
            // advance the phase tracker from the DD phase error Im{z·conj(d)} (2nd-order, fixed small gains).
            double pe = zi * d / (Math.Sqrt(mag2) + 1e-9);
            freq += 1e-4 * pe; ph += freq + 1e-2 * pe;
          }

          // w ← w + e·conj(x) over the window (e already carries the step size and sign).
          for (int m = 0; m < N; m++)
          {
            int idx = center + m - C;
            if ((uint)idx >= (uint)n) continue;
            double cr = xr[idx], ci = xi[idx];
            wr[m] += er * cr + ei * ci;              // e·conj(x): real
            wi[m] += ei * cr - er * ci;              // e·conj(x): imag
          }
        }
      }

      // apply pass: re-filter the whole burst from the start with the converged taps.
      var outR = new float[K]; var outI = new float[K];
      for (int k = 0; k < K; k++)
      {
        var (yr, yi) = Filter(wr, wi, xr, xi, 2 * k + 1, C, n);
        outR[k] = (float)yr; outI[k] = (float)yi;
      }
      return (outR, outI);
    }

    /// <summary>y = Σ w[m]·x[center + m − C], complex, with zero-padding outside the buffer.</summary>
    private static (double yr, double yi) Filter(double[] wr, double[] wi, double[] xr, double[] xi, int center, int C, int n)
    {
      double yr = 0, yi = 0;
      int N = wr.Length;
      for (int m = 0; m < N; m++)
      {
        int idx = center + m - C;
        if ((uint)idx >= (uint)n) continue;
        double cr = xr[idx], ci = xi[idx];
        yr += wr[m] * cr - wi[m] * ci;
        yi += wr[m] * ci + wi[m] * cr;
      }
      return (yr, yi);
    }

    /// <summary>Identity fallback: return the strobe samples unchanged (burst too short to adapt).</summary>
    private static (float[] yr, float[] yi) Passthrough(float[] hr, float[] hi, int K)
    {
      var outR = new float[K]; var outI = new float[K];
      for (int k = 0; k < K; k++) { outR[k] = hr[2 * k + 1]; outI[k] = hi[2 * k + 1]; }
      return (outR, outI);
    }
  }
}
