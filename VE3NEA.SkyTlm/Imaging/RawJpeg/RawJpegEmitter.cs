using System;
using System.Linq;
using System.Text;

namespace VE3NEA.SkyTlm.Imaging.RawJpeg
{
  /// <summary>
  /// Turns a partly filled <see cref="SparseImageBuffer"/> into the file a decoder should be shown.
  /// Shared by <see cref="RawJpegAssembler"/>, which emits what one pass heard, and
  /// <see cref="RawJpegMerge"/>, which emits what several passes heard together, so both answer the
  /// question the same way.
  /// <para>
  /// <b>The policy changed on 2026-09-13</b>, when the first real imaging capture arrived. Until then
  /// this was "everything up to the first gap", on the reasoning that a raw JPEG cannot survive a hole.
  /// The data refutes that for baseline JPEG: a 92.5 %-complete 800x600 photograph was 14 gaps, eleven
  /// of them one to four fragments, and zero-filling every one of them renders the whole picture, where
  /// truncating cost 73 % of it. It does <b>not</b> hold for progressive JPEG, which needs the scan
  /// walk below.
  /// </para>
  /// <para>
  /// <b>What a hole actually costs.</b> This comment used to say "a DC-predictor colour shift below it
  /// and nothing more". That undercounts by one: a gap desynchronises the entropy stream in two
  /// independent ways — the DC-predictor offset, and an MCU-count desync that displaces everything
  /// below the seam in the raster (on the picture above, by exactly one MCU). Emitting the whole buffer
  /// is still right; it simply leaves two defects to repair rather than one. Correcting both by hand
  /// took that picture's usable fraction from 26.8 % to 56 %, which is what the entropy walker is for.
  /// </para>
  /// </summary>
  internal static class RawJpegEmitter
  {
    /// <summary>
    /// How much of a progressive scan must have arrived for the scan to be emitted at all. Measured
    /// against the one real progressive image in hand (Geoscan-5 picture 14, 8 scans, 69.5 % covered):
    /// 0.5 stops after scan 3 and 0.35 stops after scan 6, which is marginally sharper, and both render
    /// a good 800x600 picture where the whole buffer renders black.
    /// <para>
    /// <b>Tuned on n = 1.</b> 0.5 is the conservative of the two and is what ships; revisit when a
    /// second progressive capture exists.
    /// </para>
    /// </summary>
    public const double ScanCoverageThreshold = 0.5;

    /// <summary>
    /// The file as far as it can be believed, closed with an EOI so a decoder will accept it. Holes
    /// inside it read as zero, which is what the buffer already holds.
    /// <para>
    /// <b>The entropy repair runs here, automatically, and by default.</b> On a baseline file with holes
    /// in its scan, <see cref="JpegEntropyRepair"/> puts the MCUs below each hole back where the encoder
    /// wrote them and fills what the holes destroyed with mid-gray, which is the difference between a
    /// picture that is displaced and colour-shifted below its first gap and one that is not. It has no
    /// tunable and no correct-looking wrong answer to choose between, so there is nothing for a dialog to
    /// ask; and it has to happen here rather than as a filter on the rendered bitmap, because these are
    /// the bytes that get auto-saved, sized, and merged. It declines by returning null — on a progressive
    /// file, on a file with restart markers, on one with no hole in its scan, and on anything it cannot
    /// re-encode exactly — and then the buffer is emitted as it always was.
    /// </para>
    /// </summary>
    public static byte[] ToJpeg(SparseImageBuffer buffer) => ToJpeg(buffer, true, out _);

    /// <summary>
    /// The same emission, with the repair switchable and reporting what it did.
    /// <para>
    /// <paramref name="repair"/> is the operator's "Repair Damaged Image" switch, and turning it off is
    /// exact rather than approximate: the buffer is never altered, so the unrepaired emission is the same
    /// bytes this always produced. <paramref name="repaired"/> is null whenever the repair did not run or
    /// declined, which is also exactly when the returned bytes are the raw buffer.
    /// </para>
    /// </summary>
    public static byte[] ToJpeg(SparseImageBuffer buffer, bool repair, out JpegRepair? repaired)
    {
      repaired = null;

      int end = TrustedEnd(buffer);
      if (end < 2) return [];

      var span = buffer.Span[..end];
      bool closed = span[^2] == 0xFF && span[^1] == 0xD9;
      var jpeg = new byte[end + (closed ? 0 : 2)];
      span.CopyTo(jpeg);
      if (!closed) { jpeg[^2] = 0xFF; jpeg[^1] = 0xD9; }

      if (!repair) return jpeg;

      var gaps = buffer.Gaps(0, end);
      var fixedUp = JpegEntropyRepair.TryRepair(jpeg, gaps, out var walk);
      if (fixedUp == null || walk == null) return jpeg;

      repaired = new JpegRepair(gaps.Count, [.. walk.Segments.Select(s => s.McuCount)],
        walk.DeclinedRuns, walk.RecoveredMcus, walk.McuCount);
      return fixedUp;
    }

    /// <summary>
    /// The transfer read as text, or null when it is not one. The Geoscan fleet's playlist mixes
    /// photographs with one-fragment ASCII slides — "4/5 VISIT GEOSCAN.SPACE FOR CAMPAIGN DETAILS!" —
    /// and assembling those into 56-byte "JPEGs" produced tree nodes that could never render. A whole
    /// file of printable ASCII that does not open with an SOI is not a picture and is not being sent as
    /// one.
    /// </summary>
    public static string? ToText(SparseImageBuffer buffer)
    {
      if (!buffer.IsContiguous) return null;

      var span = buffer.Span;
      if (span.Length == 0 || span[0] == 0xFF) return null;   // an SOI, or the first half of one

      foreach (byte b in span)
        if (b > 0x7E || b < 0x20 && b != 0x0D && b != 0x0A && b != 0x09) return null;

      return Encoding.ASCII.GetString(span).TrimEnd();
    }

    /// <summary>
    /// Where to stop, or 0 for "nothing here can be shown".
    /// <para>
    /// <b>The header must be whole first.</b> A hole in the entropy data costs the two defects above and
    /// leaves a picture; a hole in the quantisation or Huffman tables leaves no way to interpret a byte
    /// of what did arrive, and no emitter can repair it because the bytes are not in the file. Three
    /// images auto-saved on the 2026-09-13 passes were written to disk as unopenable <c>.jpg</c> files
    /// for want of this test. Two conditions, catching different things:
    /// </para>
    /// <list type="bullet">
    /// <item>The walk reached a scan at all. <see cref="JpegScanTable.Read"/> reports a derailed walk and
    /// a clean single-scan baseline file with the same two field values, so an empty scan list is the
    /// only signal that it lost sync — as it does on a hole inside the header segments, and on a buffer
    /// with no SOI.</item>
    /// <item>The first hole is at or past the first scan's data. That is what catches a hole landing
    /// strictly inside a segment payload while leaving the next marker covered: the walk sails through to
    /// the SOS and reports scans over a file whose DQT or DHT is zeros. The test is exact rather than
    /// heuristic — everything before the first scan is header, and a JPEG header is not partially usable.
    /// </item>
    /// </list>
    /// <para>
    /// Past that gate: baseline is the whole buffer; progressive is the end of the last scan that cleared
    /// <see cref="ScanCoverageThreshold"/>, stopping before the first that did not, because a scan refines
    /// the ones before it and a scan that is mostly absent overwrites them with nothing. A file whose very
    /// first scan falls short keeps the old rule — the trusted prefix — which at worst is a blurry DC-only
    /// render and at best is all there is.
    /// </para>
    /// <para>
    /// Ordering the header tests before <see cref="JpegScanTable.Progressive"/> also fixes that verdict
    /// for free: it is read off the SOF marker byte before anything checks that the marker's payload
    /// arrived, so consulting it only after the header is known whole stops it being decided from a
    /// segment of zeros.
    /// </para>
    /// </summary>
    private static int TrustedEnd(SparseImageBuffer buffer)
    {
      var table = JpegScanTable.Read(buffer.Span);
      if (table == null || table.Scans.Count == 0) return 0;        // no SOI, or never reached a scan
      if (buffer.FirstGapOffset < table.Scans[0].Start) return 0;   // a hole inside the header segments
      if (!table.Progressive) return buffer.Length;

      int end = 0;
      foreach (var (start, scanEnd) in table.Scans)
      {
        if (buffer.CoveredBytes(start, scanEnd) < (scanEnd - start) * ScanCoverageThreshold) break;
        end = scanEnd;
      }

      return end > 0 ? end : buffer.FirstGapOffset;
    }
  }
}
