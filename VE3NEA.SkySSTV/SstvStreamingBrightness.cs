using System;
using MathNet.Numerics;

namespace VE3NEA.SkySSTV
{
  /// <summary>
  /// Streaming form of the Stage-3 brightness path (plan §1.4/§6.1, §7 P7.5(a)): mix the real audio
  /// down by the 1900 Hz center (an NCO) so the video sits at baseband and its mirror at −3800 Hz,
  /// complex low-pass to <see cref="SstvDecodeOptions.BrightnessBwHz"/> (which also rejects the DC
  /// Doppler term the mix pushes to −1900), then instantaneous frequency + 1900 — block-in / block-out
  /// with bounded state. The batch <see cref="SstvDecoder.Brightness"/> is a thin wrapper over this
  /// stage — the causal FIR's group delay is absorbed here, and <see cref="Flush"/> drains the delay
  /// line at end-of-stream. The NCO phase is an accumulated, wrapped recurrence, so precision holds
  /// over a 10+ min pass (retro N).
  /// </summary>
  internal sealed class SstvStreamingBrightness : IDisposable
  {
    private readonly double fs;
    private readonly double w;                 // subcarrier-center phase step per sample

    private readonly StreamingFirComplex fir;
    private readonly int groupDelay;
    private int rampRemaining;                 // leading causal FIR outputs to drop (the batch 'same' shift)
    private Complex32[] mixBuf = new Complex32[8192];
    private Complex32[] firBuf = new Complex32[8192];

    // instantaneous-frequency state
    private double ph;                         // NCO phase (wrapped)
    private Complex32 prevBb;
    private bool havePrev;
    private bool emittedFirst;                 // brightness[0] (= brightness[1], the batch edge case)

    // output accumulator (reused per call)
    private double[] outBuf = new double[8192];
    private int outLen;

    /// <summary>Total finalized samples emitted so far — the absolute batch-timeline index of the next
    /// output sample.</summary>
    public long EmittedCount { get; private set; }

    public SstvStreamingBrightness(SstvDecodeOptions o)
    {
      fs = o.SampleRate;
      w = 2 * Math.PI * SstvTones.Center / fs;
      float[] h = global::VE3NEA.Dsp.BlackmanSincKernel(o.BrightnessBwHz / fs,
        SstvDecoder.KernelTaps(o.BrightnessBwHz, fs));
      fir = new StreamingFirComplex(h);
      groupDelay = fir.GroupDelay;
      rampRemaining = groupDelay;
    }

    /// <summary>Feed the next disc block; returns the brightness samples (Hz) finalized by it
    /// (batch-timeline order). The span is valid until the next call.</summary>
    public ReadOnlySpan<double> Process(ReadOnlySpan<double> disc)
    {
      outLen = 0;
      if (disc.IsEmpty) return ReadOnlySpan<double>.Empty;

      if (mixBuf.Length < disc.Length)
      {
        mixBuf = new Complex32[disc.Length];
        firBuf = new Complex32[disc.Length];
      }
      for (int i = 0; i < disc.Length; i++)
      {
        mixBuf[i] = new Complex32((float)(disc[i] * Math.Cos(ph)), (float)(disc[i] * Math.Sin(ph)));
        ph -= w; if (ph < -Math.PI) ph += 2 * Math.PI;
      }
      fir.Process(mixBuf.AsSpan(0, disc.Length), firBuf);
      int skip = Math.Min(rampRemaining, disc.Length);
      rampRemaining -= skip;
      for (int i = skip; i < disc.Length; i++) InstFreq(firBuf[i]);

      return new ReadOnlySpan<double>(outBuf, 0, outLen);
    }

    /// <summary>End of stream: drain the FIR delay line (the batch tail flush). Call once after the last
    /// <see cref="Process"/>.</summary>
    public ReadOnlySpan<double> Flush()
    {
      outLen = 0;
      if (groupDelay > 0)
      {
        var zeros = new Complex32[groupDelay];
        if (firBuf.Length < groupDelay) firBuf = new Complex32[groupDelay];
        fir.Process(zeros, firBuf);
        int skip = Math.Min(rampRemaining, groupDelay);
        rampRemaining -= skip;
        for (int i = skip; i < groupDelay; i++) InstFreq(firBuf[i]);
      }
      return new ReadOnlySpan<double>(outBuf, 0, outLen);
    }

    public void Dispose() => fir.Dispose();

    /// <summary>Instantaneous frequency of the baseband pair + the 1900 Hz center. The batch edge case
    /// brightness[0] = brightness[1] is reproduced by emitting the first computed value twice.</summary>
    private void InstFreq(Complex32 bb)
    {
      if (!havePrev) { prevBb = bb; havePrev = true; return; }

      double re = (double)bb.Real * prevBb.Real + (double)bb.Imaginary * prevBb.Imaginary;
      double im = (double)bb.Imaginary * prevBb.Real - (double)bb.Real * prevBb.Imaginary;
      double b = Math.Atan2(im, re) * fs / (2 * Math.PI) + SstvTones.Center;
      prevBb = bb;

      if (!emittedFirst) { emittedFirst = true; Emit(b); }
      Emit(b);
    }

    private void Emit(double brightness)
    {
      if (outLen == outBuf.Length) Array.Resize(ref outBuf, outBuf.Length * 2);
      outBuf[outLen++] = brightness;
      EmittedCount++;
    }
  }
}
