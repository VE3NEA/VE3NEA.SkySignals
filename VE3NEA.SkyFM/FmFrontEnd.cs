using System;
using System.Collections.Generic;
using MathNet.Numerics;

namespace VE3NEA.SkyFM
{
  /// <summary>
  /// Streaming FM voice front-end (plan §5.1–5.2): I/Q → channel FIR → discriminator → impulse blanker
  /// (<see cref="FmStreamingDiscriminator"/>) → [squelch detector tap → <see cref="CarrierSegmenter"/>] →
  /// voice bandpass → decimation to 16 kHz mono. Block-in / block-out with bounded state; the voice
  /// output is on the zero-phase timeline (the bandpass group delay is absorbed, <see cref="Flush"/>
  /// drains it), so segment times index directly into the voice stream.
  ///
  /// <para>The squelch detector taps the BROADBAND discriminator, before the voice bandpass strips the
  /// above-voice noise the carrier-quieting metric relies on (plan §5.2). Voice samples are instantaneous
  /// frequency in Hz — deviation units; the caller normalizes for playback/export.</para>
  /// </summary>
  public sealed class FmFrontEnd : IDisposable
  {
    private const int VoiceBandpassTaps = 1025;   // clean ~250 Hz high-pass skirt: kills CTCSS + Doppler DC

    private readonly double fs;
    private readonly int decim;
    private readonly int frameLen;

    private readonly FmStreamingDiscriminator disc;
    private readonly SoftSquelch squelch;
    private readonly CarrierSegmenter segmenter;
    private readonly StreamingFir voiceFir;
    private readonly int voiceDelay;
    private int voiceRamp;       // leading causal FIR outputs to drop (the zero-phase 'same' shift)
    private long voiceIndex;     // absolute disc-timeline index of the next post-ramp FIR output
    private readonly List<float> squelchLevelDb = new();

    // reused per-call buffers
    private float[] firIn = new float[8192];
    private float[] firOut = new float[8192];
    private float[] normBuf = new float[8192];
    private byte[] gateBuf = new byte[8192];
    private float[] levelBuf = new float[8192];
    private float[] outBuf = new float[8192];
    private int outLen;

    /// <summary>Output audio sample rate (Hz).</summary>
    public int OutputSampleRate { get; }

    /// <summary>Transmissions finalized so far (grows as the stream advances; complete after
    /// <see cref="Flush"/>).</summary>
    public IReadOnlyList<FmTransmission> Transmissions => segmenter.Transmissions;

    /// <summary>Smoothed squelch noise level (dB re 1 cycle/sample), one value per
    /// <see cref="FmDecodeOptions.SquelchFrameS"/> frame — the quieting-depth confidence input of
    /// plan §5.2.</summary>
    public IReadOnlyList<float> SquelchLevelDb => squelchLevelDb;

    /// <summary>Earliest time (s) a still-pending transmission might need audio from (see
    /// <see cref="CarrierSegmenter.PendingStartSeconds"/>).</summary>
    public double PendingStartSeconds => segmenter.PendingStartSeconds;

    public FmFrontEnd(FmDecodeOptions o)
    {
      fs = o.SampleRate;
      OutputSampleRate = o.OutputSampleRate;
      decim = (int)Math.Round(fs / o.OutputSampleRate);
      if (decim < 1 || fs != decim * (double)o.OutputSampleRate)
        throw new ArgumentException($"sample rate {fs} is not an integer multiple of {o.OutputSampleRate}");
      frameLen = Math.Max(1, (int)Math.Round(o.SquelchFrameS * fs));

      disc = new FmStreamingDiscriminator(o);
      squelch = new SoftSquelch(o) { Enabled = false };   // detector only: gates + levels, audio untouched
      segmenter = new CarrierSegmenter(o);
      voiceFir = new StreamingFir(VoiceBandpassKernel(o.VoiceLowHz, o.VoiceHighHz, fs));
      voiceDelay = voiceFir.GroupDelay;
      voiceRamp = voiceDelay;
    }

    /// <summary>A cosine-modulated BlackmanSinc bandpass over the voice band. Many taps so the low-edge
    /// skirt is clean enough to suppress the DC Doppler term and the 67 Hz CTCSS.</summary>
    private static float[] VoiceBandpassKernel(double lo, double hi, double fs)
    {
      double half = (hi - lo) / 2, f0 = (lo + hi) / 2;
      float[] lp = Dsp.BlackmanSincKernel(half / fs, VoiceBandpassTaps);
      var bp = new float[lp.Length];
      int center = lp.Length / 2;
      double w0 = 2 * Math.PI * f0 / fs;
      for (int i = 0; i < lp.Length; i++) bp[i] = 2f * lp[i] * (float)Math.Cos(w0 * (i - center));
      return bp;
    }

    /// <summary>Feed the next IQ block; returns the 16 kHz voice samples (Hz units) finalized by it.
    /// The span is valid until the next call.</summary>
    public ReadOnlySpan<float> Process(ReadOnlySpan<Complex32> iq)
    {
      outLen = 0;
      HandleDisc(disc.Process(iq));
      return new ReadOnlySpan<float>(outBuf, 0, outLen);
    }

    /// <summary>End of stream: drain the discriminator and the voice bandpass, finalize segmentation.
    /// Call once after the last <see cref="Process"/>.</summary>
    public ReadOnlySpan<float> Flush()
    {
      outLen = 0;
      HandleDisc(disc.Flush());

      // drain the causal voice FIR so the zero-phase timeline is complete
      long total = disc.EmittedCount;
      if (voiceDelay > 0)
      {
        var zeros = new float[voiceDelay];
        EnsureBuffers(voiceDelay);
        voiceFir.Process(zeros, firOut);
        EmitVoice(voiceDelay, total);
      }

      segmenter.Flush();
      return new ReadOnlySpan<float>(outBuf, 0, outLen);
    }

    public void Dispose()
    {
      disc.Dispose();
      squelch.Dispose();
      voiceFir.Dispose();
    }


    // ----------------------------------------------------------------------------------------------------
    //                                          per-block pipeline
    // ----------------------------------------------------------------------------------------------------

    /// <summary>Run one finalized discriminator block through the squelch/segmenter tap and the voice
    /// bandpass + decimator.</summary>
    private void HandleDisc(ReadOnlySpan<double> d)
    {
      int n = d.Length;
      if (n == 0) return;
      EnsureBuffers(n);

      // squelch detector on the broadband discriminator, normalized to cycles/sample (the SoftSquelch
      // input scale); the per-frame level track feeds the abstention policy later
      float inv = (float)(1.0 / fs);
      for (int i = 0; i < n; i++) { firIn[i] = (float)d[i]; normBuf[i] = firIn[i] * inv; }
      squelch.Process(normBuf.AsSpan(0, n), gateBuf.AsSpan(0, n), levelBuf.AsSpan(0, n));
      segmenter.Process(gateBuf.AsSpan(0, n), levelBuf.AsSpan(0, n));

      long firstIndex = disc.EmittedCount - n;   // absolute index of d[0]
      for (int i = 0; i < n; i++)
        if ((firstIndex + i) % frameLen == 0)
          squelchLevelDb.Add((float)(20.0 * Math.Log10(Math.Max(levelBuf[i], 1e-9f))));

      // voice bandpass; drop the leading group-delay ramp so output index == zero-phase disc index
      voiceFir.Process(firIn.AsSpan(0, n), firOut);
      EmitVoice(n, long.MaxValue);
    }

    /// <summary>Emit decimated voice from firOut[0..n), skipping the start-up ramp and stopping at the
    /// end of the true disc timeline (<paramref name="limit"/>, for the zero-padded flush tail).</summary>
    private void EmitVoice(int n, long limit)
    {
      int skip = Math.Min(voiceRamp, n);
      voiceRamp -= skip;
      for (int i = skip; i < n && voiceIndex < limit; i++, voiceIndex++)
        if (voiceIndex % decim == 0)
        {
          if (outLen == outBuf.Length) Array.Resize(ref outBuf, outBuf.Length * 2);
          outBuf[outLen++] = firOut[i];
        }
    }

    private void EnsureBuffers(int n)
    {
      if (firIn.Length < n) firIn = new float[n];
      if (firOut.Length < n) firOut = new float[n];
      if (normBuf.Length < n) normBuf = new float[n];
      if (gateBuf.Length < n) gateBuf = new byte[n];
      if (levelBuf.Length < n) levelBuf = new float[n];
    }
  }
}
