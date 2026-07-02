using FluentAssertions;
using MathNet.Numerics;
using VE3NEA.SkyTlm.Core;
using VE3NEA.SkyTlm.Dsp;
using VE3NEA.SkyTlm.Tests.Fixtures;
using Xunit;
using Xunit.Abstractions;

namespace VE3NEA.SkyTlm.Tests.Unit
{
  /// <summary>
  /// Equalizer test: isolate <see cref="BpskEqualizer"/> correctness on a
  /// <i>known</i> linear-ISI channel (T1) and prove it stays out of the way on a clean signal (T2). T1 is the
  /// gating proof the implementation inverts linear ISI; T2 guards the existing clean BPSK round-trips.
  /// </summary>
  public class BpskEqualizerTests
  {
    private readonly ITestOutputHelper output;
    public BpskEqualizerTests(ITestOutputHelper o) => output = o;

    private const double Fs = 48000;
    private const double Baud = 1200;

    private static SignalParams Bpsk() => new(Baud, Modulation.BPSK,  Framing.USP, Fs);

    private static BpskDemodulator NoEq() =>
      new(new BpskDemodOptions { Differential = false, Equalizer = PskEqualizer.Off });
    private static BpskDemodulator WithEq() =>
      new(new BpskDemodOptions { Differential = false, Equalizer = PskEqualizer.Fse });
    // a thorough offline-adaptation config (proves the algorithm converges on a hard known channel).
    private static BpskDemodulator WithEqThorough() =>
      new(new BpskDemodOptions
      {
        Differential = false, Equalizer = PskEqualizer.Fse,
        EqTaps = 31, EqStepCma = 2e-3, EqStepDd = 5e-3, EqPasses = 14
      });

    // T1 — the equalizer recovers a signal an ISI channel has wrecked, and the same channel breaks the no-EQ path.
    [Fact]
    public void SyntheticIsi_EqualizerRecovers_NoEqFails()
    {
      // proakis B — the classic CMA test channel (center-tap ≈ Σ side-taps), so the eye is essentially closed
      // and a plain slicer fails, but an FSE can invert it.
      var bits = GmskModulator.RandomBits(2000, seed: 31);
      var clean = BpskModulator.ModulateBpsk(bits, Baud, Fs, phaseRad: 0.3);
      var isi = ApplySymbolIsi(clean, sps: (int)(Fs / Baud), new[] { 0.407, 0.815, 0.407 });

      var (berNoEq, _, _) = BerTools.BestBer(bits, NoEq().DemodulateSegment(isi, Bpsk()).Soft);
      var (berEq, _, _) = BerTools.BestBer(bits, WithEqThorough().DemodulateSegment(isi, Bpsk()).Soft);

      output.WriteLine($"ISI[0.407,0.815,0.407] BER: no-EQ={berNoEq:0.0000}  with-EQ={berEq:0.0000}");
      berNoEq.Should().BeGreaterThan(0.05, "the Proakis-B ISI channel must wreck the un-equalized path");
      berEq.Should().BeLessThan(2e-3, "the FSE must learn and invert the known linear ISI");
    }

    // T2 — center-spike init = identity: a clean signal through the EQ decodes as well as without it.
    [Fact]
    public void CleanSignal_EqualizerIsIdentity()
    {
      var bits = GmskModulator.RandomBits(2000, seed: 37);
      var iq = BpskModulator.ModulateBpsk(bits, Baud, Fs, phaseRad: 0.3);

      var (berNoEq, _, _) = BerTools.BestBer(bits, NoEq().DemodulateSegment(iq, Bpsk()).Soft);
      var (berEq, _, _) = BerTools.BestBer(bits, WithEq().DemodulateSegment(iq, Bpsk()).Soft);

      output.WriteLine($"clean BER: no-EQ={berNoEq:0.0000}  with-EQ={berEq:0.0000}");
      berNoEq.Should().BeLessThan(1e-3);
      berEq.Should().BeLessThan(1e-3, "the center-spike equalizer must leave a clean signal essentially untouched");
    }

    /// <summary>Apply a real symbol-spaced FIR ISI channel to a complex baseband burst (taps spaced
    /// <paramref name="sps"/> samples apart): y[i] = Σ tap[k]·x[i − k·sps]. Energy-normalized.</summary>
    private static Complex32[] ApplySymbolIsi(Complex32[] x, int sps, double[] taps)
    {
      double e = 0; foreach (var t in taps) e += t * t;
      double norm = 1.0 / System.Math.Sqrt(e);
      int n = x.Length;
      var y = new Complex32[n];
      for (int i = 0; i < n; i++)
      {
        double yr = 0, yi = 0;
        for (int k = 0; k < taps.Length; k++)
        {
          int j = i - k * sps;
          if (j < 0) continue;
          yr += taps[k] * x[j].Real; yi += taps[k] * x[j].Imaginary;
        }
        y[i] = new Complex32((float)(yr * norm), (float)(yi * norm));
      }
      return y;
    }
  }
}
