using System;
using FsCheck;
using FsCheck.Xunit;
using VE3NEA.SkyTlm.Core;
using VE3NEA.SkyTlm.Dsp;
using VE3NEA.SkyTlm.Tests.Fixtures;

namespace VE3NEA.SkyTlm.Tests.Roundtrip
{
  /// <summary>
  /// Property-based (FsCheck) demod invariants. Separate from the example-based round-trips because
  /// FsCheck.Xunit constructs the test class itself and can't supply an <c>ITestOutputHelper</c>, so this
  /// class must have a parameterless constructor.
  /// </summary>
  public class PropertyTests
  {
    private const double Fs = 48000;
    private static readonly double[] Bauds = { 9600, 4800, 2400, 1200 };
    private static SignalParams Params(double baud) => new(baud, Modulation.GMSK,  Framing.USP, Fs);

    /// <summary>For any bit pattern at any supported baud, a clean signal decodes error-free.</summary>
    [Property(MaxTest = 40)]
    public Property CleanGmsk_AnyBits_AnyBaud_DecodesErrorFree(int seed, byte baudIdx)
    {
      double baud = Bauds[baudIdx % Bauds.Length];
      var bits = GmskModulator.RandomBits(400, seed);
      var sym = new GmskDemodulator().DemodulateSegment(GmskModulator.Modulate(bits, baud, Fs, bt: 0.5), Params(baud));
      var (ber, _, _) = BerTools.BestBer(bits, sym.Soft);
      return (ber < 1e-3).ToProperty().Label($"baud={baud} seed={seed} ber={ber:0.000} eye={sym.EyeSnrDb:0.0}dB");
    }

    /// <summary>The recovered symbol clock tracks the true baud regardless of the data carried.</summary>
    [Property(MaxTest = 30)]
    public Property RecoveredClock_IndependentOfData(int seed)
    {
      var bits = GmskModulator.RandomBits(500, seed);
      var sym = new GmskDemodulator().DemodulateSegment(GmskModulator.Modulate(bits, 4800, Fs), Params(4800));
      return (Math.Abs(sym.SamplesPerSymbol - 10.0) < 0.2).ToProperty().Label($"seed={seed} sps={sym.SamplesPerSymbol:0.00}");
    }
  }
}
