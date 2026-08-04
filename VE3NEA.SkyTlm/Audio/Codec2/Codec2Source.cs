using System;
using VE3NEA.SkyTlm.Core;

namespace VE3NEA.SkyTlm.Audio.Codec2
{
  /// <summary>
  /// How one satellite's frames carry voice sub-frames. The audio counterpart of
  /// <see cref="Imaging.Ssdv.SsdvSource"/>, and the gate that decides which frames are voice at all:
  /// on HADES-SA the sub-frames are interleaved with telemetry, SSDV and PN9 on one downlink, so
  /// <see cref="TryExtract"/> rejecting a frame is the normal case.
  /// </summary>
  public sealed class Codec2Source
  {
    private readonly Func<Frame, (int Number, byte[] Payload)?> extract;

    private Codec2Source(string name, Codec2Variant variant,
                         Func<Frame, (int, byte[])?> extract)
    {
      Name = name;
      Variant = variant;
      this.extract = extract;
    }

    /// <summary>The satellite this source describes.</summary>
    public string Name { get; }

    /// <summary>The codec2 variant its sub-frames decode under.</summary>
    public Codec2Variant Variant { get; }

    /// <summary>
    /// HADES-SA: packet type 11 on the GENESIS link. The deframer crops the packet to start at the
    /// type/address byte, so a voice frame is 37 bytes beginning <c>B3</c> — type 11 in the high
    /// nibble, address 3 in the low one — followed by the 8-bit sub-frame number and 35 payload bytes.
    /// <para>
    /// The gate is the length and that first byte, and it is all the defence there is: type 11 carries
    /// no CRC and no FEC, so a frame that gets through is decoded as speech whatever it holds. Off air
    /// this is not theoretical — of five type-13 frames in the reference capture, one was unrelated
    /// content that had passed the same gate. What keeps that out of the audio is the assembler's
    /// sub-frame-number continuity rule, not this.
    /// </para>
    /// </summary>
    public static readonly Codec2Source HadesSa = new("HADES-SA", Codec2Variant.HadesSa700C,
      frame =>
      {
        if (frame.Framing != Framing.HADES) return null;
        if (frame.Bytes.Length != HadesFrameLen) return null;
        if (frame.Bytes[0] != HadesTypeAddress) return null;
        return (frame.Bytes[1], frame.Bytes[2..]);
      });

    // 1 type/address byte + 1 sub-frame number + 35 payload bytes; type 11, source address 3.
    private const int HadesFrameLen = 37;
    private const byte HadesTypeAddress = (11 << 4) | 3;

    /// <summary>
    /// Pull the sub-frame number and payload out of this frame, or return false when the frame is not
    /// a voice sub-frame of this source. The payload is not validated — there is nothing to validate
    /// it against.
    /// </summary>
    public bool TryExtract(Frame frame, out int number, out byte[] payload)
    {
      var result = extract(frame);
      number = result?.Number ?? -1;
      payload = result?.Payload ?? [];
      return result != null;
    }
  }
}
