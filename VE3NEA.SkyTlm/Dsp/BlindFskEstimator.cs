namespace VE3NEA.SkyTlm.Dsp
{
  /// <summary>Blind estimation result from the averaged burst PSD.</summary>
  /// <param name="CfoHz">Estimated carrier offset from DC, Hz.</param>
  /// <param name="DeviationHz">Estimated tone deviation, Hz.</param>
  /// <param name="Confidence">Relative autocorrelation peak (0..1); higher = more two-tone.</param>
  /// <param name="IsFsk">True when all FSK gates pass (two-sided, central dip, occupancy). False → drop the burst.</param>
  public sealed record BlindFskResult(double CfoHz, double DeviationHz, double Confidence, bool IsFsk);

  /// <summary>
  /// Deviation- and carrier-blind FSK parameter estimator for cold-start bursts whose
  /// <see cref="Core.SignalParams.Deviation"/> is unknown.  Operates on the Welch-averaged
  /// in-band burst PSD that the streaming detector already accumulates (<c>avgQ</c> in
  /// <see cref="Core.StreamingPipeline"/>): no additional FFT pass.
  ///
  /// <para><b>Pipeline:</b>
  /// (1) PSD autocorrelation <c>R[τ] = Σ q[f]·q[f+τ]</c> → peak at <c>τ = 2·dev</c>;
  /// (2) mirror-symmetry carrier (PSD self-convolution argmax);
  /// (3) refine carrier as midpoint of the two parabola-peaks at <c>±dev</c>;
  /// (4) gates: two-sidedness, central dip, occupancy → <see cref="BlindFskResult.IsFsk"/>.</para>
  /// </summary>
  internal static class BlindFskEstimator
  {
    // plausible h range [0.3, 6] ⇒ dev/baud ∈ [0.15, 3]
    private const double DMinFrac = 0.15, DMaxFrac = 3.0;

    // ---- public API -------------------------------------------------------------------------

    /// <summary>
    /// Full blind estimation: carrier + deviation from the averaged PSD.
    /// </summary>
    /// <param name="avgQ">Noise-subtracted, DC-notched in-band PSD, length = 2·<paramref name="occHalfBins"/>+1,
    /// index <paramref name="occHalfBins"/> = 0 Hz.</param>
    /// <param name="occHalfBins">Centre index of <paramref name="avgQ"/> (DC = 0 Hz).</param>
    /// <param name="binHz">Hz per bin.</param>
    /// <param name="baud">Symbol rate, Hz.</param>
    /// <param name="cfoMaxHz">CFO search half-range, Hz.</param>
    public static BlindFskResult Estimate(float[] avgQ, int occHalfBins, double binHz, double baud, double cfoMaxHz)
    {
      int L = avgQ.Length;
      int cfoMaxBins = (int)Math.Ceiling(cfoMaxHz / binHz);
      double dMin = DMinFrac * baud, dMax = DMaxFrac * baud;
      int tauMin = Math.Max(2, (int)Math.Ceiling(2.0 * dMin / binHz));
      int tauMax = Math.Min(L - 1, (int)Math.Floor(2.0 * dMax / binHz));

      if (tauMax < tauMin)
        return new BlindFskResult(0, 0, 0, false);

      // step 1 — PSD autocorrelation: R[τ] = Σ q[j]·q[j+τ], peak at τ = 2·dev
      // carrier-independent: the cross-correlation of the two symmetric tones appears at τ = 2·dev
      // regardless of where the carrier sits.
      double R0 = 0;
      for (int j = 0; j < L; j++) R0 += (double)avgQ[j] * avgQ[j];
      if (R0 <= 0) return new BlindFskResult(0, 0, 0, false);

      var R = new double[tauMax + 1];
      for (int tau = tauMin; tau <= tauMax; tau++)
        for (int j = 0; j + tau < L; j++)
          R[tau] += (double)avgQ[j] * avgQ[j + tau];

      int bestTau = tauMin;
      double bestR = double.NegativeInfinity;
      for (int tau = tauMin; tau <= tauMax; tau++)
        if (R[tau] > bestR) { bestR = R[tau]; bestTau = tau; }

      // parabola refinement of the spacing lag
      double tauFrac = bestTau;
      if (bestTau > tauMin && bestTau < tauMax)
      {
        double a = R[bestTau - 1], b = R[bestTau], c = R[bestTau + 1];
        double denom = a - 2 * b + c;
        if (Math.Abs(denom) > 1e-12) tauFrac = bestTau + 0.5 * (a - c) / denom;
      }
      double dev = Math.Max(dMin, Math.Min(dMax, tauFrac * binHz / 2.0));
      int devBins = (int)Math.Round(dev / binHz);

      // step 2 — mirror-symmetry carrier: argmax_c Σ q[j]·q[2c−j], clamped to ±cfoMaxHz
      int lo = Math.Max(0, occHalfBins - cfoMaxBins);
      int hi = Math.Min(L - 1, occHalfBins + cfoMaxBins);
      double bestSym = double.NegativeInfinity; int bestC = occHalfBins;
      var sym = new double[hi - lo + 1];
      for (int c = lo; c <= hi; c++)
      {
        double acc = 0;
        int jLo = Math.Max(0, 2 * c - (L - 1));
        int jHi = Math.Min(L - 1, 2 * c);
        for (int j = jLo; j <= jHi; j++) acc += (double)avgQ[j] * avgQ[2 * c - j];
        sym[c - lo] = acc;
        if (acc > bestSym) { bestSym = acc; bestC = c; }
      }

      // parabola refinement of the carrier
      double cDelta = 0;
      int ci = bestC - lo;
      if (ci > 0 && ci < sym.Length - 1)
      {
        double a = sym[ci - 1], b = sym[ci], d = sym[ci + 1];
        double denom = a - 2 * b + d;
        if (Math.Abs(denom) > 1e-12) cDelta = 0.5 * (a - d) / denom;
      }
      double cfoHz = (bestC - occHalfBins + cDelta) * binHz;

      // step 3 — refine carrier as midpoint of the two peaks at ±dev
      int approxCarrier = occHalfBins + (int)Math.Round(cfoHz / binHz);
      cfoHz = RefineCarrierFromPeaks(avgQ, approxCarrier, devBins, occHalfBins, binHz, cfoMaxBins, L);

      // step 4 — FSK gates
      int carrierBin = occHalfBins + (int)Math.Round(cfoHz / binHz);
      bool twoSided = CheckTwoSidedness(avgQ, carrierBin, devBins, L);
      bool centralDip = CheckCentralDip(avgQ, carrierBin, devBins, L);
      bool inRange = dev >= dMin && dev <= dMax;
      bool isFsk = twoSided && centralDip && inRange;

      double confidence = bestR / R0;
      return new BlindFskResult(cfoHz, dev, confidence, isFsk);
    }

    /// <summary>
    /// CFO-only estimation when the deviation is already known (session-learned path):
    /// slides a two-lobe template at <paramref name="deviationHz"/> over ±<paramref name="cfoMaxHz"/>
    /// and returns the best-matching carrier offset.
    /// </summary>
    public static double EstimateCarrierFromKnownDev(
      float[] avgQ, int occHalfBins, double binHz, double cfoMaxHz, double deviationHz)
    {
      int L = avgQ.Length;
      int cfoMaxBins = (int)Math.Ceiling(cfoMaxHz / binHz);
      int devBins = (int)Math.Round(deviationHz / binHz);

      int lo = Math.Max(0, occHalfBins - cfoMaxBins);
      int hi = Math.Min(L - 1, occHalfBins + cfoMaxBins);

      double best = double.NegativeInfinity; int bestS = occHalfBins;
      var corr = new double[hi - lo + 1];
      for (int c = lo; c <= hi; c++)
      {
        // score = energy at the two expected tone positions
        int p1 = c + devBins, p2 = c - devBins;
        double v = 0;
        if ((uint)p1 < (uint)L) v += avgQ[p1];
        if ((uint)p2 < (uint)L) v += avgQ[p2];
        corr[c - lo] = v;
        if (v > best) { best = v; bestS = c; }
      }

      // parabola refinement
      double delta = 0;
      int si = bestS - lo;
      if (si > 0 && si < corr.Length - 1)
      {
        double a = corr[si - 1], b = corr[si], d = corr[si + 1];
        double denom = a - 2 * b + d;
        if (Math.Abs(denom) > 1e-12) delta = 0.5 * (a - d) / denom;
      }
      return (bestS - occHalfBins + delta) * binHz;
    }

    // ---- helpers ----------------------------------------------------------------------------

    /// <summary>
    /// Refine the carrier estimate as the midpoint of the dominant PSD peaks at
    /// approximately <c>approxCarrier ± devBins</c>.
    /// </summary>
    private static double RefineCarrierFromPeaks(float[] q, int approxCarrier, int devBins,
      int occHalfBins, double binHz, int cfoMaxBins, int L)
    {
      int searchRange = Math.Max(2, devBins / 3);

      int rLo = Math.Max(0, approxCarrier + devBins - searchRange);
      int rHi = Math.Min(L - 1, approxCarrier + devBins + searchRange);
      int lLo = Math.Max(0, approxCarrier - devBins - searchRange);
      int lHi = Math.Min(L - 1, approxCarrier - devBins + searchRange);

      float rMax = 0; int rPeak = (rLo + rHi) / 2;
      for (int j = rLo; j <= rHi; j++) if (q[j] > rMax) { rMax = q[j]; rPeak = j; }

      float lMax = 0; int lPeak = (lLo + lHi) / 2;
      for (int j = lLo; j <= lHi; j++) if (q[j] > lMax) { lMax = q[j]; lPeak = j; }

      if (rMax <= 0 || lMax <= 0)
        return (approxCarrier - occHalfBins) * binHz;

      double carrierBin = (rPeak + lPeak) / 2.0;
      carrierBin = Math.Max(occHalfBins - cfoMaxBins, Math.Min(occHalfBins + cfoMaxBins, carrierBin));
      return (carrierBin - occHalfBins) * binHz;
    }

    /// <summary>
    /// Two-sidedness gate: energy must exist on BOTH sides of the carrier
    /// in the tone band. The ratio of the weaker side to the stronger side must exceed 0.25
    /// to reject CW (one-sided) and noise.
    /// </summary>
    private static bool CheckTwoSidedness(float[] q, int carrierBin, int devBins, int L)
    {
      int halfDev = Math.Max(1, devBins / 2);
      int doubleDev = Math.Max(devBins + 1, devBins * 2);

      double eRight = 0, eLeft = 0;
      for (int j = Math.Max(0, carrierBin + halfDev); j <= Math.Min(L - 1, carrierBin + doubleDev); j++)
        eRight += q[j];
      for (int j = Math.Max(0, carrierBin - doubleDev); j <= Math.Min(L - 1, carrierBin - halfDev); j++)
        eLeft += q[j];

      double eMax = Math.Max(eRight, eLeft);
      double eMin = Math.Min(eRight, eLeft);
      return eMax > 0 && eMin / eMax > 0.25;
    }

    /// <summary>
    /// Central-dip gate: the mean power within half-dev of the carrier
    /// must be less than 60% of the mean power near the tone peaks. Rejects CW (peaks at
    /// carrier) and centered FM/SSTV (flat hump with no dip).
    /// </summary>
    private static bool CheckCentralDip(float[] q, int carrierBin, int devBins, int L)
    {
      int halfDev = Math.Max(1, devBins / 2);
      int peakRange = Math.Max(1, devBins / 4);

      // center energy: within ±halfDev of carrier (excluding DC notch region at ±1)
      double centerSum = 0; int cn = 0;
      for (int j = Math.Max(0, carrierBin - halfDev + 2); j <= Math.Min(L - 1, carrierBin + halfDev - 2); j++)
      { centerSum += q[j]; cn++; }

      // peak energy: ±peakRange around each expected tone
      double peakSum = 0; int pn = 0;
      for (int j = Math.Max(0, carrierBin + devBins - peakRange); j <= Math.Min(L - 1, carrierBin + devBins + peakRange); j++)
      { peakSum += q[j]; pn++; }
      for (int j = Math.Max(0, carrierBin - devBins - peakRange); j <= Math.Min(L - 1, carrierBin - devBins + peakRange); j++)
      { peakSum += q[j]; pn++; }

      double centerMean = cn > 0 ? centerSum / cn : 0;
      double peakMean = pn > 0 ? peakSum / pn : 0;
      return peakMean > 0 && centerMean < 0.6 * peakMean;
    }
  }
}
