using System;
using System.Collections.Generic;
using VE3NEA.SkyTlm.Core;

namespace VE3NEA.SkyTlm.Imaging
{
  /// <summary>
  /// One fragment of an image, kept in the form it was accepted in so that a caller can archive it and
  /// hand it back later. This is what makes an image survive the pass it arrived in: a picture heard
  /// across several passes can only be assembled if the earlier passes' fragments were written down.
  /// </summary>
  /// <param name="Id">Position of the fragment within the image — an SSDV packet ID.</param>
  /// <param name="Bytes">The canonical fragment as validated, i.e. after any FEC repair. Re-parsing
  /// these bytes reproduces the fragment exactly, and on a family with a CRC it also re-checks it, so a
  /// stored fragment that was edited or truncated is rejected rather than trusted.</param>
  /// <param name="CorrectedBytes">Bytes the FEC repaired to get here, 0 when the fragment arrived clean.
  /// It cannot be recovered by re-parsing <paramref name="Bytes"/> — those are already repaired — so it
  /// must be carried alongside them. It is what ranks two copies of the same fragment against each
  /// other when an image is rebuilt from more than one reception.</param>
  public readonly record struct ImageFragment(int Id, byte[] Bytes, int CorrectedBytes);


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
  /// <param name="Fragments">The fragments this reconstruction was built from, ascending by ID. Empty
  /// when the family cannot hand them out in an archivable form.</param>
  /// <param name="FragmentFormat">Name of the format <see cref="Fragments"/> are in — an
  /// <see cref="Ssdv.SsdvVariant.Name"/> — or null when they must not be archived. Null means one of two
  /// things, and the caller need not tell them apart: the family yields no fragments at all, or its
  /// fragments carry no integrity check of their own and so cannot be trusted once they leave the pass
  /// that heard them. Only a named format may be written to an archive and read back.</param>
  public sealed record ImageProduct(
    int ImageId, string? Source, byte[] Jpeg, int Width, int Height,
    int FragmentsReceived, int FragmentsExpected, int FirstGapOffset, bool Complete,
    IReadOnlyList<ImageFragment> Fragments, string? FragmentFormat);


  /// <summary>
  /// Whether the image fragment inside one frame survived its own integrity check, and what that cost.
  /// Only the SSDV family answers: its packets carry a CRC-32 and RS(255,223) of their own, riding above
  /// framings — <see cref="Core.Framing.HADES"/>, <see cref="Core.Framing.AO40FEC"/> — that carry no frame
  /// CRC at all, so <c>Frame.CrcValid</c> is null on exactly the frames whose payload does have one. The
  /// raw-JPEG family has no per-fragment check, its framing's CRC being the only one, and never yields this.
  /// </summary>
  /// <param name="Ok">The fragment passed its checksum, possibly after FEC repair.</param>
  /// <param name="CorrectedBytes">Bytes the FEC had to repair to get there — 0 when the fragment arrived
  /// clean, and meaningless when <paramref name="Ok"/> is false. A packet that needed most of the RS
  /// capacity is one the link nearly lost, which is worth showing even though it decoded.</param>
  public readonly record struct ImagePacketCheck(bool Ok, int CorrectedBytes);


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
