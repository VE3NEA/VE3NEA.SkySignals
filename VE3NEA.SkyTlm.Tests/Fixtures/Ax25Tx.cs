using System;
using System.Collections.Generic;
using System.Text;
using VE3NEA.SkyTlm.Deframing;

namespace VE3NEA.SkyTlm.Tests.Fixtures
{
  /// <summary>
  /// AX.25 G3RUH transmit-side reference for the deframer tests: the inverse of the receive chain
  /// (HDLC flag/stuff → NRZI encode → G3RUH scramble). Building a frame here and pushing it through
  /// <c>Ax25G3ruhDeframer</c> proves the RX chain end-to-end, independent of the real recordings.
  /// On-air bit relationships are taken from direwolf (NRZI "1 = no transition", scrambler 1+x¹²+x¹⁷).
  /// </summary>
  public static class Ax25Tx
  {
    /// <summary>Build the FCS-bearing frame bytes (Address…Info + 2 FCS octets, low byte first).</summary>
    public static byte[] WithFcs(byte[] frame)
    {
      ushort fcs = Crc16Ccitt.Compute(frame);
      var o = new byte[frame.Length + 2];
      Array.Copy(frame, o, frame.Length);
      o[frame.Length] = (byte)(fcs & 0xff);
      o[frame.Length + 1] = (byte)(fcs >> 8);
      return o;
    }

    /// <summary>Bytes → LSB-first bits (AX.25 sends each octet least-significant bit first).</summary>
    public static List<int> ToBitsLsbFirst(byte[] bytes)
    {
      var bits = new List<int>(bytes.Length * 8);
      foreach (byte b in bytes)
        for (int i = 0; i < 8; i++) bits.Add((b >> i) & 1);
      return bits;
    }

    /// <summary>Insert a 0 after every run of five consecutive 1s (HDLC transmit bit-stuffing).</summary>
    public static List<int> BitStuff(IReadOnlyList<int> bits)
    {
      var o = new List<int>(bits.Count + bits.Count / 5 + 1);
      int ones = 0;
      foreach (int b in bits)
      {
        o.Add(b);
        if (b == 1) { if (++ones == 5) { o.Add(0); ones = 0; } }
        else ones = 0;
      }
      return o;
    }

    /// <summary>The 8-bit HDLC flag 0x7E (01111110) as LSB-first bits — never stuffed.</summary>
    public static int[] Flag() => new[] { 0, 1, 1, 1, 1, 1, 1, 0 };

    /// <summary>
    /// Frame bytes (with FCS) → the full HDLC bit stream: <paramref name="flagsBefore"/> opening flags,
    /// the bit-stuffed payload, then <paramref name="flagsAfter"/> closing flags.
    /// </summary>
    public static List<int> HdlcBits(byte[] frameWithFcs, int flagsBefore = 8, int flagsAfter = 4)
    {
      var bits = new List<int>();
      for (int i = 0; i < flagsBefore; i++) bits.AddRange(Flag());
      bits.AddRange(BitStuff(ToBitsLsbFirst(frameWithFcs)));
      for (int i = 0; i < flagsAfter; i++) bits.AddRange(Flag());
      return bits;
    }

    /// <summary>NRZI encode (TX): a data 1 keeps the level, a 0 flips it. Seeded with level 0.</summary>
    public static List<int> NrziEncode(IReadOnlyList<int> data, int seed = 0)
    {
      var o = new List<int>(data.Count);
      int level = seed & 1;
      foreach (int b in data) { if (b == 0) level ^= 1; o.Add(level); }
      return o;
    }

    /// <summary>G3RUH scramble (TX, multiplicative): <c>t[n] = d[n] ⊕ t[n−12] ⊕ t[n−17]</c>, register init 0.</summary>
    public static List<int> G3ruhScramble(IReadOnlyList<int> data)
    {
      var t = new List<int>(data.Count);
      for (int n = 0; n < data.Count; n++)
      {
        int t12 = n >= 12 ? t[n - 12] : 0;
        int t17 = n >= 17 ? t[n - 17] : 0;
        t.Add(data[n] ^ t12 ^ t17);
      }
      return t;
    }

    /// <summary>Full on-air bit stream: HDLC(frame+FCS) → NRZI encode → G3RUH scramble.</summary>
    public static int[] OnAirBits(byte[] frame, int flagsBefore = 16, int flagsAfter = 8)
    {
      var hdlc = HdlcBits(WithFcs(frame), flagsBefore, flagsAfter);
      var nrzi = NrziEncode(hdlc);
      return G3ruhScramble(nrzi).ToArray();
    }

    /// <summary>Build a UI frame (no FCS yet): dest/src addresses, control 0x03, PID 0xF0, ASCII info.</summary>
    public static byte[] MakeUiFrame(string dest, string src, string info)
    {
      var f = new List<byte>();
      f.AddRange(EncodeAddr(dest, last: false));
      f.AddRange(EncodeAddr(src, last: true));
      f.Add(0x03); // UI
      f.Add(0xF0); // no layer 3
      f.AddRange(Encoding.ASCII.GetBytes(info));
      return f.ToArray();
    }

    private static byte[] EncodeAddr(string call, bool last)
    {
      string name = call; int ssid = 0;
      int dash = call.IndexOf('-');
      if (dash >= 0) { name = call[..dash]; ssid = int.Parse(call[(dash + 1)..]); }
      var a = new byte[7];
      for (int i = 0; i < 6; i++)
      {
        char c = i < name.Length ? name[i] : ' ';
        a[i] = (byte)(c << 1);                      // callsign chars shifted left 1
      }
      a[6] = (byte)(0x60 | ((ssid & 0x0f) << 1) | (last ? 1 : 0)); // RR=11, SSID, end-of-address bit
      return a;
    }

    /// <summary>Map on-air bits to clean soft symbols (bit 1 → +amp, bit 0 → −amp).</summary>
    public static float[] ToSoft(int[] bits, float amp = 1f)
    {
      var s = new float[bits.Length];
      for (int i = 0; i < bits.Length; i++) s[i] = bits[i] == 1 ? amp : -amp;
      return s;
    }
  }
}
