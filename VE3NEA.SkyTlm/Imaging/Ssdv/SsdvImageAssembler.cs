using System;
using System.Collections.Generic;
using VE3NEA.SkyTlm.Core;

namespace VE3NEA.SkyTlm.Imaging.Ssdv
{
  /// <summary>
  /// Turns a satellite's frames into progressively-filling JPEGs. Frames become packets via the
  /// <see cref="SsdvSource"/>, packets are validated and filed by image, and every accepted packet
  /// re-transcodes the image it belongs to.
  /// <para>
  /// Re-transcoding the whole image per packet looks wasteful and is not: an image is a few hundred
  /// packets of bit-twiddling, which costs microseconds, and the alternative — incremental transcoder
  /// state that can absorb a packet arriving before its predecessors — is a great deal of machinery for
  /// no gain. Packets genuinely do arrive late, so the simple version is also the correct one.
  /// </para>
  /// </summary>
  public sealed class SsdvImageAssembler : IImageAssembler
  {
    private readonly SsdvSource source;
    private readonly SsdvImageSet images = new();
    private readonly HashSet<SsdvImageKey> completed = [];

    public SsdvImageAssembler(SsdvSource source) => this.source = source;

    public event Action<ImageProduct>? ImageUpdated;
    public event Action<ImageProduct>? ImageCompleted;

    /// <summary>Packets accepted so far, across every image — the assembler's own yield.</summary>
    public int PacketsAccepted { get; private set; }

    /// <summary>Frames that looked like image packets but failed validation.</summary>
    public int PacketsRejected { get; private set; }

    public void Push(Frame frame)
    {
      if (!source.TryExtract(frame, out var bytes)) return;

      if (!SsdvPacket.TryParse(bytes, source.Variant, out var packet))
      {
        PacketsRejected++;
        return;
      }

      var previous = images.Current;
      if (!images.Add(packet!, out var image, out bool isNewImage)) return;   // a duplicate changes nothing
      PacketsAccepted++;

      // The sender has moved on, so whatever it was sending before will get nothing further — except
      // stragglers, which is why the old image stays in the set and can still be updated below.
      if (isNewImage && previous != null && previous != image) Complete(previous);

      var product = Product(image);
      ImageUpdated?.Invoke(product);

      // An image with its last packet in hand is finished no matter what arrives next.
      if (image.IsComplete) Complete(image, product);
    }

    /// <summary>
    /// The verdict <see cref="Push"/> would reach on one frame, without assembling anything: <c>null</c>
    /// when the frame is not an SSDV packet of this source at all, otherwise whether it passed the
    /// packet's own CRC-32 and how much RS repair that took. For a caller describing a frame rather than
    /// accumulating it — see <see cref="ImagePacketCheck"/> for why the frame's own CRC cannot answer.
    /// </summary>
    public ImagePacketCheck? Check(Frame frame)
    {
      if (!source.TryExtract(frame, out var bytes)) return null;

      return SsdvPacket.TryParse(bytes, source.Variant, out var packet)
        ? new ImagePacketCheck(true, packet!.CorrectedBytes)
        : new ImagePacketCheck(false, 0);
    }

    /// <summary>
    /// End of stream. The pass is over, so the image still being received will get nothing more —
    /// announce it, incomplete as it is.
    /// </summary>
    public void Flush()
    {
      if (images.Current != null) Complete(images.Current);
    }

    private void Complete(SsdvImage image, ImageProduct? product = null)
    {
      if (!completed.Add(image.Key)) return;
      ImageCompleted?.Invoke(product ?? Product(image));
    }

    private ImageProduct Product(SsdvImage image) => new(
      ImageId: image.ImageId,
      Source: source.HasSenderId && image.Callsign.Length > 0 ? image.Callsign : null,
      Jpeg: SsdvTranscoder.Transcode(image.Packets),
      Width: image.Width,
      Height: image.Height,
      FragmentsReceived: image.PacketsReceived,
      FragmentsExpected: image.PacketsExpected,
      // SSDV has no such boundary: a lost packet costs its own MCUs and everything after it still lands
      // in the right place. See the remarks on ImageProduct.FirstGapOffset.
      FirstGapOffset: -1,
      Complete: image.IsComplete,
      Fragments: image.ToFragments(),
      // A packet is worth archiving only if it can be verified on its own, and the CRC is what does that.
      // Without one — JY1SAT, DSLWP — a packet damaged below the SSDV layer parses normally and renders
      // as a corrupt band, so a stored copy could not be told from a good one when rebuilding an image
      // later, and neither could a copy of a different picture that reused the image ID.
      FragmentFormat: source.Variant.HasCrc ? source.Variant.Name : null);
  }
}
