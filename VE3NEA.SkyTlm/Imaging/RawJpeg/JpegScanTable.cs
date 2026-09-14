using System;
using System.Collections.Generic;

namespace VE3NEA.SkyTlm.Imaging.RawJpeg
{
  /// <summary>
  /// The scan structure of a JPEG file: whether it is progressive, and where each scan's entropy-coded
  /// data begins and ends. <see cref="JpegHeader"/> stops at the frame header because a size is all a
  /// complete file needs; a partly received <b>progressive</b> file needs more.
  /// <para>
  /// A baseline file is one scan and degrades gracefully — a hole below the header costs a DC-predictor
  /// colour shift and an MCU-count desync, both repairable, and the picture survives either way. (This
  /// said "a colour shift below it and nothing else" until 2026-09-13; see <see cref="RawJpegEmitter"/>
  /// for the correction.) A progressive file is a stack of scans that refine one another, and a scan that is
  /// mostly missing does not degrade the picture, it destroys it: the 2026-09-12/13 Geoscan capture has
  /// an 8-scan 800x600 photograph whose whole buffer renders <b>pure black</b> and whose first four
  /// scans render a clean picture. Choosing where to stop needs the scan boundaries, which is this.
  /// </para>
  /// <para>
  /// It also carries the frame detail the entropy walker needs to <b>decode</b> a baseline file rather
  /// than merely locate its scans: the sampling factors, the Huffman tables, the restart interval, and
  /// the MCU count the walker counts back from. All of it is read on the same marker walk, because the
  /// walk is already here and a second one over the same damaged bytes could only disagree with it.
  /// </para>
  /// </summary>
  internal sealed class JpegScanTable
  {
    private JpegScanTable(bool progressive, bool baseline, int width, int height,
      IReadOnlyList<JpegFrameComponent> components, int restartInterval,
      IReadOnlyList<JpegHuffmanTable> huffmanTables, IReadOnlyList<JpegScan> scans)
    {
      Progressive = progressive;
      Baseline = baseline;
      Width = width;
      Height = height;
      Components = components;
      RestartInterval = restartInterval;
      HuffmanTables = huffmanTables;
      Scans = scans;

      int hMax = 0, vMax = 0;
      foreach (var c in components) { hMax = Math.Max(hMax, c.H); vMax = Math.Max(vMax, c.V); }

      // an interleaved baseline scan covers the picture in MCUs of hMax x vMax blocks, the partial ones
      // at the right and bottom edges included, which is what makes the total a count the walker can
      // subtract from rather than an estimate
      if (hMax > 0 && vMax > 0 && width > 0 && height > 0)
      {
        McusPerRow = (width + 8 * hMax - 1) / (8 * hMax);
        McuRows = (height + 8 * vMax - 1) / (8 * vMax);
      }
    }

    /// <summary>The frame header is one of the progressive SOFs — 0xC2 and the three variants of it —
    /// so the scans below refine one another rather than each covering their own part of the picture.</summary>
    public bool Progressive { get; }

    /// <summary>The frame header is SOF0, the only one the entropy walker decodes. Extended sequential
    /// and lossless are not progressive either, so <c>!Progressive</c> is not the same question.</summary>
    public bool Baseline { get; }

    /// <summary>Picture width as the frame header gives it, or 0 when the walk never reached one.</summary>
    public int Width { get; }

    /// <summary>Picture height as the frame header gives it, or 0 when the walk never reached one.</summary>
    public int Height { get; }

    /// <summary>The frame's components in the order the frame header lists them, which is the order an
    /// interleaved scan visits them in within each MCU.</summary>
    public IReadOnlyList<JpegFrameComponent> Components { get; }

    /// <summary>MCUs between restart markers, or 0 when the file carries no DRI. A file that has one
    /// resynchronises both the DC predictor and the MCU count by itself, and needs no walker.</summary>
    public int RestartInterval { get; }

    /// <summary>Every table the file's DHT segments declare, in file order. A table may be redefined,
    /// in which case both definitions are here and the later one is the one in force.</summary>
    public IReadOnlyList<JpegHuffmanTable> HuffmanTables { get; }

    /// <summary>MCUs across one MCU row, or 0 when there is no frame header to derive it from.</summary>
    public int McusPerRow { get; }

    /// <summary>MCU rows in the frame, or 0 when there is no frame header to derive it from.</summary>
    public int McuRows { get; }

    /// <summary>Total MCUs the encoder wrote. The walker's right-to-left accounting starts here: the
    /// last surviving entropy segment necessarily ends on the last MCU.</summary>
    public int McuCount => McusPerRow * McuRows;

    /// <summary>Entropy-coded data of each scan as [Start, End), in file order. The ranges exclude the
    /// SOS segment itself, so a scan that is dropped takes its data and leaves its header.</summary>
    public IReadOnlyList<JpegScan> Scans { get; }

    /// <summary>
    /// Walk the markers of a partly received file. Returns null when it does not even open with an SOI,
    /// and stops early — reporting what it found so far — where the walk loses sync, which is what a
    /// hole in the header segments does. Both are normal off air and neither is an error.
    /// </summary>
    public static JpegScanTable? Read(ReadOnlySpan<byte> jpeg)
    {
      if (jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8) return null;

      bool progressive = false, baseline = false;
      int width = 0, height = 0, restartInterval = 0;
      var components = new List<JpegFrameComponent>();
      var huffmanTables = new List<JpegHuffmanTable>();
      var scans = new List<JpegScan>();

      int at = 2;
      while (at + 1 < jpeg.Length)
      {
        if (jpeg[at] != 0xFF) break;                      // lost sync: not a marker boundary any more
        byte marker = jpeg[at + 1];

        if (marker == 0xFF) { at++; continue; }           // fill bytes before a marker are legal
        if (marker == 0xD8 || marker == 0x01 || marker >= 0xD0 && marker <= 0xD7) { at += 2; continue; }
        if (marker == 0xD9) break;                        // end of image

        if (at + 3 >= jpeg.Length) break;
        int len = jpeg[at + 2] << 8 | jpeg[at + 3];
        if (len < 2) break;

        // the segment payload, which is everything after the two length bytes. A segment the file stops
        // inside is left unparsed rather than half-parsed; the walk breaks out below on the same bytes.
        int payloadAt = at + 4, payloadLen = len - 2;
        bool whole = payloadAt + payloadLen <= jpeg.Length;
        var payload = whole ? jpeg.Slice(payloadAt, payloadLen) : default;

        // SOF0-SOF15, less the three 0xC-something markers that are not frame headers: DHT, JPGA, DAC.
        // Of those that are, the progressive ones are 0xC2 and the differential/arithmetic variants of
        // it, which is the low two bits reading 2.
        if (marker >= 0xC0 && marker <= 0xCF && marker != 0xC4 && marker != 0xC8 && marker != 0xCC)
        {
          progressive = (marker & 0x03) == 0x02;
          baseline = marker == 0xC0;
          if (whole) ReadFrame(payload, ref width, ref height, components);
        }

        if (marker == 0xC4 && whole) ReadHuffmanTables(payload, huffmanTables);
        if (marker == 0xDD && whole && payload.Length >= 2) restartInterval = payload[0] << 8 | payload[1];

        if (marker == 0xDA)
        {
          int start = at + 2 + len;
          if (start >= jpeg.Length) break;
          int end = EntropyEnd(jpeg, start);
          scans.Add(new JpegScan(start, end, whole ? ReadScanComponents(payload) : []));
          at = end;
          continue;
        }

        at += 2 + len;
      }

      return new JpegScanTable(progressive, baseline, width, height, components, restartInterval,
        huffmanTables, scans);
    }




    // ----------------------------------------------------------------------------------------------------
    //                                          segment payloads
    // ----------------------------------------------------------------------------------------------------
    /// <summary>
    /// The frame header payload: precision, size, and one triplet per component. A payload too short for
    /// the component count it declares keeps the components it did cover, because a frame header with a
    /// hole in it is the case the emitter's header gate refuses and the walker therefore never sees.
    /// </summary>
    private static void ReadFrame(ReadOnlySpan<byte> payload, ref int width, ref int height,
      List<JpegFrameComponent> components)
    {
      if (payload.Length < 6) return;

      height = payload[1] << 8 | payload[2];
      width = payload[3] << 8 | payload[4];

      components.Clear();
      int count = payload[5];
      for (int i = 0; i < count && 6 + i * 3 + 2 < payload.Length; i++)
      {
        var c = payload.Slice(6 + i * 3, 3);
        components.Add(new JpegFrameComponent(c[0], c[1] >> 4, c[1] & 0x0F, c[2]));
      }
    }

    /// <summary>
    /// A DHT segment, which may declare several tables one after another: a class-and-id byte, the count
    /// of codes of each length 1 to 16, and then that many values.
    /// </summary>
    private static void ReadHuffmanTables(ReadOnlySpan<byte> payload, List<JpegHuffmanTable> into)
    {
      int at = 0;
      while (at + 17 <= payload.Length)
      {
        byte classAndId = payload[at];
        var counts = payload.Slice(at + 1, 16);

        int total = 0;
        foreach (byte n in counts) total += n;
        if (at + 17 + total > payload.Length) return;     // a table the segment stops inside

        into.Add(new JpegHuffmanTable(classAndId >> 4, classAndId & 0x0F,
          counts.ToArray(), payload.Slice(at + 17, total).ToArray()));
        at += 17 + total;
      }
    }

    /// <summary>
    /// The scan header payload: one component-selector and table-selector pair per component in the
    /// scan, then the three spectral-selection bytes a baseline scan does not vary.
    /// </summary>
    private static JpegScanComponent[] ReadScanComponents(ReadOnlySpan<byte> payload)
    {
      if (payload.Length < 1) return [];

      int count = payload[0];
      if (1 + count * 2 > payload.Length) return [];

      var components = new JpegScanComponent[count];
      for (int i = 0; i < count; i++)
      {
        var c = payload.Slice(1 + i * 2, 2);
        components[i] = new JpegScanComponent(c[0], c[1] >> 4, c[1] & 0x0F);
      }
      return components;
    }

    /// <summary>
    /// Where a scan's entropy-coded data ends: the first 0xFF that introduces a real marker. Inside the
    /// data an 0xFF is either stuffed (followed by 0x00), a restart marker, or a fill byte, and none of
    /// those ends the scan.
    /// </summary>
    private static int EntropyEnd(ReadOnlySpan<byte> jpeg, int start)
    {
      for (int at = start; at + 1 < jpeg.Length; at++)
      {
        if (jpeg[at] != 0xFF) continue;
        byte next = jpeg[at + 1];
        if (next != 0x00 && next != 0xFF && !(next >= 0xD0 && next <= 0xD7)) return at;
      }
      return jpeg.Length;
    }
  }




  // ----------------------------------------------------------------------------------------------------
  //                                   what the header segments declare
  // ----------------------------------------------------------------------------------------------------
  /// <summary>One component of the frame: its identifier, its sampling factors, and which quantisation
  /// table it was quantised with. The walker repairs in the coefficient domain and never dequantises, so
  /// the table number is carried and the table itself is not.</summary>
  internal readonly record struct JpegFrameComponent(int Id, int H, int V, int QuantTable);

  /// <summary>One component of a scan: which frame component it is, and which of the file's Huffman
  /// tables decode its DC and its AC coefficients. Without this the tables are unusable — a file may
  /// declare four of each class.</summary>
  internal readonly record struct JpegScanComponent(int Id, int DcTable, int AcTable);

  /// <summary>
  /// A Huffman table exactly as its DHT segment declares it: the number of codes of each length 1 to 16,
  /// and the values those codes map to, in canonical order. Kept in declaration form rather than as a
  /// decode tree because the walker re-encodes with the file's own tables and has to write the segment
  /// back out unchanged.
  /// </summary>
  internal sealed class JpegHuffmanTable
  {
    public JpegHuffmanTable(int tableClass, int id, IReadOnlyList<byte> counts, IReadOnlyList<byte> values)
    {
      Class = tableClass;
      Id = id;
      Counts = counts;
      Values = values;
    }

    /// <summary>0 for a DC table, 1 for an AC table.</summary>
    public int Class { get; }

    /// <summary>Which of the four tables of that class this is, as a scan header selects it.</summary>
    public int Id { get; }

    /// <summary>How many codes are of length 1, 2, ... 16. Always 16 entries.</summary>
    public IReadOnlyList<byte> Counts { get; }

    /// <summary>The values the codes map to, shortest code first.</summary>
    public IReadOnlyList<byte> Values { get; }
  }

  /// <summary>
  /// One scan: where its entropy-coded data lies as [Start, End), and which tables decode it. The
  /// two-field deconstruction is what the emitter's scan-coverage walk uses, which cares only about the
  /// range.
  /// </summary>
  internal sealed class JpegScan
  {
    public JpegScan(int start, int end, IReadOnlyList<JpegScanComponent> components)
    {
      Start = start;
      End = end;
      Components = components;
    }

    /// <summary>First byte of the entropy-coded data, which is the byte after the SOS segment.</summary>
    public int Start { get; }

    /// <summary>One past the last byte of the entropy-coded data.</summary>
    public int End { get; }

    /// <summary>The scan's components, in the order an interleaved scan visits them within each MCU.</summary>
    public IReadOnlyList<JpegScanComponent> Components { get; }

    public void Deconstruct(out int start, out int end)
    {
      start = Start;
      end = End;
    }
  }
}
