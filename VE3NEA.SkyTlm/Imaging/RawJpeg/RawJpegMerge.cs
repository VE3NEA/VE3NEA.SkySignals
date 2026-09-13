using System;
using System.Collections.Generic;

namespace VE3NEA.SkyTlm.Imaging.RawJpeg
{
  /// <summary>
  /// Rebuilds one raw-JPEG picture from fragments heard on more than one occasion — the counterpart of
  /// <see cref="Ssdv.SsdvMerge"/>, and worth having for the same reason: the Geoscan fleet repeats a
  /// short playlist over successive passes, and two half-heard receptions of one picture are often most
  /// of a whole one. The 2026-09-12/13 capture has two runs of picture 5 and two of picture 14, and the
  /// runs that did not carry the file's head were discarded entirely before this existed.
  /// <para>
  /// <b>What stands in for a CRC.</b> A Geoscan v2 fragment has an identity — (sat_num, fnum, file
  /// offset), all three explicit in the frame — but no checksum of its own, so a stored fragment cannot
  /// be re-validated the way an SSDV packet can. Agreement between receptions is the substitute: two
  /// copies of one byte range that differ mean one of them is wrong, and this refuses rather than
  /// guesses. Across 254 repeat receptions in that capture the check never fired once.
  /// </para>
  /// <para>
  /// It is refused per <b>reception</b>, not per merge: the receptions arrive best-first, the live pass
  /// leading, so a single bad sidecar drops out and the rest of the archive still contributes. That is
  /// also what makes a cross-satellite merge safe to allow — Geoscan-4 and Geoscan-5 send the identical
  /// file for a given fnum today, and if the playlist ever diverges the overlap disagrees and the other
  /// bird's reception is dropped on the bytes, with no version negotiation anywhere.
  /// </para>
  /// </summary>
  public static class RawJpegMerge
  {
    /// <summary>
    /// The <see cref="ImageProduct.FragmentFormat"/> this merge reads. Geoscan v2 is the only raw-JPEG
    /// layout whose fragments can be archived at all — v1 offsets are positions in the satellite's
    /// address space and USP numbers a session rather than a file.
    /// </summary>
    public const string Format = "geoscan-v2";

    /// <summary>
    /// The picture several receptions make together, or null when nothing usable came of them.
    /// </summary>
    /// <param name="receptions">One fragment list per reception, <b>best first</b>: the live pass leads,
    /// then the archive newest first. A reception that contradicts what the ones before it established
    /// is dropped whole.</param>
    /// <param name="format">The format every fragment is in. Anything but <see cref="Format"/> is
    /// refused — receptions in different formats cannot be merged and must not be passed together.</param>
    /// <param name="imageId">The picture number. Unlike SSDV, a raw-JPEG fragment does not repeat it, so
    /// the caller — which decided these receptions are the same picture — has to supply it.</param>
    /// <param name="source">Sender label for the resulting <see cref="ImageProduct"/>, for the same
    /// reason: it is not in the fragments.</param>
    public static ImageProduct? Build(IEnumerable<IReadOnlyList<ImageFragment>> receptions, string? format,
      int imageId, string? source)
    {
      if (format != Format) return null;

      var buffer = new SparseImageBuffer();
      var kept = new SortedDictionary<int, ImageFragment>();
      int largest = 0;
      bool hasEoi = false;

      foreach (var reception in receptions)
      {
        if (Disagrees(buffer, reception)) continue;

        foreach (var f in reception)
        {
          if (f.Bytes.Length == 0 || !buffer.Write(f.Id, f.Bytes)) continue;
          kept[f.Id] = f;
          largest = Math.Max(largest, f.Bytes.Length);
          hasEoi |= EndsImage(f.Bytes);
        }
      }

      if (kept.Count == 0) return null;

      var text = RawJpegEmitter.ToText(buffer);
      byte[] jpeg = text != null ? [] : RawJpegEmitter.ToJpeg(buffer);
      JpegHeader.ReadSize(jpeg, out int width, out int height);

      return new ImageProduct(
        ImageId: imageId,
        Source: source,
        Jpeg: jpeg,
        Width: width,
        Height: height,
        FragmentsReceived: kept.Count,
        // fragments are uniform within a stream, so the span divided by the largest one seen is exact
        // for a whole picture and a sound lower bound before that — the same rule the assembler uses
        FragmentsExpected: largest == 0 ? 0 : Math.Max(kept.Count, (buffer.Length + largest - 1) / largest),
        FirstGapOffset: buffer.FirstGapOffset,
        // no protocol here states the file length, so both ends present and nothing missing between them
        // is the whole test, exactly as in the assembler
        Complete: buffer.IsContiguous && hasEoi,
        // what the picture was actually built from, which is not what was offered: a reception that was
        // refused contributed nothing and must not be counted or handed back as if it had
        Fragments: [.. kept.Values],
        FragmentFormat: Format,
        Text: text);
    }

    /// <summary>Whether any fragment of this reception contradicts a byte already assembled.</summary>
    private static bool Disagrees(SparseImageBuffer buffer, IReadOnlyList<ImageFragment> reception)
    {
      foreach (var f in reception)
        if (buffer.Conflicts(f.Id, f.Bytes)) return true;
      return false;
    }

    private static bool EndsImage(byte[] data)
    {
      for (int i = 0; i + 1 < data.Length; i++)
        if (data[i] == 0xFF && data[i + 1] == 0xD9) return true;
      return false;
    }
  }
}
