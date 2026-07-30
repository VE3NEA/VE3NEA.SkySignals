using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using VE3NEA.SkyTlm.Core;
using VE3NEA.SkyTlm.Deframing;
using VE3NEA.SkyTlm.Tests.Fixtures;
using VE3NEA.SkyTlm.Tests.Regression;
using Xunit;

namespace VE3NEA.SkyTlm.Tests.Unit
{
  /// <summary>
  /// AO-40 FEC deframer validation against the gr-satellites QA vector (an off-air AO-73 packet): the whole
  /// chain — distributed syncframe, 80 × 65 matrix deinterleave, Viterbi, CCSDS descramble and interleaved
  /// Reed–Solomon — must reproduce the reference 256-byte frame bit-exactly. Everything else here exercises
  /// the same golden symbols under polarity inversion, corruption and stream handling, so no reference
  /// encoder is needed (there is no upstream AO-40 encoder to port).
  /// </summary>
  public class Ao40FecDeframerTests
  {
    private static readonly SignalParams P = new(1200, Modulation.BPSK, Framing.AO40FEC, 48000);

    private static float[] Symbols()
    {
      string path = Path.Combine(TestPaths.DataDir, Ao40Vectors.SymbolsFile);
      File.Exists(path).Should().BeTrue($"the QA vector {Ao40Vectors.SymbolsFile} must be committed");

      var bytes = File.ReadAllBytes(path);
      var soft = new float[bytes.Length / sizeof(float)];
      Buffer.BlockCopy(bytes, 0, soft, 0, soft.Length * sizeof(float));
      return soft;
    }

    private static SoftSymbols Soft(float[] s) => new() { Soft = s, SymbolRate = 1200 };


    // ---- golden vector -----------------------------------------------------------------------------

    [Fact]
    public void QaVector_ProducesReferenceFrame()
    {
      var frames = new Ao40FecDeframer().Deframe(Soft(Symbols()), P).ToList();

      frames.Should().ContainSingle();
      var f = frames[0];
      f.Hex.Should().Be(Ao40Vectors.Frame.ToUpperInvariant());
      f.Length.Should().Be(256, "an AO-40 frame is 2 x RS(160,128) worth of data");
      f.CrcValid.Should().BeTrue("Reed-Solomon is the integrity gate");
      f.Framing.Should().Be(Framing.AO40FEC);
      f.CorrectedBits.Should().Be(0, "the QA packet's codewords are clean");
      f.SoftBitOffset.Should().Be(Ao40Vectors.SyncOffset);
      f.SoftBitEnd.Should().Be(Ao40Vectors.SyncOffset + 5200, "the interleaved block is 80 x 65 symbols");
    }

    [Fact]
    public void InvertedStream_ProducesTheSameFrame()
    {
      var inverted = Symbols().Select(v => -v).ToArray();

      new Ao40FecDeframer().Deframe(Soft(inverted), P).Single()
        .Hex.Should().Be(Ao40Vectors.Frame.ToUpperInvariant());
    }


    // ---- FEC gate ----------------------------------------------------------------------------------

    [Fact]
    public void IsolatedSymbolErrors_AreAbsorbedByTheViterbi()
    {
      // scattered channel-symbol flips land on unrelated trellis steps, so the convolutional code alone
      // fixes them and RS sees a clean codeword — which is the point of interleaving before the FEC.
      var s = Symbols();
      foreach (int k in new[] { 700, 2100, 3600 }) s[Ao40Vectors.SyncOffset + k] = -s[Ao40Vectors.SyncOffset + k];

      var f = new Ao40FecDeframer().Deframe(Soft(s), P).Single();
      f.Hex.Should().Be(Ao40Vectors.Frame.ToUpperInvariant());
      f.CorrectedBits.Should().Be(0);
    }

    [Fact]
    public void CorrectableSymbolErrors_DecodeAndAreCounted()
    {
      // a run of consecutive *coded* symbols (spread far apart on air by the interleaver) overwhelms the
      // Viterbi locally and leaves an error burst for RS — a handful of bytes, inside the 16-per-codeword
      // capacity.
      var s = Symbols();
      for (int c = 400; c < 424; c++) FlipCodedSymbol(s, c);

      var f = new Ao40FecDeframer().Deframe(Soft(s), P).Single();
      f.Hex.Should().Be(Ao40Vectors.Frame.ToUpperInvariant());
      f.CorrectedBits.Should().BeInRange(1, 32, "RS had to repair the Viterbi error event");
    }

    /// <summary>Flip the channel symbol carrying coded symbol <paramref name="coded"/>: the deinterleaver
    /// reads coded index <c>c</c> from block offset <c>80·(m mod 65) + m/65</c>, <c>m = c + 65</c>.</summary>
    private static void FlipCodedSymbol(float[] s, int coded)
    {
      int m = coded + 65;
      int idx = Ao40Vectors.SyncOffset + 80 * (m % 65) + m / 65;
      s[idx] = -s[idx];
    }

    [Fact]
    public void UncorrectableErrors_YieldNoFrame()
    {
      // wreck a long run of symbols: the sync bits (every 80th) survive, so the block is still found, but
      // the damage is far past RS capacity.
      var s = Symbols();
      for (int k = 1000; k < 3000; k++) s[Ao40Vectors.SyncOffset + k] = -s[Ao40Vectors.SyncOffset + k];

      new Ao40FecDeframer().Deframe(Soft(s), P).Should().BeEmpty();
    }

    [Fact]
    public void SyncBitErrors_BeyondThreshold_YieldNoFrame()
    {
      var s = Symbols();
      for (int j = 0; j < 12; j++)
      {
        int idx = Ao40Vectors.SyncOffset + j * 80;
        s[idx] = -s[idx];
      }

      new Ao40FecDeframer().Deframe(Soft(s), P).Should().BeEmpty("12 > the default threshold of 8");
    }

    [Fact]
    public void SyncBitErrors_WithinThreshold_StillDecode()
    {
      var s = Symbols();
      for (int j = 0; j < 8; j++)
      {
        int idx = Ao40Vectors.SyncOffset + j * 80;
        s[idx] = -s[idx];
      }

      new Ao40FecDeframer().Deframe(Soft(s), P).Single()
        .Hex.Should().Be(Ao40Vectors.Frame.ToUpperInvariant());
    }


    // ---- stream handling ---------------------------------------------------------------------------

    [Fact]
    public void TwoAdjacentBlocks_BothDecode()
    {
      var s = Symbols();
      var block = s.Skip(Ao40Vectors.SyncOffset).Take(5200).ToArray();
      var twice = block.Concat(block).ToArray();

      var frames = new Ao40FecDeframer().Deframe(Soft(twice), P).ToList();

      frames.Should().HaveCount(2, "resuming past a decoded block must not skip the next one");
      frames.Should().OnlyContain(f => f.Hex == Ao40Vectors.Frame.ToUpperInvariant());
      frames[1].SoftBitOffset.Should().Be(5200);
    }

    [Fact]
    public void NoSync_YieldsNoFrames()
    {
      var soft = new SoftSymbols { Soft = Enumerable.Repeat(-1f, 12000).ToArray(), SymbolRate = 1200 };

      new Ao40FecDeframer().Deframe(soft, P).Should().BeEmpty();
    }

    [Fact]
    public void ShortStream_YieldsNoFrames()
    {
      new Ao40FecDeframer().Deframe(Soft(Symbols()[..5000]), P).Should().BeEmpty("a whole block never fits");
    }
  }
}
