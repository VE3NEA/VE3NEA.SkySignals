using System;
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
  /// of them one to four fragments, and zero-filling every one of them renders the whole picture — each
  /// hole costs a DC-predictor colour shift below it and nothing more. Truncating cost 73 % of that
  /// picture to avoid a colour cast. It does <b>not</b> hold for progressive JPEG, which needs the scan
  /// walk below.
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
    /// </summary>
    public static byte[] ToJpeg(SparseImageBuffer buffer)
    {
      int end = TrustedEnd(buffer);
      if (end < 2) return [];

      var span = buffer.Span[..end];
      bool closed = span[^2] == 0xFF && span[^1] == 0xD9;
      var jpeg = new byte[end + (closed ? 0 : 2)];
      span.CopyTo(jpeg);
      if (!closed) { jpeg[^2] = 0xFF; jpeg[^1] = 0xD9; }
      return jpeg;
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
    /// Where to stop. Baseline is the whole buffer; progressive is the end of the last scan that
    /// cleared <see cref="ScanCoverageThreshold"/>, stopping before the first that did not, because a
    /// scan refines the ones before it and a scan that is mostly absent overwrites them with nothing.
    /// A file whose very first scan falls short keeps the old rule — the trusted prefix — which at worst
    /// is a blurry DC-only render and at best is all there is.
    /// </summary>
    private static int TrustedEnd(SparseImageBuffer buffer)
    {
      var table = JpegScanTable.Read(buffer.Span);
      if (table == null || !table.Progressive) return buffer.Length;

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
