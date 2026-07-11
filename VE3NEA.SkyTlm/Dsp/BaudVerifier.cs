using System;
using System.Collections.Generic;
using MathNet.Numerics;

namespace VE3NEA.SkyTlm.Dsp
{
  /// <summary>Result of the cyclostationary baud-line search.</summary>
  /// <param name="MeasuredBaud">Refined frequency of the strongest symbol-rate line, Hz — the on-air baud.</param>
  /// <param name="CandidateBaud">The nominal candidate whose search window contains the line.</param>
  /// <param name="Score">Line strength relative to the expected pure-noise peak (≥ 1 ≈ a real line).</param>
  public sealed record BaudLineResult(double MeasuredBaud, double CandidateBaud, double Score);

  /// <summary>
  /// Cyclostationary verification of the labeled baud (Phase 4): the DB labels lie about baud (2400 vs
  /// 9600), and the CRC-gated trials can only prove a baud that actually decodes. The symbol-rate spectral
  /// line is <i>direct</i> evidence from a single burst: the squared FM-discriminator output
  /// <c>c[n] = disc[n]²</c> of a 2-FSK/GMSK/GFSK burst dips toward zero at every symbol transition, so it
  /// carries a spectral line at the symbol rate — the discriminator-domain analog of the Oerder–Meyr
  /// <c>|y|²</c> statistic that <see cref="BpskDemodulator.FeedforwardSync"/> uses, located with the same
  /// zoom-DTFT (<see cref="VE3NEA.ChirpZTransform"/>) + parabolic-peak machinery over a small candidate
  /// set. The strongest line is the on-air baud; a subharmonic of the true baud has no line at all, and a
  /// harmonic (candidate = k·true) is always weaker than the fundamental.
  /// </summary>
  internal static class BaudVerifier
  {
    /// <summary>Relative half-width of the search window around each candidate baud (label clock error is
    /// ppm-scale; this only needs to keep the true line inside one candidate's window).</summary>
    private const double ClockTolerance = 0.02;

    /// <summary>Candidates closer than this relative spacing collapse into one (2·1200 vs 2400 etc.).</summary>
    private const double DedupTolerance = 0.05;

    /// <summary>Analysis slice cap: ≥ 2.7 s at 48 kHz — thousands of symbols at any candidate baud, and it
    /// bounds the per-burst chirp-Z cost on very long bursts.</summary>
    private const int MaxAnalysisSamples = 1 << 17;

    /// <summary>Fraction of the burst trimmed at each edge before the slice: the detected span pads the
    /// signal with guard/ramp noise whose discriminator output is broadband junk that dilutes the line.</summary>
    private const double EdgeTrimFraction = 0.05;

    /// <summary>Minimum score (line peak over the expected pure-noise peak) to declare a line at all.</summary>
    private const double MinLineScore = 2.0;



    // ----------------------------------------------------------------------------------------------------
    //                                            public API
    // ----------------------------------------------------------------------------------------------------
    /// <summary>
    /// The Phase 4 candidate set {labeled baud, 2×, ½, 1200, 2400, 4800, 9600}, deduplicated and limited
    /// to lines the sample rate can carry (the line at <c>baud</c> Hz must sit below Nyquist).
    /// </summary>
    public static double[] CandidateBauds(double labeledBaud, double sampleRate)
    {
      var wanted = new[] { labeledBaud, 2 * labeledBaud, 0.5 * labeledBaud, 1200.0, 2400.0, 4800.0, 9600.0 };
      var result = new List<double>();
      foreach (double b in wanted)
      {
        if (b <= 0 || b * (1 + ClockTolerance) >= 0.5 * sampleRate) continue;
        bool dup = false;
        foreach (double r in result) if (Math.Abs(r - b) < DedupTolerance * b) { dup = true; break; }
        if (!dup) result.Add(b);
      }
      return result.ToArray();
    }

    /// <summary>
    /// Locate the strongest symbol-rate line of the burst over the candidate bauds. <paramref name="seg"/>
    /// is the raw (un-derotated) burst segment; <paramref name="cfoHz"/> is the burst's carrier estimate and
    /// <paramref name="cutoffHz"/> the discriminator pre-filter cutoff — it must pass the outer tone plus
    /// the transition bandwidth of the highest candidate (≈ dev + 0.75·max candidate), or the line of a
    /// faster-than-label signal is filtered away before the discriminator ever sees it.
    /// Returns null when no candidate shows a line (noise, CW, or too short a burst).
    /// </summary>
    public static BaudLineResult? StrongestLine(Complex32[] seg, double sampleRate, double cfoHz,
      double cutoffHz, IReadOnlyList<double> candidateBauds)
    {
      // central slice, edges trimmed (see EdgeTrimFraction), capped for bounded cost
      int trim = (int)(seg.Length * EdgeTrimFraction);
      int len = Math.Min(seg.Length - 2 * trim, MaxAnalysisSamples);
      if (len < 256 || candidateBauds.Count == 0) return null;
      int start = trim + (seg.Length - 2 * trim - len) / 2;
      var x = new Complex32[len];
      Array.Copy(seg, start, x, 0, len);
      global::VE3NEA.Dsp.Mix(x, -cfoHz / sampleRate);

      // band-limit before the nonlinear discriminator so out-of-band noise doesn't swamp the per-sample
      // phase difference; taps sized for the lowest candidate's symbol span (the longest structure)
      double fc = cutoffHz / sampleRate;
      if (fc > 0 && fc < 0.5)
      {
        double bMin = double.MaxValue;
        foreach (double b in candidateBauds) bMin = Math.Min(bMin, b);
        int taps = Math.Max(41, Math.Min((int)Math.Round(6 * sampleRate / bMin) | 1, 511));
        x = global::VE3NEA.LiquidFir.ConvolveSame(x, KernelCache.BlackmanSinc(fc, taps));
      }

      // FM discriminator (unscaled — only the line position matters), then the timing statistic
      // c[n] = disc[n]², mean-removed so the strong DC term doesn't bias the numerics
      var c = new double[len];
      double mean = 0;
      for (int i = 1; i < len; i++)
      {
        double re = (double)x[i].Real * x[i - 1].Real + (double)x[i].Imaginary * x[i - 1].Imaginary;
        double im = (double)x[i].Imaginary * x[i - 1].Real - (double)x[i].Real * x[i - 1].Imaginary;
        double d = Math.Atan2(im, re);
        c[i] = d * d;
        mean += c[i];
      }
      c[0] = len > 1 ? c[1] : 0;
      mean = (mean + c[0]) / len;
      for (int i = 0; i < len; i++) c[i] -= mean;

      BaudLineResult? best = null;
      foreach (double b in candidateBauds)
      {
        var line = LineAt(c, b, sampleRate);
        if (line != null && (best == null || line.Score > best.Score)) best = line;
      }
      return best != null && best.Score >= MinLineScore ? best : null;
    }



    // ----------------------------------------------------------------------------------------------------
    //                                             helpers
    // ----------------------------------------------------------------------------------------------------
    /// <summary>
    /// Zoom-DTFT line search around one candidate baud: chirp-Z over <c>candidate·(1 ± ClockTolerance)</c>
    /// (grid ≈ 8 samples per DTFT main-lobe width), parabolic peak refine, and a score that compares across
    /// candidates: peak-to-median of |S|², normalized by the expected peak-to-median of pure noise so a
    /// wider window (more chances for a spurious noise peak) doesn't outscore a narrower one.
    /// </summary>
    private static BaudLineResult? LineAt(double[] c, double candidateBaud, double sampleRate)
    {
      int n = c.Length;
      double f0 = candidateBaud / sampleRate;
      double fLo = f0 * (1 - ClockTolerance), fHi = f0 * (1 + ClockTolerance);
      if (fHi >= 0.5) return null;
      int grid = Math.Max(64, (int)(8 * n * (fHi - fLo)));

      var spectrum = new Complex32[grid + 1];
      using (var czt = new VE3NEA.ChirpZTransform(n, grid + 1, fLo, (fHi - fLo) / grid))
        czt.Compute(c, spectrum);

      var mags = new double[grid + 1];
      double bestMag = -1; int bestG = 0;
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
        double a = mags[bestG - 1], b = mags[bestG], d = mags[bestG + 1];
        double den = a - 2 * b + d;
        if (Math.Abs(den) > 1e-30) delta = 0.5 * (a - d) / den;
      }
      double fStar = fLo + (fHi - fLo) * (bestG + delta) / grid;

      var sorted = (double[])mags.Clone();
      Array.Sort(sorted);
      double median = sorted[sorted.Length / 2];
      if (median <= 0 || bestMag <= 0) return null;

      // |S|² of noise is ~exponential: median = ln2·mean, max of m independent draws ≈ mean·ln m, so the
      // expected noise peak-to-median is ln(m)/ln2. The ~8-point main lobe correlates neighbors → m ≈ grid/8.
      double noisePeak = Math.Log(Math.Max(2.0, grid / 8.0)) / Math.Log(2.0);
      double score = bestMag / median / noisePeak;
      return new BaudLineResult(fStar * sampleRate, candidateBaud, score);
    }
  }
}
