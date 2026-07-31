using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using FluentAssertions;
using VE3NEA.SkyTlm.Imaging.Ssdv;
using VE3NEA.SkyTlm.Tests.Regression;
using Xunit;

namespace VE3NEA.SkyTlm.Tests.Unit
{
  /// <summary>
  /// Differential tests for the <c>ssdv.c</c> port. SSDV has no specification — the reference
  /// implementation is the specification — so correctness here means byte-identical output, not merely
  /// a decodable JPEG. Each <c>.ssdv</c> fixture in <c>Data/Ssdv</c> is paired with a <c>.ref.jpg</c>
  /// produced by <c>fsphil/ssdv</c> built from source at commit-time; the tool is not a build or test
  /// dependency, only its output is.
  /// <para>
  /// The corpus covers all three subsampling modes, a greyscale source, complete and truncated images,
  /// and every kind of loss — interior gaps, a missing tail and a missing head. Between the two
  /// satellites it spans six geometries including a portrait one, and quality levels 1, 2, 4, 5 and 7 —
  /// five of the eight DQT scalings.
  /// </para>
  /// <para>
  /// Three fixtures were completed from the SatNOGS DB (2026-07-30, NORAD 68446, observers
  /// <c>EU1XX-KO33ru</c> and <c>HB9AKP-JN36gn</c>, all SiDS uploads — SatNOGS itself has no decoder for
  /// this satellite, so its stations produce no frames). Different receivers lose different packets, so
  /// the union of two captures is materially more complete than either: our own capture of image 235 held
  /// 12 of 15 packets and SatNOGS supplied exactly the three that were missing. On-air and SatNOGS
  /// packets are interchangeable once the five constant lead bytes are restored.
  /// </para>
  /// </summary>
  public class SsdvTranscoderTests
  {
    /// <summary>Every fixture, with the geometry and packet count the reference tool reported for it.</summary>
    public static TheoryData<string, int, int, int> Fixtures => new()
    {
      // name                       width height packets
      { "hades-sa_img235_12of15",     320,  240,  12 },  // off air: interior gaps, EOI present
      { "hades-sa_img236_10pkt",      320,  240,  10 },  // off air: gaps and a missing tail
      { "hades-sa_img226_5pkt",       320,  240,   5 },  // off air: only the first third of the image
      { "hades-sa_img235_complete",   320,  240,  15 },  // off air: the same image, completed from SatNOGS
      { "hades-sa_img231_complete",   320,  240,  19 },  // off air: complete, and the most detailed we have
      { "hades-sa_img225_tailonly",   320,  240,   6 },  // off air: packets 0-8 lost, so the head is all fill
      { "testcard_420",               160,  128,  21 },  // synthetic: complete, 2x2 subsampling
      { "testcard_444",               160,  128,  34 },  // synthetic: complete, 1x1 subsampling
      { "testcard_grey",              160,  128,  16 },  // synthetic: complete, greyscale source
      { "testcard_420_lossy",         160,  128,  15 },  // synthetic: packets 0, 3, 4, 9, 15, 20 dropped
      { "jy1sat_img7",                544,  304,  59 },  // off air: complete, q2
      { "jy1sat_img8",                560,  320,  62 },  // off air: complete, q5
      { "jy1sat_img9",                368,  656,  67 },  // off air: complete, portrait
      { "jy1sat_img11",               432,  288,  67 },  // off air: complete, q4
      { "jy1sat_img14",               544,  304,  69 },  // off air: complete, the largest fixture
      { "jy1sat_img30",               560,  240,  29 },  // off air: complete, q1 — the coarsest DQT scaling
      { "jy1sat_img9_lossy",          368,  656,  61 },  // off air, thinned: packets 0, 1, 17, 30-32 dropped
    };

    /// <summary>
    /// Which variant a fixture's packets are. Taken from the name rather than carried in the theory data
    /// because <see cref="SsdvVariant"/> is a record, and xUnit would need it to be serialisable to put
    /// it in a <see cref="TheoryData{T}"/>.
    /// </summary>
    private static SsdvVariant Variant(string name) =>
      name.StartsWith("jy1sat") ? SsdvVariant.Jy1Sat200 : SsdvVariant.Standard256;

    private static string Path(string name, string ext) =>
      System.IO.Path.Combine(TestPaths.DataDir, "Ssdv", name + ext);

    /// <summary>Concatenated canonical packets in transmission order — what <c>ssdv -d</c> eats.</summary>
    private static List<SsdvPacket> ReadPackets(string name)
    {
      var v = Variant(name);
      var bytes = File.ReadAllBytes(Path(name, ".ssdv"));
      (bytes.Length % v.PacketLen).Should().Be(0, $"the fixture is whole {v.PacketLen}-byte packets");

      var packets = new List<SsdvPacket>();
      for (int i = 0; i < bytes.Length / v.PacketLen; i++)
      {
        SsdvPacket.TryParse(bytes.AsSpan(i * v.PacketLen, v.PacketLen), v, out var p)
          .Should().BeTrue($"packet {i} of {name} was validated when the fixture was built");
        packets.Add(p!);
      }
      return packets;
    }


    // ---- the differential test ---------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Transcode_IsByteIdenticalToTheReferenceImplementation(string name, int width, int height, int packets)
    {
      var input = ReadPackets(name);
      input.Should().HaveCount(packets);

      var jpeg = SsdvTranscoder.Transcode(input);

      jpeg.Should().Equal(File.ReadAllBytes(Path(name, ".ref.jpg")),
        "the port must agree with fsphil/ssdv byte for byte — that is the only specification there is");
      _ = (width, height);
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Transcode_ProducesADecodableJpegOfTheRightSize(string name, int width, int height, int packets)
    {
      _ = packets;
      var jpeg = SsdvTranscoder.Transcode(ReadPackets(name));

      jpeg.Take(2).Should().Equal([0xFF, 0xD8], "the file opens with SOI");
      jpeg.TakeLast(2).Should().Equal([0xFF, 0xD9], "and closes with EOI");

      // byte-identical to the reference is the real assertion; this one says the reference itself is
      // producing something a JPEG decoder accepts, which is what SkyRoof will hand to a PictureBox.
      using var stream = new MemoryStream(jpeg);
      using var bitmap = new Bitmap(stream);
      bitmap.Width.Should().Be(width);
      bitmap.Height.Should().Be(height);
    }


    // ---- geometry and state ------------------------------------------------------------------------

    [Theory]
    [InlineData("testcard_420", 0, 80)]     // 10x8 blocks of 16x16
    [InlineData("testcard_444", 3, 320)]    // the same blocks split into 8x8 MCUs
    [InlineData("hades-sa_img235_12of15", 0, 300)]
    public void McuCountAndSubsampling_ComeFromTheHeader(string name, int subsampling, int mcuCount)
    {
      var packets = ReadPackets(name);

      packets.Should().OnlyContain(p => p.Subsampling == subsampling);
      packets[0].McuCount.Should().Be(mcuCount);
    }

    [Fact]
    public void CompleteImage_ReportsComplete()
    {
      var t = new SsdvTranscoder();
      foreach (var p in ReadPackets("testcard_420")) t.Feed(p);

      t.IsComplete.Should().BeTrue("the fixture holds every packet of the image");
      t.McuId.Should().Be(t.McuCount);
      t.Width.Should().Be(160);
      t.Height.Should().Be(128);
    }

    [Fact]
    public void TruncatedImage_IsFilledToTheEndOnly_WhenTheJpegIsTaken()
    {
      var t = new SsdvTranscoder();
      foreach (var p in ReadPackets("hades-sa_img226_5pkt")) t.Feed(p);

      t.IsComplete.Should().BeFalse("the pass caught only the first five packets");
      t.McuId.Should().BeLessThan(t.McuCount);

      t.GetJpeg().Should().NotBeEmpty();
      t.McuId.Should().Be(t.McuCount, "the missing tail is padded so the file is still decodable");
    }

    [Fact]
    public void GetJpeg_IsIdempotent()
    {
      var t = new SsdvTranscoder();
      foreach (var p in ReadPackets("hades-sa_img236_10pkt")) t.Feed(p);

      var first = t.GetJpeg();
      t.GetJpeg().Should().BeSameAs(first, "finalising twice must not append a second EOI");
    }

    [Fact]
    public void FeedAfterGetJpeg_Throws()
    {
      var packets = ReadPackets("hades-sa_img226_5pkt");
      var t = new SsdvTranscoder();
      t.Feed(packets[0]);
      t.GetJpeg();

      t.Invoking(x => x.Feed(packets[1])).Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void NoPackets_ProduceNoFile()
    {
      // an empty JPEG is more honest than headers wrapped around nothing: there is no geometry to
      // write, because geometry comes from the packets.
      SsdvTranscoder.Transcode([]).Should().BeEmpty();
    }


    // ---- loss and disorder -------------------------------------------------------------------------

    [Fact]
    public void DroppedPackets_CostOnlyTheirOwnMcus()
    {
      // the property that makes SSDV worth having: the packets after a hole still land in the right
      // place, so the output is the same length and only the gap differs.
      var whole = SsdvTranscoder.Transcode(ReadPackets("testcard_420"));
      var lossy = SsdvTranscoder.Transcode(ReadPackets("testcard_420_lossy"));

      lossy.Take(2).Should().Equal(whole.Take(2), "same SOI");
      using var stream = new MemoryStream(lossy);
      using var bitmap = new Bitmap(stream);
      bitmap.Size.Should().Be(new Size(160, 128), "loss changes the content, never the geometry");
    }

    [Fact]
    public void SatnogsPackets_CompleteAnImageOurOwnReceiverMissed()
    {
      // Image 235 twice: as one receiver heard it, and as two receivers heard it between them. This is
      // the case for treating the SatNOGS DB as a packet source rather than a curiosity — and it also
      // pins that packets from a third party parse and place identically to our own.
      var ours = ReadPackets("hades-sa_img235_12of15");
      var both = ReadPackets("hades-sa_img235_complete");

      var mine = ours.Select(p => p.PacketId).ToList();
      var merged = both.Select(p => p.PacketId).ToList();

      mine.Should().NotContain([0, 6, 10]);
      merged.Should().Contain(mine, "a union never loses a packet it already had");
      merged.Should().Equal(Enumerable.Range(0, 15), "and it gained exactly the three that were missing");

      both.Should().OnlyContain(p => p.ImageId == 235 && p.CallsignCode == 0xBF35FBA3);

      // the completed image needs no gap fill at all, so every MCU in the file is real data
      var t = new SsdvTranscoder();
      foreach (var p in both) t.Feed(p);
      t.IsComplete.Should().BeTrue();
      t.McuId.Should().Be(300, "320x240 at 2x2 is 20x15 MCUs, all of them received");
    }

    [Fact]
    public void MissingHead_IsFilledBeforeTheFirstRealPacket()
    {
      // Image 225 lost packets 0-8, so the transcoder's first act is to write nine packets' worth of
      // neutral MCUs and only then place real data — the opposite end of the stream from the tail fill,
      // and a path no synthetic fixture reaches.
      var packets = ReadPackets("hades-sa_img225_tailonly");

      packets.Select(p => p.PacketId).Should().Equal([9, 10, 11, 12, 13, 14]);
      packets.Last().IsEoi.Should().BeTrue("the last packet of the image did arrive");

      var t = new SsdvTranscoder();
      t.Feed(packets[0]);
      t.McuId.Should().BeGreaterThan(0, "the head was filled before the first real packet was placed");
    }

    // ---- the second variant ------------------------------------------------------------------------

    [Fact]
    public void Jy1SatPackets_CarryNoCallsignAndNoIntegrityLayer()
    {
      // 320,762 packets in the SatNOGS DB archive and not one of them can be checked: the AO-40 FEC
      // layer below is the only integrity there is, so a packet damaged after it renders as a corrupt
      // band instead of being rejected. These fixtures were majority-voted across ~200 complete copies
      // of each picture for exactly that reason.
      var packets = ReadPackets("jy1sat_img8");

      packets.Should().OnlyContain(p => p.Variant == SsdvVariant.Jy1Sat200);
      packets.Should().OnlyContain(p => p.CallsignCode == 0 && p.Callsign == "");
      packets.Should().OnlyContain(p => p.CorrectedBytes == 0, "there is no RS to correct anything");
      packets.Should().OnlyContain(p => p.Payload.Length == 189);
    }

    [Theory]
    [InlineData("jy1sat_img30", 1)]
    [InlineData("jy1sat_img7", 2)]
    [InlineData("jy1sat_img11", 4)]
    [InlineData("jy1sat_img8", 5)]
    [InlineData("hades-sa_img235_complete", 7)]
    public void QualityIndex_SelectsTheQuantisationTableScaling(string name, int quality)
    {
      // JY1SAT is the only source we have for quality levels other than 7, and the scaling factors run
      // from 5000 to 0 — a mis-scaled table is invisible in the header and obvious in the picture.
      var packets = ReadPackets(name);
      packets.Should().OnlyContain(p => p.Quality == quality);

      var jpeg = SsdvTranscoder.Transcode(packets);
      var dqt = FirstDqt(jpeg);
      dqt.Should().Equal(SsdvJpegTables.LoadStandardDqt(SsdvJpegTables.StdDqt0, quality)[1..],
        "the DQT written must be the standard table scaled for this quality");
    }

    /// <summary>The 64 quantisation values of the first DQT segment in a JPEG.</summary>
    private static byte[] FirstDqt(byte[] jpeg)
    {
      for (int i = 2; i + 4 < jpeg.Length; i += 2 + (jpeg[i + 2] << 8 | jpeg[i + 3]))
      {
        jpeg[i].Should().Be(0xFF, "markers are walked from SOI, so every step must land on one");
        if (jpeg[i + 1] == 0xDB) return jpeg[(i + 5)..(i + 69)];
        if (jpeg[i + 1] == 0xDA) break;
      }
      throw new InvalidOperationException("no DQT segment");
    }


    [Fact]
    public void OutOfOrderPacket_IsSkipped()
    {
      // the scan is one bit stream, so a packet that arrives after the decoder has moved past it cannot
      // be placed. Feeding it must be harmless, not corrupting.
      var packets = ReadPackets("testcard_420");
      var shuffled = packets.ToList();
      shuffled.Insert(10, packets[2]);

      SsdvTranscoder.Transcode(shuffled).Should().Equal(SsdvTranscoder.Transcode(packets));
    }

    [Fact]
    public void DuplicatePacket_IsSkipped()
    {
      var packets = ReadPackets("testcard_420");
      var doubled = packets.SelectMany(p => new[] { p, p }).ToList();

      SsdvTranscoder.Transcode(doubled).Should().Equal(SsdvTranscoder.Transcode(packets));
    }
  }
}
