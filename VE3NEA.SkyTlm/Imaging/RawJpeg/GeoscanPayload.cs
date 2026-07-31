using System;
using System.Buffers.Binary;
using VE3NEA.SkyTlm.Core;

namespace VE3NEA.SkyTlm.Imaging.RawJpeg
{
  /// <summary>
  /// The Geoscan application layer: two frame layouts sharing one downlink with AX.25 beacons and
  /// telemetry. Ported from SatsDecoder's <c>geoscan.py</c>, which is the reference implementation for
  /// this family the way <c>ssdv.c</c> is for SSDV.
  /// <para>
  /// Both layouts are little-endian and both are zero-padded by the modem to a fixed frame size, so
  /// <c>dlen</c> is a payload length rather than a frame length — measured on 226 off-air and SatNOGS
  /// frames (2026-07-30), every short frame's tail past <c>dlen</c> was all zero.
  /// </para>
  /// </summary>
  public static class GeoscanPayload
  {
    /// <summary>Image data begins here; also resets the offset base.</summary>
    private const int CmdImageStart = 0x0901;

    private static readonly int[] ImageFrameCommands = [0x0905, 0x0920];
    private static readonly int[] HighResCommands = [0x9820, 0x411C];
    private static readonly int[] SpecialCommands = [0x4150];

    /// <summary>v2's fixed marker, <c>"1okо"</c> read little-endian, which is how SatsDecoder recognises
    /// the layout — v2 keys off this rather than off <c>mtype</c>.</summary>
    private const uint MarkerV2 = 0x6F6B6F31;

    /// <summary>Value <c>reserved0</c> takes on every real v1 frame; SatsDecoder uses it as the v1
    /// fallback gate after a v2 parse fails.</summary>
    private const byte V1Reserved0 = 0x98;

    /// <summary>The two platforms that only ever speak v1, so v2 is not attempted for them.</summary>
    private const int SatGeoscan = 0x01, SatStratosat = 0x02;

    /// <summary>Platform IDs that are image senders at all — anything else is not ours.</summary>
    private static readonly int[] KnownSenders =
      [0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F, 0x10,
       0x11, 0x12];

    /// <summary>
    /// Reconstruct the image fragment this frame carries, or null when it carries none. The v2 layout is
    /// tried first and v1 is the fallback, matching SatsDecoder — except for the two platforms above,
    /// which go straight to v1.
    /// </summary>
    public static RawJpegFragment? TryParse(Frame frame)
    {
      if (frame.Framing != Framing.GEOSCAN) return null;

      var bytes = frame.Bytes;
      if (bytes.Length < 8) return null;

      int sender = bytes[0];
      if (Array.IndexOf(KnownSenders, sender) < 0) return null;

      if (sender != SatGeoscan && sender != SatStratosat)
      {
        var v2 = ParseV2(bytes, sender);
        if (v2 != null) return v2;
        if (bytes[1] != V1Reserved0) return null;
      }

      return ParseV1(bytes, sender);
    }


    // ----------------------------------------------------------------------------------------------------
    //                                          the two layouts
    // ----------------------------------------------------------------------------------------------------
    /// <summary>
    /// v1: <c>sat_num, reserved0, dlen, mtype:u16, offset:u16, subsystem_num</c>, then <c>dlen−6</c>
    /// payload bytes. The absolute offset is 24 bits — <c>subsystem_num</c> holds the high 8.
    /// </summary>
    private static RawJpegFragment? ParseV1(byte[] bytes, int sender)
    {
      const int headerLen = 8;
      int dlen = bytes[2];
      int dataLen = dlen - 6;
      if (dataLen <= 0 || headerLen + dataLen > bytes.Length) return null;

      int mtype = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(3));
      bool isStart = mtype == CmdImageStart;
      bool isHighRes = Array.IndexOf(HighResCommands, mtype) >= 0;
      if (!isStart && !isHighRes
          && Array.IndexOf(ImageFrameCommands, mtype) < 0
          && Array.IndexOf(SpecialCommands, mtype) < 0)
        return null;

      int offset = bytes[7] << 16 | BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(5));
      var key = new RawJpegImageKey(sender, isHighRes ? 1 : 0, Sequence: -1);
      return new RawJpegFragment(key, offset, bytes[headerLen..(headerLen + dataLen)], isStart);
    }

    /// <summary>
    /// v2 (Lobachevsky): <c>sat_num, reserved0, dlen, reserved1:2, marker:u32, offset:u32, fnum:u16</c>,
    /// then 54 payload bytes. There is no <c>mtype</c> — the marker is what says "this is an image
    /// frame", and <c>fnum</c> is an explicit picture counter, so v2 needs no start command.
    /// </summary>
    private static RawJpegFragment? ParseV2(byte[] bytes, int sender)
    {
      const int headerLen = 15, dataLen = 54;
      if (bytes.Length < headerLen + dataLen) return null;
      if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(5)) != MarkerV2) return null;

      long offset = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(9));
      if (offset > int.MaxValue) return null;
      int frameNum = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(13));

      var key = new RawJpegImageKey(sender, Flavour: 0, Sequence: frameNum);
      return new RawJpegFragment(key, (int)offset, bytes[headerLen..(headerLen + dataLen)], IsStart: false);
    }
  }
}
