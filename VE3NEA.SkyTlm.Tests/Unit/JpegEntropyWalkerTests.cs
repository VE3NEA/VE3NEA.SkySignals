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
  /// The MCU accounting and the resync search, against files whose true answer is known because the file
  /// was complete before the test punched a hole in it. That is what makes the assertions here exact
  /// rather than plausible: the intact file is decoded first and every coefficient of every MCU kept, so
  /// a walk over the damaged copy can be checked coefficient for coefficient at the MCU indices it
  /// claims. A resync one bit out produces a decode that is still valid Huffman and still ends where it
  /// should, and misses every one of them.
  /// <para>
  /// The DC coefficients are excluded from the comparison and only from it. A segment's DC predictor
  /// chain restarts from a value the missing bytes carried, so an offset there is expected and is the one
  /// thing the counting argument cannot recover; the AC coefficients are what prove the alignment.
  /// </para>
  /// </summary>
  public class JpegEntropyWalkerTests
  {
    private static byte[] Reference(string folder, string name) =>
      File.ReadAllBytes(Path.Combine(TestPaths.DataDir, folder, name + ".ref.jpg"));

    /// <summary>Every MCU of a complete file, as the coefficient arrays the decoder produces.</summary>
    private static List<short[]> DecodeEveryMcu(byte[] jpeg, out JpegScanTable table, out JpegScan scan)
    {
      table = JpegScanTable.Read(jpeg)!;
      scan = table.Scans[0];
      var decoder = JpegBaselineDecoder.TryCreate(table, scan)!;

      var mcus = new List<short[]>(decoder.McuCount);
      var predictors = new int[decoder.ComponentCount];
      var bits = new JpegBitReader(jpeg.AsSpan(scan.Start, scan.End - scan.Start));

      for (int i = 0; i < decoder.McuCount; i++)
      {
        var coefficients = new short[64 * decoder.BlocksPerMcu];
        decoder.TryDecodeMcu(ref bits, coefficients, predictors).Should().BeTrue();
        mcus.Add(coefficients);
      }

      return mcus;
    }

    /// <summary>A copy of the file with [start, end) never received: zeros, and a gap to walk around.</summary>
    private static byte[] Punch(byte[] jpeg, int start, int end)
    {
      var damaged = jpeg.ToArray();
      Array.Clear(damaged, start, end - start);
      return damaged;
    }

    /// <summary>
    /// Re-decode one placed segment from the bit offset the resync search chose and compare every AC
    /// coefficient against the same MCU of the intact file.
    /// </summary>
    private static void SegmentMatchesTheIntactFile(byte[] damaged, JpegScanTable table, JpegScan scan,
      JpegEntropySegment segment, List<short[]> intact)
    {
      var decoder = JpegBaselineDecoder.TryCreate(table, scan)!;
      var predictors = new int[decoder.ComponentCount];
      var coefficients = new short[64 * decoder.BlocksPerMcu];
      var bits = new JpegBitReader(
        damaged.AsSpan(scan.Start + segment.Start + segment.SkipBytes, segment.End - segment.Start - segment.SkipBytes),
        segment.SkipBits);

      for (int i = 0; i < segment.McuCount; i++)
      {
        decoder.TryDecodeMcu(ref bits, coefficients, predictors).Should().BeTrue();

        var expected = intact[segment.FirstMcu + i];
        for (int b = 0; b < decoder.BlocksPerMcu; b++)
          for (int k = 1; k < 64; k++)
            coefficients[64 * b + k].Should().Be(expected[64 * b + k],
              $"MCU {segment.FirstMcu + i}, block {b}, coefficient {k} must be the one the encoder wrote");
      }
    }


    // ---- the counting argument, on files whose true answer is known ----------------------------------

    [Theory]
    [InlineData("jy1sat_img9", 6000, 6054)]
    [InlineData("jy1sat_img7", 5000, 5054)]
    [InlineData("hades-sa_img235_complete", 1000, 1054)]
    [InlineData("testcard_420", 1500, 1554)]
    public void OneGap_PlacesBothRunsExactlyAndCountsWhatItDestroyed(string name, int gapAt, int gapEnd)
    {
      var jpeg = Reference("Ssdv", name);
      var intact = DecodeEveryMcu(jpeg, out var table, out var scan);
      var damaged = Punch(jpeg, scan.Start + gapAt, scan.Start + gapEnd);

      var walk = JpegEntropyWalker.TryWalk(damaged, table, scan,
        [(scan.Start + gapAt, scan.Start + gapEnd)]);

      walk.Should().NotBeNull();
      walk!.Segments.Should().HaveCount(2);
      walk.DeclinedRuns.Should().Be(0);

      // the two anchors: the first run starts the frame, the last one ends it
      walk.Segments[0].FirstMcu.Should().Be(0);
      (walk.Segments[1].FirstMcu + walk.Segments[1].McuCount).Should().Be(walk.McuCount);

      // and the accounting partitions the frame with nothing left over
      walk.MissingMcus.Should().ContainSingle();
      (walk.RecoveredMcus + walk.MissingMcus.Sum(m => m.McuCount)).Should().Be(walk.McuCount);

      SegmentMatchesTheIntactFile(damaged, table, scan, walk.Segments[0], intact);
      SegmentMatchesTheIntactFile(damaged, table, scan, walk.Segments[1], intact);
    }

    [Fact]
    public void TwoGaps_PlaceTheTwoAnchorsAndDeclineTheRunBetweenThem()
    {
      // The middle run is anchored at neither end and cannot be placed: its MCU count is known exactly,
      // but both gaps around it destroyed an unknown number of MCUs, which is two unknowns against the one
      // equation saying the counts and the losses sum to the frame. Declining is the answer, and the two
      // runs that do touch an end of the scan are still placed exactly.
      var jpeg = Reference("Ssdv", "jy1sat_img9");
      var intact = DecodeEveryMcu(jpeg, out var table, out var scan);
      var damaged = Punch(Punch(jpeg, scan.Start + 3000, scan.Start + 3054), scan.Start + 8000, scan.Start + 8054);

      var walk = JpegEntropyWalker.TryWalk(damaged, table, scan,
        [(scan.Start + 3000, scan.Start + 3054), (scan.Start + 8000, scan.Start + 8054)]);

      walk.Should().NotBeNull();
      walk!.Segments.Should().HaveCount(2);
      walk.DeclinedRuns.Should().Be(1);
      walk.Segments[0].FirstMcu.Should().Be(0);
      (walk.Segments[1].FirstMcu + walk.Segments[1].McuCount).Should().Be(walk.McuCount);

      // everything between the two anchors is reported missing rather than placed somewhere plausible
      walk.MissingMcus.Should().ContainSingle();
      (walk.RecoveredMcus + walk.MissingMcus.Sum(m => m.McuCount)).Should().Be(walk.McuCount);

      foreach (var segment in walk.Segments) SegmentMatchesTheIntactFile(damaged, table, scan, segment, intact);
    }


    // ---- the generated sweep, over every fragment-aligned gap ----------------------------------------

    /// <summary>
    /// Where every MCU of a complete file begins, as a bit offset into the scan, followed by the offset
    /// the last one ends on — the encoder's own grid, read off the intact file before the test damages
    /// it. A run is placed correctly exactly when the bit offset the resync search chose is one of these
    /// entries and the MCU index the walk claims is that entry's index. Nothing about that is
    /// approximate, which is what makes a sweep worth generating: one bit out is a different entry or no
    /// entry at all.
    /// </summary>
    private static List<int> McuGrid(byte[] jpeg, JpegScanTable table, JpegScan scan)
    {
      var decoder = JpegBaselineDecoder.TryCreate(table, scan)!;
      var predictors = new int[decoder.ComponentCount];
      var coefficients = new short[64 * decoder.BlocksPerMcu];
      var bits = new JpegBitReader(jpeg.AsSpan(scan.Start, scan.End - scan.Start));

      var grid = new List<int>(decoder.McuCount + 1);
      for (int i = 0; i < decoder.McuCount; i++)
      {
        var (b, p) = bits.Position;
        grid.Add(8 * b + p);
        decoder.TryDecodeMcu(ref bits, coefficients, predictors).Should().BeTrue();
      }

      var (endByte, endBits) = bits.Position;
      grid.Add(8 * endByte + endBits);

      return grid;
    }

    /// <summary>
    /// The gap starts to try, in file coordinates: multiples of the 54-byte Geoscan v2 fragment, which is
    /// the only granularity a lost gap can have, spread evenly over the part of the scan that leaves both
    /// anchors something to work with. Two fragments at the head so the left anchor holds MCUs, and eight
    /// at the tail so the right one holds more than the resync window costs.
    /// </summary>
    private static List<int> AlignedGapStarts(JpegScan scan, int gapBytes, int count)
    {
      const int fragment = 54;

      int first = (scan.Start + 2 * fragment + fragment - 1) / fragment * fragment;
      int last = (scan.End - 8 * fragment - gapBytes) / fragment * fragment;

      var starts = new List<int>();
      if (last < first) return starts;

      int stride = Math.Max(1, (last - first) / (count * fragment)) * fragment;
      for (int at = first; at <= last; at += stride) starts.Add(at);

      return starts;
    }

    [Theory]
    [InlineData("jy1sat_img9", 54, 0, 26)]
    [InlineData("jy1sat_img9", 216, 0, 21)]
    [InlineData("jy1sat_img9", 864, 0, 26)]
    [InlineData("jy1sat_img7", 54, 0, 27)]
    [InlineData("jy1sat_img7", 216, 0, 27)]
    [InlineData("jy1sat_img7", 864, 0, 17)]
    [InlineData("hades-sa_img235_complete", 54, 0, 21)]
    [InlineData("hades-sa_img235_complete", 216, 0, 21)]
    [InlineData("hades-sa_img235_complete", 864, 0, 21)]
    [InlineData("testcard_420", 54, 0, 10)]
    [InlineData("testcard_420", 216, 1, 10)]
    [InlineData("testcard_420", 864, 1, 10)]
    public void TheSweep_PlacesEveryRunOnTheEncodersOwnGrid_ExceptWhereTheWindowCannotReach(
      string name, int gapBytes, int expectedOffGrid, int worstCost)
    {
      // The four single cases above prove the walk on one gap each; this is the same claim over every
      // place the gap could have fallen. The assertions are exact and structural: the left anchor stops
      // on the last MCU whose every bit arrived, and the right anchor resumes on a bit offset that is an
      // MCU boundary of the intact file at precisely the index it claims.
      //
      // `expectedOffGrid` is the accepted residual, and it is a count of known cases rather than a
      // tolerance. The resync window has to reach the run's first whole MCU for B4's argument to apply,
      // and on content whose MCUs are long enough it does not; where that happens the search settles
      // inside the MCU it names instead of on its boundary. Only the synthetic test card does this, and
      // only on the one run a gap can reach two ways — see *B6* for what was measured and why widening
      // the window is not the answer. The bound asserted for those cases is that the miss stays within
      // the MCU it claims, which is what keeps it to a few MCUs of damage.
      //
      // `worstCost` is measured, not chosen: the MCUs between the first whole MCU after the gap and the
      // one the search could prove, which is the search window's price on the runs it did place.
      var jpeg = Reference("Ssdv", name);
      var table = JpegScanTable.Read(jpeg)!;
      var scan = table.Scans[0];
      var grid = McuGrid(jpeg, table, scan);
      int mcuCount = grid.Count - 1;

      var starts = AlignedGapStarts(scan, gapBytes, 24);
      starts.Should().NotBeEmpty("a {0}-byte gap has to fit inside this file's scan", gapBytes);

      int worst = 0, offGrid = 0;
      foreach (int gapAt in starts)
      {
        int gapEnd = gapAt + gapBytes;
        var damaged = Punch(jpeg, gapAt, gapEnd);
        var walk = JpegEntropyWalker.TryWalk(damaged, table, scan, [(gapAt, gapEnd)]);

        string where = $"gap of {gapBytes} at {gapAt}";
        walk.Should().NotBeNull("{0} leaves both ends of the scan intact", where);
        walk!.McuCount.Should().Be(mcuCount);
        walk.Segments.Should().HaveCount(2, "{0} anchors at both ends", where);
        walk.DeclinedRuns.Should().Be(0, "{0} is the one-gap case, which is determined", where);

        // the left anchor: MCU 0, stopping on the last MCU whose every bit arrived before the gap. The
        // next one needs a byte the gap took, which is what pins the count rather than bounding it.
        var left = walk.Segments[0];
        left.FirstMcu.Should().Be(0, "{0}: the run that starts the scan starts on MCU 0", where);
        grid[left.McuCount].Should().BeLessThanOrEqualTo(8 * (gapAt - scan.Start),
          "{0}: the left anchor cannot claim an MCU that needed a byte from inside the gap", where);
        grid[left.McuCount + 1].Should().BeGreaterThan(8 * (gapAt - scan.Start),
          "{0}: nor stop short of one whose bits all arrived", where);

        // the right anchor counts out to the end of the frame, and never claims an MCU the gap destroyed:
        // the first whole MCU after the gap is the earliest it could honestly start on
        var right = walk.Segments[1];
        (right.FirstMcu + right.McuCount).Should().Be(mcuCount,
          "{0}: the run that ends the scan ends on the last MCU", where);

        int firstWhole = grid.FindIndex(b => b >= 8 * (gapEnd - scan.Start));
        right.FirstMcu.Should().BeGreaterThanOrEqualTo(firstWhole,
          "{0}: MCU {1} is the first the gap left whole", where, firstWhole);

        // placed exactly: the offset the search chose is that MCU's own bit offset in the intact file,
        // and what it cost beyond the first whole MCU is the search window's price
        int at = 8 * (right.Start + right.SkipBytes) + right.SkipBits;
        if (at == grid[right.FirstMcu])
        {
          worst = Math.Max(worst, right.FirstMcu - firstWhole);
          continue;
        }

        // or the accepted residual: the window never reached this run's first whole MCU, so no candidate
        // sat on the encoder's grid and the search settled short. It keeps the index and misses the
        // boundary, which bounds the damage to the MCUs before the stream resynchronises itself.
        offGrid++;
        at.Should().BeInRange(grid[right.FirstMcu] + 1, grid[right.FirstMcu + 1] - 1,
          "{0}: an accepted miss stays inside the MCU it names", where);
      }

      offGrid.Should().Be(expectedOffGrid,
        "the runs whose first whole MCU the window cannot reach are a known, counted set");
      worst.Should().BeLessThanOrEqualTo(worstCost,
        "the search window's price is a handful of MCUs at the head of the run, not a drifting one");
    }


    // ---- the DC level, which is estimated rather than counted ---------------------------------------

    /// <summary>
    /// The offset the segment really needs, from the file that was complete before the test damaged it:
    /// the DC the encoder wrote at the segment's first MCU, less the DC a predictor chain restarting from
    /// zero decodes there. Every block of the segment is that same constant away from the truth, so one
    /// MCU is enough to measure it.
    /// </summary>
    private static int[] TrueDcOffsets(byte[] damaged, JpegScanTable table, JpegScan scan,
      JpegEntropySegment segment, List<short[]> intact)
    {
      var decoder = JpegBaselineDecoder.TryCreate(table, scan)!;
      var predictors = new int[decoder.ComponentCount];
      var coefficients = new short[64 * decoder.BlocksPerMcu];
      var bits = new JpegBitReader(
        damaged.AsSpan(scan.Start + segment.Start + segment.SkipBytes, segment.End - segment.Start - segment.SkipBytes),
        segment.SkipBits);

      decoder.TryDecodeMcu(ref bits, coefficients, predictors).Should().BeTrue();

      var offsets = new int[decoder.ComponentCount];
      for (int c = 0; c < decoder.ComponentCount; c++)
      {
        int block = decoder.Layout[c].FirstBlock;
        offsets[c] = intact[segment.FirstMcu][64 * block] - coefficients[64 * block];
      }

      return offsets;
    }

    /// <summary>
    /// The step the picture itself carries across the seam, from the intact file rather than from the
    /// damaged one: the median DC difference over the pairs of blocks that touch. The gap destroyed no
    /// coefficients here — only the predictor value that indexes them — so this is exactly what the seam
    /// estimate is worth on this picture, and it is what that estimate will be wrong by.
    /// </summary>
    private static int[] TrueSeamSteps(JpegScanTable table, JpegScan scan, JpegEntropySegment above,
      JpegEntropySegment below, List<short[]> intact)
    {
      var decoder = JpegBaselineDecoder.TryCreate(table, scan)!;
      int perRow = table.McusPerRow;
      int first = Math.Max(below.FirstMcu, above.FirstMcu + perRow);
      int last = Math.Min(below.FirstMcu + below.McuCount, above.FirstMcu + above.McuCount + perRow);

      var offsets = new int[decoder.ComponentCount];
      for (int c = 0; c < decoder.ComponentCount; c++)
      {
        var (firstBlock, h, v) = decoder.Layout[c];
        var differences = new List<int>();

        for (int m = first; m < last; m++)
          for (int x = 0; x < h; x++)
            differences.Add(
              intact[m - perRow][64 * (firstBlock + (v - 1) * h + x)] - intact[m][64 * (firstBlock + x)]);

        differences.Sort();
        offsets[c] = differences.Count == 0 ? 0 : differences[differences.Count / 2];
      }

      return offsets;
    }

    [Theory]
    [InlineData("jy1sat_img9", 6000, 6054, 0)]
    [InlineData("jy1sat_img7", 5000, 5054, 0)]
    [InlineData("hades-sa_img235_complete", 1000, 1054, 32)]
    [InlineData("testcard_420", 1500, 1554, 60)]
    public void TheSeam_EstimatesTheRightAnchorsDcLevel(string name, int gapAt, int gapEnd, int worstError)
    {
      // `worstError` is measured, not chosen: it is the largest step the picture itself carries across the
      // seam, in quantised DC units. On the two photographs there is none and the estimate is exact. On
      // hades-sa the gap left only four MCUs with a neighbour above them and those four straddle an edge;
      // on the synthetic test card, five. Nothing in the entropy data says which side of an edge the level
      // belongs to, so that step is the method's floor rather than a defect in it — which is why the
      // second assertion below is the load-bearing one.
      var jpeg = Reference("Ssdv", name);
      var intact = DecodeEveryMcu(jpeg, out var table, out var scan);
      var damaged = Punch(jpeg, scan.Start + gapAt, scan.Start + gapEnd);

      var walk = JpegEntropyWalker.TryWalk(damaged, table, scan, [(scan.Start + gapAt, scan.Start + gapEnd)]);

      walk.Should().NotBeNull();
      walk!.Segments.Should().HaveCount(2);

      // the run that starts the scan starts where the encoder's own predictor chain did
      walk.Segments[0].DcOffsets.Should().AllBeEquivalentTo(0);

      var truth = TrueDcOffsets(damaged, table, scan, walk.Segments[1], intact);
      var steps = TrueSeamSteps(table, scan, walk.Segments[0], walk.Segments[1], intact);

      for (int c = 0; c < truth.Length; c++)
      {
        walk.Segments[1].DcOffsets[c].Should().BeCloseTo(truth[c], (uint)worstError,
          $"component {c}'s DC level has to come back off the seam");

        // and exactly: the estimate off the damaged file is the true level plus the step the picture
        // carries across the seam, and nothing else — so the decode, the placement, the block geometry and
        // the median are all doing what they claim, and the whole of the error is the picture's
        walk.Segments[1].DcOffsets[c].Should().Be(truth[c] + steps[c],
          $"component {c}'s residual is the picture's own step across the seam and nothing else");
      }
    }


    // ---- the real reception, with the gaps the sky actually left ------------------------------------

    [Fact]
    public void TheRealGeoscanPicture_RecoversTheEndOfTheImageAsWellAsTheStart()
    {
      // Geoscan-5 picture 5, merged across the three runs of the 2026-09-12/13 capture: 92.5 % of the
      // bytes, in fifteen pieces, with fourteen gaps inside the scan. What the emitter can show today is
      // the run before the first gap and nothing after it, so the bottom three quarters of the picture is
      // noise. The right anchor is what this is for: the last 424 MCUs of the frame are placed exactly,
      // and the thirteen runs between the two anchors are declined rather than put somewhere plausible.
      var buffer = new SparseImageBuffer();
      foreach (var f in MergedPicture5().Fragments) buffer.Write(f.Id, f.Bytes);

      var table = JpegScanTable.Read(buffer.Span)!;
      var scan = table.Scans[0];
      table.Baseline.Should().BeTrue();
      table.RestartInterval.Should().Be(0, "no file in the corpus carries a DRI");

      var gaps = buffer.Gaps(scan.Start, scan.End);
      gaps.Should().HaveCount(14);

      var walk = JpegEntropyWalker.TryWalk(buffer.Span, table, scan, gaps);

      walk.Should().NotBeNull();
      walk!.McuCount.Should().Be(1900);
      walk.Segments.Should().HaveCount(2);
      walk.DeclinedRuns.Should().Be(13);

      // the left anchor is what the trusted prefix already showed; the right anchor is new
      walk.Segments[0].FirstMcu.Should().Be(0);
      walk.Segments[0].McuCount.Should().Be(545);
      walk.Segments[1].FirstMcu.Should().Be(1476);
      (walk.Segments[1].FirstMcu + walk.Segments[1].McuCount).Should().Be(1900);
      walk.RecoveredMcus.Should().Be(969, "51 % of the frame, against the 29 % the first gap allows today");

      // the resync landed 191 bytes and 3 bits into the run, which is the cost of the search window
      walk.Segments[1].SkipBytes.Should().Be(191);
      walk.Segments[1].SkipBits.Should().Be(3);

      // 931 MCUs lie between the two anchors, which is many rows, so no MCU of the right anchor has a
      // placed one above it and the seam has nothing to read. The offset stays at zero: the bottom of the
      // picture comes back with its structure right and its level unknown, which is the honest answer and
      // the mildest way this can be wrong.
      walk.Segments[1].DcOffsets.Should().AllBeEquivalentTo(0);

      (walk.RecoveredMcus + walk.MissingMcus.Sum(m => m.McuCount)).Should().Be(walk.McuCount);
    }


    // ---- the re-emission, which is what reaches the disk -----------------------------------------------

    /// <summary>
    /// The DC of every block of one MCU, which is the part of a repaired file the seam estimate rather
    /// than the counting argument is responsible for.
    /// </summary>
    private static int[] BlockDc(short[] mcu, int blocksPerMcu)
    {
      var dc = new int[blocksPerMcu];
      for (int b = 0; b < blocksPerMcu; b++) dc[b] = mcu[64 * b];
      return dc;
    }

    [Theory]
    [InlineData("jy1sat_img9", 6000, 6054)]
    [InlineData("jy1sat_img7", 5000, 5054)]
    [InlineData("hades-sa_img235_complete", 1000, 1054)]
    [InlineData("testcard_420", 1500, 1554)]
    public void TheRepairedFile_IsAWholeJpegWhosePlacedMcusAreTheEncodersOwn(string name, int gapAt, int gapEnd)
    {
      // The walk knows where the MCUs go; this is the other half, which puts them there. The assertions
      // are the same kind as the walk's — exact, against the file that was complete before the test
      // damaged it — because the repair re-encodes coefficients rather than pixels and so has no
      // tolerance to spend.
      var jpeg = Reference("Ssdv", name);
      var intact = DecodeEveryMcu(jpeg, out var table, out var scan);
      var damaged = Punch(jpeg, scan.Start + gapAt, scan.Start + gapEnd);
      var gaps = new List<(int, int)> { (scan.Start + gapAt, scan.Start + gapEnd) };

      var repaired = JpegEntropyRepair.TryRepair(damaged, gaps);

      repaired.Should().NotBeNull();
      repaired!.Take(scan.Start).Should().Equal(jpeg.Take(scan.Start),
        "the header and the SOS are the file's own, and the re-encode uses the tables they declare");
      repaired![^2].Should().Be(0xFF);
      repaired![^1].Should().Be(0xD9);

      // it is a whole baseline JPEG: every MCU the frame header declares decodes, in one unbroken run,
      // which is the thing the damaged file cannot do and the reason a viewer can open this one
      var back = DecodeEveryMcu(repaired, out _, out _);
      back.Should().HaveCount(intact.Count);

      var walk = JpegEntropyWalker.TryWalk(damaged, table, scan, gaps)!;
      var decoder = JpegBaselineDecoder.TryCreate(table, scan)!;

      foreach (var segment in walk.Segments)
      {
        // the structure: every AC coefficient of every placed MCU is the one the encoder wrote, at the
        // MCU index the encoder wrote it at
        for (int i = 0; i < segment.McuCount; i++)
          for (int b = 0; b < decoder.BlocksPerMcu; b++)
            for (int k = 1; k < 64; k++)
              back[segment.FirstMcu + i][64 * b + k].Should().Be(intact[segment.FirstMcu + i][64 * b + k],
                $"MCU {segment.FirstMcu + i}, block {b}, coefficient {k} must survive the round trip");

        // the level: the DC is out by one constant per component and no more, which is precisely the
        // seam estimate's error and is what TheSeam_EstimatesTheRightAnchorsDcLevel measures. Zero for
        // the anchor that starts the scan, whose predictor chain never restarted.
        var first = BlockDc(back[segment.FirstMcu], decoder.BlocksPerMcu)
          .Zip(BlockDc(intact[segment.FirstMcu], decoder.BlocksPerMcu), (a, b) => a - b).ToArray();

        for (int c = 0; c < decoder.ComponentCount; c++)
        {
          var (firstBlock, h, v) = decoder.Layout[c];
          if (segment.FirstMcu == 0)
            first[firstBlock].Should().Be(0, "the run that starts the scan needs no DC correction");

          for (int i = 0; i < segment.McuCount; i++)
            for (int b = firstBlock; b < firstBlock + h * v; b++)
              (back[segment.FirstMcu + i][64 * b] - intact[segment.FirstMcu + i][64 * b])
                .Should().Be(first[firstBlock],
                  $"component {c} of MCU {segment.FirstMcu + i} carries one level error, not a drifting one");
        }
      }
    }

    [Theory]
    [InlineData("jy1sat_img9", 6000, 6054)]
    [InlineData("testcard_420", 1500, 1554)]
    public void TheRepairedFile_FillsWhatTheGapDestroyedWithNeutralGray(string name, int gapAt, int gapEnd)
    {
      // A destroyed MCU is worth nothing, and saying so is the honest emission: a zero DC is the block
      // average the level shift puts at mid-gray, and zero AC is a flat block. The alternative is the
      // noise the undecodable bytes render as, which reads as recovered data and is not.
      var jpeg = Reference("Ssdv", name);
      DecodeEveryMcu(jpeg, out var table, out var scan);
      var damaged = Punch(jpeg, scan.Start + gapAt, scan.Start + gapEnd);
      var gaps = new List<(int, int)> { (scan.Start + gapAt, scan.Start + gapEnd) };

      var repaired = JpegEntropyRepair.TryRepair(damaged, gaps)!;
      var back = DecodeEveryMcu(repaired, out _, out _);
      var walk = JpegEntropyWalker.TryWalk(damaged, table, scan, gaps)!;

      walk.MissingMcus.Should().NotBeEmpty();
      foreach (var (firstMcu, count) in walk.MissingMcus)
        for (int i = 0; i < count; i++)
          back[firstMcu + i].Should().OnlyContain(c => c == 0, $"MCU {firstMcu + i} never arrived");

      // and nothing is lost or invented: the placed MCUs and the gray ones partition the frame
      (walk.RecoveredMcus + walk.MissingMcus.Sum(m => m.McuCount)).Should().Be(back.Count);
    }

    [Fact]
    public void TheRealGeoscanPicture_IsEmittedAsAFileAViewerOpens()
    {
      // The same reception as above, through the emitter rather than through the walker: what the repair
      // is for is the bytes that get auto-saved, and this is the path they take.
      var buffer = new SparseImageBuffer();
      foreach (var f in MergedPicture5().Fragments) buffer.Write(f.Id, f.Bytes);

      var jpeg = RawJpegEmitter.ToJpeg(buffer);

      jpeg.Should().NotBeEmpty();
      var back = DecodeEveryMcu(jpeg, out _, out _);
      back.Should().HaveCount(1900);

      // the emitted file is the repaired one and not the buffer: the 931 MCUs between the two anchors are
      // declined and come back gray. Counting gray MCUs over the whole frame would not say this — 1110 of
      // them are, because the picture has flat regions of its own — so the range itself is what is asserted.
      jpeg.Should().NotEqual(buffer.Span.ToArray());
      for (int i = 545; i < 1476; i++)
        back[i].Should().OnlyContain(c => c == 0, $"MCU {i} lies between the two anchors");
    }

    [Fact]
    public void TheEmission_ReportsWhatTheRepairDidAndCanBeSwitchedOff()
    {
      // What the operator is shown, and the one switch that changes it. The counts are the walk's own —
      // the info pane and the menu item both read this rather than recomputing anything.
      var buffer = new SparseImageBuffer();
      foreach (var f in MergedPicture5().Fragments) buffer.Write(f.Id, f.Bytes);

      var repaired = RawJpegEmitter.ToJpeg(buffer, true, out var info);

      info.Should().NotBeNull();
      info!.Gaps.Should().Be(14);
      info.PlacedMcus.Should().Equal(545, 424);
      info.DeclinedRuns.Should().Be(13);
      info.RecoveredMcus.Should().Be(969);
      info.McuCount.Should().Be(1900);
      info.RecoveredMcus.Should().Be(info.PlacedMcus.Sum(), "the total is the runs and nothing else");

      // switched off, the emission is the buffer again — exactly, because the repair never altered it
      var raw = RawJpegEmitter.ToJpeg(buffer, false, out var none);

      none.Should().BeNull("nothing was repaired, so there is nothing to report");
      raw.Should().NotEqual(repaired);
      raw.Take(buffer.Length).Should().Equal(buffer.Span.ToArray(),
        "the unrepaired emission is the bytes that arrived");
      raw.Length.Should().Be(buffer.Length + 2, "with an EOI added, which this reception never received");
      raw[^2..].Should().Equal(0xFF, 0xD9);

      // and a complete file reports nothing either way: there was no repair to do
      var whole = new SparseImageBuffer();
      whole.Write(0, Reference("Ssdv", "hades-sa_img235_complete"));
      RawJpegEmitter.ToJpeg(whole, true, out var clean);
      clean.Should().BeNull();
    }


    [Fact]
    public void AFileWithNoGapInIt_IsNotRepaired()
    {
      // The emitter calls the repair on every image it emits, so the answer for a complete file has to be
      // "nothing to do" rather than a re-encode: a file that arrived whole is already the encoder's own
      // bytes, and passing it through this would be a lossless no-op at best.
      var jpeg = Reference("Ssdv", "hades-sa_img235_complete");

      JpegEntropyRepair.TryRepair(jpeg, []).Should().BeNull();
      JpegEntropyRepair.TryRepair(Reference("Geoscan", "geoscan5_pic14_progressive"), [(100, 154)])
        .Should().BeNull("SOF2 is not baseline");
    }


    // ---- refusing -------------------------------------------------------------------------------------

    [Fact]
    public void AFileWithNoGapInItsScan_IsNotWalkedAtAll()
    {
      var jpeg = Reference("Ssdv", "hades-sa_img235_complete");
      var table = JpegScanTable.Read(jpeg)!;

      JpegEntropyWalker.TryWalk(jpeg, table, table.Scans[0], []).Should().BeNull();
      JpegEntropyWalker.TryWalk(jpeg, table, table.Scans[0], [(0, 2)])
        .Should().BeNull("a gap in the header is not this walk's problem");
    }

    [Fact]
    public void AProgressiveFile_IsNotWalked()
    {
      var jpeg = Reference("Geoscan", "geoscan5_pic14_progressive");
      var table = JpegScanTable.Read(jpeg)!;
      var scan = table.Scans[0];

      JpegEntropyWalker.TryWalk(jpeg, table, scan, [(scan.Start + 100, scan.Start + 154)])
        .Should().BeNull("SOF2 is not baseline");
    }


    // ---- the gaps the buffer records ------------------------------------------------------------------

    /// <summary>Picture 5 rebuilt from the three receptions of the real capture, as
    /// <see cref="GeoscanImageCaptureTests"/> builds it.</summary>
    private static ImageProduct MergedPicture5()
    {
      var bytes = File.ReadAllBytes(Path.Combine(TestPaths.DataDir, "Geoscan", "geoscan_v2_images.bin"));
      var assembler = new RawJpegAssembler(RawJpegSource.Geoscan);
      List<ImageProduct> done = [];
      assembler.ImageCompleted += done.Add;
      for (int i = 0; i < bytes.Length / 72; i++)
        assembler.Push(new Frame
        {
          Bytes = bytes.AsSpan(i * 72, 72).ToArray(), CrcValid = true, Framing = Framing.GEOSCAN
        });
      assembler.Flush();

      var receptions = done.Where(p => p.Source == "Geoscan-5" && p.ImageId == 5).Select(p => p.Fragments);
      return RawJpegMerge.Build(receptions, RawJpegMerge.Format, 5, "Geoscan-5")!;
    }

    [Fact]
    public void TheBufferReportsWhereItsHolesAre()
    {
      var buffer = new SparseImageBuffer();
      buffer.Write(0, new byte[10]);
      buffer.Write(20, new byte[10]);
      buffer.Write(40, new byte[10]);

      buffer.Gaps(0, buffer.Length).Should().Equal((10, 20), (30, 40));
      buffer.Gaps(0, 100).Should().Equal((10, 20), (30, 40), (50, 100));
      // the window clips the gap it starts inside
      buffer.Gaps(12, 25).Should().Equal([(12, 20)]);
      buffer.Gaps(0, 10).Should().BeEmpty();
    }
  }
}
