using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using VE3NEA.SkyTlm.Core;
using VE3NEA.SkyTlm.Imaging;
using VE3NEA.SkyTlm.Imaging.RawJpeg;
using VE3NEA.SkyTlm.Tests.Regression;
using Xunit;

namespace VE3NEA.SkyTlm.Tests.Unit
{
  /// <summary>
  /// USP file transfer — the second front-end onto <see cref="RawJpegAssembler"/>, covering the Sputnix
  /// birds (Luca / RS90S, 239Alferov / RS61S, HyperView-1G / RS66S). They send pictures down the ordinary
  /// telemetry downlink as file transfers, which is why none of them advertises an imaging transmitter.
  /// <para>
  /// <b>Every frame here is synthesised.</b> No <c>FILETRANSFER_*</c> message has ever been captured off
  /// air — every USP frame in the corpus is telemetry — so these tests pin the port against SatsDecoder's
  /// structs and prove the assembler behaves, and they cannot pin the field widths against reality. That
  /// is the phase's stated limitation, not an oversight.
  /// </para>
  /// </summary>
  public class UspFileTransferTests
  {
    private const int MessageInit = 0x0C20, MessageFileSize = 0x0C2B, MessageData = 0x0C24;
    private const int BlockLen = 48;

    private static byte[] KnownJpeg(string name = "hades-sa_img235_complete") =>
      File.ReadAllBytes(Path.Combine(TestPaths.DataDir, "Ssdv", name + ".ref.jpg"));

    /// <summary>One USP <c>Data</c> message: message/sender/receiver/size as u16 LE, then the payload.</summary>
    private static byte[] Message(int message, ReadOnlySpan<byte> payload)
    {
      var bytes = new byte[8 + payload.Length];
      BitConverter.GetBytes((ushort)message).CopyTo(bytes, 0);
      BitConverter.GetBytes((ushort)0x1234).CopyTo(bytes, 2);   // sender, which imaging ignores
      BitConverter.GetBytes((ushort)0x5678).CopyTo(bytes, 4);   // receiver, likewise
      BitConverter.GetBytes((ushort)payload.Length).CopyTo(bytes, 6);
      payload.CopyTo(bytes.AsSpan(8));
      return bytes;
    }

    private static byte[] Init(int session, string fileName)
    {
      var payload = new byte[10 + fileName.Length];
      payload[0] = 0;                                            // mode: Receive
      payload[1] = (byte)session;
      BitConverter.GetBytes((ushort)BlockLen).CopyTo(payload, 2);
      Encoding.UTF8.GetBytes(fileName).CopyTo(payload, 10);
      return Message(MessageInit, payload);
    }

    private static byte[] FileSize(int size)
    {
      var payload = new byte[4];
      BitConverter.GetBytes(size).CopyTo(payload, 0);
      return Message(MessageFileSize, payload);
    }

    private static byte[] Data(int session, int offset, ReadOnlySpan<byte> data)
    {
      var payload = new byte[5 + data.Length];
      payload[0] = (byte)session;
      BitConverter.GetBytes(offset).CopyTo(payload, 1);
      data.CopyTo(payload.AsSpan(5));
      return Message(MessageData, payload);
    }

    /// <summary>Wrap messages in an AX.25 UI frame, which is how USP travels.</summary>
    private static Frame Ax25(params byte[][] messages)
    {
      var header = new byte[16];
      for (int i = 0; i < 12; i++) header[i] = (byte)('A' << 1);
      header[6] = 0x60;                       // destination SSID, end-of-address clear
      header[13] = 0x61;                      // source SSID with the end-of-address bit set
      header[14] = 0x00;                      // control, as every real USP frame carries it — not 0x03
      header[15] = 0xF0;                      // no layer 3
      return new Frame
      {
        Bytes = [.. header, .. messages.SelectMany(m => m)],
        CrcValid = true,
        Framing = Framing.USP
      };
    }

    /// <summary>A whole file as a session would send it: announce, state the size, then the blocks.</summary>
    private static List<Frame> Transfer(byte[] file, int session = 3, string name = "img.jpg")
    {
      List<Frame> frames = [Ax25(Init(session, name), FileSize(file.Length))];
      for (int at = 0; at < file.Length; at += BlockLen)
        frames.Add(Ax25(Data(session, at, file.AsSpan(at, Math.Min(BlockLen, file.Length - at)))));
      return frames;
    }

    private static (RawJpegAssembler A, List<ImageProduct> Updates, List<ImageProduct> Done) Assembler()
    {
      var a = new RawJpegAssembler(RawJpegSource.Usp);
      List<ImageProduct> updates = [], done = [];
      a.ImageUpdated += updates.Add;
      a.ImageCompleted += done.Add;
      return (a, updates, done);
    }


    // ---- message parsing ---------------------------------------------------------------------------

    [Fact]
    public void ADataMessage_YieldsItsOffsetAndBytes()
    {
      var frame = Ax25(Data(3, 0x1234, [1, 2, 3, 4]));

      var fragments = RawJpegSource.Usp.Extract(frame);

      var f = fragments.Should().ContainSingle().Subject;
      f.Offset.Should().Be(0x1234);
      f.Data.Should().Equal([1, 2, 3, 4]);
      f.Key.Sender.Should().Be(3, "the session ID is what identifies one transfer");
      f.IsAnnouncement.Should().BeFalse();
    }

    [Fact]
    public void AnInitMessage_YieldsTheFileNameAndStartsATransfer()
    {
      var frame = Ax25(Init(7, "photo_001.jpg"));

      var f = RawJpegSource.Usp.Extract(frame).Should().ContainSingle().Subject;
      f.Name.Should().Be("photo_001.jpg");
      f.IsStart.Should().BeTrue();
      f.IsAnnouncement.Should().BeTrue("INIT announces a transfer, it does not carry any of it");
      f.Key.Sender.Should().Be(7);
    }

    [Fact]
    public void ANulPaddedFileName_IsTrimmed()
    {
      var frame = Ax25(Init(1, "a.jpg\0\0\0\0\0"));

      RawJpegSource.Usp.Extract(frame)[0].Name.Should().Be("a.jpg");
    }

    [Fact]
    public void AFileSizeMessage_YieldsTheTotalAndNamesNoSession()
    {
      var f = RawJpegSource.Usp.Extract(Ax25(FileSize(4096))).Should().ContainSingle().Subject;

      f.TotalSize.Should().Be(4096);
      f.IsAnnouncement.Should().BeTrue();
      f.Key.Should().Be(UspFileTransfer.AnySession, "FILESIZE carries no session ID of its own");
    }

    [Fact]
    public void OneFrame_CanCarrySeveralMessages()
    {
      // the reason extraction returns a list: USP packs a run of Data messages into one info field.
      var frame = Ax25(Init(3, "x.jpg"), FileSize(128), Data(3, 0, [0xFF, 0xD8, 0x01]));

      var fragments = RawJpegSource.Usp.Extract(frame);

      fragments.Should().HaveCount(3);
      fragments[0].Name.Should().Be("x.jpg");
      fragments[1].TotalSize.Should().Be(128);
      fragments[2].Data.Should().Equal([0xFF, 0xD8, 0x01]);
    }

    [Fact]
    public void TelemetryMessages_AreSkippedAndTheRestStillParses()
    {
      // 0x000E is REGULAR_COMMON, one of the many telemetry IDs that share this stream.
      var frame = Ax25(Message(0x000E, new byte[20]), Data(3, 64, [9, 9, 9]));

      var f = RawJpegSource.Usp.Extract(frame).Should().ContainSingle().Subject;
      f.Offset.Should().Be(64);
    }

    [Fact]
    public void APureTelemetryFrame_YieldsNothing() =>
      RawJpegSource.Usp.Extract(Ax25(Message(0x000E, new byte[20]), Message(0x4246, new byte[12])))
        .Should().BeEmpty("no FILETRANSFER message means no image, which is every frame captured so far");


    // ---- the gate ----------------------------------------------------------------------------------

    [Theory]
    [InlineData(0x00)]   // what all seven real frames carry
    [InlineData(0x03)]   // the AX.25 UI value the gate used to demand
    [InlineData(0x13)]   // and anything else a future build might emit
    public void TheControlByte_IsNotChecked(byte control)
    {
      // Requiring 0x03 here rejected every USP frame ever received. The octet is filler on this downlink,
      // so the PID and the exact-consumption walk do the gating instead.
      var frame = Ax25(Data(3, 0, [1, 2, 3]));
      frame.Bytes[14] = control;

      RawJpegSource.Usp.Extract(frame).Should().ContainSingle();
    }

    [Fact]
    public void AFrameWithALayer3Protocol_IsIgnored()
    {
      var frame = Ax25(Data(3, 0, [1, 2, 3]));
      frame.Bytes[15] = 0xCC;   // some PID other than "no layer 3"

      RawJpegSource.Usp.Extract(frame).Should().BeEmpty();
    }

    [Fact]
    public void AnotherFramingEntirely_IsIgnored()
    {
      var frame = Ax25(Data(3, 0, [1, 2, 3])) with { Framing = Framing.GEOSCAN };

      RawJpegSource.Usp.Extract(frame).Should().BeEmpty();
    }

    [Fact]
    public void ATruncatedMessage_StopsTheWalkRatherThanReadingPastTheEnd()
    {
      var good = Ax25(Data(3, 0, [1, 2, 3]));
      var truncated = good with { Bytes = good.Bytes[..^2] };   // the last message now claims more than it has

      RawJpegSource.Usp.Extract(truncated).Should().BeEmpty();
    }

    [Fact]
    public void AnEmptyDataMessage_IsNotAFragment() =>
      RawJpegSource.Usp.Extract(Ax25(Data(3, 0, []))).Should().BeEmpty("there are no file bytes in it");

    [Fact]
    public void ATrailingPartialMessage_RejectsTheWholeFrame()
    {
      // The structural check that took the control byte's place: a real info field is a whole number of
      // messages, so a few octets left dangling mean this was never one, and the walk is not evidence.
      var good = Ax25(Data(3, 0, [1, 2, 3]));
      var padded = good with { Bytes = [.. good.Bytes, (byte)0, (byte)0, (byte)0] };

      RawJpegSource.Usp.Extract(padded).Should().BeEmpty();
    }


    // ---- real off-air frames -----------------------------------------------------------------------

    /// <summary>The pinned CRC-OK frames from <c>Data/usp_telemetry_regression.json</c> — all telemetry,
    /// and the only USP frames anyone has ever captured.</summary>
    private static List<byte[]> RealUspFrames()
    {
      string path = Path.Combine(TestPaths.ProjectRoot, "Data", "usp_telemetry_regression.json");
      using var doc = JsonDocument.Parse(File.ReadAllText(path));
      return [.. doc.RootElement.GetProperty("frames").EnumerateArray()
                  .Select(f => Convert.FromHexString(f.GetProperty("hex").GetString()!))];
    }

    [Fact]
    public void EveryRealFrame_YieldsNoFragmentsAndIsConsumedExactly()
    {
      var frames = RealUspFrames();
      frames.Should().HaveCountGreaterThanOrEqualTo(6);

      foreach (var bytes in frames)
      {
        bytes[14].Should().Be(0x00, "the corpus is what showed the 0x03 control gate to be wrong");

        var frame = new Frame { Bytes = bytes, CrcValid = true, Framing = Framing.USP };
        RawJpegSource.Usp.Extract(frame)
          .Should().BeEmpty("every USP frame captured so far is telemetry, not a file transfer");

        // and the walk really did reach the end of the genuine info field: a DATA message appended to it
        // is only found when the telemetry messages ahead of it were consumed exactly.
        var extended = frame with { Bytes = [.. bytes, .. Data(3, 0x2000, [7, 7, 7])] };
        RawJpegSource.Usp.Extract(extended).Should().ContainSingle()
          .Which.Offset.Should().Be(0x2000);
      }
    }


    // ---- assembling --------------------------------------------------------------------------------

    [Fact]
    public void AWholeTransfer_RebuildsTheFileExactly()
    {
      var jpeg = KnownJpeg();
      var (a, _, done) = Assembler();

      foreach (var f in Transfer(jpeg)) a.Push(f);

      var final = done.Should().ContainSingle().Subject;
      final.Jpeg.Should().Equal(jpeg);
      final.Complete.Should().BeTrue();
      final.Source.Should().Be("img.jpg", "USP's useful label is the file name, not the session number");
      final.Width.Should().Be(320);
      final.Height.Should().Be(240);
      final.FirstGapOffset.Should().Be(jpeg.Length);
      a.FragmentsRejected.Should().Be(0);
    }

    [Fact]
    public void TheAnnouncedSize_DecidesCompletenessWithoutLookingForAnEoi()
    {
      // FILESIZE turns "complete" from an inference into a fact, which matters because a JPEG's own EOI
      // can be missing while the file is whole, and can appear inside data while it is not.
      var jpeg = KnownJpeg();
      var (a, updates, _) = Assembler();

      foreach (var f in Transfer(jpeg).SkipLast(1)) a.Push(f);
      updates[^1].Complete.Should().BeFalse("the announced size has not been reached");

      a.Push(Transfer(jpeg)[^1]);
      updates[^1].Complete.Should().BeTrue();
    }

    [Fact]
    public void AnInitWithNoDataFollowing_IsNotAnnouncedAsAnImage()
    {
      var (a, updates, done) = Assembler();

      a.Push(Ax25(Init(3, "nothing.jpg"), FileSize(9000)));
      a.Flush();

      updates.Should().BeEmpty("nothing was received to show");
      done.Should().BeEmpty("and an empty transfer is not a picture");
    }

    [Fact]
    public void ANonImageFile_IsNotReconstructed()
    {
      // USP moves logs and configs down the same channel. A name we cannot render means the bytes are
      // dropped rather than buffered and offered as a broken JPEG.
      var (a, updates, done) = Assembler();

      a.Push(Ax25(Init(4, "events.log")));
      foreach (var f in Transfer(KnownJpeg(), session: 4, name: "events.log").Skip(1)) a.Push(f);
      a.Flush();

      updates.Should().BeEmpty();
      done.Should().BeEmpty();
      a.FragmentsAccepted.Should().Be(0);
    }

    [Fact]
    public void AnUnnamedTransfer_IsStillAttempted()
    {
      // the INIT may simply have been missed. A transfer with no name is treated as a picture, which is
      // what SatsDecoder does too — it files them under USP_unknown.
      var jpeg = KnownJpeg();
      var (a, _, done) = Assembler();

      foreach (var f in Transfer(jpeg).Skip(1)) a.Push(f);
      a.Flush();

      var final = done.Should().ContainSingle().Subject;
      final.Jpeg.Should().Equal(jpeg);
      final.Source.Should().BeNull("no name was announced, so there is nothing to show");
    }

    [Fact]
    public void ANewSession_CompletesThePreviousTransfer()
    {
      var jpeg = KnownJpeg();
      var (a, _, done) = Assembler();

      foreach (var f in Transfer(jpeg, session: 3, name: "first.jpg")) a.Push(f);
      foreach (var f in Transfer(jpeg, session: 4, name: "second.jpg").Take(4)) a.Push(f);
      a.Flush();

      done.Select(p => p.Source).Should().Equal("first.jpg", "second.jpg");
    }

    [Fact]
    public void ALostBlock_LeavesAHoleRatherThanTruncatingTheFile()
    {
      // The policy changed on 2026-09-13 with the first real imaging capture: a baseline JPEG survives
      // a hole, which costs a colour shift below it and nothing else, and truncating at the first gap
      // threw away most of a nearly complete picture to avoid that. FirstGapOffset stays as the honesty
      // metric the UI shows; it is no longer where the file is cut.
      // the dropped block must land past the 623-byte header: a hole in the header is a different
      // failure entirely and emits nothing at all — see RawJpegHeaderGateTests.
      const int lost = 13;                                           // covers [624, 672), clear of the header
      var jpeg = KnownJpeg();
      var frames = Transfer(jpeg);
      var (a, _, done) = Assembler();

      foreach (var f in frames.Where((_, i) => i != lost + 1)) a.Push(f);   // index 0 is the announcement
      a.Flush();

      var final = done.Should().ContainSingle().Subject;
      final.Complete.Should().BeFalse();
      final.FirstGapOffset.Should().Be(lost * BlockLen, "that is still where truth stops");
      // Superseded on 2026-09-14 by the entropy repair (design-docs/imaging-improvement-plan.md, B5):
      // the scan is re-encoded rather than copied, so the rest is where it belongs in MCUs rather than in
      // bytes, and the hole reads as neutral gray rather than as zero. JpegEntropyWalkerTests asserts that
      // equivalence coefficient for coefficient; what is left here is that the file is not truncated.
      final.Jpeg.Length.Should().BeGreaterThan(lost * BlockLen, "but the whole file is emitted");
      final.Jpeg.Take(RawJpegAssemblerTests.FirstScanStart).Should()
        .Equal(jpeg.Take(RawJpegAssemblerTests.FirstScanStart), "the header is the file's own");
    }

    [Fact]
    public void BlocksLandAtTheirFileOffsetEvenWhenTheStartIsMissed()
    {
      // USP offsets are file offsets, as SatsDecoder's straight-through push_data shows. A transfer
      // joined in progress must leave the missing head as a hole rather than sliding down to zero.
      var jpeg = KnownJpeg();
      var frames = Transfer(jpeg);
      var (a, updates, _) = Assembler();

      // frames[0] is the announcement, so this joins the transfer at block 4 — no INIT, no FILESIZE
      foreach (var f in frames.Skip(5)) a.Push(f);
      updates[^1].FirstGapOffset.Should().Be(0, "the first four blocks are missing");

      // and now the blocks that were missed, without a second INIT — a repeated INIT would mean a new
      // transfer, which is a different case from a gap being filled
      foreach (var f in frames.Skip(1).Take(4)) a.Push(f);
      updates[^1].Jpeg.Should().Equal(jpeg, "the head slotted in front of data that never moved");
    }

    [Fact]
    public void OutOfOrderBlocks_StillLandInTheRightPlace()
    {
      var jpeg = KnownJpeg();
      var frames = Transfer(jpeg);
      var (a, _, done) = Assembler();

      a.Push(frames[0]);
      foreach (var f in frames.Skip(1).Reverse()) a.Push(f);

      done.Should().ContainSingle().Which.Jpeg.Should().Equal(jpeg);
    }


    // ---- dispatch ----------------------------------------------------------------------------------

    [Fact]
    public void Factory_BuildsARawJpegAssemblerForUsp() =>
      ImageAssemblerFactory.Create(
        new SignalParams(Baud: 9600, Modulation.GMSK, Framing.USP, SampleRate: 48000, Deviation: null),
        98449).Should().BeOfType<RawJpegAssembler>();
  }
}
