using System;
using System.Numerics;
using FluentAssertions;
using MathNet.Numerics;
using Xunit;
using Xunit.Abstractions;

namespace VE3NEA.SkySSTV.Tests
{
  /// <summary>
  /// Declick plan 2a: the brightness branch's click-template table (<see cref="SstvClickTemplates"/>).
  /// Two of these are structural — unit area, and the identity of row 0 with the branch's own kernel — and
  /// the third is the plan's synthetic click-injection pin: a slip of known time and area pushed through
  /// the production mixer and low-pass, its deposit fitted against the template. That pin is what licenses
  /// Phase 2c to subtract a computed shape instead of an empirical one.
  /// </summary>
  public class SstvClickTemplateTests
  {
    private const double Fs = 48000.0;
    private readonly ITestOutputHelper output;
    public SstvClickTemplateTests(ITestOutputHelper o) => output = o;

    private static int NarrowTaps => SstvDecoder.KernelTaps(600.0, Fs);

    [Fact]
    public void Rows_CarryUnitArea_AndRowZeroIsTheBranchKernel()
    {
      var tpl = new SstvClickTemplates(600.0 / Fs, NarrowTaps);
      tpl.Length.Should().Be(NarrowTaps);
      tpl.Center.Should().Be((NarrowTaps - 1) / 2);

      double worstArea = 0;
      for (int k = 0; k <= tpl.SubSteps; k++)
      {
        double sum = 0;
        foreach (float v in tpl.Row(k)) sum += v;
        worstArea = Math.Max(worstArea, Math.Abs(sum - 1.0));
      }

      // row 0 must be the very kernel the branch's FIR runs, or the template models a different filter
      float[] kernel = global::VE3NEA.Dsp.BlackmanSincKernel(600.0 / Fs, NarrowTaps);
      var row0 = tpl.Row(0);
      double worstTap = 0;
      for (int i = 0; i < kernel.Length; i++) worstTap = Math.Max(worstTap, Math.Abs(row0[i] - kernel[i]));

      output.WriteLine($"taps={tpl.Length} center={tpl.Center} substeps={tpl.SubSteps}");
      output.WriteLine($"worst |area-1| = {worstArea:E2}; worst row0-vs-kernel tap = {worstTap:E2} " +
        $"(peak tap {kernel[tpl.Center]:E3})");
      worstArea.Should().BeLessThan(1e-6, "every row carries unit area by construction");
      worstTap.Should().BeLessThan(1e-6, "row 0 is the branch kernel, recomputed in double precision");
    }

    /// <summary>The rows must actually shift: a fractional-delay design that got the sign or the scale of
    /// the delay wrong would still pass the area test and would still look like a plausible pulse.</summary>
    [Fact]
    public void RowCentroid_TracksTheFractionalDelay()
    {
      var tpl = new SstvClickTemplates(600.0 / Fs, NarrowTaps);
      double zero = Centroid(tpl.Row(0));

      double worst = 0;
      for (int k = 0; k <= tpl.SubSteps; k++)
      {
        double want = (double)k / tpl.SubSteps;
        double got = Centroid(tpl.Row(k)) - zero;
        output.WriteLine($"step {k}/{tpl.SubSteps}: delay {want:F4} -> centroid shift {got:F6}");
        worst = Math.Max(worst, Math.Abs(got - want));
      }

      zero.Should().BeApproximately(tpl.Center, 1e-6, "row 0 is symmetric about the center tap");
      worst.Should().BeLessThan(0.002, "the centroid is the delay, to well inside the 1/16-sample step");
    }

    /// <summary>The 2a pin. A slip of known area at a known (fractional) time, injected in the disc domain
    /// as the channel filter's response, then carried through the production mix + brightness low-pass:
    /// the deposit it leaves on the baseband pair must be the template, scaled by one complex number whose
    /// magnitude is the area and whose phase is the mixer's rotation at the arrival time.
    /// <para>Mixer and FIR are both linear, so the deposit is exact — the difference of the clicked and
    /// clean baseband is the click's own image, with the subcarrier subtracted out identically. Any residue
    /// beyond the fitted scalar is the model's error: the slip's finite width against the low-pass.</para>
    /// </summary>
    [Theory]
    [InlineData(0.0, 1)]
    [InlineData(0.5, 1)]
    [InlineData(0.25, -1)]
    [InlineData(0.875, -2)]
    public void ClickDeposit_MatchesTheTemplate_AtKnownTimeAndArea(double frac, int turns)
    {
      var o = new SstvDecodeOptions();
      int n = 4096, t0 = 2048;

      // a live 2000 Hz video subcarrier under the click, so the test exercises what the stage carries
      var clean = new double[n];
      for (int i = 0; i < n; i++) clean[i] = 4000 * Math.Cos(2 * Math.PI * 2000 * i / Fs);

      // the slip as it reaches the disc output: the VIDEO channel filter's response with area 'turns'
      // cycles (rows carry unit area, so scaling by fs makes Σ/fs = 1 cycle), arriving at t0 + frac
      var chan = new SstvClickTemplates(o.VideoChannelBwHz / Fs,
        SstvDecoder.KernelTaps(o.VideoChannelBwHz, Fs));
      var chanRow = chan.Row(chan.NearestStep(frac));
      var clicked = (double[])clean.Clone();
      for (int i = 0; i < chanRow.Length; i++)
        clicked[t0 - chan.Center + i] += turns * Fs * chanRow[i];

      var bbClean = SstvClickOracle.BrightnessBaseband(clean, Fs, o.BrightnessBwHz, NarrowTaps);
      var bbClicked = SstvClickOracle.BrightnessBaseband(clicked, Fs, o.BrightnessBwHz, NarrowTaps);

      // fit the template's one free complex scalar over the row's support, then measure what is left
      var tpl = new SstvClickTemplates(o.BrightnessBwHz / Fs, NarrowTaps);
      var tplRow = tpl.Row(tpl.NearestStep(frac));
      Complex num = Complex.Zero;
      double den = 0, energy = 0;
      for (int i = 0; i < tplRow.Length; i++)
      {
        int at = t0 - tpl.Center + i;
        var d = new Complex(bbClicked[at].Real - bbClean[at].Real,
          bbClicked[at].Imaginary - bbClean[at].Imaginary);
        num += d * tplRow[i];
        den += (double)tplRow[i] * tplRow[i];
        energy += d.Real * d.Real + d.Imaginary * d.Imaginary;
      }
      Complex fit = num / den;

      double residual = 0;
      for (int i = 0; i < tplRow.Length; i++)
      {
        int at = t0 - tpl.Center + i;
        var d = new Complex(bbClicked[at].Real - bbClean[at].Real,
          bbClicked[at].Imaginary - bbClean[at].Imaginary);
        var r = d - fit * tplRow[i];
        residual += r.Real * r.Real + r.Imaginary * r.Imaginary;
      }

      // the mixer's own rotation at the arrival time is the whole of the expected phase
      double w = 2 * Math.PI * SstvTones.Center / Fs;
      double wantPhase = -w * (t0 + frac);
      double gotPhase = fit.Phase - (turns < 0 ? Math.PI : 0);   // the sign of the area is a π turn
      double phaseErr = Math.IEEERemainder(gotPhase - wantPhase, 2 * Math.PI);
      double areaCycles = fit.Magnitude / Fs * Math.Sign(turns);

      output.WriteLine($"frac={frac} turns={turns}: fitted area {areaCycles:F5} cycles " +
        $"({fit.Magnitude / (Math.Abs(turns) * Fs):F5}·fs per turn), phase error {phaseErr:E2} rad, " +
        $"residual {residual / energy:E2} of deposit energy");

      Math.Abs(areaCycles).Should().BeApproximately(Math.Abs(turns), 0.01,
        "the fitted amplitude is the slip's area in cycles");
      Math.Sign(areaCycles).Should().Be(Math.Sign(turns), "polarity must survive the mixer");
      Math.Abs(phaseErr).Should().BeLessThan(0.01, "the deposit's phase is the mixer's rotation at arrival");
      (residual / energy).Should().BeLessThan(1e-4,
        "the template accounts for the deposit; the residue is only the slip's finite width");
    }

    /// <summary>The claim that lets one detection be shared between the branches: everything that scales or
    /// rotates the deposit — the slip's area, the video channel filter's gain, the mixer's phase — happens
    /// UPSTREAM of the branch split, so only the shape differs and each branch has its own table for that.
    /// <para>The phase is shared exactly (6e-4 rad). The fitted MAGNITUDES differ by 0.9 %, and that residue
    /// is the slip's own finite width: the deposit is the branch kernel convolved with the ±4 kHz channel's
    /// ~±6-sample response, and projecting that slightly-too-broad pulse onto the unbroadened template loses
    /// more of it the narrower the kernel is in TIME — 0.12 % for the 600 Hz branch's ±40 samples, 0.99 % for
    /// the 1200 Hz branch's ±20. Area is conserved either way; it is the least-squares projection that
    /// shrinks. 0.9 % of a deposit left behind is −41 dB, so sharing stands.</para></summary>
    [Fact]
    public void BothBranches_ShareOneComplexAmplitude()
    {
      var o = new SstvDecodeOptions();
      int n = 4096, t0 = 2048;
      double frac = 0.375;

      var clean = new double[n];
      for (int i = 0; i < n; i++) clean[i] = 4000 * Math.Cos(2 * Math.PI * 2000 * i / Fs);
      var chan = new SstvClickTemplates(o.VideoChannelBwHz / Fs,
        SstvDecoder.KernelTaps(o.VideoChannelBwHz, Fs));
      var chanRow = chan.Row(chan.NearestStep(frac));
      var clicked = (double[])clean.Clone();
      for (int i = 0; i < chanRow.Length; i++)
        clicked[t0 - chan.Center + i] += Fs * chanRow[i];

      Complex narrowFit = FitAmplitude(clicked, clean, o.BrightnessBwHz, frac, t0);
      Complex wideFit = FitAmplitude(clicked, clean, o.BrightnessWideBwHz, frac, t0);

      output.WriteLine($"narrow ({o.BrightnessBwHz} Hz): {narrowFit.Magnitude / Fs:F5}·fs " +
        $"@{narrowFit.Phase:F5} rad");
      output.WriteLine($"wide   ({o.BrightnessWideBwHz} Hz): {wideFit.Magnitude / Fs:F5}·fs " +
        $"@{wideFit.Phase:F5} rad");
      (wideFit.Magnitude / narrowFit.Magnitude).Should().BeApproximately(1.0, 0.015,
        "the amplitude is set upstream of the split, to within each branch's projection loss");
      Math.Abs(Math.IEEERemainder(wideFit.Phase - narrowFit.Phase, 2 * Math.PI)).Should()
        .BeLessThan(2e-3, "the phase is shared outright — nothing downstream of the mixer rotates it");
    }

    /// <summary>
    /// Where the subtraction belongs. Everything between the discriminator and <c>InstFreq</c> is LINEAR —
    /// a time-varying multiply and an FIR — so subtracting a slip's template from the disc stream and
    /// subtracting its deposit from the baseband pair are the SAME operation, and the choice of stage is a
    /// choice of detector, not of repair.
    /// <para>They are not quite equally good, and the direction of the inequality is the point: the
    /// disc-domain subtraction is EXACT (the click's shape there is the channel filter's response, which is
    /// what was added), while the brightness-domain one carries the ~0.1 % width error 2a measured, because
    /// the deposit is only approximately the branch kernel. Upstream is exact and downstream is approximate,
    /// so there is nothing to gain by moving the subtraction later — only the detector's view changes, and
    /// <c>SstvDeclickProbe.BrightnessDetectorRoc</c> measured that view to be the worse one.</para>
    /// </summary>
    [Fact]
    public void DiscDomainSubtraction_EqualsBrightnessDomainSubtraction_AndIsExact()
    {
      var o = new SstvDecodeOptions();
      int n = 4096, t0 = 2048;
      double frac = 0.25;

      var clean = new double[n];
      for (int i = 0; i < n; i++) clean[i] = 4000 * Math.Cos(2 * Math.PI * 2000 * i / Fs);

      var chan = new SstvClickTemplates(o.VideoChannelBwHz / Fs,
        SstvDecoder.KernelTaps(o.VideoChannelBwHz, Fs));
      var chanRow = chan.Row(chan.NearestStep(frac));
      var clicked = (double[])clean.Clone();
      for (int i = 0; i < chanRow.Length; i++)
        clicked[t0 - chan.Center + i] += Fs * chanRow[i];

      // arm A — subtract the slip in the DISC domain, then run the stage
      var discRepaired = (double[])clicked.Clone();
      for (int i = 0; i < chanRow.Length; i++)
        discRepaired[t0 - chan.Center + i] -= Fs * chanRow[i];
      var bbA = SstvClickOracle.BrightnessBaseband(discRepaired, Fs, o.BrightnessBwHz, NarrowTaps);

      // arm B — run the stage, then subtract the modelled deposit on the baseband pair
      var bbB = SstvClickOracle.BrightnessBaseband(clicked, Fs, o.BrightnessBwHz, NarrowTaps);
      var tpl = new SstvClickTemplates(o.BrightnessBwHz / Fs, NarrowTaps);
      var tplRow = tpl.Row(tpl.NearestStep(frac));
      double w = 2 * Math.PI * SstvTones.Center / Fs;
      var rot = Complex.FromPolarCoordinates(Fs, -w * (t0 + frac));
      for (int i = 0; i < tplRow.Length; i++)
      {
        int at = t0 - tpl.Center + i;
        var d = rot * tplRow[i];
        bbB[at] = new Complex32((float)(bbB[at].Real - d.Real), (float)(bbB[at].Imaginary - d.Imaginary));
      }

      // the truth both arms are trying to reach
      var bbTruth = SstvClickOracle.BrightnessBaseband(clean, Fs, o.BrightnessBwHz, NarrowTaps);

      double errA = 0, errB = 0, gap = 0, signal = 0;
      for (int i = t0 - tpl.Center; i < t0 + tpl.Center; i++)
      {
        errA += Norm(bbA[i], bbTruth[i]);
        errB += Norm(bbB[i], bbTruth[i]);
        gap += Norm(bbA[i], bbB[i]);
        signal += (double)bbTruth[i].Real * bbTruth[i].Real +
          (double)bbTruth[i].Imaginary * bbTruth[i].Imaginary;
      }

      output.WriteLine($"residual vs truth: disc-domain {Math.Sqrt(errA / signal):E2}, " +
        $"brightness-domain {Math.Sqrt(errB / signal):E2}; arm-to-arm gap {Math.Sqrt(gap / signal):E2}");
      Math.Sqrt(errA / signal).Should().BeLessThan(1e-6, "the disc-domain subtraction is exact");
      Math.Sqrt(errB / signal).Should().BeLessThan(0.02,
        "the brightness-domain one is as good as the template model, and no better");
      Math.Sqrt(errB / signal).Should().BeGreaterThan(Math.Sqrt(errA / signal),
        "and it is the approximate one of the two — the later stage cannot repair better, only later");
    }

    private static double Norm(Complex32 a, Complex32 b)
    {
      double re = (double)a.Real - b.Real, im = (double)a.Imaginary - b.Imaginary;
      return re * re + im * im;
    }

    /// <summary>Least-squares fit of the deposit's one complex scalar against the branch's template.</summary>
    private static Complex FitAmplitude(double[] clicked, double[] clean, double bwHz, double frac, int t0)
    {
      var bbA = SstvClickOracle.BrightnessBaseband(clicked, Fs, bwHz, NarrowTaps);
      var bbB = SstvClickOracle.BrightnessBaseband(clean, Fs, bwHz, NarrowTaps);
      var tpl = new SstvClickTemplates(bwHz / Fs, NarrowTaps);
      var row = tpl.Row(tpl.NearestStep(frac));

      Complex num = Complex.Zero;
      double den = 0;
      for (int i = 0; i < row.Length; i++)
      {
        int at = t0 - tpl.Center + i;
        num += new Complex(bbA[at].Real - bbB[at].Real, bbA[at].Imaginary - bbB[at].Imaginary) * row[i];
        den += (double)row[i] * row[i];
      }
      return num / den;
    }

    private static double Centroid(ReadOnlySpan<float> row)
    {
      double sum = 0, moment = 0;
      for (int i = 0; i < row.Length; i++) { sum += row[i]; moment += (double)i * row[i]; }
      return moment / sum;
    }
  }
}
