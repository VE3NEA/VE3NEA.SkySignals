using System;

namespace VE3NEA.SkySSTV
{
  /// <summary>Outcome of mode inference (plan §4).</summary>
  /// <param name="Found">A mode was identified (from VIS or the sync cadence).</param>
  /// <param name="Mode">The identified mode, or null.</param>
  /// <param name="FromVis">True if the decision came from a valid VIS header (strong prior); false if from the MHT.</param>
  /// <param name="LinePeriodMs">Measured (MHT) or tabulated (VIS) line period, ms — a diagnostic.</param>
  /// <param name="SyncScore">Mean matched-filter score of the winning train's pulses (0 if VIS-only).</param>
  /// <param name="FirstSyncSample">Sample index of the first line's sync onset (the burst start), or −1.</param>
  /// <param name="Vis">The raw VIS result (its own score seeds the prior even when not Found).</param>
  public readonly record struct SstvModeResult(
    bool Found, SstvMode? Mode, bool FromVis, double LinePeriodMs, double SyncScore, int FirstSyncSample,
    SstvVisResult Vis);

  /// <summary>
  /// Mode inference (plan §4/§4.1): a valid VIS header is a strong prior; the multiple-hypothesis tracker
  /// (<see cref="SstvPulseTrainExtractor"/>) scores every candidate mode against the observed sync cadence
  /// and sync-duration family. Both run always — a VIS seeds a high-prior train that promotes on 3
  /// confirming pulses, the MHT fills in when VIS is absent, and it can override a corrupted-but-parseable
  /// VIS when a strong train of a different sync-duration family dominates the pass. No single pulse is
  /// ever thresholded into a decision: the winning train is the one whose period-consistent pulse train
  /// integrated the most soft evidence (weak-and-consistent beats strong-and-scattered).
  /// </summary>
  internal static class SstvModeDetector
  {
    public static SstvModeResult Detect(double[] sync, double fs, SstvDecodeOptions o)
    {
      var vis = SstvVisDetector.Detect(sync, fs, 0, o.AcquireSearchSamples);
      var extractor = SstvDecoder.ExtractTrains(sync, fs, vis);
      var best = extractor.BestTrain();
      double score = best?.MeanPower ?? 0;

      // reconcile: trust a valid VIS unless a strong train of a different sync-duration family dominates
      if (vis.Found && vis.Mode is SstvMode vm)
      {
        bool contradicted = best is SstvPulseTrain train
          && SstvModes.Get(train.Format).SyncMs != SstvModes.Get(vm).SyncMs
          && train.MeanPower > 2 * SstvPulseDetector.ScoreThreshold;
        if (!contradicted)
          return new SstvModeResult(true, vm, true, SstvModes.Get(vm).LinePeriodMs, score, vis.HeaderEndSample, vis);
      }

      if (best is SstvPulseTrain picked)
        return new SstvModeResult(true, picked.Format, false, picked.Regr.Period / fs * 1000.0,
          picked.MeanPower, (int)Math.Round(picked.Regr.GetPulseTime(0)), vis);
      return new SstvModeResult(false, null, false, 0, 0, -1, vis);
    }
  }
}
