using System;
using VE3NEA.SkyTlm.Core;

namespace VE3NEA.SkyTlm.Imaging
{
  /// <summary>
  /// One image as it stands right now — always a decodable JPEG, however little of it has arrived, so a
  /// UI can show it at any point in a pass and watch it fill in. Missing parts are neutral grey rather
  /// than absent.
  /// </summary>
  /// <param name="ImageId">The sender's image number. Only 8 bits on SSDV, and reused within hours, so
  /// it identifies an image within a pass but not across days.</param>
  /// <param name="Source">Who sent it, when the format carries a usable sender ID — null when it does
  /// not, which includes HADES-SA, where the callsign field is overloaded with constants.</param>
  /// <param name="Jpeg">The image file. Never empty once any fragment has been accepted.</param>
  /// <param name="Width">Image width in pixels.</param>
  /// <param name="Height">Image height in pixels.</param>
  /// <param name="FragmentsReceived">Distinct fragments accepted — SSDV packets, or raw-JPEG chunks.</param>
  /// <param name="FragmentsExpected">Total fragments in the image. Exact once the last one has arrived;
  /// before that only a lower bound, because a stream that stops gives no notice.</param>
  /// <param name="FirstGapOffset">Byte offset where the reconstruction stops being trustworthy, or −1
  /// when the concept does not apply. It applies to the raw-JPEG family, where one lost fragment
  /// desynchronises everything after it, so the UI has to be able to say where truth ends. It does
  /// <b>not</b> apply to SSDV, whose whole design is that a lost packet costs its own MCUs and nothing
  /// else — there, −1 means "no such boundary exists", not "no gaps".</param>
  /// <param name="Complete">Every fragment of the image has been received.</param>
  public sealed record ImageProduct(
    int ImageId, string? Source, byte[] Jpeg, int Width, int Height,
    int FragmentsReceived, int FragmentsExpected, int FirstGapOffset, bool Complete);


  /// <summary>
  /// Accumulates frames into images. One implementation per fragment protocol — SSDV packets and raw
  /// JPEG byte ranges share nothing but this interface and the product it yields.
  /// <para>
  /// <see cref="Push"/> may be fed every frame the deframer produces, unconditionally: an assembler
  /// silently ignores frames that are not its own, which on every supported satellite means the
  /// telemetry beacons interleaved with the image data.
  /// </para>
  /// </summary>
  public interface IImageAssembler
  {
    /// <summary>Offer one decoded frame. Frames that are not image fragments are ignored.</summary>
    void Push(Frame frame);

    /// <summary>
    /// End of the stream: finalise whatever image is still open. A pass almost never ends on an image
    /// boundary, so without this the last image of a pass would never be announced as finished.
    /// </summary>
    void Flush();

    /// <summary>Fires for each accepted fragment, with the image it belongs to.</summary>
    event Action<ImageProduct>? ImageUpdated;

    /// <summary>
    /// Fires when an image will receive nothing further: its last fragment arrived, the sender moved on
    /// to another image, or <see cref="Flush"/> was called. A completed image may still be incomplete —
    /// this says "no more is coming", not "all of it is here".
    /// </summary>
    event Action<ImageProduct>? ImageCompleted;
  }
}
