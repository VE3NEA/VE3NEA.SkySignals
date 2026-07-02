using FluentAssertions;
using VE3NEA.SkyTlm.Deframing;
using VE3NEA.SkyTlm.Tests.Fixtures;
using Xunit;

namespace VE3NEA.SkyTlm.Tests.Unit
{
  /// <summary>
  /// The soft (LLR) bit operations behind the soft-decision deframer:
  /// box-plus XOR/XNOR, soft NRZI decode, and soft G3RUH descramble. The signs (hard decisions) must be
  /// exact GF(2); the magnitude is the min of the inputs (the weakest link sets the confidence).
  /// </summary>
  public class SoftBitsTests
  {
    [Theory]
    // a, b (soft) -> expected hard XOR bit
    [InlineData(+2f, +3f, 0)]  // 1 ^ 1 = 0
    [InlineData(+2f, -3f, 1)]  // 1 ^ 0 = 1
    [InlineData(-2f, +3f, 1)]  // 0 ^ 1 = 1
    [InlineData(-2f, -3f, 0)]  // 0 ^ 0 = 0
    public void Xor_HardBit_IsGf2(float a, float b, int expected)
    {
      SoftBits.Hard(SoftBits.Xor(a, b)).Should().Be(expected);
      SoftBits.Hard(SoftBits.Xnor(a, b)).Should().Be(1 - expected); // XNOR is the complement
    }

    [Fact]
    public void Xor_Magnitude_IsMinConfidence()
    {
      System.Math.Abs(SoftBits.Xor(0.4f, -5f)).Should().BeApproximately(0.4f, 1e-6f);
      System.Math.Abs(SoftBits.Xnor(5f, 0.7f)).Should().BeApproximately(0.7f, 1e-6f);
    }

    [Fact]
    public void NrziDecode_RecoversData_AndIsPolarityInsensitive()
    {
      var data = GmskModulator.RandomBits(200, seed: 11);
      var enc = Ax25Tx.NrziEncode(data);

      foreach (float pol in new[] { +1f, -1f }) // global sign flip must not change the decode
      {
        var soft = Ax25Tx.ToSoft(System.Linq.Enumerable.ToArray(enc), pol);
        var dec = SoftBits.NrziDecode(soft);
        // nrziEncode seeds a reference level, so decoded[k] aligns with data[k] for k>=1.
        for (int k = 1; k < data.Length; k++)
          SoftBits.Hard(dec[k - 1]).Should().Be(data[k], $"NRZI must recover data bit {k} (pol {pol})");
      }
    }

    [Fact]
    public void G3ruhDescramble_InvertsScramble()
    {
      var data = GmskModulator.RandomBits(300, seed: 5);
      var scr = Ax25Tx.G3ruhScramble(System.Linq.Enumerable.ToArray(data));
      var soft = Ax25Tx.ToSoft(scr.ToArray());

      var des = SoftBits.G3ruhDescramble(soft);
      for (int n = 0; n < data.Length; n++)
        SoftBits.Hard(des[n]).Should().Be(data[n], $"descramble must invert scramble at bit {n}");
    }

    [Fact]
    public void Descramble_Then_Nrzi_RecoversData()
    {
      // the receive order (descramble the channel bits, then NRZI-decode) inverts the TX chain.
      var data = GmskModulator.RandomBits(400, seed: 9);
      var onair = Ax25Tx.G3ruhScramble(Ax25Tx.NrziEncode(System.Linq.Enumerable.ToArray(data)));
      var soft = Ax25Tx.ToSoft(onair.ToArray());

      var des = SoftBits.G3ruhDescramble(soft);
      var dec = SoftBits.NrziDecode(des);
      for (int k = 1; k < data.Length; k++)
        SoftBits.Hard(dec[k - 1]).Should().Be(data[k]);
    }
  }
}
