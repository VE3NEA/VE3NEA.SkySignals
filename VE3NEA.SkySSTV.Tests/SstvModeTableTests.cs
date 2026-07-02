using System.Numerics;
using FluentAssertions;
using Xunit;

namespace VE3NEA.SkySSTV.Tests
{
  /// <summary>
  /// Structural checks on the data-driven mode table: VIS parity bytes match the published codes, the
  /// per-mode timing constants sum to the stated line period, and the table is complete and looked up
  /// consistently. Catches a transcription slip at the constant, not later as a mis-decoded image.
  /// </summary>
  public class SstvModeTableTests
  {
    [Theory]
    [InlineData(SstvMode.Robot36, 0x88)]   // mmsstv-confirmed 8-bit VIS (plan §2)
    [InlineData(SstvMode.Robot72, 0x0C)]
    [InlineData(SstvMode.Pd120, 0x5F)]
    [InlineData(SstvMode.Pd180, 0x60)]
    public void VisByte_MatchesPublishedCode(SstvMode mode, int expectedByte)
    {
      SstvModes.Get(mode).VisByte.Should().Be(expectedByte);
    }

    [Fact]
    public void EvenParityByte_MakesTotalOnesEven()
    {
      for (int code = 0; code < 128; code++)
      {
        int b = SstvModes.EvenParityByte(code);
        (b & 0x7F).Should().Be(code, "the low 7 bits carry the data unchanged");
        (BitOperations.PopCount((uint)b) & 1).Should().Be(0, "even parity ⇒ total number of 1s is even");
      }
    }

    [Fact]
    public void TableIsComplete_AndIndexedByEnum()
    {
      SstvModes.All.Should().HaveCount(9);
      foreach (SstvMode m in System.Enum.GetValues<SstvMode>())
        SstvModes.Get(m).Mode.Should().Be(m, "All must be indexed in enum order");
    }

    [Fact]
    public void FromVisByte_RoundTripsEverySupportedMode()
    {
      foreach (var spec in SstvModes.All)
        SstvModes.FromVisByte(spec.VisByte).Should().BeSameAs(spec);

      SstvModes.FromVisByte(0x7F).Should().BeNull("unassigned VIS byte is unrecognized");
    }

    [Fact]
    public void RobotLinePeriods_SumFromSegments()
    {
      var r36 = SstvModes.Get(SstvMode.Robot36);
      // sync + porch + Y + sep + sepPorch + chroma
      (r36.SyncMs + r36.SyncPorchMs + r36.ScanYMs + r36.SepMs + r36.SepPorchMs + r36.ScanChromaMs)
        .Should().BeApproximately(r36.LinePeriodMs, 1e-9);
      r36.LinePeriodMs.Should().BeApproximately(150.0, 1e-9);

      var r72 = SstvModes.Get(SstvMode.Robot72);
      // sync + porch + Y + 2·(sep + sepPorch + chroma)
      (r72.SyncMs + r72.SyncPorchMs + r72.ScanYMs + 2 * (r72.SepMs + r72.SepPorchMs + r72.ScanChromaMs))
        .Should().BeApproximately(r72.LinePeriodMs, 1e-9);
      r72.LinePeriodMs.Should().BeApproximately(300.0, 1e-9);
    }

    [Theory]
    [InlineData(SstvMode.Pd120, 126.1)]    // ~126 s (plan §2)
    [InlineData(SstvMode.Pd180, 187.1)]    // ~187 s
    public void PdLinePeriods_SumFromSegments_AndTotalTimePlausible(SstvMode mode, double approxSeconds)
    {
      var spec = SstvModes.Get(mode);
      // PD: sync + porch + 4·scan, no separators.
      (spec.SyncMs + spec.SyncPorchMs + 4 * spec.ScanYMs).Should().BeApproximately(spec.LinePeriodMs, 1e-9);
      spec.SepMs.Should().Be(0);

      double totalSeconds = spec.LineCount * spec.LinePeriodMs / 1000.0;
      totalSeconds.Should().BeApproximately(approxSeconds, 1.0);
    }

    [Fact]
    public void RowsPerLine_And_LineCount_MatchLayout()
    {
      SstvModes.Get(SstvMode.Robot36).RowsPerLine.Should().Be(1);
      SstvModes.Get(SstvMode.Robot36).LineCount.Should().Be(240);

      var pd = SstvModes.Get(SstvMode.Pd120);
      pd.RowsPerLine.Should().Be(2);
      pd.LineCount.Should().Be(pd.Height / 2);
    }
  }
}
