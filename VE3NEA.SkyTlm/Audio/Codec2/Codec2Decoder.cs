using System;
using System.Collections.Generic;
using VE3NEA;

namespace VE3NEA.SkyTlm.Audio.Codec2
{
  /// <summary>What to put where a sub-frame was never received.</summary>
  public enum Concealment
  {
    /// <summary>Zero samples. Honest and cheap: the listener hears the gap, and nothing is invented.</summary>
    Silence,

    /// <summary>Re-decode the previous sub-frame's bits. Sounds smoother, but fabricates content that
    /// was never transmitted — so it is not the default.</summary>
    RepeatPrevious,
  }


  /// <summary>
  /// Turns a run of on-air voice sub-frames into 16-bit PCM. Owns one <c>libcodec2</c> instance and
  /// therefore the synthesis filter state, so a decoder must be fed a message's sub-frames in order
  /// and from the beginning — which is why <see cref="Codec2VoiceAssembler"/> re-decodes a whole
  /// message rather than appending to a running decoder.
  /// <para>
  /// The on-air payload is not in codec2's own frame format. Per sub-frame this class applies
  /// <see cref="Codec2Variant.XorMask"/>, then repacks <c>FramesPerPacket</c> tight-packed
  /// <c>BitsPerFrame</c>-bit frames into the byte-aligned, left-justified form
  /// <c>codec2_decode</c> reads. For HADES-SA that is 35 bytes → 10 × 4 bytes, and getting it wrong
  /// produces plausible noise rather than an error, which is what the differential fixtures are for.
  /// </para>
  /// </summary>
  public sealed class Codec2Decoder : IDisposable
  {
    /// <summary>Codec2 speaks 8 kHz mono, and only 8 kHz mono.</summary>
    public const int SampleRate = 8000;

    /// <summary>Samples one codec2 frame decodes to — 40 ms, the same for every mode we bind.</summary>
    public const int SamplesPerFrame = 320;

    private readonly Codec2Variant variant;
    private readonly int bytesPerFrame;
    private IntPtr codec;

    public Codec2Decoder(Codec2Variant variant)
    {
      this.variant = variant;
      codec = NativeCodec2.codec2_create(variant.Mode);
      if (codec == IntPtr.Zero)
        throw new InvalidOperationException($"codec2_create failed for mode {variant.Mode}");

      // The variant states the frame geometry so the repack can be written against it; the library is
      // the authority. Disagreement means the variant is wrong, and silence is the worst way to learn.
      int bits = NativeCodec2.codec2_bits_per_frame(codec);
      int samples = NativeCodec2.codec2_samples_per_frame(codec);
      bytesPerFrame = NativeCodec2.codec2_bytes_per_frame(codec);
      if (bits != variant.BitsPerFrame)
        throw new InvalidOperationException(
          $"{variant.Name}: variant says {variant.BitsPerFrame} bits/frame, libcodec2 says {bits}");
      if (samples != SamplesPerFrame)
        throw new InvalidOperationException(
          $"{variant.Name}: expected {SamplesPerFrame} samples/frame, libcodec2 says {samples}");
    }

    /// <summary>Bit-error estimate handed to codec2 per frame, 0 for a clean link. It only softens the
    /// postfilter and the voicing decision — it repairs nothing — but this downlink has no FEC and no
    /// CRC, so telling the codec that is worth doing.</summary>
    public float BerEstimate { get; set; }

    /// <summary>What fills a missing sub-frame.</summary>
    public Concealment Concealment { get; set; } = Concealment.Silence;

    /// <summary>
    /// Decode a contiguous span of sub-frame numbers <paramref name="first"/>..<paramref name="last"/>
    /// inclusive, taking each sub-frame's payload from <paramref name="payloads"/> and concealing the
    /// numbers it does not contain. The result is always
    /// <c>(last − first + 1) × SamplesPerPacket</c> samples long, so a lost sub-frame costs its own
    /// 400 ms and shifts nothing after it.
    /// </summary>
    public short[] Decode(IReadOnlyDictionary<int, byte[]> payloads, int first, int last)
    {
      int packets = last - first + 1;
      var pcm = new short[packets * variant.SamplesPerPacket];
      var frame = new short[SamplesPerFrame];
      var bits = new byte[bytesPerFrame];
      byte[]? previous = null;

      for (int p = 0; p < packets; p++)
      {
        byte[]? payload = payloads.TryGetValue(first + p, out var found) ? found : null;
        if (payload == null && Concealment == Concealment.RepeatPrevious) payload = previous;

        // A gap stays silent, but the codec still has to see it: skipping the frames would leave the
        // synthesis state where the last good sub-frame left it, so the audio would resume mid-note.
        if (payload == null)
        {
          Array.Clear(bits);
          for (int f = 0; f < variant.FramesPerPacket; f++)
            NativeCodec2.codec2_decode_ber(codec, frame, bits, BerEstimate);
          continue;   // pcm is already zero
        }

        previous = payload;
        var unmasked = Unmask(payload);
        for (int f = 0; f < variant.FramesPerPacket; f++)
        {
          Repack(unmasked, f, bits);
          NativeCodec2.codec2_decode_ber(codec, frame, bits, BerEstimate);
          frame.CopyTo(pcm, p * variant.SamplesPerPacket + f * SamplesPerFrame);
        }
      }
      return pcm;
    }

    /// <summary>Undo the fixed byte mask. A copy, so an archived fragment keeps its on-air form.</summary>
    private byte[] Unmask(byte[] payload)
    {
      var mask = variant.XorMask;
      var result = new byte[payload.Length];
      for (int i = 0; i < payload.Length; i++)
        result[i] = i < mask.Length ? (byte)(payload[i] ^ mask[i]) : payload[i];
      return result;
    }

    /// <summary>
    /// Copy codec2 frame <paramref name="index"/> out of the tight-packed payload into the byte-aligned
    /// form <c>codec2_decode</c> reads: <c>BitsPerFrame</c> bits MSB-first, left-justified, tail bits
    /// zero. This is the AMSAT-EA decoder's <c>add_padding_codec2</c>, one frame at a time.
    /// </summary>
    private void Repack(byte[] unmasked, int index, byte[] bits)
    {
      Array.Clear(bits);
      int start = index * variant.BitsPerFrame;
      for (int i = 0; i < variant.BitsPerFrame; i++)
      {
        int src = start + i;
        int bit = (unmasked[src >> 3] >> (7 - (src & 7))) & 1;
        if (bit != 0) bits[i >> 3] |= (byte)(1 << (7 - (i & 7)));
      }
    }

    public void Dispose()
    {
      if (codec == IntPtr.Zero) return;
      NativeCodec2.codec2_destroy(codec);
      codec = IntPtr.Zero;
    }
  }
}
