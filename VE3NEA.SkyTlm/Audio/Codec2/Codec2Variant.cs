using System;
using VE3NEA;

namespace VE3NEA.SkyTlm.Audio.Codec2
{
  /// <summary>
  /// How one satellite's on-air voice payload relates to codec2's own frame format. The audio
  /// counterpart of <see cref="Imaging.Ssdv.SsdvVariant"/>, and here for the same reason: every
  /// constant that varies per satellite belongs in a record, not in a <c>switch</c> inside the decoder.
  /// </summary>
  /// <param name="Mode">A <c>NativeCodec2.MODE_*</c> id.</param>
  /// <param name="BitsPerFrame">Bits codec2 consumes per frame — must match
  /// <c>codec2_bits_per_frame</c>, which <see cref="Codec2Decoder"/> asserts at construction.</param>
  /// <param name="FramesPerPacket">Codec2 frames carried by one on-air sub-frame.</param>
  /// <param name="PayloadBytes">Payload bytes in one sub-frame, after the type/address and sub-frame
  /// number bytes are stripped.</param>
  /// <param name="XorMask">Fixed byte mask the payload is XORed with before anything else, or empty
  /// when the payload is sent in the clear. Not a scrambler — no state, no LFSR — but without it the
  /// payload is statistically indistinguishable from random data.</param>
  public sealed record Codec2Variant(int Mode, int BitsPerFrame, int FramesPerPacket,
                                     int PayloadBytes, byte[] XorMask)
  {
    /// <summary>Name of the variant, for logs and for the archive format tag.</summary>
    public string Name { get; init; } = "";

    /// <summary>
    /// HADES-SA packet type 11: Codec2 700C, ten 28-bit frames tight-packed into 35 bytes, XORed with
    /// the mask AMSAT-EA's decoder calls <c>xor_codec2</c>. 280 bits per sub-frame is 700 bps in the
    /// 0.40 s the sub-frame occupies at 800 bps, so the voice plays back in real time. Cross-checked
    /// three ways: AMSAT-EA's own <c>codec2-merge</c> writes a <c>.c2</c> header whose mode byte is 8
    /// (700C); 35 sub-frames × 400 ms = 14 s matches their published 15-second BBS message limit; and
    /// the decoded pitch track measures as coherent speech.
    /// </summary>
    public static readonly Codec2Variant HadesSa700C = new(
      Mode: NativeCodec2.MODE_700C, BitsPerFrame: 28, FramesPerPacket: 10, PayloadBytes: 35,
      XorMask:
      [
        0xED, 0x15, 0xD5, 0x3B, 0x34, 0x70, 0xE0, 0xFD, 0xED, 0x83, 0x90, 0xDB,
        0xAA, 0x2E, 0x25, 0xD6, 0x5E, 0x81, 0x41, 0x86, 0xBD, 0x67, 0x79, 0x5D,
        0x70, 0xA1, 0x13, 0xCE, 0x50, 0x0C, 0x19, 0xCA, 0xFB, 0x44, 0x0D,
      ])
    { Name = "HadesSa700C" };

    /// <summary>Bits carried by one sub-frame's payload — the packing is tight, so this need not be a
    /// whole number of bytes even though it happens to be here.</summary>
    public int BitsPerPacket => BitsPerFrame * FramesPerPacket;

    /// <summary>Audio samples one sub-frame decodes to, at <see cref="Codec2Decoder.SampleRate"/>.
    /// 40 ms per codec2 frame for every mode we bind, so this is <c>320 × FramesPerPacket</c>.</summary>
    public int SamplesPerPacket => Codec2Decoder.SamplesPerFrame * FramesPerPacket;

    /// <summary>Seconds of audio in one sub-frame.</summary>
    public double SecondsPerPacket => (double)SamplesPerPacket / Codec2Decoder.SampleRate;
  }
}
