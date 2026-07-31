using System;
using FluentAssertions;
using VE3NEA.SkyTlm.Imaging.RawJpeg;
using Xunit;

namespace VE3NEA.SkyTlm.Tests.Unit
{
  /// <summary>
  /// The sparse buffer, whose whole reason to exist is knowing which bytes are real. Everything the
  /// raw-JPEG family reports about how much of a picture can be trusted comes out of here.
  /// </summary>
  public class SparseImageBufferTests
  {
    private static byte[] Bytes(int n, byte fill = 0xAB)
    {
      var b = new byte[n];
      Array.Fill(b, fill);
      return b;
    }


    // ---- writing and gaps --------------------------------------------------------------------------

    [Fact]
    public void ContiguousWrites_LeaveNoGap()
    {
      var b = new SparseImageBuffer();
      b.Write(0, Bytes(64)).Should().BeTrue();
      b.Write(64, Bytes(64)).Should().BeTrue();

      b.Length.Should().Be(128);
      b.BytesWritten.Should().Be(128);
      b.SpanCount.Should().Be(1, "adjacent runs merge into one");
      b.FirstGapOffset.Should().Be(128);
      b.IsContiguous.Should().BeTrue();
    }

    [Fact]
    public void AGap_StopsTheTrustedPrefixAtIt()
    {
      var b = new SparseImageBuffer();
      b.Write(0, Bytes(64));
      b.Write(128, Bytes(64));      // 64..128 never arrived

      b.Length.Should().Be(192);
      b.BytesWritten.Should().Be(128);
      b.FirstGapOffset.Should().Be(64, "truth stops where the first run does");
      b.TrustedSpan.Length.Should().Be(64);
      b.IsContiguous.Should().BeFalse();
    }

    [Fact]
    public void FillingAGap_ReopensTheTrustedPrefix()
    {
      // the case that matters across passes: a second pass supplies what the first missed.
      var b = new SparseImageBuffer();
      b.Write(0, Bytes(64));
      b.Write(128, Bytes(64));
      b.FirstGapOffset.Should().Be(64);

      b.Write(64, Bytes(64));
      b.SpanCount.Should().Be(1);
      b.FirstGapOffset.Should().Be(192, "the hole is closed, so everything is trustworthy again");
      b.IsContiguous.Should().BeTrue();
    }

    [Fact]
    public void AMissingFirstByte_MakesNothingTrustworthy()
    {
      var b = new SparseImageBuffer();
      b.Write(8, Bytes(64));

      b.FirstGapOffset.Should().Be(0, "the prefix is empty, not the whole run");
      b.TrustedSpan.Length.Should().Be(0);
    }

    [Fact]
    public void OverlappingWrites_AreCountedOnce()
    {
      var b = new SparseImageBuffer();
      b.Write(0, Bytes(64));
      b.Write(32, Bytes(64));

      b.Length.Should().Be(96);
      b.BytesWritten.Should().Be(96, "the overlap is not received twice");
      b.SpanCount.Should().Be(1);
    }

    [Fact]
    public void AnImplausibleOffset_IsRefusedRatherThanAllocated()
    {
      // measured off air: a telemetry frame misread as an image frame yields offsets in the millions.
      var b = new SparseImageBuffer();

      b.Write(16308290, Bytes(64)).Should().BeFalse();
      b.Write(SparseImageBuffer.MaxLength - 32, Bytes(64)).Should().BeFalse("it would run past the cap");
      b.Write(SparseImageBuffer.MaxLength - 64, Bytes(64)).Should().BeTrue("but ending exactly on it is fine");
      b.Write(-1, Bytes(64)).Should().BeFalse();
    }


    // ---- shifting ----------------------------------------------------------------------------------

    [Fact]
    public void ShiftingForward_DropsTheBytesBeforeTheNewZero()
    {
      var b = new SparseImageBuffer();
      b.Write(0, [1, 2, 3, 4, 5, 6, 7, 8]);

      b.Shift(3);

      b.Length.Should().Be(5);
      b.Span.ToArray().Should().Equal([4, 5, 6, 7, 8]);
      b.BytesWritten.Should().Be(5);
      b.IsContiguous.Should().BeTrue();
    }

    [Fact]
    public void ShiftingBack_MakesRoomAndLeavesItUntrusted()
    {
      var b = new SparseImageBuffer();
      b.Write(0, [4, 5, 6, 7, 8]);

      b.Shift(-3);

      b.Length.Should().Be(8);
      b.Span.ToArray().Should().Equal([0, 0, 0, 4, 5, 6, 7, 8]);
      b.BytesWritten.Should().Be(5, "the room made is not data");
      b.FirstGapOffset.Should().Be(0, "and it is a gap, so nothing before it can be believed");
    }

    [Fact]
    public void ShiftingPastEverything_EmptiesTheBuffer()
    {
      var b = new SparseImageBuffer();
      b.Write(0, Bytes(16));

      b.Shift(64);

      b.Length.Should().Be(0);
      b.BytesWritten.Should().Be(0);
    }

    [Fact]
    public void ShiftingPreservesGapStructure()
    {
      var b = new SparseImageBuffer();
      b.Write(0, Bytes(32));
      b.Write(64, Bytes(32));

      b.Shift(16);

      b.Length.Should().Be(80);
      b.SpanCount.Should().Be(2, "shifting moves the runs, it does not merge them");
      b.FirstGapOffset.Should().Be(16);
      b.BytesWritten.Should().Be(48);
    }
  }
}
