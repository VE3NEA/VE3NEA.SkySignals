using System;
using System.Collections.Generic;
using System.Linq;

namespace VE3NEA.SkySSTV
{
  /// <summary>Outcome of mode inference (plan §4).</summary>
  /// <param name="Found">A mode was identified (from VIS or the sync cadence).</param>
  /// <param name="Mode">The identified mode, or null.</param>
  /// <param name="FromVis">True if the decision came from a valid VIS header (strong prior); false if from the MHT.</param>
  /// <param name="LinePeriodMs">Measured (MHT) or tabulated (VIS) line period, ms — a diagnostic.</param>
  /// <param name="SyncScore">Mean matched-filter score of the detected sync train (0 if VIS-only).</param>
  /// <param name="FirstSyncSample">Sample index of the first line's sync onset (the burst start), or −1.</param>
  /// <param name="Vis">The raw VIS result (its own score seeds the prior even when not Found).</param>
  public readonly record struct SstvModeResult(
    bool Found, SstvMode? Mode, bool FromVis, double LinePeriodMs, double SyncScore, int FirstSyncSample,
    SstvVisResult Vis);

  /// <summary>
  /// Mode inference (plan §4): a valid VIS header is a strong prior; the multiple-hypothesis tracker (MHT)
  /// scores every candidate mode against the observed sync cadence and sync-duration. Both run always — VIS
  /// confirms/seeds, MHT fills in when VIS is absent and can override a corrupted-but-parseable VIS when the
  /// sync evidence strongly contradicts it.
  ///
  /// <para>The MHT reuses the §4 matched-filter template bank (<see cref="SstvSyncFilter"/>): for each
  /// <b>sync-duration family</b> (Robot 9 ms vs PD 20 ms) it detects the sync-pulse train at that pulse
  /// length; the family whose template scores highest fixes the sync duration, and the median inter-pulse
  /// spacing gives the line period — which pins the specific mode within the family. Because the correct
  /// pulse length scores strictly higher (a too-short template catches the far sync edge in its flank, a
  /// too-long one dilutes the pulse mean with porch), the family choice is unambiguous on clean signals.</para>
  /// </summary>
  internal static class SstvModeDetector
  {
    private const double PeakThreshold = 0.15;    // matched-filter score to register a sync pulse (below the tracker gate)

    public static SstvModeResult Detect(double[] disc, double fs, SstvDecodeOptions o)
    {
      var vis = SstvVisDetector.Detect(disc, fs, 0, o.AcquireSearchSamples);
      int searchStart = vis.Found ? vis.HeaderEndSample : 0;   // skip the VIS header's own 1200 blips
      int minSpacing = (int)Math.Round(0.06 * fs);             // below the shortest line period (~91 ms)

      var filter = new SstvSyncFilter(disc, fs);

      // one hypothesis per sync-duration family (grouped by pulse length): score the sync train at each length.
      double bestScore = -1, bestPeriod = 0;
      double bestSyncMs = 0;
      int bestFirstSync = -1;
      foreach (var fam in SstvModes.All.GroupBy(m => m.SyncMs))
      {
        int pulseLen = (int)Math.Round(fam.Key / 1000.0 * fs);
        var peaks = FindSyncTrain(filter, pulseLen, searchStart, minSpacing);
        if (peaks.Count < 3) continue;
        double meanScore = peaks.Average(p => filter.Score(p, pulseLen));
        if (meanScore > bestScore)
        { bestScore = meanScore; bestPeriod = MedianSpacing(peaks); bestSyncMs = fam.Key; bestFirstSync = peaks[0]; }
      }

      // within the winning family, the mode whose tabulated line period best matches the measured cadence.
      SstvMode? mht = null; double mhtPeriodMs = 0;
      if (bestScore > 0)
      {
        double periodMs = bestPeriod / fs * 1000.0;
        var pick = SstvModes.All.Where(m => m.SyncMs == bestSyncMs)
                                .OrderBy(m => Math.Abs(m.LinePeriodMs - periodMs)).First();
        mht = pick.Mode; mhtPeriodMs = periodMs;
      }

      // reconcile: trust a valid VIS unless the MHT is confident and disagrees on the sync-duration family.
      if (vis.Found && vis.Mode is SstvMode vm)
      {
        bool override_ = mht is SstvMode mm && SstvModes.Get(mm).SyncMs != SstvModes.Get(vm).SyncMs
                      && bestScore > 2 * PeakThreshold;
        if (!override_)
        {
          int firstSync = vis.HeaderEndSample >= 0 ? vis.HeaderEndSample : bestFirstSync;
          return new SstvModeResult(true, vm, true, SstvModes.Get(vm).LinePeriodMs, bestScore, firstSync, vis);
        }
      }

      if (mht is SstvMode picked)
        return new SstvModeResult(true, picked, false, mhtPeriodMs, bestScore, bestFirstSync, vis);
      return new SstvModeResult(false, null, false, 0, 0, -1, vis);
    }

    /// <summary>Detect the train of sync-pulse onsets for <paramref name="pulseLen"/> from
    /// <paramref name="start"/> onward: each above-threshold local maximum of the matched-filter score,
    /// then skip <paramref name="minSpacing"/> past it (below the shortest line period, so no real pulse is
    /// skipped).</summary>
    private static List<int> FindSyncTrain(SstvSyncFilter filter, int pulseLen, int start, int minSpacing)
    {
      var peaks = new List<int>();
      int maxPos = filter.MaxPos(pulseLen);
      int t = Math.Max(0, start);
      while (t <= maxPos)
      {
        if (filter.Score(t, pulseLen) < PeakThreshold) { t++; continue; }
        int pk = t; double best = filter.Score(t, pulseLen);
        int end = Math.Min(maxPos, t + minSpacing);
        for (int j = t + 1; j <= end; j++)
        {
          double s = filter.Score(j, pulseLen);
          if (s > best) { best = s; pk = j; }
        }
        peaks.Add(pk);
        t = pk + minSpacing;
      }
      return peaks;
    }

    /// <summary>Median of the consecutive onset spacings — a robust line-period estimate.</summary>
    private static double MedianSpacing(List<int> peaks)
    {
      var diffs = new List<int>(peaks.Count - 1);
      for (int i = 1; i < peaks.Count; i++) diffs.Add(peaks[i] - peaks[i - 1]);
      diffs.Sort();
      int n = diffs.Count;
      return n == 0 ? 0 : (n % 2 == 1 ? diffs[n / 2] : 0.5 * (diffs[n / 2 - 1] + diffs[n / 2]));
    }
  }
}
