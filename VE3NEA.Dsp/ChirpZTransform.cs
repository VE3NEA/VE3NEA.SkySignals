using System;
using MathNet.Numerics;


namespace VE3NEA
{
  /// <summary>
  /// Chirp-Z (Bluestein) transform — a "zoom FFT". Evaluates <c>M</c> uniformly-spaced samples of the DTFT,
  /// <c>S[k] = Σ_{n=0}^{N-1} x[n]·e^{-j2π(f0 + k·df)·n}</c>, k = 0…M−1, at an arbitrary start frequency
  /// <c>f0</c> and bin spacing <c>df</c> (both in cycles/sample). This resolves a narrow band at high
  /// resolution without the huge zero-padded FFT a plain DTFT-on-a-grid would need: the cost is one
  /// forward + one inverse FFT of length <c>L = next power of two ≥ N+M−1</c>, i.e. ~O((N+M)·log(N+M))
  /// instead of O(M·N).
  ///
  /// <para>Bluestein's identity <c>nk = (n²+k²−(k−n)²)/2</c> turns the transform into a convolution:
  /// <c>S[k] = w^{k²/2}·Σ_n (a[n]·w^{n²/2})·w^{−(k−n)²/2}</c> with <c>a[n] = x[n]·e^{-j2πf0·n}</c> and
  /// <c>w = e^{-j2π·df}</c>. The pre-/post-chirps and the FFT of the kernel <c>w^{−m²/2}</c> depend only on
  /// the geometry (N, M, f0, df), so they are precomputed once here and reused for every <see cref="Compute"/>.</para>
  /// </summary>
  public sealed class ChirpZTransform : IDisposable
  {
    private readonly int N;
    private readonly int M;
    private readonly int L;
    private readonly Fft<Complex32> Forward;
    private readonly Fft<Complex32> Inverse;
    private readonly Complex32[] Pre;     // a[n] multiplier incl. the chirp: e^{-j2πf0·n}·e^{-jπ·df·n²}, n = 0…N−1
    private readonly Complex32[] KernelHat;  // FFT of the Bluestein kernel v[m] = e^{+jπ·df·m²}
    private readonly Complex32[] Post;    // e^{-jπ·df·k²}, k = 0…M−1
    private readonly Complex32[] Scratch; // length-L work buffer


    /// <summary>Build the transform for a fixed geometry: <paramref name="n"/> input samples,
    /// <paramref name="m"/> output frequency bins starting at <paramref name="f0"/> (cycles/sample) and spaced
    /// <paramref name="df"/> apart. Allocates the FFTW plans and precomputes the chirp kernels.</summary>
    public ChirpZTransform(int n, int m, double f0, double df)
    {
      if (n < 1 || m < 1) throw new ArgumentException("ChirpZTransform requires n >= 1 and m >= 1.");
      N = n; M = m;
      int l = 1; while (l < n + m - 1) l <<= 1; L = l;

      Forward = new Fft<Complex32>(L, NativeFftw.FftwFlags.Estimate);
      Inverse = new Fft<Complex32>(L, NativeFftw.FftwFlags.Estimate, NativeFftw.FftwDirection.Backward);

      // pre-chirp a[n]·w^{n²/2} = e^{-j2πf0·n}·e^{-jπ·df·n²}
      Pre = new Complex32[N];
      for (int i = 0; i < N; i++)
      {
        double ang = -2.0 * Math.PI * f0 * i - Math.PI * df * (double)i * i;
        Pre[i] = new Complex32((float)Math.Cos(ang), (float)Math.Sin(ang));
      }

      // kernel v[m] = w^{−m²/2} = e^{+jπ·df·m²}, for m = −(N−1)…(M−1); negative indices wrap to the top of the
      // length-L buffer so the circular convolution reproduces the linear one (L ≥ N+M−1 keeps them disjoint).
      var v = new Complex32[L];
      for (int k = 0; k < M; k++) { double ang = Math.PI * df * (double)k * k; v[k] = new Complex32((float)Math.Cos(ang), (float)Math.Sin(ang)); }
      for (int k = 1; k < N; k++) { double ang = Math.PI * df * (double)k * k; v[L - k] = new Complex32((float)Math.Cos(ang), (float)Math.Sin(ang)); }
      Array.Copy(v, Forward.InputData, L);
      Forward.Execute();
      KernelHat = (Complex32[])Forward.OutputData.Clone();

      // post-chirp w^{k²/2} = e^{-jπ·df·k²}
      Post = new Complex32[M];
      for (int k = 0; k < M; k++)
      {
        double ang = -Math.PI * df * (double)k * k;
        Post[k] = new Complex32((float)Math.Cos(ang), (float)Math.Sin(ang));
      }

      Scratch = new Complex32[L];
    }


    /// <summary>Compute the M DTFT samples of the real sequence <paramref name="x"/> (length N) into
    /// <paramref name="result"/> (length ≥ M).</summary>
    public void Compute(ReadOnlySpan<double> x, Complex32[] result)
    {
      if (x.Length != N) throw new ArgumentException($"input length {x.Length} != configured N {N}.", nameof(x));
      if (result.Length < M) throw new ArgumentException($"result length {result.Length} < M {M}.", nameof(result));

      Array.Clear(Forward.InputData);
      for (int i = 0; i < N; i++) Forward.InputData[i] = Pre[i] * (float)x[i];
      Forward.Execute();

      for (int i = 0; i < L; i++) Scratch[i] = Forward.OutputData[i] * KernelHat[i];

      Array.Copy(Scratch, Inverse.InputData, L);
      Inverse.Execute();   // normalized inverse (1/L applied in Fft.Execute)

      for (int k = 0; k < M; k++) result[k] = Inverse.OutputData[k] * Post[k];
    }


    public void Dispose()
    {
      Forward.Dispose();
      Inverse.Dispose();
    }
  }
}
