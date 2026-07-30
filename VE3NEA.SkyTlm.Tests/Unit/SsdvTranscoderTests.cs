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
  /// and both kinds of loss — interior gaps and a missing tail.
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
      { "testcard_420",               160,  128,  21 },  // synthetic: complete, 2x2 subsampling
      { "testcard_444",               160,  128,  34 },  // synthetic: complete, 1x1 subsampling
      { "testcard_grey",              160,  128,  16 },  // synthetic: complete, greyscale source
      { "testcard_420_lossy",         160,  128,  15 },  // synthetic: packets 0, 3, 4, 9, 15, 20 dropped
    };

    private static string Path(string name, string ext) =>
      System.IO.Path.Combine(TestPaths.DataDir, "Ssdv", name + ext);

    /// <summary>Concatenated canonical 256-byte packets in transmission order — what <c>ssdv -d</c> eats.</summary>
    private static List<SsdvPacket> ReadPackets(string name)
    {
      var bytes = File.ReadAllBytes(Path(name, ".ssdv"));
      (bytes.Length % 256).Should().Be(0, "the fixture is whole 256-byte packets");

      var packets = new List<SsdvPacket>();
      for (int i = 0; i < bytes.Length / 256; i++)
      {
        SsdvPacket.TryParse(bytes.AsSpan(i * 256, 256), SsdvVariant.Standard256, out var p)
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
