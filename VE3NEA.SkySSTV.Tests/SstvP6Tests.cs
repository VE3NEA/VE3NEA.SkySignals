using System;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace VE3NEA.SkySSTV.Tests
{
  /// <summary>
  /// P6 work-off of the alignment-retro items (docs/roadmap/sstv_alignment_retro.md): J — the Stage-2
  /// sync-path bandpass, and M — Robot36 chroma identity read from the separator tone. Each test pins the
  /// property the retro item demands, on the synthetic closed loop.
  /// </summary>
  public class SstvP6Tests
  {
    private const double Fs = 48000.0;
    private readonly ITestOutputHelper output;
    public SstvP6Tests(ITestOutputHelper o) => output = o;


    // ----------------------------------------------------------------------------------------------------
    //                                 J: Stage-2 sync-path bandpass
    // ----------------------------------------------------------------------------------------------------


    [Fact]
    public void SyncAudio_PassesToneBand_RejectsDcAndHf()
    {
      // discriminated audio = DC Doppler + in-band sync tone + out-of-band FM noise stand-in (8 kHz)
      int n = 48000;
      var disc = new double[n];
      for (int i = 0; i < n; i++)
        disc[i] = 300.0 + Math.Sin(2 * Math.PI * 1200 * i / Fs) + Math.Sin(2 * Math.PI * 8000 * i / Fs);

      double[] y = SstvDecoder.SyncAudio(disc, Fs, new SstvDecodeOptions());

      double mean = 0; for (int i = 0; i < n; i++) mean += y[i]; mean /= n;
      double a1200 = ToneAmp(y, 1200);
      double a8000 = ToneAmp(y, 8000);
      output.WriteLine($"mean={mean:0.000} a1200={a1200:0.000} a8000={a8000:0.0000}");

      Math.Abs(mean).Should().BeLessThan(1.0, "the bandpass must remove the DC Doppler term");
      a1200.Should().BeGreaterThan(0.85, "the 1200 Hz sync tone is in the passband");
      a8000.Should().BeLessThan(0.02, "out-of-band noise must be rejected from the energy normalizer");
    }

    [Fact]
    public void SyncAudio_LiftsSyncCoherence_UnderWidebandNoise()
    {
      // the retro-J mechanism, tested directly: coherence divides by TOTAL window energy, so wideband
      // noise on the discriminator output (post-FM noise is parabolic, mostly ABOVE the audio band)
      // inflates the denominator and crushes the score unless the band is limited first. Inject white
      // discriminator-domain noise of the FM deviation's scale: ~94 % of it lies out of band.
      var spec = SstvModes.Get(SstvMode.Robot36);
      var iq = SstvEncoder.Encode(new RgbImage(spec.Width, spec.Height), SstvMode.Robot36,
        new SstvEncoderOptions { IncludeVis = false });
      var o = new SstvDecodeOptions();
      double[] disc = SstvDecoder.Discriminator(iq, o);
      var rng = new Random(5);
      for (int i = 0; i < disc.Length; i++) disc[i] += 5000.0 * Gauss(rng);
      double[] sync = SstvDecoder.SyncAudio(disc, Fs, o);

      double raw = MaxSyncScore(disc, spec);
      double band = MaxSyncScore(sync, spec);
      output.WriteLine($"max sync matched-filter score: raw={raw:0.000} bandpassed={band:0.000}");

      band.Should().BeGreaterThan(raw * 1.5, "band-limiting must lift the noise-crushed sync score substantially");
      band.Should().BeGreaterThan(0.25, "a bandpassed sync must clear the detection thresholds");
    }


    // ----------------------------------------------------------------------------------------------------
    //                       M: Robot36 chroma identity from the separator tone
    // ----------------------------------------------------------------------------------------------------


    [Fact]
    public void Robot36_ChromaSurvivesMidImageLock()
    {
      // lock one full line late (an odd transmitted line): with parity-derived chroma the R-Y/B-Y streams
      // swap and solid red decodes blue; reading the 1500/2300 Hz separator keeps the colors right.
      var spec = SstvModes.Get(SstvMode.Robot36);
      var src = new RgbImage(spec.Width, spec.Height);
      for (int y = 0; y < spec.Height; y++)
        for (int x = 0; x < spec.Width; x++)
          src.Set(x, y, 220, 30, 30);

      var iq = SstvEncoder.Encode(src, SstvMode.Robot36, new SstvEncoderOptions { IncludeVis = false });
      int oneLine = (int)Math.Round(spec.LinePeriodMs / 1000.0 * Fs);
      var dec = SstvDecoder.Decode(iq, SstvMode.Robot36,
        new SstvDecodeOptions { Acquire = false, Track = false, StartSample = oneLine });

      // average an interior patch (rows early enough that the truncated tail does not matter)
      double r = 0, b = 0; int cnt = 0;
      for (int y = 20; y < 100; y++)
        for (int x = 40; x < spec.Width - 40; x++)
        {
          var (rr, _, bb) = dec.Get(x, y);
          r += rr; b += bb; cnt++;
        }
      r /= cnt; b /= cnt;
      output.WriteLine($"mid-lock decode: mean R={r:0} mean B={b:0}");

      r.Should().BeGreaterThan(150, "the image is red; chroma identity must survive a mid-image lock");
      b.Should().BeLessThan(90, "swapped chroma would turn the red image blue");
    }


    // ----------------------------------------------------------------------------------------------------
    //                                            helpers
    // ----------------------------------------------------------------------------------------------------


    /// <summary>Amplitude of the <paramref name="freqHz"/> tone via coherent correlation (skips edges).</summary>
    private static double ToneAmp(double[] x, double freqHz)
    {
      int a = x.Length / 8, b = x.Length - x.Length / 8;
      double w = 2 * Math.PI * freqHz / Fs, sc = 0, ss = 0;
      for (int i = a; i < b; i++) { sc += x[i] * Math.Cos(w * i); ss += x[i] * Math.Sin(w * i); }
      return 2 * Math.Sqrt(sc * sc + ss * ss) / (b - a);
    }

    private static double Gauss(Random r)
    {
      double u1 = 1.0 - r.NextDouble(), u2 = 1.0 - r.NextDouble();
      return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }

    /// <summary>Best matched-filter sync score over the first few lines of <paramref name="audio"/>.</summary>
    private static double MaxSyncScore(double[] audio, SstvModeSpec spec)
    {
      var filter = new SstvSyncFilter(audio, Fs);
      int pulseLen = (int)Math.Round(spec.SyncMs / 1000.0 * Fs);
      int limit = Math.Min(filter.MaxPos(pulseLen), (int)(5 * spec.LinePeriodMs / 1000.0 * Fs));
      double best = 0;
      for (int t = pulseLen; t < limit; t++) best = Math.Max(best, filter.Score(t, pulseLen));
      return best;
    }
  }
}
