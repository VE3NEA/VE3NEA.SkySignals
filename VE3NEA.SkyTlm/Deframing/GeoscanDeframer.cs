using System;
using System.Collections.Generic;
using VE3NEA.SkyTlm.Core;

namespace VE3NEA.SkyTlm.Deframing
{
  /// <summary>Tunables for <see cref="GeoscanDeframer"/>.</summary>
  public sealed class GeoscanOptions
  {
    /// <summary>Max syncword bit errors to accept (default 4 of 32, the gr-satellites default).</summary>
    public int SyncThreshold { get; init; } = 4;

    /// <summary>
    /// On-air frame length in bytes after the syncword, <b>including</b> the trailing 2-byte CRC.
    /// <para>
    /// The length is a per-satellite constant, not a protocol constant, and gr-satellites' satyaml is the
    /// source of truth for it. <b>74 is the default because it is the only size still flying</b>: every live
    /// bird uses it (Geoscan-1…6, 239Alferov, Colibri-S, InnoSat3/16, RTU MIREA1, Vizard-ion), while 66
    /// appears only on Geoscan-Edelveis and the StratoSat TK-1 family, all of which have re-entered. The
    /// older size is still reachable — satyaml states 66 for those birds, so
    /// <see cref="SignalParams.FrameSize"/> carries it through <see cref="DeframerFactory"/> and archived
    /// recordings of them keep decoding — it is simply not what an uncatalogued bird is guessed to be.
    /// </para>
    /// </summary>
    public int FrameSize { get; init; } = 74;
  }

  /// <summary>
  /// GEOSCAN custom-frame deframer — a C# port of the gr-satellites <c>geoscan_deframer</c> chain used by the
  /// Geoscan/Sputnix CC1125-based fleet (Geoscan-1…5, StratoSat, Lobachevsky, …) at GFSK 9k6. Consumes the
  /// demodulator's soft symbols and runs <b>sync (0x930B51DE) → fixed-length capture
  /// (<see cref="SyncToPacket"/>) → PN9 de-whitening (<see cref="Pn9Scrambler"/>) → CC11xx CRC-16
  /// (<see cref="Crc16Cc11xx"/>)</b>, emitting the 64-byte payload. FEC-free: the CRC is the only integrity
  /// gate, so a frame either checks out or is dropped. Every framing parameter is corroborated by three
  /// independent implementations — gr-satellites <c>geoscan_deframer.py</c>, SatDump
  /// <c>module_geoscan_decoder.cpp</c> and SatsDecoder <c>systems/geoscan.py</c>.
  /// <para>
  /// Decoded bytes only: the payload's own application layer (the raw-JPEG image fragments carrying an
  /// absolute byte offset, and the beacon telemetry) is parsed elsewhere.
  /// </para>
  /// </summary>
  public sealed class GeoscanDeframer : IDeframer
  {
    // 0x930B51DE = 10010011000010110101000111011110, 32-bit MSB-first.
    private const ulong SyncWord = 0x930B51DE;
    private const int SyncLen = 32;

    private readonly GeoscanOptions opt;

    public GeoscanDeframer(GeoscanOptions? options = null)
    {
      opt = options ?? new GeoscanOptions();
      if (opt.FrameSize < 3) throw new ArgumentException("GEOSCAN frame size must leave room for the CRC", nameof(options));
    }

    /// <summary>Sync + the whole fixed-length frame (payload + CRC).</summary>
    public int MaxFrameBits => SyncLen + opt.FrameSize * 8;

    public IEnumerable<Frame> Deframe(SoftSymbols syms, SignalParams p)
    {
      foreach (var (raw, syncBit, _) in SyncToPacket.Extract(syms.Soft, SyncWord, SyncLen, opt.FrameSize, opt.SyncThreshold))
      {
        // the CRC covers the whole frame, so a capture cut short by the end of the burst can't be gated
        // (unlike the length-cropped HADES types, GEOSCAN frames are fixed-size).
        if (raw.Length < opt.FrameSize) continue;

        // the whitening runs over the entire frame, CRC bytes included, and the CRC is checked on the
        // de-whitened bytes (gr-satellites orders the chain scrambler → crc).
        Pn9Scrambler.XorSequenceInPlace(raw);

        int bodyLen = opt.FrameSize - 2;
        ushort rx = (ushort)((raw[bodyLen] << 8) | raw[bodyLen + 1]);   // big-endian on air
        if (Crc16Cc11xx.Compute(raw.AsSpan(0, bodyLen)) != rx) continue;

        yield return new Frame
        {
          Bytes = raw[..bodyLen],
          CrcValid = true,
          Fcs = rx,
          Framing = Framing.GEOSCAN,
          SoftBitOffset = syncBit,
          SoftBitEnd = syncBit + SyncLen + opt.FrameSize * 8
        };
      }
    }
  }
}
