using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using FluentAssertions;
using VE3NEA.SkyTlm.Imaging;
using VE3NEA.SkyTlm.Imaging.RawJpeg;
using VE3NEA.SkyTlm.Tests.Regression;
using Xunit;

namespace VE3NEA.SkyTlm.Tests.Unit
{
  /// <summary>
  /// The header gate: a raw-JPEG reception whose quantisation or Huffman tables are missing emits
  /// <b>nothing</b>, rather than a long file that looks like a picture and opens in no decoder.
  /// <para>
  /// This is the one raw-JPEG behaviour with real off-air vectors rather than synthesised ones. The
  /// 2026-09-13 16:14 and 17:51 Geoscan-5 passes auto-saved three images and not one of the three
  /// <c>.jpg</c> files opened; their sidecars are committed here under <c>Data/RawJpeg</c>, fragments and
  /// all, and they cover all three routes into the defect — a hole inside the header segments, a hole
  /// inside an SOF payload, and no SOI at all. A hole in the <i>entropy</i> data is the opposite case and
  /// still emits the whole buffer, which <see cref="RawJpegAssemblerTests"/> covers.
  /// </para>
  /// <para>
  /// Nothing here recovers a picture, and nothing can: the missing bytes are not in the file. The point
  /// is that the failure is honest and that the fragments survive it — they are what a later pass merges
  /// against.
  /// </para>
  /// </summary>
  public class RawJpegHeaderGateTests
  {
    /// <summary>One archived reception, read back out of a committed sidecar exactly as
    /// <c>TelemetryPanel</c> wrote it.</summary>
    private static (List<ImageFragment> Fragments, int FirstGapOffset, int Received, int Expected) Sidecar(string name)
    {
      string path = Path.Combine(TestPaths.DataDir, "RawJpeg", name + ".json");
      using var doc = JsonDocument.Parse(File.ReadAllText(path));
      var root = doc.RootElement;

      var fragments = root.GetProperty("Packets").EnumerateArray()
        .Select(p => new ImageFragment(
          p.GetProperty("Id").GetInt32(),
          Convert.FromBase64String(p.GetProperty("Data").GetString()!),
          p.GetProperty("Corrected").GetInt32()))
        .ToList();

      return (fragments, root.GetProperty("FirstGapOffset").GetInt32(),
              root.GetProperty("FragmentsReceived").GetInt32(),
              root.GetProperty("FragmentsExpected").GetInt32());
    }

    private static ImageProduct? Rebuild(List<ImageFragment> fragments, int imageId) =>
      RawJpegMerge.Build([fragments], RawJpegMerge.Format, imageId, "Geoscan-5");


    // ---- the three real header-loss receptions -----------------------------------------------------

    /// <summary>
    /// The three vectors, with what the walk sees in each. Before the gate all three emitted a long
    /// unopenable file: 48,278 / 34,022 / 15,284 bytes written to <c>SsdvImages\</c> as <c>.jpg</c>.
    /// </summary>
    public static TheoryData<string, int, int, int> HeaderLoss => new()
    {
      // name                        imageId  firstGap  fragments received
      { "headerloss_gap_in_header",  14,      216,      682 },   // gap [216,918) takes both DQTs, SOF2, both DHTs
      { "headerloss_gap_in_sof",      5,      162,      248 },   // SOF0 marker present at 158, its payload inside the hole
      { "headerloss_no_soi",         14,        0,       10 },   // 10 fragments, all past byte 13,824
    };

    [Theory]
    [MemberData(nameof(HeaderLoss))]
    public void AReceptionMissingItsHeader_EmitsNothing(string name, int imageId, int firstGap, int received)
    {
      var (fragments, sidecarGap, sidecarReceived, _) = Sidecar(name);
      sidecarGap.Should().Be(firstGap, "the committed sidecar is the capture, not a restatement of it");
      sidecarReceived.Should().Be(received);

      var product = Rebuild(fragments, imageId);

      product.Should().NotBeNull("the reception itself is sound — it is the picture that cannot be built");
      product!.Jpeg.Should().BeEmpty("there is no interpreting entropy data whose tables never arrived");
      product.FirstGapOffset.Should().Be(firstGap);
    }

    [Theory]
    [MemberData(nameof(HeaderLoss))]
    public void AReceptionMissingItsHeader_KeepsItsFragments(string name, int imageId, int firstGap, int received)
    {
      // D makes the failure honest; it does not throw the reception away. These fragments are the only
      // copy of what was heard, and the merge is what turns them back into a picture on a later pass.
      var (fragments, _, _, _) = Sidecar(name);

      var product = Rebuild(fragments, imageId)!;

      product.FragmentsReceived.Should().Be(received, "every fragment that arrived is still counted");
      product.Fragments.Should().HaveCount(received);
      product.FragmentFormat.Should().Be(RawJpegMerge.Format, "so the sidecar can still be archived");
      product.FirstGapOffset.Should().Be(firstGap, "and the honesty metric still says where truth stops");
      product.Complete.Should().BeFalse();
    }

    [Fact]
    public void TheWorstVector_IsMostlyZeroAndStillWasWrittenAsAPicture()
    {
      // 15,282 bytes of which 540 are real (3.5 %), the first 13,824 of them zeros. This is the one that
      // shows the old branch for what it was: table == null means "does not even open with an SOI", and
      // it fell through to the same emit-everything path as a valid baseline file.
      var (fragments, _, _, _) = Sidecar("headerloss_no_soi");

      fragments.Min(f => f.Id).Should().Be(13824, "nothing before that offset was ever heard");
      fragments.Sum(f => f.Bytes.Length).Should().BeLessThan(1000);

      Rebuild(fragments, 14)!.Jpeg.Should().BeEmpty();
    }


    // ---- and the case the gate must not catch ------------------------------------------------------

    [Fact]
    public void AHoleBelowTheHeader_StillEmitsThePicture()
    {
      // The distinction the whole part rests on: B repairs a hole in the entropy stream, and there is no
      // repairing a hole in the tables. A reference file with its header intact and a gap after it is
      // emitted rather than withheld — which since B5 means emitted repaired, so its length is the
      // re-encoded scan's and not the buffer's. That the picture is right is JpegEntropyWalkerTests';
      // that it is emitted at all is this one's.
      var jpeg = File.ReadAllBytes(Path.Combine(TestPaths.DataDir, "Ssdv", "hades-sa_img235_complete.ref.jpg"));
      const int hole = 640;   // the fragment at [640, 704), the first one wholly past the 623-byte header

      var product = Rebuild(Chop(jpeg, dropFragmentAt: hole), 1)!;

      product.FirstGapOffset.Should().Be(hole).And.BeGreaterThanOrEqualTo(RawJpegAssemblerTests.FirstScanStart);
      product.Jpeg.Should().NotBeEmpty("the header is whole, so the picture is worth showing");
      product.Width.Should().Be(320);
      product.Height.Should().Be(240);
    }

    [Fact]
    public void AHoleInTheHeaderOfTheSameFile_EmitsNothing()
    {
      // the same file, the same single missing fragment, moved above the first scan — and the verdict
      // flips. Nothing else about the reception differs, which is what makes the header the cause.
      var jpeg = File.ReadAllBytes(Path.Combine(TestPaths.DataDir, "Ssdv", "hades-sa_img235_complete.ref.jpg"));
      const int hole = 448;   // the fragment at [448, 512), inside the DQT/DHT run

      var product = Rebuild(Chop(jpeg, dropFragmentAt: hole), 1)!;

      product.FirstGapOffset.Should().Be(hole).And.BeLessThan(RawJpegAssemblerTests.FirstScanStart);
      product.Jpeg.Should().BeEmpty();
      product.FragmentsReceived.Should().BeGreaterThan(1, "but the fragments are kept either way");
    }

    /// <summary>The file in 64-byte fragments with the one starting at <paramref name="dropFragmentAt"/>
    /// left out, so the first gap lands exactly there.</summary>
    private static List<ImageFragment> Chop(byte[] jpeg, int dropFragmentAt)
    {
      const int len = 64;
      var fragments = new List<ImageFragment>();
      for (int at = 0; at < jpeg.Length; at += len)
      {
        if (at == dropFragmentAt) continue;
        fragments.Add(new ImageFragment(at, jpeg[at..Math.Min(at + len, jpeg.Length)], 0));
      }
      return fragments;
    }
  }
}
