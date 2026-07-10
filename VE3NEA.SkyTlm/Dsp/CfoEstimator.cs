using MathNet.Numerics;
using VE3NEA.SkyTlm.Core;
using VE3NEA;

namespace VE3NEA.SkyTlm.Dsp
{
  /// <summary>Spectral measurements for a burst.</summary>
  /// <param name="CfoHz">Residual carrier offset from DC.</param>
  /// <param name="ShapeScore">Cosine match of the PSD to the expected modulation shape (0..1); gates GMSK/GFSK.</param>
  /// <param name="BandwidthHz">RMS bandwidth of the burst PSD; gates FSK/AFSK (narrow CW vs wide burst).</param>
  public sealed record BurstSpectralInfo(double CfoHz, double ShapeScore, double BandwidthHz);

  /// <summary>
  /// Per-burst spectral analysis.
  /// CFO comes from the <b>autocorrelation / mirror-symmetry of the PSD</b>: the carrier is
  /// the axis about which the spectrum is most symmetric (true for GMSK bell, FSK two-tone,
  /// BPSK), robust to FSK data imbalance and stray in-band tones that bias a plain centroid.
  /// <see cref="ShapeScore"/> (cosine vs the expected modulation template) separates digital
  /// bursts from SSTV/CW; <see cref="BandwidthHz"/> separates wide FSK bursts from narrow CW.
  /// </summary>
  public sealed class CfoEstimator : IDisposable
  {
    private readonly Fft<Complex32> fft;
    private readonly float[] window;
    private readonly int size;
    private readonly double binHz;
    private readonly double occHalfHz;
    private readonly int occHalfBins;   // index of 0 Hz in the q[] array
    private readonly int cfoMaxBins;
    private readonly float[] template;  // expected PSD shape, centered at occHalfBins
    private readonly double templateMax;
    private readonly double baud;
    private readonly bool useTemplateCfo;   // cross-correlate the PSD with the (two-lobe) template for CFO

    public CfoEstimator(double fs, double cfoMaxHz, SignalParams p, int fftSize = 8192)
    {
      size = fftSize;
      baud = p.Baud;
      binHz = fs / size;
      fft = new Fft<Complex32>(size, NativeFftw.FftwFlags.Estimate);
      window = global::VE3NEA.Dsp.BlackmanHarrisWindow(size);

      // same formula StreamingPipeline/BurstDetector use to size q[]/avgQ[] (StftPsd.OccupiedHalfHz's blind-FSK
      // branch widens the band when deviation is unknown) — template[] below must match that length exactly, or
      // TemplateCfo's q.Length-bounded loop indexes template[] out of range for a blind (deviation-unknown) burst.
      occHalfHz = StftPsd.OccupiedHalfHz(p, cfoMaxHz);
      occHalfBins = (int)Math.Ceiling(occHalfHz / binHz);
      cfoMaxBins = (int)Math.Ceiling(cfoMaxHz / binHz);

      // matched template over the q[] grid (index j -> freq (j-C)*binHz), centered at C: the analytic
      // modulation shape. CFO via template cross-correlation for two-tone FSK: a two-lobe template anchors
      // the carrier at the MIDPOINT of the tones, whereas PSD self-convolution (SymmetryCfo) is
      // amplitude-weighted and slides toward the stronger tone. Bell modulations keep symmetry CFO.
      useTemplateCfo = p.Modulation == Modulation.FSK;
      int L = 2 * occHalfBins + 1;
      template = new float[L];
      double tmax = 0;
      for (int j = 0; j < L; j++)
      {
        double freqHz = (j - occHalfBins) * binHz;
        float t = (float)ModulationTemplate.ShapeValue(freqHz, p);
        template[j] = t;
        if (t > tmax) tmax = t;
      }
      templateMax = tmax;
    }

    /// <summary>CFO only (used by the residual-after-derotation check).</summary>
    public double Estimate(Complex32[] iq, int start, int end) => SymmetryCfo(BuildPsd(iq, start, end));

    /// <summary>Full spectral info for a burst: symmetry CFO, shape-match score, RMS bandwidth.</summary>
    public BurstSpectralInfo Analyze(Complex32[] iq, int start, int end) => AnalyzeSpectrum(BuildPsd(iq, start, end));

    /// <summary>
    /// Spectral info from an <b>already-built</b> in-band power spectrum (noise-subtracted, DC-notched, on this
    /// estimator's grid — length <c>2·occHalfBins+1</c>, index <c>occHalfBins</c> = 0 Hz). Lets a caller that
    /// already has the STFT power (e.g. the streaming detector, which averages the same frames it detects on)
    /// reuse it instead of recomputing an FFT here.
    /// </summary>
    public BurstSpectralInfo AnalyzeSpectrum(float[] q)
    {
      // two-tone FSK → slide the template for the best fit (anchors the carrier at the midpoint of both
      // tones, robust to amplitude imbalance). Bell modulations fall back to PSD mirror-symmetry.
      double cfo = useTemplateCfo ? TemplateCfo(q) : SymmetryCfo(q);
      return new BurstSpectralInfo(cfo, ShapeScore(q, cfo), RmsBandwidth(q, cfo));
    }

    /// <summary>Number of in-band bins this estimator's spectra carry (so a caller can size a matching buffer).</summary>
    public int SpectrumLength => 2 * occHalfBins + 1;

    /// <summary>
    /// Measure the burst's empirical spectral shape: the noise-subtracted PSD, resampled about the carrier
    /// onto the baud-normalized <see cref="LearnedShape"/> grid and peak-normalized, plus the tone deviation
    /// (dominant peak each side of the carrier) and RMS bandwidth. Correlated against the modeled template
    /// for burst validation, and rendered in the shape view.
    /// </summary>
    public LearnedShape EstimateShape(Complex32[] iq, int start, int end, double cfoHz)
      => EstimateShapeFromSpectrum(BuildPsdAveraged(iq, start, end), cfoHz);

    /// <summary>
    /// As <see cref="EstimateShape(Complex32[],int,int,double)"/>, but from an already-built in-band power
    /// spectrum on this estimator's grid (noise-subtracted, DC-notched). For callers that already have the
    /// averaged STFT power and shouldn't recompute it.
    /// </summary>
    public LearnedShape EstimateShapeFromSpectrum(float[] q, double cfoHz)
    {
      int n = LearnedShape.GridPoints;
      var profile = new float[n];
      double peak = 1e-12;
      for (int i = 0; i < n; i++)
      {
        double freqHz = cfoHz + LearnedShape.BaudAt(i) * baud;  // offset from the carrier → absolute (q is at 0 Hz)
        float v = (float)Math.Max(0, SampleQ(q, freqHz));
        profile[i] = v;
        if (v > peak) peak = v;
      }
      for (int i = 0; i < n; i++) profile[i] = (float)(profile[i] / peak);

      return new LearnedShape
      {
        DeviationHz = EstimateDeviation(profile, baud),
        BandwidthHz = RmsBandwidth(q, cfoHz),
        // the measured spectrum carries data only across the occupied band [−occHalfHz, +occHalfHz] (centred at
        // the carrier after the CFO shift); beyond that it is hard zero. Tell the shape match where the real
        // data ends so it doesn't read the band-limit edge as a modulation skirt (false match on filtered noise).
        ValidHalfBaud = (occHalfHz - Math.Abs(cfoHz)) / baud,
        Profile = profile,
        Count = 1,
      };
    }

    /// <summary>Linear-interpolated PSD power at an absolute offset <paramref name="freqHz"/> from DC (0 outside the band).</summary>
    private double SampleQ(float[] q, double freqHz)
    {
      double x = freqHz / binHz + occHalfBins;
      if (x < 0 || x > q.Length - 1) return 0;
      int j = (int)Math.Floor(x);
      if (j >= q.Length - 1) return q[q.Length - 1];
      double mu = x - j;
      return q[j] * (1 - mu) + q[j + 1] * mu;
    }

    /// <summary>Tone deviation = mean |offset| of the dominant PSD peak on each side of the carrier (guarded off DC).</summary>
    private static double EstimateDeviation(float[] profile, double baud)
    {
      const double guardBaud = 0.3;
      double posBaud = 0, negBaud = 0; float posMax = 0, negMax = 0;
      for (int i = 0; i < profile.Length; i++)
      {
        double fb = LearnedShape.BaudAt(i);
        if (fb > guardBaud && profile[i] > posMax) { posMax = profile[i]; posBaud = fb; }
        if (fb < -guardBaud && profile[i] > negMax) { negMax = profile[i]; negBaud = -fb; }
      }
      return posMax > 0 && negMax > 0 ? (posBaud + negBaud) / 2.0 * baud : 0;
    }

    /// <summary>Coarse normalized PSD profile across the search band (0..9 per bin), for diagnostics.</summary>
    public int[] Profile(Complex32[] iq, int start, int end, int nbins = 48)
    {
      var q = BuildPsd(iq, start, end);
      int L = q.Length;
      var vals = new double[nbins];
      double max = 1e-12;
      for (int b = 0; b < nbins; b++)
      {
        int j0 = b * L / nbins, j1 = (b + 1) * L / nbins;
        double s = 0; for (int j = j0; j < j1; j++) s += q[j];
        vals[b] = s / Math.Max(1, j1 - j0);
        if (vals[b] > max) max = vals[b];
      }
      var prof = new int[nbins];
      for (int b = 0; b < nbins; b++) prof[b] = (int)Math.Round(9 * vals[b] / max);
      return prof;
    }

    /// <summary>Noise-subtracted in-band PSD in natural frequency order; index C = 0 Hz, DC notched.</summary>
    private float[] BuildPsd(Complex32[] iq, int start, int end)
    {
      int len = end - start;
      int copy = Math.Min(len, size);
      int from = start + Math.Max(0, (len - size) / 2);

      Array.Clear(fft.InputData);
      for (int i = 0; i < copy; i++)
        fft.InputData[i] = iq[from + i] * window[i];
      fft.Execute();

      int L = 2 * occHalfBins + 1;
      var q = new float[L];
      var oob = new List<float>(); // out-of-band powers for the noise floor
      for (int k = 0; k < size; k++)
      {
        double f = (k <= size / 2 ? k : k - size) * binHz;
        var c = fft.OutputData[k];
        float pw = c.Real * c.Real + c.Imaginary * c.Imaginary;
        if (Math.Abs(f) <= occHalfHz)
        {
          int j = (int)Math.Round(f / binHz) + occHalfBins;
          if ((uint)j < (uint)L) q[j] = pw;
        }
        else oob.Add(pw);
      }

      // raw bin powers are exponential — rescale the trimmed mean to the median's calibration (item 6).
      double noise = NoiseFloor.TrimmedMean(oob) * NoiseFloor.ExponentialMedianScale;
      for (int j = 0; j < L; j++) q[j] = (float)Math.Max(0, q[j] - noise);
      // notch DC (LO leakage) so it doesn't bias the symmetry toward 0 Hz
      for (int j = occHalfBins - 1; j <= occHalfBins + 1; j++)
        if ((uint)j < (uint)L) q[j] = 0;
      return q;
    }

    /// <summary>
    /// Welch-averaged in-band PSD over the whole burst (≤ 32 windows spanning [start,end)): same in-band /
    /// noise-subtracted / DC-notched form as <see cref="BuildPsd"/>, but averaged so the learned/displayed
    /// shape reflects the burst as a whole rather than one centre snapshot. Falls back to a single window
    /// for bursts shorter than the FFT.
    /// </summary>
    private float[] BuildPsdAveraged(Complex32[] iq, int start, int end)
    {
      int len = end - start;
      if (len <= size) return BuildPsd(iq, start, end);

      int L = 2 * occHalfBins + 1;
      var acc = new double[L];
      var oob = new List<float>();
      int nWin = Math.Min(32, (len - size) / (size / 2) + 1);
      for (int w = 0; w < nWin; w++)
      {
        int from = start + (int)((long)w * (len - size) / Math.Max(1, nWin - 1));
        Array.Clear(fft.InputData);
        for (int i = 0; i < size; i++) fft.InputData[i] = iq[from + i] * window[i];
        fft.Execute();
        for (int k = 0; k < size; k++)
        {
          double f = (k <= size / 2 ? k : k - size) * binHz;
          var c = fft.OutputData[k];
          float pw = c.Real * c.Real + c.Imaginary * c.Imaginary;
          if (Math.Abs(f) <= occHalfHz)
          {
            int j = (int)Math.Round(f / binHz) + occHalfBins;
            if ((uint)j < (uint)L) acc[j] += pw;
          }
          else oob.Add(pw);
        }
      }

      var q = new float[L];
      for (int j = 0; j < L; j++) q[j] = (float)(acc[j] / nWin);
      double noise = NoiseFloor.TrimmedMean(oob) * NoiseFloor.ExponentialMedianScale;  // single-window scale == averaged mean
      for (int j = 0; j < L; j++) q[j] = (float)Math.Max(0, q[j] - noise);
      for (int j = occHalfBins - 1; j <= occHalfBins + 1; j++)
        if ((uint)j < (uint)L) q[j] = 0;                      // notch DC/LO leakage
      return q;
    }

    /// <summary>Carrier = axis of maximum PSD mirror-symmetry: argmax_c Σ_j q[j]·q[2c−j], parabola-refined.</summary>
    private double SymmetryCfo(float[] q)
    {
      int L = q.Length;
      // an empty/flat spectrum (a noise blip with no in-band energy) has no symmetry axis: every shift scores 0,
      // so the argmax would otherwise latch onto the first candidate — the search-band EDGE — and report a
      // spurious CFO of exactly ∓CfoMax. Report 0 (DC) instead.
      double energy = 0; for (int j = 0; j < L; j++) energy += q[j];
      if (energy <= 0) return 0;

      int lo = occHalfBins - cfoMaxBins;
      int hi = occHalfBins + cfoMaxBins;
      double best = -1; int bestC = occHalfBins;
      var s = new double[L];

      for (int c = lo; c <= hi; c++)
      {
        double acc = 0;
        int jStart = Math.Max(0, 2 * c - (L - 1));
        int jEnd = Math.Min(L - 1, 2 * c);
        for (int j = jStart; j <= jEnd; j++)
          acc += (double)q[j] * q[2 * c - j];
        s[c] = acc;
        if (acc > best) { best = acc; bestC = c; }
      }

      // parabolic interpolation around the peak for sub-bin accuracy
      double delta = 0;
      if (bestC > lo && bestC < hi)
      {
        double a = s[bestC - 1], b = s[bestC], d = s[bestC + 1];
        double denom = a - 2 * b + d;
        if (Math.Abs(denom) > 1e-12) delta = 0.5 * (a - d) / denom;
      }
      return (bestC + delta - occHalfBins) * binHz;
    }

    /// <summary>
    /// Carrier = the template shift that maximizes cross-correlation with the PSD: argmax_s Σ_j q[j]·T[j−s].
    /// With a learned two-lobe template this requires energy at <i>both</i> tones at the right spacing, so a
    /// single stray tone or a data-imbalanced burst can't pull the estimate the way mirror-symmetry does.
    /// </summary>
    private double TemplateCfo(float[] q)
    {
      int L = q.Length;
      double best = double.NegativeInfinity; int bestS = 0;
      var corr = new double[2 * cfoMaxBins + 1];
      for (int s = -cfoMaxBins; s <= cfoMaxBins; s++)
      {
        double dot = 0;
        for (int j = 0; j < L; j++)
        {
          int tj = j - s;                               // slide the template by s bins
          if ((uint)tj < (uint)L) dot += (double)q[j] * template[tj];
        }
        corr[s + cfoMaxBins] = dot;
        if (dot > best) { best = dot; bestS = s; }
      }
      double delta = 0;
      int c = bestS + cfoMaxBins;
      if (c > 0 && c < corr.Length - 1)
      {
        double a = corr[c - 1], b = corr[c], d = corr[c + 1];
        double denom = a - 2 * b + d;
        if (Math.Abs(denom) > 1e-12) delta = 0.5 * (a - d) / denom;
      }
      return (bestS + delta) * binHz;
    }

    /// <summary>
    /// <b>Pearson correlation</b> of the PSD to the expected modulation shape, aligned at the CFO and
    /// restricted to the template's significant support. Mean-subtracted and unit-variance normalized: flat
    /// noise → ~0 regardless of the window width, a true shape match → ~1. This is the proper matched
    /// statistic — raw cosine of two non-negative PSDs is dominated by their shared DC pedestal, so it barely
    /// separates signal from noise (noise lands at 0.4–0.6, on top of weak bursts). On real bursts Pearson
    /// gives a clean split (signal ≈0.3–0.4, noise ≈0) where cosine overlaps.
    /// </summary>
    private double ShapeScore(float[] q, double cfoHz)
    {
      int L = q.Length;
      int shift = (int)Math.Round(cfoHz / binHz);
      double tThresh = 0.05 * templateMax;

      // means over the template support
      double qs = 0, ts = 0; int n = 0;
      for (int j = 0; j < L; j++)
      {
        int tj = j - shift;
        float t = (uint)tj < (uint)L ? template[tj] : 0f;
        if (t < tThresh) continue; // only the signal band, not the noise margin
        qs += q[j]; ts += t; n++;
      }
      if (n == 0) return 0;
      double qm = qs / n, tm = ts / n;

      double num = 0, qv = 0, tv = 0;
      for (int j = 0; j < L; j++)
      {
        int tj = j - shift;
        float t = (uint)tj < (uint)L ? template[tj] : 0f;
        if (t < tThresh) continue;
        double dq = q[j] - qm, dt = t - tm;
        num += dq * dt; qv += dq * dq; tv += dt * dt;
      }
      return qv > 0 && tv > 0 ? num / (Math.Sqrt(qv) * Math.Sqrt(tv)) : 0;
    }

    /// <summary>RMS bandwidth of the (noise-subtracted) PSD about the CFO, in Hz.</summary>
    private double RmsBandwidth(float[] q, double cfoHz)
    {
      double sum = 0, sumf2 = 0;
      for (int j = 0; j < q.Length; j++)
      {
        double f = (j - occHalfBins) * binHz - cfoHz;
        sum += q[j];
        sumf2 += q[j] * f * f;
      }
      return sum > 0 ? 2 * Math.Sqrt(sumf2 / sum) : 0; // 2·sigma ~ full RMS width
    }

    public void Dispose() => fft.Dispose();
  }
}
