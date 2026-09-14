using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using VE3NEA.SkyTlm.Imaging.RawJpeg;
using VE3NEA.SkyTlm.Tests.Regression;
using Xunit;

namespace VE3NEA.SkyTlm.Tests.Unit
{
  /// <summary>
  /// The baseline entropy decoder, against a Python decoder written independently from the JPEG spec and
  /// sharing no code with it. What is compared is not "it produced something": it is the exact MCU count,
  /// the exact byte the decode stopped on, the exact number of padding bits left over, and the exact DC
  /// predictor each component ended on. A decoder that is wrong anywhere — one table, one zig-zag index,
  /// one sign — desynchronises and misses all four.
  /// <para>
  /// The three complete reference files decode end to end, which is the positive case. Geoscan picture 5
  /// is the negative one and is the more important of the two: it is a real reception with a real hole,
  /// and what the decoder must do there is <b>stop</b>, at the same place every time, rather than
  /// improvise its way to the end of the file.
  /// </para>
  /// </summary>
  public class JpegBaselineDecoderTests
  {
    private static byte[] Reference(string folder, string name) =>
      File.ReadAllBytes(Path.Combine(TestPaths.DataDir, folder, name + ".ref.jpg"));


    // ---- the three complete files, decoded end to end ------------------------------------------------

    [Theory]
    [InlineData("jy1sat_img9", 943, 12562, 0, 12, 0, 1)]
    [InlineData("jy1sat_img7", 646, 11036, 2, -27, -1, 2)]
    [InlineData("hades-sa_img235_complete", 300, 3040, 7, 1024, 0, 0)]
    public void ACompleteFile_DecodesToTheLastMcuAndConsumesTheScanExactly(
      string name, int mcuCount, int scanLength, int paddingBits, int finalY, int finalCb, int finalCr)
    {
      var jpeg = Reference("Ssdv", name);
      var table = JpegScanTable.Read(jpeg)!;
      var scan = table.Scans[0];
      (scan.End - scan.Start).Should().Be(scanLength);

      var decoder = JpegBaselineDecoder.TryCreate(table, scan);
      decoder.Should().NotBeNull();
      decoder!.McuCount.Should().Be(mcuCount);
      decoder.BlocksPerMcu.Should().Be(6, "4:2:0 is four luma blocks and one of each chroma");

      var coefficients = new short[64 * decoder.BlocksPerMcu];
      var predictors = new int[decoder.ComponentCount];
      var bits = new JpegBitReader(jpeg.AsSpan(scan.Start, scan.End - scan.Start));

      int mcus = 0;
      while (mcus < decoder.McuCount && decoder.TryDecodeMcu(ref bits, coefficients, predictors)) mcus++;

      mcus.Should().Be(mcuCount, "the encoder wrote every MCU of the frame");

      // the whole scan and nothing but the scan: what is left is the encoder's one-bits padding of the
      // final byte, which is also the property the resync search accepts a bit phase on
      bits.ConsumedBytes.Should().Be(scanLength);
      bits.HeldBits.Should().Be(paddingBits);
      predictors.Should().Equal(finalY, finalCb, finalCr);
    }

    [Fact]
    public void TheFirstMcu_DecodesToTheCoefficientsTheReferenceDecoderReports()
    {
      var jpeg = Reference("Ssdv", "hades-sa_img235_complete");
      var table = JpegScanTable.Read(jpeg)!;
      var scan = table.Scans[0];
      var decoder = JpegBaselineDecoder.TryCreate(table, scan)!;

      var coefficients = new short[64 * decoder.BlocksPerMcu];
      var predictors = new int[decoder.ComponentCount];
      var bits = new JpegBitReader(jpeg.AsSpan(scan.Start, scan.End - scan.Start));

      decoder.TryDecodeMcu(ref bits, coefficients, predictors).Should().BeTrue();

      // every non-zero coefficient of all six blocks, in zig-zag order — four luma then Cb then Cr. The
      // DC of a block is the running predictor, not the difference the stream carries.
      var nonZero = Enumerable.Range(0, 6)
        .Select(b => Enumerable.Range(0, 64)
          .Where(i => coefficients[64 * b + i] != 0)
          .Select(i => (i, (int)coefficients[64 * b + i])).ToArray())
        .ToArray();

      nonZero[0].Should().Equal((0, -384));
      nonZero[1].Should().Equal((0, -352), (1, -22));
      nonZero[2].Should().Equal((0, -384), (1, -22));
      nonZero[3].Should().Equal((0, -352));
      nonZero[4].Should().Equal((0, 34));
      nonZero[5].Should().Equal((0, 102));
    }

    [Fact]
    public void TheRunDriver_StopsOnTheMcuLimitAndCarriesOnFromWhereItStopped()
    {
      var jpeg = Reference("Ssdv", "hades-sa_img235_complete");
      var table = JpegScanTable.Read(jpeg)!;
      var scan = table.Scans[0];
      var decoder = JpegBaselineDecoder.TryCreate(table, scan)!;

      var bits = new JpegBitReader(jpeg.AsSpan(scan.Start, scan.End - scan.Start));

      decoder.TryDecodeRun(ref bits, 100, out int first).Should().BeTrue();
      first.Should().Be(100, "the limit is hard: the run stops on it with data still to come");

      decoder.TryDecodeRun(ref bits, decoder.McuCount, out int rest).Should().BeTrue();
      rest.Should().Be(200, "the frame is 300 MCUs and 100 of them are already decoded");
      bits.ConsumedBytes.Should().Be(scan.End - scan.Start);
    }


    // ---- the real reception with the real hole -------------------------------------------------------

    [Fact]
    public void AFileWithAHoleInIt_StopsRatherThanImprovises()
    {
      // Geoscan picture 5: a 54-byte gap at 26.8 % of the file, zero-filled. Past the gap the decoder is
      // reading the tail against a predictor and an MCU index that no longer mean anything, and it keeps
      // finding valid codes for a while — 1112 of the 1900 MCUs — before one of them is not a code at
      // all. That it refuses at all is the point; that it refuses at the same MCU every time is what B3's
      // accounting and B4's resync search are entitled to rely on.
      var jpeg = Reference("Geoscan", "geoscan5_pic5_baseline");
      var table = JpegScanTable.Read(jpeg)!;
      var scan = table.Scans[0];
      var decoder = JpegBaselineDecoder.TryCreate(table, scan)!;

      var bits = new JpegBitReader(jpeg.AsSpan(scan.Start, scan.End - scan.Start));
      decoder.TryDecodeRun(ref bits, decoder.McuCount, out int mcus).Should().BeFalse();

      mcus.Should().Be(1112);
      decoder.McuCount.Should().Be(1900);
      bits.Exhausted.Should().BeFalse("it ran into a bad code, not out of data");
    }

    [Fact]
    public void AProgressiveFile_IsRefusedRatherThanDecodedBadly()
    {
      var jpeg = Reference("Geoscan", "geoscan5_pic14_progressive");
      var table = JpegScanTable.Read(jpeg)!;

      JpegBaselineDecoder.TryCreate(table, table.Scans[0]).Should().BeNull("SOF2 is not baseline");
    }


    // ---- the bit stream ------------------------------------------------------------------------------

    [Fact]
    public void TheBitReader_UnstuffsFfZeroAndStopsAtAMarker()
    {
      // the one thing B4 warns is easy to forget: inside entropy data an 0xFF is written as 0xFF 0x00 and
      // is one byte of data, while an 0xFF followed by anything else is a marker and ends the stream
      var bits = new JpegBitReader([0xFF, 0x00, 0xA5, 0xFF, 0xD9]);

      bits.TryRead(8, out int first).Should().BeTrue();
      first.Should().Be(0xFF);
      bits.TryRead(8, out int second).Should().BeTrue();
      second.Should().Be(0xA5);

      bits.TryRead(1, out _).Should().BeFalse();
      bits.Exhausted.Should().BeTrue();
      bits.ConsumedBytes.Should().Be(3, "the stuffing byte is consumed with the 0xFF it belongs to");
    }

    [Fact]
    public void TheBitReader_CanStartAtAnyBitPhase()
    {
      // what the resync search needs: a segment after a hole starts at a known byte and an unknown bit
      var bits = new JpegBitReader([0b0110_1001, 0b1100_0000], skipBits: 3);

      bits.TryRead(5, out int value).Should().BeTrue();
      value.Should().Be(0b01001);
      bits.TryRead(2, out int next).Should().BeTrue();
      next.Should().Be(0b11);
    }
  }
}
