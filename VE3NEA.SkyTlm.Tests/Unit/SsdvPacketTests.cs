using System;
using System.Linq;
using FluentAssertions;
using VE3NEA.SkyTlm.Deframing;
using VE3NEA.SkyTlm.Imaging.Ssdv;
using VE3NEA.SkyTlm.Tests.Fixtures;
using Xunit;

namespace VE3NEA.SkyTlm.Tests.Unit
{
  /// <summary>
  /// SSDV packet layer: the variant table's derived geometry, a round trip against the
  /// <see cref="SsdvTx"/> reference encoder for every variant, the integrity gates (big-endian CRC-32,
  /// RS(255,223) repair and its limit), and the golden off-air HADES-SA packet that
  /// <c>ssdv-research.md</c> §6.1 characterises byte for byte.
  /// </summary>
  public class SsdvPacketTests
  {
    // the type-10 HADES-SA SSDV frame from HadesDeframerTests (UZ7HO hadessa modem, 2026-06-07 pass) —
    // 251 on-air bytes, i.e. the standard packet minus the five constant bytes AMSAT-EA strips.
    private const string HadesOnAir =
      "A3000010140F1815007EFAD7FCCCDF183E17F82FFF33351FF1981F3BFF1879FEC83C59AC7FC261FF205FF8493FF08FAF40" +
      "FD93FE1BEB9F0DFE0DFF61F8D3FE460FF84C3E2878D3FF33357B0784FC1FA1E8F5E8140051451400514514005145140051" +
      "4514005145140051451400514514005145140051451401FC4FFFC1E05AC7FC98FE87FF6741FFBC16BF85FD5ABFB40FF83C" +
      "0B58FF8BC9FB0FE87FF547FE287FEF05AFE27F56AF9DE1B3E88E7F56AE7EBA0AE7E83E74E7E8A28A002B9FA28A00F60A2B" +
      "9FA2803A0A2B9FA2803A0FED8A2B9FA2803A0FD482609121BB46854946019539E75F02E353E7EB936E1227C995D4536C35" +
      "ED64E2B6B6A8";

    private static byte[] HadesCanonical()
    {
      var pkt = new byte[256];
      Convert.FromHexString("5566BF35FB").CopyTo(pkt, 0);
      Convert.FromHexString(HadesOnAir).CopyTo(pkt, 5);
      return pkt;
    }

    private static SsdvVariant Variant(string name) => name switch
    {
      "Standard256" => SsdvVariant.Standard256,
      "NoFec256" => SsdvVariant.NoFec256,
      "Jy1Sat200" => SsdvVariant.Jy1Sat200,
      "Dslwp218" => SsdvVariant.Dslwp218,
      _ => throw new ArgumentException(name)
    };


    // ---- primitives --------------------------------------------------------------------------------

    [Fact]
    public void Crc32_MatchesCanonicalCheckValue() =>
      Crc32.Compute("123456789"u8).Should().Be(0xCBF43926);

    [Theory]
    [InlineData("Standard256", 15, 205)]
    [InlineData("NoFec256", 15, 237)]
    [InlineData("Jy1Sat200", 11, 189)]
    [InlineData("Dslwp218", 9, 209)]
    public void Variant_HeaderAndPayloadLengths_AreTheDocumentedOnes(string name, int headerLen, int payloadLen)
    {
      var v = Variant(name);

      v.HeaderLen.Should().Be(headerLen);
      v.PayloadLen.Should().Be(payloadLen);
      (v.HeaderLen + v.PayloadLen + (v.HasCrc ? 4 : 0) + (v.HasRs ? 32 : 0)).Should().Be(v.PacketLen);
    }

    [Fact]
    public void HadesVariant_IsTheStandardPacket() =>
      SsdvVariant.HadesSa251.Should().BeSameAs(SsdvVariant.Standard256,
        "HADES-SA is standard SSDV once the five constant bytes are put back — the trimming is the source's job");

    [Theory]
    [InlineData("VE3NEA")]
    [InlineData("MI0VIM")]
    [InlineData("K")]
    [InlineData("1A2B3C")]
    public void Callsign_RoundTrips(string callsign) =>
      // pins the digit alphabet and encoder/decoder agreement; the test below pins the digit order.
      SsdvPacket.DecodeCallsign(SsdvTx.EncodeCallsign(callsign)).Should().Be(callsign);

    [Fact]
    public void ReferenceCallsignEncoding()
    {
      // Digit order has no off-air anchor — neither live variant puts a real callsign in the field — so
      // it is pinned against the reference tool instead: `ssdv -e -c VE3NEA` writes these four bytes.
      // Note fsphil's encoder walks the string backwards, making the *last* character the most
      // significant digit, which is the opposite of what the field layout suggests.
      SsdvTx.EncodeCallsign("VE3NEA").Should().Be(0x584C99F3);
      SsdvPacket.DecodeCallsign(0x584C99F3).Should().Be("VE3NEA");
    }

    [Fact]
    public void Callsign_OutOfRangeCode_DecodesToNothing() =>
      // 40^6 and above cannot be a 6-character callsign; fsphil's decoder returns an empty string.
      SsdvPacket.DecodeCallsign(0xF4240000).Should().BeEmpty();


    // ---- round trip --------------------------------------------------------------------------------

    [Theory]
    [InlineData("Standard256")]
    [InlineData("NoFec256")]
    [InlineData("Jy1Sat200")]
    [InlineData("Dslwp218")]
    public void CleanPacket_RoundTrips(string name)
    {
      var v = Variant(name);
      var payload = SsdvTx.Payload(v, seed: 7);
      var raw = SsdvTx.Build(v, payload, imageId: 42, packetId: 300, callsign: "VE3NEA",
                             widthMcu: 20, heightMcu: 15, quality: 6, eoi: true,
                             subsampling: 2, mcuOffset: 21, mcuIndex: 126);

      SsdvPacket.TryParse(raw, v, out var p).Should().BeTrue();

      p!.ImageId.Should().Be(42);
      p.PacketId.Should().Be(300);
      p.Width.Should().Be(320);
      p.Height.Should().Be(240);
      p.Quality.Should().Be(6);
      p.IsEoi.Should().BeTrue();
      p.Subsampling.Should().Be(2);
      p.McuOffset.Should().Be(21);
      p.McuIndex.Should().Be(126);
      p.Payload.Should().Equal(payload);
      p.CorrectedBytes.Should().Be(0);
      p.Callsign.Should().Be(v.HasCallsign ? "VE3NEA" : "");
    }

    [Theory]
    [InlineData("Standard256")]
    [InlineData("Jy1Sat200")]
    public void NoMcuInPacket_RoundTripsAsMinusOne(string name)
    {
      // 0xFF / 0xFFFF mean "this payload continues an MCU begun earlier", not offset 255 / MCU 65535.
      var v = Variant(name);
      var raw = SsdvTx.Build(v, SsdvTx.Payload(v, seed: 8), mcuOffset: -1, mcuIndex: -1);

      SsdvPacket.TryParse(raw, v, out var p).Should().BeTrue();
      p!.McuOffset.Should().Be(-1);
      p.McuIndex.Should().Be(-1);
    }


    // ---- integrity gates ---------------------------------------------------------------------------

    [Fact]
    public void CrcIsStoredBigEndian()
    {
      // the whole point of the note in research §1.1: a little-endian store would parse here if the
      // parser were sloppy about byte order, so assert the stored bytes directly.
      var v = SsdvVariant.NoFec256;
      var raw = SsdvTx.Build(v, SsdvTx.Payload(v, seed: 9));
      uint crc = Crc32.Compute(raw.AsSpan(1, v.CrcOffset - 1));

      raw.Skip(v.CrcOffset).Take(4).Should().Equal(
        (byte)(crc >> 24), (byte)(crc >> 16), (byte)(crc >> 8), (byte)crc);
      SsdvPacket.TryParse(raw, v, out _).Should().BeTrue();
    }

    [Fact]
    public void CorruptedPacket_WithoutRs_IsRejected()
    {
      var v = SsdvVariant.NoFec256;
      var raw = SsdvTx.Build(v, SsdvTx.Payload(v, seed: 10));
      raw[100] ^= 0xFF;

      SsdvPacket.TryParse(raw, v, out _).Should().BeFalse("the CRC is the only gate when there is no FEC");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    [InlineData(16)]
    public void CorruptedPacket_IsRepairedByRs(int errors)
    {
      var v = SsdvVariant.Standard256;
      var payload = SsdvTx.Payload(v, seed: 11);
      var raw = SsdvTx.Build(v, payload, imageId: 7, packetId: 3);
      // spread over header, payload and parity
      for (int i = 0; i < errors; i++) raw[2 + i * 13] ^= 0xFF;

      SsdvPacket.TryParse(raw, v, out var p).Should().BeTrue("RS(255,223) corrects up to 16 bytes");
      p!.CorrectedBytes.Should().Be(errors);
      p.ImageId.Should().Be(7);
      p.PacketId.Should().Be(3);
      p.Payload.Should().Equal(payload);
    }

    [Fact]
    public void BeyondRsCapacity_IsRejected()
    {
      var v = SsdvVariant.Standard256;
      var raw = SsdvTx.Build(v, SsdvTx.Payload(v, seed: 12));
      for (int i = 0; i < 24; i++) raw[2 + i * 9] ^= 0xFF;

      SsdvPacket.TryParse(raw, v, out _).Should().BeFalse(
        "17+ byte errors are past RS capacity, and the CRC re-check rejects whatever RS claims");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void CorruptedSyncOrTypeByte_IsRepaired(int at)
    {
      // Both bytes are constants for a known variant, so the parser overwrites them and lets RS and the
      // CRC decide — as ssdv_dec_is_packet does. Gating on them instead would throw away packets whose
      // only damage is in the two bytes we already know the value of.
      var v = SsdvVariant.Standard256;
      var raw = SsdvTx.Build(v, SsdvTx.Payload(v, seed: 13), imageId: 7, packetId: 3);
      raw[at] ^= 0xFF;

      SsdvPacket.TryParse(raw, v, out var p).Should().BeTrue();
      p!.ImageId.Should().Be(7);
      p.PacketId.Should().Be(3);
      p.CorrectedBytes.Should().Be(0,
        "overwriting a known constant repairs it before RS sees it, so none of the RS capacity is spent on it");
    }

    [Fact]
    public void WrongVariant_IsRejected()
    {
      // 0x66 vs 0x67 is the only thing distinguishing a packet with RS from one without, and getting it
      // wrong silently shifts the payload by 32 bytes. NoFec256 has no RS to fall back on, so the CRC —
      // computed over a span 32 bytes longer than the sender used — rejects it.
      var v = SsdvVariant.Standard256;
      var raw = SsdvTx.Build(v, SsdvTx.Payload(v, seed: 13));

      SsdvPacket.TryParse(raw, SsdvVariant.NoFec256, out _).Should().BeFalse();
    }

    [Fact]
    public void NoiseWithTheRightLength_IsRejected()
    {
      // the flip side of repairing the sync/type bytes: with them forced, the CRC is the only thing
      // standing between random bytes and the image map, and it has to hold.
      var v = SsdvVariant.Standard256;
      var noise = new byte[v.PacketLen];
      new Random(99).NextBytes(noise);

      SsdvPacket.TryParse(noise, v, out _).Should().BeFalse();
    }

    [Theory]
    [InlineData(0, 15, 0, 0, "zero width")]
    [InlineData(20, 0, 0, 0, "zero height")]
    [InlineData(20, 15, 300, 0, "MCU index at the MCU count")]
    [InlineData(20, 15, 0, 205, "MCU offset past the payload")]
    public void ImplausibleHeader_IsRejected(int widthMcu, int heightMcu, int mcuIndex, int mcuOffset, string why)
    {
      // These pass the CRC — they are what the sender actually sent — but the transcoder trusts them
      // absolutely, so ssdv_dec_is_packet's sanity checks are reproduced here.
      var v = SsdvVariant.Standard256;
      var raw = SsdvTx.Build(v, SsdvTx.Payload(v, seed: 15), widthMcu: widthMcu, heightMcu: heightMcu,
                             subsampling: 0, mcuIndex: mcuIndex, mcuOffset: mcuOffset);

      SsdvPacket.TryParse(raw, v, out _).Should().BeFalse(why);
    }

    [Fact]
    public void WrongLength_IsRejected()
    {
      var v = SsdvVariant.Standard256;
      var raw = SsdvTx.Build(v, SsdvTx.Payload(v, seed: 14));

      SsdvPacket.TryParse(raw.AsSpan(0, 255), v, out _).Should().BeFalse();
      SsdvPacket.TryParse(raw.Concat(new byte[] { 0 }).ToArray(), v, out _).Should().BeFalse();
    }


    // ---- the golden off-air HADES-SA packet ---------------------------------------------------------

    [Fact]
    public void HadesGoldenFrame_ParsesAfterTheFiveByteReconstruction()
    {
      var canonical = HadesCanonical();

      SsdvPacket.TryParse(canonical, SsdvVariant.HadesSa251, out var p).Should().BeTrue();

      p!.ImageId.Should().Be(0);
      p.PacketId.Should().Be(16);
      p.Width.Should().Be(320);
      p.Height.Should().Be(240);
      p.Quality.Should().Be(7);
      p.IsEoi.Should().BeFalse();
      p.McuOffset.Should().Be(21);
      p.McuIndex.Should().Be(126);
      p.Payload.Should().HaveCount(205);
      p.CorrectedBytes.Should().Be(0, "the golden frame is clean — the CRC passes with no RS repair");
      p.CallsignCode.Should().Be(0xBF35FBA3,
        "AMSAT-EA overloaded the callsign field with the GENESIS sync, size and type/address bytes");
    }

    [Fact]
    public void HadesGoldenFrame_HasTheDocumentedCrcAndRsSyndromes()
    {
      var canonical = HadesCanonical();

      Crc32.Compute(canonical.AsSpan(1, 219)).Should().Be(0xD4826091, "research §6.1's stated checksum");
      Convert.ToHexString(canonical, 220, 4).Should().Be("D4826091", "and it is stored big-endian");
      RsCodeword.Decode(canonical[1..], pad: 0, dualBasis: false).Should().Be(0,
        "all 32 RS syndromes are zero — the reconstructed packet is a valid codeword");
    }

    [Fact]
    public void HadesOnAirBytes_WithoutReconstruction_DoNotParse()
    {
      // the five prepended bytes are not cosmetic: without them neither the CRC span nor the RS codeword
      // lines up, and no contiguous range of the transmitted bytes checksums to anything.
      var onAir = Convert.FromHexString(HadesOnAir);

      onAir.Should().HaveCount(251);
      SsdvPacket.TryParse(onAir, SsdvVariant.HadesSa251, out _).Should().BeFalse();
    }
  }
}
