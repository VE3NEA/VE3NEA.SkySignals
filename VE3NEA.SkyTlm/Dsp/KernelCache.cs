using System.Collections.Concurrent;
using VDsp = VE3NEA.Dsp;

namespace VE3NEA.SkyTlm.Dsp
{
  /// <summary>
  /// Process-wide cache of FIR kernels. The demodulator rebuilds the
  /// same Gaussian / Blackman-sinc kernels for every decoded segment — hundreds of times per pass with
  /// identical arguments — so memoize them. The key space is tiny in practice (one or two (sps, fc) pairs
  /// per signal), so entries are never evicted. <b>Returned arrays are shared: callers must not mutate.</b>
  /// </summary>
  internal static class KernelCache
  {
    private static readonly ConcurrentDictionary<double, float[]> Gauss = new();
    private static readonly ConcurrentDictionary<(double Fc, int Taps), float[]> Sinc = new();

    /// <summary>Cached <see cref="VE3NEA.Dsp.GaussianLowpass"/> (unit-DC Gaussian LPF of the given full width).</summary>
    public static float[] GaussianLowpass(double widthSamples) =>
      Gauss.GetOrAdd(widthSamples, static w => VDsp.GaussianLowpass(w));

    /// <summary>Cached <see cref="VE3NEA.Dsp.BlackmanSincKernel"/> (windowed-sinc LPF, unit DC gain).</summary>
    public static float[] BlackmanSinc(double fc, int taps) =>
      Sinc.GetOrAdd((fc, taps), static k => VDsp.BlackmanSincKernel(k.Fc, k.Taps));

    private static readonly ConcurrentDictionary<(double Fc, int Taps, int Gain), float[]> SincScaled = new();

    /// <summary>Cached Blackman-sinc kernel with its taps pre-multiplied by <paramref name="gain"/>
    /// (interpolator kernels carry gain L to undo the zero-stuffing power loss).</summary>
    public static float[] BlackmanSincScaled(double fc, int taps, int gain) =>
      SincScaled.GetOrAdd((fc, taps, gain), static k =>
      {
        var h = (float[])VDsp.BlackmanSincKernel(k.Fc, k.Taps).Clone();
        for (int i = 0; i < h.Length; i++) h[i] *= k.Gain;
        return h;
      });
  }
}
