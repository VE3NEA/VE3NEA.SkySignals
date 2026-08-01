using System;
using FluentAssertions;
using VE3NEA.SkyTlm.Deframing;
using Xunit;

namespace VE3NEA.SkyTlm.Tests.Unit
{
  /// <summary>
  /// The AX.25 address field as the panel shows it. The roundtrip tests already cover the ordinary
  /// space-padded case end to end; what is pinned here is the padding, which is where real satellites
  /// depart from the standard.
  /// </summary>
  public class Ax25AddressTests
  {
    /// <summary>An address field from callsigns and SSIDs, each character shifted left one bit, padded to
    /// six characters with <paramref name="pad"/>.</summary>
    private static byte[] Field(string dest, string src, char pad = ' ')
    {
      var bytes = new byte[16];
      for (int i = 0; i < 6; i++) bytes[i] = (byte)((i < dest.Length ? dest[i] : pad) << 1);
      bytes[6] = 0x60;
      for (int i = 0; i < 6; i++) bytes[7 + i] = (byte)((i < src.Length ? src[i] : pad) << 1);
      bytes[13] = 0xE1;
      bytes[14] = 0x03;
      bytes[15] = 0xF0;
      return bytes;
    }

    [Fact]
    public void ASpacePaddedCallsign_Parses()
    {
      Ax25Address.Describe(Field("BEACON", "RS92S4")).Should().Be("RS92S4 -> BEACON");
    }

    [Fact]
    public void ANulPaddedCallsign_ParsesToo()
    {
      // several of the Geoscan fleet pad with NULs rather than spaces; the address is no less readable
      Ax25Address.Describe(Field("BEACON", "RS61S", '\0')).Should().Be("RS61S -> BEACON");
    }

    [Fact]
    public void ARealNulPaddedBeacon_Parses()
    {
      var beacon = Convert.FromHexString(
        "848A82869E9C60A4A66C62A600E103F0019C076D6A0304380036003C106B200200000070000000" +
        "537500F4030E0B0100000000FF000000000DDD209A03A18F5E71270100F7000000");

      Ax25Address.Describe(beacon).Should().Be("RS61S -> BEACON");
      Ax25Address.AddressFieldLength(beacon).Should().Be(14);
    }

    [Fact]
    public void PaddingAlone_IsNotAnAddress()
    {
      // an all-NUL (or all-space) subfield has no callsign in it, whatever its SSID octet says
      Ax25Address.Describe(new byte[16]).Should().BeNull();
      Ax25Address.Describe(Field("BEACON", "", '\0')).Should().BeNull();
    }

    [Fact]
    public void AnImplausibleCharacter_IsRejected()
    {
      Ax25Address.Describe(Field("BEACON", "RS61!")).Should().BeNull();
    }
  }
}
