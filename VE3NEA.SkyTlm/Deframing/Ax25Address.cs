using System;
using System.Text;

namespace VE3NEA.SkyTlm.Deframing
{
  /// <summary>
  /// Light AX.25 header parsing for display: pull the destination/source callsigns out of a
  /// deframed frame. Each callsign character is shifted left one bit on the wire (<c>byte &gt;&gt; 1</c> to
  /// recover ASCII); the 7th octet of each address holds the SSID (bits 1–4) and the end-of-address flag
  /// (bit 0). This is presentation only — the deframer's correctness does not depend on it.
  /// </summary>
  public static class Ax25Address
  {
    /// <summary>"SRC&gt;DEST" (with SSIDs) if the address field parses, else null.</summary>
    public static string? Describe(byte[] frame)
    {
      if (frame.Length < 14) return null;
      string dest = Callsign(frame, 0);
      string src = Callsign(frame, 7);
      if (dest.Length == 0 || src.Length == 0) return null;
      return $"{src} -> {dest}";
    }

    private static string Callsign(byte[] frame, int offset)
    {
      if (offset + 7 > frame.Length) return string.Empty;
      var sb = new StringBuilder(9);
      for (int i = 0; i < 6; i++)
      {
        char c = (char)(frame[offset + i] >> 1);
        if (c == ' ') continue;
        if (c < '0' || c > 'z') return string.Empty; // not a plausible callsign char
        sb.Append(c);
      }
      int ssid = (frame[offset + 6] >> 1) & 0x0f;
      if (ssid != 0) sb.Append('-').Append(ssid);
      return sb.ToString();
    }
  }
}
