using System;
using FluentAssertions;
using VE3NEA.SkyTlm.Core;
using VE3NEA.SkyTlm.Dsp;
using VE3NEA.SkyTlm.Tests.Fixtures;
using Xunit;
using Xunit.Abstractions;

namespace VE3NEA.SkyTlm.Tests.Unit
{
  /// <summary>
  /// The general rational-h MLSE path (Phase-3 MLSE for h = 5/6): the 2p-phase-state trellis with
  /// per-symbol tone-correlation metrics must decode Bell-202-shaped CPFSK (rectangular pulse,
  /// h = 5/6, 12 states) that the hard-wired h = 1/2 Laurent trellis cannot express. Synthetic-only
  /// by design: the real AFSK recordings have a single verifiable frame and cannot show the gain.
  /// </summary>
  public class MlseGeneralHTests
  {
    private readonly ITestOutputHelper output;
    public MlseGeneralHTests(ITestOutputHelper o) => output = o;

    private const double Fs = 48000, Baud = 1200, H = 5.0 / 6;

    private static ModProfile RectProfile => new()
    {
      Pulse = PulseShape.Rectangular,
      Bt = null,
      ModIndex = H,
    };

    private static SignalParams Params() =>
      new(Baud, Modulation.FSK, Framing.USP, Fs, Deviation: H * Baud / 2);

    [Fact]
    public void TryRationalH_SnapsKnownIndices()
    {
      MlsePspDetector.TryRationalH(5.0 / 6, out int m, out int p).Should().BeTrue();
      (m, p).Should().Be((5, 6), "Bell-202's h = 5/6 must map to the 12-state trellis");
      MlsePspDetector.TryRationalH(0.5, out m, out p).Should().BeTrue();
      (m, p).Should().Be((1, 2), "the smallest denominator wins");
      MlsePspDetector.TryRationalH(1.0, out m, out p).Should().BeTrue();
      (m, p).Should().Be((1, 1));
      MlsePspDetector.TryRationalH(0.46, out _, out _).Should().BeFalse(
        "h with no small-denominator rational within tolerance must be rejected (DF-DD fallback)");
    }

    [Fact]
    public void Detector_IdealTiming_DecodesCleanCpfskH56()
    {
      // bypass the Gardner front end: hand the detector exact strobes (symbol centres) so a failure
      // here is a trellis/metric bug, not a timing-alignment one. Scan the fractional strobe offset
      // to also report where the general-h correlator grid actually sits.
      double sps = Fs / Baud;
      var bits = GmskModulator.RandomBits(600, seed: 7);
      var iq = GmskModulator.Modulate(bits, Baud, Fs, bt: 0, h: H);
      var det = new MlsePspDetector(RectProfile, new GmskDemodOptions());

      double bestBer = 1, bestOff = 0;
      for (double off = 0; off < 1; off += 0.125)
      {
        int K = bits.Length - 2;
        var strobes = new double[K];
        for (int k = 0; k < K; k++) strobes[k] = (k + 0.5 + off) * sps;
        var ctx = new DetectorContext
        {
          Baseband = iq, GardnerSoft = new float[K], Strobes = strobes, Sps = sps, Params = Params()
        };
        var soft = det.Detect(ctx);
        var (ber, _, _) = BerTools.BestBer(bits, soft);
        output.WriteLine($"strobe offset {off:0.000} sym: ber={ber:0.0000}");
        if (ber < bestBer) { bestBer = ber; bestOff = off; }
      }
      output.WriteLine($"best: off={bestOff:0.000} ber={bestBer:0.0000}");
      bestBer.Should().BeLessThan(1e-3, "with ideal timing the 12-state trellis must decode clean h=5/6 CPFSK error-free");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]     // residual subcarrier offset (TX audio clock error scale)
    [InlineData(-5)]
    public void Roundtrip_CleanCpfskH56_TracksResidualCfo(double residualCfoHz)
    {
      // full front end (channel filter → discriminator → Gardner) + the general-h trellis; the
      // modulator CFO lands on the detector directly, exercising the 2p-power feed-forward estimate
      // plus the per-survivor trackers.
      var bits = GmskModulator.RandomBits(1000, seed: 5);
      var iq = GmskModulator.Modulate(bits, Baud, Fs, bt: 0, cfoHz: residualCfoHz, esN0Db: 15, h: H);
      var demod = new CpmFskDemodulator(RectProfile, new GmskDemodOptions { DifferentialOrder = 2, UseMlse = true });
      var sym = demod.DemodulateSegment(iq, Params());
      var (ber, _, _) = BerTools.BestBer(bits, sym.Soft);
      output.WriteLine($"residual cfo={residualCfoHz}Hz ber={ber:0.0000}");
      ber.Should().BeLessThan(5e-3, "PSP must hold lock on h=5/6 CPFSK through a residual carrier offset");
    }

    [Theory]
    [InlineData(9)]
    [InlineData(7)]
    public void Roundtrip_BeatsDfdd_UnderNoise(double esN0Db)
    {
      // the point of the generalization: the coherent 12-state trellis with true non-orthogonal tone
      // correlations must beat non-coherent DF-DD on the same noisy h=5/6 burst.
      var bits = GmskModulator.RandomBits(4000, seed: 11);
      var iq = GmskModulator.Modulate(bits, Baud, Fs, bt: 0, esN0Db: esN0Db, h: H);
      var dfdd = new CpmFskDemodulator(RectProfile, new GmskDemodOptions { DifferentialOrder = 2, UseMlse = false })
        .DemodulateSegment(iq, Params());
      var mlse = new CpmFskDemodulator(RectProfile, new GmskDemodOptions { DifferentialOrder = 2, UseMlse = true })
        .DemodulateSegment(iq, Params());
      var (berDfdd, _, _) = BerTools.BestBer(bits, dfdd.Soft);
      var (berMlse, _, _) = BerTools.BestBer(bits, mlse.Soft);
      output.WriteLine($"Es/N0={esN0Db}dB  DF-DD ber={berDfdd:0.0000}  MLSE ber={berMlse:0.0000}");
      berMlse.Should().BeLessThan(berDfdd, "the coherent general-h trellis must beat non-coherent DF-DD at low SNR");
    }

    [Fact]
    public void GaussianPulse_WideH_StillFallsBackToDfdd()
    {
      // the corpus GFSK classes are tuned on DF-DD: a Gaussian-pulse profile at h ≠ 1/2 must keep
      // taking the DF-DD fallback even now that a general-h trellis exists (its full-response signal
      // model does not include the Gaussian pulse ISI).
      const double h = 0.8;
      var bits = GmskModulator.RandomBits(600, seed: 7);
      var iq = GmskModulator.Modulate(bits, 4800, Fs, bt: 0.5, h: h);
      var p = new SignalParams(4800, Modulation.GFSK, Framing.USP, Fs, Deviation: h * 4800 / 2);
      var sym = new CpmFskDemodulator(ModProfile.Gfsk, new GmskDemodOptions { DifferentialOrder = 2, UseMlse = true })
        .DemodulateSegment(iq, p);
      var (ber, _, _) = BerTools.BestBer(bits, sym.Soft);
      output.WriteLine($"h={h} gaussian fallback ber={ber:0.0000}");
      ber.Should().BeLessThan(1e-3, "Gaussian h≠1/2 must still decode via the DF-DD fallback");
    }
  }
}
