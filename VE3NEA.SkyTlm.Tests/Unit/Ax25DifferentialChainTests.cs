using System;
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
  /// Pinpoints the G3RUH-BPSK differential deframing bug using REAL satellite AX.25 frames (RANDEV and
  /// Eaglet-1, both published on SatNOGS DB). The on-air channel is NRZI(scramble(HDLC)). A coherent receiver
  /// recovers the channel bits, so the deframer must descramble + NRZI-decode. A DIFFERENTIAL receiver's
  /// detector already performs the NRZI decode (it reads phase transitions), so its output is the scrambled
  /// bits directly — the deframer must then descramble ONLY. Applying NRZI a second time destroys the frame.
  /// Reception is simulated at the bit level so the chain is isolated from the demod.
  /// </summary>
  public class Ax25DifferentialChainTests
  {
    private readonly ITestOutputHelper output;
    public Ax25DifferentialChainTests(ITestOutputHelper o) => output = o;

    // A genuine RANDEV UI frame (source RANDEV), payload without FCS, from SatNOGS DB 2026-05-21.
    private static byte[] RandevFrame() => new byte[]
    {
      0x82,0xA6,0x86,0x98,0x40,0x40,0xE0,0xA4,0x82,0x9C,0x88,0x8A,0xAC,0x61,0x03,0xF0,
      0x0A,0x3F,0x92,0xFB,0x15,0xFF,0xFF,0xFF,0xFF,0x00,0x41,0x00,0x46,0x11,0x11,0x11,0x11,0x11,0x11,0x11,0x11,
      0x12,0x12,0x12,0x12,0x12,0x12,0x12,0x12,0x13,0x13,0x13,0x13,0x13,0x13,0x13,0x13,
      0x01,0x01,0x01,0x01,0x01,0x01,0x01,0x01,0x02,0x02,0x02,0x02,0x02,0x02,0x02,0x02,
      0x03,0x03,0x03,0x03,0x03,0x03,0x03,0x03,0x00,0x00,0x00,0x00,0x00,0x02,0x03,0x84,
    };

    // A genuine Eaglet-1 UI frame (source EAGLET), payload without FCS — info field "00-01-11-10".
    private static byte[] EagletFrame() => new byte[]
    {
      0x9E,0x90,0x84,0xA4,0x9E,0x9A,0xE0,0x8A,0x82,0x8E,0x98,0x8A,0xA8,0x61,0x03,0xF0,
      0x30,0x30,0x2D,0x30,0x31,0x2D,0x31,0x31,0x2D,0x31,0x30,
    };

    public static TheoryData<string> Frames => new() { "RANDEV", "EAGLET" };
    private static byte[] FrameByName(string n) => n == "RANDEV" ? RandevFrame() : EagletFrame();

    private static SignalParams P => new(1200, Modulation.BPSK,  Framing.AX25G3RUH, 48000);

    /// <summary>NRZI decode at the bit level (what a differential detector does): out[k] = NOT(c[k] ^ c[k-1]).</summary>
    private static float[] DifferentialDetect(int[] channel)
    {
      var d = new float[channel.Length - 1];
      for (int k = 1; k < channel.Length; k++) d[k - 1] = (channel[k] == channel[k - 1]) ? 1f : -1f;
      return d;
    }

    [Theory]
    [MemberData(nameof(Frames))]
    public void Coherent_RecoversRealFrame(string name)
    {
      var frame = FrameByName(name);
      int[] onair = Ax25Tx.OnAirBits(frame, flagsBefore: 16, flagsAfter: 8);  // channel = scramble(nrzi(hdlc))
      float[] soft = Ax25Tx.ToSoft(onair);

      var frames = new Ax25G3ruhDeframer().Deframe(new SoftSymbols { Soft = soft, SymbolRate = 1200 }, P).ToList();
      output.WriteLine($"{name} coherent frames={frames.Count}");
      frames.Should().Contain(f => f.CrcValid == true && f.Bytes.SequenceEqual(frame));
    }

    [Theory]
    [MemberData(nameof(Frames))]
    public void Differential_DescrambleOnly_RecoversRealFrame(string name)
    {
      var frame = FrameByName(name);
      int[] onair = Ax25Tx.OnAirBits(frame, flagsBefore: 16, flagsAfter: 8);
      float[] diffSoft = DifferentialDetect(onair);   // detector output = NRZI-decoded channel bits

      // the deframer also tries the descramble-only (differential) chain, which recovers the frame.
      var frames = new Ax25G3ruhDeframer().Deframe(new SoftSymbols { Soft = diffSoft, SymbolRate = 1200 }, P).ToList();
      output.WriteLine($"{name} differential frames={frames.Count}");
      frames.Should().Contain(f => f.CrcValid == true && f.Bytes.SequenceEqual(frame));
    }

    [Theory]
    [MemberData(nameof(Frames))]
    public void Differential_DoubleNrzi_WasTheBug(string name)
    {
      // regression guard: descramble + NRZI on a differential stream does NOT recover the frame — the second
      // NRZI is the bug.
      var frame = FrameByName(name);
      int[] onair = Ax25Tx.OnAirBits(frame, flagsBefore: 16, flagsAfter: 8);
      float[] diffSoft = DifferentialDetect(onair);

      float[] doubled = SoftBits.NrziDecode(SoftBits.G3ruhDescramble(diffSoft));
      var frames = new Ax25G3ruhDeframer().ExtractFrames(doubled);
      frames.Should().NotContain(f => f.CrcValid == true && f.Bytes.SequenceEqual(frame));
    }
  }
}
