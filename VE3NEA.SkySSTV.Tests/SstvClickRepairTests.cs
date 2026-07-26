using System;
using System.Collections.Generic;
using FluentAssertions;
using MathNet.Numerics;
using Xunit;
using Xunit.Abstractions;

namespace VE3NEA.SkySSTV.Tests
{
  /// <summary>
  /// Declick plan 3a: the disc-domain click repair stage (<see cref="SstvClickRepair"/>), pinned against
  /// slips of known area at known times. The claim under test is 3b's — that only the removed AREA matters,
  /// because everything downstream is far narrower in bandwidth than the event — so these measure area
  /// removed and area left, not waveform resemblance.
  /// </summary>
  public class SstvClickRepairTests
  {
    private const double Fs = 48000.0;

    // the subcarrier the slips sit on: mid-band, at the FM deviation the video chain carries. Its own
    // per-sample excursion is what the guard-median detrend has to remove before the area threshold means
    // anything, so a live subcarrier is the honest background for this test. The deviation is
    // SstvEncoderOptions.DeviationHz — the measured real-satellite value, so the bias these tests report is
    // the bias the real captures carry, not a chosen one.
    private const double SubcarrierHz = 2000.0;
    private const double DeviationHz = 3300.0;

    private readonly ITestOutputHelper output;
    public SstvClickRepairTests(ITestOutputHelper o) => output = o;

    /// <summary>A clean subcarrier must not look like a click. This is the false-alarm floor: the detrended
    /// window integral of pure modulation has to stay under the 0.7-cycle threshold at every position, or
    /// the stage would inject a cycle of error into an undamaged signal.</summary>
    [Fact]
    public void CleanSubcarrier_TriggersNothing()
    {
      var clean = Subcarrier((int)Fs);
      double[] got = Run(clean, new SstvDecodeOptions { ClickRepair = true }, out long repairs);

      got.Length.Should().Be(clean.Length);
      output.WriteLine($"{repairs} detections on {clean.Length} clean samples");
      repairs.Should().Be(0, "modulation alone must never reach the area threshold");
      for (int i = 0; i < clean.Length; i++) got[i].Should().BeApproximately(clean[i], 1e-9);
    }

    /// <summary>The 3a/3b pin, with the subcarrier held flat so the mechanism is tested in isolation: a slip
    /// of known area at a known time, and what is left of it. Removal is by a rectangle, so the repaired
    /// waveform does NOT match the clean one sample by sample — the residual AREA is what survives the video
    /// low-pass, and that is what must go to zero.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(-1)]
    public void KnownSlip_OnAFlatBaseline_HasItsAreaRemoved(int turns)
    {
      int n = 8192, t0 = 4096;
      var clean = Subcarrier(n, 0.0);
      var clicked = Inject(clean, t0, 0.0, turns);

      double before = AreaCycles(clicked, clean, t0, 60);
      double[] got = Run(clicked, new SstvDecodeOptions { ClickRepair = true }, out long repairs);
      double after = AreaCycles(got, clean, t0, 60);

      output.WriteLine($"turns={turns}: {repairs} detections, area {before:F3} -> {after:F3} cycles " +
        $"(peak residual {PeakError(got, clean, t0, 60):F0} Hz)");
      repairs.Should().Be(1, "one slip, one subtraction");
      Math.Abs(before).Should().BeApproximately(1.0, 0.01, "the injected slip carries one cycle");
      Math.Abs(after).Should().BeLessThan(0.05, "and the stage takes that cycle back out");
    }

    /// <summary>
    /// Why the ported detector cannot be used on SSTV as it stands — the 3a finding, measured rather than
    /// argued. The area statistic detrends by the MEDIAN of guard samples ±4…±8 from the window, which
    /// assumes the modulation is slow across that span. FmDenoiser's was: 1 kHz speech, 48 samples per
    /// period. The SSTV subcarrier is 1500–2300 Hz — 21 to 32 samples per period — so the guards straddle
    /// most of a period, their median carries no information about the modulation at the centre, and what
    /// is left in the window is the modulation itself.
    /// <para>Reported here for a clean signal, so every cycle measured is spurious: the statistic's excursion
    /// against the 0.7-cycle threshold, over subcarrier phase and across the video band. This is the same
    /// structural fact 1a hit from the other side — "in the disc domain the modulation, not the noise, sets a
    /// median/rms threshold, because the subcarrier steps every ~13 samples".</para>
    /// </summary>
    [Fact]
    public void SubcarrierAlone_DominatesTheAreaStatistic()
    {
      var o = new SstvDecodeOptions { ClickRepair = true };
      output.WriteLine($"deviation {DeviationHz:0} Hz, threshold {o.ClickAreaThresholdCycles:0.00} cycle");

      double worst = 0;
      foreach (double tone in new[] { SstvTones.Black, 1700.0, SstvTones.Center, 2100.0, SstvTones.White })
      {
        var clean = Subcarrier(8192, DeviationHz, tone);
        double peak = 0;
        for (int c = 40; c < 8152; c++) peak = Math.Max(peak, Math.Abs(AreaStatistic(clean, c)));

        Run(clean, o, out long spurious);
        output.WriteLine($"  {tone:0} Hz ({Fs / tone:0.0} samples/period): peak |area| {peak:F3} cycle " +
          $"= {100 * peak / o.ClickAreaThresholdCycles:0}% of threshold, {spurious} spurious detections");
        worst = Math.Max(worst, peak);
      }

      worst.Should().BeGreaterThan(0.3,
        "the modulation alone must be shown to eat a large share of the detection threshold");
    }

    /// <summary>The consequence, on the case that matters: the same slip that is cleanly removed on a flat
    /// baseline is mishandled on a live subcarrier, and the failure is not a miss but a WRONG SIGN — the
    /// modulation's contribution can outweigh the slip's and reverse the measured step, so the stage adds a
    /// cycle where it meant to remove one. A miss costs what the click already cost; a sign error costs
    /// double. This is what disqualifies the ported detector rather than merely weakening it.</summary>
    [Fact]
    public void OnALiveSubcarrier_TheRepairCanInvertTheSlip()
    {
      int n = 8192;
      var clean = Subcarrier(n);
      var o = new SstvDecodeOptions { ClickRepair = true };

      int inverted = 0, removed = 0, missed = 0;
      // walk the arrival across one subcarrier period, so every phase relationship is covered
      for (int t0 = 4096; t0 < 4096 + 24; t0++)
      {
        var clicked = Inject(clean, t0, 0.0, 1);
        double[] got = Run(clicked, o, out long repairs);
        double after = AreaCycles(got, clean, t0, 60);

        if (repairs == 0) missed++;
        else if (after > 1.5) inverted++;
        else if (Math.Abs(after) < 0.3) removed++;
      }

      output.WriteLine($"over 24 arrival phases: {removed} removed, {missed} missed, {inverted} INVERTED " +
        $"(residual area near +2 cycles)");
      (removed + missed + inverted).Should().Be(24, "every arrival must fall into one of the three");
      inverted.Should().BeGreaterThan(0, "the sign error is real and is the reason this detector is rejected");
    }

    /// <summary>
    /// The 4π double encirclement, and a second limit of the ported design — this one independent of the
    /// subcarrier, since it shows up on a flat baseline. Two slips 4 samples apart merge into one window, and
    /// the stage removes ONE cycle and stops: half the damage.
    /// <para>The rescan is not at fault. The batch original refuses to infer a turn count from the measured
    /// area, on the grounds that a 4π click's in-window area (0.57 cycle) is indistinguishable from a 2π
    /// one's (0.50) — true at its 16 kHz bandwidth, where the window catches only the doublet's core. At
    /// SSTV's ±4 kHz the ±3-sample window catches 87 % of one slip's area, so the merged pair measures ≈1.5
    /// cycles and IS distinguishable: rounding the measured area to the nearest whole cycle would repair both.
    /// That fix is unavailable for the reason the other tests here measure — on a live subcarrier the area is
    /// biased by up to 0.5 cycle, so the rounding would land on the wrong integer.</para>
    /// </summary>
    [Fact]
    public void MergedDoubleEncirclement_LosesOnlyOneOfItsTwoCycles()
    {
      int n = 8192, t0 = 4096;
      var clean = Subcarrier(n, 0.0);
      var clicked = Inject(Inject(clean, t0, 0.0, 1), t0 + 4, 0.0, 1);

      double before = AreaCycles(clicked, clean, t0 + 2, 60);
      double[] got = Run(clicked, new SstvDecodeOptions { ClickRepair = true }, out long repairs);
      double after = AreaCycles(got, clean, t0 + 2, 60);

      output.WriteLine($"{repairs} detections, area {before:F3} -> {after:F3} cycles " +
        $"(merged window measures {AreaStatistic(clicked, t0 + 2):F3} cycles)");
      before.Should().BeApproximately(2.0, 0.02, "two encirclements, two cycles");
      repairs.Should().Be(1, "the merged pair reads as one over-threshold step");
      after.Should().BeApproximately(1.0, 0.05, "so one whole cycle of the pair survives");
    }

    /// <summary>Opposite polarities cancel in area but not in damage, and must be repaired separately —
    /// a check that the detector keys on the local step and not on some running total.</summary>
    [Fact]
    public void OpposedSlips_AreBothRemoved()
    {
      int n = 8192;
      var clean = Subcarrier(n, 0.0);
      var clicked = Inject(Inject(clean, 3000, 0.0, 1), 5000, 0.0, -1);

      double[] got = Run(clicked, new SstvDecodeOptions { ClickRepair = true }, out long repairs);

      double a = AreaCycles(got, clean, 3000, 60), b = AreaCycles(got, clean, 5000, 60);
      output.WriteLine($"{repairs} detections, residual areas {a:F3} and {b:F3} cycles");
      repairs.Should().Be(2);
      Math.Abs(a).Should().BeLessThan(0.05);
      Math.Abs(b).Should().BeLessThan(0.05);
    }

    /// <summary>The envelope test is a rejection, and it is optional: with no envelope to consult (the
    /// audio-input path, plan 2f) the stage must still repair, and with an envelope that says "no fade here"
    /// it must decline. Both directions matter — the first is what makes the stage reachable from
    /// <c>Decode(double[] disc, …)</c>, the second is what holds its false-alarm rate down.</summary>
    [Fact]
    public void EnvelopeConfirmation_IsOptionalAndRejects()
    {
      int n = 8192, t0 = 4096;
      var clean = Subcarrier(n, 0.0);
      var clicked = Inject(clean, t0, 0.0, 1);
      var o = new SstvDecodeOptions { ClickRepair = true };

      Run(clicked, o, out long noEnvelope);                       // NaN ratio: test skipped
      Run(clicked, o, out long faded, 0.3);                       // deep fade: confirmed
      Run(clicked, o, out long strong, 1.0);                      // no fade: rejected

      output.WriteLine($"detections — no envelope {noEnvelope}, ratio 0.3 {faded}, ratio 1.0 {strong}");
      noEnvelope.Should().Be(1, "no envelope must mean no rejection, not no repair");
      faded.Should().Be(1);
      strong.Should().Be(0, "a slip with no fade under it is rejected by the 0.75 confirmation");
    }

    /// <summary>Threshold behaviour, from the one direction that is safe to assert: a slip well under the
    /// threshold must be left alone. The stage removes a whole cycle per detection, so acting on a partial
    /// step injects more error than it removes — this is the guard the 0.7 default exists for.</summary>
    [Fact]
    public void PartialStep_IsLeftAlone()
    {
      int n = 8192, t0 = 4096;
      var clean = Subcarrier(n, 0.0);
      var clicked = Inject(clean, t0, 0.0, 1, 0.3);               // 0.3 cycle: not a slip

      double[] got = Run(clicked, new SstvDecodeOptions { ClickRepair = true }, out long repairs);

      output.WriteLine($"{repairs} detections on a 0.3-cycle step");
      repairs.Should().Be(0);
      for (int i = 0; i < n; i++) got[i].Should().BeApproximately(clicked[i], 1e-9);
    }


    // ----------------------------------------------------------------------------------------------------
    //                                            fixtures
    // ----------------------------------------------------------------------------------------------------


    /// <summary>Push a disc stream through the stage in ragged blocks (so the test also exercises the
    /// held-back window) and collect everything it gives back.</summary>
    private static double[] Run(double[] disc, SstvDecodeOptions o, out long repairs,
      double envRatio = double.NaN)
    {
      var stage = new SstvClickRepair(Fs, o);
      var got = new List<double>(disc.Length);
      var rng = new Random(4);

      int at = 0;
      while (at < disc.Length)
      {
        int len = Math.Min(1 + rng.Next(500), disc.Length - at);
        for (int i = 0; i < len; i++)
          foreach (double v in stage.Push(disc[at + i], envRatio)) got.Add(v);
        at += len;
      }
      foreach (double v in stage.Flush()) got.Add(v);

      repairs = stage.RepairCount;
      got.Count.Should().Be(disc.Length, "the stage delays samples, it does not add or drop them");
      return got.ToArray();
    }

    /// <summary>The video subcarrier as the discriminator delivers it: instantaneous frequency in Hz,
    /// swinging by the FM deviation at the subcarrier rate. A zero deviation gives the flat baseline the
    /// mechanism tests use.</summary>
    private static double[] Subcarrier(int n, double deviation = DeviationHz, double tone = SubcarrierHz)
    {
      var disc = new double[n];
      for (int i = 0; i < n; i++) disc[i] = deviation * Math.Cos(2 * Math.PI * tone * i / Fs);
      return disc;
    }

    /// <summary>The stage's own detection statistic, recomputed here so the probe can read it directly: the
    /// window's integral of the disc output less the guard median, in cycles.</summary>
    private static double AreaStatistic(double[] disc, int center)
    {
      int hw = SstvClickRepair.HalfWidth, gw = SstvClickRepair.GuardWidth;
      var guard = new double[2 * gw];
      for (int k = 0; k < gw; k++)
      {
        guard[k] = disc[center - hw - gw + k];
        guard[gw + k] = disc[center + hw + 1 + k];
      }
      Array.Sort(guard);
      double baseline = 0.5 * (guard[gw - 1] + guard[gw]);

      double sum = 0;
      for (int k = center - hw; k <= center + hw; k++) sum += (disc[k] - baseline) / Fs;
      return sum;
    }

    /// <summary>Add a phase slip of <paramref name="turns"/> cycles (scaled by
    /// <paramref name="scale"/>) at <paramref name="t0"/> + <paramref name="frac"/>, shaped as the video
    /// channel filter's impulse response — the shape a slip actually has at the discriminator output.
    /// </summary>
    private static double[] Inject(double[] disc, int t0, double frac, int turns, double scale = 1.0)
    {
      var o = new SstvDecodeOptions();
      var chan = new SstvClickTemplates(o.VideoChannelBwHz / Fs,
        SstvDecoder.KernelTaps(o.VideoChannelBwHz, Fs));
      var row = chan.Row(chan.NearestStep(frac));

      var result = (double[])disc.Clone();
      for (int i = 0; i < row.Length; i++)
      {
        int at = t0 - chan.Center + i;
        if ((uint)at < result.Length) result[at] += turns * scale * Fs * row[i];
      }
      return result;
    }

    /// <summary>Area of the difference between two disc streams over a window, in cycles — the statistic
    /// that survives the video low-pass, and therefore the only one the repair is judged on.</summary>
    private static double AreaCycles(double[] got, double[] clean, int center, int half)
    {
      double sum = 0;
      for (int i = Math.Max(0, center - half); i <= Math.Min(clean.Length - 1, center + half); i++)
        sum += (got[i] - clean[i]) / Fs;
      return sum;
    }

    /// <summary>Largest pointwise error left in the window, Hz — reported, not asserted: a rectangle
    /// standing in for a doublet leaves plenty of it, and that is the design, not a defect.</summary>
    private static double PeakError(double[] got, double[] clean, int center, int half)
    {
      double peak = 0;
      for (int i = Math.Max(0, center - half); i <= Math.Min(clean.Length - 1, center + half); i++)
        peak = Math.Max(peak, Math.Abs(got[i] - clean[i]));
      return peak;
    }
  }
}
