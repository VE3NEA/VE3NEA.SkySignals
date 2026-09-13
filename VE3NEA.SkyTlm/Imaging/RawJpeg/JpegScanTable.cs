using System;
using System.Collections.Generic;

namespace VE3NEA.SkyTlm.Imaging.RawJpeg
{
  /// <summary>
  /// The scan structure of a JPEG file: whether it is progressive, and where each scan's entropy-coded
  /// data begins and ends. <see cref="JpegHeader"/> stops at the frame header because a size is all a
  /// complete file needs; a partly received <b>progressive</b> file needs more.
  /// <para>
  /// A baseline file is one scan and degrades gracefully — a hole costs a colour shift below it and
  /// nothing else. A progressive file is a stack of scans that refine one another, and a scan that is
  /// mostly missing does not degrade the picture, it destroys it: the 2026-09-12/13 Geoscan capture has
  /// an 8-scan 800x600 photograph whose whole buffer renders <b>pure black</b> and whose first four
  /// scans render a clean picture. Choosing where to stop needs the scan boundaries, which is this.
  /// </para>
  /// </summary>
  internal sealed class JpegScanTable
  {
    private JpegScanTable(bool progressive, IReadOnlyList<(int Start, int End)> scans)
    {
      Progressive = progressive;
      Scans = scans;
    }

    /// <summary>The frame header is one of the progressive SOFs — 0xC2 and the three variants of it —
    /// so the scans below refine one another rather than each covering their own part of the picture.</summary>
    public bool Progressive { get; }

    /// <summary>Entropy-coded data of each scan as [Start, End), in file order. The ranges exclude the
    /// SOS segment itself, so a scan that is dropped takes its data and leaves its header.</summary>
    public IReadOnlyList<(int Start, int End)> Scans { get; }

    /// <summary>
    /// Walk the markers of a partly received file. Returns null when it does not even open with an SOI,
    /// and stops early — reporting what it found so far — where the walk loses sync, which is what a
    /// hole in the header segments does. Both are normal off air and neither is an error.
    /// </summary>
    public static JpegScanTable? Read(ReadOnlySpan<byte> jpeg)
    {
      if (jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8) return null;

      bool progressive = false;
      var scans = new List<(int, int)>();

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

        // SOF0-SOF15, less the three 0xC-something markers that are not frame headers: DHT, JPGA, DAC.
        // Of those that are, the progressive ones are 0xC2 and the differential/arithmetic variants of
        // it, which is the low two bits reading 2.
        if (marker >= 0xC0 && marker <= 0xCF && marker != 0xC4 && marker != 0xC8 && marker != 0xCC)
          progressive = (marker & 0x03) == 0x02;

        if (marker == 0xDA)
        {
          int start = at + 2 + len;
          if (start >= jpeg.Length) break;
          int end = EntropyEnd(jpeg, start);
          scans.Add((start, end));
          at = end;
          continue;
        }

        at += 2 + len;
      }

      return new JpegScanTable(progressive, scans);
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
}
