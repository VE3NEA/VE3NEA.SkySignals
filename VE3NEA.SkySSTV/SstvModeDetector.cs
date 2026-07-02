using System;
using System.Collections.Generic;

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
  /// Mode inference (plan §4/§4.1): valid VIS headers are strong priors; the multiple-hypothesis tracker
  /// (<see cref="SstvPulseTrainExtractor"/>) scores every candidate mode against the observed sync cadence
  /// and sync-duration family. Both run continuously over the whole stream — every VIS hit seeds a
  /// high-prior train that promotes on 3 confirming pulses, the MHT fills in when VIS is absent, and a
  /// corrupted-but-parseable VIS arbitrates itself: its train collects no confirming pulses and dies while
  /// the true cadence promotes a plain candidate. No single pulse is ever thresholded into a decision: the
  /// winning train is the one whose period-consistent pulse train integrated the most soft evidence
  /// (weak-and-consistent beats strong-and-scattered).
  /// </summary>
  internal static class SstvModeDetector
  {
    public static SstvModeResult Detect(double[] sync, double fs, SstvDecodeOptions o)
    {
      var hits = SstvVisDetector.DetectAll(sync, fs);
      var extractor = SstvDecoder.ExtractTrains(sync, fs, hits);
      var best = extractor.BestTrain();

      if (best is SstvVisPulseTrain visTrain)
        return new SstvModeResult(true, visTrain.Format, true, visTrain.Regr.Period / fs * 1000.0,
          visTrain.MeanPower, (int)Math.Round(visTrain.Regr.GetPulseTime(0)), HitFor(hits, visTrain));

      if (best is SstvPulseTrain picked)
        return new SstvModeResult(true, picked.Format, false, picked.Regr.Period / fs * 1000.0,
          picked.MeanPower, (int)Math.Round(picked.Regr.GetPulseTime(0)), FirstHit(hits));

      // no promoted train: a parity-valid VIS alone still identifies the mode (e.g. a signal cut short)
      foreach (var hit in hits)
        if (hit.Mode is SstvMode vm)
          return new SstvModeResult(true, vm, true, SstvModes.Get(vm).LinePeriodMs, 0, hit.HeaderEndSample, hit);
      return new SstvModeResult(false, null, false, 0, 0, -1, FirstHit(hits));
    }

    /// <summary>The VIS hit that seeded <paramref name="train"/> (matched by its anchor sample).</summary>
    private static SstvVisResult HitFor(List<SstvVisResult> hits, SstvVisPulseTrain train)
    {
      foreach (var hit in hits) if (hit.HeaderEndSample == train.VisTime) return hit;
      return FirstHit(hits);
    }

    private static SstvVisResult FirstHit(List<SstvVisResult> hits) => hits.Count > 0 ? hits[0] : default;
  }
}
