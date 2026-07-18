using System;
using System.Collections.Generic;

namespace VE3NEA.SkyFM
{
  /// <summary>One keyed transmission found by the squelch: padded start/end on the audio timeline
  /// (plan §5.2 — the unit the ASR engines consume).</summary>
  public readonly record struct FmTransmission(double StartSeconds, double EndSeconds)
  {
    public double DurationSeconds => EndSeconds - StartSeconds;
  }

  /// <summary>
  /// Streaming carrier segmenter (plan §5.2): consumes the per-sample squelch gate track from
  /// <see cref="SoftSquelch"/> and produces per-transmission segments — squelch-open spans merged across
  /// short gaps (speech pauses, fades), filtered by minimum duration, padded on both sides. Bounded
  /// state: only the currently open span is held back; a segment is finalized once the gate has stayed
  /// closed longer than the merge gap.
  /// </summary>
  public sealed class CarrierSegmenter
  {
    private readonly double fs;
    private readonly long mergeGap, minLen;
    private readonly double pad;

    private readonly List<FmTransmission> transmissions = new();
    private long index;          // absolute sample index of the next gate sample
    private bool active;         // a span is open (or within the merge gap of one)
    private long start, lastOpen;

    /// <summary>Transmissions finalized so far, in time order.</summary>
    public IReadOnlyList<FmTransmission> Transmissions => transmissions;

    public CarrierSegmenter(FmDecodeOptions o)
    {
      fs = o.SampleRate;
      mergeGap = (long)(o.SegmentMergeGapS * fs);
      minLen = (long)(o.SegmentMinS * fs);
      pad = o.SegmentPadS;
    }

    /// <summary>Feed the next block of per-sample gate flags (1 = squelch open).</summary>
    public void Process(ReadOnlySpan<byte> gates)
    {
      for (int i = 0; i < gates.Length; i++, index++)
      {
        if (gates[i] != 0)
        {
          if (!active) { active = true; start = index; }
          lastOpen = index;
        }
        else if (active && index - lastOpen > mergeGap)
        {
          Close();
          active = false;
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

      // padding may bridge into the previous segment: extend it instead of overlapping
      if (transmissions.Count > 0 && s <= transmissions[^1].EndSeconds)
        transmissions[^1] = transmissions[^1] with { EndSeconds = e };
      else
        transmissions.Add(new FmTransmission(s, e));
    }
  }
}
