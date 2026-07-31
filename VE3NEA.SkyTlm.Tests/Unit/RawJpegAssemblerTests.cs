using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using VE3NEA.SkyTlm.Core;
using VE3NEA.SkyTlm.Imaging;
using VE3NEA.SkyTlm.Imaging.RawJpeg;
using VE3NEA.SkyTlm.Tests.Regression;
using Xunit;

namespace VE3NEA.SkyTlm.Tests.Unit
{
  /// <summary>
  /// The raw-JPEG family: Geoscan's application layer and the assembler behind it. There is no off-air
  /// capture with images in it — the Geoscan fleet images in short scheduled campaigns and every frame we
  /// or SatNOGS have caught is telemetry — so the positive tests drive a known JPEG through the real
  /// frame layout, and the negative tests use 23 genuine off-air frames.
  /// <para>
  /// That split is deliberate. Chopping a JPEG into fragments proves the assembler puts bytes back where
  /// they came from, which is all this family does; the thing synthetic data cannot prove is that the
  /// gate rejects what it should, and that is exactly what the real frames are for.
  /// </para>
  /// </summary>
  public class RawJpegAssemblerTests
  {
    private const int FragmentLen = 64;         // Geoscan v1 with dlen = 70, the only value seen off air
    private const int AddressBase = 0x02_4000;  // absolute offsets are in the satellite's address space

    private static byte[] KnownJpeg(string name = "jy1sat_img9") =>
      File.ReadAllBytes(Path.Combine(TestPaths.DataDir, "Ssdv", name + ".ref.jpg"));

    /// <summary>One Geoscan v1 frame: 8-byte header, payload, zero-padded to 72 bytes as the modem
    /// sends it. <c>dlen</c> counts the payload plus the six header bytes after <c>dlen</c> itself.</summary>
    private static Frame V1(int satNum, int mtype, int offset, ReadOnlySpan<byte> data)
    {
      var bytes = new byte[72];
      bytes[0] = (byte)satNum;
      bytes[1] = 0x98;
      bytes[2] = (byte)(data.Length + 6);
      bytes[3] = (byte)mtype;
      bytes[4] = (byte)(mtype >> 8);
      bytes[5] = (byte)offset;
      bytes[6] = (byte)(offset >> 8);
      bytes[7] = (byte)(offset >> 16);
      data.CopyTo(bytes.AsSpan(8));
      return new Frame { Bytes = bytes, CrcValid = true, Framing = Framing.GEOSCAN };
    }

    /// <summary>Lobachevsky's v2 layout: no mtype, a fixed marker, a 32-bit offset and a picture
    /// counter, then exactly 54 payload bytes.</summary>
    private static Frame V2(int satNum, int offset, int frameNum, ReadOnlySpan<byte> data)
    {
      var bytes = new byte[72];
      bytes[0] = (byte)satNum;
      bytes[1] = 0x98;
      bytes[2] = 60;
      BitConverter.GetBytes(0x6F6B6F31u).CopyTo(bytes, 5);
      BitConverter.GetBytes(offset).CopyTo(bytes, 9);
      BitConverter.GetBytes((ushort)frameNum).CopyTo(bytes, 13);
      data.CopyTo(bytes.AsSpan(15));
      return new Frame { Bytes = bytes, CrcValid = true, Framing = Framing.GEOSCAN };
    }

    /// <summary>A whole file as the satellite would send it: 0x0905 image frames at consecutive
    /// absolute offsets, the first one carrying the SOI that says where zero is.</summary>
    private static List<Frame> Stream(byte[] jpeg, int satNum = 0x0C, int mtype = 0x0905, int firstOffset = AddressBase)
    {
      var frames = new List<Frame>();
      for (int at = 0; at < jpeg.Length; at += FragmentLen)
        frames.Add(V1(satNum, mtype, firstOffset + at,
                      jpeg.AsSpan(at, Math.Min(FragmentLen, jpeg.Length - at))));
      return frames;
    }

    private static (RawJpegAssembler A, List<ImageProduct> Updates, List<ImageProduct> Done) Assembler()
    {
      var a = new RawJpegAssembler(RawJpegSource.Geoscan);
      List<ImageProduct> updates = [], done = [];
      a.ImageUpdated += updates.Add;
      a.ImageCompleted += done.Add;
      return (a, updates, done);
    }


    // ---- the round trip ----------------------------------------------------------------------------

    [Fact]
    public void AWholeStream_RebuildsTheFileExactly()
    {
      var jpeg = KnownJpeg();
      var (a, updates, done) = Assembler();

      foreach (var f in Stream(jpeg)) a.Push(f);

      var final = done.Should().ContainSingle().Subject;
      final.Jpeg.Should().Equal(jpeg, "reassembly is byte-for-byte or it is nothing");
      final.Complete.Should().BeTrue();
      final.Width.Should().Be(368);
      final.Height.Should().Be(656);
      final.Source.Should().Be("Geoscan-2");
      final.FirstGapOffset.Should().Be(jpeg.Length, "truth extends to the end of the file");
      final.FragmentsReceived.Should().Be(updates.Count);
      final.FragmentsExpected.Should().Be(final.FragmentsReceived, "nothing was missing");
      a.FragmentsRejected.Should().Be(0);
    }

    [Fact]
    public void EveryUpdateIsADecodableJpegPrefix()
    {
      var jpeg = KnownJpeg("hades-sa_img235_complete");
      var (a, updates, _) = Assembler();

      foreach (var f in Stream(jpeg)) a.Push(f);

      updates.Should().OnlyContain(p => p.Jpeg.Length >= 2);
      foreach (var p in updates)
      {
        p.Jpeg.Take(2).Should().Equal([0xFF, 0xD8], "a progressive view still has to open with SOI");
        p.Jpeg.TakeLast(2).Should().Equal([0xFF, 0xD9], "and be closed, synthetically if need be");
      }
      updates[^1].Width.Should().Be(320);
      updates[^1].Height.Should().Be(240);
    }

    [Fact]
    public void OutOfOrderFragments_StillLandInTheRightPlace()
    {
      var jpeg = KnownJpeg();
      var frames = Stream(jpeg);
      // the SOI fragment has to stay first: it is what fixes where byte zero is
      var shuffled = frames.Take(1).Concat(frames.Skip(1).Reverse()).ToList();
      var (a, _, done) = Assembler();

      foreach (var f in shuffled) a.Push(f);
      a.Flush();

      done[^1].Jpeg.Should().Equal(jpeg, "an absolute offset does not care when it arrives");
    }

    [Fact]
    public void DuplicateFragments_AreNotCountedTwice()
    {
      var jpeg = KnownJpeg("hades-sa_img235_complete");
      var frames = Stream(jpeg);
      var (a, _, done) = Assembler();

      foreach (var f in frames) a.Push(f);
      int accepted = a.FragmentsAccepted;
      foreach (var f in frames.Skip(1)) a.Push(f);   // skip the SOI frame, which would start a new image

      done[0].Jpeg.Should().Equal(jpeg);
      a.FragmentsAccepted.Should().Be(accepted * 2 - 1, "a re-decode is still written");
      done[0].FragmentsReceived.Should().Be(accepted, "but the image counts distinct fragments");
    }


    // ---- loss --------------------------------------------------------------------------------------

    [Fact]
    public void ALostFragment_TruncatesTheFileAtTheGap()
    {
      // the property that separates this family from SSDV: a hole costs everything after it, so the
      // product stops at the hole rather than pretending the rest is a picture.
      var jpeg = KnownJpeg();
      var frames = Stream(jpeg);
      var (a, _, done) = Assembler();

      foreach (var f in frames.Where((_, i) => i != 10)) a.Push(f);
      a.Flush();

      var final = done.Should().ContainSingle().Subject;
      final.Complete.Should().BeFalse();
      final.FirstGapOffset.Should().Be(10 * FragmentLen);
      final.Jpeg.Length.Should().Be(10 * FragmentLen + 2, "the trusted prefix, closed with a synthetic EOI");
      final.Jpeg.Take(10 * FragmentLen).Should().Equal(jpeg.Take(10 * FragmentLen));
      final.FragmentsExpected.Should().BeGreaterThan(final.FragmentsReceived);
    }

    [Fact]
    public void ALaterPassFillingTheGap_CompletesTheImage()
    {
      var jpeg = KnownJpeg("hades-sa_img235_complete");
      var frames = Stream(jpeg);
      var (a, updates, _) = Assembler();

      foreach (var f in frames.Where((_, i) => i != 5)) a.Push(f);
      updates[^1].Complete.Should().BeFalse();

      a.Push(frames[5]);

      updates[^1].Complete.Should().BeTrue("both ends are present and nothing is missing between them");
      updates[^1].Jpeg.Should().Equal(jpeg);
    }

    [Fact]
    public void AStreamWithNoSoi_IsRebasedOnTheLowestOffsetSeen()
    {
      // a pass that starts mid-image: nothing says where byte zero is, so the earliest fragment becomes
      // it. The picture is unusable, but the buffer stays small instead of honouring an offset of 147456.
      var jpeg = KnownJpeg("hades-sa_img235_complete");
      var frames = Stream(jpeg).Skip(4).ToList();
      var (a, updates, _) = Assembler();

      foreach (var f in frames) a.Push(f);

      var final = updates[^1];
      final.Complete.Should().BeFalse("there is no SOI, so this is not a whole file");
      final.FirstGapOffset.Should().Be(jpeg.Length - 4 * FragmentLen, "contiguous, just not from the start");
      a.FragmentsRejected.Should().Be(0, "the address-space offset was rebased, not refused");
    }

    [Fact]
    public void AnEarlierFragmentArrivingLate_ShiftsTheBufferBack()
    {
      var jpeg = KnownJpeg("hades-sa_img235_complete");
      var frames = Stream(jpeg);
      var (a, updates, _) = Assembler();

      a.Push(frames[6]);                          // no SOI, so base is guessed as this fragment's offset
      a.Push(frames[5]);                          // lower: the guess was wrong

      updates[^1].FirstGapOffset.Should().Be(2 * FragmentLen, "both fragments are now in order from zero");
      a.FragmentsRejected.Should().Be(0);
    }


    // ---- image boundaries --------------------------------------------------------------------------

    [Fact]
    public void ASecondSoi_StartsASecondImage()
    {
      var jpeg = KnownJpeg("hades-sa_img235_complete");
      var (a, _, done) = Assembler();

      foreach (var f in Stream(jpeg)) a.Push(f);
      foreach (var f in Stream(jpeg, firstOffset: AddressBase + 0x8000)) a.Push(f);

      done.Should().HaveCount(2);
      done.Select(p => p.ImageId).Should().Equal(0, 1);
      done.Should().OnlyContain(p => p.Complete);
    }

    [Fact]
    public void AStartCommand_StartsANewImageEvenWithoutAnSoi()
    {
      var jpeg = KnownJpeg("hades-sa_img235_complete");
      var (a, _, done) = Assembler();

      foreach (var f in Stream(jpeg)) a.Push(f);
      a.Push(V1(0x0C, 0x0901, AddressBase + 0x8000, jpeg.AsSpan(0, FragmentLen)));
      a.Flush();

      done.Should().HaveCount(2, "mtype 0x0901 says outright that a picture begins here");
    }

    [Fact]
    public void ADifferentSatellite_CompletesThePreviousImage()
    {
      var jpeg = KnownJpeg("hades-sa_img235_complete");
      var (a, _, done) = Assembler();

      foreach (var f in Stream(jpeg, satNum: 0x0C)) a.Push(f);
      foreach (var f in Stream(jpeg, satNum: 0x0D).Take(3)) a.Push(f);
      a.Flush();

      done.Select(p => p.Source).Should().Equal("Geoscan-2", "Geoscan-3");
    }

    [Fact]
    public void TheHighResolutionFlag_SeparatesImages()
    {
      // a different camera is a different picture, even from the same satellite in the same pass.
      var jpeg = KnownJpeg("hades-sa_img235_complete");
      var (a, _, done) = Assembler();

      foreach (var f in Stream(jpeg, mtype: 0x0905).Take(4)) a.Push(f);
      foreach (var f in Stream(jpeg, mtype: 0x9820).Take(4)) a.Push(f);
      a.Flush();

      done.Should().HaveCount(2);
    }

    [Fact]
    public void FlushOnAnEmptyAssembler_IsHarmless()
    {
      var (a, _, done) = Assembler();
      a.Flush();
      done.Should().BeEmpty();
    }


    // ---- the gate ----------------------------------------------------------------------------------

    [Fact]
    public void RealOffAirFrames_AreNotImageFragments()
    {
      // 23 genuine Geoscan-framed frames from SatNOGS, one of every (satellite, mtype) pair observed
      // across 226 of them. None is image data — which is the whole reason this phase has no corpus
      // test — so every one of them must be rejected by the payload gate rather than half-parsed.
      var bytes = File.ReadAllBytes(Path.Combine(TestPaths.DataDir, "Geoscan", "geoscan_telemetry.bin"));
      (bytes.Length % 72).Should().Be(0);
      var (a, updates, _) = Assembler();

      for (int i = 0; i < bytes.Length / 72; i++)
      {
        var frame = new Frame
        {
          Bytes = bytes.AsSpan(i * 72, 72).ToArray(), CrcValid = true, Framing = Framing.GEOSCAN
        };
        RawJpegSource.Geoscan.TryExtract(frame, out _).Should().BeFalse($"frame {i} is telemetry");
        a.Push(frame);
      }

      updates.Should().BeEmpty();
      a.FragmentsAccepted.Should().Be(0);
      a.FragmentsRejected.Should().Be(0, "a telemetry frame is not a failed image fragment");
    }

    [Fact]
    public void TelemetryInterleavedWithImageData_IsInert()
    {
      var jpeg = KnownJpeg("hades-sa_img235_complete");
      var telemetry = File.ReadAllBytes(Path.Combine(TestPaths.DataDir, "Geoscan", "geoscan_telemetry.bin"));
      var noise = new Frame
      {
        Bytes = telemetry[..72], CrcValid = true, Framing = Framing.GEOSCAN
      };
      var (a, _, done) = Assembler();

      foreach (var f in Stream(jpeg)) { a.Push(noise); a.Push(f); }

      done.Should().ContainSingle().Which.Jpeg.Should().Equal(jpeg);
    }

    [Fact]
    public void AnotherFramingEntirely_IsIgnored()
    {
      var frame = Stream(KnownJpeg())[0] with { Framing = Framing.AX25G3RUH };

      RawJpegSource.Geoscan.TryExtract(frame, out _).Should().BeFalse();
    }

    [Theory]
    [InlineData(0x00)]   // not a platform ID at all
    [InlineData(0x40)]
    [InlineData(0xFF)]
    public void AnUnknownSenderIsRejected(int satNum) =>
      RawJpegSource.Geoscan.TryExtract(V1(satNum, 0x0905, AddressBase, new byte[FragmentLen]), out _)
        .Should().BeFalse();

    [Fact]
    public void AShortFrame_IsRejectedRatherThanReadPastItsEnd()
    {
      var frame = new Frame { Bytes = [0x0C, 0x98, 0x46], CrcValid = true, Framing = Framing.GEOSCAN };

      RawJpegSource.Geoscan.TryExtract(frame, out _).Should().BeFalse();
    }

    [Fact]
    public void ADlenLongerThanTheFrame_IsRejected()
    {
      // dlen is a payload length and the modem pads to a fixed size, so a dlen implying more payload
      // than the frame holds means the frame is not what it claims.
      var frame = V1(0x0C, 0x0905, AddressBase, new byte[FragmentLen]);
      frame.Bytes[2] = 200;

      RawJpegSource.Geoscan.TryExtract(frame, out _).Should().BeFalse();
    }


    // ---- the v2 layout -----------------------------------------------------------------------------

    [Fact]
    public void Version2_IsRecognisedByItsMarker()
    {
      var jpeg = KnownJpeg("hades-sa_img235_complete");
      var (a, _, done) = Assembler();

      for (int at = 0; at < jpeg.Length; at += 54)
        a.Push(V2(0x12, at, frameNum: 7, jpeg.AsSpan(at, Math.Min(54, jpeg.Length - at))));
      a.Flush();

      var final = done.Should().ContainSingle().Subject;
      final.Source.Should().Be("Lobachevsky");
      final.Complete.Should().BeTrue();

      // v2 has no per-frame payload length — SatsDecoder's struct reads a fixed 54 bytes and its
      // push_data2 writes all of them, ignoring dlen — so the last frame of a file carries padding
      // past the EOI. It lands after the end of the image and no decoder ever reads it.
      final.Jpeg.Take(jpeg.Length).Should().Equal(jpeg);
      final.Jpeg.Skip(jpeg.Length).Should().OnlyContain(b => b == 0x00 || b == 0xFF || b == 0xD9);
    }

    [Fact]
    public void Version2_SeparatesImagesByFrameNumber()
    {
      var jpeg = KnownJpeg("hades-sa_img235_complete");
      var (a, _, done) = Assembler();

      a.Push(V2(0x12, 0, frameNum: 7, jpeg.AsSpan(0, 54)));
      a.Push(V2(0x12, 54, frameNum: 7, jpeg.AsSpan(54, 54)));
      a.Push(V2(0x12, 0, frameNum: 8, jpeg.AsSpan(0, 54)));
      a.Flush();

      done.Should().HaveCount(2, "fnum is v2's picture counter, and it changed");
    }

    [Fact]
    public void AV2FrameWithTheWrongMarker_FallsBackToV1()
    {
      // SatsDecoder tries v2 first for these platforms and falls back on reserved0 == 0x98. A Geoscan-2
      // frame is not v2, and must still parse as v1 rather than being dropped by the failed attempt.
      var frame = V1(0x0C, 0x0905, AddressBase, new byte[FragmentLen]);

      RawJpegSource.Geoscan.TryExtract(frame, out var fragment).Should().BeTrue();
      fragment.Offset.Should().Be(AddressBase);
      fragment.Data.Should().HaveCount(FragmentLen);
    }

    [Fact]
    public void AFrameThatIsNeitherLayout_IsRejected()
    {
      var frame = V1(0x0C, 0x0905, AddressBase, new byte[FragmentLen]);
      frame.Bytes[1] = 0x00;   // not the v1 reserved0, and the v2 marker is absent too

      RawJpegSource.Geoscan.TryExtract(frame, out _).Should().BeFalse();
    }


    // ---- dispatch ----------------------------------------------------------------------------------

    [Fact]
    public void Factory_BuildsARawJpegAssemblerForGeoscan() =>
      ImageAssemblerFactory.Create(
        new SignalParams(Baud: 9600, Modulation.GFSK, Framing.GEOSCAN, SampleRate: 48000, Deviation: 2400),
        64890).Should().BeOfType<RawJpegAssembler>();
  }
}
