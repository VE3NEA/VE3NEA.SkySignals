using System;

namespace VE3NEA.SkySSTV
{
  /// <summary>
  /// Streaming FM click (phase-slip) repair on the discriminator output — the declick plan's Phase 3a,
  /// ported from <c>C:\Proj\Try\FmDenoiser\FmDenoiser\ClickDenoiser.cs</c> with its greedy non-maximum
  /// suppression replaced by the §3.2 subtract-rescan loop.
  ///
  /// <para>A threshold click is a full ±2π rotation of the resultant phasor about the origin. The
  /// discriminator output is instantaneous frequency, so the phase step over a window is the integral of
  /// that output (the FM→PM conversion of US 2010/0002807), and the integral is taken over a
  /// <b>detrended</b> output: the SSTV subcarrier alone contributes up to 0.05 cycle per sample, which over
  /// the 7-sample window would be 0.35 cycle against a 0.7-cycle threshold. The baseline is the median of
  /// the guard samples flanking the window — robust to a neighbouring click, and short enough to track a
  /// subcarrier that steps every ~13 samples.</para>
  ///
  /// <para><b>Why here and not at the brightness stage.</b> Everything between this stage and the pixel
  /// integrator is linear, so subtracting a slip here and subtracting its deposit on the brightness
  /// baseband are the same operation — but here the slip is still sharp (±4 kHz of channel, ~6 samples)
  /// while there it is smeared over 320, and 2b measured what that costs a detector: ±4-sample timing at
  /// best, precision ~0.5, and deposits overlapping 6–10 deep below +6 dB CNR. Upstream is also the exact
  /// subtraction rather than the approximate one. See the plan's §7 note.</para>
  ///
  /// <para><b>Repair removes area, not shape.</b> Everything downstream is far narrower in bandwidth than
  /// the event, so only the removed area survives: 0d measured the §3.2 matched template worth 0.1–0.2 dB
  /// PSNR over a plain rectangle and marginally worse on brightness error. This stage therefore subtracts a
  /// rectangle of area ∓1 cycle per detection, spread over the window — differentiating a ±2π phase step
  /// ramped across the window gives exactly that. Sub-sample arrival time is unnecessary for the same
  /// reason, which is why <see cref="SstvClickTemplates"/> is not used here.</para>
  ///
  /// <para><b>Envelope confirmation is optional.</b> Passing <see cref="double.NaN"/> as the ratio skips it,
  /// which is what the audio-input path (<c>SstvDecoder.Decode(double[], …)</c>) must do — it has no IQ,
  /// hence no envelope. The confirmation is a rejection test rather than a precondition, and a loose one:
  /// an encirclement needs the resultant to pass <i>around</i> the origin, not especially close to it, so
  /// requiring a deep fade misses most clicks (FmDenoiser measured recall falling to a third at 0.25).</para>
  ///
  /// <para>Block-in / block-out with bounded state and bounded latency (plan §1.13): emission lags the
  /// newest sample by 2·<see cref="SuppressWidth"/> + <see cref="Margin"/> + <see cref="HalfWidth"/> = 25
  /// samples, 0.52 ms at 48 kHz — negligible against the blanker's 20 ms max-gap bound, which the host
  /// stage already pays.</para>
  /// </summary>
  internal sealed class SstvClickRepair
  {
    /// <summary>Half-width of the click window. The doublet is about fs/BW wide — 6 samples at 48 kHz in a
    /// ±4 kHz video channel — so ±3 captures it.</summary>
    public const int HalfWidth = 3;

    /// <summary>Guard samples on each side of the window, from which the modulation baseline is taken. They
    /// must span much less than a modulation period, which is why the baseline cannot be a long running
    /// median.</summary>
    public const int GuardWidth = 5;

    /// <summary>Samples needed on each side of a candidate center before it can be scored.</summary>
    public const int Margin = HalfWidth + GuardWidth;

    /// <summary>One window's width: the non-maximum exclusion distance, and how far back the rescan
    /// reaches after a subtraction.</summary>
    public const int SuppressWidth = 2 * HalfWidth + 1;

    // subtractions allowed per sample of forward progress. A subtraction removes ~1 cycle from its own
    // center's area, so the loop converges on its own; this only bounds the worst case, and 4 covers a 4π
    // click (two encirclements) with margin.
    private const int MaxRepairsPerAdvance = 4;

    private readonly double fs;
    private readonly double areaThreshold;
    private readonly double envelopeFraction;
    private readonly double[] guard = new double[2 * GuardWidth];

    // the live window, indexed absolutely: buf[i] is stream sample bufBase + i. Compacted rather than
    // ringed — only ~40 samples are ever live, so the shift is rare and the arithmetic stays readable.
    private double[] buf = new double[256];
    private double[] ratio = new double[256];
    private double[] area = new double[256];
    private bool[] areaValid = new bool[256];
    private long bufBase;
    private int bufCount;

    private long nextDecide = Margin;      // next center to score; the head the batch detector skips
    private long highWater = Margin;       // furthest nextDecide has reached, for the rescan budget
    private long commit;                   // next sample to release
    private int budget = MaxRepairsPerAdvance;

    private double[] ready = new double[64];
    private int readyLen;

    /// <summary>Slips subtracted so far.</summary>
    public long RepairCount { get; private set; }

    public SstvClickRepair(double fs, SstvDecodeOptions o)
    {
      this.fs = fs;
      areaThreshold = o.ClickAreaThresholdCycles;
      envelopeFraction = o.ClickEnvelopeFraction;
    }

    /// <summary>Feed one discriminator sample and its envelope ratio (|z| over the tracked mean envelope;
    /// <see cref="double.NaN"/> when no envelope is available); returns the samples this push finalized,
    /// valid until the next call. Usually one, none while a rescan backs up, several as it catches up.
    /// </summary>
    public ReadOnlySpan<double> Push(double disc, double envRatio)
    {
      readyLen = 0;
      Append(disc, envRatio);

      long newest = bufBase + bufCount - 1;
      while (nextDecide + SuppressWidth + Margin <= newest) Step();

      // any future subtraction is centred at nextDecide − SuppressWidth or later (a rescan can back up
      // exactly that far), so it cannot reach below that center's own left edge
      Release(nextDecide - SuppressWidth - HalfWidth);
      return new ReadOnlySpan<double>(ready, 0, readyLen);
    }

    /// <summary>End of stream: release everything still held. The tail's centers never get their right
    /// context, so they go out unexamined — the same truncation the batch detector applies at the end of an
    /// array.</summary>
    public ReadOnlySpan<double> Flush()
    {
      readyLen = 0;
      Release(bufBase + bufCount);
      return new ReadOnlySpan<double>(ready, 0, readyLen);
    }


    // ----------------------------------------------------------------------------------------------------
    //                                        detect and subtract
    // ----------------------------------------------------------------------------------------------------


    /// <summary>Score one candidate center and act on it. A subtraction backs the cursor up by one window
    /// so the neighbourhood is re-scored against the corrected data — this is what replaces the batch
    /// version's greedy non-maximum suppression, and it handles the 4π double encirclement without a
    /// special case: the second slip simply becomes the largest remaining step once the first is gone.
    /// </summary>
    private void Step()
    {
      long center = nextDecide;
      double a = AreaAt(center);

      if (Math.Abs(a) > areaThreshold && IsLargestNearby(center, a) && EnvelopeConfirms(center))
      {
        Subtract(center, a);
        RepairCount++;

        // back up to re-score the neighbourhood, but never far enough to touch a released sample
        if (budget > 0)
        {
          budget--;
          nextDecide = Math.Max(commit + HalfWidth, center - SuppressWidth);
        }
        else
          nextDecide = center + 1;
      }
      else
        nextDecide = center + 1;

      if (nextDecide > highWater) { highWater = nextDecide; budget = MaxRepairsPerAdvance; }
    }

    /// <summary>The non-maximum test: this center must carry the largest step within one window of itself,
    /// ties going to the earlier sample so the choice does not depend on scan direction.</summary>
    private bool IsLargestNearby(long center, double a)
    {
      double mine = Math.Abs(a);
      for (long k = center - SuppressWidth; k <= center + SuppressWidth; k++)
      {
        if (k == center) continue;
        double other = Math.Abs(AreaAt(k));
        if (k < center ? mine <= other : mine < other) return false;
      }
      return true;
    }

    /// <summary>The phase step centred on <paramref name="center"/>, in cycles: the window's integral of the
    /// discriminator output less what the modulation alone would have contributed. Cached per sample and
    /// invalidated by a subtraction, since the non-maximum test asks for 15 of these per center.</summary>
    private double AreaAt(long center)
    {
      if (center < Margin) return 0;
      if (center - Margin < bufBase || center + Margin > bufBase + bufCount - 1) return 0;

      int at = (int)(center - bufBase);
      if (areaValid[at]) return area[at];

      for (int k = 0; k < GuardWidth; k++)
      {
        guard[k] = buf[at - HalfWidth - GuardWidth + k];
        guard[GuardWidth + k] = buf[at + HalfWidth + 1 + k];
      }
      Array.Sort(guard);
      double baseline = 0.5 * (guard[GuardWidth - 1] + guard[GuardWidth]);

      double sum = 0;
      for (int k = at - HalfWidth; k <= at + HalfWidth; k++) sum += (buf[k] - baseline) / fs;

      area[at] = sum;
      areaValid[at] = true;
      return sum;
    }

    /// <summary>The envelope test — a rejection, and skipped outright when there is no envelope to consult
    /// (a NaN ratio, i.e. the audio-input path or a bypassed blanker, whose tracker never ran).</summary>
    private bool EnvelopeConfirms(long center)
    {
      double r = ratio[center - bufBase];
      return double.IsNaN(r) || r < envelopeFraction;
    }

    /// <summary>Remove one cycle of phase, spread evenly over the window: differentiating a ±2π step ramped
    /// across the window gives a rectangle of this height. One cycle per detection — a turn count inferred
    /// from the measured area would be guesswork, because most of a slip's area falls outside the window
    /// (the batch original measured 0.57 cycle in-window for a 4π click against 0.50 for a 2π one).</summary>
    private void Subtract(long center, double a)
    {
      int at = (int)(center - bufBase);
      double step = Math.Sign(a) * fs / (2 * HalfWidth + 1);
      for (int k = at - HalfWidth; k <= at + HalfWidth; k++) buf[k] -= step;

      // every center whose window or guard overlaps the correction has to be re-derived
      int from = Math.Max(0, at - HalfWidth - Margin);
      int to = Math.Min(bufCount - 1, at + HalfWidth + Margin);
      for (int k = from; k <= to; k++) areaValid[k] = false;
    }


    // ----------------------------------------------------------------------------------------------------
    //                                          window plumbing
    // ----------------------------------------------------------------------------------------------------


    private void Append(double disc, double envRatio)
    {
      if (bufCount == buf.Length) Compact();
      buf[bufCount] = disc;
      ratio[bufCount] = envRatio;
      areaValid[bufCount] = false;
      bufCount++;
    }

    /// <summary>Release everything below <paramref name="limit"/> that has not gone out yet.</summary>
    private void Release(long limit)
    {
      while (commit < limit && commit <= bufBase + bufCount - 1)
      {
        if (readyLen == ready.Length) Array.Resize(ref ready, ready.Length * 2);
        ready[readyLen++] = buf[commit - bufBase];
        commit++;
      }
    }

    /// <summary>Drop the samples no longer reachable — those below both the release cursor and the oldest
    /// guard sample any future center can ask for.</summary>
    private void Compact()
    {
      long floor = Math.Min(commit, nextDecide - SuppressWidth - Margin);
      int drop = (int)Math.Min(Math.Max(0, floor - bufBase), bufCount);
      if (drop == 0)
      {
        int size = buf.Length * 2;
        Array.Resize(ref buf, size);
        Array.Resize(ref ratio, size);
        Array.Resize(ref area, size);
        Array.Resize(ref areaValid, size);
        return;
      }

      Array.Copy(buf, drop, buf, 0, bufCount - drop);
      Array.Copy(ratio, drop, ratio, 0, bufCount - drop);
      Array.Copy(area, drop, area, 0, bufCount - drop);
      Array.Copy(areaValid, drop, areaValid, 0, bufCount - drop);
      bufCount -= drop;
      bufBase += drop;
    }
  }
}
