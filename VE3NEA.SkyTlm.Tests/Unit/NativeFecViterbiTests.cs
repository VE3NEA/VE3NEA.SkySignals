using System;
using FluentAssertions;
using VE3NEA.SkyTlm.Tests.Fixtures;
using VE3NEA;
using Xunit;
using Xunit.Abstractions;

namespace VE3NEA.SkyTlm.Tests.Unit
{
  /// <summary>
  /// Validates the x64 <c>fec.dll</c> (quiet/libfec) Viterbi r=1/2 k=7 decoder through the
  /// <see cref="NativeFec"/> P/Invoke layer: encode known bits with the CCSDS convolutional code in C#,
  /// hand the soft symbols to libfec, and confirm the chainback reproduces the bits. Pins the polynomial
  /// convention used by the USP deframer (<c>{79, -109}</c> = V27POLYB, V27POLYA-inverted — same as
  /// gr-satellites) and the soft-symbol mapping (255 = confident 1, 0 = confident 0).
  /// </summary>
  public class NativeFecViterbiTests
  {
    private readonly ITestOutputHelper output;
    public NativeFecViterbiTests(ITestOutputHelper o) => output = o;

    // CCSDS r=1/2 k=7: first output uses 0x4f, second uses 0x6d inverted.
    private static readonly int[] Polys = { 0x4f, -0x6d };

    private static int Parity(int x) { int p = 0; while (x != 0) { p ^= x & 1; x >>= 1; } return p; }

    /// <summary>Convolutionally encode data bits to libfec soft symbols (0/255), 2 per bit.</summary>
    private static byte[] Encode(int[] bits)
    {
      var syms = new byte[bits.Length * 2];
      int sr = 0;
      for (int i = 0; i < bits.Length; i++)
      {
        sr = ((sr << 1) | bits[i]) & 0x7f;
        int s0 = Parity(sr & 0x4f);              // poly0 = +0x4f
        int s1 = Parity(sr & 0x6d) ^ 1;          // poly1 = -0x6d -> invert
        syms[2 * i] = (byte)(s0 == 1 ? 255 : 0);
        syms[2 * i + 1] = (byte)(s1 == 1 ? 255 : 0);
      }
      return syms;
    }

    [Theory]
    [InlineData(128)]
    [InlineData(1000)]
    public void Viterbi_CleanRoundTrip_IsErrorFree(int n)
    {
      var rng = GmskModulator.RandomBits(n, seed: 17);
      var data = new int[n + 6];                  // 6 zero tail bits terminate the trellis at state 0
      Array.Copy(rng, data, n);

      var syms = Encode(data);                    // 2*(n+6) soft symbols

      IntPtr vp = NativeFec.create_viterbi27(n);  // allocates n+6 decisions
      vp.Should().NotBe(IntPtr.Zero, "fec.dll must allocate the decoder");
      try
      {
        NativeFec.set_viterbi27_polynomial(Polys);
        NativeFec.init_viterbi27(vp, 0);
        NativeFec.update_viterbi27_blk(vp, syms, n + 6); // feed all encoded bits incl. tail
        var outBytes = new byte[(n + 7) / 8];
        NativeFec.chainback_viterbi27(vp, outBytes, (uint)n, 0); // emit n data bits (skips 6 tail)

        int errs = 0;
        for (int i = 0; i < n; i++)
        {
          int bit = (outBytes[i >> 3] >> (7 - (i & 7))) & 1; // libfec packs MSB-first
          if (bit != data[i]) errs++;
        }
        output.WriteLine($"n={n} errors={errs}");
        errs.Should().Be(0, "a clean convolutional round-trip through libfec must be error-free");
      }
      finally { NativeFec.delete_viterbi27(vp); }
    }

    [Fact]
    public void Viterbi_CorrectsBitErrors()
    {
      const int n = 500;
      var data = new int[n + 6];
      Array.Copy(GmskModulator.RandomBits(n, seed: 5), data, n);
      var syms = Encode(data);

      // flip a handful of well-separated symbols (a few channel errors the code should fix).
      foreach (int k in new[] { 50, 137, 240, 401, 620, 800 }) syms[k] = (byte)(255 - syms[k]);

      IntPtr vp = NativeFec.create_viterbi27(n);
      try
      {
        NativeFec.set_viterbi27_polynomial(Polys);
        NativeFec.init_viterbi27(vp, 0);
        NativeFec.update_viterbi27_blk(vp, syms, n + 6);
        var outBytes = new byte[(n + 7) / 8];
        NativeFec.chainback_viterbi27(vp, outBytes, (uint)n, 0);

        int errs = 0;
        for (int i = 0; i < n; i++)
          if (((outBytes[i >> 3] >> (7 - (i & 7))) & 1) != data[i]) errs++;
        output.WriteLine($"residual errors after correction = {errs}");
        errs.Should().Be(0, "the r=1/2 k=7 code corrects a few isolated symbol errors");
      }
      finally { NativeFec.delete_viterbi27(vp); }
    }
  }
}
