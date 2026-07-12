using System;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace VE3NEA.SkySSTV.Tests
{
  /// <summary>P6(b) tests for the streaming sync-pulse detector — now the full separable zero-mean 2D
  /// matched filter (retro A/C/N): ~one onset pulse per line at the line period on a synthetic signal,
  /// mode-family template length carried on each pulse, and a sustained 1200 Hz carrier (no time
  /// contrast) must NOT produce interior pulses.</summary>
  public class SstvPulseDetectorTests
  {
    private const double Fs = 48000.0;
    private readonly ITestOutputHelper output;
    public SstvPulseDetectorTests(ITestOutputHelper o) => output = o;

    [Fact]
    public void EmitsOnePulsePerLine_AtLinePeriod()
    {
      var spec = SstvModes.Get(SstvMode.Robot36);
      var iq = SstvEncoder.Encode(new RgbImage(spec.Width, spec.Height), SstvMode.Robot36,
        new SstvEncoderOptions { IncludeVis = false });
      var o = new SstvDecodeOptions();
      double[] audio = SstvDecoder.SyncAudio(SstvDecoder.Discriminator(iq, o), Fs, o);

      var pulses = new SstvPulseDetector(Fs, spec.SyncMs).Detect(audio);
      output.WriteLine($"{pulses.Count} pulses over {spec.LineCount} lines");

      // roughly one pulse per line (allow a little slack at the edges)
      pulses.Count.Should().BeInRange(spec.LineCount - 8, spec.LineCount + 8);

      // spacing ≈ the line period, and each pulse carries the family template length
      double period = spec.LinePeriodMs / 1000.0 * Fs;
      int mids = 0;
      for (int i = 1; i < pulses.Count; i++)
        if (Math.Abs((pulses[i].Time - pulses[i - 1].Time) - period) < 40) mids++;
      mids.Should().BeGreaterThan(pulses.Count - 10, "consecutive pulses are one line period apart");
      pulses.Should().OnlyContain(p => p.DurMs == (float)spec.SyncMs, "pulses carry the family discriminant");
    }

    [Fact]
    public void OnsetsAreConsistent_AcrossTheTrain()
    {
      // the bipolar template peaks at the pulse ONSET; the onset phase modulo the line period must be
      // stable to a few samples (this is what the RLS regressor consumes).
      var spec = SstvModes.Get(SstvMode.Robot36);
      var iq = SstvEncoder.Encode(new RgbImage(spec.Width, spec.Height), SstvMode.Robot36,
        new SstvEncoderOptions { IncludeVis = false });
      var o = new SstvDecodeOptions();
      double[] audio = SstvDecoder.SyncAudio(SstvDecoder.Discriminator(iq, o), Fs, o);

      var pulses = new SstvPulseDetector(Fs, spec.SyncMs).Detect(audio);
      double period = spec.LinePeriodMs / 1000.0 * Fs;
      int stable = 0;
      for (int i = 1; i < pulses.Count; i++)
      {
        double phase = (pulses[i].Time - pulses[0].Time) % period;
        if (phase > period / 2) phase -= period;
        if (Math.Abs(phase) < 12) stable++;
      }
      stable.Should().BeGreaterThan((int)(0.9 * (pulses.Count - 1)), "onsets must sit on a stable period grid");
    }

    [Fact]
    public void SustainedCarrier_ProducesNoInteriorPulses()
    {
      // retro item A: a constant 1200 Hz tone has no time contrast — the bipolar template must drive its
      // score to ~0. Only the on/off edges may register; the interior must be silent (the old
      // frequency-axis-only detector emitted a pulse at the centroid of the whole carrier).
      int n = (int)(3 * Fs);
      var audio = new double[n];
      for (int i = 0; i < n; i++) audio[i] = 400.0 * Math.Sin(2 * Math.PI * SstvTones.Sync * i / Fs);

      var pulses = new SstvPulseDetector(Fs, SstvModes.Get(SstvMode.Robot36).SyncMs).Detect(audio);
      output.WriteLine($"{pulses.Count} pulses on a 3 s sustained carrier: " +
        string.Join(", ", pulses.ConvertAll(p => $"{p.Time / Fs:0.00}s/{p.Power:0.00}")));

      pulses.FindAll(p => p.Time >= 0.3 * Fs && p.Time <= 2.7 * Fs).Should().BeEmpty(
        "a sustained carrier has no time contrast — only the on/off edges may score");
    }
  }
}
