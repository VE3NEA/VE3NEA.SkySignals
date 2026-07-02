using System;
using System.Linq;

namespace VE3NEA.SkyTlm.Tests.Fixtures
{
  /// <summary>
  /// Shared bit-error helpers for the round-trip tests. Kept here (not on a test class) so both the
  /// example-based and FsCheck property suites can score a demodulated soft stream against the TX bits.
  /// </summary>
  public static class BerTools
  {
    /// <summary>Best BER over a small symbol offset and ±sign (the discriminator has a sign/phase ambiguity).</summary>
    public static (double ber, int off, int sign) BestBer(int[] bits, float[] soft)
    {
      var tx = bits.Select(b => b == 1 ? 1 : -1).ToArray();
      int guard = 8;   // trim demod edge transients (filter warm-up / burst ends)
      double best = 1; int bestOff = 0, bestSign = 1;
      for (int sign = -1; sign <= 1; sign += 2)
        for (int off = -4; off <= 8; off++)
        {
          int errs = 0, tot = 0;
          for (int k = guard; k < soft.Length - guard; k++)
          {
            int ti = k + off;
            if (ti < 0 || ti >= tx.Length) continue;
            int rx = Math.Sign(soft[k]) * sign; if (rx == 0) rx = 1;
            if (rx != tx[ti]) errs++;
            tot++;
          }
          if (tot > 50)
          {
            double ber = (double)errs / tot;
            if (ber < best) { best = ber; bestOff = off; bestSign = sign; }
          }
        }
      return (best, bestOff, bestSign);
    }
  }
}
