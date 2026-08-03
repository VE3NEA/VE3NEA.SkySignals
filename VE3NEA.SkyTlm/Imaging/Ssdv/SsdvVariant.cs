using System;
using VE3NEA.SkyTlm.Deframing;

namespace VE3NEA.SkyTlm.Imaging.Ssdv
{
  /// <summary>
  /// One flavour of the SSDV packet format. "SSDV" is a family, not a format: every operator trims the
  /// packet to fit their link, so the parser is parameterised rather than hard-coded to fsphil's 256
  /// bytes. The four degrees of freedom are the packet length, whether the leading sync/type pair and
  /// the callsign field are present, and which integrity layers ride along.
  /// <para>
  /// A variant describes the <b>canonical</b> packet — the bytes fsphil's <c>ssdv -d</c> would consume.
  /// Per-satellite trimming of the on-air frame (HADES-SA drops five constant leading bytes, JY1SAT
  /// carries its packet inside a larger telemetry frame) is the caller's job, not the parser's; that
  /// split is what keeps this file free of bird-specific branches.
  /// </para>
  /// </summary>
  /// <param name="PacketLen">Total canonical packet length in bytes.</param>
  /// <param name="TypeByte">Value of the packet-type byte, <c>0x66 + type</c> (ignored when
  /// <paramref name="HasSyncType"/> is false).</param>
  /// <param name="HasSyncType">Packet opens with sync <c>0x55</c> and the type byte.</param>
  /// <param name="HasCallsign">A 4-byte base-40 callsign field follows the type byte.</param>
  /// <param name="HasCrc">A 4-byte big-endian CRC-32 follows the payload.</param>
  /// <param name="HasRs">32 RS(255,223) parity bytes close the packet.</param>
  public sealed record SsdvVariant(int PacketLen, byte TypeByte, bool HasSyncType, bool HasCallsign,
                                   bool HasCrc, bool HasRs)
  {
    /// <summary>SSDV sync byte; more sync bytes may precede it on air.</summary>
    public const byte SyncByte = 0x55;

    /// <summary>
    /// What this variant is called. It is written into archives beside the packets stored under it and
    /// read back through <see cref="ByName"/>, so it is a persisted identifier: rename one and the files
    /// already on disk stop being readable.
    /// </summary>
    public string Name { get; init; } = "";

    /// <summary>Standard packet, type 0x66: sync + type + callsign + 9 header bytes, CRC and RS. 205 B payload.</summary>
    public static readonly SsdvVariant Standard256 = new(256, 0x66, true, true, true, true) { Name = "Standard256" };

    /// <summary>Standard packet without FEC, type 0x67 — the 32 RS bytes become payload. 237 B payload.</summary>
    public static readonly SsdvVariant NoFec256 = new(256, 0x67, true, true, true, false) { Name = "NoFec256" };

    /// <summary>
    /// HADES-SA, which <b>is</b> <see cref="Standard256"/> once the five constant bytes AMSAT-EA strips
    /// (<c>55 66 BF 35 FB</c>) are put back. Named separately because the dispatch table reads by
    /// satellite, and because the callsign field here is not a callsign: AMSAT-EA overloaded it to carry
    /// the GENESIS sync 0xBF35, the size byte 0xFB and the type/address byte 0xA3. It is constant, so it
    /// still keys images correctly; it just does not spell anything.
    /// </summary>
    public static readonly SsdvVariant HadesSa251 = Standard256;

    /// <summary>JY1SAT / JO-97, type 0x68: no callsign, no CRC and no RS (the AO-40 FEC layer below
    /// provides both). 189 B payload.</summary>
    public static readonly SsdvVariant Jy1Sat200 = new(200, 0x68, true, false, false, false) { Name = "Jy1Sat200" };

    /// <summary>
    /// SilverSat, type 0x67 shortened to 195 bytes to fit its IL2P payload block, which supplies the FEC
    /// the type byte says is absent. 176 B payload. The bird has re-entered, so this exists for the one
    /// packet we have of it — see <c>ssdv-research.md</c> §1.3 — and because it is the proof that the
    /// CRC follows the shortened length rather than sitting at a fixed offset.
    /// </summary>
    public static readonly SsdvVariant SilverSat195 = new(195, 0x67, true, true, true, false) { Name = "SilverSat195" };

    /// <summary>DSLWP / LO-94: no sync, no type byte, no callsign, no CRC, no RS. 209 B payload.</summary>
    public static readonly SsdvVariant Dslwp218 = new(218, 0x00, false, false, false, false) { Name = "Dslwp218" };

    /// <summary>
    /// The variant of that name, or null when it is not one this build knows. Null is the normal answer
    /// for an archive written by a later version, and is a reason to skip that file rather than to fail.
    /// </summary>
    public static SsdvVariant? ByName(string? name) => name switch
    {
      "Standard256" => Standard256,
      "NoFec256" => NoFec256,
      "Jy1Sat200" => Jy1Sat200,
      "SilverSat195" => SilverSat195,
      "Dslwp218" => Dslwp218,
      _ => null
    };

    /// <summary>Header length in bytes: the 9 fields every variant carries (image ID, packet ID,
    /// width, height, flags, MCU offset, MCU index) plus the optional sync/type pair and callsign.</summary>
    public int HeaderLen => 9 + (HasSyncType ? 2 : 0) + (HasCallsign ? 4 : 0);

    /// <summary>Entropy-coded scan bytes per packet — whatever the header and the integrity layers leave.</summary>
    public int PayloadLen => PacketLen - HeaderLen - (HasCrc ? 4 : 0) - (HasRs ? RsCodeword.ParityBytes : 0);

    /// <summary>Offset of the CRC-32, i.e. one past the last payload byte (also the end of the CRC's own span).</summary>
    public int CrcOffset => HeaderLen + PayloadLen;

    /// <summary>Offset of the 4-byte callsign field, or −1 when the variant carries none.</summary>
    public int CallsignOffset => HasCallsign ? 2 : -1;

    /// <summary>Offset of the image-ID byte, where the 9 common header fields begin.</summary>
    public int ImageIdOffset => (HasSyncType ? 2 : 0) + (HasCallsign ? 4 : 0);

    /// <summary>Leading virtual zero pad symbols for the shortened RS codeword over <c>bytes[1..PacketLen]</c>
    /// (0 for a full 256-byte packet).</summary>
    public int RsPad => RsCodeword.Len - (PacketLen - 1);
  }
}
