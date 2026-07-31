using System;
using System.Collections.Generic;
using VE3NEA.SkyTlm.Core;

namespace VE3NEA.SkyTlm.Imaging.RawJpeg
{
  /// <summary>
  /// How one protocol's frames become JPEG fragments — the raw-JPEG counterpart of
  /// <see cref="Ssdv.SsdvSource"/>, and the seam that lets <see cref="RawJpegAssembler"/> stay free of
  /// per-protocol branches. Two front-ends feed the same assembler: Geoscan's CC1125 framing and USP's
  /// file-transfer messages, which have nothing in common but the shape of what they deliver.
  /// <para>
  /// A source is stateless on purpose. SatsDecoder tracks "did the satellite change" and "what was the
  /// file called" in mutable receiver fields; here each frame reports what it carries — identity, and a
  /// name or size where the protocol announces one — and the assembler is the only thing that remembers.
  /// Same rules, nowhere for stale state to hide.
  /// </para>
  /// </summary>
  public sealed class RawJpegSource
  {
    private readonly Func<Frame, IReadOnlyList<RawJpegFragment>> extract;

    private RawJpegSource(string name, bool hasSenderId, bool soiStartsNewImage,
                          Func<Frame, IReadOnlyList<RawJpegFragment>> extract)
    {
      Name = name;
      HasSenderId = hasSenderId;
      SoiStartsNewImage = soiStartsNewImage;
      this.extract = extract;
    }

    /// <summary>The protocol this source reads.</summary>
    public string Name { get; }

    /// <summary>
    /// Whether <see cref="RawJpegImageKey.Sender"/> names something worth showing a user. True for
    /// Geoscan, whose <c>sat_num</c> identifies the satellite; false for USP, whose sender field is a
    /// transfer session number and whose useful label is the file name instead.
    /// </summary>
    public bool HasSenderId { get; }

    /// <summary>
    /// Whether a second SOI marker means a second picture. It does for Geoscan, where nothing else
    /// reliably marks a boundary: <c>mtype 0x0901</c> is not sent before every image, so SOI is the only
    /// dependable signal, and SatsDecoder treats it that way. It does <b>not</b> for USP, which numbers
    /// its transfers — there, a picture ends when the session ID changes or a new INIT arrives, and
    /// reading SOI as a boundary would split one transfer whose blocks arrived out of order.
    /// </summary>
    public bool SoiStartsNewImage { get; }

    /// <summary>Geoscan v1 and v2, covering the whole fleet plus Lobachevsky.</summary>
    public static readonly RawJpegSource Geoscan =
      new("Geoscan", hasSenderId: true, soiStartsNewImage: true, f => Wrap(GeoscanPayload.TryParse(f)));

    /// <summary>USP <c>FILETRANSFER_*</c>, covering the Sputnix birds — Luca, 239Alferov, HyperView-1G.</summary>
    public static readonly RawJpegSource Usp =
      new("USP", hasSenderId: false, soiStartsNewImage: false, UspFileTransfer.Extract);

    /// <summary>
    /// Pull every image fragment out of a frame; empty when the frame carries none. On these downlinks
    /// most frames carry none — AX.25 beacons and telemetry share them with the file transfer — so an
    /// empty result is the normal case rather than an error. A single frame can yield more than one
    /// fragment: a USP frame may announce a file and carry a piece of it at the same time.
    /// </summary>
    public IReadOnlyList<RawJpegFragment> Extract(Frame frame) => extract(frame);

    /// <summary>Convenience for the single-fragment protocols and for tests.</summary>
    public bool TryExtract(Frame frame, out RawJpegFragment fragment)
    {
      var all = extract(frame);
      fragment = all.Count > 0 ? all[0] : default;
      return all.Count > 0;
    }

    private static IReadOnlyList<RawJpegFragment> Wrap(RawJpegFragment? one) => one == null ? [] : [one.Value];
  }
}
