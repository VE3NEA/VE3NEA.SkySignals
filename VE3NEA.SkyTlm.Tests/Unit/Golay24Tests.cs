using System;
using FluentAssertions;
using VE3NEA.SkyTlm.Deframing;
using Xunit;

namespace VE3NEA.SkyTlm.Tests.Unit
{
  /// <summary>
  /// Extended Golay (24,12) codec (port of gr-satellites <c>golay24.c</c>): exhaustive encode/decode
  /// roundtrip, full 3-error correction, and 4-error detection — the code's exact guarantees.
  /// </summary>
  public class Golay24Tests
  {
    [Fact]
    public void Roundtrip_AllDatawords()
    {
      for (uint d = 0; d < 4096; d++)
      {
        uint cw = Golay24.Encode(d);
        uint rx = cw;
        Golay24.Decode(ref rx).Should().Be(0);
        rx.Should().Be(cw);
        (rx & 0xfff).Should().Be(d);
      }
    }

    [Fact]
    public void CorrectsUpToThreeErrors()
    {
      var rnd = new Random(123);
      for (int trial = 0; trial < 200; trial++)
      {
        uint d = (uint)rnd.Next(4096);
        uint cw = Golay24.Encode(d);

        int nerr = 1 + rnd.Next(3);
        uint err = 0;
        while (System.Numerics.BitOperations.PopCount(err) < nerr)
          err |= 1u << rnd.Next(24);

        uint rx = cw ^ err;
        Golay24.Decode(ref rx).Should().Be(System.Numerics.BitOperations.PopCount(err));
        rx.Should().Be(cw, "up to 3 errors are always corrected");
      }
    }

    [Fact]
    public void DetectsFourErrors()
    {
      var rnd = new Random(321);
      for (int trial = 0; trial < 200; trial++)
      {
        uint cw = Golay24.Encode((uint)rnd.Next(4096));

        uint err = 0;
        while (System.Numerics.BitOperations.PopCount(err) < 4)
          err |= 1u << rnd.Next(24);

        uint rx = cw ^ err;
        Golay24.Decode(ref rx).Should().Be(-1, "weight-4 errors are detected as uncorrectable");
      }
    }
  }
}
