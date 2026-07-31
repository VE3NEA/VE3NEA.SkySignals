using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;
using VE3NEA.SkyTlm.Core;
using VE3NEA.SkyTlm.Deframing;

namespace VE3NEA.SkyTlm.Imaging.RawJpeg
{
  /// <summary>
  /// The USP application layer, as far as images need it. USP is a general file-transfer channel that
  /// happens to move JPEGs — the Sputnix birds (Luca / RS90S, 239Alferov / RS61S, HyperView-1G / RS66S)
  /// send pictures down the ordinary telemetry downlink rather than a dedicated imaging one, which is
  /// why none of them advertises an imaging transmitter.
  /// <para>
  /// Frames arrive as AX.25 UI (PID <c>0xF0</c>) and their info field is a run of <c>Data</c> messages,
  /// each an 8-byte header and a payload. One frame can therefore announce a file and carry a piece of
  /// it, which is why extraction yields a list. Ported from SatsDecoder's <c>usp.py</c>.
  /// </para>
  /// <para>
  /// <b>Unvalidated.</b> Every USP frame captured so far is telemetry — no <c>FILETRANSFER_*</c> message
  /// has ever been seen off air — so this is built from the reference structs and tested against
  /// synthesised frames. Treat the field widths as read from SatsDecoder, not as confirmed on air.
  /// </para>
  /// </summary>
  public static class UspFileTransfer
  {
    /// <summary>Announces a transfer: mode, session, block size, offset, and the file name.</summary>
    private const int MessageInit = 0x0C20;

    /// <summary>States the final file length, which USP sends separately from the name.</summary>
    private const int MessageFileSize = 0x0C2B;

    /// <summary>Carries file bytes at an absolute offset.</summary>
    private const int MessageData = 0x0C24;

    /// <summary>Bytes of <c>message</c>, <c>sender</c>, <c>receiver</c> and <c>size</c>, all u16 LE.</summary>
    private const int DataHeaderLen = 8;

    /// <summary>Fixed part of a DATA payload: session (u8) then offset (u32 LE).</summary>
    private const int DataFixedLen = 5;

    /// <summary>Fixed part of an INIT payload before the name: mode, session, block size, offset, reserved.</summary>
    private const int InitFixedLen = 10;

    private const byte UiControl = 0x03, NoLayer3Pid = 0xF0;

    /// <summary>
    /// Pull every file-transfer fragment out of one frame. Returns an empty list when the frame is not a
    /// USP file transfer, which is the overwhelmingly common case — telemetry shares this downlink.
    /// </summary>
    public static IReadOnlyList<RawJpegFragment> Extract(Frame frame)
    {
      // Only the framing UspDeframer emits. 239Alferov's transmitter is described as "AX.25/USP", so if
      // its frames ever resolve to plain AX.25 instead, this gate — and the factory row beside it — are
      // what would need widening. Nothing in the corpus settles which way it resolves.
      if (frame.Framing != Framing.USP) return [];

      var info = InfoField(frame.Bytes);
      if (info.IsEmpty) return [];

      List<RawJpegFragment>? fragments = null;
      int at = 0;
      while (at + DataHeaderLen <= info.Length)
      {
        int message = BinaryPrimitives.ReadUInt16LittleEndian(info[at..]);
        int size = BinaryPrimitives.ReadUInt16LittleEndian(info[(at + 6)..]);
        int payloadAt = at + DataHeaderLen;
        if (size < 0 || payloadAt + size > info.Length) break;   // truncated: trust nothing past here

        var fragment = Parse(message, info.Slice(payloadAt, size));
        if (fragment != null) (fragments ??= []).Add(fragment.Value);

        at = payloadAt + size;
      }
      return (IReadOnlyList<RawJpegFragment>?)fragments ?? [];
    }

    /// <summary>
    /// The AX.25 UI info field: past the address field, the control byte and the PID. Anything that is
    /// not an unnumbered-information frame with no layer 3 is not USP.
    /// </summary>
    private static ReadOnlySpan<byte> InfoField(byte[] bytes)
    {
      int addressLen = Ax25Address.AddressFieldLength(bytes);
      if (addressLen == 0 || addressLen + 2 >= bytes.Length) return default;
      if (bytes[addressLen] != UiControl || bytes[addressLen + 1] != NoLayer3Pid) return default;
      return bytes.AsSpan(addressLen + 2);
    }

    private static RawJpegFragment? Parse(int message, ReadOnlySpan<byte> payload)
    {
      switch (message)
      {
        case MessageData:
        {
          if (payload.Length <= DataFixedLen) return null;
          int session = payload[0];
          long offset = BinaryPrimitives.ReadUInt32LittleEndian(payload[1..]);
          if (offset > int.MaxValue) return null;
          // USP offsets are file offsets — SatsDecoder writes them straight through, and the SOI is
          // expected at offset 0 rather than at a base to be worked out.
          return new RawJpegFragment(Key(session), (int)offset, payload[DataFixedLen..].ToArray(),
                                     IsStart: false, FileRelative: true);
        }

        case MessageInit:
        {
          if (payload.Length < InitFixedLen) return null;
          int session = payload[1];
          // the name is NUL-padded inside the message rather than length-prefixed
          var name = Encoding.UTF8.GetString(payload[InitFixedLen..]).Split('\0')[0].Trim();
          // INIT is what says a transfer begins, so it resets the offset base the way Geoscan's
          // start command does
          return new RawJpegFragment(Key(session), 0, [], IsStart: true, Name: name);
        }

        case MessageFileSize:
        {
          if (payload.Length < 4) return null;
          long size = BinaryPrimitives.ReadUInt32LittleEndian(payload);
          if (size is <= 0 or > int.MaxValue) return null;
          // FILESIZE names no session — it applies to the transfer in progress, so it carries the
          // sentinel key and the assembler attaches it to whatever is open.
          return new RawJpegFragment(AnySession, 0, [], IsStart: false, TotalSize: (int)size);
        }

        default:
          return null;   // telemetry, of which there is a great deal
      }
    }

    /// <summary>USP has one file per session, so the session ID is the image identity.</summary>
    private static RawJpegImageKey Key(int session) => new(session, Flavour: 0, Sequence: -1);

    /// <summary>
    /// Matches whatever image is open. USP's FILESIZE message carries no session ID, so it cannot name
    /// its own transfer; the assembler treats this key as "the current one" rather than as a new image.
    /// </summary>
    public static readonly RawJpegImageKey AnySession = new(-1, -1, -1);
  }
}
