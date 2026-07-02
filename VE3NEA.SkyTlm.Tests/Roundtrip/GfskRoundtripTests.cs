using FluentAssertions;
using VE3NEA.SkyTlm.Core;
using VE3NEA.SkyTlm.Dsp;
using VE3NEA.SkyTlm.Tests.Fixtures;
using Xunit;
using Xunit.Abstractions;

namespace VE3NEA.SkyTlm.Tests.Roundtrip
{
  /// <summary>
  /// GFSK is the GMSK non-coherent path with the modulation index no
  /// longer pinned to 0.5. These round-trips prove the demod honors the signal's <b>real</b> h — the
  /// discriminator scales by the looked-up deviation and DF-DD advances its phase reference by ±π·h — so a
  /// wide-h GFSK burst decodes against its true h instead of riding the GMSK h=0.5 assumption.
  /// </summary>
  public class GfskRoundtripTests
  {
    private readonly ITestOutputHelper output;
    public GfskRoundtripTests(ITestOutputHelper o) => output = o;

    private const double Fs = 48000;
    private const double Baud = 4800;

    /// <summary>GFSK params carrying the real deviation (dev = h·Rs/2) so 2·<see cref="SignalParams.Deviation"/>/Rs = h.</summary>
    private static SignalParams Gfsk(double h) =>
      new(Baud, Modulation.GFSK, Framing.USP, Fs, Deviation: h * Baud / 2.0);

    private static CpmFskDemodulator Demod() =>
      new(ModProfile.Gfsk, new GmskDemodOptions { DifferentialOrder = 2 });

    [Theory]
    [InlineData(0.5)]   // degenerate: identical to GMSK
    [InlineData(0.7)]   // typical narrowband cubesat "GFSK"
    [InlineData(0.9)]   // wider
    public void CleanGfsk_DecodesErrorFree_AtRealIndex(double h)
    {
      var bits = GmskModulator.RandomBits(600, seed: 7);
      var iq = GmskModulator.Modulate(bits, Baud, Fs, bt: 0.5, h: h);
      var sym = Demod().DemodulateSegment(iq, Gfsk(h));
      var (ber, _, _) = BerTools.BestBer(bits, sym.Soft);
      output.WriteLine($"h={h} clean eye={sym.EyeSnrDb:0.0}dB ber={ber:0.0000}");
      ber.Should().BeLessThan(1e-3, "DF-DD must decode clean GFSK error-free when it honors the real h");
    }

    [Fact]
    public void HonoringRealIndex_BeatsGmskAssumption_OnWideGfsk()
    {
      // A wide-h GFSK burst (h=0.9). Decode it two ways with DF-DD: (a) honoring the real h via the GFSK
      // deviation, (b) as if it were GMSK (h forced to 0.5). The wrong ±π·h phase step misaligns DF-DD's
      // reference vectors, so honoring the real h must give markedly fewer bit errors under noise.
      const double h = 0.9;
      var bits = GmskModulator.RandomBits(1500, seed: 11);
      var iq = GmskModulator.Modulate(bits, Baud, Fs, bt: 0.5, h: h, esN0Db: 12);

      var real = Demod().DemodulateSegment(iq, Gfsk(h));
      var asGmsk = Demod().DemodulateSegment(iq, new SignalParams(Baud, Modulation.GMSK, Framing.USP, Fs));

      var (berReal, _, _) = BerTools.BestBer(bits, real.Soft);
      var (berGmsk, _, _) = BerTools.BestBer(bits, asGmsk.Soft);
      output.WriteLine($"h={h}  real-h eye={real.EyeSnrDb:0.0}dB ber={berReal:0.0000}   " +
                     $"GMSK-assumption eye={asGmsk.EyeSnrDb:0.0}dB ber={berGmsk:0.0000}");
      berReal.Should().BeLessThan(0.75 * berGmsk,
        "honoring the real h aligns DF-DD's phase reference, cutting the error rate well below the GMSK h=0.5 assumption");
    }
  }
}
