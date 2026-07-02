using FluentAssertions;
using VE3NEA.SkyTlm.Core;
using VE3NEA.SkyTlm.Dsp;
using VE3NEA.SkyTlm.Tests.Fixtures;
using Xunit;
using Xunit.Abstractions;

namespace VE3NEA.SkyTlm.Tests.Roundtrip
{
  /// <summary>
  /// The PSK sibling demodulator. These round-trips prove the linear-PSK
  /// chain end-to-end — RRC matched filter → complex Gardner timing → carrier recovery (coherent BPSK Costas)
  /// or differential detection (DBPSK), plus the Manchester chip-combine for the DBPSK-Manchester corpus.
  /// </summary>
  public class BpskRoundtripTests
  {
    private readonly ITestOutputHelper output;
    public BpskRoundtripTests(ITestOutputHelper o) => output = o;

    private const double Fs = 48000;

    private static SignalParams Bpsk(double baud) =>
      new(baud, Modulation.BPSK, Framing.USP, Fs);
    private static SignalParams Dbpsk(double baud, bool manchester = false) =>
      new(baud, Modulation.BPSK, Framing.USP, Fs) { Manchester = manchester, Differential = true };

    private static BpskDemodulator Coherent() => new(new BpskDemodOptions { Differential = false });
    private static BpskDemodulator Differential(bool manchester = false) =>
      new(new BpskDemodOptions { Differential = true, Manchester = manchester });

    [Theory]
    [InlineData(1200)]
    [InlineData(2400)]
    [InlineData(4800)]
    public void CleanBpsk_DecodesErrorFree(double baud)
    {
      // A static carrier phase offset exercises the Costas loop (its job is to recover it).
      var bits = GmskModulator.RandomBits(800, seed: 3);
      var iq = BpskModulator.ModulateBpsk(bits, baud, Fs, phaseRad: 0.7);
      var sym = Coherent().DemodulateSegment(iq, Bpsk(baud));
      var (ber, _, _) = BerTools.BestBer(bits, sym.Soft);
      output.WriteLine($"BPSK baud={baud} clean eye={sym.EyeSnrDb:0.0}dB ber={ber:0.0000}");
      ber.Should().BeLessThan(1e-3, "the Costas loop must recover the carrier and decode clean BPSK error-free");
    }

    [Theory]
    [InlineData(1200)]
    [InlineData(2400)]
    [InlineData(4800)]
    public void CleanDbpsk_DecodesErrorFree(double baud)
    {
      var bits = GmskModulator.RandomBits(800, seed: 5);
      var iq = BpskModulator.ModulateDbpsk(bits, baud, Fs);
      var sym = Differential().DemodulateSegment(iq, Dbpsk(baud));
      var (ber, _, _) = BerTools.BestBer(bits, sym.Soft);
      output.WriteLine($"DBPSK baud={baud} clean eye={sym.EyeSnrDb:0.0}dB ber={ber:0.0000}");
      ber.Should().BeLessThan(1e-3, "differential detection must decode clean DBPSK error-free");
    }

    [Fact]
    public void CleanDbpskManchester_DecodesErrorFree()
    {
      // 1200 chips/s → 600 data bits/s (the AMSAT/FUNcube DBPSK-Manchester shape).
      const double chipRate = 1200;
      var data = GmskModulator.RandomBits(400, seed: 9);
      var iq = BpskModulator.ModulateDbpskManchester(data, chipRate, Fs);
      var sym = Differential(manchester: true).DemodulateSegment(iq, Dbpsk(chipRate, manchester: true));
      var (ber, _, _) = BerTools.BestBer(data, sym.Soft);
      output.WriteLine($"DBPSK-Manchester chip={chipRate} clean eye={sym.EyeSnrDb:0.0}dB ber={ber:0.0000} " +
                     $"({sym.Count} data syms from {data.Length} bits)");
      ber.Should().BeLessThan(1e-3, "differential detection + Manchester combine must recover the data error-free");
    }

    [Fact]
    public void Dbpsk_RobustToCarrierOffset()
    {
      // A CFO that would defeat a naive coherent slicer; differential detection's CFO de-bias absorbs it.
      const double baud = 2400;
      var bits = GmskModulator.RandomBits(1000, seed: 13);
      var iq = BpskModulator.ModulateDbpsk(bits, baud, Fs, cfoHz: 150);
      var sym = Differential().DemodulateSegment(iq, Dbpsk(baud));
      var (ber, _, _) = BerTools.BestBer(bits, sym.Soft);
      output.WriteLine($"DBPSK+CFO=150Hz baud={baud} eye={sym.EyeSnrDb:0.0}dB ber={ber:0.0000}");
      ber.Should().BeLessThan(1e-3, "differential detection is carrier-offset robust after the residual-CFO de-bias");
    }

    [Theory]
    [InlineData(40)]
    [InlineData(80)]
    public void CoherentBpsk_LocksFromHead_UnderResidualCfo(double cfoHz)
    {
      // the pipeline derotates each burst by its MEAN CFO, but a Doppler rate / estimation error leaves a
      // residual carrier offset the Costas loop must acquire. Seeded from freq=0 the loop takes hundreds of
      // symbols to pull it in, corrupting the HEAD of a short telemetry frame (the AX.25 address/header). The
      // squared-symbol frequency seed must make it lock from the first symbols — so the head decodes too.
      const double baud = 1200;
      var bits = GmskModulator.RandomBits(1000, seed: 23);
      var iq = BpskModulator.ModulateBpsk(bits, baud, Fs, cfoHz: cfoHz, phaseRad: 0.5);
      var sym = Coherent().DemodulateSegment(iq, Bpsk(baud));
      var (_, off, sign) = BerTools.BestBer(bits, sym.Soft);

      // BER over the first 128 symbols (a 16-byte AX.25 header) — the region slow lock-in destroys. soft[k]
      // maps to bits[k+off]; decoded = sign(soft[k])·sign.
      int head = 128, err = 0, tot = 0;
      for (int k = 8; k < head; k++)
      {
        int ti = k + off; if (ti < 0 || ti >= bits.Length) continue;
        int rx = Math.Sign(sym.Soft[k]) * sign; if (rx == 0) rx = 1;
        if (rx != (bits[ti] == 1 ? 1 : -1)) err++;
        tot++;
      }
      double headBer = (double)err / tot;
      output.WriteLine($"BPSK+residualCFO={cfoHz}Hz baud={baud} eye={sym.EyeSnrDb:0.0}dB headBER={headBer:0.000}");
      headBer.Should().BeLessThan(0.02, "the frequency seed must let the Costas loop lock from the burst head");
    }

    [Fact]
    public void Bpsk_DegradesGracefullyUnderNoise()
    {
      const double baud = 2400;
      var bits = GmskModulator.RandomBits(4000, seed: 17);
      var clean = Coherent().DemodulateSegment(BpskModulator.ModulateBpsk(bits, baud, Fs, phaseRad: 0.4), Bpsk(baud));
      var noisy = Coherent().DemodulateSegment(
        BpskModulator.ModulateBpsk(bits, baud, Fs, phaseRad: 0.4, esN0Db: 6, seed: 2), Bpsk(baud));
      var (berClean, _, _) = BerTools.BestBer(bits, clean.Soft);
      var (berNoisy, _, _) = BerTools.BestBer(bits, noisy.Soft);
      output.WriteLine($"BPSK clean ber={berClean:0.0000} (eye {clean.EyeSnrDb:0.0}dB)  " +
                     $"noisy@Eb/N0=6dB ber={berNoisy:0.0000} (eye {noisy.EyeSnrDb:0.0}dB)");
      berClean.Should().BeLessThan(1e-3);
      // theory: Q(√(2·10^0.6)) ≈ 2.4e-3 — assert within a comfortable factor of the coherent AWGN bound.
      berNoisy.Should().BeLessThan(0.02, "coherent BPSK should track the AWGN bound (~0.24% at Eb/N0=6 dB)");
    }
  }
}
