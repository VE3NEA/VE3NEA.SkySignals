using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using VE3NEA.SkyTlm.Core;
using VE3NEA.SkyTlm.Imaging;
using VE3NEA.SkyTlm.Imaging.Ssdv;
using VE3NEA.SkyTlm.Tests.Regression;
using Xunit;

namespace VE3NEA.SkyTlm.Tests.Unit
{
  /// <summary>
  /// The assembler: frames in, progressively-filling JPEGs out. Driven by the off-air fixtures turned
  /// back into the on-air frames a deframer would emit, so the whole chain from
  /// <see cref="SsdvSource"/> through <see cref="SsdvTranscoder"/> is under test rather than mocked.
  /// </summary>
  public class SsdvImageAssemblerTests
  {
    /// <summary>The canonical fixture packets as HADES frames: the five constant lead bytes stripped
    /// again, wrapped exactly as <c>HadesDeframer</c> emits them (no CRC — the type-10 frame carries
    /// its own CRC-32 inside the SSDV packet, so the deframer reports null).</summary>
    private static List<Frame> Frames(string fixture)
    {
      var bytes = File.ReadAllBytes(Path.Combine(TestPaths.DataDir, "Ssdv", fixture + ".ssdv"));
      return Enumerable.Range(0, bytes.Length / 256)
        .Select(i => new Frame
        {
          Bytes = bytes.AsSpan(i * 256 + 5, 251).ToArray(),
          CrcValid = null,
          Framing = Framing.HADES
        })
        .ToList();
    }

    private static Frame Telemetry(int type = 1, int length = 30) => new()
    {
      // a HADES telemetry frame: same framing, same downlink, not an image
      Bytes = [(byte)(type << 4 | 3), .. new byte[length - 1]],
      CrcValid = true,
      Framing = Framing.HADES
    };


    // ---- the source gate ---------------------------------------------------------------------------

    [Fact]
    public void HadesSource_AcceptsType10AndReconstructsTheLeadBytes()
    {
      var frame = Frames("hades-sa_img235_complete")[0];

      SsdvSource.HadesSa.TryExtract(frame, out var packet).Should().BeTrue();
      packet.Should().HaveCount(256);
      packet.Take(5).Should().Equal([0x55, 0x66, 0xBF, 0x35, 0xFB], "the five constant bytes AMSAT-EA drops");
      packet.Skip(5).Should().Equal(frame.Bytes);
      SsdvPacket.TryParse(packet, SsdvVariant.HadesSa251, out _).Should().BeTrue();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(11)]   // CODEC2 voice, also a "special" type on the same downlink
    [InlineData(13)]   // PN9 link-quality pattern
    public void HadesSource_RejectsEveryOtherPacketType(int type) =>
      SsdvSource.HadesSa.TryExtract(Telemetry(type), out _).Should().BeFalse(
        "the image packets are interleaved with telemetry, so rejecting is the normal case");

    [Fact]
    public void HadesSource_RejectsAnotherFramingEntirely()
    {
      var alien = Frames("hades-sa_img235_complete")[0] with { Framing = Framing.AX25G3RUH };

      SsdvSource.HadesSa.TryExtract(alien, out _).Should().BeFalse();
    }

    [Fact]
    public void HadesSource_HasNoUsableSenderId() =>
      SsdvSource.HadesSa.HasSenderId.Should().BeFalse(
        "the callsign field carries the GENESIS sync, size and address constants, not a callsign");


    // ---- assembling --------------------------------------------------------------------------------

    [Fact]
    public void CompleteImage_IsAssembledAndAnnouncedOnce()
    {
      var a = new SsdvImageAssembler(SsdvSource.HadesSa);
      var updates = new List<ImageProduct>();
      var completions = new List<ImageProduct>();
      a.ImageUpdated += updates.Add;
      a.ImageCompleted += completions.Add;

      foreach (var f in Frames("hades-sa_img235_complete")) a.Push(f);

      updates.Should().HaveCount(15, "one per accepted packet");
      a.PacketsAccepted.Should().Be(15);
      a.PacketsRejected.Should().Be(0);

      var final = updates[^1];
      final.ImageId.Should().Be(235);
      final.Width.Should().Be(320);
      final.Height.Should().Be(240);
      final.FragmentsReceived.Should().Be(15);
      final.FragmentsExpected.Should().Be(15);
      final.Complete.Should().BeTrue();
      final.Source.Should().BeNull("HADES-SA has no usable sender ID");
      final.FirstGapOffset.Should().Be(-1, "a lost SSDV packet costs only its own MCUs");

      completions.Should().ContainSingle().Which.Should().BeSameAs(final);

      // and it really is the image, byte for byte
      final.Jpeg.Should().Equal(
        File.ReadAllBytes(Path.Combine(TestPaths.DataDir, "Ssdv", "hades-sa_img235_complete.ref.jpg")));
    }

    [Fact]
    public void EveryUpdateIsADecodableJpeg()
    {
      // the point of the progressive API: the UI can show any one of these, not just the last.
      var a = new SsdvImageAssembler(SsdvSource.HadesSa);
      var updates = new List<ImageProduct>();
      a.ImageUpdated += updates.Add;

      foreach (var f in Frames("hades-sa_img226_5pkt")) a.Push(f);

      updates.Should().HaveCount(5);
      updates.Should().OnlyContain(p => p.Jpeg.Length > 0);
      foreach (var p in updates)
      {
        p.Jpeg.Take(2).Should().Equal([0xFF, 0xD8]);
        p.Jpeg.TakeLast(2).Should().Equal([0xFF, 0xD9]);
      }
      updates.Select(p => p.FragmentsReceived).Should().Equal(1, 2, 3, 4, 5);
    }

    [Fact]
    public void IncompleteImage_IsAnnouncedOnlyByFlush()
    {
      var a = new SsdvImageAssembler(SsdvSource.HadesSa);
      var completions = new List<ImageProduct>();
      a.ImageCompleted += completions.Add;

      foreach (var f in Frames("hades-sa_img236_10pkt")) a.Push(f);
      completions.Should().BeEmpty("the image never received its last packet, so nothing said it was done");

      a.Flush();
      var done = completions.Should().ContainSingle().Subject;
      done.Complete.Should().BeFalse("'no more is coming' is not 'all of it is here'");
      done.FragmentsReceived.Should().Be(10);
    }

    [Fact]
    public void DuplicateFrames_AreIgnored()
    {
      var a = new SsdvImageAssembler(SsdvSource.HadesSa);
      int updates = 0;
      a.ImageUpdated += _ => updates++;

      var frames = Frames("hades-sa_img226_5pkt");
      foreach (var f in frames) a.Push(f);
      foreach (var f in frames) a.Push(f);

      updates.Should().Be(5, "a re-decode of a packet already held changes nothing");
      a.PacketsAccepted.Should().Be(5);
    }

    [Fact]
    public void CorruptFrame_IsCountedAsRejectedRatherThanAssembled()
    {
      var a = new SsdvImageAssembler(SsdvSource.HadesSa);
      var frames = Frames("hades-sa_img226_5pkt");
      var wrecked = (byte[])frames[1].Bytes.Clone();
      // far past what RS can repair, but leaving the type/address byte alone so the frame still looks
      // like an image packet — otherwise the source would drop it and nothing would be "rejected"
      for (int i = 1; i < 40; i++) wrecked[i * 5] ^= 0xFF;

      a.Push(frames[0]);
      a.Push(frames[1] with { Bytes = wrecked });

      a.PacketsAccepted.Should().Be(1);
      a.PacketsRejected.Should().Be(1, "it had the shape of an image packet but failed the CRC");
    }

    [Fact]
    public void NewImageId_CompletesThePreviousImage()
    {
      // the sender moves on; whatever it was sending gets nothing further and must be announced.
      var a = new SsdvImageAssembler(SsdvSource.HadesSa);
      var completions = new List<ImageProduct>();
      a.ImageCompleted += completions.Add;

      foreach (var f in Frames("hades-sa_img226_5pkt")) a.Push(f);
      foreach (var f in Frames("hades-sa_img236_10pkt")) a.Push(f);

      completions.Should().ContainSingle().Which.ImageId.Should().Be(226);
      completions[0].Complete.Should().BeFalse();

      a.Flush();
      completions.Select(p => p.ImageId).Should().Equal(226, 236);
    }

    [Fact]
    public void TelemetryFrames_PassThroughWithoutDisturbingAnything()
    {
      // Push is fed every frame the deframer produces, so interleaved telemetry must be inert.
      var a = new SsdvImageAssembler(SsdvSource.HadesSa);
      var updates = new List<ImageProduct>();
      a.ImageUpdated += updates.Add;

      foreach (var f in Frames("hades-sa_img235_complete"))
      {
        a.Push(Telemetry());
        a.Push(f);
      }

      a.PacketsRejected.Should().Be(0, "a telemetry frame is not a failed image packet");
      updates[^1].Jpeg.Should().Equal(
        File.ReadAllBytes(Path.Combine(TestPaths.DataDir, "Ssdv", "hades-sa_img235_complete.ref.jpg")));
    }

    [Fact]
    public void FlushOnAnEmptyAssembler_IsHarmless()
    {
      var a = new SsdvImageAssembler(SsdvSource.HadesSa);
      int completions = 0;
      a.ImageCompleted += _ => completions++;

      a.Flush();
      completions.Should().Be(0);
    }


    // ---- dispatch ----------------------------------------------------------------------------------

    private static SignalParams Params(Framing framing) =>
      new(Baud: 800, Modulation.FSK, framing, SampleRate: 48000, Deviation: 800);

    [Fact]
    public void Factory_BuildsAnAssemblerForHades() =>
      ImageAssemblerFactory.Create(Params(Framing.HADES), 68446)
        .Should().BeOfType<SsdvImageAssembler>();

    [Theory]
    [InlineData(Framing.AX25G3RUH)]
    [InlineData(Framing.USP)]
    [InlineData(Framing.GEOSCAN)]
    [InlineData(Framing.AO40FEC)]
    public void Factory_ReturnsNullForFramingsWithNoImagingYet(Framing framing) =>
      ImageAssemblerFactory.Create(Params(framing)).Should().BeNull();
  }
}
