using System;

namespace VE3NEA.SkySSTV
{
  /// <summary>
  /// Horizontal-sync detector (plan §7): a fixed 1200 Hz complex correlator on the FM-discriminated
  /// audio with a window equal to the mode's sync duration. <c>|corr|</c> (coherence) gives sync
  /// <b>presence</b>; the correlator's argument gives the sub-sample <b>phase</b> that KF1 will track
  /// (P3). Here in P2 it drives <b>timing acquisition</b> when there is no VIS header: the first strong
  /// coherence peak is the onset of line 0's sync pulse, i.e. the image <see cref="SstvDecodeOptions.StartSample"/>.
  ///
  /// The correlator is independent of any AFC (FM-on-FM has no audio-frequency offset, plan §1.6), so
  /// there is no sync↔AFC chicken-and-egg: 1200 Hz is an absolute frequency here.
  /// </summary>
  internal static class SstvSyncCorrelator
  {
    /// <summary>A window whose 1200 Hz coherence clears this is treated as an aligned sync pulse.</summary>
    private const double SyncCoherenceThreshold = 0.25;

    /// <summary>Coherence track: 1200 Hz coherence over the sliding window [i, i+<paramref name="windowSamples"/>)
    /// for each start i in the absolute range [<paramref name="start"/>, start+<paramref name="length"/>).
    /// Result index 0 corresponds to absolute sample <paramref name="start"/>. Used by acquisition and by
    /// tests that verify sync pulses recur at the line period.</summary>
    public static double[] CoherenceTrack(double[] disc, double fs, int windowSamples, int start, int length)
    {
      var bank = new SstvToneBank(disc, fs, SstvTones.Sync, start, length + windowSamples);
      int n = Math.Max(0, Math.Min(length, disc.Length - start));
      var track = new double[n];
      for (int i = 0; i < n; i++)
        track[i] = bank.Coherence(start + i, start + i + windowSamples);
      return track;
    }

    /// <summary>Absolute sample index of the first sync pulse's onset within [<paramref name="start"/>,
    /// start+<paramref name="length"/>), or −1 if none clears the threshold. The window is the mode's
    /// sync duration; the returned index is the first local coherence maximum above threshold, which for
    /// a leading sync pulse lands on its first sample.</summary>
    public static int FindFirstSync(double[] disc, double fs, SstvModeSpec spec, int start, int length)
    {
      int win = (int)Math.Round(spec.SyncMs / 1000.0 * fs);
      if (win < 1) win = 1;
      double[] coh = CoherenceTrack(disc, fs, win, start, length);
      if (coh.Length == 0) return -1;

      // first index that both clears the threshold and is a local maximum over ± half a sync window
      int guard = Math.Max(1, win / 2);
      for (int i = 0; i < coh.Length; i++)
      {
        if (coh[i] < SyncCoherenceThreshold) continue;
        bool peak = true;
        for (int j = Math.Max(0, i - guard); j <= Math.Min(coh.Length - 1, i + guard); j++)
          if (coh[j] > coh[i]) { peak = false; break; }
        if (peak) return start + i;
      }
      return -1;
    }
  }
}
