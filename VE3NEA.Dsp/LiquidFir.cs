using MathNet.Numerics;

namespace VE3NEA
{
  /// <summary>
  /// Zero-phase, length-preserving FIR convolution backed by liquid-dsp's SIMD <c>firfilt</c> — a drop-in
  /// fast path for the naive O(N·taps) managed loop when the kernel is <b>symmetric</b> (linear-phase).
  /// liquid computes the causal convolution <c>y_c[i] = Σ h[k]·x[i−k]</c>; the centered ("same") output is
  /// recovered by discarding the (taps−1)/2-sample group delay: <c>y[i] = y_c[i + taps/2]</c>, with the tail
  /// produced by flushing the delay line with zeros. Symmetry makes liquid's kernel orientation immaterial.
  /// Note: liquid accumulates in single precision (the managed loops use a double accumulator), so results
  /// are equal only to float rounding — callers that need bit-identical output keep the managed path.
  /// </summary>
  public static unsafe class LiquidFir
  {
    /// <summary>Zero-phase 'same' convolution of real <paramref name="x"/> with a centred symmetric kernel.</summary>
    public static float[] ConvolveSame(float[] x, float[] h)
    {
      int n = x.Length, m = h.Length, half = m / 2;
      if (n == 0 || m <= 1) return (float[])x.Clone();
      if (half > n) return ConvolveSameManaged(x, h);   // degenerate (kernel ≫ signal): keep the simple path

      var y = new float[n];
      fixed (float* ph = h)
      {
        var q = NativeLiquidDsp.firfilt_rrrf_create(ph, (uint)m);
        try
        {
          fixed (float* px = x, py = y)
            NativeLiquidDsp.firfilt_rrrf_execute_block(q, px, (uint)n, py);
          // y holds y_c[0..n); shift out the group delay, then flush `half` zeros for the tail.
          Array.Copy(y, half, y, 0, n - half);
          var zeros = new float[half];
          fixed (float* pz = zeros, py = y)
            NativeLiquidDsp.firfilt_rrrf_execute_block(q, pz, (uint)half, py + (n - half));
        }
        finally { NativeLiquidDsp.firfilt_rrrf_destroy(q); }
      }
      return y;
    }

    /// <summary>Zero-phase 'same' convolution of complex <paramref name="x"/> with a centred symmetric real kernel.</summary>
    public static Complex32[] ConvolveSame(Complex32[] x, float[] h)
    {
      int n = x.Length, m = h.Length, half = m / 2;
      if (n == 0 || m <= 1) return (Complex32[])x.Clone();
      if (half > n) return ConvolveSameManaged(x, h);

      var y = new Complex32[n];
      fixed (float* ph = h)
      {
        var q = NativeLiquidDsp.firfilt_crcf_create(ph, (uint)m);
        try
        {
          fixed (Complex32* px = x, py = y)
            NativeLiquidDsp.firfilt_crcf_execute_block(q, px, (uint)n, py);
          Array.Copy(y, half, y, 0, n - half);
          var zeros = new Complex32[half];
          fixed (Complex32* pz = zeros, py = y)
            NativeLiquidDsp.firfilt_crcf_execute_block(q, pz, (uint)half, py + (n - half));
        }
        finally { NativeLiquidDsp.firfilt_crcf_destroy(q); }
      }
      return y;
    }

    /// <summary>
    /// Zero-phase integer interpolation by <paramref name="L"/> via liquid's polyphase <c>firinterp</c>:
    /// equivalent to zero-stuffing <paramref name="x"/> L× and convolving 'same' with the centred symmetric
    /// anti-image kernel <paramref name="h"/> (no group delay in the output). The kernel is passed at its
    /// design gain; callers wanting unit passband gain after zero-stuffing pre-scale it by L.
    /// </summary>
    public static Complex32[] Interpolate(Complex32[] x, float[] h, int L)
    {
      int n = x.Length, m = h.Length, half = m / 2;
      int total = n * L;
      if (n == 0 || L <= 1) return (Complex32[])x.Clone();
      if (half > total)                                  // degenerate (kernel ≫ signal): simple managed path
      {
        var z = new Complex32[total];
        for (int i = 0; i < n; i++) z[i * L] = x[i];
        return ConvolveSameManaged(z, h);
      }

      var y = new Complex32[total];
      fixed (float* ph = h)
      {
        var q = NativeLiquidDsp.firinterp_crcf_create((uint)L, ph, (uint)m);
        try
        {
          fixed (Complex32* px = x, py = y)
            NativeLiquidDsp.firinterp_crcf_execute_block(q, px, (uint)n, py);
          // y holds the causal outputs; shift out the (m−1)/2-sample group delay (output rate), then
          // flush enough zero INPUT samples to regenerate the discarded tail.
          int keep = Math.Max(0, total - half);
          Array.Copy(y, Math.Min(half, total), y, 0, keep);
          int tail = total - keep;                       // = min(half, total)
          int zin = (tail + L - 1) / L;                  // zero inputs needed to produce ≥ tail outputs
          if (zin > 0)
          {
            var zeros = new Complex32[zin];
            var zout = new Complex32[zin * L];
            fixed (Complex32* pz = zeros, po = zout)
              NativeLiquidDsp.firinterp_crcf_execute_block(q, pz, (uint)zin, po);
            Array.Copy(zout, 0, y, keep, tail);
          }
        }
        finally { NativeLiquidDsp.firinterp_crcf_destroy(q); }
      }
      return y;
    }

    private static float[] ConvolveSameManaged(float[] x, float[] h)
    {
      int n = x.Length, m = h.Length, half = m / 2;
      var y = new float[n];
      for (int i = 0; i < n; i++)
      {
        double acc = 0;
        for (int k = 0; k < m; k++)
        {
          int j = i + k - half;
          if ((uint)j < (uint)n) acc += (double)x[j] * h[k];
        }
        y[i] = (float)acc;
      }
      return y;
    }

    private static Complex32[] ConvolveSameManaged(Complex32[] x, float[] h)
    {
      int n = x.Length, m = h.Length, half = m / 2;
      var y = new Complex32[n];
      for (int i = 0; i < n; i++)
      {
        double re = 0, im = 0;
        for (int k = 0; k < m; k++)
        {
          int j = i + k - half;
          if ((uint)j < (uint)n) { re += x[j].Real * h[k]; im += x[j].Imaginary * h[k]; }
        }
        y[i] = new Complex32((float)re, (float)im);
      }
      return y;
    }
  }
}
