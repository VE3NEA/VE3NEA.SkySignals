using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using VE3NEA.SkyTlm.Deframing;
using VE3NEA.SkyTlm.Tests.Regression;
using Xunit;

namespace VE3NEA.SkyTlm.Tests.Unit
{
  /// <summary>
  /// The Geoscan header describer: what the telemetry panel prints under PAYLOAD for a frame off this
  /// downlink. It reads the same two layouts as <c>GeoscanPayload</c> but must also answer for the frames
  /// that carry no picture — which, on every capture we have, is all of them.
  /// </summary>
  public class GeoscanHeaderTests
  {
    private static string Value(byte[] frame, string name) =>
      GeoscanHeader.Describe(frame).Single(f => f.Name == name).Value;

    /// <summary>One Geoscan v1 frame, laid out as the modem sends it: 8-byte header, payload, zero-padded
    /// to 72 bytes. Mirrors <c>RawJpegAssemblerTests.V1</c>.</summary>
    private static byte[] V1(int satNum, int mtype, int offset, ReadOnlySpan<byte> data)
    {
      var bytes = new byte[72];
      bytes[0] = (byte)satNum;
      bytes[1] = 0x98;
      bytes[2] = (byte)(data.Length + 6);
      bytes[3] = (byte)mtype;
      bytes[4] = (byte)(mtype >> 8);
      bytes[5] = (byte)offset;
      bytes[6] = (byte)(offset >> 8);
      bytes[7] = (byte)(offset >> 16);
      data.CopyTo(bytes.AsSpan(8));
      return bytes;
    }

    /// <summary>The v2 layout: no mtype, a fixed marker, a 32-bit file offset and a picture counter.</summary>
    private static byte[] V2(int satNum, int offset, int frameNum, ReadOnlySpan<byte> data)
    {
      var bytes = new byte[72];
      bytes[0] = (byte)satNum;
      bytes[1] = 0x98;
      bytes[2] = 60;
      BitConverter.GetBytes(0x6F6B6F31u).CopyTo(bytes, 5);
      BitConverter.GetBytes(offset).CopyTo(bytes, 9);
      BitConverter.GetBytes((ushort)frameNum).CopyTo(bytes, 13);
      data.CopyTo(bytes.AsSpan(15));
      return bytes;
    }


    // ---- the frames that carry no picture -----------------------------------------------------------

    [Fact]
    public void RealOffAirFrames_AreNamedWithoutBeingCalledImages()
    {
      // the same 23 genuine off-air frames the assembler's gate is tested against: one of every
      // (satellite, mtype) pair seen across 226 of them, and not an image among them.
      var bytes = File.ReadAllBytes(Path.Combine(TestPaths.DataDir, "Geoscan", "geoscan_telemetry.bin"));

      for (int i = 0; i < bytes.Length / 72; i++)
      {
        var frame = bytes.AsSpan(i * 72, 72).ToArray();
        var fields = GeoscanHeader.Describe(frame);

        fields.Should().NotBeEmpty($"frame {i} is a native Geoscan frame");
        fields[0].Name.Should().Be("Satellite");
        fields[0].Value.Should().StartWith("Geoscan-", $"frame {i} comes from a named bird");
        Value(frame, "Frame type").Should().StartWith("not an image", $"frame {i} carries no picture");
        fields.Should().NotContain(f => f.Name == "Image offset",
          $"frame {i} has no offset to report — those two bytes mean something else on a non-image type");
      }
    }

    [Fact]
    public void AnAx25Beacon_IsLeftToTheAddressParser()
    {
      // a real RS61S beacon: the first byte is 'B' of BEACON shifted left, not a platform ID
      var beacon = Convert.FromHexString(
        "848A82869E9C60A4A66C62A600E103F0019C076D6A0304380036003C106B200200000070000000" +
        "537500F4030E0B0100000000FF000000000DDD209A03A18F5E71270100F7000000");

      GeoscanHeader.Describe(beacon).Should().BeEmpty();
    }

    [Fact]
    public void AnUnknownPlatformId_IsNotClaimed()
    {
      GeoscanHeader.Describe(V1(0x7F, 0x0905, 0, new byte[64])).Should().BeEmpty();
    }


    // ---- the frames that do -------------------------------------------------------------------------

    [Fact]
    public void AV1ImageFrame_NamesTheCommandAndTheOffset()
    {
      byte[] data = [0xFF, 0xD8, .. new byte[62]];
      var frame = V1(0x0C, 0x0905, 0x02_4000, data);

      Value(frame, "Satellite").Should().Be("Geoscan-2 (sat_num 0x0C)");
      Value(frame, "Frame type").Should().Be("image data (v1, mtype 0x0905)");
      Value(frame, "Data length").Should().Be("64 bytes");
      Value(frame, "Image offset").Should().Be("147456");
      Value(frame, "Markers").Should().Be("SOI (start of image)");
    }

    [Fact]
    public void AV1HiResFrame_SaysSo()
    {
      Value(V1(0x0F, 0x9820, 100, new byte[64]), "Frame type")
        .Should().Be("image data, hi-res (v1, mtype 0x9820)");
    }

    [Fact]
    public void AV1StartFrame_SaysSo()
    {
      Value(V1(0x0F, 0x0901, 100, new byte[64]), "Frame type")
        .Should().Be("image start (v1, mtype 0x0901)");
    }

    [Fact]
    public void AV2ImageFrame_NamesThePictureAndAFileOffset()
    {
      byte[] data = [.. new byte[52], 0xFF, 0xD9];
      var frame = V2(0x12, 4266, 7, data);

      Value(frame, "Satellite").Should().Be("Lobachevsky (sat_num 0x12)");
      Value(frame, "Frame type").Should().Be("image data (v2)");
      Value(frame, "Picture").Should().Be("#7");
      Value(frame, "Image offset").Should().Be("4266 (in file)");
      Value(frame, "Markers").Should().Be("EOI (end of image)");
    }

    [Fact]
    public void TheV1OnlyPlatforms_AreNeverReadAsV2()
    {
      // Geoscan-Edelveis and StratoSat TK-1 speak v1 only, so bytes that happen to hold the v2 marker
      // are still an mtype and an offset — the same rule GeoscanPayload follows.
      var frame = V2(0x01, 4266, 7, new byte[54]);

      Value(frame, "Satellite").Should().Be("Geoscan-Edelveis (sat_num 0x01)");
      Value(frame, "Frame type").Should().StartWith("not an image");
    }
  }
}
