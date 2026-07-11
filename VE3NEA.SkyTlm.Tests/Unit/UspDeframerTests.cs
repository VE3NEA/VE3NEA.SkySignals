using System;
using System.Linq;
using FluentAssertions;
using VE3NEA.SkyTlm.Core;
using VE3NEA.SkyTlm.Deframing;
using VE3NEA.SkyTlm.Tests.Fixtures;
using Xunit;
using Xunit.Abstractions;

namespace VE3NEA.SkyTlm.Tests.Unit
{
  /// <summary>
  /// End-to-end USP deframer validation against the gr-satellites QA vectors (Daniel Estévez): feed the
  /// exact clean bit streams (syncword + frame) through <see cref="UspDeframer"/> and confirm it recovers
  /// the published AX.25 frames. Exercises the whole chain — syncword, PLS length decode, libfec Viterbi
  /// (r=1/2 k=7), CCSDS descramble, libfec RS(255,223) dual-basis, and AX.25 crop — so a pass means every
  /// convention (polynomials, scrambler, dual basis, byte order) matches the reference.
  /// </summary>
  public class UspDeframerTests
  {
    private readonly ITestOutputHelper output;
    public UspDeframerTests(ITestOutputHelper o) => output = o;

    private static readonly SignalParams P = new(9600, Modulation.GMSK,  Framing.USP, 48000);

    private static byte[] Hex(string s) => Convert.FromHexString(s);

    /// <summary>sync(8B) + frame bytes → MSB-first bits → bipolar soft symbols (matches np.unpackbits).</summary>
    private static SoftSymbols Soft(string frameHex)
    {
      byte[] bytes = Hex(UspVectors.Sync).Concat(Hex(frameHex)).ToArray();
      var soft = new float[bytes.Length * 8];
      for (int i = 0; i < bytes.Length; i++)
        for (int b = 0; b < 8; b++)
          soft[i * 8 + b] = ((bytes[i] >> (7 - b)) & 1) == 1 ? 1f : -1f;
      return new SoftSymbols { Soft = soft, SymbolRate = 9600 };
    }

    [Theory]
    [InlineData(nameof(UspVectors.FrameLong), nameof(UspVectors.FrameLongOut))]
    [InlineData(nameof(UspVectors.FrameShort), nameof(UspVectors.FrameShortOut))]
    public void Usp_DecodesQaVector(string frameField, string expectedField)
    {
      string frameHex = (string)typeof(UspVectors).GetField(frameField)!.GetValue(null)!;
      string expHex = (string)typeof(UspVectors).GetField(expectedField)!.GetValue(null)!;

      var frames = new UspDeframer().Deframe(Soft(frameHex), P).ToList();

      frames.Should().ContainSingle("each QA vector carries exactly one USP frame");
      var f = frames[0];
      f.Framing.Should().Be(Framing.USP);
      f.CrcValid.Should().BeTrue("the RS codeword must validate");
      output.WriteLine($"len={f.Length} rsCorr={f.CorrectedBits}  {f.Hex}");
      f.Bytes.Should().Equal(Hex(expHex), "the decoded frame must match the gr-satellites reference");
    }

    [Fact]
    public void Usp_ErasureRetry_RecoversFadeCorruptedFrame()
    {
      // fade a contiguous run of the rate-1/2 coded body to near-zero soft (the QMR-KWT s-class
      // "RS overload in every variant" failure mode): the Viterbi wanders there, corrupting ~20 RS
      // bytes — beyond the plain decoder's 16-error budget. The re-encode error profile ranks the
      // faded span's bytes weakest (its margins collapse) and the erasure ladder must recover the
      // frame. (A confidently-WRONG burst — sign flips — is different: the Viterbi agrees with the
      // corruption inside the span and only the error-event boundary bytes rank weak; interval-based
      // erasures would be needed there. Noted in the plan as a possible follow-up.)
      var syms = Soft(UspVectors.FrameLong);
      int body = 64 + 64;                        // syncword + PLS, then the coded body
      var rng = new Random(3);
      for (int k = body + 1000; k < body + 1000 + 320; k++)
        syms.Soft[k] = 0.05f * (float)(rng.NextDouble() - 0.5);

      var frames = new UspDeframer().Deframe(syms, P).ToList();

      frames.Should().ContainSingle("the erasure-assisted RS retry must recover the fade-corrupted frame");
      frames[0].Bytes.Should().Equal(Hex(UspVectors.FrameLongOut));
      output.WriteLine($"rsCorr={frames[0].CorrectedBits}");
    }

    [Fact]
    public void Usp_NoSync_YieldsNoFrames()
    {
      var soft = new float[8000];
      for (int i = 0; i < soft.Length; i++) soft[i] = -1f; // constant stream, no syncword
      new UspDeframer().Deframe(new SoftSymbols { Soft = soft, SymbolRate = 9600 }, P)
        .Should().BeEmpty();
    }
  }
}
