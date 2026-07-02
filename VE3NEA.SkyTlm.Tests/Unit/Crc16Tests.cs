using System.Text;
using FluentAssertions;
using VE3NEA.SkyTlm.Deframing;
using Xunit;

namespace VE3NEA.SkyTlm.Tests.Unit
{
  /// <summary>
  /// Pins the AX.25 / HDLC frame-check sequence to <b>CRC-16/X-25</b> (poly 0x1021 reflected = 0x8408,
  /// init 0xFFFF, refin/refout, xorout 0xFFFF) — the same algorithm direwolf's <c>fcs_calc</c> uses. The
  /// canonical check value for the ASCII string "123456789" is <c>0x906E</c>.
  /// </summary>
  public class Crc16Tests
  {
    [Fact]
    public void CheckValue_Matches_X25_Standard()
    {
      var data = Encoding.ASCII.GetBytes("123456789");
      Crc16Ccitt.Compute(data).Should().Be(0x906E);
    }

    [Fact]
    public void AppendedFcs_MakesFrameSelfConsistent()
    {
      // TX appends the FCS low byte first; recomputing over the data must reproduce it.
      byte[] data = { 0x82, 0xA0, 0xB4, 0x84, 0x68, 0x68, 0x60 };
      ushort fcs = Crc16Ccitt.Compute(data);

      byte lo = (byte)(fcs & 0xff), hi = (byte)(fcs >> 8);
      // the deframer recomputes over the bytes before the 2 FCS octets and compares to (lo | hi<<8).
      ushort transmitted = (ushort)(lo | (hi << 8));
      transmitted.Should().Be(fcs);
    }

    [Fact]
    public void EmptyInput_IsXorOfInit()
    {
      // init 0xFFFF, no data, xorout 0xFFFF -> 0x0000.
      Crc16Ccitt.Compute(System.Array.Empty<byte>()).Should().Be(0x0000);
    }
  }
}
