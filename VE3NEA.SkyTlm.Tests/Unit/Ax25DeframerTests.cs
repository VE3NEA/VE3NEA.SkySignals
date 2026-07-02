using System.Linq;
using FluentAssertions;
using VE3NEA.SkyTlm.Core;
using VE3NEA.SkyTlm.Deframing;
using VE3NEA.SkyTlm.Tests.Fixtures;
using Xunit;
using Xunit.Abstractions;

namespace VE3NEA.SkyTlm.Tests.Unit
{
  /// <summary>
  /// AX.25 G3RUH deframer tests driven by the transmit-side reference (<see cref="Ax25Tx"/>): build a real
  /// UI frame, run it through the TX chain (HDLC → NRZI → scramble), and assert the deframer recovers the
  /// exact bytes with a valid CRC. Covers the soft chain, polarity insensitivity, bit-stuffing, and the
  /// CRC-assisted (Chase) correction. Isolated from the DSP — a failure here is a deframing bug.
  /// </summary>
  public class Ax25DeframerTests
  {
    private readonly ITestOutputHelper output;
    public Ax25DeframerTests(ITestOutputHelper o) => output = o;

    private static readonly SignalParams P = new(9600, Modulation.GMSK,  Framing.AX25G3RUH, 48000);

    private static SoftSymbols Soft(float[] s) => new() { Soft = s, SymbolRate = 9600 };

    [Fact]
    public void CleanFrame_RoundTrips_WithValidCrc()
    {
      var frame = Ax25Tx.MakeUiFrame("CQ", "VE3NEA", "Hello from FskDemod M3!");
      var soft = Ax25Tx.ToSoft(Ax25Tx.OnAirBits(frame));

      var frames = new Ax25G3ruhDeframer().Deframe(Soft(soft), P).ToList();

      frames.Should().HaveCount(1);
      frames[0].CrcValid.Should().BeTrue();
      frames[0].CorrectedBits.Should().Be(0, "a clean frame needs no correction");
      frames[0].Bytes.Should().Equal(frame);
      frames[0].Framing.Should().Be(Framing.AX25G3RUH);
      Ax25Address.Describe(frames[0].Bytes).Should().Be("VE3NEA -> CQ");
    }

    [Fact]
    public void InvertedPolarity_StillDecodes()
    {
      // the non-coherent discriminator has a global sign ambiguity; NRZI must absorb it.
      var frame = Ax25Tx.MakeUiFrame("DEST", "SRC-7", "polarity test");
      var soft = Ax25Tx.ToSoft(Ax25Tx.OnAirBits(frame), amp: -1f); // every bit inverted

      var frames = new Ax25G3ruhDeframer().Deframe(Soft(soft), P).ToList();
      frames.Should().ContainSingle().Which.Bytes.Should().Equal(frame);
    }

    [Fact]
    public void FrameRequiringBitStuffing_RoundTrips()
    {
      // info bytes full of 1-bits force runs of five 1s -> stuffing on TX, de-stuffing on RX.
      var frame = Ax25Tx.MakeUiFrame("AAAAAA", "BBBBBB", new string((char)0xFF, 24));
      var soft = Ax25Tx.ToSoft(Ax25Tx.OnAirBits(frame));

      var frames = new Ax25G3ruhDeframer().Deframe(Soft(soft), P).ToList();
      frames.Should().ContainSingle().Which.Bytes.Should().Equal(frame);
    }

    [Fact]
    public void NoFlags_YieldsNoFrames()
    {
      // A stream with no 0x7E flag can carry no HDLC frame, so the deframer must return nothing.
      var soft = Ax25Tx.ToSoft(Enumerable.Repeat(0, 2000).ToArray()); // all zeros -> never 0x7E
      new Ax25G3ruhDeframer().Deframe(Soft(soft), P).Should().BeEmpty();
    }

    [Fact]
    public void Chase_RecoversSingleBitError_UsingSoftConfidence()
    {
      // inject one error in the DATA domain (post descramble/NRZI) at a low-confidence position and verify
      // the CRC-assisted search flips it back. Tests Chase directly via the internal HDLC stage.
      var frame = Ax25Tx.MakeUiFrame("CQ", "VE3NEA", "chase me");
      var withFcs = Ax25Tx.WithFcs(frame);
      var dataBits = Ax25Tx.HdlcBits(withFcs, flagsBefore: 8, flagsAfter: 4);
      var soft = Ax25Tx.ToSoft(dataBits.ToArray(), amp: 5f); // strong confidence everywhere

      // find a payload data bit (well past the opening flags) and corrupt it weakly (wrong sign, low |LLR|)
      int victim = 8 * 8 + 20; // inside the frame, after the 8 opening flags
      soft[victim] = soft[victim] > 0 ? -0.3f : 0.3f;

      var noChase = new Ax25G3ruhDeframer(new Ax25Options { ChaseFlipBits = 0 }).ExtractFrames(soft);
      noChase.Should().BeEmpty("without Chase the single bit error fails the CRC");

      var fixedFrames = new Ax25G3ruhDeframer(new Ax25Options { ChaseFlipBits = 1 }).ExtractFrames(soft);
      fixedFrames.Should().ContainSingle();
      fixedFrames[0].CrcValid.Should().BeTrue();
      fixedFrames[0].CorrectedBits.Should().Be(1);
      fixedFrames[0].Bytes.Should().Equal(frame);
      output.WriteLine($"Chase fixed {fixedFrames[0].CorrectedBits} bit(s); frame = {fixedFrames[0].Hex}");
    }
  }
}
