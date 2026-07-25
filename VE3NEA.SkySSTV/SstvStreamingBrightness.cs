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
  ///
  /// The mix feeds TWO low-pass branches (§6.3): the narrow <see cref="SstvDecodeOptions.BrightnessBwHz"/>
  /// and the wide <see cref="SstvDecodeOptions.BrightnessWideBwHz"/>, from which the reconstruction
  /// blends per line against the measured noise. Both branches use the same tap count, so their group
  /// delays are equal and the two streams stay sample-aligned on the batch timeline.
  /// </summary>
  internal sealed class SstvStreamingBrightness : IDisposable
  {
    private readonly double w;                 // subcarrier-center phase step per sample

    private readonly Branch narrow;
    private readonly Branch? wide;             // null when the adaptive branch is disabled
    private readonly int groupDelay;
    private int rampRemaining;                 // leading causal FIR outputs to drop (the batch 'same' shift)
    private Complex32[] mixBuf = new Complex32[8192];

    private double ph;                         // NCO phase (wrapped)

    /// <summary>Total finalized samples emitted so far — the absolute batch-timeline index of the next
    /// output sample.</summary>
    public long EmittedCount => narrow.EmittedCount;

    /// <summary>The wide branch's samples for the span the last <see cref="Process"/>/<see cref="Flush"/>
    /// returned, same length and timeline. Falls back to the narrow branch when adaptation is off.</summary>
    public ReadOnlySpan<double> Wide => (wide ?? narrow).Out;

    public SstvStreamingBrightness(SstvDecodeOptions o)
    {
      double fs = o.SampleRate;
      w = 2 * Math.PI * SstvTones.Center / fs;

      // the narrow cutoff sets the tap count; the wide branch reuses it (a longer-than-needed kernel
      // only sharpens its skirt) so both branches share one group delay
      int taps = SstvDecoder.KernelTaps(o.BrightnessBwHz, fs);
      narrow = new Branch(o.BrightnessBwHz, fs, taps);
      if (o.BrightnessWideBwHz > o.BrightnessBwHz) wide = new Branch(o.BrightnessWideBwHz, fs, taps);

      groupDelay = narrow.GroupDelay;
      rampRemaining = groupDelay;
    }

    /// <summary>Feed the next disc block; returns the narrow branch's brightness samples (Hz) finalized
    /// by it (batch-timeline order), with the wide branch's on <see cref="Wide"/>. Both spans are valid
    /// until the next call.</summary>
    public ReadOnlySpan<double> Process(ReadOnlySpan<double> disc)
    {
      if (disc.IsEmpty)
      {
        narrow.Reset();
        wide?.Reset();
        return ReadOnlySpan<double>.Empty;
      }

      if (mixBuf.Length < disc.Length) mixBuf = new Complex32[disc.Length];
      for (int i = 0; i < disc.Length; i++)
      {
        mixBuf[i] = new Complex32((float)(disc[i] * Math.Cos(ph)), (float)(disc[i] * Math.Sin(ph)));
        ph -= w; if (ph < -Math.PI) ph += 2 * Math.PI;
      }

      int skip = Math.Min(rampRemaining, disc.Length);
      rampRemaining -= skip;
      var mix = mixBuf.AsSpan(0, disc.Length);
      narrow.Process(mix, skip);
      wide?.Process(mix, skip);
      return narrow.Out;
    }

    /// <summary>End of stream: drain the FIR delay lines (the batch tail flush). Call once after the last
    /// <see cref="Process"/>.</summary>
    public ReadOnlySpan<double> Flush()
    {
      narrow.Reset();
      wide?.Reset();
      if (groupDelay > 0)
      {
        var zeros = new Complex32[groupDelay];
        int skip = Math.Min(rampRemaining, groupDelay);
        rampRemaining -= skip;
        narrow.Process(zeros, skip);
        wide?.Process(zeros, skip);
      }
      return narrow.Out;
    }

    public void Dispose()
    {
      narrow.Dispose();
      wide?.Dispose();
    }


    // ----------------------------------------------------------------------------------------------------
    //                                       one low-pass branch
    // ----------------------------------------------------------------------------------------------------


    /// <summary>One complex low-pass + instantaneous-frequency chain over the shared mixer output.</summary>
    private sealed class Branch : IDisposable
    {
      private readonly double fs;
      private readonly StreamingFirComplex fir;
      private Complex32[] firBuf = new Complex32[8192];

      // instantaneous-frequency state
      private Complex32 prevBb;
      private bool havePrev;
      private bool emittedFirst;               // brightness[0] (= brightness[1], the batch edge case)

      // output accumulator (reused per call)
      private double[] outBuf = new double[8192];
      private int outLen;

      public long EmittedCount { get; private set; }
      public int GroupDelay => fir.GroupDelay;
      public ReadOnlySpan<double> Out => new(outBuf, 0, outLen);

      public Branch(double cutoffHz, double fs, int taps)
      {
        this.fs = fs;
        fir = new StreamingFirComplex(global::VE3NEA.Dsp.BlackmanSincKernel(cutoffHz / fs, taps));
      }

      public void Reset() => outLen = 0;

      public void Process(ReadOnlySpan<Complex32> mix, int skip)
      {
        outLen = 0;
        if (firBuf.Length < mix.Length) firBuf = new Complex32[mix.Length];
        fir.Process(mix, firBuf);
        for (int i = skip; i < mix.Length; i++) InstFreq(firBuf[i]);
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
}
