using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using VE3NEA.SkyTlm.Imaging.Ssdv;
using VE3NEA.SkyTlm.Tests.Fixtures;
using VE3NEA.SkyTlm.Tests.Regression;
using Xunit;

namespace VE3NEA.SkyTlm.Tests.Unit
{
  /// <summary>
  /// Image accumulation: ordering, de-duplication, coverage and image-boundary detection against
  /// synthetic packet streams, then the whole packet layer end to end over the off-air HADES-SA packets
  /// in <c>Data/Ssdv</c> (2026-07-30 passes — see <c>ssdv-research.md</c> §13).
  /// </summary>
  public class SsdvImageTests
  {
    private static readonly SsdvVariant V = SsdvVariant.Standard256;

    private static SsdvPacket Packet(int packetId, int imageId = 5, bool eoi = false, string callsign = "VE3NEA")
    {
      var raw = SsdvTx.Build(V, SsdvTx.Payload(V, seed: packetId + 1), imageId, packetId, callsign, eoi: eoi);
      SsdvPacket.TryParse(raw, V, out var p).Should().BeTrue();
      return p!;
    }

    /// <summary>Concatenated canonical 256-byte packets, sorted by packet ID — the exact byte stream
    /// fsphil's <c>ssdv -d</c> consumes, and how the off-air fixtures are stored.</summary>
    private static SsdvPacket[] ReadFixture(string name)
    {
      var bytes = File.ReadAllBytes(Path.Combine(TestPaths.DataDir, "Ssdv", name));
      (bytes.Length % 256).Should().Be(0, "the fixture is whole 256-byte packets");

      return Enumerable.Range(0, bytes.Length / 256).Select(i =>
      {
        SsdvPacket.TryParse(bytes.AsSpan(i * 256, 256), SsdvVariant.HadesSa251, out var p)
          .Should().BeTrue($"packet {i} of {name} was validated when it was captured");
        return p!;
      }).ToArray();
    }


    // ---- one image ---------------------------------------------------------------------------------

    [Fact]
    public void Packets_AreOrderedByPacketId_WhateverOrderTheyArriveIn()
    {
      var img = new SsdvImage(Packet(3));
      foreach (int id in new[] { 1, 4, 0, 2 }) img.Add(Packet(id));

      img.Packets.Select(p => p.PacketId).Should().Equal(0, 1, 2, 3, 4);
      img.PacketsReceived.Should().Be(5);
    }

    [Fact]
    public void DuplicatePacket_IsIgnored()
    {
      var img = new SsdvImage(Packet(2));

      img.Add(Packet(2)).Should().BeFalse("every copy already passed the CRC — the first one is as good as any");
      img.PacketsReceived.Should().Be(1);
    }

    [Fact]
    public void PacketOfAnotherImage_IsRejected()
    {
      var img = new SsdvImage(Packet(0, imageId: 5));

      img.Invoking(i => i.Add(Packet(1, imageId: 6))).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void MissingPackets_AreReported()
    {
      var img = new SsdvImage(Packet(0));
      foreach (int id in new[] { 2, 5 }) img.Add(Packet(id));

      img.PacketsExpected.Should().Be(6, "without EOI the length is only known as far as the highest ID seen");
      img.MissingPacketIds.Should().Equal(1, 3, 4);
      img.IsComplete.Should().BeFalse();
    }

    [Fact]
    public void Eoi_FixesTheImageLength()
    {
      var img = new SsdvImage(Packet(0));
      img.Add(Packet(2, eoi: true));

      img.HasEoi.Should().BeTrue();
      img.EoiPacketId.Should().Be(2);
      img.PacketsExpected.Should().Be(3);
      img.MissingPacketIds.Should().Equal(1);
      img.IsComplete.Should().BeFalse();

      img.Add(Packet(1));
      img.MissingPacketIds.Should().BeEmpty();
      img.IsComplete.Should().BeTrue();
    }

    [Fact]
    public void Geometry_ComesFromTheHeader()
    {
      var img = new SsdvImage(Packet(0));

      img.Width.Should().Be(320);
      img.Height.Should().Be(240);
      img.Quality.Should().Be(7);
      img.Callsign.Should().Be("VE3NEA");
    }


    // ---- the image set -----------------------------------------------------------------------------

    [Fact]
    public void NewImageId_StartsANewImage()
    {
      var set = new SsdvImageSet();

      set.Add(Packet(0, imageId: 5), out var first, out bool isNew).Should().BeTrue();
      isNew.Should().BeTrue();
      set.Add(Packet(1, imageId: 5), out _, out isNew).Should().BeTrue();
      isNew.Should().BeFalse();

      set.Add(Packet(0, imageId: 6), out var second, out isNew).Should().BeTrue();
      isNew.Should().BeTrue("the sender has moved on");
      second.Should().NotBeSameAs(first);
      set.Current.Should().BeSameAs(second);
      set.Images.Should().HaveCount(2);
    }

    [Fact]
    public void LatePacketOfTheOldImage_StillLands()
    {
      // a retransmission or a late decode arrives after the sender has switched images; it must go to
      // its own image rather than be lost or corrupt the current one.
      var set = new SsdvImageSet();
      set.Add(Packet(0, imageId: 5), out var five, out _);
      set.Add(Packet(0, imageId: 6), out _, out _);

      set.Add(Packet(1, imageId: 5), out var late, out bool isNew).Should().BeTrue();
      isNew.Should().BeFalse();
      late.Should().BeSameAs(five);
      five.PacketsReceived.Should().Be(2);
    }

    [Fact]
    public void SameImageIdFromAnotherSender_IsADifferentImage()
    {
      // image IDs are 8 bits and repeat within hours, so the callsign field has to be part of the key.
      var set = new SsdvImageSet();
      set.Add(Packet(0, imageId: 5, callsign: "VE3NEA"), out var a, out _);
      set.Add(Packet(0, imageId: 5, callsign: "MI0VIM"), out var b, out bool isNew);

      isNew.Should().BeTrue();
      b.Should().NotBeSameAs(a);
      set.Images.Should().HaveCount(2);
    }


    // ---- off-air fixtures --------------------------------------------------------------------------

    [Fact]
    public void OffAir_Image235_ParsesAndReportsItsGaps()
    {
      var packets = ReadFixture("hades-sa_img235_12of15.ssdv");
      var set = new SsdvImageSet();
      foreach (var p in packets) set.Add(p, out _, out _).Should().BeTrue();

      var img = set.Images.Should().ContainSingle().Subject;
      img.ImageId.Should().Be(235);
      img.Width.Should().Be(320);
      img.Height.Should().Be(240);
      img.Quality.Should().Be(7);
      img.PacketsReceived.Should().Be(12);

      img.HasEoi.Should().BeTrue("the pass caught the last packet of the image");
      img.EoiPacketId.Should().Be(14);
      img.PacketsExpected.Should().Be(15);
      img.MissingPacketIds.Should().Equal(0, 6, 10);
      img.IsComplete.Should().BeFalse("three packets of the fifteen were lost");
    }

    [Fact]
    public void OffAir_Image235_HasMonotoneMcuIndices()
    {
      // the MCU index must rise with the packet ID: it is what the transcoder uses to place a packet's
      // MCUs in the image and to size the gap before it, so a non-monotone sequence would mean the
      // header parse (or the ordering) is wrong.
      var img = new SsdvImage(ReadFixture("hades-sa_img235_12of15.ssdv")[0]);
      foreach (var p in ReadFixture("hades-sa_img235_12of15.ssdv").Skip(1)) img.Add(p);

      var mcu = img.Packets.Select(p => p.McuIndex).ToList();
      mcu.Should().Equal(20, 37, 56, 75, 95, 134, 154, 173, 219, 240, 260, 282);
      mcu.Should().BeInAscendingOrder().And.OnlyContain(i => i >= 0 && i < 300,
        "320×240 is 20×15 = 300 MCUs");
      img.Packets.Should().OnlyContain(p => p.McuOffset >= 0 && p.McuOffset < 205);
    }

    [Theory]
    [InlineData("hades-sa_img236_10pkt.ssdv", 236, 10)]
    [InlineData("hades-sa_img226_5pkt.ssdv", 226, 5)]
    public void OffAir_PartialImages_ParseWithoutEoi(string file, int imageId, int count)
    {
      var packets = ReadFixture(file);
      var set = new SsdvImageSet();
      foreach (var p in packets) set.Add(p, out _, out _);

      var img = set.Images.Should().ContainSingle().Subject;
      img.ImageId.Should().Be(imageId);
      img.PacketsReceived.Should().Be(count);
      img.HasEoi.Should().BeFalse("neither pass caught the last packet");
      img.IsComplete.Should().BeFalse();
      img.Packets.Should().OnlyContain(p => p.Width == 320 && p.Height == 240 && p.Quality == 7);
    }

    [Fact]
    public void OffAir_EveryFixturePacket_IsTheHadesConstantCallsignField()
    {
      var all = new[] { "hades-sa_img235_12of15.ssdv", "hades-sa_img236_10pkt.ssdv", "hades-sa_img226_5pkt.ssdv" }
        .SelectMany(ReadFixture).ToList();

      all.Should().HaveCount(27);
      all.Should().OnlyContain(p => p.CallsignCode == 0xBF35FBA3,
        "the field is AMSAT-EA's constant sync/size/address overload, so it keys every HADES-SA image alike");
      all.Should().OnlyContain(p => p.CorrectedBytes == 0,
        "the fixtures were written out after RS repair, so they re-parse CRC-clean");
    }
  }
}
