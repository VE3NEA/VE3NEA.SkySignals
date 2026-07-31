using System;

namespace VE3NEA.SkyTlm.Imaging.RawJpeg
{
  /// <summary>
  /// What makes two fragments part of the same picture. The raw-JPEG protocols carry no image number —
  /// there is nothing like SSDV's image ID — so identity is assembled from whatever the frame does say,
  /// and a change in any component means the sender has moved on.
  /// </summary>
  /// <param name="Sender">Which satellite or session the fragment came from (Geoscan's <c>sat_num</c>).</param>
  /// <param name="Flavour">A sub-stream within one sender that must not be mixed with another —
  /// Geoscan's high-resolution flag, which selects a different camera and so a different picture.</param>
  /// <param name="Sequence">An explicit image counter where the protocol has one (Geoscan v2's
  /// <c>fnum</c>), or −1 where it does not.</param>
  public readonly record struct RawJpegImageKey(int Sender, int Flavour, int Sequence);


  /// <summary>
  /// One chunk of a JPEG file, with the absolute offset it belongs at. Contrast
  /// <see cref="Ssdv.SsdvPacket"/>, which carries MCU structure: here there is none, and a fragment is
  /// only meaningful at exactly the right byte position.
  /// </summary>
  /// <param name="Key">Which picture this belongs to.</param>
  /// <param name="Offset">Byte offset in the <b>satellite's</b> address space, not the file's. The
  /// fragment carrying the SOI marker is what relates the two.</param>
  /// <param name="Data">The file bytes.</param>
  /// <param name="IsStart">The protocol explicitly said "new image starts here" (Geoscan's
  /// <c>mtype 0x0901</c>), which is stronger evidence than a key change and resets the offset base.</param>
  /// <param name="Name">File name, where the protocol announces one — USP's <c>FILETRANSFER_INIT</c>.
  /// Null when unknown, which is always the case for Geoscan and is also normal for USP until the
  /// announcement is heard.</param>
  /// <param name="TotalSize">Final file length in bytes where the protocol states it — USP's
  /// <c>FILETRANSFER_FILESIZE</c> — or 0 when unknown. Worth carrying because it turns "complete" from
  /// an inference into a fact.</param>
  /// <param name="FileRelative">
  /// <see cref="Offset"/> is already an offset into the file, so byte 0 needs no working out. True for
  /// Geoscan v2 and for USP; false for Geoscan v1, whose offsets are positions in the satellite's own
  /// address space and mean nothing until an SOI marker relates the two.
  /// <para>
  /// This is not a detail: rebasing a file-relative stream would shift the picture. SatsDecoder draws
  /// the same distinction by writing <c>off - base_offset</c> in its v1 path and a bare <c>off</c> in
  /// its v2 and USP paths, and IZ7EVR's 2026-07-30 reception shows it plainly — the receiver reports
  /// <c>Base offset: 0</c> while the frames beside it carry offset 4266.
  /// </para>
  /// </param>
  public readonly record struct RawJpegFragment(RawJpegImageKey Key, int Offset, byte[] Data, bool IsStart,
                                                string? Name = null, int TotalSize = 0,
                                                bool FileRelative = false)
  {
    /// <summary>
    /// Carries no file bytes: an announcement of a name or a size rather than a piece of the picture.
    /// USP sends both as their own messages, so a fragment that only tells the assembler something is
    /// normal and is not a failure to parse.
    /// </summary>
    public bool IsAnnouncement => Data.Length == 0;

    /// <summary>Start-of-image marker: this fragment holds the first bytes of the JPEG file itself.</summary>
    public bool HasSoi => Data.Length >= 2 && Data[0] == 0xFF && Data[1] == 0xD8;

    /// <summary>End-of-image marker somewhere in the fragment: the file finishes here.</summary>
    public bool HasEoi
    {
      get
      {
        for (int i = 0; i + 1 < Data.Length; i++)
          if (Data[i] == 0xFF && Data[i + 1] == 0xD9) return true;
        return false;
      }
    }
  }
}
