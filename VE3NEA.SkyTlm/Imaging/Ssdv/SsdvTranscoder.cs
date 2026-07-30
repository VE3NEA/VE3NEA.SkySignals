using System;
using System.Collections.Generic;
using System.IO;

namespace VE3NEA.SkyTlm.Imaging.Ssdv
{
  /// <summary>
  /// Turns a run of ordered <see cref="SsdvPacket"/>s into a JPEG file. This is a transcoder, not a
  /// decoder: nothing is ever transformed into pixels. The packets carry entropy-coded scan data whose
  /// only departures from baseline JPEG are that the DC predictor is reset at the start of each packet
  /// and that byte stuffing has been stripped, so the work is to Huffman-decode each coefficient,
  /// re-encode it with the DC predictor restored and stuffing reinstated, and wrap the result in the
  /// standard markers.
  /// <para>
  /// A C# transcription of the decoder half of <c>fsphil/ssdv</c>'s <c>ssdv.c</c> (GPL-3.0, Philip
  /// Heron), and deliberately a close one: output is asserted byte-identical to the reference tool's,
  /// which is the only practical way to be sure of a format with no specification. Three simplifications
  /// were taken, each safe only because this is the decode direction:
  /// </para>
  /// <list type="number">
  /// <item>The reference's <c>BADJ</c>/<c>UADJ</c>/<c>AADJ</c> re-quantisation macros are identities
  /// here. They exist to convert between a source and a destination quantisation table, and the decoder
  /// builds both from the same base tables at the same quality, so no coefficient ever changes.</item>
  /// <item>The greyscale MCU padding in <c>ssdv_process</c> is unreachable: SSDV always transmits three
  /// components, and the encoder is what pads a greyscale source.</item>
  /// <item>The restart-interval branch is unreachable: DRI is only ever read from a source JPEG's
  /// markers, which the decoder never sees.</item>
  /// </list>
  /// <para>
  /// Missing packets are not an error. <see cref="Feed"/> pads the hole with neutral MCUs so that
  /// everything after it still lands in the right place — the loss costs its own MCUs and nothing else,
  /// which is the whole point of the format.
  /// </para>
  /// </summary>
  public sealed class SsdvTranscoder
  {
    private const int MarkerSoi = 0xFFD8, MarkerEoi = 0xFFD9, MarkerSof0 = 0xFFC0;
    private const int MarkerDht = 0xFFC4, MarkerDqt = 0xFFDB, MarkerSos = 0xFFDA, MarkerApp0 = 0xFFE0;

    private enum Step { Ok, FeedMe, Eoi, Error }
    private enum State { Huff, Int }

    // image geometry, taken from the first packet fed
    private int width, height, quality, mcuMode, mcuCount, ycParts;
    private byte[] ddqt0 = [], ddqt1 = [];

    // output bit stream
    private readonly MemoryStream output = new();
    private uint outBits;
    private int outLen;
    private bool stuffing;

    // input bit stream
    private uint workBits;
    private int workLen;

    // JPEG scan state. acRun is the zero run of the coefficient being decoded; pendingRun accumulates
    // runs across coefficients that quantised away (dead here, but kept so the port stays legible
    // against the original).
    private State state = State.Huff;
    private int component, mcuPart, acPart, acRun, pendingRun, needBits;
    private readonly int[] dc = new int[3];
    private int mcuId, resetMcu, nextResetMcu;

    // packet sequencing
    private int expectedPacketId, packetMcuId = -1, packetMcuOffset = -1;
    private bool headersWritten, finished;
    private byte[]? jpeg;

    /// <summary>Image width in pixels, known once the first packet has been fed.</summary>
    public int Width => width;

    /// <summary>Image height in pixels.</summary>
    public int Height => height;

    /// <summary>MCUs written so far, out of <see cref="McuCount"/> — including those written as gap fill.</summary>
    public int McuId => mcuId;

    /// <summary>Total MCUs in the image.</summary>
    public int McuCount => mcuCount;

    /// <summary>Every MCU of the image has been written; further packets would be ignored.</summary>
    public bool IsComplete { get; private set; }

    /// <summary>
    /// Transcode a whole image in one call. Packets must be in ascending <see cref="SsdvPacket.PacketId"/>
    /// order; gaps are filled, and packets out of order or contradicting the MCU count are skipped.
    /// </summary>
    public static byte[] Transcode(IEnumerable<SsdvPacket> packets)
    {
      var t = new SsdvTranscoder();
      foreach (var p in packets) t.Feed(p);
      return t.GetJpeg();
    }

    /// <summary>
    /// Add one validated packet. The first one fed also fixes the image geometry and emits the JPEG
    /// headers, so it must belong to the same image as every packet after it.
    /// </summary>
    public void Feed(SsdvPacket packet)
    {
      if (finished) throw new InvalidOperationException("the JPEG has been finalised; start a new transcoder");
      if (IsComplete) return;

      packetMcuOffset = packet.McuOffset;
      packetMcuId = packet.McuIndex;
      if (packetMcuId >= 0) nextResetMcu = packetMcuId;

      // The one deliberate deviation from the reference, which tests for "the next packet I expect is
      // still 0" instead of keeping a flag. Those agree except when the very first packet is dropped
      // below for having no MCU of its own: the counter then stays at 0 and the reference emits a
      // second set of JPEG headers mid-file. The flag is used instead, so such a stream yields a
      // shorter but valid file where the reference yields a broken one.
      if (!headersWritten) WriteHeaders(packet);

      int i = 0;
      if (packet.PacketId != expectedPacketId)
      {
        // A packet older than the one expected cannot be placed: the scan is a single bit stream and
        // its position has already moved past.
        if (packet.PacketId < expectedPacketId) return;

        // Packets lost. Without an MCU boundary of its own this packet cannot be re-anchored either,
        // because there is no way to know where in the gap its bits belong.
        if (packetMcuId < 0) return;

        FillGap(packetMcuId);
        i = packetMcuOffset;

        state = State.Huff;
        component = 0;
        mcuPart = 0;
        acPart = 0;
        pendingRun = 0;
        expectedPacketId = packet.PacketId;
      }

      var payload = packet.Payload;
      for (; i < payload.Length; i++)
      {
        if (i == packetMcuOffset)
        {
          // the first MCU of a packet is byte-aligned, so any partial byte left over from the previous
          // packet is padding and must be dropped
          workBits = 0;
          workLen = 0;

          // and if the sender's idea of where we are disagrees with ours, the packet cannot be placed
          if (mcuId != packetMcuId) return;
        }

        workBits = workBits << 8 | payload[i];
        workLen += 8;

        Step r;
        while ((r = Process()) == Step.Ok) ;

        if (r == Step.Eoi) { IsComplete = true; return; }
        if (r == Step.Error) throw new InvalidDataException(
          $"SSDV packet {packet.PacketId} of image {packet.ImageId} contains an invalid Huffman code");
      }

      expectedPacketId++;
    }

    /// <summary>
    /// Finalise and return the JPEG. Any MCUs still missing at the end of the image are filled, so this
    /// always returns a decodable file — a partial image simply has neutral grey where nothing arrived.
    /// The transcoder cannot be fed after this; call it once per image, or use <see cref="Transcode"/>.
    /// </summary>
    public byte[] GetJpeg()
    {
      if (jpeg != null) return jpeg;
      if (!headersWritten) return jpeg = [];

      if (mcuId < mcuCount) FillGap(mcuCount);

      OutbitsSync();
      stuffing = false;
      WriteMarker(MarkerEoi, default);

      finished = true;
      return jpeg = output.ToArray();
    }




    // ----------------------------------------------------------------------------------------------------
    //                                          the scan processor
    // ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// One step of the transcode: decode a single Huffman symbol or the integer that follows one, and
    /// re-emit it. Returns <see cref="Step.FeedMe"/> when the input bit buffer has run dry, which is the
    /// normal way out.
    /// </summary>
    private Step Process()
    {
      if (state == State.Huff)
      {
        if (mcuPart == 0 && acPart == 0 && nextResetMcu > resetMcu) resetMcu = nextResetMcu;

        var step = DhtLookup(out byte symbol, out int codeWidth);
        if (step != Step.Ok) return step;

        if (acPart == 0)
        {
          if (symbol == 0x00)
          {
            // no DC change from the previous block — but at a packet boundary the sender's predictor
            // was reset to zero, so ours has to be steered back to zero too
            if (resetMcu == mcuId && (mcuPart == 0 || mcuPart >= ycParts))
            {
              OutJpegInt(0, 0 - dc[component]);
              dc[component] = 0;
            }
            else OutJpegInt(0, 0);

            acPart++;
          }
          else
          {
            state = State.Int;
            needBits = symbol;
          }
        }
        else
        {
          acRun = 0;
          if (symbol == 0x00)
          {
            // end of block: every remaining AC coefficient is zero
            OutJpegInt(0, 0);
            acPart = 64;
          }
          else if (symbol == 0xF0)
          {
            // ZRL: the next 16 AC coefficients are zero
            OutJpegInt(15, 0);
            acPart += 16;
          }
          else
          {
            state = State.Int;
            acRun = symbol >> 4;
            acPart += acRun;
            needBits = symbol & 0x0F;
          }
        }

        workLen -= codeWidth;
        workBits &= (1u << workLen) - 1;
      }
      else
      {
        if (workLen < needBits) return Step.FeedMe;

        int i = JpegInt((int)(workBits >> (workLen - needBits)), needBits);

        if (acPart == 0)
        {
          if (resetMcu == mcuId && (mcuPart == 0 || mcuPart >= ycParts))
          {
            // the packet carries an absolute DC value; the file wants it relative to our predictor
            OutJpegInt(0, i - dc[component]);
            dc[component] = i;
          }
          else
          {
            dc[component] += i;
            OutJpegInt(0, i);
          }
        }
        else
        {
          if (i != 0)
          {
            pendingRun += acRun;
            while (pendingRun >= 16)
            {
              OutJpegInt(15, 0);
              pendingRun -= 16;
            }
            OutJpegInt(pendingRun, i);
            pendingRun = 0;
          }
          else if (acPart >= 63)
          {
            OutJpegInt(0, 0);
            pendingRun = 0;
          }
          else pendingRun += acRun + 1;
        }

        acPart++;
        state = State.Huff;

        workLen -= needBits;
        workBits &= (1u << workLen) - 1;
      }

      if (acPart >= 64)
      {
        mcuPart++;

        if (mcuPart == ycParts + 2)
        {
          mcuPart = 0;
          mcuId++;
          if (mcuId >= mcuCount)
          {
            OutbitsSync();
            return Step.Eoi;
          }
        }

        component = mcuPart < ycParts ? 0 : mcuPart - ycParts + 1;
        acPart = 0;
        pendingRun = 0;
      }

      return Step.Ok;
    }

    /// <summary>
    /// Write neutral MCUs — DC and AC both zero, i.e. mid-grey — up to <paramref name="nextMcu"/>, first
    /// closing off whatever block was left half-written when the stream broke.
    /// </summary>
    private void FillGap(int nextMcu)
    {
      if (mcuPart > 0 || acPart > 0)
      {
        if (acPart > 0)
        {
          OutJpegInt(0, 0);
          mcuPart++;
        }

        for (; mcuPart < ycParts + 2; mcuPart++) WriteEmptyBlock();
        mcuId++;
      }

      for (; mcuId < nextMcu; mcuId++)
        for (mcuPart = 0; mcuPart < ycParts + 2; mcuPart++) WriteEmptyBlock();
    }

    /// <summary>One all-zero block for the current <see cref="mcuPart"/>: a zero DC delta and an EOB.</summary>
    private void WriteEmptyBlock()
    {
      component = mcuPart < ycParts ? 0 : mcuPart - ycParts + 1;
      acPart = 0;
      OutJpegInt(0, 0);
      acPart = 1;
      OutJpegInt(0, 0);
    }




    // ----------------------------------------------------------------------------------------------------
    //                                          huffman coding
    // ----------------------------------------------------------------------------------------------------

    /// <summary>Huffman table for the coefficient being decoded: AC or DC, luminance or chrominance.</summary>
    private ReadOnlySpan<byte> Dht => acPart != 0
      ? component != 0 ? SsdvJpegTables.StdDht11 : SsdvJpegTables.StdDht10
      : component != 0 ? SsdvJpegTables.StdDht01 : SsdvJpegTables.StdDht00;

    /// <summary>
    /// Read the next Huffman code from the input bits. Codes are canonical, so they can be matched by
    /// walking the code-length counts and incrementing a running code value rather than by building a
    /// lookup table — at a few hundred packets per image there is nothing to gain by doing better.
    /// </summary>
    private Step DhtLookup(out byte symbol, out int codeWidth)
    {
      symbol = 0;
      codeWidth = 0;

      var dht = Dht;
      int code = 0, at = 17;

      for (int cw = 1; cw <= 16; cw++)
      {
        if (cw > workLen) return Step.FeedMe;

        for (int n = dht[cw]; n > 0; n--)
        {
          if ((int)(workBits >> (workLen - cw)) == code)
          {
            symbol = dht[at];
            codeWidth = cw;
            return Step.Ok;
          }
          at++;
          code++;
        }

        code <<= 1;
      }

      return Step.Error;
    }

    /// <summary>The inverse: find the code for a symbol we are about to write.</summary>
    private bool DhtLookupSymbol(byte symbol, out int bits, out int codeWidth)
    {
      var dht = Dht;
      int code = 0, at = 17;

      for (int cw = 1; cw <= 16; cw++)
      {
        for (int n = dht[cw]; n > 0; n--)
        {
          if (dht[at] == symbol)
          {
            bits = code;
            codeWidth = cw;
            return true;
          }
          at++;
          code++;
        }

        code <<= 1;
      }

      bits = codeWidth = 0;
      return false;
    }

    /// <summary>Sign-extend a JPEG variable-length integer: the top half of the range is positive, the
    /// bottom half is its ones' complement negative.</summary>
    private static int JpegInt(int bits, int codeWidth)
    {
      int b = (1 << codeWidth) - 1;
      if (bits <= b >> 1) bits = -(bits ^ b);
      return bits;
    }

    /// <summary>The inverse: the magnitude category and the bits to write for a value.</summary>
    private static void JpegEncodeInt(int value, out int bits, out int codeWidth)
    {
      bits = value;

      int magnitude = value < 0 ? -value : value;
      for (codeWidth = 0; magnitude != 0; magnitude >>= 1) codeWidth++;

      if (bits < 0) bits = -bits ^ (1 << codeWidth) - 1;
    }

    /// <summary>Emit one coefficient: the Huffman code for (zero run, magnitude category), then the value.</summary>
    private void OutJpegInt(int run, int value)
    {
      JpegEncodeInt(value, out int intBits, out int intLen);

      byte symbol = (byte)(run << 4 | intLen & 0x0F);
      if (!DhtLookupSymbol(symbol, out int huffBits, out int huffLen))
        throw new InvalidDataException($"no Huffman code for symbol 0x{symbol:X2} (run {run}, value {value})");

      Outbits((uint)huffBits, huffLen);
      if (intLen > 0) Outbits((uint)intBits, intLen);
    }




    // ----------------------------------------------------------------------------------------------------
    //                                          the output stream
    // ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// Append bits, flushing whole bytes as they complete. Inside the scan a 0xFF byte is followed by a
    /// stuffed 0x00 so it cannot be mistaken for a marker; the trick used to insert it is the reference's
    /// — rewind the bit count by a byte's worth of zeros, which the next pass writes out.
    /// </summary>
    private void Outbits(uint bits, int codeWidth)
    {
      if (codeWidth > 0)
      {
        outBits <<= codeWidth;
        outBits |= bits & (1u << codeWidth) - 1;
        outLen += codeWidth;
      }

      while (outLen >= 8)
      {
        byte b = (byte)(outBits >> outLen - 8);
        output.WriteByte(b);
        outLen -= 8;

        if (stuffing && b == 0xFF)
        {
          outBits &= (1u << outLen) - 1;
          outLen += 8;
        }
      }
    }

    /// <summary>Pad to a byte boundary with 1 bits, as JPEG requires at the end of a scan.</summary>
    private void OutbitsSync()
    {
      int partial = outLen % 8;
      if (partial != 0) Outbits(0xFF, 8 - partial);
    }

    private void WriteMarker(int id, ReadOnlySpan<byte> data)
    {
      Outbits((uint)id, 16);
      if (data.Length == 0) return;

      Outbits((uint)(data.Length + 2), 16);
      foreach (byte b in data) Outbits(b, 8);
    }

    /// <summary>
    /// Emit everything that precedes the scan, then turn on byte stuffing. The tables are the fixed ones
    /// the format assumes; only the quantisation scaling and the frame geometry come from the packet.
    /// </summary>
    private void WriteHeaders(SsdvPacket packet)
    {
      width = packet.Width;
      height = packet.Height;
      quality = packet.Quality;
      mcuMode = packet.Subsampling;
      mcuCount = packet.McuCount;
      ycParts = mcuMode switch { 0 => 4, 1 or 2 => 2, _ => 1 };

      ddqt0 = SsdvJpegTables.LoadStandardDqt(SsdvJpegTables.StdDqt0, quality);
      ddqt1 = SsdvJpegTables.LoadStandardDqt(SsdvJpegTables.StdDqt1, quality);

      WriteMarker(MarkerSoi, default);
      WriteMarker(MarkerApp0, SsdvJpegTables.App0);
      WriteMarker(MarkerDqt, ddqt0);
      WriteMarker(MarkerDqt, ddqt1);

      var sof0 = new byte[]
      {
        8,                                  // sample precision
        (byte)(height >> 8), (byte)height,
        (byte)(width >> 8), (byte)width,
        3,                                  // Y'CbCr
        1, mcuMode switch { 0 => 0x22, 1 => 0x12, 2 => 0x21, _ => 0x11 }, 0x00,
        2, 0x11, 0x01,
        3, 0x11, 0x01
      };
      WriteMarker(MarkerSof0, sof0);

      WriteMarker(MarkerDht, SsdvJpegTables.StdDht00);
      WriteMarker(MarkerDht, SsdvJpegTables.StdDht10);
      WriteMarker(MarkerDht, SsdvJpegTables.StdDht01);
      WriteMarker(MarkerDht, SsdvJpegTables.StdDht11);
      WriteMarker(MarkerSos, SsdvJpegTables.Sos);

      stuffing = true;
      headersWritten = true;
    }
  }
}
