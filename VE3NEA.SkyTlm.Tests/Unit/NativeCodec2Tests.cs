using System;
using System.IO;
using FluentAssertions;
using VE3NEA;
using VE3NEA.SkyTlm.Tests.Regression;
using Xunit;

namespace VE3NEA.SkyTlm.Tests.Unit
{
  /// <summary>
  /// Differential tests for the <c>libcodec2</c> binding: our P/Invoke path must reproduce
  /// <c>c2dec</c>'s output. The fixtures in <c>Data/Codec2</c> are that tool's own input and output
  /// (see <c>VE3NEA.Dsp/Vendor/codec2/README.md</c>), so the assertion is against the reference
  /// implementation rather than against our reading of it — and no C toolchain is needed at run time.
  /// <para>
  /// The comparison is a bound, not equality, because <c>codec2_rand()</c> is process-global (see
  /// <see cref="NativeCodec2"/>): only the first decode in a process is byte-identical to the
  /// reference. The bound still discriminates decisively — two decodes of the same bits differ by 15 %
  /// relative RMS, a one-bit unpacking error by 116 %.
  /// </para>
  /// </summary>
  public class NativeCodec2Tests
  {
    private static string Fixture(string name) => Path.Combine(TestPaths.DataDir, "Codec2", name);

    [Fact]
    public void Codec2_700C_HasTheFrameGeometryTheVariantClaims()
    {
      var codec = NativeCodec2.codec2_create(NativeCodec2.MODE_700C);
      codec.Should().NotBe(IntPtr.Zero, "codec2.dll must be present and expose mode 700C");
      try
      {
        NativeCodec2.codec2_bits_per_frame(codec).Should().Be(28);
        NativeCodec2.codec2_bytes_per_frame(codec).Should().Be(4);
        NativeCodec2.codec2_samples_per_frame(codec).Should().Be(320);   // 40 ms at 8 kHz
      }
      finally { NativeCodec2.codec2_destroy(codec); }
    }

    [Fact]
    public void Decode_700C_MatchesC2decSampleForSample()
    {
      byte[] bits = File.ReadAllBytes(Fixture("kristoff_700c.bit"));
      short[] expected = ReadPcm(Fixture("kristoff_700c.raw"));

      var codec = NativeCodec2.codec2_create(NativeCodec2.MODE_700C);
      try
      {
        int nbyte = NativeCodec2.codec2_bytes_per_frame(codec);
        int nsam = NativeCodec2.codec2_samples_per_frame(codec);
        int frames = bits.Length / nbyte;
        var actual = new short[frames * nsam];
        var frame = new short[nsam];
        var one = new byte[nbyte];

        for (int f = 0; f < frames; f++)
        {
          Array.Copy(bits, f * nbyte, one, 0, nbyte);
          NativeCodec2.codec2_decode(codec, frame, one);
          frame.CopyTo(actual, f * nsam);
        }

        actual.Should().HaveCount(expected.Length);
        RelativeRms(actual, expected).Should().BeLessThan(DecodeTolerance);
      }
      finally { NativeCodec2.codec2_destroy(codec); }
    }

    [Fact]
    public void DecodeBer_WithZeroEstimate_IsIdenticalToDecode()
    {
      // codec2_decode is literally codec2_decode_ber(..., 0.0), which is what lets Codec2Decoder use
      // the ber variant unconditionally and still agree with c2dec's default output. The two decodes
      // cannot be compared byte for byte — the shared RNG is at a different point for the second — so
      // this asserts what is checkable: the same speech, not merely something.
      byte[] bits = File.ReadAllBytes(Fixture("kristoff_700c.bit"));

      short[] plain = DecodeAll(bits, ber: null);
      short[] withBer = DecodeAll(bits, ber: 0f);

      withBer.Should().HaveCount(plain.Length);
      RelativeRms(withBer, plain).Should().BeLessThan(DecodeTolerance);
    }

    private static short[] DecodeAll(byte[] bits, float? ber)
    {
      var codec = NativeCodec2.codec2_create(NativeCodec2.MODE_700C);
      try
      {
        int nbyte = 4, nsam = 320;
        var actual = new short[bits.Length / nbyte * nsam];
        var frame = new short[nsam];
        var one = new byte[nbyte];
        for (int f = 0; f < bits.Length / nbyte; f++)
        {
          Array.Copy(bits, f * nbyte, one, 0, nbyte);
          if (ber is { } b) NativeCodec2.codec2_decode_ber(codec, frame, one, b);
          else NativeCodec2.codec2_decode(codec, frame, one);
          frame.CopyTo(actual, f * nsam);
        }
        return actual;
      }
      finally { NativeCodec2.codec2_destroy(codec); }
    }

    internal static short[] ReadPcm(string path)
    {
      byte[] raw = File.ReadAllBytes(path);
      var pcm = new short[raw.Length / 2];
      Buffer.BlockCopy(raw, 0, pcm, 0, pcm.Length * 2);
      return pcm;
    }

    /// <summary>
    /// RMS of the difference as a fraction of the reference's RMS. 0 when the decode is byte-identical,
    /// ~0.15 when only the shared RNG has moved on, and ≥ 1.1 when the bits were unpacked wrongly —
    /// see the remarks on this class for the measurements.
    /// </summary>
    internal static double RelativeRms(short[] actual, short[] expected)
    {
      double err = 0, signal = 0;
      for (int i = 0; i < expected.Length; i++)
      {
        double d = actual[i] - (double)expected[i];
        err += d * d;
        signal += (double)expected[i] * expected[i];
      }
      return Math.Sqrt(err / Math.Max(signal, 1e-9));
    }

    /// <summary>Comfortably above the RNG-induced 0.15 and far below the 1.1 a real defect gives.</summary>
    internal const double DecodeTolerance = 0.35;
  }
}
