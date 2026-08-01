using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace VE3NEA.SkyTlm.Deframing
{
  /// <summary>
  /// Light Geoscan header parsing for display: name the sending satellite and the frame type of a native
  /// Geoscan frame, and where an image frame's bytes belong in the picture. The display counterpart of
  /// <see cref="Imaging.RawJpeg.GeoscanPayload"/>, which reads the same two headers but keeps only the
  /// frames that carry image data — most of what this downlink sends does not, and until now those frames
  /// showed as hex alone.
  /// <para>
  /// The other flavor sharing this downlink is an AX.25 UI beacon, whose first byte is a shifted callsign
  /// character rather than a platform ID; <see cref="Describe"/> answers with nothing for those, leaving
  /// them to <see cref="Ax25Address"/> and the telemetry definitions. This is presentation only — the
  /// deframer's and the assembler's correctness do not depend on it.
  /// </para>
  /// </summary>
  public static class GeoscanHeader
  {
    /// <summary>v2's fixed marker, <c>"1okо"</c> read little-endian — see
    /// <see cref="Imaging.RawJpeg.GeoscanPayload"/>, which gates the layout on the same value.</summary>
    private const uint MarkerV2 = 0x6F6B6F31;

    /// <summary>The two platforms that only ever speak v1, so v2 is not attempted for them.</summary>
    private const int SatGeoscan = 0x01, SatStratosat = 0x02;

    private const int HeaderLenV1 = 8, HeaderLenV2 = 15, DataLenV2 = 54;

    /// <summary>
    /// The header fields worth naming, as label/value pairs ready for the telemetry panel, or an empty list
    /// when the frame is not a native Geoscan frame (an AX.25 beacon, or a platform ID we do not know).
    /// </summary>
    public static IReadOnlyList<(string Name, string Value)> Describe(byte[] frame)
    {
      if (frame.Length < HeaderLenV1) return [];

      int sender = frame[0];
      string? satellite = SenderName(sender);
      if (satellite == null) return [];

      var fields = new List<(string, string)> { ("Satellite", $"{satellite} (sat_num 0x{sender:X2})") };

      if (sender != SatGeoscan && sender != SatStratosat && IsV2(frame)) DescribeV2(frame, fields);
      else DescribeV1(frame, fields);

      return fields;
    }

    /// <summary>SatsDecoder's platform table, in full: every ID the fleet sends, not only the birds that
    /// carry cameras (which is what <c>RawJpegAssembler.SenderName</c> answers). Null for anything else,
    /// which is how a beacon or a noise frame is told apart from a native one.</summary>
    public static string? SenderName(int sender) => sender switch
    {
      0x01 => "Geoscan-Edelveis",
      0x02 => "StratoSat-TK1",
      0x03 => "Horizon",
      0x04 => "RTU MIREA-1",
      0x05 => "TUSUR-GO",
      0x06 => "Colibri-S",
      0x07 => "Vizard-ion",
      0x09 => "239Alferov",
      0x0A => "InnoSat-3",
      0x0B => "Geoscan-1",
      0x0C => "Geoscan-2",
      0x0D => "Geoscan-3",
      0x0E => "Geoscan-4",
      0x0F => "Geoscan-5",
      0x10 => "Geoscan-6",
      0x11 => "InnoSat-16",
      0x12 => "Lobachevsky",
      _ => null
    };




    // ----------------------------------------------------------------------------------------------------
    //                                          the two layouts
    // ----------------------------------------------------------------------------------------------------
    private static bool IsV2(byte[] frame) =>
      frame.Length >= HeaderLenV2 + DataLenV2 &&
      BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(5)) == MarkerV2;

    /// <summary>
    /// v1: <c>sat_num, reserved0, dlen, mtype:u16, offset:u16, subsystem_num</c>, then <c>dlen−6</c>
    /// payload bytes. Only an image <c>mtype</c> makes the offset an offset — on every other message type
    /// those two bytes mean something we have no table for, so they are not shown as one.
    /// </summary>
    private static void DescribeV1(byte[] frame, List<(string, string)> fields)
    {
      int dataLen = frame[2] - 6;
      int mtype = BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(3));
      string? command = ImageCommand(mtype);

      fields.Add(("Frame type", $"{command ?? "not an image"} (v1, mtype 0x{mtype:X4})"));
      if (dataLen > 0) fields.Add(("Data length", $"{dataLen} bytes"));
      if (command == null) return;

      int offset = frame[7] << 16 | BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(5));
      fields.Add(("Image offset", offset.ToString()));
      AddMarkers(frame, HeaderLenV1, dataLen, fields);
    }

    /// <summary>
    /// v2: <c>sat_num, reserved0, dlen, reserved1:2, marker:u32, offset:u32, fnum:u16</c>, then 54 payload
    /// bytes. There is no <c>mtype</c> — the marker is what says "this is an image frame" — and the offset
    /// is already a position in the file rather than in the satellite's address space.
    /// </summary>
    private static void DescribeV2(byte[] frame, List<(string, string)> fields)
    {
      uint offset = BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(9));
      int frameNum = BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(13));

      fields.Add(("Frame type", "image data (v2)"));
      fields.Add(("Picture", $"#{frameNum}"));
      fields.Add(("Data length", $"{DataLenV2} bytes"));
      fields.Add(("Image offset", $"{offset} (in file)"));
      AddMarkers(frame, HeaderLenV2, DataLenV2, fields);
    }

    /// <summary>The image commands, from SatsDecoder's <c>GeoscanImageReceiver</c>; null when the message
    /// type carries no picture.</summary>
    private static string? ImageCommand(int mtype) => mtype switch
    {
      0x0901 => "image start",
      0x0905 or 0x0920 => "image data",
      0x9820 or 0x411C => "image data, hi-res",
      0x4150 => "image data, special",
      _ => null
    };

    /// <summary>Names the JPEG markers the chunk happens to contain: SOI is what relates a v1 offset to a
    /// position in the file, EOI is the last chunk of the picture. Neither is a header field — both are
    /// read out of the payload, the way the assembler reads them.</summary>
    private static void AddMarkers(byte[] frame, int start, int dataLen, List<(string, string)> fields)
    {
      int end = Math.Min(start + dataLen, frame.Length);
      if (end - start < 2) return;

      bool soi = frame[start] == 0xFF && frame[start + 1] == 0xD8;
      bool eoi = false;
      for (int i = start; i + 1 < end && !eoi; i++) eoi = frame[i] == 0xFF && frame[i + 1] == 0xD9;

      if (soi && eoi) fields.Add(("Markers", "SOI (start of image), EOI (end of image)"));
      else if (soi) fields.Add(("Markers", "SOI (start of image)"));
      else if (eoi) fields.Add(("Markers", "EOI (end of image)"));
    }
  }
}
