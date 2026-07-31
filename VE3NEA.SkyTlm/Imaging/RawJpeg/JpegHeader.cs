using System;

namespace VE3NEA.SkyTlm.Imaging.RawJpeg
{
  /// <summary>
  /// Just enough JPEG marker walking to read a picture's size. The SSDV side knows its geometry from
  /// every packet header; the raw-JPEG family carries no geometry at all, so the only place to find it is
  /// the file's own frame header — which means it is unknown until enough of the file has arrived.
  /// </summary>
  internal static class JpegHeader
  {
    /// <summary>
    /// Read the size from the first start-of-frame segment. Returns false, with zero size, when the file
    /// does not reach one — normal early in a pass, and the caller reports 0 × 0 rather than guessing.
    /// </summary>
    public static bool ReadSize(ReadOnlySpan<byte> jpeg, out int width, out int height)
    {
      width = height = 0;
      if (jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8) return false;

      int at = 2;
      while (at + 3 < jpeg.Length)
      {
        if (jpeg[at] != 0xFF) return false;               // lost sync: not a marker boundary any more
        byte marker = jpeg[at + 1];
        if (marker == 0xD8 || marker == 0x01 || marker >= 0xD0 && marker <= 0xD7) { at += 2; continue; }
        if (marker == 0xD9 || marker == 0xDA) return false;   // scan reached without a frame header

        int len = jpeg[at + 2] << 8 | jpeg[at + 3];
        if (len < 2) return false;

        // SOF0-SOF15, which is every 0xC0-0xCF except the three that are not frame headers: DHT (0xC4),
        // JPGA (0xC8) and DAC (0xCC).
        if (marker >= 0xC0 && marker <= 0xCF && marker != 0xC4 && marker != 0xC8 && marker != 0xCC)
        {
          if (at + 9 >= jpeg.Length) return false;
          height = jpeg[at + 5] << 8 | jpeg[at + 6];
          width = jpeg[at + 7] << 8 | jpeg[at + 8];
          return width > 0 && height > 0;
        }

        at += 2 + len;
      }
      return false;
    }
  }
}
