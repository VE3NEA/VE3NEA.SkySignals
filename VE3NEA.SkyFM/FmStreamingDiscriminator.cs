using System;
using MathNet.Numerics;

namespace VE3NEA.SkyFM
{
  /// <summary>
  /// Streaming FM demod chain: channel FIR → discriminator → impulse blanker → de-emphasis, block-in /
  /// block-out with bounded state. A port of the SSTV decoder's <c>SstvStreamingDiscriminator</c> —
  /// validated on real satellite FM IQ from the same recorder (ASR-plan.md §5.1) — with the voice-channel
  /// options. Output is the instantaneous frequency in Hz on the zero-phase batch timeline: the causal
  /// FIR's group delay is absorbed here (output sample k is the zero-phase chain's disc[k]) and
  /// <see cref="Flush"/> drains the delay lines at end-of-stream.
  ///
  /// <para>Bounded latency, not zero latency: emission lags the input by the FIR group delay plus the
  /// blanker's envelope-priming window (100 ms, start-up only) plus its max-gap lookahead (20 ms — a
  /// faded run's disposition is known only when it ends or exceeds the gap bound).</para>
  /// </summary>
  internal sealed class FmStreamingDiscriminator : IDisposable
  {
    private readonly double fs;
    private readonly double blankerThreshold;
    private readonly double deEmphasisUs;

    // channel FIR (null = stage disabled, wider than Nyquist or bw 0)
    private readonly StreamingFirComplex? fir;
    private readonly int groupDelay;
    private int rampRemaining;     // leading causal FIR outputs to drop (the zero-phase 'same' shift)
    private Complex32[] firBuf = new Complex32[8192];

    // discriminator state
    private Complex32 prevChan;
    private bool havePrev;      // prevChan holds chan[j−1]
    private bool emittedFirst;  // disc[0] (= disc[1], the stream-start edge case) has been staged

    // blanker state: envelope prime buffer, then the run state machine
    private readonly int primeLen;
    private readonly int maxGap;
    private readonly double alpha;
    private double envMean;
    private bool primed;
    private int primeCount;
    private readonly double[] primeMag;
    private readonly double[] primeDisc;
    private bool pendingBadNext;   // a faded chan sample poisons two discriminator outputs
    private double lastGoodDisc;
    private bool haveGood;
    private double[] run = new double[1024];   // held-back bad run awaiting its right neighbor
    private int runLen;

    // de-emphasis state
    private double deEmphY;
    private bool deEmphStarted;

    // output accumulator (reused per call)
    private double[] outBuf = new double[8192];
    private int outLen;

    /// <summary>Total finalized samples emitted so far — the absolute timeline index of the next
    /// output sample.</summary>
    public long EmittedCount { get; private set; }

    public FmStreamingDiscriminator(FmDecodeOptions o)
    {
      fs = o.SampleRate;
      blankerThreshold = o.BlankerThreshold;
      deEmphasisUs = o.DeEmphasisUs;

      double fc = o.ChannelBwHz / fs;
      // no FIR when disabled or already wider than Nyquist
      if (o.ChannelBwHz > 0 && fc < 0.5)
      {
        float[] h = Dsp.BlackmanSincKernel(fc, KernelTaps(o.ChannelBwHz, fs));
        fir = new StreamingFirComplex(h);
        groupDelay = fir.GroupDelay;
        rampRemaining = groupDelay;
      }

      primeLen = (int)(0.1 * fs);
      maxGap = (int)(0.02 * fs);
      alpha = 1.0 / (0.1 * fs);
      primeMag = new double[primeLen];
      primeDisc = new double[primeLen];
    }

    /// <summary>Odd tap count for a windowed-sinc LPF at <paramref name="cutoffHz"/>: ~4 cycles of the
    /// cutoff period, floored so the skirt is clean (same formula as the SSTV chain this is a port of).</summary>
    internal static int KernelTaps(double cutoffHz, double fs)
    {
      int taps = (int)Math.Round(4.0 * fs / Math.Max(1.0, cutoffHz)) | 1;
      return Math.Max(63, Math.Min(taps, 1023));
    }

    /// <summary>Feed the next IQ block; returns the disc samples finalized by it (absolute-timeline
    /// order). The span is valid until the next call.</summary>
    public ReadOnlySpan<double> Process(ReadOnlySpan<Complex32> iq)
    {
      outLen = 0;
      if (iq.IsEmpty) return ReadOnlySpan<double>.Empty;

      if (fir != null)
      {
        if (firBuf.Length < iq.Length) firBuf = new Complex32[iq.Length];
        fir.Process(iq, firBuf);
        int skip = Math.Min(rampRemaining, iq.Length);
        rampRemaining -= skip;
        for (int i = skip; i < iq.Length; i++) Discriminate(firBuf[i]);
      }
      else
        for (int i = 0; i < iq.Length; i++) Discriminate(iq[i]);

      return new ReadOnlySpan<double>(outBuf, 0, outLen);
    }

    /// <summary>End of stream: drain the FIR delay line, resolve a still-open blanker run and an
    /// unfinished prime buffer. Call once after the last <see cref="Process"/>.</summary>
    public ReadOnlySpan<double> Flush()
    {
      outLen = 0;
      if (fir != null && groupDelay > 0)
      {
        var zeros = new Complex32[groupDelay];
        if (firBuf.Length < groupDelay) firBuf = new Complex32[groupDelay];
        fir.Process(zeros, firBuf);
        int skip = Math.Min(rampRemaining, groupDelay);
        rampRemaining -= skip;
        for (int i = skip; i < groupDelay; i++) Discriminate(firBuf[i]);
      }

      // a stream shorter than the prime window: prime over what exists (min(n, 0.1·fs))
      if (!primed && blankerThreshold > 0) Prime(primeCount);

      // an open bad run at end-of-stream: interpolate toward left (right = left)
      if (runLen > 0)
      {
        if (runLen <= maxGap && haveGood)
          for (int j = 0; j < runLen; j++) run[j] = lastGoodDisc;
        for (int j = 0; j < runLen; j++) Emit(run[j]);
        runLen = 0;
      }
      return new ReadOnlySpan<double>(outBuf, 0, outLen);
    }

    public void Dispose() => fir?.Dispose();


    // ----------------------------------------------------------------------------------------------------
    //                                        per-sample pipeline
    // ----------------------------------------------------------------------------------------------------

    /// <summary>arg(chan[j]·conj(chan[j−1])) scaled to Hz, then into the blanker. The stream-start edge
    /// case disc[0] = disc[1] is reproduced by staging the first pair together.</summary>
    private void Discriminate(Complex32 chan)
    {
      if (!havePrev) { prevChan = chan; havePrev = true; return; }

      float re = chan.Real * prevChan.Real + chan.Imaginary * prevChan.Imaginary;
      float im = chan.Imaginary * prevChan.Real - chan.Real * prevChan.Imaginary;
      double f = Math.Atan2(im, re) * fs / (2 * Math.PI);

      if (!emittedFirst)
      {
        // disc[0] carries disc[1]'s value but chan[0]'s envelope (the pairing of mag[i] to disc[i])
        emittedFirst = true;
        Blank(Mag(prevChan), f);
      }
      Blank(Mag(chan), f);
      prevChan = chan;
    }

    private static double Mag(Complex32 c)
      => Math.Sqrt((double)c.Real * c.Real + (double)c.Imaginary * c.Imaginary);

    /// <summary>Envelope-gated impulse blanker: FM clicks happen where the instantaneous envelope fades,
    /// so discriminator samples whose envelope is below threshold·(running mean envelope, single-pole
    /// tracker τ = 100 ms primed on the first window) are unreliable and are replaced by linear
    /// interpolation across the fade; fades longer than the 20 ms gap bound are dropouts, left raw.
    /// Realized as a run state machine: a faded run is held back until its right neighbor arrives
    /// (interpolate) or it outgrows the gap bound (leave raw).</summary>
    private void Blank(double mag, double disc)
    {
      if (blankerThreshold <= 0) { Emit(disc); return; }

      if (!primed)
      {
        primeMag[primeCount] = mag;
        primeDisc[primeCount] = disc;
        if (++primeCount < primeLen) return;
        Prime(primeLen);
        return;
      }
      BlankPrimed(mag, disc);
    }

    /// <summary>Set the initial envelope mean over the buffered prime window, then run the state machine
    /// over the samples the priming held back.</summary>
    private void Prime(int count)
    {
      if (count == 0) { primed = true; return; }
      double mean = 0;
      for (int i = 0; i < count; i++) mean += primeMag[i];
      envMean = mean / count;
      primed = true;
      for (int i = 0; i < count; i++) BlankPrimed(primeMag[i], primeDisc[i]);
    }

    private void BlankPrimed(double mag, double disc)
    {
      bool bad = pendingBadNext;
      if (mag < blankerThreshold * envMean) { bad = true; pendingBadNext = true; }
      else pendingBadNext = false;
      envMean += (mag - envMean) * alpha;

      if (bad)
      {
        if (runLen == run.Length) Array.Resize(ref run, run.Length * 2);
        run[runLen++] = disc;
        return;
      }

      if (runLen > 0)
      {
        if (runLen <= maxGap)
        {
          // interpolate across the fade: left = the last good sample (or the right neighbor at stream
          // start), right = this sample
          double left = haveGood ? lastGoodDisc : disc;
          for (int j = 0; j < runLen; j++)
            run[j] = left + (disc - left) * (j + 1) / (runLen + 1);
        }
        for (int j = 0; j < runLen; j++) Emit(run[j]);
        runLen = 0;
      }
      lastGoodDisc = disc;
      haveGood = true;
      Emit(disc);
    }

    /// <summary>Final emission, through the optional classic FM de-emphasis: a single-pole low-pass
    /// <c>y += (x − y)·a</c> with corner 1/(2πτ), −6 dB/oct above it — the inverse of the parabolic
    /// (+6 dB/oct amplitude) post-discriminator FM noise.</summary>
    private void Emit(double disc)
    {
      if (deEmphasisUs > 0)
      {
        double a = 1.0 - Math.Exp(-1.0 / (deEmphasisUs * 1e-6 * fs));
        if (!deEmphStarted) { deEmphY = disc; deEmphStarted = true; }
        deEmphY += (disc - deEmphY) * a;
        disc = deEmphY;
      }
      if (outLen == outBuf.Length) Array.Resize(ref outBuf, outBuf.Length * 2);
      outBuf[outLen++] = disc;
      EmittedCount++;
    }
  }
}
