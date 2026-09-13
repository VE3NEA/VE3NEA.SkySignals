using System;
using System.Collections.Generic;
using System.Drawing;
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
  /// The first real Geoscan imaging capture, 2026-09-12/13: 1,451 v2 image frames from Geoscan-4 and
  /// Geoscan-5, in arrival order, as <c>Data/Geoscan/geoscan_v2_images.bin</c>.
  /// <see cref="RawJpegAssemblerTests"/> proves the assembler puts bytes back where they came from,
  /// which synthetic data can do; what only real data can prove is what to <b>emit</b> from a picture
  /// with holes in it, and that is what this file is for.
  /// <para>
  /// The two <c>.ref.jpg</c> fixtures were produced by an independent Python reassembly of the same
  /// frames and each renders 800x600, so comparing against them is a cross-check rather than a
  /// tautology. Every number asserted here was measured before any of this code was written and is
  /// recorded in <c>design-docs/ssdv-geoscan-fix-plan.md</c>.
  /// </para>
  /// </summary>
  public class GeoscanImageCaptureTests
  {
    private const int FrameLen = 72;
    private const int Geoscan4 = 0x0E, Geoscan5 = 0x0F;

    private static List<Frame> Capture()
    {
      var bytes = File.ReadAllBytes(Path.Combine(TestPaths.DataDir, "Geoscan", "geoscan_v2_images.bin"));
      (bytes.Length % FrameLen).Should().Be(0);

      var frames = new List<Frame>(bytes.Length / FrameLen);
      for (int i = 0; i < bytes.Length / FrameLen; i++)
        frames.Add(new Frame
        {
          Bytes = bytes.AsSpan(i * FrameLen, FrameLen).ToArray(), CrcValid = true, Framing = Framing.GEOSCAN
        });
      return frames;
    }

    private static byte[] Reference(string name) =>
      File.ReadAllBytes(Path.Combine(TestPaths.DataDir, "Geoscan", name + ".ref.jpg"));

    /// <summary>Every image the assembler finalizes over the whole capture, in order.</summary>
    private static (List<ImageProduct> Done, RawJpegAssembler A) Replay(IEnumerable<Frame> frames)
    {
      var a = new RawJpegAssembler(RawJpegSource.Geoscan);
      List<ImageProduct> done = [];
      a.ImageCompleted += done.Add;
      foreach (var f in frames) a.Push(f);
      a.Flush();
      return (done, a);
    }

    /// <summary>One fragment list per finalized run of the given picture, newest run first — the order
    /// TelemetryPanel hands them to the merge in.</summary>
    private static List<IReadOnlyList<ImageFragment>> Receptions(List<ImageProduct> done, string source, int fnum) =>
      [.. done.Where(p => p.Source == source && p.ImageId == fnum).Reverse().Select(p => p.Fragments)];

    private static Size Render(byte[] jpeg)
    {
      using var stream = new MemoryStream(jpeg);
      using var bitmap = new Bitmap(stream);
      return bitmap.Size;
    }


    // ---- what the capture is -----------------------------------------------------------------------

    [Fact]
    public void EveryFrameOfTheCapture_IsAV2ImageFragment()
    {
      var frames = Capture();
      frames.Should().HaveCount(1451);

      foreach (var frame in frames)
      {
        RawJpegSource.Geoscan.TryExtract(frame, out var fragment).Should().BeTrue();
        fragment.FileRelative.Should().BeTrue("v2 offsets are file offsets");
        fragment.Data.Should().HaveCount(54);
        fragment.Key.Sender.Should().BeOneOf(Geoscan4, Geoscan5);
      }
    }

    [Fact]
    public void TheCaptureHasElevenTransfers_AndTheirIdsAreTheSatellitesOwn()
    {
      var (done, a) = Replay(Capture());

      done.Should().HaveCount(11);
      a.FragmentsRejected.Should().Be(0);
      // fnum, not a per-pass counter: picture 5 was heard in three separate runs and every one of them
      // is image 5, which is what lets an archived reception be found again
      done.Select(p => (p.Source, p.ImageId)).Should().Equal(
        ("Geoscan-4", 5), ("Geoscan-4", 9), ("Geoscan-4", 14),
        ("Geoscan-5", 14), ("Geoscan-5", 10), ("Geoscan-5", 5), ("Geoscan-5", 10),
        ("Geoscan-5", 5), ("Geoscan-5", 11), ("Geoscan-5", 14), ("Geoscan-5", 5));
    }

    [Fact]
    public void NoTwoReceptionsOfOneByteRangeDisagree()
    {
      // The finding the whole merge design rests on: of the 1,451 fragments only 1,203 are a distinct
      // (fnum, offset), so 248 repeat a byte range already received — and not one pair of copies
      // differs. Agreement is what stands in for the CRC these fragments do not carry, so if this ever
      // fails the merge's refusal rule is what saves the picture. Keyed on fnum alone rather than on
      // (satellite, fnum), which makes it the cross-satellite claim as well: 151 of the repeats are the
      // same bird heard twice and the other 97 are one bird's copy of the other's byte range.
      var byPicture = new Dictionary<int, SparseImageBuffer>();
      int repeats = 0;

      foreach (var frame in Capture())
      {
        RawJpegSource.Geoscan.TryExtract(frame, out var f).Should().BeTrue();
        int fnum = f.Key.Sequence;
        if (!byPicture.TryGetValue(fnum, out var buffer)) byPicture[fnum] = buffer = new SparseImageBuffer();

        if (buffer.CoveredBytes(f.Offset, f.Offset + f.Data.Length) > 0) repeats++;
        buffer.Conflicts(f.Offset, f.Data).Should().BeFalse($"offset {f.Offset} of picture {fnum}");
        buffer.Write(f.Offset, f.Data);
      }

      repeats.Should().Be(248, "the repeat receptions are what make the check meaningful");
    }


    // ---- defect 1: a baseline picture is emitted whole, not truncated at the first gap ---------------

    [Fact]
    public void ABaselinePicture_IsEmittedWholeWithItsGapsZeroFilled()
    {
      var (done, _) = Replay(Capture());
      var best = done.Where(p => p.Source == "Geoscan-5" && p.ImageId == 5)
                     .OrderByDescending(p => p.FragmentsReceived).First();

      best.FragmentsReceived.Should().Be(579);
      best.FirstGapOffset.Should().Be(9126, "the honesty metric stays, it just stops being the cut");
      best.Jpeg.Length.Should().Be(34022, "the whole 34,020-byte span plus a synthetic EOI, not 9,128 B");
      Render(best.Jpeg).Should().Be(new Size(800, 600));
    }


    // ---- defect 2: a progressive picture stops at the last scan that arrived ------------------------

    [Fact]
    public void AProgressivePicture_StopsAfterTheLastScanThatArrived()
    {
      var (done, _) = Replay(Capture());
      var best = done.Where(p => p.Source == "Geoscan-5" && p.ImageId == 14)
                     .OrderByDescending(p => p.FragmentsReceived).First();

      best.FirstGapOffset.Should().Be(4374);
      // emitting the whole 46,980-byte span of this one renders pure black: its last scan is 20 % covered
      // and overwrites everything the scans before it built. Truncating at the first gap cuts inside
      // scan 0 and renders a blur. The scan walk stops between the two.
      best.Jpeg.Length.Should().BeGreaterThan(4376).And.BeLessThan(46980);
      Render(best.Jpeg).Should().Be(new Size(800, 600));
    }

    [Fact]
    public void TheProgressiveScanTable_IsReadOffTheRealFile()
    {
      var table = JpegScanTable.Read(Reference("geoscan5_pic14_progressive"));

      table.Should().NotBeNull();
      table!.Progressive.Should().BeTrue("picture 14 is SOF2");
      // the emitted prefix carries the four scans that cleared the coverage threshold and no more
      table.Scans.Should().HaveCount(4);
      table.Scans.Select(s => s.End).Should().Equal(7716, 12484, 14852, 19294);
    }

    [Fact]
    public void ABaselineFile_HasOneScanAndIsNotProgressive()
    {
      var table = JpegScanTable.Read(Reference("geoscan5_pic5_baseline"));

      table.Should().NotBeNull();
      table!.Progressive.Should().BeFalse();
      table.Scans.Should().ContainSingle();
    }


    // ---- defect 4: the playlist's ASCII slides are text, not broken pictures -------------------------

    [Fact]
    public void TheAsciiSlides_AreTextProductsRatherThanFiftySixByteJpegs()
    {
      var (done, _) = Replay(Capture());
      var slides = done.Where(p => p.Text != null).ToList();

      // four nodes, not the five the plan's table counted: its node 10 is the 17-fragment start of a
      // picture whose second fragment was lost, which looks like a slide only by its FirstGapOffset
      slides.Should().HaveCount(4, "three distinct slides, one of them heard twice");
      slides.Should().OnlyContain(p => p.Jpeg.Length == 0 && p.Width == 0 && p.FragmentsReceived == 1);
      slides.Select(p => p.Text).Distinct().Should().BeEquivalentTo(
        "2/5 UNTIL OCTOBER 15, ADD YOUR NAME, DRAWING OR PHOTO!",
        "3/5 YOUR MESSAGE WILL FLY TO ORBIT ON CUBESAT IOFFE-1!",
        "4/5 VISIT GEOSCAN.SPACE FOR CAMPAIGN DETAILS!");
    }

    [Fact]
    public void APictureIsNeverMistakenForText()
    {
      var (done, _) = Replay(Capture());

      done.Where(p => p.ImageId is 5 or 14).Should().OnlyContain(p => p.Text == null);
    }


    // ---- the fragments a reception hands back --------------------------------------------------------

    [Fact]
    public void EveryTransferYieldsArchivableFragmentsKeyedByFileOffset()
    {
      var (done, _) = Replay(Capture());

      done.Should().OnlyContain(p => p.FragmentFormat == RawJpegMerge.Format);
      foreach (var p in done)
      {
        p.Fragments.Should().HaveCount(p.FragmentsReceived, "one per distinct fragment written");
        p.Fragments.Select(f => f.Id).Should().BeInAscendingOrder().And.OnlyHaveUniqueItems();
        p.Fragments.Should().OnlyContain(f => f.Bytes.Length == 54 && f.CorrectedBytes == 0);
        // the offset IS the identity, so a fragment can be put back exactly where it came from
        p.Fragments.Should().OnlyContain(f => f.Id % 54 == 0);
      }
    }


    // ---- merging across passes ----------------------------------------------------------------------

    [Fact]
    public void MergingThePassesRebuildsTheBaselinePictureExactly()
    {
      var (done, _) = Replay(Capture());
      var merged = RawJpegMerge.Build(Receptions(done, "Geoscan-5", 5), RawJpegMerge.Format, 5, "Geoscan-5");

      merged.Should().NotBeNull();
      merged!.FragmentsReceived.Should().Be(583, "the three runs together, 91.9 % -> 92.5 %");
      merged.Jpeg.Should().Equal(Reference("geoscan5_pic5_baseline"),
        "byte for byte what an independent reassembly of the same frames produces");
      Render(merged.Jpeg).Should().Be(new Size(800, 600));
    }

    [Fact]
    public void MergingThePassesRebuildsTheProgressivePictureExactly()
    {
      var (done, _) = Replay(Capture());
      var merged = RawJpegMerge.Build(Receptions(done, "Geoscan-5", 14), RawJpegMerge.Format, 14, "Geoscan-5");

      merged.Should().NotBeNull();
      merged!.FragmentsReceived.Should().Be(614, "65.4 % from the best single run -> 69.5 %");
      merged.Jpeg.Should().Equal(Reference("geoscan5_pic14_progressive"));
      Render(merged.Jpeg).Should().Be(new Size(800, 600));
    }

    [Fact]
    public void ARunThatCarriedNoHeader_IsStillWorthArchiving()
    {
      // The 08:04 run of picture 14 joined mid-transfer at offset 12,636, so it has no SOI and can never
      // render on its own — but its 70 fragments are real, and merging them into the run that did carry
      // the header is what they exist for. Before this they were discarded entirely.
      var (done, _) = Replay(Capture());
      var joinedLate = done.Single(p => p.Source == "Geoscan-5" && p.ImageId == 14 && p.FirstGapOffset == 0);
      joinedLate.FragmentsReceived.Should().Be(70);

      var withHead = done.Single(p => p.Source == "Geoscan-5" && p.ImageId == 14 && p.FirstGapOffset > 0);
      var merged = RawJpegMerge.Build([withHead.Fragments, joinedLate.Fragments], RawJpegMerge.Format, 14, null);

      merged!.FragmentsReceived.Should().Be(614);
      Render(merged.Jpeg).Should().Be(new Size(800, 600));
    }

    [Fact]
    public void MergingAcrossSatellitesWorksBecauseTheFleetSendsOneSharedPlaylist()
    {
      // Geoscan-4 and Geoscan-5 transmit the identical file for a given fnum, which is what makes a
      // cross-satellite merge meaningful at all, and the byte agreement below is what makes it safe.
      var (done, _) = Replay(Capture());
      var five = Receptions(done, "Geoscan-5", 5);
      var four = Receptions(done, "Geoscan-4", 5);

      var alone = RawJpegMerge.Build(five, RawJpegMerge.Format, 5, "Geoscan-5")!;
      var together = RawJpegMerge.Build([.. five, .. four], RawJpegMerge.Format, 5, "Geoscan-5")!;

      together.FragmentsReceived.Should().BeGreaterThan(alone.FragmentsReceived,
        "the other bird's reception added fragments rather than being refused");
      Render(together.Jpeg).Should().Be(new Size(800, 600));
    }

    [Fact]
    public void AReceptionThatDisagreesIsDroppedWhole_AndTheRestStillMerge()
    {
      var (done, _) = Replay(Capture());
      var receptions = Receptions(done, "Geoscan-5", 5);
      var good = RawJpegMerge.Build(receptions, RawJpegMerge.Format, 5, null)!;

      // one byte changed in the last reception, which is what a picture that reused the file number, or
      // an edited sidecar, would look like: that reception is refused and the merge is otherwise intact
      var tampered = receptions[^1].ToList();
      var altered = tampered[0].Bytes.ToArray();
      altered[0] ^= 0xFF;
      tampered[0] = tampered[0] with { Bytes = altered };

      var merged = RawJpegMerge.Build([.. receptions[..^1], tampered], RawJpegMerge.Format, 5, null)!;

      merged.FragmentsReceived.Should().BeLessThan(good.FragmentsReceived);
      merged.Fragments.Should().NotContain(f => f.Id == tampered[0].Id && f.Bytes[0] == altered[0]);
    }

    [Fact]
    public void AMergeOfNothingIsNull()
    {
      RawJpegMerge.Build([], RawJpegMerge.Format, 5, null).Should().BeNull();
      RawJpegMerge.Build([[]], "ssdv-normal", 5, null).Should().BeNull("a foreign format is refused");
    }
  }
}
