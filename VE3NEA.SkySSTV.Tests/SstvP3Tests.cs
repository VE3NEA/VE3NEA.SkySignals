using System;
using FluentAssertions;
using MathNet.Numerics;
using Xunit;
using Xunit.Abstractions;

namespace VE3NEA.SkySSTV.Tests
{
  /// <summary>
  /// P3 tests (plan §1.6/§1.10/§7): KF1 sync/slant tracking and coast-through-fades. Tracking measures each
  /// line's 1200 Hz sync onset and re-anchors the scan to it, so a sample-clock error (slant) that shears a
  /// fixed-period decode is removed, and a mid-image dropout is ridden through by the Kalman prediction.
  /// </summary>
  public class SstvP3Tests
  {
    private const double Fs = 48000.0;
    private readonly ITestOutputHelper output;
    public SstvP3Tests(ITestOutputHelper o) => output = o;


    // ----------------------------------------------------------------------------------------------------
    //                                          slant correction
    // ----------------------------------------------------------------------------------------------------


    [Theory]
    [InlineData(SstvMode.Robot36)]
    [InlineData(SstvMode.Robot72)]
    public void Tracking_CorrectsSlant(SstvMode mode)
    {
      // 200 ppm sample-clock error accumulates to a many-pixel horizontal shear by the last line; a
      // fixed-period decode is badly slanted, tracking re-anchors every line and recovers full fidelity.
      var spec = SstvModes.Get(mode);
      var src = GrayscaleGradient(spec.Width, spec.Height);
      var iq = SstvEncoder.Encode(src, mode, new SstvEncoderOptions { IncludeVis = false, SlantPpm = 200 });

      double tracked = Psnr(src, SstvDecoder.Decode(iq, mode, new SstvDecodeOptions()));
      double untracked = Psnr(src, SstvDecoder.Decode(iq, mode, new SstvDecodeOptions { Track = false }));
      output.WriteLine($"{mode} slant 200 ppm: tracked={tracked:0.0} dB  untracked={untracked:0.0} dB");

      tracked.Should().BeGreaterThan(30.0, "KF1 re-anchors each line to its sync, removing the slant");
      tracked.Should().BeGreaterThan(untracked + 6.0, "tracking must clearly beat a slant-blind fixed decode");
    }

    [Fact]
    public void Tracking_CleanSignal_MatchesFixedTiming()
    {
      // on a slant-free signal tracking must not hurt: it should land within ~1 sample of the true onsets.
      var spec = SstvModes.Get(SstvMode.Robot36);
      var src = GrayscaleGradient(spec.Width, spec.Height);
      var iq = SstvEncoder.Encode(src, SstvMode.Robot36, new SstvEncoderOptions { IncludeVis = false });

      double tracked = Psnr(src, SstvDecoder.Decode(iq, SstvMode.Robot36, new SstvDecodeOptions()));
      output.WriteLine($"clean tracked PSNR = {tracked:0.0} dB");
      tracked.Should().BeGreaterThan(30.0, "tracking a clean signal stays near the fixed-timing fidelity");
    }


    // ----------------------------------------------------------------------------------------------------
    //                                       coast through a fade
    // ----------------------------------------------------------------------------------------------------


    [Fact]
    public void Tracking_CoastsThroughMidImageFade()
    {
      // blank a ~0.6 s span in the middle of the transmission (a signal dropout): the syncs there vanish, so
      // KF1 coasts on its prediction. The lines AFTER the fade must stay locked — a fixed decode past a fade
      // that also lost acquisition would drift; here timing rides through and the tail image is intact.
      var spec = SstvModes.Get(SstvMode.Robot36);
      var src = GrayscaleGradient(spec.Width, spec.Height);
      var iq = SstvEncoder.Encode(src, SstvMode.Robot36, new SstvEncoderOptions { IncludeVis = false });

      int fadeStart = iq.Length / 2;
      int fadeLen = (int)(0.6 * Fs);
      for (int i = fadeStart; i < fadeStart + fadeLen && i < iq.Length; i++) iq[i] = Complex32.Zero;

      var dec = SstvDecoder.Decode(iq, SstvMode.Robot36, new SstvDecodeOptions());

      // measure fidelity only over the lines AFTER the fade — those prove the tracker re-locked / never lost lock.
      int firstPostFadeRow = (int)((double)(fadeStart + fadeLen) / iq.Length * spec.Height) + 4;
      double tail = PsnrRows(src, dec, firstPostFadeRow, spec.Height);
      output.WriteLine($"post-fade rows [{firstPostFadeRow},{spec.Height}) PSNR = {tail:0.0} dB");
      tail.Should().BeGreaterThan(28.0, "timing must ride through the dropout so the image after it stays aligned");
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

    private static double Psnr(RgbImage a, RgbImage b) => PsnrRows(a, b, 0, a.Height);

    /// <summary>Peak-SNR (dB) over image rows [<paramref name="row0"/>, <paramref name="row1"/>).</summary>
    private static double PsnrRows(RgbImage a, RgbImage b, int row0, int row1)
    {
      a.Width.Should().Be(b.Width);
      a.Height.Should().Be(b.Height);
      double se = 0; long n = (long)a.Width * (row1 - row0) * 3;
      for (int row = row0; row < row1; row++)
        for (int x = 0; x < a.Width; x++)
        {
          int i = row * a.Width + x;
          se += Sq(a.R[i] - b.R[i]) + Sq(a.G[i] - b.G[i]) + Sq(a.B[i] - b.B[i]);
        }
      double mse = se / n;
      return mse <= 1e-9 ? 100.0 : 10.0 * Math.Log10(255.0 * 255.0 / mse);
    }

    private static double Sq(int d) => (double)d * d;
  }
}
