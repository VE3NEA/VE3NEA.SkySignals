using System;
using FluentAssertions;
using VE3NEA.SkyTlm.Telemetry;
using Xunit;

namespace VE3NEA.SkyTlm.Tests.Unit
{
  /// <summary>
  /// The telemetry <see cref="BitReader"/> is the correctness-critical piece: an MSB-first bit
  /// cursor that must cross byte boundaries for HADES's 12-bit fields, honor le/be byte order for byte-aligned
  /// integers, sign-extend, decode floats, and seek. These pins are tested hardest.
  /// </summary>
  public class BitReaderTests
  {
    [Fact]
    public void ReadBitsBe_IsMsbFirst()
    {
      var r = new BitReader(new byte[] { 0b1010_0000 });
      r.ReadBitsBe(3).Should().Be(0b101UL);   // top three bits, MSB-first
      r.BitPos.Should().Be(3);
    }

    [Fact]
    public void TwelveBitFields_CrossByteBoundary_MsbFirst()
    {
      // 0xAB 0xCD 0xEF = 1010 1011 1100 1101 1110 1111 -> two 12-bit MSB-first fields 0xABC, 0xDEF.
      var r = new BitReader(new byte[] { 0xAB, 0xCD, 0xEF });
      r.ReadUInt(12, littleEndian: false).Should().Be(0xABCUL);
      r.ReadUInt(12, littleEndian: false).Should().Be(0xDEFUL);
      r.BitPos.Should().Be(24);
    }

    [Fact]
    public void TwelveBit_LittleEndianFlag_IsIgnored_ForSubByteWidth()
    {
      // a 12-bit field has no byte order: the le flag must not swap it (only byte-multiple widths swap).
      var be = new BitReader(new byte[] { 0xAB, 0xCD }).ReadUInt(12, littleEndian: false);
      var le = new BitReader(new byte[] { 0xAB, 0xCD }).ReadUInt(12, littleEndian: true);
      le.Should().Be(be).And.Be(0xABCUL);
    }

    [Theory]
    [InlineData(false, 0xDE5F5B00UL)]   // big-endian: MSB-first assembly as-is
    [InlineData(true, 0x005B5FDEUL)]    // little-endian: byte-reversed -> 5,988,318 (the HADES sclock vector)
    public void ThirtyTwoBit_HonorsEndian(bool le, ulong expected)
    {
      new BitReader(new byte[] { 0xDE, 0x5F, 0x5B, 0x00 }).ReadUInt(32, le).Should().Be(expected);
    }

    [Fact]
    public void SixteenBit_LittleEndian_SwapsBytes()
    {
      new BitReader(new byte[] { 0x34, 0x12 }).ReadUInt(16, littleEndian: true).Should().Be(0x1234UL);
      new BitReader(new byte[] { 0x12, 0x34 }).ReadUInt(16, littleEndian: false).Should().Be(0x1234UL);
    }

    [Theory]
    [InlineData(8, new byte[] { 0xFF }, false, -1L)]
    [InlineData(8, new byte[] { 0x80 }, false, -128L)]
    [InlineData(16, new byte[] { 0xFF, 0xFF }, true, -1L)]
    [InlineData(16, new byte[] { 0x00, 0x80 }, true, -32768L)] // 0x8000 little-endian
    public void ReadInt_SignExtends(int bits, byte[] data, bool le, long expected)
    {
      new BitReader(data).ReadInt(bits, le).Should().Be(expected);
    }

    [Fact]
    public void ReadFloat_F4_And_F8()
    {
      byte[] f4be = BitConverter.GetBytes(1.5f);            // host is little-endian
      Array.Reverse(f4be);
      new BitReader(f4be).ReadFloat(32, littleEndian: false).Should().BeApproximately(1.5, 1e-6);

      byte[] f4le = BitConverter.GetBytes(-2.25f);
      new BitReader(f4le).ReadFloat(32, littleEndian: true).Should().BeApproximately(-2.25, 1e-6);

      byte[] f8le = BitConverter.GetBytes(3.141592653589793);
      new BitReader(f8le).ReadFloat(64, littleEndian: true).Should().BeApproximately(Math.PI, 1e-12);
    }

    [Fact]
    public void SeekAndSkip()
    {
      var r = new BitReader(new byte[] { 0x11, 0x22, 0x33, 0x44 });
      r.SeekBytes(2);
      r.ReadUInt(8, false).Should().Be(0x33UL);
      r.SeekBits(0);
      r.SkipBytes(3);
      r.ReadUInt(8, false).Should().Be(0x44UL);
    }

    [Fact]
    public void ReadAscii_TrimsTrailingPadding()
    {
      var r = new BitReader(new byte[] { (byte)'V', (byte)'E', (byte)'3', 0x00, 0x00 });
      r.ReadAscii(5).Should().Be("VE3");
    }

    [Fact]
    public void ReadBytes_RequiresByteAlignment()
    {
      var r = new BitReader(new byte[] { 0x12, 0x34 });
      r.ReadBitsBe(4);
      FluentActions.Invoking(() => r.ReadBytes(1)).Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void OverRead_Throws()
    {
      var r = new BitReader(new byte[] { 0x00 });
      FluentActions.Invoking(() => r.ReadBitsBe(9)).Should().Throw<InvalidOperationException>();
    }
  }
}
