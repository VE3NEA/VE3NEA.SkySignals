using System;
using VE3NEA.SkyTlm.Core;

namespace VE3NEA.SkyTlm.Imaging.RawJpeg
{
  /// <summary>
  /// How one protocol's frames become JPEG fragments — the raw-JPEG counterpart of
  /// <see cref="Ssdv.SsdvSource"/>, and the seam that lets <see cref="RawJpegAssembler"/> stay free of
  /// per-protocol branches. Geoscan is the first front-end; the USP file-transfer stream is meant to be
  /// the second, feeding the same assembler.
  /// <para>
  /// A source is stateless on purpose. SatsDecoder tracks "did the satellite change" in mutable fields on
  /// the receiver; here the front-end simply reports the identity it sees in each frame and the assembler
  /// compares successive keys, which is the same rule with nowhere for stale state to hide.
  /// </para>
  /// </summary>
  public sealed class RawJpegSource
  {
    private readonly Func<Frame, RawJpegFragment?> extract;

    private RawJpegSource(string name, bool hasSenderId, Func<Frame, RawJpegFragment?> extract)
    {
      Name = name;
      HasSenderId = hasSenderId;
      this.extract = extract;
    }

    /// <summary>The protocol this source reads.</summary>
    public string Name { get; }

    /// <summary>Whether <see cref="RawJpegImageKey.Sender"/> names something worth showing a user.
    /// True for Geoscan, whose <c>sat_num</c> identifies the satellite.</summary>
    public bool HasSenderId { get; }

    /// <summary>Geoscan v1 and v2, covering the whole fleet plus Lobachevsky.</summary>
    public static readonly RawJpegSource Geoscan = new("Geoscan", hasSenderId: true, GeoscanPayload.TryParse);

    /// <summary>
    /// Pull the image fragment out of a frame, or return false when the frame is not one. On this
    /// downlink most frames are not: AX.25 beacons and Geoscan-framed telemetry share it with the image
    /// data, so rejecting is the normal case rather than an error.
    /// </summary>
    public bool TryExtract(Frame frame, out RawJpegFragment fragment)
    {
      var parsed = extract(frame);
      fragment = parsed ?? default;
      return parsed != null;
    }
  }
}
