using System;
using MathNet.Filtering.IIR;

namespace VE3NEA.SkyFM
{
  /// <summary>
  /// FM noise squelch, ported from SkyRoof (<c>SkyRoof\DSP\SoftSquelch.cs</c>) per the plan's reuse
  /// decision. Input is the discriminator output normalized to cycles/sample (disc Hz ÷ fs) at 48 kHz —
  /// the same scaling SkyRoof's <c>freqdem_create(1)</c> produces, so the stock thresholds carry over.
  /// The detector extracts the above-voice-band (&gt;3500 Hz) noise power of the discriminator — FM
  /// capture quiets it when a carrier is present — smooths it, and gates with hysteresis; audio is
  /// delayed so the gate leads it, then the open/closed gain is applied and the result band-passed to
  /// the voice band. This class supplies both the playback muting (in-place audio path, as in SkyRoof)
  /// and the per-sample gate/level tracks the <see cref="CarrierSegmenter"/> and the abstention policy
  /// consume (plan §5.2).
  ///
  /// <para>The IIR detector filters are fixed designs for fs = 48 kHz (scipy coefficients inherited from
  /// SkyRoof); the band-pass center/width scale with the actual rate.</para>
  /// </summary>
  public sealed unsafe class SoftSquelch : IDisposable
  {
    private const int FILTER_DELAY = 766;
    private const float OPEN_GAIN = 3f;
    private const float CLOSED_GAIN = 0.3f;

    private readonly OnlineIirFilter Hpf, Lpf;
    private NativeLiquidDsp.firfilt_rrrf* Bpf;
    private float[] DelayLine = new float[FILTER_DELAY + 1024];
    private float gain = 1;

    private readonly float openLevel, closeLevel;

    public bool Enabled = true;

    public SoftSquelch(FmDecodeOptions o)
    {
      openLevel = (float)o.SquelchOpenLevel;
      closeLevel = (float)o.SquelchCloseLevel;

      // b, a = scipy.signal.iirfilter(5, 3500, fs=48000, btype="high", ftype="cheby2", rs=40)
      double[] coefficients = [
        0.39422113, -1.86812319, 3.63871459, -3.63871459, 1.86812319, -0.39422113,
        1, -2.97879203, 3.91647098, -2.74144198, 1.01000506, -0.15540778f];

      Hpf = new OnlineIirFilter(coefficients);

      // b, a = scipy.signal.iirfilter(3, Wn=10, fs=48000, btype="low", ftype="bessel")
      coefficients = [
        2.23580506e-09, 6.70741518e-09, 6.70741518e-09, 2.23580506e-09,
        1, -2.99363411, 2.9872851, -0.99365097f];

      Lpf = new OnlineIirFilter(coefficients);

      CreateBandpassFilter(o.SampleRate);
    }

    private const float BANDWIDTH = 2700;
    private const float CENTER_FREQ = 1650;
    private const int STOPBAND_REJECTION_DB = 80;
    private const int FILTER_LENGTH = 601;
    private void CreateBandpassFilter(double fs)
    {
      // create lowpass filter
      float cutoff = BANDWIDTH / 2 / (float)fs;
      float centerFreq = CENTER_FREQ / (float)fs;
      var lowpassFilter = NativeLiquidDsp.firfilt_rrrf_create_kaiser(FILTER_LENGTH, cutoff, STOPBAND_REJECTION_DB, 0);

      // shift the frequency response to the center frequency
      var coeffs = NativeLiquidDsp.firfilt_rrrf_get_coefficients(lowpassFilter);
      Dsp.Mix(coeffs, FILTER_LENGTH, centerFreq);
      Bpf = NativeLiquidDsp.firfilt_rrrf_create(coeffs, FILTER_LENGTH);
      NativeLiquidDsp.firfilt_rrrf_destroy(lowpassFilter);
    }

    /// <summary>SkyRoof-compatible entry point: gate and band-pass <paramref name="data"/> in place.</summary>
    public void Process(float[] data) => Process(data, default, default);

    /// <summary>Gate and band-pass <paramref name="data"/> in place; optionally report the per-sample
    /// gate state (1 = open, carrier present) in <paramref name="gates"/> and the smoothed noise
    /// amplitude (cycles/sample) in <paramref name="levels"/>. The gate/level tracks are indexed on the
    /// input timeline: gates[i] is the detector's verdict computed at input sample i (the detector's own
    /// smoothing lag, ~tens of ms, is inherent; the audio path additionally lags by the delay line and
    /// the band-pass group delay). Both spans, when supplied, must be at least data.Length long.</summary>
    public void Process(Span<float> data, Span<byte> gates, Span<float> levels)
    {
      // ensure buffer size
      int count = data.Length;
      if (DelayLine.Length < FILTER_DELAY + count) Array.Resize(ref DelayLine, FILTER_DELAY + count);

      // push to delay line
      for (int i = 0; i < count; i++) DelayLine[FILTER_DELAY + i] = data[i];

      for (int i = 0; i < count; i++)
      {
        // extract noise power above 3500 Hz
        double value = Hpf.ProcessSample(data[i]);
        value *= value;

        // compute smoothed amplitude
        value = Lpf.ProcessSample(value);
        value = Math.Sqrt(Math.Max(1e-6, value));

        // threshold
        if (value < openLevel) gain = OPEN_GAIN; else if (value > closeLevel) gain = CLOSED_GAIN;

        if (!gates.IsEmpty) gates[i] = gain > 1 ? (byte)1 : (byte)0;
        if (!levels.IsEmpty) levels[i] = (float)value;

        // apply gain
        if (Enabled) data[i] = DelayLine[i] * gain;
      }

      // low-pass filter the result
      if (Enabled)
        fixed (float* pData = data)
          NativeLiquidDsp.firfilt_rrrf_execute_block(Bpf, pData, (uint)count, pData);

      // dump old samples
      Array.Copy(DelayLine, count, DelayLine, 0, FILTER_DELAY);
    }

    public void Dispose()
    {
      if (Bpf != null) NativeLiquidDsp.firfilt_rrrf_destroy(Bpf);
      Bpf = null;
    }
  }
}
