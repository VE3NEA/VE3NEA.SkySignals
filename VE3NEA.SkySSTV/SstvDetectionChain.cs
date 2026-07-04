using System;
using System.Collections.Generic;

namespace VE3NEA.SkySSTV
{
  /// <summary>
  /// The live detection graph (plan §7 P7.5(b)/(c)): per-family streaming <see cref="SstvPulseDetector"/>s
  /// riding a shared <see cref="SstvSoftComb"/>, feeding one persistent <see cref="SstvPulseTrainExtractor"/>
  /// on the 0.25 s block boundaries they already assume — the streaming heart that
  /// <c>SstvDecoder.ExtractTrains</c> used to run as a whole-array loop (it is now a thin batch wrapper over
  /// this class). Fed Stage-2 sync audio in arbitrary block sizes; pulse times are emitted in stream
  /// coordinates (the internal warm-up pad is subtracted).
  ///
  /// <para>Block boundaries are fixed to the padded-stream grid regardless of the caller's block sizes, so
  /// the chain behaves identically for any push pattern. The only nondeterminism vs the batch loop is the
  /// ordering of two different-family pulses landing on the same sample (the time sort cannot separate
  /// them) — never observed to matter.</para>
  /// </summary>
  internal sealed class SstvDetectionChain
  {
    private readonly double fs;
    private readonly int pad;                  // lead-in: > 2× the longest sync template
    private readonly int maxLatency;           // bound on detector emission lag past an onset
    private readonly int blockSize;

    private readonly SstvSoftComb comb;
    private readonly SstvPulseDetector[] detectors;
    private readonly List<SstvPulse> raw = new();
    private readonly List<SstvPulse> pending = new();
    private readonly List<SstvPulse> deliver = new();

    private long paddedPos;                    // samples fed to the detectors (incl. the pad)
    private long boundaryDone;                 // padded position of the last completed block boundary

    public SstvPulseTrainExtractor Extractor { get; }

    /// <summary>Raised when a train retires at a block boundary — the streaming decoder's image-finalize
    /// hook (plan §1.10).</summary>
    public Action<SstvPulseTrain>? TrainRetired { get; set; }

    /// <summary>Sync-audio samples consumed so far (stream coordinates).</summary>
    public long SyncConsumed => paddedPos - pad;

    public SstvDetectionChain(double fs)
    {
      this.fs = fs;
      Extractor = new SstvPulseTrainExtractor(fs);

      var families = new List<double>();
      foreach (var spec in SstvModes.All)
        if (!families.Contains(spec.SyncMs)) families.Add(spec.SyncMs);

      pad = (int)Math.Round(0.05 * fs);
      maxLatency = (int)Math.Round(0.10 * fs);
      blockSize = (int)Math.Round(0.25 * fs);

      // the streaming soft-comb rides the detectors' un-thresholded score streams (plan §4.1): a confirmed
      // hit seeds a high-prior back-dated train — the sensitivity floor for transmissions whose single
      // pulses never separate from noise (the 04-18 class)
      comb = new SstvSoftComb(fs);
      detectors = new SstvPulseDetector[families.Count];
      for (int i = 0; i < families.Count; i++)
      {
        double family = families[i];
        detectors[i] = new SstvPulseDetector(fs, family)
        {
          Threshold = SstvPulseDetector.AssocThreshold,      // two-tier soft evidence (plan §4.1)
          ScoreTap = (t, s) => comb.Process(family, t - pad, s)
        };
      }

      // warm the detector flanks exactly like the batch front-pad: the pad is shorter than one block, so
      // no boundary work can fall inside it
      for (int i = 0; i < pad; i++)
        foreach (var det in detectors)
          det.Process(0.0, raw);
      // per-detector processing order differs from the batch here (sample-major vs block-major), but the
      // detectors are independent state machines and raw is re-sorted by time at every boundary
      paddedPos = pad;
    }

    /// <summary>Seed a high-prior train from a decoded VIS header. Must be called before the sync samples
    /// at and after the header end are pushed (the streaming caller's VIS tiling guarantees it; pulses
    /// before the anchor never associate to a VIS train, so an early seed is never wrong).</summary>
    public void SeedVis(SstvMode mode, int headerEndSample) => Extractor.AddVisTrain(mode, headerEndSample);

    /// <summary>Feed the next block of Stage-2 sync audio (any size), folding detector output into the
    /// extractor at each fixed 0.25 s boundary crossed.</summary>
    public void Process(ReadOnlySpan<double> sync)
    {
      int at = 0;
      while (at < sync.Length)
      {
        long nextBoundary = (paddedPos / blockSize + 1) * blockSize;
        int len = (int)Math.Min(sync.Length - at, nextBoundary - paddedPos);
        foreach (var det in detectors)
          for (int i = at; i < at + len; i++)
            det.Process(sync[i], raw);
        at += len;
        paddedPos += len;
        if (paddedPos == nextBoundary) OnBoundary(paddedPos);
      }
    }

    /// <summary>End of stream: the final partial-block boundary work, the detectors' tail flush, and the
    /// extractor's last lifecycle pass. Call once after the last <see cref="Process"/>.</summary>
    public void Finish()
    {
      if (paddedPos > boundaryDone) OnBoundary(paddedPos);

      int syncLen = (int)(paddedPos - pad);
      raw.Clear();
      foreach (var det in detectors) det.Flush(raw);
      foreach (var p in raw) pending.Add(new SstvPulse(p.Time - pad, p.Power, p.DurMs));
      pending.Sort((a, b) => a.Time.CompareTo(b.Time));
      Extractor.Process(pending, syncLen);
      pending.Clear();
      Extractor.Finish(syncLen);
    }

    /// <summary>The per-block fold (the batch loop body): re-order the detectors' differing emission
    /// latencies, let a confirmed comb hit seed its back-dated train before this block's pulses are
    /// associated, deliver only onset-ordered pulses, and flush a retiring train's family rings so the
    /// decaying ridge cannot re-fire as a phantom seed.</summary>
    private void OnBoundary(long blockEnd)
    {
      boundaryDone = blockEnd;
      foreach (var p in raw) pending.Add(new SstvPulse(p.Time - pad, p.Power, p.DurMs));
      raw.Clear();
      pending.Sort((a, b) => a.Time.CompareTo(b.Time));

      if (comb.Check((int)(blockEnd - pad)) is SstvCombHit hit)
        Extractor.AddCombTrain(hit.Mode, (int)hit.AnchorSample);

      int safeTime = (int)(blockEnd - pad - maxLatency);
      deliver.Clear();
      int cnt = 0;
      while (cnt < pending.Count && pending[cnt].Time <= safeTime) deliver.Add(pending[cnt++]);
      pending.RemoveRange(0, cnt);
      Extractor.Process(deliver, (int)(blockEnd - pad));

      if (Extractor.RetiredTrain is SstvPulseTrain retired)
      {
        comb.ResetFamily(SstvModes.Get(retired.Format).SyncMs);
        TrainRetired?.Invoke(retired);
      }
    }
  }
}
