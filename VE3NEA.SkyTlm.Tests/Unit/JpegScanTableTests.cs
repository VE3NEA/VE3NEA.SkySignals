using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using VE3NEA.SkyTlm.Imaging.RawJpeg;
using VE3NEA.SkyTlm.Tests.Regression;
using Xunit;

namespace VE3NEA.SkyTlm.Tests.Unit
{
  /// <summary>
  /// What the marker walk reports beyond the scan boundaries: the sampling factors, the Huffman tables,
  /// the restart interval and the MCU count. The entropy walker decodes from these, so a wrong value
  /// here is not a wrong number in a report — it is a decoder that reads the wrong table and a repair
  /// that lands MCUs in the wrong place.
  /// <para>
  /// Every expected value is read out of the committed reference files by an independent script rather
  /// than by this code, which is what makes the comparison a cross-check. The one exception is the DRI
  /// case, which no file in the corpus exercises: nothing the fleet sends carries a restart interval,
  /// and that is precisely why the walker exists, so the only way to prove the field is read is to
  /// synthesise a file that has one.
  /// </para>
  /// </summary>
  public class JpegScanTableTests
  {
    private static byte[] Reference(string folder, string name) =>
      File.ReadAllBytes(Path.Combine(TestPaths.DataDir, folder, name + ".ref.jpg"));


    // ---- the frame detail, off the real files -------------------------------------------------------

    [Fact]
    public void TheBaselineFrameHeader_GivesTheSamplingFactorsAndTheMcuGeometry()
    {
      var table = JpegScanTable.Read(Reference("Geoscan", "geoscan5_pic5_baseline"));

      table.Should().NotBeNull();
      table!.Baseline.Should().BeTrue("picture 5 is SOF0");
      table.Width.Should().Be(800);
      table.Height.Should().Be(600);

      // 4:2:0 — one 2x2 luma component and two 1x1 chroma, so an MCU is 16 x 16 px
      table.Components.Select(c => (c.Id, c.H, c.V, c.QuantTable))
        .Should().Equal((1, 2, 2, 0), (2, 1, 1, 1), (3, 1, 1, 1));

      // 50 MCUs per row is what made picture 5 the easy corner of B2's distribution: a small lag stays
      // inside one row. 1900 is the number the right-to-left accounting counts back from.
      table.McusPerRow.Should().Be(50);
      table.McuRows.Should().Be(38);
      table.McuCount.Should().Be(1900);
    }

    [Fact]
    public void TheBaselineTables_AreReadWithTheScanThatSelectsThem()
    {
      var table = JpegScanTable.Read(Reference("Geoscan", "geoscan5_pic5_baseline"));

      // two DC tables and two AC tables, in the order the DHT segments declare them
      table!.HuffmanTables.Select(t => (t.Class, t.Id, t.Values.Count))
        .Should().Equal((0, 0, 7), (1, 0, 47), (0, 1, 5), (1, 1, 18));
      table.HuffmanTables.Should().OnlyContain(t => t.Counts.Count == 16);

      // the scan header is what says which of the four a component decodes with: luma on table 0,
      // both chroma on table 1
      table.Scans.Should().ContainSingle();
      table.Scans[0].Components.Select(c => (c.Id, c.DcTable, c.AcTable))
        .Should().Equal((1, 0, 0), (2, 1, 1), (3, 1, 1));
    }

    [Theory]
    [InlineData("jy1sat_img9", 368, 656, 23, 41)]
    [InlineData("jy1sat_img7", 544, 304, 34, 19)]
    [InlineData("hades-sa_img235_complete", 320, 240, 20, 15)]
    public void TheSweepFixtures_AreBaselineFourTwoZeroWithNoRestartInterval(
      string name, int width, int height, int mcusPerRow, int mcuRows)
    {
      // the three complete reference files B6's ground-truth sweep punches gaps into. What makes a
      // sweep possible is exactly what is asserted here: baseline, and no DRI to resynchronise on.
      var table = JpegScanTable.Read(Reference("Ssdv", name));

      table!.Baseline.Should().BeTrue();
      table.Progressive.Should().BeFalse();
      table.RestartInterval.Should().Be(0);
      table.Width.Should().Be(width);
      table.Height.Should().Be(height);
      table.McusPerRow.Should().Be(mcusPerRow);
      table.McuRows.Should().Be(mcuRows);
      table.Components.Select(c => (c.H, c.V)).Should().Equal((2, 2), (1, 1), (1, 1));
    }

    [Fact]
    public void AProgressiveFile_IsNotBaseline()
    {
      // SOF2 is neither, and the walker refuses on Baseline rather than on !Progressive, because
      // extended sequential and lossless are not progressive either
      var table = JpegScanTable.Read(Reference("Geoscan", "geoscan5_pic14_progressive"));

      table!.Progressive.Should().BeTrue();
      table.Baseline.Should().BeFalse();
    }


    // ---- the restart interval, which no real file in hand carries ------------------------------------

    [Fact]
    public void ARestartInterval_IsReported()
    {
      var table = JpegScanTable.Read(Synthetic(restartInterval: 8));

      table!.RestartInterval.Should().Be(8);
      table.McusPerRow.Should().Be(2, "32 px across at 4:2:0, whose MCU is 16 px wide");
      table.McuRows.Should().Be(1);
    }

    [Fact]
    public void NoRestartInterval_ReadsAsZeroRatherThanAsAbsent()
    {
      JpegScanTable.Read(Synthetic(restartInterval: 0))!.RestartInterval.Should().Be(0);
    }

    /// <summary>
    /// The smallest baseline 4:2:0 file that exercises the walk: a DRI when one is asked for, an SOF0
    /// with three components, a one-table DHT, and a scan whose entropy data is never decoded here.
    /// </summary>
    private static byte[] Synthetic(int restartInterval)
    {
      var jpeg = new List<byte> { 0xFF, 0xD8 };

      if (restartInterval > 0)
        jpeg.AddRange([0xFF, 0xDD, 0x00, 0x04, (byte)(restartInterval >> 8), (byte)restartInterval]);

      // SOF0: 8-bit, 32 x 16, components 1 at 2x2 and 2 and 3 at 1x1
      jpeg.AddRange([0xFF, 0xC0, 0x00, 0x11, 0x08, 0x00, 0x10, 0x00, 0x20, 0x03,
        0x01, 0x22, 0x00, 0x02, 0x11, 0x01, 0x03, 0x11, 0x01]);

      // DHT: one DC table with a single one-bit code
      jpeg.AddRange([0xFF, 0xC4, 0x00, 0x15, 0x00,
        0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00]);

      // SOS: all three components on tables 0/0, then a byte of entropy data
      jpeg.AddRange([0xFF, 0xDA, 0x00, 0x0C, 0x03, 0x01, 0x00, 0x02, 0x00, 0x03, 0x00, 0x00, 0x3F, 0x00]);
      jpeg.Add(0x00);
      jpeg.AddRange([0xFF, 0xD9]);

      return [.. jpeg];
    }
  }
}
