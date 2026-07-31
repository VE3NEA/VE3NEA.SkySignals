using System;
using System.Collections.Generic;
using VE3NEA.SkyTlm.Core;

namespace VE3NEA.SkyTlm.Imaging.RawJpeg
{
  /// <summary>
  /// Assembles JPEG files sent as absolute byte ranges. The counterpart of
  /// <see cref="Ssdv.SsdvImageAssembler"/>, and deliberately not a variation on it: SSDV fragments carry
  /// MCU structure and survive loss, these carry none and do not. A hole here desynchronises the entropy
  /// stream and costs everything after it, so this assembler's job is less "fill in the gaps" than
  /// "report exactly where the picture stops being true".
  /// <para>
  /// Only one image is held at a time, unlike the SSDV side which keeps images side by side. That is
  /// forced by the format: offsets are relative to a base the receiver infers, and re-basing rewrites the
  /// buffer, so a fragment arriving after the sender moved on cannot be placed in the old image any more.
  /// SatsDecoder is built the same way, around a single <c>cur_img</c>.
  /// </para>
  /// </summary>
  public sealed class RawJpegAssembler : IImageAssembler
  {
    private readonly RawJpegSource source;
    private Image? current;
    private int imagesSeen;

    public RawJpegAssembler(RawJpegSource source) => this.source = source;

    public event Action<ImageProduct>? ImageUpdated;
    public event Action<ImageProduct>? ImageCompleted;

    /// <summary>Fragments written into an image.</summary>
    public int FragmentsAccepted { get; private set; }

    /// <summary>Fragments that parsed but could not be placed, which in practice means an offset far
    /// enough out to be unbelievable — see <see cref="SparseImageBuffer.MaxLength"/>.</summary>
    public int FragmentsRejected { get; private set; }

    public void Push(Frame frame)
    {
      if (!source.TryExtract(frame, out var fragment)) return;

      // The sender has moved on when the identity changes, when it says so outright, or when a fragment
      // carries an SOI — a second start-of-image can only be a second picture. SatsDecoder makes the
      // same three decisions, via force_new and get_image(has_soi).
      if (current == null || fragment.Key != current.Key || fragment.IsStart || fragment.HasSoi)
        StartNew(fragment);
      else if (!current.HasSoi && fragment.Offset < current.BaseOffset)
      {
        // No SOI has been seen, so the base is only a guess: the lowest offset so far. A lower one
        // arriving means the guess was wrong, and the picture starts further back than assumed.
        current.Buffer.Shift(fragment.Offset - current.BaseOffset);
        current.BaseOffset = fragment.Offset;
      }

      int at = fragment.Offset - current.BaseOffset;
      if (at < 0)
      {
        // Below a base that the SOI marker fixed, so this belongs to neither this picture nor a rebased
        // version of it. The reference restarts with an unshifted base; so do we.
        StartNew(fragment);
        current.BaseOffset = 0;
        at = fragment.Offset;
      }

      if (!current.Buffer.Write(at, fragment.Data)) { FragmentsRejected++; return; }

      FragmentsAccepted++;
      current.Fragments++;
      current.LargestFragment = Math.Max(current.LargestFragment, fragment.Data.Length);
      if (fragment.HasEoi) current.HasEoi = true;

      var product = Product(current);
      ImageUpdated?.Invoke(product);
      if (current.IsComplete) Complete(current, product);
    }

    /// <summary>End of stream: the open image will get nothing more, so announce it as it stands.</summary>
    public void Flush()
    {
      if (current != null) Complete(current);
    }

    private void StartNew(RawJpegFragment fragment)
    {
      if (current != null) Complete(current);

      current = new Image
      {
        Key = fragment.Key,
        // Its own offset until something better is known: for an SOI fragment that is exactly right, and
        // otherwise it keeps the buffer small instead of honouring an address-space offset of millions.
        BaseOffset = fragment.Offset,
        HasSoi = fragment.HasSoi,
        Id = imagesSeen++
      };
    }

    private void Complete(Image image, ImageProduct? product = null)
    {
      if (image.Announced) return;
      image.Announced = true;
      ImageCompleted?.Invoke(product ?? Product(image));
    }

    private ImageProduct Product(Image image)
    {
      var jpeg = image.ToJpeg();
      JpegHeader.ReadSize(jpeg, out int width, out int height);

      return new ImageProduct(
        // No raw-JPEG protocol numbers its pictures, so this counts them within the pass. Geoscan v2
        // does have fnum, which is in the key and is what separates its images; it is not an ID either,
        // being a slot number the satellite reuses.
        ImageId: image.Id,
        Source: source.HasSenderId ? SenderName(image.Key.Sender) : null,
        Jpeg: jpeg,
        Width: width,
        Height: height,
        FragmentsReceived: image.Fragments,
        FragmentsExpected: image.FragmentsExpected,
        FirstGapOffset: image.Buffer.FirstGapOffset,
        Complete: image.IsComplete);
    }

    /// <summary>SatsDecoder's platform table, for the birds that carry cameras.</summary>
    private static string? SenderName(int sender) => sender switch
    {
      0x01 => "Geoscan-Edelveis",
      0x02 => "StratoSat-TK1",
      0x0B => "Geoscan-1",
      0x0C => "Geoscan-2",
      0x0D => "Geoscan-3",
      0x0E => "Geoscan-4",
      0x0F => "Geoscan-5",
      0x10 => "Geoscan-6",
      0x12 => "Lobachevsky",
      _ => null
    };




    // ----------------------------------------------------------------------------------------------------
    //                                        one image in progress
    // ----------------------------------------------------------------------------------------------------
    private sealed class Image
    {
      public readonly SparseImageBuffer Buffer = new();

      public RawJpegImageKey Key { get; init; }
      public int Id { get; init; }

      /// <summary>Offset in the satellite's address space that corresponds to byte 0 of the file.</summary>
      public int BaseOffset { get; set; }

      /// <summary>An SOI marker has been seen, so <see cref="BaseOffset"/> is known rather than guessed.</summary>
      public bool HasSoi { get; set; }

      public bool HasEoi { get; set; }
      public int Fragments { get; set; }
      public int LargestFragment { get; set; }
      public bool Announced { get; set; }

      /// <summary>
      /// Both ends of the file present and nothing missing in between. Stricter than SatsDecoder, which
      /// calls an image finished on a heuristic — an EOI in a fragment shorter than its predecessor —
      /// because that has to decide without being able to see the gaps, and this does not.
      /// </summary>
      public bool IsComplete => HasSoi && HasEoi && Buffer.IsContiguous;

      /// <summary>
      /// How many fragments the picture would take if none were missing. Fragments are uniform within a
      /// stream (64 bytes on Geoscan v1, 54 on v2), so dividing the span by the largest one seen is exact
      /// for a complete image and a sound lower bound before that — which is what
      /// <see cref="ImageProduct.FragmentsExpected"/> asks for.
      /// </summary>
      public int FragmentsExpected => LargestFragment == 0
        ? 0
        : Math.Max(Fragments, (Buffer.Length + LargestFragment - 1) / LargestFragment);

      /// <summary>
      /// The file as far as it can be believed: everything up to the first gap, closed with an EOI so a
      /// decoder will accept it. Nothing is filled in — a raw JPEG has no structure to align a filler to,
      /// and a plausible-looking wrong picture is worse than a short one.
      /// </summary>
      public byte[] ToJpeg()
      {
        var trusted = Buffer.TrustedSpan;
        if (trusted.Length < 2) return [];

        bool closed = trusted[^2] == 0xFF && trusted[^1] == 0xD9;
        var jpeg = new byte[trusted.Length + (closed ? 0 : 2)];
        trusted.CopyTo(jpeg);
        if (!closed) { jpeg[^2] = 0xFF; jpeg[^1] = 0xD9; }
        return jpeg;
      }
    }
  }
}
