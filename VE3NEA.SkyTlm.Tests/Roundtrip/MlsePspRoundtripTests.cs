using System;
using FluentAssertions;
using VE3NEA.SkyTlm.Core;
using VE3NEA.SkyTlm.Dsp;
using VE3NEA.SkyTlm.Tests.Fixtures;
using Xunit;
using Xunit.Abstractions;

namespace VE3NEA.SkyTlm.Tests.Roundtrip
{
  /// <summary>
  /// Round-trips for the coherent MLSE/PSP detector (<see cref="MlsePspDetector"/>):
  /// clean GMSK decodes error-free, the trellis beats DF-DD at the low SNRs where the Suomi/NUSHSat1 bursts
  /// die, the per-survivor trackers ride out a residual CFO, and h ≠ 1/2 falls back to DF-DD.
  /// </summary>
  public class MlsePspRoundtripTests
  {
    private readonly ITestOutputHelper output;
    public MlsePspRoundtripTests(ITestOutputHelper o) => output = o;

    private const double Fs = 48000;
    private static SignalParams Params(double baud) => new(baud, Modulation.GMSK,  Framing.USP, Fs);
    private static GmskDemodOptions MlseOpt => new() { DifferentialOrder = 2, UseMlse = true };

    [Theory]
    [InlineData(4800)]
    [InlineData(9600)]   // the Suomi 100 rate: native sps 5
    [InlineData(19200)]  // the CUTE rate: native sps 2.5, leans hardest on the upsampler
    public void Mlse_CleanGmsk_DecodesErrorFree(double baud)
    {
      var bits = GmskModulator.RandomBits(600, seed: 7);
      var iq = GmskModulator.Modulate(bits, baud, Fs, bt: 0.5);
      var sym = new GmskDemodulator(MlseOpt).DemodulateSegment(iq, Params(baud));
      var (ber, off, sign) = BerTools.BestBer(bits, sym.Soft);
      output.WriteLine($"baud={baud} clean ber={ber:0.0000} off={off} sign={sign}");
      ber.Should().BeLessThan(1e-3, "MLSE must decode clean GMSK error-free");
    }

    [Theory]
    [InlineData(9600, 8)]
    [InlineData(9600, 6)]
    [InlineData(19200, 8)]   // the CUTE rate (native sps 2.5)
    [InlineData(19200, 6)]
    public void Mlse_BeatsDfdd_UnderNoise(double baud, double esN0Db)
    {
      // high-baud GMSK where DF-DD's non-coherent loss + uncompensated
      // pulse ISI dominate. The coherent trellis should cut the BER several-fold (~2-3 dB).
      var bits = GmskModulator.RandomBits(4000, seed: 11);
      var iq = GmskModulator.Modulate(bits, baud, Fs, bt: 0.5, esN0Db: esN0Db);
      var dfdd = new GmskDemodulator(new GmskDemodOptions { DifferentialOrder = 2 })
        .DemodulateSegment(iq, Params(baud));
      var mlse = new GmskDemodulator(MlseOpt).DemodulateSegment(iq, Params(baud));
      var (berDfdd, _, _) = BerTools.BestBer(bits, dfdd.Soft);
      var (berMlse, _, _) = BerTools.BestBer(bits, mlse.Soft);
      output.WriteLine($"baud={baud} Es/N0={esN0Db}dB  DF-DD ber={berDfdd:0.0000}  MLSE ber={berMlse:0.0000}");
      berMlse.Should().BeLessThan(berDfdd, "the coherent trellis must beat non-coherent DF-DD at low SNR");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(12)]    // residual CFO the burst-level derotation typically leaves (~0.25% of Rs at 4800 Bd)
    [InlineData(-12)]
    public void Mlse_TracksResidualCfo(double residualCfoHz)
    {
      // demodulateSegment gets the burst as-is (no derotation step), so the modulator CFO lands on the
      // detector directly — exercising the feed-forward estimate plus the per-survivor trackers.
      var bits = GmskModulator.RandomBits(1000, seed: 5);
      var iq = GmskModulator.Modulate(bits, 4800, Fs, bt: 0.5, cfoHz: residualCfoHz, esN0Db: 15);
      var sym = new GmskDemodulator(MlseOpt).DemodulateSegment(iq, Params(4800));
      var (ber, _, _) = BerTools.BestBer(bits, sym.Soft);
      output.WriteLine($"residual cfo={residualCfoHz}Hz ber={ber:0.0000}");
      ber.Should().BeLessThan(5e-3, "PSP must hold lock through a residual carrier offset");
    }

    [Fact]
    public void Mlse_WideH_FallsBackToDfdd()
    {
      // h=0.8 GFSK: the 4-phase-state trellis doesn't apply; the detector must hand off to DF-DD
      // (which honors the real h) rather than emit garbage.
      const double h = 0.8;
      var bits = GmskModulator.RandomBits(600, seed: 7);
      var iq = GmskModulator.Modulate(bits, 4800, Fs, bt: 0.5, h: h);
      var p = new SignalParams(4800, Modulation.GFSK, Framing.USP, Fs, Deviation: h * 4800 / 2);
      var sym = new CpmFskDemodulator(ModProfile.Gfsk, MlseOpt).DemodulateSegment(iq, p);
      var (ber, _, _) = BerTools.BestBer(bits, sym.Soft);
      output.WriteLine($"h={h} fallback ber={ber:0.0000}");
      ber.Should().BeLessThan(1e-3, "h≠1/2 must fall back to the DF-DD path and still decode");
    }

    [Fact]
    public void Mlse_SoftOutput_FlagsTheErrors()
    {
      // the LLRs exist to drive erasure-RS/Chase (items 1/5): at an SNR where some bits do break,
      // wrong decisions must carry below-average confidence so the deframers erase the right bytes.
      var bits = GmskModulator.RandomBits(4000, seed: 13);
      var iq = GmskModulator.Modulate(bits, 9600, Fs, bt: 0.5, esN0Db: 6);
      var sym = new GmskDemodulator(MlseOpt).DemodulateSegment(iq, Params(9600));
      var (ber, off, sign) = BerTools.BestBer(bits, sym.Soft);
      ber.Should().BeGreaterThan(0, "this test needs some errors to score confidence against");

      double wrongSum = 0, rightSum = 0; int wrongN = 0, rightN = 0;
      for (int k = 8; k < sym.Soft.Length - 8; k++)
      {
        int ti = k + off;
        if (ti < 0 || ti >= bits.Length) continue;
        int tx = bits[ti] == 1 ? 1 : -1;
        int rx = Math.Sign(sym.Soft[k]) * sign; if (rx == 0) rx = 1;
        double conf = Math.Abs(sym.Soft[k]);
        if (rx == tx) { rightSum += conf; rightN++; } else { wrongSum += conf; wrongN++; }
      }
      double wrongMean = wrongSum / Math.Max(wrongN, 1), rightMean = rightSum / Math.Max(rightN, 1);
      output.WriteLine($"ber={ber:0.0000}  mean|soft| right={rightMean:0.000} wrong={wrongMean:0.000}");
      wrongMean.Should().BeLessThan(0.6 * rightMean, "bit errors must come with low LLR magnitude");
    }
  }
}
