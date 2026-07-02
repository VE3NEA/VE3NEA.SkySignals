using System;

namespace VE3NEA.SkyTlm.Deframing
{
  /// <summary>
  /// Soft-bit (LLR-style) operations for the soft-decision deframing chain.
  /// A soft value <c>v</c> follows the demodulator's
  /// convention: <c>sign(v)</c> is the bit (<c>v&gt;0 ⇒ 1</c>), <c>|v|</c> is the confidence. XOR/XNOR over
  /// GF(2) are combined with the <b>min-sum box-plus</b> approximation
  /// <c>L(a⊕b) ≈ sign·sign·min(|a|,|b|)</c>, which keeps reliability information without an early hard slice.
  /// Both the NRZI decoder and the G3RUH descrambler are linear over GF(2), so this is exact for the
  /// <i>signs</i> (hard decisions) and a good approximation for the magnitudes.
  /// </summary>
  public static class SoftBits
  {
    /// <summary>Hard bit of a soft value (v ≥ 0 ⇒ 1).</summary>
    public static int Hard(float v) => v >= 0 ? 1 : 0;

    /// <summary>
    /// NRZ-I differential decode on hard bits in place: <c>out[i] = in[i] ⊕ in[i−1]</c> (the first bit is its
    /// own reference, left unchanged) — the standard NRZ-I differential decode applied for
    /// <c>precoding: differential</c>. Polarity-insensitive (a globally inverted input
    /// produces the same output), so it absorbs the non-coherent discriminator's sign ambiguity.
    /// </summary>
    public static void DiffDecode(int[] bits)
    {
      for (int i = bits.Length - 1; i > 0; i--) bits[i] ^= bits[i - 1];
    }

    private static float Sign(float x) => x >= 0 ? 1f : -1f;

    /// <summary>Soft XOR (box-plus, min-sum): the soft value of <c>a ⊕ b</c>.</summary>
    public static float Xor(float a, float b) => -Sign(a) * Sign(b) * Math.Min(Math.Abs(a), Math.Abs(b));

    /// <summary>Soft XNOR: the soft value of <c>NOT(a ⊕ b)</c> (NRZI's "1 = no transition").</summary>
    public static float Xnor(float a, float b) => Sign(a) * Sign(b) * Math.Min(Math.Abs(a), Math.Abs(b));

    /// <summary>
    /// Soft NRZI decode: <c>data[n] = enc[n] XNOR enc[n−1]</c> — a data <c>1</c> is "no
    /// transition", a <c>0</c> is a transition (AX.25 convention). Polarity-insensitive,
    /// so it absorbs the non-coherent discriminator's global sign ambiguity. Returns <c>e.Length − 1</c>
    /// soft data bits (the first encoded bit is only a reference).
    /// </summary>
    public static float[] NrziDecode(float[] e)
    {
      if (e.Length < 2) return Array.Empty<float>();
      var d = new float[e.Length - 1];
      for (int n = 1; n < e.Length; n++) d[n - 1] = Xnor(e[n], e[n - 1]);
      return d;
    }

    /// <summary>
    /// Soft G3RUH descramble: the self-synchronizing descrambler for polynomial
    /// <c>1 + x¹² + x¹⁷</c>, <c>out[n] = r[n] ⊕ r[n−12] ⊕ r[n−17]</c> over the received (scrambled) soft
    /// stream — the inverse of the G3RUH scrambler and identical to direwolf's <c>descramble()</c>. The
    /// three XOR terms are combined with the box-plus operator so reliability is preserved. Taps before the
    /// register has filled are taken as bit 0 with full confidence (matching the scrambler's zero init),
    /// which is the XOR identity and leaves the early (pre-sync) output unchanged.
    /// </summary>
    public static float[] G3ruhDescramble(float[] r)
    {
      const float zero = -1e9f; // bit 0, full confidence => XOR identity for unfilled taps
      var o = new float[r.Length];
      for (int n = 0; n < r.Length; n++)
      {
        float t12 = n >= 12 ? r[n - 12] : zero;
        float t17 = n >= 17 ? r[n - 17] : zero;
        o[n] = Xor(Xor(r[n], t12), t17);
      }
      return o;
    }
  }
}
