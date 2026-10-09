using System;
using System.Linq;
using System.Text;
using FluentAssertions;
using VE3NEA.SkyTlm.Core;
using VE3NEA.SkyTlm.Deframing;
using VE3NEA.SkyTlm.Imaging;
using VE3NEA.SkyTlm.Imaging.Ssdv;
using Xunit;

namespace VE3NEA.SkyTlm.Tests.Unit
{
  /// <summary>
  /// AO-123 / ASRTU-1 SSDV: a DSLWP packet in the data field of a VC1 frame. Anchored on a real frame —
  /// image 55, packet 11, received by VE3NEA on 2026-10-09 12:56:39Z — rather than on the test encoder,
  /// because the encoder was written from the same reading of the format as the parser, and the earlier
  /// reading (no CRC, 209-byte payload) was wrong in a way only real packets could show.
  /// </summary>
  public class Ao123Tests
  {
    private const string OffAirFrame =
      "03220c0bff37000b1e16003700adf728bbe3907fcbc47ffc50fd6b9faea2585834b60ac44d6e7ceb47efb7fbbfd2b1b50896" +
      "544be8576a4a71228fe07ee3f1eb568e883e87e3e8a28ae73d80a28a2800a28a2800a28a2800a28a2800a28a2800a28a2800" +
      "a28a2800a28a2800a28a2800a28a2800a28a2800a28a2800a28a2800a28a2800a28a2800a28a2800a28a5a004aec3c35a279" +
      "2ab7f72bfbc619894ff08f5fad67f86f45fb64a2ee75ff478cfcaa7f8dbfc0576a4e07359ce5d11cd5aa7d94248eb146d239" +
      "da8a32c4f615e6fa9df36a37f2dc3670c70a3de4f09735";

    private static byte[] OffAir() => Convert.FromHexString(OffAirFrame);

    // a VC1 "digital callsign" beacon, text as recorded in research §6.2, zero-filled to the frame size
    private static byte[] BeaconFrame()
    {
      var frame = new byte[Asrtu1Header.FrameLen];
      new byte[] { 0x03, 0x22, 0x10, 0x10, 0xFF }.CopyTo(frame, 0);
      Encoding.ASCII.GetBytes("asrtu:1320112ACFE5A723").CopyTo(frame, Asrtu1Header.HeaderLen);
      return frame;
    }

    [Fact]
    public void OffAirPacket_PassesTheDslwpCrc()
    {
      var packet = Asrtu1Header.ExtractSsdv(OffAir());

      packet.Should().NotBeNull();
      SsdvPacket.TryParse(packet, SsdvVariant.Ao123, out var p).Should().BeTrue();
      p!.ImageId.Should().Be(55);
      p.PacketId.Should().Be(11);
      p.Width.Should().Be(480);
      p.Height.Should().Be(352);
      p.Quality.Should().Be(4);
      p.McuOffset.Should().Be(55);
      p.McuIndex.Should().Be(173);
      p.Payload.Length.Should().Be(205, "the last four bytes of the data field are the CRC, not scan data");
    }

    [Fact]
    public void Beacon_IsNotImageData()
    {
      Asrtu1Header.ExtractSsdv(BeaconFrame()).Should().BeNull("a VC1 data field failing the CRC is a beacon");
    }

    [Fact]
    public void OtherChannels_AreNotImageData()
    {
      var frame = OffAir();
      frame[1] = 0x24;   // VC2
      Asrtu1Header.ExtractSsdv(frame).Should().BeNull();
    }

    [Fact]
    public void Describe_NamesTheHeaderAndThePacket()
    {
      var fields = Asrtu1Header.Describe(OffAir()).ToDictionary(f => f.Name, f => f.Value);

      fields["Spacecraft ID"].Should().Be("50 (ASRTU-1)");
      fields["Virtual channel"].Should().Be("1 (SSDV and beacons)");
      fields["Frame count"].Should().Be("master 12, channel 11");
      fields["First header pointer"].Should().Be("0xFF (none)");
      fields["SSDV image"].Should().Be("55, packet 11");
      fields["MCU"].Should().Be("index 173 of 660, offset 55");
    }

    [Fact]
    public void Describe_ShowsTheBeaconText() =>
      Asrtu1Header.Describe(BeaconFrame()).Should().Contain(("Beacon", "asrtu:1320112ACFE5A723"));

    [Fact]
    public void Factory_SelectsAo123ByNoradOnly()
    {
      var p = new SignalParams(9600, Modulation.BPSK, Framing.CCSDS, 48000);

      ImageAssemblerFactory.Create(p, Asrtu1Header.NoradId).Should().BeOfType<SsdvImageAssembler>();
      ImageAssemblerFactory.Create(p, 12345).Should().BeNull("CCSDS alone does not mean images");
    }

    [Fact]
    public void Assembler_AcceptsTheOffAirFrame()
    {
      var assembler = (SsdvImageAssembler)ImageAssemblerFactory.Create(
        new SignalParams(9600, Modulation.BPSK, Framing.CCSDS, 48000), Asrtu1Header.NoradId)!;
      ImageProduct? product = null;
      assembler.ImageUpdated += x => product = x;

      assembler.Push(new Frame { Bytes = OffAir(), CrcValid = true, Framing = Framing.CCSDS });

      assembler.PacketsAccepted.Should().Be(1);
      product!.ImageId.Should().Be(55);
      product.FragmentFormat.Should().Be("Dslwp218", "a packet with its own CRC can be archived for merging");
    }
  }
}
