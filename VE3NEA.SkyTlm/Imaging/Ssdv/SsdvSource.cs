using System;
using VE3NEA.SkyTlm.Core;

namespace VE3NEA.SkyTlm.Imaging.Ssdv
{
  /// <summary>
  /// How one satellite's frames relate to canonical SSDV packets. Operators trim the packet to fit their
  /// link — dropping bytes whose values are fixed, or carrying the packet inside a larger frame — so
  /// between the deframer and <see cref="SsdvPacket.TryParse"/> there is always a small, bird-specific
  /// step. Keeping it here is what lets the parser and the transcoder stay free of per-satellite
  /// branches.
  /// <para>
  /// A source is also the gate that decides which frames are image data at all: on every supported
  /// satellite the image packets are interleaved with telemetry on the same downlink, so
  /// <see cref="TryExtract"/> rejecting a frame is the normal case, not an error.
  /// </para>
  /// </summary>
  public sealed class SsdvSource
  {
    private readonly Func<Frame, byte[]?> extract;

    private SsdvSource(string name, SsdvVariant variant, bool hasSenderId, Func<Frame, byte[]?> extract)
    {
      Name = name;
      Variant = variant;
      HasSenderId = hasSenderId;
      this.extract = extract;
    }

    /// <summary>The satellite this source describes.</summary>
    public string Name { get; }

    /// <summary>The packet variant its frames reconstruct to.</summary>
    public SsdvVariant Variant { get; }

    /// <summary>
    /// Whether the callsign field holds a real sender ID. False for HADES-SA, where AMSAT-EA overloaded
    /// those four bytes with the GENESIS sync, size and address constants — decoding them yields a
    /// string, but not one worth showing anybody.
    /// </summary>
    public bool HasSenderId { get; }

    /// <summary>
    /// HADES-SA: packet type 10 on the GENESIS link. AMSAT-EA drops the five leading bytes whose values
    /// never vary — the SSDV sync and type (<c>55 66</c>) and the first three bytes of the callsign
    /// field, which are really the GENESIS sync and size (<c>BF 35 FB</c>). Putting them back yields a
    /// bit-exact standard 256-byte packet, CRC and RS included.
    /// <para>
    /// The deframer crops a HADES packet to start at the type/address byte, so a type-10 frame is 251
    /// bytes beginning <c>A3</c> — type 10 in the high nibble, address 3 in the low one.
    /// </para>
    /// </summary>
    public static readonly SsdvSource HadesSa = new("HADES-SA", SsdvVariant.HadesSa251, hasSenderId: false,
      frame =>
      {
        if (frame.Framing != Framing.HADES) return null;
        if (frame.Bytes.Length != 251 || frame.Bytes[0] >> 4 != 10) return null;

        var packet = new byte[256];
        HadesLeadBytes.CopyTo(packet, 0);
        frame.Bytes.CopyTo(packet, HadesLeadBytes.Length);
        return packet;
      });

    private static readonly byte[] HadesLeadBytes = [0x55, 0x66, 0xBF, 0x35, 0xFB];

    /// <summary>
    /// Reconstruct the canonical SSDV packet this frame carries, or return false when the frame is not
    /// an image packet of this source. The bytes are not validated here — that is
    /// <see cref="SsdvPacket.TryParse"/>'s job, and it is the CRC there that decides.
    /// </summary>
    public bool TryExtract(Frame frame, out byte[] packet)
    {
      packet = extract(frame) ?? [];
      return packet.Length > 0;
    }
  }
}
