using System;
using System.Collections.Generic;

namespace VE3NEA.SkyTlm.Imaging.RawJpeg
{
  /// <summary>
  /// A growable byte buffer written at arbitrary offsets — which is the whole of raw-JPEG reassembly.
  /// Each fragment says where in the file it belongs and the receiver seeks and writes, exactly as
  /// SatsDecoder's <c>Image.push_data</c> does against a real file on disk.
  /// <para>
  /// What this adds over a plain array is knowing <b>which bytes are real</b>. Unwritten bytes read as
  /// zero and a raw JPEG cannot survive a hole — the entropy stream desynchronises and everything after
  /// it is noise — so the buffer tracks the written ranges and reports where the first one ends. That
  /// offset is the only honest answer to "how much of this picture can I believe", and it is what
  /// <see cref="ImageProduct.FirstGapOffset"/> carries to the UI.
  /// </para>
  /// </summary>
  public sealed class SparseImageBuffer
  {
    /// <summary>
    /// Largest offset the buffer will accept. This is a sanity guard, not a format limit: a Geoscan
    /// offset is assembled from <c>subsystem_num</c> and a 16-bit field, and a telemetry frame misread
    /// as an image frame yields values like 948,290 and 16,308,290 (both measured, 2026-07-30) — each
    /// of which would otherwise ask for a multi-megabyte allocation.
    /// <para>
    /// Set well above any plausible picture rather than at one. SatDump allocates 878 × 56 = 49,168
    /// bytes for a Geoscan image, but the fleet sends 640×480 and a JPEG that size can exceed it, so
    /// using SatDump's number as a ceiling would risk truncating a real image. USP has no documented
    /// limit at all, and moves whole files.
    /// </para>
    /// </summary>
    public const int MaxLength = 512 * 1024;

    private byte[] bytes = [];
    // written ranges as [start, end), kept sorted and merged, so gaps are the spaces between them
    private readonly List<(int Start, int End)> spans = [];

    /// <summary>Highest offset written, plus one: how long the file would be if nothing were missing
    /// past the end of what has arrived.</summary>
    public int Length { get; private set; }

    /// <summary>Bytes actually received, as opposed to the zeros between them.</summary>
    public int BytesWritten { get; private set; }

    /// <summary>Number of separate written runs — one when the image is contiguous.</summary>
    public int SpanCount => spans.Count;

    /// <summary>
    /// Where the reconstruction stops being trustworthy: the end of the contiguous run that starts at
    /// byte 0. Equal to <see cref="Length"/> when nothing is missing, and 0 when even the first byte is,
    /// so "trustworthy prefix" is always <c>bytes[0..FirstGapOffset]</c> with no special cases.
    /// </summary>
    public int FirstGapOffset => spans.Count > 0 && spans[0].Start == 0 ? spans[0].End : 0;

    /// <summary>True when the buffer holds one unbroken run from byte 0.</summary>
    public bool IsContiguous => Length > 0 && FirstGapOffset == Length;

    /// <summary>Everything held, holes included and reading as zero.</summary>
    public ReadOnlySpan<byte> Span => bytes.AsSpan(0, Length);

    /// <summary>The part that can be believed — the run from byte 0 to the first gap.</summary>
    public ReadOnlySpan<byte> TrustedSpan => bytes.AsSpan(0, FirstGapOffset);

    /// <summary>
    /// Seek and write. Returns false when the fragment would put the image past <see cref="MaxLength"/>,
    /// which means the offset is not believable rather than that the image is genuinely that big.
    /// </summary>
    public bool Write(int offset, ReadOnlySpan<byte> data)
    {
      if (offset < 0 || data.Length == 0) return false;
      if (offset + data.Length > MaxLength) return false;

      int end = offset + data.Length;
      if (end > bytes.Length) Array.Resize(ref bytes, Math.Max(end, Math.Min(bytes.Length * 2, MaxLength)));
      data.CopyTo(bytes.AsSpan(offset));
      if (end > Length) Length = end;

      AddSpan(offset, end);
      return true;
    }

    /// <summary>
    /// Move everything held by <paramref name="by"/> bytes: positive drops that many from the front,
    /// negative makes that much room at the front. Geoscan offsets are absolute in the satellite's
    /// address space rather than the file's, so the receiver only learns where byte zero really is when a
    /// fragment carrying the SOI marker turns up — or, if none ever does, when a fragment arrives below
    /// everything seen so far. SatsDecoder has both operations too, as <c>rebase_offset</c> and
    /// <c>shift_image</c>, rewriting the file in place.
    /// </summary>
    public void Shift(int by)
    {
      if (by == 0 || Length == 0) return;
      if (by >= Length) { Clear(); return; }

      if (by > 0)
      {
        bytes.AsSpan(by, Length - by).CopyTo(bytes);
        Length -= by;
      }
      else
      {
        int room = -by;
        if (Length + room > MaxLength) { Clear(); return; }
        if (Length + room > bytes.Length) Array.Resize(ref bytes, Length + room);
        bytes.AsSpan(0, Length).CopyTo(bytes.AsSpan(room));
        bytes.AsSpan(0, room).Clear();
        Length += room;
      }

      var moved = new List<(int Start, int End)>(spans.Count);
      foreach (var (start, end) in spans)
      {
        int s = Math.Max(0, start - by), e = end - by;
        if (e > 0) moved.Add((s, e));
      }
      spans.Clear();
      spans.AddRange(moved);
      RecountBytes();
    }

    public void Clear()
    {
      spans.Clear();
      Length = 0;
      BytesWritten = 0;
    }

    /// <summary>Record [start, end) as written, merging it into any runs it touches or overlaps.</summary>
    private void AddSpan(int start, int end)
    {
      int i = 0;
      while (i < spans.Count && spans[i].End < start) i++;

      int at = i;
      while (i < spans.Count && spans[i].Start <= end)
      {
        start = Math.Min(start, spans[i].Start);
        end = Math.Max(end, spans[i].End);
        i++;
      }

      spans.RemoveRange(at, i - at);
      spans.Insert(at, (start, end));
      RecountBytes();
    }

    private void RecountBytes()
    {
      int n = 0;
      foreach (var (start, end) in spans) n += end - start;
      BytesWritten = n;
    }
  }
}
