using MathNet.Numerics;
using System.Numerics;

namespace VE3NEA
{
  /// <summary>A collection of functions related to digital signal processing.</summary>
  public unsafe static class Dsp
  {
    /// <summary>The number of components in a complex value.</summary>
    /// <remarks>There are two floating point components in a complex value, 
    /// the real part and the imaginary part.</remarks>
    public const int COMPONENTS_IN_COMPLEX = 2;

    /// <summary>Generates the Blackman window.</summary>
    /// <param name="length">The window length.</param>
    /// <returns>The window coefficients.</returns>
    public static float[] BlackmanWindow(int length)
    {
      float[] result = new float[length];
      for (int i = 0; i < length; i++)
      {
        float x = (i + 1f) / (length + 1f);

        result[i] = (float)(
            0.42 
          - 0.50 * Math.Cos(2 * Math.PI * x) 
          + 0.08 * Math.Cos(4 * Math.PI * x)
          );
      }
      return result;
    }

    /// <summary>Generates the Blackman-Harris window.</summary>
    /// <param name="length">The window length.</param>
    /// <returns>The window coefficients.</returns>
    public static float[] BlackmanHarrisWindow(int length)
    {
      float[] result = new float[length];
      for (int i = 0; i < length; i++)
      {
        float x = (i + 1f) / (length + 1f);

        result[i] = (float)(
            0.35875
          - 0.48829 * Math.Cos(2 * Math.PI * x)
          + 0.14128 * Math.Cos(4 * Math.PI * x)
          - 0.01168 * Math.Cos(6 * Math.PI * x)
          );
      }
      return result;
    }

    /// <summary>Generates the Gaussian window.</summary>
    /// <param name="sigma">The standard deviation.</param>
    /// <param name="length">The window length.</param>
    /// <returns>The window coefficients.</returns>
    public static float[] GaussianWindow(double sigma, int length)
    {
      float[] result = new float[length];
      double mid = (length - 1) / 2d;
      sigma = Math.Max(sigma, 1e-30);

      for (int i = 0; i < length; i++)
      {
        double x = (i - mid) / (mid * sigma);
        result[i] = (float)Math.Exp(-0.5 * x * x);
      }

      return result;
    }

    /// <summary>Builds a symmetric Gaussian low-pass kernel of the given full width (in samples),
    /// truncated to ±3σ and normalized to unit DC gain. A width below ~1e-3 returns the identity
    /// kernel <c>{1}</c> (pass-through).</summary>
    /// <param name="widthSamples">The kernel full width in samples (σ = width / 2).</param>
    /// <returns>The normalized kernel coefficients.</returns>
    public static float[] GaussianLowpass(double widthSamples)
    {
      double sigma = widthSamples / 2.0;
      if (sigma < 1e-3) return new[] { 1f };
      int half = Math.Max(1, (int)Math.Round(3 * sigma));
      int m = 2 * half + 1;
      var h = new float[m];
      double sum = 0;
      for (int i = 0; i < m; i++) { double t = i - half; double v = Math.Exp(-0.5 * t * t / (sigma * sigma)); h[i] = (float)v; sum += v; }
      for (int i = 0; i < m; i++) h[i] /= (float)sum;
      return h;
    }

    /// <summary>Generates the Sinc filter kernel.</summary>
    /// <param name="Fc">The normalized cutoff frequency.</param>
    /// <param name="length">The kernel length.</param>
    /// <returns>The filter kernel.</returns>
    public static float[] Sinc(float Fc, int length)
    {
      int mid = (length-1) / 2;
      float[] result = new float[length];
      result[mid] = 1;
      for (int i = 1; i <= mid; i++)
      {
        float x = (float)(2f * Math.PI * Fc * i);
        result[mid - i] = result[mid + i] = (float)(Math.Sin(x) / x);
      }
      return result;
    }

    /// <summary>Generates the Blackman Sinc filter kernel.</summary>
    /// <param name="Fc">The normalized cutoff frequency.</param>
    /// <param name="length">The kernel length.</param>
    /// <returns>The filter kernel.</returns>
    public static float[] BlackmanSincKernel(double Fc, int length)
    {
      float[] result = BlackmanWindow(length);
      float[] sinc = Sinc((float)Fc, length);
      for (int i = 0; i < length; i++) result[i] *= sinc[i];
      Normalize(result);
      return result;
    }

    /// <summary>Generates the Blackman-Harris Sinc filter kernel.</summary>
    /// <param name="Fc">The normalized cutoff frequency.</param>
    /// <param name="length">The kernel length.</param>
    /// <returns>The filter kernel.</returns>
    internal static float[] BlackmanHarrisSincKernel(double Fc, int length)
    {
      float[] result = BlackmanHarrisWindow(length);
      float[] sinc = Sinc((float)Fc, length);
      for (int i = 0; i < length; i++) result[i] *= sinc[i];
      Normalize(result);
      return result;
    }

    /// <summary>Normalizes the specified array of floating point data to unity sum.</summary>
    /// <param name="data">The data.</param>
    public static void Normalize(float[] data)
    {
      float sum = 0;
      foreach (float d in data) sum += d;
      for (int i = 0; i < data.Length; i++) data[i] /= sum;
    }

    /// <summary>Normalizes a window to unity sum.</summary>
    /// <param name="window">Input window coefficients.</param>
    /// <returns>Normalized window with sum(w) = 1.</returns>
    public static float[] NormalizeWindow(float[] window)
    {
      float windowSum = window.Sum();
      return window.Select(w => w / windowSum).ToArray();
    }

    /// <summary>Computes the effective noise bandwidth of a window.</summary>
    /// <param name="window">Window coefficients.</param>
    /// <returns>Effective noise bandwidth in bins: sum(w²) / sum(w)².</returns>
    public static float ComputeNoiseBandwidth(float[] window)
    {
      float windowSum = window.Sum();
      float windowSumSq = window.Sum(w => w * w);
      return windowSumSq / (windowSum * windowSum);
    }

    /// <summary>Converts power ratio to decibels.</summary>
    /// <param name="x">The power ratio.</param>
    /// <returns>The ratio in decibels.</returns>
    public static float ToDb(float x)
    {
      return 10f * (float)Math.Log10(x);
    }

    /// <summary>Converts voltage ratio to decibels.</summary>
    /// <param name="x">The voltage ratio.</param>
    /// <returns>The ratio in decibels.</returns>
    public static float ToDb2(float x)
    {
      return 2 * ToDb(x);
    }

    /// <summary>  Converts the ratio in decibels to voltage ratio.</summary>
    /// <param name="x">The ratio in decibels.</param>
    /// <returns>The voltage ratio.</returns>
    public static float FromDb2(float x)
    {
      return (float)Math.Pow(10, x / 20f);
    }

    /// <summary>  Converts the ratio in decibels to power ratio.</summary>
    /// <param name="x">The ratio in decibels.</param>
    /// <returns>The power ratio.</returns>
    public static float FromDb(float x)
    {
      return FromDb2(2 * x);
    }

    /// <summary>Swaps the I and Q channels in the array of data.</summary>
    /// <param name="buffer">The buffer.</param>
    /// <param name="floatOffset">The offset to the first floating point value to convert.</param>
    /// <param name="complexCount">The number of complex values to convert.</param>    
    public static void SwapIQ(float[] buffer, int floatOffset, int complexCount)
    {
      fixed (float* pBuf = buffer)
      {
        Complex32* pDst = (Complex32*)(pBuf + floatOffset);
        for (int sample = 0; sample < complexCount; sample++, pDst++)
          *pDst = new Complex32((*pDst).Imaginary, (*pDst).Real);
      }
    }

    /// <summary>Performs in-place mixing of the specified data with a complex sinusoid.</summary>
    /// <param name="data">The data.</param>
    /// <param name="frequency">The frequency.</param>
    /// <param name="phase">The starting phase.</param>    
    public static void Mix(Complex32* pData, int count, double frequency, double phase = 0)
    {
      Complex phasor = new Complex(Math.Cos(phase), Math.Sin(phase));
      double dPhase = 2 * Math.PI * frequency;
      Complex dPhasor = new Complex(Math.Cos(dPhase), Math.Sin(dPhase));

      for (int i = 0; i < count; i++)
      {
        pData[i] *= new Complex32((float)phasor.Real, (float)phasor.Imaginary);
        phasor *= dPhasor;
      }
    }

    public static void Mix(Complex32[] data, double frequency, double phase = 0)
    {
      fixed (Complex32* pData = data) Mix(pData, data.Length, frequency, phase);
    }

    public static void Mix(float* pData, int count, double frequency, double phase = 0)
    {
      Complex phasor = new Complex(Math.Cos(phase), Math.Sin(phase));
      double dPhase = 2 * Math.PI * frequency;
      Complex dPhasor = new Complex(Math.Cos(dPhase), Math.Sin(dPhase));

      for (int i = 0; i < count; i++)
      {
        pData[i] = (float)(pData[i] * phasor.Real);
        phasor *= dPhasor;
      }
    }


    /// <summary>Unpacks complex values from the array of floats.</summary>
    /// <param name="source">The floating point values.</param>
    /// <returns>The complex values.</returns>
    public static Complex32[] FloatToComplex32(float[] source)
    {
      Complex32[] result = new Complex32[source.Length];
      for (int i = 0; i < source.Length; i++) result[i] = source[i];
      return result;
    }

    //Diophantine algorithm
    //https://www.daniweb.com/software-development/python/code/223956
    /// <summary>Finds a rational approximation
    /// of a floating point value.</summary>
    /// <param name="ratio">The floating point value.</param>
    /// <param name="maxError">The maximum error.</param>
    /// <returns>The approximating ratio, L/M.</returns>
    /// <remarks>Finds the 
    /// <a href="https://en.wikipedia.org/wiki/Diophantine_approximation">Diophantine approximation</a> 
    /// to the given floating point value. Useful for the design of low complexity 
    /// <a href="https://dspguru.com/dsp/faqs/multirate/resampling/">resamplers</a> 
    /// when the exact output rate is not required.
    /// </remarks>
    public static (int L, int M) ApproximateRatio(double ratio, double maxError)
    {
      int M, oldM, newM, L, oldL, newL;
      int quot;
      double rest;

      oldL = 1;
      L = (int)ratio;
      oldM = 0;
      M = 1;
      rest = ratio;
      quot = L;

      while (Math.Abs(L / (float)M - ratio) / ratio > maxError)
      {
        rest = 1 / (rest - quot);
        quot = (int)rest;

        newL = quot * L + oldL;
        oldL = L;
        L = newL;

        newM = quot * M + oldM;
        oldM = M;
        M = newM;
      }
    
    return (L, M);
    }

    //float[] to Complex32[]
    /// <summary>Unpacks complex values from the array of floats.</summary>
    /// <param name="source">The source array.</param>
    /// <param name="srcOffset">The offset to the first value in the source array.</param>
    /// <param name="srcStride">The stride in the source array.</param>
    /// <param name="destination">The destination array.</param>
    /// <param name="dstOffset">The offset to the first value in the destination array.</param>
    /// <param name="complexCount">The number of the complex values to output.</param>
    public static void StridedToComplex(float[] source, int srcOffset, int srcStride,
      Complex32[] destination, int dstOffset, int complexCount)
    {
      fixed (float* pSrc = source)
      fixed (Complex32* pDst = destination)
      {
        float* src = pSrc + srcOffset;
        Complex32* dst = pDst + dstOffset;
        for (int i = 0; i < complexCount; i++, src += srcStride, dst++)
          *dst = *(Complex32*)src;
      }
    }

    //float[] to float[]
    /// <summary>Convert data format.</summary>
    /// <param name="source">The source array.</param>
    /// <param name="srcOffset">The offset to the first value in the source array.</param>
    /// <param name="srcStride">The stride in the source array.</param>
    /// <param name="destination">The destination array.</param>
    /// <param name="dstOffset">The offset to the first value in the destination array.</param>
    /// <param name="complexCount">The number of the complex values to output.</param>
    public static void StridedToComplex(float[] source, int srcOffset, int srcStride,
     float[] destination, int dstOffset, int complexCount)
    {
      fixed (float* pSrc = source)
      fixed (float* pDst = destination)
      {
        float* src = pSrc + srcOffset;
        float* dst = pDst + dstOffset;
        for (int i = 0; i < complexCount; i++, src += srcStride, dst += Dsp.COMPONENTS_IN_COMPLEX)
        {
          *dst = *src;
          *(dst + 1) = *(src + 1);
        }
      }
    }

    /// <summary>Convert data format.</summary>
    /// <param name="source">The source array.</param>
    /// <param name="srcOffset">The offset to the first value in the source array.</param>
    /// <param name="destination">The destination array.</param>
    /// <param name="dstOffset">The offset to the first value in the destination array.</param>
    /// <param name="dstStride">The stride in the destination array.</param>
    /// <param name="complexCount">The number of the complex values to output.</param>
    public static void ComplexToStrided(Complex32[] source, int srcOffset,
      float[] destination, int dstOffset, int dstStride, int complexCount)
    {
      fixed (Complex32* pSrc = source)
      fixed (float* pDst = destination)
      {
        Complex32* src = pSrc + srcOffset;
        float* dst = pDst + dstOffset;
        for (int i = 0; i < complexCount; i++, src++, dst += dstStride)
          *(Complex32*)dst = *src;
      }
    }

    /// <summary>Convert data format.</summary>
    /// <param name="source">The source array.</param>
    /// <param name="srcOffset">The offset to the first value in the source array.</param>
    /// <param name="srcStride">The stride in the source array.</param>
    /// <param name="destination">The destination array.</param>
    /// <param name="dstOffset">The offset to the first value in the destination array.</param>
    /// <param name="count">The number of the values to output.</param>
    public static void StridedToFloat(float[] source, int srcOffset, int srcStride,
      float[] destination, int dstOffset, int count)
    {
      fixed (float* pSrc = source)
      fixed (float* pDst = destination)
      {
        float* src = pSrc + srcOffset;
        float* dst = pDst + dstOffset;
        for (int i = 0; i < count; i++, src += srcStride, dst++) *dst = *src;
      }
    }

    /// <summary>Convert data format.</summary>
    /// <param name="source">The source array.</param>
    /// <param name="srcOffset">The offset to the first value in the source array.</param>
    /// <param name="destination">The destination array.</param>
    /// <param name="dstOffset">The offset to the first value in the destination array.</param>
    /// <param name="dstStride">The stride in the destination array.</param>
    /// <param name="count">The number of the values to output.</param>
    public static void FloatToStrided(float[] source, int srcOffset,
      float[] destination, int dstOffset, int dstStride, int count)
    {
      fixed (float* pSrc = source)
      fixed (float* pDst = destination)
      {
        float* src = pSrc + srcOffset;
        float* dst = pDst + dstOffset;
        for (int i = 0; i < count; i++, src++, dst += dstStride) *dst = *src;
      }
    }

    /// <summary>Converts an array of floating point values to text, one value per line.</summary>
    /// <param name="array">The array of values.</param>
    /// <returns>The text.</returns>
    public static string ArrayToString(float[] array)
    {
      string s = "";
      for (int i = 0; i < array.Length; i++) s += $"{i}  {array[i]}" + Environment.NewLine;
      return s;
    }

    internal static float Clamp(float value)
    {
      return Math.Max(-1, Math.Min(1, value));
    }


    /// <summary>Returns the median of the values (the upper-middle element for even counts).
    /// Does not mutate the input; returns 0 for an empty sequence.</summary>
    /// <param name="x">The values (e.g. a <c>float[]</c> or <c>List&lt;float&gt;</c>).</param>
    public static double Median(IReadOnlyList<float> x)
    {
      if (x.Count == 0) return 0;
      var copy = new float[x.Count];
      for (int i = 0; i < x.Count; i++) copy[i] = x[i];
      Array.Sort(copy);
      return copy[copy.Length / 2];
    }

    /// <summary>The Gaussian Q-function, Q(x) = 0.5·erfc(x/√2).</summary>
    public static double GaussianQ(double x) => 0.5 * Erfc(x / Math.Sqrt(2.0));

    /// <summary>Complementary error function via the Abramowitz–Stegun 7.1.26 approximation (max error ~1.5e-7).</summary>
    public static double Erfc(double x)
    {
      double z = Math.Abs(x);
      double t = 1.0 / (1.0 + 0.5 * z);
      double ans = t * Math.Exp(-z * z - 1.26551223 + t * (1.00002368 + t * (0.37409196 +
        t * (0.09678418 + t * (-0.18628806 + t * (0.27886807 + t * (-1.13520398 +
        t * (1.48851587 + t * (-0.82215223 + t * 0.17087277)))))))));
      return x >= 0 ? ans : 2.0 - ans;
    }

    /// <summary>4-point cubic (Catmull-Rom-style Lagrange) interpolation of <paramref name="y"/> at the
    /// real index <paramref name="pos"/>. Taps outside the array are treated as 0 (see <see cref="SampleOrZero"/>).</summary>
    public static float Interp(float[] y, double pos)
    {
      int i = (int)Math.Floor(pos);
      double mu = pos - i;
      float ym1 = SampleOrZero(y, i - 1), y0 = SampleOrZero(y, i), y1 = SampleOrZero(y, i + 1), y2 = SampleOrZero(y, i + 2);
      // cubic Lagrange
      double c0 = y0;
      double c1 = y1 - (1.0 / 3) * ym1 - 0.5 * y0 - (1.0 / 6) * y2;
      double c2 = 0.5 * (ym1 + y1) - y0;
      double c3 = (1.0 / 6) * (y2 - ym1) + 0.5 * (y0 - y1);
      return (float)(((c3 * mu + c2) * mu + c1) * mu + c0);
    }

    /// <summary>Returns <c>y[i]</c>, or 0 when <paramref name="i"/> is outside the array bounds.</summary>
    public static float SampleOrZero(float[] y, int i) => (uint)i < (uint)y.Length ? y[i] : 0f;

    public static double RayleighPdf(double x, double sigma)
    {
      if (x <= 0) return 0;
      double s2 = sigma * sigma;
      return x / s2 * Math.Exp(-x * x / (2 * s2));
    }

    public static double RicianPdf(double x, double nu, double sigma)
    {
      if (x <= 0) return 0;
      //double s2 = sigma * sigma;
      //return x / s2 * Math.Exp(-(x * x + nu * nu) / (2 * s2)) * BesselI0(x * nu / s2);

      double logRician = Math.Log(x) - 2 * Math.Log(sigma) - (x * x + nu * nu) / (2 * sigma * sigma) + LogBesselI0(x * nu / (sigma * sigma));
      return Math.Exp(logRician);
    }

    // modified Bessel function of the first kind, order zero
    // polynomial approximation from Numerical Recipes, accurate to 1e-7
    public static double BesselI0(double x)
    {
      double ax = Math.Abs(x);
      if (ax < 3.75)
      {
        double t = x / 3.75;
        double t2 = t * t;
        return 1 + t2 * (3.5156229 + t2 * (3.0899424 + t2 * (1.2067492
          + t2 * (0.2659732 + t2 * (0.0360768 + t2 * 0.0045813)))));
      }
      double u = 3.75 / ax;
      return Math.Exp(ax) / Math.Sqrt(ax) * (0.39894228 + u * (0.01328592
        + u * (0.00225319 + u * (-0.00157565 + u * (0.00916281
        + u * (-0.02057706 + u * (0.02635537 + u * (-0.01647633 + u * 0.00392377))))))));
    }

    // log I_n(x) for integer order n ≥ 0
    // series for x ≤ 5(n+1); asymptotic with first-order correction for larger x
    public static double LogBesselIn(int n, double x)
    {
      if (n == 0) return LogBesselI0(x);
      if (x <= 0) return double.NegativeInfinity;
      if (x > 5.0 * (n + 1))
        // I_n(x) ~ e^x/sqrt(2πx) · (1 - (4n²-1)/(8x) + ...); first correction subtracts (4n²-1)/(8x)
        return x - 0.5 * Math.Log(2 * Math.PI * x) - (4.0 * n * n - 1.0) / (8.0 * x);
      // series: I_n(x) = (x/2)^n / n! · Σ_{k=0}^∞ (x²/4)^k / (k! · (n+k)!/n!)
      double halfXsq = x * x / 4.0;
      double term = 1.0, sum = 1.0;
      for (int k = 1; k <= 400; k++) {
        term *= halfXsq / (k * (n + k));
        sum += term;
        if (term < 1e-14 * sum) break;
      }
      double logNFact = 0;
      for (int k = 2; k <= n; k++) logNFact += Math.Log(k);
      return n * Math.Log(x / 2.0) - logNFact + Math.Log(sum);
    }

    internal static double LogBesselI0(double x)
    {
      double logI0;

      if (x < 3.75)
      {
        // A&S 9.8.1: t = (x/3.75)², poly in t
        double t = x / 3.75; 
        t *= t;
        logI0 = Math.Log(1.0 + t * (3.5156229 + t * (3.0899424 + t * (1.2067492
                + t * (0.2659732 + t * (0.0360768 + t * 0.0045813))))));
      }
      else
      {
        // A&S 9.8.2: I₀_scaled(x) = poly(3.75/x)/sqrt(x); logI0 = x - 0.5·ln(x) + ln(poly)
        double t = 3.75 / x;
        double poly = 0.39894228 + t * (0.01328592 + t * (0.00225319 + t * (-0.00157565
                    + t * (0.00916281 + t * (-0.02057706 + t * (0.02635537
                    + t * (-0.01647633 + t * 0.00392377)))))));
        logI0 = x - 0.5 * Math.Log(x) + Math.Log(poly);
      }

      return logI0;
    }
  }
}
