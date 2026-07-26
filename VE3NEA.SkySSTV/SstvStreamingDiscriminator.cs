using System;
using MathNet.Numerics;

namespace VE3NEA.SkySSTV
{
  /// <summary>
  /// Streaming form of the outer FM demod chain (plan §7 P7.5(a)): channel FIR → discriminator →
  /// impulse blanker → de-emphasis, block-in / block-out with bounded state. Produces exactly the
  /// samples <see cref="SstvDecoder.Discriminator"/> produces for the same stream — the batch method is
  /// the zero-phase reference, so the causal FIR's group delay is absorbed here (output sample k is the
  /// batch chain's disc[k]) and <see cref="Flush"/> drains the delay lines at end-of-stream.
  ///
  /// <para>Bounded latency, not zero latency: emission lags the input by the FIR group delay plus the
  /// blanker's priming window (100 ms, start-up only) plus its max-gap lookahead (20 ms — a
  /// faded run's disposition is known only when it ends or exceeds the gap bound, §1.13). The amplitude gate
  /// adds 12 samples (0.25 ms) of centered-window lookahead, which the 20 ms bound already dominates.</para>
  /// </summary>
  internal sealed class SstvStreamingDiscriminator : IDisposable
  {
    // the running-median window of the amplitude gate, and the samples added at each end of an impulse run
    // before interpolating, for the pulse skirts. The skirt is the FM-speech experiment's value (2 is its
    // best all-round setting). The window is NOT: that experiment used 21 samples, chosen so a pulse stays a
    // minority of the window while the median still follows the modulation. At SSTV's 3636 px/s the
    // subcarrier steps every ~13 samples, so no window both follows the modulation and outlasts a pulse, and
    // the tradeoff inverts — a LONGER window is better, because the pixel-step residual it leaves inflates
    // the rms and it is that inflation which holds the false-alarm rate down. Measured on the declick plan's
    // §5 ladder (1a, 2026-07-25), samples gated at +12 dB CNR with zero clicks present: 4.19 % at 9 taps,
    // 0.63 % at 21, 0.06 % at 41.
    private const int BaselineMedianLength = 41;
    private const int SkirtSamples = 2;

    private readonly double fs;
    private readonly BlankerGateMode gate;
    private readonly BlankerRepairMode repairMode;
    private readonly double blankerThreshold;
    private readonly double deEmphasisUs;

    // channel FIR (null = stage disabled, matching the batch bypass)
    private readonly StreamingFirComplex? fir;
    private readonly int groupDelay;
    private int rampRemaining;     // leading causal FIR outputs to drop (the batch 'same' shift)
    private Complex32[] firBuf = new Complex32[8192];

    // discriminator state
    private Complex32 prevChan;
    private bool havePrev;      // prevChan holds chan[j−1]
    private bool emittedFirst;  // disc[0] (= disc[1], the batch edge case) has been staged

    // blanker state: prime buffer, then the gate, then the run state machine
    private readonly int primeLen;
    private readonly int maxGap;
    private readonly double alpha;
    private readonly double strongThreshold;
    private readonly double cvStrong;
    private readonly double cvWeak;
    private double envMean;
    private double envMeanSq;      // running mean square of the envelope, for the CV that sets the threshold
    private bool primed;
    private int primeCount;
    private readonly double[] primeMag;
    private readonly double[] primeDisc;
    private bool pendingBadNext;   // a faded chan sample poisons two discriminator outputs

    // amplitude-gate state. Both stages are centered windows over the disc stream, so each holds its samples
    // by absolute index: medWin[i] is sample medBase+i, and medCenter/skirtCenter are the next sample each
    // stage can decide. Together they add BaselineMedianLength/2 + SkirtSamples samples of lookahead.
    private readonly double rmsMultiple;
    private readonly int medianHalf;
    private readonly double[] medWin;
    private readonly double[] medSort;
    private int medCount;
    private long medBase;
    private long medCenter;
    private double msq;            // running mean square of the detrended output (same τ as envMean)
    private readonly double[] skirtDisc;
    private readonly bool[] skirtBad;
    private int skirtCount;
    private long skirtBase;
    private long skirtCenter;
    private double lastGoodDisc;
    private bool haveGood;
    private double[] run = new double[1024];   // held-back bad run awaiting its right neighbor
    private double[] runRatio = new double[1024];   // its envelope ratios, which travel with the samples
    private int runLen;

    // Phase-3a click repair (null = disabled), between the blanker and the de-emphasis
    private readonly SstvClickRepair? repair;

    // de-emphasis state
    private double deEmphY;
    private bool deEmphStarted;

    // output accumulator (reused per call)
    private double[] outBuf = new double[8192];
    private int outLen;

    /// <summary>Total finalized samples emitted so far — the absolute batch-timeline index of the next
    /// output sample.</summary>
    public long EmittedCount { get; private set; }

    public SstvStreamingDiscriminator(SstvDecodeOptions o, double bwHz)
    {
      fs = o.SampleRate;
      gate = o.BlankerGate;
      repairMode = o.BlankerRepair;
      blankerThreshold = o.BlankerThreshold;
      strongThreshold = o.BlankerStrongThreshold;
      cvStrong = o.BlankerCvStrong;
      cvWeak = o.BlankerCvWeak;
      rmsMultiple = o.BlankerRmsMultiple;
      deEmphasisUs = o.DeEmphasisUs;

      double fc = bwHz / fs;
      // no FIR when disabled or already wider than Nyquist (the batch ChannelFilter bypass)
      if (bwHz > 0 && fc < 0.5)
      {
        float[] h = global::VE3NEA.Dsp.BlackmanSincKernel(fc, SstvDecoder.KernelTaps(bwHz, fs));
        fir = new StreamingFirComplex(h);
        groupDelay = fir.GroupDelay;
        rampRemaining = groupDelay;
      }

      primeLen = (int)(0.1 * fs);
      maxGap = (int)(0.02 * fs);
      alpha = 1.0 / (0.1 * fs);
      primeMag = new double[primeLen];
      primeDisc = new double[primeLen];

      medianHalf = BaselineMedianLength / 2;
      medWin = new double[BaselineMedianLength];
      medSort = new double[BaselineMedianLength];
      skirtDisc = new double[2 * SkirtSamples + 1];
      skirtBad = new bool[2 * SkirtSamples + 1];

      if (o.ClickRepair) repair = new SstvClickRepair(fs, o);
    }

    /// <summary>Slips subtracted by the Phase-3a stage, 0 when it is disabled.</summary>
    public long RepairCount => repair?.RepairCount ?? 0;

    /// <summary>Whether the selected gate is armed; either threshold at 0 bypasses the blanker.</summary>
    private bool GateEnabled
      => gate == BlankerGateMode.Amplitude ? rmsMultiple > 0 : blankerThreshold > 0;

    /// <summary>Feed the next IQ block; returns the disc samples finalized by it (batch-timeline order).
    /// The span is valid until the next call.</summary>
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

    /// <summary>End of stream: drain the FIR delay line (the batch tail flush), resolve a still-open
    /// blanker run and an unfinished prime buffer. Call once after the last <see cref="Process"/>.</summary>
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

      // a stream shorter than the prime window: the batch primes over what exists (min(n, 0.1·fs))
      if (!primed && GateEnabled) Prime(primeCount);

      // the amplitude gate's centered windows still owe their last few samples a decision, taken with the
      // right half of each window truncated
      if (gate == BlankerGateMode.Amplitude && GateEnabled)
      {
        while (medCount > 0 && medCenter <= medBase + medCount - 1) MedianDecide();
        while (skirtCount > 0 && skirtCenter <= skirtBase + skirtCount - 1) SkirtDecide();
      }

      // an open bad run at end-of-stream: the batch interpolates toward left (right = left)
      if (runLen > 0)
      {
        if (runLen <= maxGap && haveGood)
        {
          if (repairMode == BlankerRepairMode.SubtractArea) SubtractRunArea(lastGoodDisc, lastGoodDisc);
          else
            for (int j = 0; j < runLen; j++) run[j] = lastGoodDisc;
        }
        for (int j = 0; j < runLen; j++) Emit(run[j], runRatio[j]);
        runLen = 0;
      }

      // and the repair stage's own lookahead, whose tail goes out unexamined
      if (repair != null)
        foreach (double d in repair.Flush()) EmitFinal(d);

      return new ReadOnlySpan<double>(outBuf, 0, outLen);
    }

    public void Dispose() => fir?.Dispose();


    // ----------------------------------------------------------------------------------------------------
    //                                        per-sample pipeline
    // ----------------------------------------------------------------------------------------------------


    /// <summary>arg(chan[j]·conj(chan[j−1])) scaled to Hz, then into the blanker. The batch edge case
    /// disc[0] = disc[1] is reproduced by staging the first pair together.</summary>
    private void Discriminate(Complex32 chan)
    {
      if (!havePrev) { prevChan = chan; havePrev = true; return; }

      float re = chan.Real * prevChan.Real + chan.Imaginary * prevChan.Imaginary;
      float im = chan.Imaginary * prevChan.Real - chan.Real * prevChan.Imaginary;
      double f = Math.Atan2(im, re) * fs / (2 * Math.PI);

      if (!emittedFirst)
      {
        // disc[0] carries disc[1]'s value but chan[0]'s envelope (the batch pairing of mag[i] to disc[i])
        emittedFirst = true;
        Blank(Mag(prevChan), f);
      }
      Blank(Mag(chan), f);
      prevChan = chan;
    }

    private static double Mag(Complex32 c)
      => Math.Sqrt((double)c.Real * c.Real + (double)c.Imaginary * c.Imaginary);

    /// <summary>Impulse blanker: buffer the prime window, gate, then interpolate across the bad runs. Which
    /// signal the gate looks at is <see cref="SstvDecodeOptions.BlankerGate"/>; the default is the
    /// envelope (P6(c), mined from Hopper's FmNoise experiment §6.1):
    /// FM clicks happen where the instantaneous envelope fades — DevVsMag.txt measured the discriminator
    /// error std ~6× larger at zero envelope than at the mean. Discriminator samples whose envelope is
    /// below threshold·(running mean envelope, single-pole tracker τ = 100 ms primed on the first window)
    /// are unreliable and are replaced by linear interpolation across the fade — the threshold itself moving
    /// with the tracked CNR, see <see cref="EnvelopeThreshold"/>; fades longer than the
    /// 20 ms gap bound are dropouts, left to the pulse-train coasting. Realized as a run state machine:
    /// a faded run is held back until its right neighbor arrives (interpolate) or it outgrows the gap
    /// bound (leave raw) — the bounded max-gap latency of plan §1.13.</summary>
    private void Blank(double mag, double disc)
    {
      // with the gate bypassed the envelope tracker never runs, so there is no ratio to confirm against —
      // the repair stage treats NaN as "no envelope" and skips its rejection test
      if (!GateEnabled) { Emit(disc, double.NaN); return; }

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

    /// <summary>Set the selected gate's initial scale over the buffered prime window — the mean envelope, or
    /// the mean square of the detrended output — then run the state machine over the samples the priming
    /// held back. The scale starts at the window statistic rather than at zero, so there is no ramp-in
    /// bias.</summary>
    private void Prime(int count)
    {
      if (count == 0) { primed = true; return; }

      if (gate == BlankerGateMode.Amplitude)
      {
        double sumSquares = 0;
        for (int i = 0; i < count; i++)
        {
          int lo = Math.Max(0, i - medianHalf), hi = Math.Min(count - 1, i + medianHalf);
          int len = hi - lo + 1;
          Array.Copy(primeDisc, lo, medSort, 0, len);
          Array.Sort(medSort, 0, len);
          double d = primeDisc[i] - medSort[len / 2];
          sumSquares += d * d;
        }
        msq = sumSquares / count;
      }
      else
      {
        double mean = 0, meanSq = 0;
        for (int i = 0; i < count; i++)
        {
          mean += primeMag[i];
          meanSq += primeMag[i] * primeMag[i];
        }
        envMean = mean / count;
        envMeanSq = meanSq / count;
      }

      primed = true;
      for (int i = 0; i < count; i++) BlankPrimed(primeMag[i], primeDisc[i]);
    }

    private void BlankPrimed(double mag, double disc)
    {
      if (gate == BlankerGateMode.Amplitude) { MedianPush(disc); return; }

      bool bad = pendingBadNext;
      if (mag < EnvelopeThreshold * envMean) { bad = true; pendingBadNext = true; }
      else pendingBadNext = false;
      double ratio = envMean > 0 ? mag / envMean : double.NaN;
      envMean += (mag - envMean) * alpha;
      envMeanSq += (mag * mag - envMeanSq) * alpha;
      Decide(bad, disc, ratio);
    }

    /// <summary>The envelope-gate threshold for the current sample: the §6 item-1b ramp from
    /// <see cref="SstvDecodeOptions.BlankerStrongThreshold"/> to
    /// <see cref="SstvDecodeOptions.BlankerThreshold"/> over the tracked envelope coefficient of variation,
    /// which stands in for the in-channel CNR. The best fixed threshold falls with CNR because the gate's
    /// cost and its benefit scale differently: what it buys is bounded by the clicks actually present, which
    /// vanish as the CNR rises, while what it costs — interpolation over samples it was wrong about — is set
    /// by its appetite, which does not. A degenerate or inverted CV pair means "no ramp", i.e. the fixed
    /// threshold.</summary>
    private double EnvelopeThreshold
    {
      get
      {
        if (cvWeak <= cvStrong) return blankerThreshold;
        double variance = envMeanSq - envMean * envMean;
        if (envMean <= 0 || variance <= 0) return strongThreshold;
        double w = (Math.Sqrt(variance) / envMean - cvStrong) / (cvWeak - cvStrong);
        w = Math.Clamp(w, 0.0, 1.0);
        return strongThreshold + (blankerThreshold - strongThreshold) * w;
      }
    }

    /// <summary>
    /// Amplitude gate, stage 1 (the streaming port of <c>ImpulseBlanker.MarkImpulsive</c>): marks the
    /// impulses by their own magnitude rather than by the envelope. The modulation is estimated by a short
    /// centered running median — robust to the impulses, which are a minority of the window — and what is
    /// left is measured against a running rms.
    ///
    /// <para>The batch original takes that rms over the whole signal; here it is a single-pole tracker on the
    /// same τ = 100 ms as <see cref="envMean"/>, primed on the first window. Local rather than global is the
    /// better statistic for a faded capture (the threshold follows the noise level down a fade instead of
    /// being set by the burst's loudest stretch), and it is what a streaming stage can compute. The rms and
    /// not a MAD-based sigma: the distribution is heavy-tailed by construction, so its MAD sits far below its
    /// rms and a "3 sigma" threshold built that way lands near 1 rms and blanks 40 % of the signal.</para>
    /// </summary>
    private void MedianPush(double disc)
    {
      if (medCount == medWin.Length)
      {
        Array.Copy(medWin, 1, medWin, 0, medCount - 1);
        medCount--;
        medBase++;
      }
      medWin[medCount++] = disc;

      // decide every sample whose right half-window has now arrived
      while (medCenter <= medBase + medCount - 1 - medianHalf) MedianDecide();
    }

    /// <summary>Decide <see cref="medCenter"/>, whose window is <c>[medCenter − medianHalf, newest]</c> —
    /// the full 21 samples in steady state, truncated on the left at stream start and on the right at
    /// <see cref="Flush"/>.</summary>
    private void MedianDecide()
    {
      int drop = (int)(medCenter - medianHalf - medBase);
      if (drop > 0)
      {
        Array.Copy(medWin, drop, medWin, 0, medCount - drop);
        medCount -= drop;
        medBase += drop;
      }

      double value = medWin[medCenter - medBase];
      Array.Copy(medWin, medSort, medCount);
      Array.Sort(medSort, 0, medCount);
      double d = value - medSort[medCount / 2];
      bool impulsive = Math.Abs(d) > rmsMultiple * Math.Sqrt(msq);
      msq += (d * d - msq) * alpha;

      medCenter++;
      SkirtPush(value, impulsive);
    }

    /// <summary>Amplitude gate, stage 2: widen each impulse run by the pulse skirts, which sit below the
    /// threshold but still carry energy. The pulse is really CNR-dependent (1 sample wide at 8 dB CNR, 8 at
    /// 4 dB), so a width that tracked the CNR would do better than this constant.</summary>
    private void SkirtPush(double disc, bool impulsive)
    {
      if (skirtCount == skirtDisc.Length)
      {
        Array.Copy(skirtDisc, 1, skirtDisc, 0, skirtCount - 1);
        Array.Copy(skirtBad, 1, skirtBad, 0, skirtCount - 1);
        skirtCount--;
        skirtBase++;
      }
      skirtDisc[skirtCount] = disc;
      skirtBad[skirtCount] = impulsive;
      skirtCount++;

      while (skirtCenter <= skirtBase + skirtCount - 1 - SkirtSamples) SkirtDecide();
    }

    private void SkirtDecide()
    {
      int drop = (int)(skirtCenter - SkirtSamples - skirtBase);
      if (drop > 0)
      {
        Array.Copy(skirtDisc, drop, skirtDisc, 0, skirtCount - drop);
        Array.Copy(skirtBad, drop, skirtBad, 0, skirtCount - drop);
        skirtCount -= drop;
        skirtBase += drop;
      }

      // the buffer now holds exactly the skirt window, so any flag in it condemns the center
      bool bad = false;
      for (int i = 0; i < skirtCount; i++)
        if (skirtBad[i]) { bad = true; break; }

      double value = skirtDisc[skirtCenter - skirtBase];
      skirtCenter++;
      // the amplitude gate carries no envelope statistic, so the repair stage gets no ratio from this path
      Decide(bad, value, double.NaN);
    }

    /// <summary>The run state machine shared by both gates: a bad run is held back until its right neighbor
    /// arrives (interpolate) or it outgrows the gap bound (leave raw). The envelope ratio travels with each
    /// sample for the Phase-3a stage downstream; where the run is interpolated the step is already gone, so
    /// what that stage makes of the ratio there does not matter.</summary>
    private void Decide(bool bad, double disc, double ratio)
    {
      if (bad)
      {
        if (runLen == run.Length)
        {
          Array.Resize(ref run, run.Length * 2);
          Array.Resize(ref runRatio, runRatio.Length * 2);
        }
        runRatio[runLen] = ratio;
        run[runLen++] = disc;
        return;
      }

      if (runLen > 0)
      {
        if (runLen <= maxGap)
        {
          // left = the last good sample (or the right neighbor at stream start, the batch a == 0 case),
          // right = this sample; the line between them is the modulation the run would have carried
          double left = haveGood ? lastGoodDisc : disc;
          if (repairMode == BlankerRepairMode.SubtractArea) SubtractRunArea(left, disc);
          else
            for (int j = 0; j < runLen; j++)
              run[j] = left + (disc - left) * (j + 1) / (runLen + 1);
        }
        for (int j = 0; j < runLen; j++) Emit(run[j], runRatio[j]);
        runLen = 0;
      }
      lastGoodDisc = disc;
      haveGood = true;
      Emit(disc, ratio);
    }

    /// <summary>The §3.2 item-1 alternative to interpolating a condemned run: measure its area above the
    /// line its neighbours imply, and take exactly that much back out, spread evenly. The run's samples keep
    /// their noise and their shape — only the offending area goes, which is the only part of a slip that
    /// survives the video low-pass (0d). Self-limiting by construction: a fade that carried no slip has
    /// little excess area, so little is removed, where interpolation would have flattened it regardless.
    /// <para><paramref name="right"/> is the good sample that closed the run, or — at
    /// <see cref="Flush"/>, where there is no right neighbour — the left one again, which makes the baseline
    /// the constant the interpolating path uses there.</para></summary>
    private void SubtractRunArea(double left, double right)
    {
      double excess = 0;
      for (int j = 0; j < runLen; j++)
        excess += run[j] - (left + (right - left) * (j + 1) / (runLen + 1));

      double step = excess / runLen;
      for (int j = 0; j < runLen; j++) run[j] -= step;
    }

    /// <summary>Final emission, through the optional classic FM de-emphasis (plan §1.3, P6(c)
    /// experiment): a single-pole low-pass <c>y += (x − y)·a</c> with corner 1/(2πτ), −6 dB/oct above
    /// it — the exact inverse of the parabolic (+6 dB/oct amplitude) post-discriminator FM noise, so it
    /// flattens the noise floor across the subcarrier band. Brightness is the instantaneous frequency of
    /// the dominant subcarrier tone, so the LTI amplitude tilt itself is invisible to the video; only the
    /// noise reshaping (and the pole's small, sub-pixel group delay) can matter.</summary>
    private void Emit(double disc, double ratio)
    {
      if (repair == null) { EmitFinal(disc); return; }
      foreach (double d in repair.Push(disc, ratio)) EmitFinal(d);
    }

    /// <summary>The last link: de-emphasis, then out. Separate from <see cref="Emit"/> because the repair
    /// stage sits between them — it must see the raw steps, not de-emphasized ones, and it reorders nothing
    /// but does delay.</summary>
    private void EmitFinal(double disc)
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
