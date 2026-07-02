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
    [InlineData(0.01, 29.0)]   // ~37 dB SNR
    [InlineData(0.02, 28.0)]   // ~31 dB SNR
    [InlineData(0.05, 26.0)]   // ~23 dB SNR
    [InlineData(0.10, 20.0)]   // ~17 dB SNR
    public void Frontend_DegradesGracefullyWithNoise(double sigma, double minPsnr)
    {
      // fixed timing isolates the front-end (channel FIR → discriminator → streaming Stage-3 brightness LPF →
      // integrator) from acquisition/tracking. Floors reflect the P6a streaming filter (see class remarks).
      var spec = SstvModes.Get(SstvMode.Robot36);
      var src = GrayscaleGradient(spec.Width, spec.Height);
      var iq = SstvEncoder.Encode(src, SstvMode.Robot36,
        new SstvEncoderOptions { IncludeVis = false, NoiseStdDev = sigma, NoiseSeed = 7 });
      var dec = SstvDecoder.Decode(iq, SstvMode.Robot36, new SstvDecodeOptions { Acquire = false, Track = false });

      double psnr = Psnr(src, dec);
      output.WriteLine($"sigma={sigma} PSNR={psnr:0.0} dB (floor {minPsnr})");
      psnr.Should().BeGreaterThan(minPsnr, "the front-end must degrade gracefully, not collapse, above the FM threshold");
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
