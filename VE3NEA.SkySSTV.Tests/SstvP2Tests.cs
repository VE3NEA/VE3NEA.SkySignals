using System;
using FluentAssertions;
using MathNet.Numerics;
using Xunit;
using Xunit.Abstractions;

namespace VE3NEA.SkySSTV.Tests
{
  /// <summary>
  /// P2 tests (plan §4/§7): the VIS-header matched filter, the 1200 Hz sync correlator, and the
  /// real-timing acquisition that replaces P1's fixed <see cref="SstvDecodeOptions.StartSample"/>.
  /// The VIS/sync detectors are exercised directly (internals are visible to this project); acquisition
  /// is exercised end-to-end through <see cref="SstvDecoder.Decode"/> including a lead pad.
  /// </summary>
  public class SstvP2Tests
  {
    private const double Fs = 48000.0;
    private readonly ITestOutputHelper output;
    public SstvP2Tests(ITestOutputHelper o) => output = o;


    // ----------------------------------------------------------------------------------------------------
    //                                           VIS detection
    // ----------------------------------------------------------------------------------------------------


    [Theory]
    [InlineData(SstvMode.Robot36)]
    [InlineData(SstvMode.Robot72)]
    [InlineData(SstvMode.Pd120)]
    public void DetectVis_FindsHeaderAndDecodesMode(SstvMode mode)
    {
      var spec = SstvModes.Get(mode);
      var iq = SstvEncoder.Encode(new RgbImage(spec.Width, spec.Height), mode,
        new SstvEncoderOptions { IncludeVis = true });

      var vis = SstvDecoder.DetectVis(iq);
      output.WriteLine($"{mode}: found={vis.Found} byte=0x{vis.VisByte:X2} mode={vis.Mode} score={vis.Score:0.00}");
      vis.Found.Should().BeTrue("a clean VIS header must pass all structural gates + parity");
      vis.ParityOk.Should().BeTrue();
      vis.VisByte.Should().Be(spec.VisByte);
      vis.Mode.Should().Be(mode);
    }

    [Fact]
    public void DetectVis_NoHeader_ReportsNotFound()
    {
      var spec = SstvModes.Get(SstvMode.Robot36);
      var iq = SstvEncoder.Encode(new RgbImage(spec.Width, spec.Height), SstvMode.Robot36,
        new SstvEncoderOptions { IncludeVis = false });

      var vis = SstvDecoder.DetectVis(iq);
      vis.Found.Should().BeFalse("without a header there is no VIS leader/break/parity structure to lock onto");
    }

    [Fact]
    public void DetectVis_SustainedCarrier_RejectedByBreakNotch()
    {
      // a continuous 1900 Hz tone sits on the leader frequency but has no 10 ms 1200 Hz break; the
      // break-notch gate (a dip a constant carrier physically cannot fake, plan §4) must reject it.
      var iq = FmTone(SstvTones.Center, 1.5, Fs, deviationHz: 5000.0);

      var vis = SstvDecoder.DetectVis(iq);
      vis.Found.Should().BeFalse("a sustained leader-frequency carrier lacks the mandatory 1200 Hz break");
    }


    // ----------------------------------------------------------------------------------------------------
    //                                     sync-train acquisition
    // ----------------------------------------------------------------------------------------------------


    [Fact]
    public void ExtractTrains_NoVis_AnchorsOnLineZero()
    {
      // the MHT extraction replaces the P2 first-peak correlator: the winning train's back-filled grid
      // must extrapolate to the line-0 sync onset even though the detector's own first emittable onset
      // sits one template length into the stream (the driver's lead-in pad covers it).
      var spec = SstvModes.Get(SstvMode.Robot36);
      var iq = SstvEncoder.Encode(new RgbImage(spec.Width, spec.Height), SstvMode.Robot36,
        new SstvEncoderOptions { IncludeVis = false });
      var o = new SstvDecodeOptions();
      double[] sync = SstvDecoder.SyncAudio(SstvDecoder.Discriminator(iq, o), Fs, o);

      var train = SstvDecoder.ExtractTrains(sync, Fs).BestTrain();
      train.Should().NotBeNull("a clean image carries an unmistakable sync train");
      train!.Format.Should().Be(SstvMode.Robot36);
      int first = (int)Math.Round(train.Regr.GetPulseTime(0));
      output.WriteLine($"train first-pulse onset = {first}, pulses = {train.PulseCnt}");
      first.Should().BeInRange(-MsToSamples(spec.SyncMs), MsToSamples(spec.SyncMs),
        "a signal that begins on its line-0 sync pulse anchors within one sync of sample 0");
    }


    // ----------------------------------------------------------------------------------------------------
    //                                       acquisition round-trip
    // ----------------------------------------------------------------------------------------------------


    [Fact]
    public void Decode_AcquiresViaVis_WithLeadPad()
    {
      var spec = SstvModes.Get(SstvMode.Robot36);
      var src = GrayscaleGradient(spec.Width, spec.Height);
      var iq = SstvEncoder.Encode(src, SstvMode.Robot36, new SstvEncoderOptions { IncludeVis = true });
      var padded = Prepend(iq, 20000);                   // silence before the header must not fool acquisition

      var dec = SstvDecoder.Decode(padded, SstvMode.Robot36, new SstvDecodeOptions());
      double psnr = Psnr(src, dec);
      output.WriteLine($"acquired-via-VIS (padded) PSNR = {psnr:0.0} dB");
      psnr.Should().BeGreaterThan(27.0, "VIS acquisition must locate the image start past the lead pad");
    }

    [Fact]
    public void Decode_AcquiresViaSync_NoVis()
    {
      var spec = SstvModes.Get(SstvMode.Robot72);
      var src = GrayscaleGradient(spec.Width, spec.Height);
      var iq = SstvEncoder.Encode(src, SstvMode.Robot72, new SstvEncoderOptions { IncludeVis = false });

      var dec = SstvDecoder.Decode(iq, SstvMode.Robot72, new SstvDecodeOptions());   // Acquire defaults true
      double psnr = Psnr(src, dec);
      output.WriteLine($"acquired-via-sync PSNR = {psnr:0.0} dB");
      psnr.Should().BeGreaterThan(29.0, "sync acquisition on a header-less signal must land on line 0");
    }


    // ----------------------------------------------------------------------------------------------------
    //                                          signal helpers
    // ----------------------------------------------------------------------------------------------------


    /// <summary>FM-on-FM IQ of a constant audio tone: the discriminator recovers a pure sinusoid at
    /// <paramref name="freqHz"/>, mirroring the encoder's tone path.</summary>
    private static Complex32[] FmTone(double freqHz, double seconds, double fs, double deviationHz)
    {
      int n = (int)(seconds * fs);
      var iq = new Complex32[n];
      double subPhase = 0, fmPhase = 0;
      double subStep = 2 * Math.PI * freqHz / fs;
      double devStep = 2 * Math.PI * deviationHz / fs;
      for (int i = 0; i < n; i++)
      {
        subPhase += subStep;
        fmPhase += devStep * Math.Sin(subPhase);
        iq[i] = new Complex32((float)Math.Cos(fmPhase), (float)Math.Sin(fmPhase));
      }
      return iq;
    }

    private static Complex32[] Prepend(Complex32[] iq, int pad)
    {
      var padded = new Complex32[pad + iq.Length];       // leading Complex32.Zero silence
      Array.Copy(iq, 0, padded, pad, iq.Length);
      return padded;
    }

    private static int MsToSamples(double ms) => (int)Math.Round(ms / 1000.0 * Fs);

    /// <summary>Largest value within ±<paramref name="radius"/> of <paramref name="center"/>.</summary>
    private static double MaxAround(double[] track, int center, int radius)
    {
      double max = 0;
      for (int i = Math.Max(0, center - radius); i <= Math.Min(track.Length - 1, center + radius); i++)
        if (track[i] > max) max = track[i];
      return max;
    }


    // ----------------------------------------------------------------------------------------------------
    //                                          images / metric
    // ----------------------------------------------------------------------------------------------------


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

    /// <summary>Peak-SNR (dB) over all pixels and channels; the two images must share dimensions.</summary>
    private static double Psnr(RgbImage a, RgbImage b)
    {
      a.Width.Should().Be(b.Width);
      a.Height.Should().Be(b.Height);
      double se = 0; long n = (long)a.Width * a.Height * 3;
      for (int i = 0; i < a.R.Length; i++)
        se += Sq(a.R[i] - b.R[i]) + Sq(a.G[i] - b.G[i]) + Sq(a.B[i] - b.B[i]);
      double mse = se / n;
      return mse <= 1e-9 ? 100.0 : 10.0 * Math.Log10(255.0 * 255.0 / mse);
    }

    private static double Sq(int d) => (double)d * d;
  }
}
