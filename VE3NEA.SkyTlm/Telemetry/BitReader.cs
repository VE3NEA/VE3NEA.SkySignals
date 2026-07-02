using System;

namespace VE3NEA.SkyTlm.Telemetry
{
  /// <summary>
  /// MSB-first bit cursor over a frame's bytes — the correctness-critical primitive of the telemetry parser.
  /// Bits are consumed most-significant-first, so an N-bit read that is <i>not</i> byte-aligned
  /// (HADES's 12-bit fields) crosses byte boundaries naturally. Byte-multiple-width integers honor a byte
  /// order: read big-endian (the MSB-first assembly), then — for little-endian — reverse the byte order. This
  /// single rule serves both bit-packed fields (12-bit, never swapped) and byte-aligned le/be integers.
  /// </summary>
  public sealed class BitReader
  {
    private readonly byte[] data;

    public BitReader(byte[] data) => this.data = data;

    /// <summary>Current cursor position, in bits from the start of <c>data</c>.</summary>
    public int BitPos { get; private set; }

    /// <summary>Total bits available.</summary>
    public int BitLength => data.Length * 8;

    /// <summary>Seek to an absolute bit position.</summary>
    public void SeekBits(int bitPos)
    {
      if (bitPos < 0 || bitPos > BitLength) throw new ArgumentOutOfRangeException(nameof(bitPos));
      BitPos = bitPos;
    }

    /// <summary>Seek to an absolute byte position.</summary>
    public void SeekBytes(int bytePos) => SeekBits(bytePos * 8);

    /// <summary>Advance the cursor by a number of bytes.</summary>
    public void SkipBytes(int bytes) => SeekBits(BitPos + bytes * 8);

    /// <summary>
    /// Read <paramref name="bits"/> (1…64) MSB-first into a big-endian-assembled value. This is the raw,
    /// unswapped read used directly for bit-packed (non-byte-aligned) fields.
    /// </summary>
    public ulong ReadBitsBe(int bits)
    {
      if (bits < 1 || bits > 64) throw new ArgumentOutOfRangeException(nameof(bits), bits, "1..64");
      if (BitPos + bits > BitLength)
        throw new InvalidOperationException($"read of {bits} bits at {BitPos} exceeds {BitLength} bits");

      ulong v = 0;
      for (int i = 0; i < bits; i++)
      {
        int p = BitPos + i;
        uint bit = (uint)((data[p >> 3] >> (7 - (p & 7))) & 1);   // MSB-first within each byte
        v = (v << 1) | bit;
      }
      BitPos += bits;
      return v;
    }

    /// <summary>
    /// Read an unsigned integer of <paramref name="bits"/> bits honoring <paramref name="littleEndian"/>. The
    /// byte-order swap applies only when the width is a whole number of bytes (a sub-byte field has no byte
    /// order — it is the MSB-first bit-packed value).
    /// </summary>
    public ulong ReadUInt(int bits, bool littleEndian)
    {
      ulong v = ReadBitsBe(bits);
      if (littleEndian && bits % 8 == 0 && bits > 8) v = SwapBytes(v, bits / 8);
      return v;
    }

    /// <summary>Read a two's-complement signed integer of <paramref name="bits"/> bits.</summary>
    public long ReadInt(int bits, bool littleEndian)
    {
      ulong v = ReadUInt(bits, littleEndian);
      if (bits == 64) return (long)v;                   // full width: the cast carries the sign
      ulong signBit = 1UL << (bits - 1);
      if ((v & signBit) != 0) return (long)(v | ~((1UL << bits) - 1)); // sign-extend
      return (long)v;
    }

    /// <summary>Read an IEEE-754 float: <paramref name="bits"/> must be 32 (<c>f4</c>) or 64 (<c>f8</c>).</summary>
    public double ReadFloat(int bits, bool littleEndian)
    {
      ulong v = ReadUInt(bits, littleEndian);
      return bits switch
      {
        32 => BitConverter.Int32BitsToSingle((int)(uint)v),
        64 => BitConverter.Int64BitsToDouble((long)v),
        _ => throw new ArgumentOutOfRangeException(nameof(bits), bits, "float must be 32 or 64 bits")
      };
    }

    /// <summary>Read <paramref name="count"/> raw bytes; requires the cursor to be byte-aligned.</summary>
    public byte[] ReadBytes(int count)
    {
      if ((BitPos & 7) != 0) throw new InvalidOperationException("ReadBytes requires byte alignment");
      int start = BitPos >> 3;
      if (start + count > data.Length)
        throw new InvalidOperationException($"read of {count} bytes at byte {start} exceeds {data.Length}");
      var slice = new byte[count];
      Array.Copy(data, start, slice, 0, count);
      BitPos += count * 8;
      return slice;
    }

    /// <summary>Read <paramref name="count"/> bytes as an ASCII string. The field is treated as a
    /// NUL-terminated C buffer: the string ends at the first NUL, so uninitialized bytes past the terminator
    /// (common in fixed <c>char[N]</c> firmware buffers, e.g. a version string) are ignored. Trailing dots
    /// (non-printables) and spaces are then dropped. The full <paramref name="count"/> bytes are still consumed.</summary>
    public string ReadAscii(int count)
    {
      byte[] raw = ReadBytes(count);
      int len = Array.IndexOf(raw, (byte)0);        // C-string: stop at the first NUL
      if (len < 0) len = count;
      var chars = new char[len];
      for (int i = 0; i < len; i++) chars[i] = raw[i] >= 0x20 && raw[i] < 0x7f ? (char)raw[i] : '.';
      return new string(chars).TrimEnd('.', ' ', '\0');
    }

    private static ulong SwapBytes(ulong v, int nbytes)
    {
      ulong r = 0;
      for (int i = 0; i < nbytes; i++) { r = (r << 8) | (v & 0xFF); v >>= 8; }
      return r;
    }
  }
}
