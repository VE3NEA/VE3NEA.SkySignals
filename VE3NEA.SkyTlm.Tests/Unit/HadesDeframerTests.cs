using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using FluentAssertions;
using VE3NEA.SkyTlm.Core;
using VE3NEA.SkyTlm.Deframing;
using Xunit;
using Xunit.Abstractions;

namespace VE3NEA.SkyTlm.Tests.Unit
{
  /// <summary>
  /// HADES (GENESIS family) deframer validation. Two independent pins come straight from the AMSAT-EA
  /// "HADES-SA SpinnyONE — Transmissions description" spec: the CRC-16/CCITT-FALSE check value
  /// (<c>"EASAT-2" → 0x7D58</c>) and the multiplicative-scrambler golden vector
  /// (<c>"GENESIS-Genesis\0" → 0xC743…C8</c>). The rest drive the full chain
  /// (<see cref="SyncToPacket"/> → crop-by-type → CRC → descramble) with synthetic bursts built by the
  /// inverse scrambler, exercising the HADES-SA length table, polarity inversion, and CRC rejection.
  /// </summary>
  public class HadesDeframerTests
  {
    private readonly ITestOutputHelper output;
    public HadesDeframerTests(ITestOutputHelper o) => output = o;

    private static readonly SignalParams P = new(800, Modulation.FSK,  Framing.HADES, 38400);

    // ---- Spec golden vectors (independent of the synthetic-burst path) --------------------------------

    [Theory]
    [InlineData("123456789", 0x29B1)] // canonical CRC-16/CCITT-FALSE check value
    [InlineData("EASAT-2", 0x7D58)]    // HADES-SA spec golden vector
    public void Crc_MatchesSpecVectors(string text, int expected)
        => Crc16CcittFalse.Compute(Encoding.ASCII.GetBytes(text)).Should().Be((ushort)expected);

    [Fact]
    public void Descrambler_ReproducesSpecGoldenVector()
    {
      // spec example: scramble("GENESIS-Genesis\0") = 0xC7434C274B1713D76B05AAD1899747C8 (16 bytes; the
      // string is NUL-terminated). The spec example scrambles from byte 0, while the packet rule exempts
      // the type byte — so prepend a throwaway byte and descramble from index 1 (what Descramble does).
      var buf = new byte[] { 0x00 }
          .Concat(Convert.FromHexString("C7434C274B1713D76B05AAD1899747C8")).ToArray();

      HadesDeframer.Descramble(buf);

      buf[1..].Should().Equal(Encoding.ASCII.GetBytes("GENESIS-Genesis\0"),
          "the bug-for-bug GENESIS descrambler must invert the AMSAT-EA scrambler exactly");
    }

    // ---- Full chain over the HADES-SA length table ----------------------------------------------------

    [Theory]
    [InlineData(1, 31)]
    [InlineData(2, 17)]
    [InlineData(3, 41)]   // HADES-SA-specific (HADES-R is 29)
    [InlineData(4, 35)]
    [InlineData(5, 27)]
    [InlineData(8, 31)]
    [InlineData(9, 123)]
    [InlineData(12, 64)]
    [InlineData(14, 38)]
    [InlineData(15, 73)]  // HADES-SA-specific (HADES-R is 41)
    public void Deframe_RecoversTelemetryPacket(int type, int totalLen)
    {
      // plaintext frame = type/address byte + data (CRC excluded), length = totalLen - 2.
      var plain = new byte[totalLen - 2];
      plain[0] = (byte)((type << 4) | 3);                  // type high nibble, address 3 (HADES-SA)
      for (int i = 1; i < plain.Length; i++) plain[i] = (byte)(i * 7 + 1);

      var frames = new HadesDeframer().Deframe(MakeBurst(plain), P).ToList();

      frames.Should().ContainSingle("the burst carries exactly one HADES packet");
      var f = frames[0];
      f.Framing.Should().Be(Framing.HADES);
      f.CrcValid.Should().BeTrue();
      f.Length.Should().Be(totalLen - 2, "the frame is the cropped packet minus its 2-byte CRC");
      f.Bytes.Should().Equal(plain, "the descrambled frame must match the plaintext (type byte unscrambled)");
      output.WriteLine($"type={type} len={f.Length} {f.Hex}");
    }

    [Fact]
    public void Deframe_DecodesRealType2Frame()
    {
      // A real HADES-SA Type-2 (temperature) payload captured off-air on 2026-06-07 and cross-checked
      // against the UZ7HO modem's KISS output AND the AMSAT-EA decoder: sclock 5988318 (little-endian
      // DE5F5B00), tpb=+7.5°C (0x5F), tpc=+9.0 (0x62), tpd=+8.0 (0x60), teps=+0.0 (0x50), ttx=+5.0 (0x5A),
      // ttx2=+7.0 (0x5E), tcpu=+8.5 (0x61), tpa/trx error (0xFF). The on-air CRC over the scrambled packet
      // was 0xCCD1 — this pins our scramble + CRC convention against ground truth, plus the size-byte skip.
      var payload = Convert.FromHexString("23DE5F5B00FF5F6260FF505A5EFF61");
      Crc16CcittFalse.Compute(Scramble(payload)).Should().Be(0xCCD1, "the on-air CRC observed off-air");

      var f = new HadesDeframer().Deframe(MakeBurst(payload), P).Should().ContainSingle().Subject;
      f.CrcValid.Should().BeTrue();
      f.Bytes.Should().Equal(payload);
    }

    // ---- SSDV(10) / CODEC2(11) / PN9(13) special types -----------------------------------------------
    // golden vectors are real frames the reference UZ7HO `hadessa` modem emitted over KISS (hades_kiss.log,
    // 2026-06-07 HADES-SA pass). Unlike telemetry these are NOT GENESIS-descrambled and carry no HADES CRC —
    // the deframer just crops to the per-type length, so the on-air bytes == the emitted bytes == these.

    private const string SsdvKiss =   // type 10, packet id 0x0010 (KISS #34), 251 B
      "A3000010140F1815007EFAD7FCCCDF183E17F82FFF33351FF1981F3BFF1879FEC83C59AC7FC261FF205FF8493FF08FAF40" +
      "FD93FE1BEB9F0DFE0DFF61F8D3FE460FF84C3E2878D3FF33357B0784FC1FA1E8F5E8140051451400514514005145140051" +
      "4514005145140051451400514514005145140051451401FC4FFFC1E05AC7FC98FE87FF6741FFBC16BF85FD5ABFB40FF83C" +
      "0B58FF8BC9FB0FE87FF547FE287FEF05AFE27F56AF9DE1B3E88E7F56AE7EBA0AE7E83E74E7E8A28A002B9FA28A00F60A2B" +
      "9FA2803A0A2B9FA2803A0FED8A2B9FA2803A0FD482609121BB46854946019539E75F02E353E7EB936E1227C995D4536C35" +
      "ED64E2B6B6A8";
    private const string Codec2Kiss = // type 11, frame number 0x0B (KISS #10), 37 B
      "B30BD8D815086960E1331F03E2575228D2CDDECE367EBB90109D3C48ABC846F3A51A1E9B87";
    private const string Pn9Kiss =    // type 13 (KISS #1), 249 B
      "D3B948267479E0FF8DB859A7A1CC24575E218694B8A55FD8AFF6D8C907EF5C9291D5B1C4A8D9F3C5B94826747DE0FF87DC" +
      "064C384F9D9D26292332CE195DE995B0D5390C42011191D5B1C4A8D9F3C530BA48411E1C92D9F54D32D4775FBB64FDA49B" +
      "F2D4289D9FB0D5390C4201117F3448744BBD8E23F3036C1EABCF4D71A2FE96298C066564FDA49BF2D4289D3A7ABB8343FB" +
      "61384BDC761633DEA35B4DDB582EF8F34D71A2FE96298C06656D546563BD122861CFC8680037D21325CE0774F528155F5A" +
      "0DDB582EF8F34FD3F79BBA2E9617321CDA55D056EEC5DC2CDBD0E6122BAF24CE0774F528155FDF5549167C2FDE439F49F0" +
      "00F45171";

    [Theory]
    [InlineData(SsdvKiss, 10, 251)]
    [InlineData(Codec2Kiss, 11, 37)]
    [InlineData(Pn9Kiss, 13, 249)]
    public void Deframe_RecoversSpecialType(string goldenHex, int type, int len)
    {
      var golden = Convert.FromHexString(goldenHex);
      golden.Length.Should().Be(len);
      (golden[0] >> 4).Should().Be(type);
      (golden[0] & 0x0F).Should().Be(3, "HADES-SA source address is 3");

      var f = new HadesDeframer().Deframe(MakeSpecialBurst(golden), P).Should().ContainSingle().Subject;
      f.Framing.Should().Be(Framing.HADES);
      f.CrcValid.Should().BeNull("SSDV/CODEC2/PN9 carry no HADES CRC-16 — null means not applicable, not an error");
      f.Bytes.Should().Equal(golden, "the special types are crop-only (no descramble) — on-air bytes pass through verbatim");
    }

    [Fact]
    public void Deframe_SpecialType_IsNotDescrambled()
    {
      // descrambling these would corrupt the output (this is exactly the bug the KISS comparison caught):
      // assert the deframer returns the bytes untouched, NOT HadesDeframer.Descramble'd.
      var golden = Convert.FromHexString(Codec2Kiss);
      var wronglyDescrambled = (byte[])golden.Clone();
      HadesDeframer.Descramble(wronglyDescrambled);

      var f = new HadesDeframer().Deframe(MakeSpecialBurst(golden), P).Should().ContainSingle().Subject;
      f.Bytes.Should().Equal(golden);
      f.Bytes.Should().NotEqual(wronglyDescrambled, "the special types must not run through the GENESIS descrambler");
    }

    [Fact]
    public void Deframe_Ssdv_SplitAcrossBurst_DropsPartialPacket()
    {
      // A 251-byte SSDV packet needs the whole capture; a short burst (one fragment) must not emit a partial.
      var golden = Convert.FromHexString(SsdvKiss);
      var partial = golden[..40];                            // first ~40 bytes only
      new HadesDeframer().Deframe(MakeSpecialBurst(partial, sizeByte: 251), P)
          .Should().BeEmpty("an SSDV packet shorter than its 251-byte length cannot be cropped");
    }

    [Fact]
    public void Deframe_SpecialType_RejectsWrongAddress()
    {
      var golden = Convert.FromHexString(Codec2Kiss);
      var wrongAddr = (byte[])golden.Clone();
      wrongAddr[0] = (byte)((11 << 4) | 5);                  // type 11 but address 5, not 3
      new HadesDeframer().Deframe(MakeSpecialBurst(wrongAddr), P)
          .Should().BeEmpty("only source address 3 (HADES-SA) is accepted");
    }

    /// <summary>Build a soft-symbol burst for one special (un-scrambled) packet: 0xBF35 sync, SIZE byte, then
    /// the verbatim packet bytes, MSB-first, ±1 soft values (mirrors <see cref="MakeBurst"/> without scrambling
    /// or CRC). <paramref name="sizeByte"/> overrides the on-air SIZE field (default = packet length).</summary>
    private static SoftSymbols MakeSpecialBurst(byte[] packet, int? sizeByte = null)
    {
      var onair = new byte[1 + packet.Length];
      onair[0] = (byte)(sizeByte ?? packet.Length);
      Array.Copy(packet, 0, onair, 1, packet.Length);

      var bits = new List<int>(16 + onair.Length * 8);
      for (int i = 15; i >= 0; i--) bits.Add((0xBF35 >> i) & 1);
      foreach (byte by in onair) for (int b = 7; b >= 0; b--) bits.Add((by >> b) & 1);

      var soft = new float[bits.Count];
      for (int i = 0; i < bits.Count; i++) soft[i] = bits[i] == 1 ? 1f : -1f;
      return new SoftSymbols { Soft = soft, SymbolRate = 800 };
    }

    [Fact]
    public void Deframe_AbsorbsInvertedPolarity()
    {
      var plain = MakePlain(2, 17);
      new HadesDeframer().Deframe(MakeBurst(plain, invert: true), P)
          .Should().ContainSingle().Which.Bytes.Should().Equal(plain,
              "the syncword search tries both polarities, so a sign-flipped stream still decodes");
    }

    [Fact]
    public void Deframe_RejectsCorruptedCrc()
    {
      var plain = MakePlain(2, 17);
      // flip one bit in each of three on-air data bytes (b[0]=size, b[1]=type/addr, b[2…]=data) so the CRC
      // (checked before descrambling) fails beyond what the ≤2-flip Chase correction can repair.
      new HadesDeframer().Deframe(MakeBurst(plain, corruptOnAir: b => { b[2] ^= 0x10; b[4] ^= 0x10; b[6] ^= 0x10; }), P)
          .Should().BeEmpty("a CRC mismatch beyond Chase's reach must drop the packet");
    }

    // ---- Chase correction (CRC-assisted weak-bit flipping, telemetry types only) ----------------------

    [Theory]
    [InlineData(new[] { 20 })]
    [InlineData(new[] { 20, 45 })]
    public void Deframe_Chase_CorrectsWeakBitErrors(int[] badBits)
    {
      var plain = MakePlain(2, 17);
      var syms = MakeBurst(plain);

      // invert + attenuate the on-air soft bits: a flipped low-|soft| bit is exactly what Chase targets.
      // packet bit b lives at soft[16 (sync) + 8 (size byte) + b].
      foreach (int b in badBits) syms.Soft[16 + 8 + b] *= -0.05f;

      var f = new HadesDeframer().Deframe(syms, P).Should().ContainSingle().Subject;
      f.Bytes.Should().Equal(plain, "Chase must restore the corrupted packet before descrambling");
      f.CrcValid.Should().BeTrue();
      f.CorrectedBits.Should().Be(badBits.Length);
    }

    [Fact]
    public void Deframe_Chase_RejectsThreeWeakBitErrors()
    {
      var plain = MakePlain(2, 17);
      var syms = MakeBurst(plain);
      foreach (int b in new[] { 20, 45, 70 }) syms.Soft[16 + 8 + b] *= -0.05f;

      new HadesDeframer().Deframe(syms, P)
          .Should().BeEmpty("3 bit errors exceed the ≤2-flip Chase depth and must not decode");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]   // not used on HADES-SA
    [InlineData(7)]   // not used on HADES-SA
    public void TryDeframe_DropsUnknownOrUnusedType(int type)
    {
      var raw = new byte[256];
      raw[0] = 17;                          // size byte (skipped by the deframer)
      raw[1] = (byte)((type << 4) | 3);     // type/address
      new HadesDeframer().TryDeframe(raw).Should().BeNull("type is not in any HADES-SA length table");
    }

    [Fact]
    public void Deframe_NoSync_YieldsNoFrames()
    {
      var soft = new float[8000];
      for (int i = 0; i < soft.Length; i++) soft[i] = -1f; // constant stream, no 0xBF35
      new HadesDeframer().Deframe(new SoftSymbols { Soft = soft, SymbolRate = 800 }, P).Should().BeEmpty();
    }

    [Fact]
    public void UnknownSatellite_Throws()
        => FluentActions.Invoking(() => new HadesDeframer(new HadesOptions { Satellite = "HADES-X" }))
            .Should().Throw<ArgumentException>();

    // ---- SyncToPacket threshold behaviour -------------------------------------------------------------

    [Theory]
    [InlineData(0, false)] // 1 sync-bit error, threshold 0 → no packet
    [InlineData(1, true)]  // 1 sync-bit error, threshold 1 → still framed
    public void SyncToPacket_HonorsThreshold(int threshold, bool expectPacket)
    {
      // 0xBF35 with its LSB flipped (1 bit error), then 8 data bytes.
      ushort badSync = 0xBF35 ^ 0x0001;
      var bits = new List<int>();
      for (int i = 15; i >= 0; i--) bits.Add((badSync >> i) & 1);
      for (int b = 0; b < 8 * 8; b++) bits.Add(1);
      var soft = bits.Select(b => b == 1 ? 1f : -1f).ToArray();

      var packets = SyncToPacket.Extract(soft, 0xBF35, 16, 8, threshold).ToList();
      (packets.Count > 0).Should().Be(expectPacket);
    }

    // ---- helpers --------------------------------------------------------------------------------------

    private static byte[] MakePlain(int type, int totalLen)
    {
      var plain = new byte[totalLen - 2];
      plain[0] = (byte)((type << 4) | 3);
      for (int i = 1; i < plain.Length; i++) plain[i] = (byte)(i * 7 + 1);
      return plain;
    }

    /// <summary>
    /// Build a soft-symbol burst for one HADES packet: scramble the payload (type byte exempt), append the
    /// CRC-16/CCITT-FALSE over the on-air (scrambled) bytes big-endian, prepend the 0xBF35 sync, and pad the
    /// capture to the 135-byte <c>packlen</c>. MSB-first throughout, ±1 soft values. Optional polarity flip
    /// and on-air corruption mirror the receiver-side conditions the deframer must handle.
    /// </summary>
    private static SoftSymbols MakeBurst(byte[] plain, bool invert = false, Action<byte[]>? corruptOnAir = null)
    {
      byte[] scrambled = Scramble(plain);                     // first byte exempt, matches Descramble
      ushort crc = Crc16CcittFalse.Compute(scrambled);
      byte[] packet = scrambled.Append((byte)(crc >> 8)).Append((byte)(crc & 0xFF)).ToArray(); // type/addr…CRC
      // on air the packet is preceded by the unscrambled SIZE byte (= packet length); the deframer skips it.
      byte[] onair = new byte[1 + packet.Length];
      onair[0] = (byte)packet.Length;
      Array.Copy(packet, 0, onair, 1, packet.Length);
      corruptOnAir?.Invoke(onair);

      var capture = new byte[135];
      Array.Copy(onair, capture, onair.Length);              // remainder is 0x00 filler

      var bits = new List<int>(16 + capture.Length * 8);
      for (int i = 15; i >= 0; i--) bits.Add((0xBF35 >> i) & 1);
      foreach (byte by in capture) for (int b = 7; b >= 0; b--) bits.Add((by >> b) & 1);

      var soft = new float[bits.Count];
      for (int i = 0; i < bits.Count; i++)
      {
        int bit = invert ? bits[i] ^ 1 : bits[i];
        soft[i] = bit == 1 ? 1f : -1f;
      }
      return new SoftSymbols { Soft = soft, SymbolRate = 800 };
    }

    /// <summary>Inverse of <see cref="HadesDeframer.Descramble"/>: the multiplicative scrambler (output fed
    /// back into the register), first byte exempt, per-byte LSB passed through without advancing the state.</summary>
    private static byte[] Scramble(byte[] plain)
    {
      var outp = (byte[])plain.Clone();
      int state = 1 << 16;
      for (int j = 1; j < plain.Length; j++)
      {
        int o = 0;
        for (int k = 0; k < 7; k++)
        {
          int d = (plain[j] >> (7 - k)) & 1;
          int s = d ^ (((state >> 16) ^ (state >> 11)) & 1);
          o = (o << 1) | s;
          state = ((state << 1) | s) & 0x1ffff;             // feed the OUTPUT (scrambled) bit
        }
        o = (o << 1) | (plain[j] & 1);
        outp[j] = (byte)o;
      }
      return outp;
    }
  }
}
