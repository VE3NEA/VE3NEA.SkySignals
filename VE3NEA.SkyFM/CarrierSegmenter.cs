using System;
using System.Collections.Generic;

namespace VE3NEA.SkyFM
{
  /// <summary>One keyed transmission found by the squelch: padded start/end on the audio timeline
  /// (plan §5.2 — the unit the ASR engines consume), tagged with its carrier quieting depth — how far
  /// the squelch noise level dipped below the closed-squelch noise ceiling (dB; NaN when the level
  /// track was not supplied or no ceiling was observed yet). The §5.2 role-(b) confidence input to the
  /// abstention policy: weak carriers dip only 4–5 dB, strong ones far more.</summary>
  public readonly record struct FmTransmission(double StartSeconds, double EndSeconds,
    double QuietingDepthDb = double.NaN)
  {
    public double DurationSeconds => EndSeconds - StartSeconds;
  }

  /// <summary>
  /// Streaming carrier segmenter (plan §5.2): consumes the per-sample squelch gate track from
  /// <see cref="SoftSquelch"/> and produces per-transmission segments — squelch-open spans merged across
  /// short gaps (speech pauses, fades), filtered by minimum duration, padded on both sides. Bounded
  /// state: only the currently open span is held back; a segment is finalized once the gate has stayed
  /// closed longer than the merge gap.
  ///
  /// <para>When the per-sample squelch level track is supplied alongside the gates, each transmission is
  /// tagged with its quieting depth: the mean open-gate level (dB) subtracted from a slow EMA of the
  /// closed-gate level — the local noise ceiling. The EMA holds during open spans, so the ceiling a
  /// transmission is measured against is the noise around it, unbiased by the quieting itself.</para>
  /// </summary>
  public sealed class CarrierSegmenter
  {
    /// <summary>Time constant (s of closed-gate samples) of the noise-ceiling EMA.</summary>
    private const double CeilingTauS = 2.0;

    private readonly double fs;
    private readonly long mergeGap, minLen;
    private readonly double pad;
    private readonly double ceilAlpha;

    private readonly List<FmTransmission> transmissions = new();
    private long index;          // absolute sample index of the next gate sample
    private bool active;         // a span is open (or within the merge gap of one)
    private long start, lastOpen;
    private double ceilDb = double.NaN;             // EMA of the closed-gate level (noise ceiling)
    private double openDbSum; private long openCount;   // level accumulator over the span's open samples
    private double prevDbSum; private long prevCount;   // ditto for the last finalized transmission

    /// <summary>Transmissions finalized so far, in time order.</summary>
    public IReadOnlyList<FmTransmission> Transmissions => transmissions;

    /// <summary>Earliest time (s) a still-pending span might need audio from — the open span's start,
    /// or the stream position when no span is open. A streaming host may discard audio older than this
    /// minus the segment pad (plan §1.13 bounded state).</summary>
    public double PendingStartSeconds => active ? start / fs : index / fs;

    public CarrierSegmenter(FmDecodeOptions o)
    {
      fs = o.SampleRate;
      mergeGap = (long)(o.SegmentMergeGapS * fs);
      minLen = (long)(o.SegmentMinS * fs);
      pad = o.SegmentPadS;
      ceilAlpha = 1.0 / (CeilingTauS * fs);
    }

    /// <summary>Feed the next block of per-sample gate flags (1 = squelch open); transmissions get no
    /// quieting depth.</summary>
    public void Process(ReadOnlySpan<byte> gates) => Process(gates, default);

    /// <summary>Feed the next block of per-sample gate flags, with the matching smoothed squelch noise
    /// levels (cycles/sample, as reported by <see cref="SoftSquelch"/>) to measure quieting
    /// depth.</summary>
    public void Process(ReadOnlySpan<byte> gates, ReadOnlySpan<float> levels)
    {
      for (int i = 0; i < gates.Length; i++, index++)
      {
        double db = levels.IsEmpty ? double.NaN : 20.0 * Math.Log10(Math.Max(levels[i], 1e-9f));

        if (gates[i] != 0)
        {
          if (!active)
          {
            active = true;
            start = index;
            openDbSum = 0;
            openCount = 0;
          }
          lastOpen = index;
          if (!double.IsNaN(db)) { openDbSum += db; openCount++; }
        }
        else
        {
          if (!double.IsNaN(db)) ceilDb = double.IsNaN(ceilDb) ? db : ceilDb + ceilAlpha * (db - ceilDb);
          if (active && index - lastOpen > mergeGap)
          {
            Close();
            active = false;
          }
        }
      }
    }

    /// <summary>End of stream: finalize a still-open span. Call once after the last
    /// <see cref="Process"/>.</summary>
    public void Flush()
    {
      if (active) Close();
      active = false;
    }

    private void Close()
    {
      if (lastOpen - start + 1 < minLen) return;
      double s = Math.Max(0.0, start / fs - pad);
      double e = (lastOpen + 1) / fs + pad;

      // padding may bridge into the previous segment: extend it instead of overlapping, pooling the
      // open-level statistics so the merged transmission's depth covers both spans
      if (transmissions.Count > 0 && s <= transmissions[^1].EndSeconds)
      {
        openDbSum += prevDbSum;
        openCount += prevCount;
        transmissions[^1] = transmissions[^1] with { EndSeconds = e, QuietingDepthDb = Depth() };
      }
      else
        transmissions.Add(new FmTransmission(s, e, Depth()));

      prevDbSum = openDbSum;
      prevCount = openCount;
    }

    /// <summary>Quieting depth of the just-closed span: noise ceiling minus the mean open-gate level
    /// (dB); NaN without levels or before any closed-gate sample seeded the ceiling.</summary>
    private double Depth()
      => openCount == 0 || double.IsNaN(ceilDb) ? double.NaN : ceilDb - openDbSum / openCount;
  }
}
