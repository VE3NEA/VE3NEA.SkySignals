using FluentAssertions;
using VE3NEA.SkyTlm.Dsp;
using VE3NEA.SkyTlm.Tests.Fixtures;
using Xunit;
using Xunit.Abstractions;

namespace VE3NEA.SkyTlm.Tests.Unit
{
  /// <summary>
  /// Unit tests for <see cref="BaudVerifier"/>: synthetic GMSK/FSK bursts at a known baud → the
  /// strongest symbol-rate line must land on the true baud even when the "label" (and hence the
  /// candidate ordering) disagrees — the 2400-vs-9600 DB-label confusion in both directions.
  /// </summary>
  public class BaudVerifierTests
  {
    private readonly ITestOutputHelper output;
    public BaudVerifierTests(ITestOutputHelper o) => output = o;

    private const double Fs = 48000;

    /// <summary>Synthesize a burst and run the verifier with the candidate set of <paramref name="labeledBaud"/>.</summary>
    private BaudLineResult? Verify(double trueBaud, double labeledBaud, double h = 0.5,
      double cfoHz = 0, double esN0Db = double.PositiveInfinity, int nBits = 4000)
    {
      var bits = GmskModulator.RandomBits(nBits);
      var iq = GmskModulator.Modulate(bits, trueBaud, Fs, cfoHz: cfoHz, esN0Db: esN0Db, h: h);
      var candidates = BaudVerifier.CandidateBauds(labeledBaud, Fs);
      double maxCandidate = 0;
      foreach (double b in candidates) maxCandidate = Math.Max(maxCandidate, b);
      double dev = h * trueBaud / 2.0;
      double cutoffHz = dev + 0.75 * maxCandidate;
      var line = BaudVerifier.StrongestLine(iq, Fs, cfoHz, cutoffHz, candidates);
      output.WriteLine($"true={trueBaud} label={labeledBaud} h={h} → " +
        (line == null ? "no line" : $"candidate={line.CandidateBaud} measured={line.MeasuredBaud:F1} score={line.Score:F1}"));
      return line;
    }

    [Theory]
    [InlineData(9600, 2400)]   // label 2400, on air 9600 (the Luca-2k4 class)
    [InlineData(2400, 9600)]   // label 9600, on air 2400 — the harmonic trap: 9600 is 2400's 4th harmonic
    [InlineData(2400, 4800)]   // label 4800, on air 2400 (the CubeSX-HSE-3 class)
    [InlineData(4800, 4800)]   // truthful label
    [InlineData(1200, 1200)]   // truthful label, low baud
    public void StrongestLine_Gmsk_PicksTrueBaud(double trueBaud, double labeledBaud)
    {
      var line = Verify(trueBaud, labeledBaud);

      line.Should().NotBeNull("a clean GMSK burst must show its symbol-rate line");
      line!.CandidateBaud.Should().Be(trueBaud, "the strongest line must sit at the on-air baud");
      line.MeasuredBaud.Should().BeApproximately(trueBaud, trueBaud * 0.01,
        "the refined line frequency is the on-air baud");
    }

    [Theory]
    [InlineData(1.0)]   // h = 1 FSK (dev = baud/2)
    [InlineData(2.0)]   // h = 2 wide FSK (dev = baud)
    public void StrongestLine_WideFsk_PicksTrueBaud(double h)
    {
      var line = Verify(trueBaud: 4800, labeledBaud: 2400, h: h);

      line.Should().NotBeNull();
      line!.CandidateBaud.Should().Be(4800);
    }

    [Fact]
    public void StrongestLine_NoisyOffsetBurst_StillFindsLine()
    {
      var line = Verify(trueBaud: 9600, labeledBaud: 2400, cfoHz: 700, esN0Db: 15);

      line.Should().NotBeNull("15 dB Es/N0 with a 700 Hz CFO is routine burst quality");
      line!.CandidateBaud.Should().Be(9600);
    }

    [Fact]
    public void StrongestLine_NoiseOnly_ReturnsNull()
    {
      var iq = Signals.Awgn(60000, 1.0);
      var candidates = BaudVerifier.CandidateBauds(4800, Fs);

      var line = BaudVerifier.StrongestLine(iq, Fs, 0, 9600, candidates);

      output.WriteLine(line == null ? "no line" : $"candidate={line.CandidateBaud} score={line.Score:F1}");
      line.Should().BeNull("pure noise has no symbol-rate line");
    }

    [Fact]
    public void CandidateBauds_DedupsAndRespectsNyquist()
    {
      var c = BaudVerifier.CandidateBauds(2400, Fs);

      output.WriteLine(string.Join(", ", c));
      c.Should().BeEquivalentTo(new[] { 2400.0, 4800.0, 1200.0, 9600.0 },
        "2×2400 duplicates 4800 and ½·2400 duplicates 1200");

      var high = BaudVerifier.CandidateBauds(19200, Fs);
      high.Should().NotContain(b => b * 1.02 >= Fs / 2, "a line at the baud must sit below Nyquist");
      high.Should().Contain(19200.0);
    }
  }
}
