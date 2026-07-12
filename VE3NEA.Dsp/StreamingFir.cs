using MathNet.Numerics;

namespace VE3NEA
{
  /// <summary>
  /// Stateful block-streaming FIR filter over liquid-dsp's SIMD <c>firfilt_rrrf</c> — the streaming
  /// counterpart of <see cref="LiquidFir.ConvolveSame(float[], float[])"/>. The filter object persists
  /// across <see cref="Process"/> calls, so consecutive blocks are filtered as one continuous stream
  /// (the delay line carries over). Output is the causal convolution: <c>y[i]</c> lags the zero-phase
  /// 'same' output by <see cref="GroupDelay"/> samples — the caller accounts for the lag in its absolute
  /// sample bookkeeping instead of paying a per-call zero-flush.
  /// </summary>
  public sealed unsafe class StreamingFir : IDisposable
  {
    private NativeLiquidDsp.firfilt_rrrf* q;

    /// <summary>Lag of the causal output behind the zero-phase 'same' convolution: taps/2 samples
    /// for the centred symmetric (linear-phase) kernels used throughout.</summary>
    public int GroupDelay { get; }

    public StreamingFir(float[] h)
    {
      if (h == null || h.Length == 0) throw new ArgumentException("empty FIR kernel", nameof(h));
      GroupDelay = h.Length / 2;
      fixed (float* ph = h) q = NativeLiquidDsp.firfilt_rrrf_create(ph, (uint)h.Length);
    }

    /// <summary>Filter the next block; <paramref name="y"/> must be at least as long as <paramref name="x"/>.</summary>
    public void Process(ReadOnlySpan<float> x, Span<float> y)
    {
      if (y.Length < x.Length) throw new ArgumentException("output shorter than input", nameof(y));
      if (x.IsEmpty) return;
      fixed (float* px = x, py = y)
        NativeLiquidDsp.firfilt_rrrf_execute_block(q, px, (uint)x.Length, py);
    }

    public void Dispose()
    {
      if (q != null) { NativeLiquidDsp.firfilt_rrrf_destroy(q); q = null; }
    }
  }

  /// <summary>
  /// Stateful block-streaming FIR filter over liquid-dsp's <c>firfilt_crcf</c> (complex signal, real
  /// symmetric kernel) — the streaming counterpart of
  /// <see cref="LiquidFir.ConvolveSame(Complex32[], float[])"/>. Same causal-output contract as
  /// <see cref="StreamingFir"/>.
  /// </summary>
  public sealed unsafe class StreamingFirComplex : IDisposable
  {
    private NativeLiquidDsp.firfilt_crcf* q;

    /// <summary>Lag of the causal output behind the zero-phase 'same' convolution (taps/2).</summary>
    public int GroupDelay { get; }

    public StreamingFirComplex(float[] h)
    {
      if (h == null || h.Length == 0) throw new ArgumentException("empty FIR kernel", nameof(h));
      GroupDelay = h.Length / 2;
      fixed (float* ph = h) q = NativeLiquidDsp.firfilt_crcf_create(ph, (uint)h.Length);
    }

    /// <summary>Filter the next block; <paramref name="y"/> must be at least as long as <paramref name="x"/>.</summary>
    public void Process(ReadOnlySpan<Complex32> x, Span<Complex32> y)
    {
      if (y.Length < x.Length) throw new ArgumentException("output shorter than input", nameof(y));
      if (x.IsEmpty) return;
      fixed (Complex32* px = x, py = y)
        NativeLiquidDsp.firfilt_crcf_execute_block(q, px, (uint)x.Length, py);
    }

    public void Dispose()
    {
      if (q != null) { NativeLiquidDsp.firfilt_crcf_destroy(q); q = null; }
    }
  }
}
