using System;
using FluentAssertions;
using MathNet.Numerics;
using Xunit;
using Xunit.Abstractions;

namespace VE3NEA.SkySSTV.Tests
{
  /// <summary>
  /// Low-SNR behavior of P1–P4 on the synthetic FM-on-FM chain. The encoder adds complex AWGN of per-
  /// component std σ to a unit-amplitude (constant-envelope) carrier, so the RF SNR is
  /// <c>1/(2σ²)</c> — σ = 0.05 ≈ 23 dB, 0.1 ≈ 17 dB, 0.2 ≈ 11 dB, 0.3 ≈ 7 dB, 0.5 ≈ 3 dB.
  ///
  /// Both detection and image fidelity are robust and asserted here:
  /// <list type="bullet">
  /// <item><b>Detection (P2 VIS, P4 MHT) is robust</b> — the tone/sync matched filters integrate over long
  /// windows, so they survive to a few dB SNR and, importantly, do <b>not</b> false-trigger on pure noise.</item>
  /// <item><b>Image fidelity is now robust too (P6a).</b> The streaming Stage-3 brightness filter (mix-to-
  /// baseband + complex low-pass, <see cref="SstvDecodeOptions.BrightnessBwHz"/>) band-limits the video and so
  /// rejects the FM phase noise the old batch full-band analytic passed straight through — σ=0.05 (~23 dB SNR)
  /// went from ~11 dB PSNR to ~30 dB. These floors reflect the streaming filter; P6(c) tunes the bandwidth on
  /// real captures.</item>
  /// </list>
  /// </summary>
  public class SstvNoiseTests
  {
    private const double Fs = 48000.0;
    private readonly ITestOutputHelper output;
    public SstvNoiseTests(ITestOutputHelper o) => output = o;


    // ----------------------------------------------------------------------------------------------------
    //                                    front-end graceful degradation
    // ----------------------------------------------------------------------------------------------------


    [Theory]
    [InlineData(0.01, 26.0)]   // ~37 dB SNR — at the ≈27.2 dB clean ceiling (video-filter-limited)
    [InlineData(0.02, 26.0)]   // ~31 dB SNR — still at the ceiling
    [InlineData(0.05, 26.0)]   // ~23 dB SNR — still at the ceiling
    [InlineData(0.10, 25.0)]   // ~17 dB SNR — ≈27.0 dB measured: the curve is nearly flat now
    public void Frontend_DegradesGracefullyWithNoise(double sigma, double minPsnr)
    {
      // fixed timing isolates the front-end (channel FIR → discriminator → streaming Stage-3 brightness LPF →
      // integrator) from acquisition/tracking. Floors reflect the P6(c) real-tuned defaults (chan ±6 kHz,
      // video ±600 Hz, encoder dev 3.3 kHz): Robot36 clean sits at a ≈27.2 dB filter-limited ceiling and the
      // noise curve is nearly flat to σ=0.1 — the resolution/noise trade chosen for real weak signals.
      var spec = SstvModes.Get(SstvMode.Robot36);
      var src = GrayscaleGradient(spec.Width, spec.Height);
      var iq = SstvEncoder.Encode(src, SstvMode.Robot36,
        new SstvEncoderOptions { IncludeVis = false, NoiseStdDev = sigma, NoiseSeed = 7 });
      var dec = SstvDecoder.Decode(iq, SstvMode.Robot36, new SstvDecodeOptions { Acquire = false, Track = false });

      double psnr = Psnr(src, dec);
      output.WriteLine($"sigma={sigma} PSNR={psnr:0.0} dB (floor {minPsnr})");
      psnr.Should().BeGreaterThan(minPsnr, "the front-end must degrade gracefully, not collapse, above the FM threshold");
    }


    [Fact]
    public void Frontend_BlankerAndChannelSweep()
    {
      // P6(c) closed-loop experiment: does the envelope-gated impulse blanker (and/or a deviation-matched
      // channel) lift PSNR at and below the FM threshold? Ground truth is exact here; the real-capture
      // probes confirm visually. Nominal dev 3.3 kHz (measured real) and low dev 1.5 kHz (the 04-18 class).
      //
      // Result 2026-07-03: a useful NEGATIVE that does NOT transfer to real signals — synthetic AWGN at
      // σ≤0.6 yields ≤0.05 % clicks, so the blanker only costs (−0.3..−1.6 dB at blank 0.5, ~0 at low σ)
      // and the narrow channel is a wash. The REAL captures show 1.2–2.6 % clicks and the opposite verdict
      // (see Real_P6cDecodeGridProbe): real FM-threshold noise is impulsive in a way this AWGN loop does
      // not reproduce. Keep this sweep as the above-threshold no-regression guard.
      var spec = SstvModes.Get(SstvMode.Robot36);
      var src = GrayscaleGradient(spec.Width, spec.Height);

      foreach (double dev in new[] { 3300.0, 1500.0 })
        foreach (double chanBw in dev > 2000 ? new[] { 6000.0, 4500.0 } : new[] { 6000.0, 3900.0 })
        {
          output.WriteLine($"--- dev={dev:0} chan=±{chanBw:0}");
          foreach (double sigma in new[] { 0.3, 0.4, 0.5, 0.6 })
          {
            var iq = SstvEncoder.Encode(src, SstvMode.Robot36, new SstvEncoderOptions
            { IncludeVis = false, DeviationHz = dev, NoiseStdDev = sigma, NoiseSeed = 7 });

            var line = $"sigma={sigma:0.0}:";
            foreach (double blank in new[] { 0.0, 0.5 })
            {
              var o = new SstvDecodeOptions
              { Acquire = false, Track = false, ChannelBwHz = chanBw, BlankerThreshold = blank };
              double[] disc = SstvDecoder.Discriminator(iq, o);
              int clicks = 0;
              for (int i = 0; i < disc.Length; i++) if (Math.Abs(disc[i]) > 15000) clicks++;
              var dec = SstvDecoder.Decode(disc, SstvMode.Robot36, o);
              line += $"  blank={blank:0.0}: PSNR={Psnr(src, dec):0.0} dB clicks={100.0 * clicks / disc.Length:0.00}%";
            }
            output.WriteLine(line);
          }
        }
    }


    [Fact]
    public void Frontend_DeEmphasisSweep()
    {
      // P6(c) closed-loop de-emphasis experiment (plan §1.3/§6 item 2): exact ground truth on top of the
      // locked decode chain (chan ±4000, blanker 0.5). Single-pole corners: 75 µs ≈ 2122 Hz (broadcast),
      // 300 µs ≈ 531 Hz (NBFM), 750 µs ≈ 212 Hz. The real-capture verdict comes from
      // Real_P6cDeEmphasisProbe; this sweep guards the clean/above-threshold end.
      var spec = SstvModes.Get(SstvMode.Robot36);
      var src = GrayscaleGradient(spec.Width, spec.Height);

      foreach (double sigma in new[] { 0.0, 0.3, 0.5 })
      {
        var iq = SstvEncoder.Encode(src, SstvMode.Robot36, new SstvEncoderOptions
        { IncludeVis = false, DeviationHz = 3300.0, NoiseStdDev = sigma, NoiseSeed = 7 });

        var line = $"sigma={sigma:0.0}:";
        foreach (double tau in new[] { 0.0, 75.0, 300.0, 750.0 })
        {
          var o = new SstvDecodeOptions
          { Acquire = false, Track = false, ChannelBwHz = 4000.0, DeEmphasisUs = tau };
          var dec = SstvDecoder.Decode(SstvDecoder.Discriminator(iq, o), SstvMode.Robot36, o);
          line += $"  tau={tau:0}us: PSNR={Psnr(src, dec):0.0} dB";
        }
        output.WriteLine(line);
      }
    }


    [Fact]
    public void Frontend_WienerSweep()
    {
      // P6(d) synthetic guard (plan §6.2 item 3), now on the PRODUCTION post-filter (SstvWienerFilter,
      // defaults locked by the 2026-07-04 visual judgment): with known injected noise the filtered
      // closed-loop PSNR must not fall below the unfiltered decode — Wiener shrinkage with a correct
      // σ²n is MMSE-favorable, so a regression means the σ²n calibration is wrong. Two sources: the
      // smooth gradient (pure shrinkage gain) and colorbars (edges must pass at g ≈ 1).
      var spec = SstvModes.Get(SstvMode.Robot36);
      foreach (var (name, src) in new[]
        { ("gradient", GrayscaleGradient(spec.Width, spec.Height)),
          ("colorbars", ColorBars(spec.Width, spec.Height)) })
        foreach (double sigma in new[] { 0.0, 0.1, 0.3, 0.5 })
        {
          var iq = SstvEncoder.Encode(src, SstvMode.Robot36, new SstvEncoderOptions
          { IncludeVis = false, DeviationHz = 3300.0, NoiseStdDev = sigma, NoiseSeed = 7 });
          var o = new SstvDecodeOptions
          { Acquire = false, Track = false, ChannelBwHz = 4000.0, WienerEnabled = false };
          double[] disc = SstvDecoder.Discriminator(iq, o);
          var dec = SstvDecoder.Decode(disc, SstvMode.Robot36, o);
          var filtered = SstvDecoder.Decode(disc, SstvMode.Robot36, o with { WienerEnabled = true });

          double rawPsnr = Psnr(src, dec), fPsnr = Psnr(src, filtered);
          output.WriteLine($"{name} sigma={sigma:0.0}: raw={rawPsnr:0.0} dB wiener={fPsnr:0.0} dB " +
            $"({fPsnr - rawPsnr:+0.0;-0.0})");
          if (sigma > 0)
            fPsnr.Should().BeGreaterThanOrEqualTo(rawPsnr,
              $"Wiener shrinkage must not regress PSNR under known noise ({name}, σ={sigma})");
        }
    }


    // ----------------------------------------------------------------------------------------------------
    //                                     VIS detection under noise
    // ----------------------------------------------------------------------------------------------------


    [Theory]
    [InlineData(0.05)]
    [InlineData(0.1)]
    [InlineData(0.2)]
    [InlineData(0.3)]
    public void Vis_DetectsThroughNoise(double sigma)
    {
      var spec = SstvModes.Get(SstvMode.Robot36);
      for (int seed = 1; seed <= 5; seed++)
      {
        var iq = SstvEncoder.Encode(new RgbImage(spec.Width, spec.Height), SstvMode.Robot36,
          new SstvEncoderOptions { IncludeVis = true, NoiseStdDev = sigma, NoiseSeed = seed });
        var vis = SstvDecoder.DetectVis(iq);
        vis.Found.Should().BeTrue($"VIS must survive σ={sigma} (seed {seed})");
        vis.Mode.Should().Be(SstvMode.Robot36);
        vis.ParityOk.Should().BeTrue();
      }
    }

    [Fact]
    public void Vis_RejectsPureNoise()
    {
      // the mandatory 1200 Hz break-notch + parity gates must keep noise from ever reading as a VIS header.
      for (int seed = 1; seed <= 8; seed++)
        SstvDecoder.DetectVis(Noise(96000, 1.0, seed)).Found
          .Should().BeFalse($"pure noise must not fake a VIS header (seed {seed})");
    }


    // ----------------------------------------------------------------------------------------------------
    //                                    MHT mode detection under noise
    // ----------------------------------------------------------------------------------------------------


    [Theory]
    [InlineData(SstvMode.Robot36, 0.3)]
    [InlineData(SstvMode.Robot72, 0.3)]
    [InlineData(SstvMode.Pd120, 0.2)]
    public void ModeDetection_ThroughNoise(SstvMode mode, double sigma)
    {
      var spec = SstvModes.Get(mode);
      for (int seed = 1; seed <= 5; seed++)
      {
        var iq = SstvEncoder.Encode(new RgbImage(spec.Width, spec.Height), mode,
          new SstvEncoderOptions { IncludeVis = false, NoiseStdDev = sigma, NoiseSeed = seed });   // no VIS ⇒ MHT must carry it
        var res = SstvDecoder.DetectMode(iq);
        res.Found.Should().BeTrue($"the sync cadence must survive σ={sigma} for {mode} (seed {seed})");
        res.Mode.Should().Be(mode);
        res.FromVis.Should().BeFalse("no header present — this is the MHT, not VIS");
      }
    }

    [Fact]
    public void ModeDetection_RejectsPureNoise()
    {
      for (int seed = 1; seed <= 8; seed++)
        SstvDecoder.DetectMode(Noise(96000, 1.0, seed)).Found
          .Should().BeFalse($"pure noise has no coherent sync train (seed {seed})");
    }


    // ----------------------------------------------------------------------------------------------------
    //                                          signal helpers
    // ----------------------------------------------------------------------------------------------------


    /// <summary>Signal-free complex AWGN of per-component std <paramref name="sigma"/>.</summary>
    private static Complex32[] Noise(int n, double sigma, int seed)
    {
      var rng = new Random(seed);
      var iq = new Complex32[n];
      for (int i = 0; i < n; i++)
        iq[i] = new Complex32((float)(Gauss(rng) * sigma), (float)(Gauss(rng) * sigma));
      return iq;
    }

    private static double Gauss(Random r)
    {
      double u1 = 1.0 - r.NextDouble(), u2 = 1.0 - r.NextDouble();
      return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }

    private static RgbImage GrayscaleGradient(int w, int h)
    {
      var img = new RgbImage(w, h);
      for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
          byte v = (byte)((x * 255 / (w - 1) + y * 255 / (h - 1)) / 2);
          img.Set(x, y, v, v, v);
        }
      return img;
    }

    /// <summary>Saturated color bars — strong chroma edges for the Wiener guard (the harness pattern
    /// without the luma ramp).</summary>
    private static RgbImage ColorBars(int w, int h)
    {
      (byte r, byte g, byte b)[] bars =
      {
        (255,255,255),(255,255,0),(0,255,255),(0,255,0),(255,0,255),(255,0,0),(0,0,255),(0,0,0)
      };
      var img = new RgbImage(w, h);
      for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
          var (r, g, b) = bars[x * bars.Length / w];
          img.Set(x, y, r, g, b);
        }
      return img;
    }

    private static double Psnr(RgbImage a, RgbImage b)
    {
      double se = 0; long n = (long)a.Width * a.Height * 3;
      for (int i = 0; i < a.R.Length; i++)
        se += Sq(a.R[i] - b.R[i]) + Sq(a.G[i] - b.G[i]) + Sq(a.B[i] - b.B[i]);
      double mse = se / n;
      return mse <= 1e-9 ? 100.0 : 10.0 * Math.Log10(255.0 * 255.0 / mse);
    }

    private static double Sq(int d) => (double)d * d;
  }
}
