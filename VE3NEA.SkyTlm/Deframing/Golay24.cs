using System.Numerics;

namespace VE3NEA.SkyTlm.Deframing
{
  /// <summary>
  /// Extended binary Golay (24,12) codec — a C# port of gr-satellites <c>golay24.c</c> (Daniel Estévez,
  /// after R.H. Morelos-Zaragoza, <i>The Art of Error Correcting Coding</i>, §2.2.3). Corrects up to 3 bit
  /// errors and detects 4 in a 24-bit codeword. Used by the GOMspace AX100/U482C "ASM+Golay" framing to
  /// protect the 12-bit length+flags header that follows the 32-bit ASM (<see cref="Ax100Deframer"/>).
  /// Codeword layout matches the reference: parity in bits 23..12, data in bits 11..0.
  /// </summary>
  public static class Golay24
  {
    private const int N = 12;

    // rows of the parity-check matrix H = [I | B] packed as 24-bit words (parity half | B half).
    private static readonly uint[] H =
    {
      0x8008ed, 0x4001db, 0x2003b5, 0x100769, 0x080ed1, 0x040da3,
      0x020b47, 0x01068f, 0x008d1d, 0x004a3b, 0x002477, 0x001ffe,
    };

    private static uint B(int i) => H[i] & 0xfff;

    /// <summary>Encode the low 12 bits of <paramref name="data"/>; returns the 24-bit codeword.</summary>
    public static uint Encode(uint data)
    {
      uint r = data & 0xfff;
      uint s = 0;
      for (int i = 0; i < N; i++)
      {
        s <<= 1;
        s |= Parity(H[i] & r);
      }
      return ((s & 0xfff) << N) | r;
    }

    /// <summary>
    /// Decode a 24-bit codeword in place (corrected codeword left in <paramref name="data"/>, payload in
    /// its low 12 bits). Returns the number of bit errors corrected (0–3), or −1 when uncorrectable.
    /// </summary>
    public static int Decode(ref uint data)
    {
      uint r = data;
      uint e; // estimated error vector

      // step 1. syndrome s = H·r
      uint s = 0;
      for (int i = 0; i < N; i++)
      {
        s <<= 1;
        s |= Parity(H[i] & r);
      }

      // step 2. w(s) <= 3  =>  e = (s, 0)
      if (BitOperations.PopCount(s) <= 3) { e = s << N; goto step8; }

      // step 3. w(s + B[i]) <= 2  =>  e = (s + B[i], e_{i+1})
      for (int i = 0; i < N; i++)
        if (BitOperations.PopCount(s ^ B(i)) <= 2)
        {
          e = ((s ^ B(i)) << N) | (1u << (N - i - 1));
          goto step8;
        }

      // step 4. modified syndrome q = B·s
      uint q = 0;
      for (int i = 0; i < N; i++)
      {
        q <<= 1;
        q |= Parity(B(i) & s);
      }

      // step 5. w(q) <= 3  =>  e = (0, q)
      if (BitOperations.PopCount(q) <= 3) { e = q; goto step8; }

      // step 6. w(q + B[i]) <= 2  =>  e = (e_{i+1}, q + B[i])
      for (int i = 0; i < N; i++)
        if (BitOperations.PopCount(q ^ B(i)) <= 2)
        {
          e = (1u << (2 * N - i - 1)) | (q ^ B(i));
          goto step8;
        }

      // step 7. uncorrectable
      return -1;

    step8:
      data = r ^ e;
      return BitOperations.PopCount(e);
    }

    private static uint Parity(uint x) => (uint)(BitOperations.PopCount(x) & 1);
  }
}
