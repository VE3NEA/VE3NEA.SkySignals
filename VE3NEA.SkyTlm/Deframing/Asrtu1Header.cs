using System;
using System.Collections.Generic;
using System.Text;
using VE3NEA.SkyTlm.Imaging.Ssdv;

namespace VE3NEA.SkyTlm.Deframing
{
  /// <summary>
  /// AO-123 / ASRTU-1's 223-byte CCSDS frame: the 5-byte "TM short" primary header (gr-satellites
  /// <c>by02.TMPrimaryHeader</c>) and what each virtual channel carries in the 218 bytes after it. Shared by
  /// <see cref="SsdvSource.Ao123"/>, which takes the SSDV packets out of VC1, and by the telemetry panel,
  /// which names the header fields of every frame.
  /// <para>
  /// Header layout, MSB first: version (2 bits), spacecraft ID (10), virtual channel (3), OCF flag (1),
  /// master channel frame count (8), virtual channel frame count (8), first header pointer (8). There is no
  /// data field status word, so the data field starts at byte 5.
  /// </para>
  /// <para>
  /// Channel map, from the 2026-10-08/09 SatNOGS harvest (ssdv-research.md §6.2): VC0 and VC2 carry a KISS
  /// byte stream (<c>C0</c> = FEND, <c>DB DC</c> = escaped C0, <c>C0</c> also the idle fill) whose first
  /// FEND the first header pointer locates; VC1 carries SSDV packets and the ASCII "digital callsign"
  /// beacons, both with the pointer at 0xFF.
  /// </para>
  /// </summary>
  public static class Asrtu1Header
  {
    /// <summary>ASRTU-1's NORAD catalog number.</summary>
    public const int NoradId = 61781;

    /// <summary>The frame size of both downlinks, and the only one this layout applies to.</summary>
    public const int FrameLen = 223;

    /// <summary>Length of the TM short primary header, i.e. the offset of the data field.</summary>
    public const int HeaderLen = 5;

    /// <summary>The virtual channel that carries SSDV.</summary>
    public const int SsdvChannel = 1;

    /// <summary>
    /// The spacecraft IDs seen on AO-123's frequencies. 50 is ASRTU-1 itself, seen by every observer. 517
    /// was reported only by two Chinese stations on 2026-10-09, 14:07–14:13Z, sending the same SSDV images
    /// byte for byte — a second HIT transmitter whose identity is not settled, so it is shown by number.
    /// </summary>
    private const int ScidAsrtu1 = 50;

    private const int FhpNone = 0xFF;

    /// <summary>The transfer frame version, spacecraft ID and virtual channel, or null for a frame of the
    /// wrong size or version — which is then not one of AO-123's.</summary>
    public static (int Scid, int Vcid)? Parse(ReadOnlySpan<byte> frame)
    {
      if (frame.Length != FrameLen || frame[0] >> 6 != 0) return null;
      int word = frame[0] << 8 | frame[1];
      return (word >> 4 & 0x3FF, word >> 1 & 7);
    }

    /// <summary>
    /// The SSDV packet in a VC1 frame, or null when the frame is not one. The packet's own CRC is part of
    /// the gate: the CCSDS RS below has already validated the frame, so a VC1 data field failing the DSLWP
    /// CRC is not a damaged packet but a callsign beacon, and is not image data at all.
    /// </summary>
    public static byte[]? ExtractSsdv(ReadOnlySpan<byte> frame)
    {
      if (Parse(frame) is not { Vcid: SsdvChannel } || frame[4] != FhpNone) return null;

      var packet = frame[HeaderLen..];
      var v = SsdvVariant.Ao123;
      int at = v.CrcOffset;
      uint stored = (uint)(packet[at] << 24 | packet[at + 1] << 16 | packet[at + 2] << 8 | packet[at + 3]);
      return Crc32.Compute(packet[..at], v.CrcInit) == stored ? packet.ToArray() : null;
    }

    /// <summary>
    /// The header fields worth naming, as label/value pairs ready for the telemetry panel, followed by the
    /// SSDV packet header on an image frame and the text of a beacon. Empty for a frame that is not 223
    /// bytes of version-0 TM.
    /// </summary>
    public static IReadOnlyList<(string Name, string Value)> Describe(byte[] frame)
    {
      if (Parse(frame) is not var (scid, vcid)) return [];

      int fhp = frame[4];
      var fields = new List<(string, string)>
      {
        ("Spacecraft ID", scid == ScidAsrtu1 ? $"{scid} (ASRTU-1)" : $"{scid}"),
        ("Virtual channel", $"{vcid} ({ChannelContent(vcid)})"),
        ("Frame count", $"master {frame[2]}, channel {frame[3]}"),
        ("First header pointer", fhp == FhpNone ? "0xFF (none)" : $"{fhp}")
      };

      var packet = ExtractSsdv(frame);
      if (packet != null && SsdvPacket.TryParse(packet, SsdvVariant.Ao123, out var p))
      {
        fields.Add(("SSDV image", $"{p!.ImageId}, packet {p.PacketId}{(p.IsEoi ? " (last)" : "")}"));
        fields.Add(("Image size", $"{p.Width}x{p.Height}, quality {p.Quality}"));
        fields.Add(("MCU", p.McuIndex < 0 ? "none starts here" : $"index {p.McuIndex} of {p.McuCount}, offset {p.McuOffset}"));
      }
      else if (vcid == SsdvChannel && Beacon(frame) is string text)
        fields.Add(("Beacon", text));

      return fields;
    }

    private static string ChannelContent(int vcid) => vcid switch
    {
      0 or 2 => "KISS telemetry",
      SsdvChannel => "SSDV and beacons",
      _ => "unknown"
    };

    // the ASCII beacon text: printable bytes from the start of the data field up to the zero fill, or null
    // when the data field does not open with text
    private static string? Beacon(byte[] frame)
    {
      int end = HeaderLen;
      while (end < frame.Length && frame[end] >= 0x20 && frame[end] < 0x7F) end++;
      if (end - HeaderLen < 4) return null;
      return Encoding.ASCII.GetString(frame, HeaderLen, end - HeaderLen);
    }
  }
}
