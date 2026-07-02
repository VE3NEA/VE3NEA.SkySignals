using System;
using System.Linq;
using FluentAssertions;
using VE3NEA.SkyTlm.Core;
using VE3NEA.SkyTlm.Deframing;
using VE3NEA.SkyTlm.Tests.Fixtures;
using Xunit;

namespace VE3NEA.SkyTlm.Tests.Unit
{
  /// <summary>
  /// GOMspace AX100 deframer validation against the <see cref="Ax100Tx"/> reference encoder (the inverse
  /// of the gr-satellites <c>ax100_deframer</c> chain): ASM+Golay and RS modes, both stream polarities,
  /// scrambler on/off, and error injection at every protection layer (Golay header, RS codeword). The
  /// clean-frame cases also cross-check the managed RS(255,223) encoder against the native libfec decoder.
  /// </summary>
  public class Ax100DeframerTests
  {
    private static readonly SignalParams P = new(4800, Modulation.GMSK,  Framing.AX100ASM, 48000);

    private static byte[] Payload(int len, int seed = 1)
    {
      var rnd = new Random(seed);
      var p = new byte[len];
      rnd.NextBytes(p);
      return p;
    }

    // ---- ASM+Golay mode --------------------------------------------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Asm_CleanFrame_Decodes(bool invert)
    {
      var payload = Payload(100);
      var soft = Ax100Tx.ToSoft(Ax100Tx.BuildAsmFrame(payload), invert);

      var frames = new Ax100Deframer().Deframe(soft, P).ToList();

      frames.Should().ContainSingle();
      var f = frames[0];
      f.Bytes.Should().Equal(payload);
      f.CrcValid.Should().BeTrue();
      f.Framing.Should().Be(Framing.AX100ASM);
      f.CorrectedBits.Should().Be(0, "the codeword is clean");
      f.SoftBitOffset.Should().Be(64, "the ASM follows the 8-byte preamble");
    }

    [Fact]
    public void Asm_NoScrambler_DecodesWithMatchingOption()
    {
      var payload = Payload(60);
      var soft = Ax100Tx.ToSoft(Ax100Tx.BuildAsmFrame(payload, scrambler: false));

      var f = new Ax100Deframer(new Ax100Options { Scrambler = false }).Deframe(soft, P).Single();
      f.Bytes.Should().Equal(payload);
      f.Note.Should().BeNull("no fallback fired — the option matched the bird");

      // the default CCSDS-scrambler deframer recovers it too, via the scrambler:none auto-fallback
      var fb = new Ax100Deframer().Deframe(soft, P).Single();
      fb.Bytes.Should().Equal(payload);
      fb.Note.Should().Be("scrambler:none fallback", "the frame must be tagged when the fallback fired");
    }

    [Fact]
    public void Asm_RsCorrectableErrors_DecodeAndAreCounted()
    {
      var payload = Payload(100);
      var bits = Ax100Tx.BuildAsmFrame(payload);

      // corrupt 5 whole bytes of the coded body (RS corrects up to 16 byte errors)
      int body = (8 + 4 + 3) * 8;                       // preamble + ASM + Golay header, in bits
      foreach (int byteIdx in new[] { 0, 13, 47, 88, 120 })
        for (int b = 0; b < 8; b++)
          bits[body + byteIdx * 8 + b] ^= 1;

      var f = new Ax100Deframer().Deframe(Ax100Tx.ToSoft(bits), P).Single();
      f.Bytes.Should().Equal(payload);
      f.CorrectedBits.Should().Be(5, "RS reports corrected byte count");
    }

    [Fact]
    public void Asm_GolayHeaderErrors_AreCorrected()
    {
      var payload = Payload(40);
      var bits = Ax100Tx.BuildAsmFrame(payload);

      int header = (8 + 4) * 8;                         // first bit of the Golay header
      foreach (int b in new[] { 1, 9, 20 }) bits[header + b] ^= 1;   // 3 errors = Golay's limit

      new Ax100Deframer().Deframe(Ax100Tx.ToSoft(bits), P).Single().Bytes.Should().Equal(payload);
    }

    [Fact]
    public void Asm_UncorrectableBody_YieldsNoFrame()
    {
      var payload = Payload(100);
      var bits = Ax100Tx.BuildAsmFrame(payload);

      // 20 byte errors > 16-byte RS capability. All soft values are full-confidence here, so the erasure
      // retry's "weakest" ranking degenerates to byte order — keep the corruption past the first 16 bytes
      // so no erasure lands on an error and the retry can't (and shouldn't) rescue the codeword.
      int body = (8 + 4 + 3) * 8;
      for (int byteIdx = 20; byteIdx < 40; byteIdx++)
        bits[body + byteIdx * 8] ^= 1;                  // one bit per byte = 20 corrupted symbols

      new Ax100Deframer().Deframe(Ax100Tx.ToSoft(bits), P).Should().BeEmpty();
    }

    // ---- erasure-assisted RS decoding --------------------------------------------------------------

    [Fact]
    public void Asm_ErasuresRecoverCorruptionOnWeakBytes()
    {
      var payload = Payload(150);
      var bits = Ax100Tx.BuildAsmFrame(payload);
      int body = (8 + 4 + 3) * 8;                       // first coded-body bit
      int[] corrupted = Enumerable.Range(0, 20).Select(j => 16 + j * 8).ToArray();  // 20 > 16 ⇒ plain RS fails

      foreach (int k in corrupted) bits[body + k * 8] ^= 1;

      // same corruption at full confidence: erasures land elsewhere (ties rank by byte order), no decode
      new Ax100Deframer().Deframe(Ax100Tx.ToSoft(bits), P)
        .Should().BeEmpty("20 full-confidence byte errors exceed plain RS and give erasures nothing to aim at");

      // attenuate the corrupted bytes' soft bits: now the weakest bytes coincide with the corruption
      var soft = Ax100Tx.ToSoft(bits);
      foreach (int k in corrupted)
        for (int b = 0; b < 8; b++) soft.Soft[body + k * 8 + b] *= 0.05f;

      var f = new Ax100Deframer().Deframe(soft, P).Single();
      f.Bytes.Should().Equal(payload);
      f.CorrectedBits.Should().Be(20, "all 20 corrupted bytes were fixed (erased + errors)");
      f.ErasedBytes.Should().BeGreaterThan(0).And.BeLessThanOrEqualTo(16, "the decode came from the erasure retry, capped at f = 16");
    }

    [Fact]
    public void Asm_ManyFullConfidenceErrors_NeverDecode()
    {
      // 25 random full-confidence byte errors: even f = 16 erasures leave e ≥ 9 ⇒ 2e + f > 32, so any
      // claimed RS success would be a miscorrection — the deframer must produce no frame.
      var payload = Payload(150);
      var bits = Ax100Tx.BuildAsmFrame(payload);
      int body = (8 + 4 + 3) * 8;

      var rnd = new Random(11);
      var positions = Enumerable.Range(0, payload.Length + 32).OrderBy(_ => rnd.Next()).Take(25);
      foreach (int k in positions)
        for (int b = 0; b < 8; b++) bits[body + k * 8 + b] ^= 1;    // whole-byte corruption

      new Ax100Deframer().Deframe(Ax100Tx.ToSoft(bits), P).Should().BeEmpty();
    }

    // ---- resume past decoded frames ----------------------------------------------------------------

    [Fact]
    public void Asm_TwoAdjacentFrames_BothDecode()
    {
      var p1 = Payload(80, seed: 2);
      var p2 = Payload(40, seed: 3);
      var bits = Ax100Tx.BuildAsmFrame(p1).Concat(Ax100Tx.BuildAsmFrame(p2)).ToArray();

      var frames = new Ax100Deframer().Deframe(Ax100Tx.ToSoft(bits), P).ToList();

      frames.Should().HaveCount(2, "resuming past a decoded body must not skip the next frame");
      frames[0].Bytes.Should().Equal(p1);
      frames[1].Bytes.Should().Equal(p2);
    }

    [Fact]
    public void Asm_SyncPatternInsideBody_ProducesNoDuplicateFrame()
    {
      // the outer payload is itself a complete, decodable ASM frame (sync + Golay + RS codeword). Before
      // the resume-past-body scan, the sync search re-entered the decoded body and emitted the embedded
      // frame as a duplicate; now the scan resumes at the body end and yields the outer frame only.
      var innerBits = Ax100Tx.BuildAsmFrame(Payload(20, seed: 5), scrambler: false, preambleBytes: 0);
      var innerBytes = new byte[innerBits.Length / 8];
      for (int i = 0; i < innerBits.Length; i++)
        if (innerBits[i] == 1) innerBytes[i >> 3] |= (byte)(1 << (7 - (i & 7)));

      var bits = Ax100Tx.BuildAsmFrame(innerBytes, scrambler: false);
      var frames = new Ax100Deframer(new Ax100Options { Scrambler = false })
        .Deframe(Ax100Tx.ToSoft(bits), P).ToList();

      frames.Should().ContainSingle("the embedded near-sync body must not be re-scanned");
      frames[0].Bytes.Should().Equal(innerBytes);
    }

    // ---- RS mode ----------------------------------------------------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Rs_CleanFrame_Decodes(bool invert)
    {
      var payload = Payload(50, seed: 7);
      var soft = Ax100Tx.ToSoft(Ax100Tx.BuildRsFrame(payload), invert);

      var frames = new Ax100Deframer(new Ax100Options { Mode = Ax100Mode.Rs }).Deframe(soft, P).ToList();

      frames.Should().ContainSingle();
      var f = frames[0];
      f.Bytes.Should().Equal(payload);
      f.CrcValid.Should().BeTrue();
      f.Framing.Should().Be(Framing.AX100RS);
      f.SoftBitOffset.Should().Be(64);
    }

    [Fact]
    public void Rs_FullLengthFrame_Decodes()
    {
      var payload = Payload(222, seed: 3);              // total = 1 + 222 + 32 = 255, the longest legal frame
      var soft = Ax100Tx.ToSoft(Ax100Tx.BuildRsFrame(payload));

      new Ax100Deframer(new Ax100Options { Mode = Ax100Mode.Rs }).Deframe(soft, P)
        .Single().Bytes.Should().Equal(payload);
    }

    [Fact]
    public void Rs_CorrectableErrors_Decode()
    {
      var payload = Payload(50, seed: 7);
      var bits = Ax100Tx.BuildRsFrame(payload);

      // flip scattered single bits in the codeword body (the multiplicative descrambler trebles each
      // channel bit error, so 4 flips => up to 12 byte errors, still within RS's 16)
      foreach (int i in new[] { 150, 250, 350, 450 }) bits[i] ^= 1;

      var f = new Ax100Deframer(new Ax100Options { Mode = Ax100Mode.Rs }).Deframe(Ax100Tx.ToSoft(bits), P).Single();
      f.Bytes.Should().Equal(payload);
      f.CorrectedBits.Should().BeGreaterThan(0);
    }

    [Fact]
    public void NoSync_YieldsNoFrames()
    {
      var soft = new SoftSymbols { Soft = Enumerable.Repeat(-1f, 4000).ToArray(), SymbolRate = 4800 };
      new Ax100Deframer().Deframe(soft, P).Should().BeEmpty();
      new Ax100Deframer(new Ax100Options { Mode = Ax100Mode.Rs }).Deframe(soft, P).Should().BeEmpty();
    }

    // ---- building blocks ---------------------------------------------------------------------------

    [Fact]
    public void CcsdsByteSequence_MatchesStandardRandomizer()
    {
      var zeros = new byte[8];
      CcsdsScrambler.XorSequenceInPlace(zeros);
      // the CCSDS PN sequence bytes are pinned by CCSDS 131.0-B
      zeros.Should().Equal(new byte[] { 0xFF, 0x48, 0x0E, 0xC0, 0x9A, 0x0D, 0x70, 0xBC });
    }

    [Fact]
    public void G3ruhScramble_IsInverseOfSoftDescramble()
    {
      var rnd = new Random(42);
      var bits = Enumerable.Range(0, 500).Select(_ => rnd.Next(2)).ToArray();

      var scrambled = Ax100Tx.G3ruhScramble(bits);
      float[] soft = scrambled.Select(b => b == 1 ? 1f : -1f).ToArray();
      var back = SoftBits.G3ruhDescramble(soft).Select(SoftBits.Hard).ToArray();

      back.Should().Equal(bits);
    }
  }
}
