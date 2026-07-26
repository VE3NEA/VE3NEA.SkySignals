using System;
using System.Collections.Generic;
using MathNet.Numerics;

namespace VE3NEA.SkySSTV.Tests
{
  /// <summary>
  /// Ground-truth FM click detection, available only in simulation — the declick plan §5 step 0c port of
  /// <c>C:\Proj\Try\FmDenoiser\FmDenoiser\ClickOracle.cs</c>, on <c>double[]</c> discriminator output and
  /// with the area-repair arm the plan's ceiling table needs.
  ///
  /// <para>Referred to the clean signal, the received phasor is <c>r/s = 1 + n/s</c>, and a threshold click
  /// is by definition an encirclement of the origin by that phasor. An encirclement is exactly a ±2π step in
  /// the unwrapped phase error <c>arg(r·conj(s))</c>, so counting the wraps of that error counts the clicks —
  /// no thresholds, no detector, no misses.</para>
  ///
  /// <para>Both phasors must be taken AFTER the receiver's channel filter (<see cref="ChannelFilter"/>): the
  /// discriminator sees the filtered signal, and it is encirclements of THAT phasor which become clicks in
  /// its output. The filter is the decoder's own kernel and 'same'-aligned, so click index i lands on
  /// <c>disc[i]</c> — matching <see cref="SstvDecoder.Discriminator"/>, whose streaming FIR drops the same
  /// group delay. The lone exception is its <c>disc[0] = disc[1]</c> edge case, one sample at the very
  /// start.</para>
  ///
  /// <para>This is not a receiver algorithm and never becomes one: it exists to measure the CEILING of click
  /// repair (perfect detection, perfect polarity, perfect turn count) so a blind detector can be judged
  /// against what was achievable.</para>
  /// </summary>
  internal static class SstvClickOracle
  {
    /// <summary>A true click: the sample at which the phase error wrapped, and how many turns it took —
    /// ±1 for the ordinary 2π click, ±2 for the 4π double encirclement.</summary>
    public readonly record struct OracleClick(int Index, int Turns);

    /// <summary>Wraps closer together than this are one multi-turn click (0.5 ms at 48 kHz).</summary>
    public const int GroupGapSamples = 24;

    /// <summary>Half-width of the repair window, samples. The doublet is about fs/BW wide — 6 samples at
    /// 48 kHz in a ±4 kHz channel — so ±3 captures it.</summary>
    public const int RepairHalfWidth = 3;

    /// <summary>Samples just outside the window used to estimate the modulation the window would have
    /// carried had there been no click. Must span much less than a modulation period.</summary>
    public const int GuardWidth = 5;


    /// <summary>The decoder's Stage-1 channel filter, applied to a whole array — the same kernel and the
    /// same 'same' alignment <see cref="SstvDecoder.Discriminator"/> uses, so the filtered clean/noisy pair
    /// is index-aligned with the discriminator output.</summary>
    public static Complex32[] ChannelFilter(Complex32[] iq, double bwHz, double fs)
    {
      double fc = bwHz / fs;
      if (bwHz <= 0 || fc >= 0.5) return iq;                 // the decoder's bypass case
      float[] h = global::VE3NEA.Dsp.BlackmanSincKernel(fc, SstvDecoder.KernelTaps(bwHz, fs));
      return global::VE3NEA.LiquidFir.ConvolveSame(iq, h);
    }

    /// <summary>Every origin encirclement in the received signal, exactly.</summary>
    public static List<OracleClick> Detect(Complex32[] clean, Complex32[] noisy)
    {
      var result = new List<OracleClick>();
      int n = Math.Min(clean.Length, noisy.Length);
      if (n == 0) return result;

      double previousError = PhaseError(clean[0], noisy[0]);
      double unwrapped = previousError;
      int wraps = 0;

      for (int i = 1; i < n; i++)
      {
        double error = PhaseError(clean[i], noisy[i]);

        // the phasor moves by well under half a turn between samples, so the principal-value difference is
        // the true one and the running sum is the unwrapped phase error
        double delta = error - previousError;
        if (delta > Math.PI) delta -= 2 * Math.PI;
        else if (delta < -Math.PI) delta += 2 * Math.PI;
        unwrapped += delta;
        previousError = error;

        // how many whole turns the unwrapped error is away from its principal value: this integer changes
        // exactly when the phasor passes around the origin
        int turns = (int)Math.Round((unwrapped - error) / (2 * Math.PI));
        if (turns != wraps)
        {
          result.Add(new OracleClick(i, turns - wraps));
          wraps = turns;
        }
      }
      return result;
    }

    /// <summary>
    /// Merges wraps that belong to one event into a single multi-turn click. The unwrapped phase moves by
    /// less than half a turn per sample, so a 4π encirclement cannot appear as one ±2 wrap — it arrives as
    /// two same-direction wraps a few samples apart, and only grouping reveals it.
    ///
    /// <para>For <b>statistics only</b>. Repair must use the ungrouped wraps: the turns of a 4π click happen
    /// at different instants, and removing both at the first one's position misplaces half the correction
    /// (FmDenoiser measured 6 dB worse at −5 dB CNR).</para>
    /// </summary>
    public static List<OracleClick> Group(IReadOnlyList<OracleClick> clicks, int gapSamples = GroupGapSamples)
    {
      var result = new List<OracleClick>();
      int i = 0;

      while (i < clicks.Count)
      {
        int turns = clicks[i].Turns;
        int index = clicks[i].Index;
        int j = i + 1;

        // same direction and close by: the same encirclement event continuing
        while (j < clicks.Count && clicks[j].Index - clicks[j - 1].Index <= gapSamples
               && Math.Sign(clicks[j].Turns) == Math.Sign(turns))
        {
          turns += clicks[j].Turns;
          j++;
        }

        result.Add(new OracleClick(index, turns));
        i = j;
      }
      return result;
    }

    /// <summary>arg(noisy · conj(clean)) — the received phase referred to the transmitted phase.</summary>
    private static double PhaseError(Complex32 clean, Complex32 noisy)
    {
      double re = (double)noisy.Real * clean.Real + (double)noisy.Imaginary * clean.Imaginary;
      double im = (double)noisy.Imaginary * clean.Real - (double)noisy.Real * clean.Imaginary;
      return Math.Atan2(im, re);
    }




    // ----------------------------------------------------------------------------------------------------
    //                                              repair
    // ----------------------------------------------------------------------------------------------------


    /// <summary>
    /// Removes each click's whole nominal phase step — one cycle per turn, so a 4π click loses 4π — spread
    /// over the repair window. The ceiling for slip repair on a RAW discriminator output: perfect detection,
    /// perfect polarity, perfect turn count, the only remaining error being the shape mismatch between a
    /// rectangular correction and the click.
    /// </summary>
    public static double[] RepairSteps(double[] freqHz, IReadOnlyList<OracleClick> clicks, double fs,
      int halfWidth = RepairHalfWidth)
    {
      var result = (double[])freqHz.Clone();
      int width = 2 * halfWidth + 1;

      foreach (var click in clicks)
      {
        double step = click.Turns * fs / width;
        int start = Math.Max(0, click.Index - halfWidth);
        int end = Math.Min(result.Length - 1, click.Index + halfWidth);
        for (int k = start; k <= end; k++) result[k] -= step;
      }
      return result;
    }

    /// <summary>
    /// Removes the area actually present at each known click position, rather than a nominal ±2π: the window
    /// is replaced by the modulation its guard neighbours imply. This is the ceiling for the case where
    /// DETECTION is given but estimation is not — and it is deliberately weak, because the ±3 window holds
    /// only about a third of a click's step (see <see cref="ResidualAreaCycles"/>); the rest of the doublet
    /// lies in tails too long to separate from the subcarrier. Kept as the lower bound of the ceiling table.
    /// </summary>
    public static double[] RepairArea(double[] freqHz, IReadOnlyList<OracleClick> clicks,
      int halfWidth = RepairHalfWidth, int guard = GuardWidth)
    {
      var result = (double[])freqHz.Clone();

      foreach (var click in clicks)
      {
        int start = click.Index - halfWidth, end = click.Index + halfWidth;
        if (start - guard < 0 || end + guard >= freqHz.Length) continue;

        double baseline = GuardMedian(freqHz, click.Index, halfWidth, guard);
        for (int k = start; k <= end; k++) result[k] = baseline;
      }
      return result;
    }

    /// <summary>
    /// The SM5BSZ repair (plan §3.2 items 1–2): subtract the click's KNOWN SHAPE rather than a rectangle of
    /// the same area. The event is far shorter than the channel filter that shapes it, so what the
    /// discriminator sees is that filter's own impulse response — computable, not empirical.
    ///
    /// <para>Both corrections remove the same total ±1 cycle per turn, so a downstream filter far narrower
    /// than the event cannot tell them apart. They differ in what they leave BEHIND locally: the rectangle
    /// concentrates the whole cycle in 7 samples while the true event spreads it over the filter's response,
    /// leaving a ±0.7-cycle dipole a few samples wide. Whether the brightness low-pass is narrow enough to
    /// swallow that is exactly what the ceiling table measures.</para>
    /// </summary>
    /// <param name="template">Normalized click shape, <see cref="ClickTemplate"/>: sums to
    /// <paramref name="fs"/>, i.e. carries unit area in cycles.</param>
    public static double[] RepairMatched(double[] freqHz, IReadOnlyList<OracleClick> clicks,
      double[] template)
    {
      var result = (double[])freqHz.Clone();
      int half = template.Length / 2;

      foreach (var click in clicks)
      {
        int from = Math.Max(0, click.Index - half);
        int to = Math.Min(result.Length - 1, click.Index + half);
        for (int k = from; k <= to; k++)
          result[k] -= click.Turns * template[k - click.Index + half];
      }
      return result;
    }

    /// <summary>The channel filter's impulse response, scaled to unit area in cycles — the shape a phase
    /// slip deposits on the discriminator output. The kernel has unit DC gain, so scaling by
    /// <paramref name="fs"/> makes <c>Σ template / fs = 1</c> cycle.</summary>
    public static double[] ClickTemplate(double bwHz, double fs)
    {
      float[] h = global::VE3NEA.Dsp.BlackmanSincKernel(bwHz / fs, SstvDecoder.KernelTaps(bwHz, fs));
      double sum = 0;
      foreach (float v in h) sum += v;

      var template = new double[h.Length];
      for (int i = 0; i < h.Length; i++) template[i] = h[i] / sum * fs;
      return template;
    }

    /// <summary>Re-applies the production blanker's decisions to a repaired stream. The blanker gates on the
    /// IQ ENVELOPE, which disc-domain repair cannot change, so its mask is identical either way — and the
    /// mask is recoverable exactly, as the samples where the production blanked output differs from the raw
    /// one. This is how a repair arm is stacked on top of today's chain without duplicating the blanker.
    /// </summary>
    public static double[] ApplyBlankerMask(double[] repaired, double[] raw, double[] blanked)
    {
      var result = (double[])repaired.Clone();
      int i = 0;

      while (i < raw.Length)
      {
        if (raw[i] == blanked[i]) { i++; continue; }

        int end = i;
        while (end + 1 < raw.Length && raw[end + 1] != blanked[end + 1]) end++;
        int length = end - i + 1;

        // the same linear interpolation across the run, on the repaired samples
        double left = i > 0 ? result[i - 1] : (end + 1 < result.Length ? result[end + 1] : 0);
        double right = end + 1 < result.Length ? result[end + 1] : left;
        for (int j = 0; j < length; j++)
          result[i + j] = left + (right - left) * (j + 1) / (length + 1);
        i = end + 1;
      }
      return result;
    }

    /// <summary>The residual phase-step area at the known click positions, in cycles per click — the §4
    /// statistic that replaces the amplitude proxy <c>|disc| &gt; 15000</c>.
    ///
    /// <para><b>Read it as a relative indicator, not as "cycles remaining".</b> An untouched click measures
    /// ≈0.33 cycle, not 1.0: only the doublet's core falls inside the ±3 window, and the window cannot be
    /// widened because the guard baseline must track a 1500–2300 Hz subcarrier whose own period is 25
    /// samples. A full-cycle correction therefore drives this statistic to ≈−0.67 (reported as 0.67), which
    /// is over-subtraction WITHIN the window and not an error in the total. The honest whole-event measure
    /// is the brightness-domain error against the noise-free decode, which simulation can compute
    /// directly.</para></summary>
    public static double ResidualAreaCycles(double[] freqHz, IReadOnlyList<OracleClick> clicks, double fs,
      int halfWidth = RepairHalfWidth, int guard = GuardWidth)
    {
      double sum = 0;
      int count = 0;

      foreach (var click in clicks)
      {
        int start = click.Index - halfWidth, end = click.Index + halfWidth;
        if (start - guard < 0 || end + guard >= freqHz.Length) continue;

        double baseline = GuardMedian(freqHz, click.Index, halfWidth, guard);
        double area = 0;
        for (int k = start; k <= end; k++) area += (freqHz[k] - baseline) / fs;
        sum += Math.Abs(area);
        count++;
      }
      return count == 0 ? 0 : sum / count;
    }

    /// <summary>Median of the guard samples flanking the click window: the modulation the window would have
    /// carried. Robust to a neighbouring click, and short enough to track the audio-band modulation.</summary>
    private static double GuardMedian(double[] freqHz, int center, int halfWidth, int guard)
    {
      var flank = new double[2 * guard];
      for (int k = 0; k < guard; k++)
      {
        flank[k] = freqHz[center - halfWidth - guard + k];
        flank[guard + k] = freqHz[center + halfWidth + 1 + k];
      }
      Array.Sort(flank);
      return 0.5 * (flank[guard - 1] + flank[guard]);
    }
  }
}
