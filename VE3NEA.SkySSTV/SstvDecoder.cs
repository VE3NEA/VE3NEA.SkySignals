using System;
using System.Collections.Generic;
using MathNet.Numerics;

namespace VE3NEA.SkySSTV
{
  /// <summary>
  /// Decoder (plan §3, §4, §4.1, §7): recover an image from an FM-on-FM IQ stream with <b>real timing
  /// acquisition</b> — the VIS header (plan §4) or, absent it, the winning MHT pulse train (plan §4.1)
  /// locates the image start; per-line timing and slant come from the train's RLS period/phase regression
  /// (<see cref="SstvSyncRegressor"/> — missed pulses coast for free). The chain is
  /// <c>channel FIR → FM discriminator → mix + complex low-pass brightness → instantaneous frequency →
  /// per-pixel matched integrator</c>, then a per-layout YCrCb→RGB reconstruction. Reuses the wrapped
  /// VE3NEA.Dsp natives (BlackmanSinc kernels, SIMD LiquidFir, FFTW) for the filters; the discriminator
  /// and integrator are trivial inline loops.
  ///
  /// Brightness is the subcarrier's instantaneous frequency, so it is independent of FM deviation and
  /// amplitude (plan §1.4). All three color layouts are implemented — Robot36, Robot72 and PD.
  /// </summary>
  public static class SstvDecoder
  {
    /// <summary>Decode <paramref name="iq"/> in <paramref name="mode"/> to an image. By default the start
    /// timing is acquired from the VIS header or the winning sync train; set
    /// <see cref="SstvDecodeOptions.Acquire"/> false to decode at the fixed
    /// <see cref="SstvDecodeOptions.StartSample"/>.</summary>
    public static RgbImage Decode(Complex32[] iq, SstvMode mode, SstvDecodeOptions? options = null)
      => Decode(Discriminator(iq, options ?? new SstvDecodeOptions()), mode, options);

    /// <summary>Decode from the discriminated audio (one <see cref="Discriminator"/> pass is shared between
    /// detection and decode — retro item O; every stage downstream of the outer FM demod consumes this
    /// array, so callers that already discriminated must not pay a second multi-hundred-MB pass).</summary>
    public static RgbImage Decode(double[] disc, SstvMode mode, SstvDecodeOptions? options = null)
    {
      var o = options ?? new SstvDecodeOptions();
      var spec = SstvModes.Get(mode);
      double fs = o.SampleRate;

      // the MHT pulse-train extraction supplies acquisition (burst start) and per-line timing (plan §4.1)
      SstvPulseTrain? train = null;
      SstvVisResult firstHit = default;
      if (o.Acquire || o.Track)
      {
        double[] sync = SyncAudio(disc, fs, o);              // Stage-2 band-limit for all timing statistics
        List<SstvVisResult>? seeds = null;
        if (o.Acquire)
        {
          seeds = new List<SstvVisResult>();
          foreach (var hit in SstvVisDetector.DetectAll(sync, fs))
            if (hit.Mode == mode)
            {
              seeds.Add(hit);
              if (!firstHit.Found) firstHit = hit;
            }
        }
        train = ExtractTrains(sync, fs, seeds).BestTrain(mode);
      }

      int start = !o.Acquire ? o.StartSample
        : train != null ? (int)Math.Round(train.Regr.GetPulseTime(0))
        : firstHit.Found ? firstHit.HeaderEndSample          // VIS parsed but no train confirmed it
        : o.StartSample;

      var (lineOnset, corr) = LineOnsets(train, fs, spec, o, start);
      double[] brightness = Brightness(disc, fs, o);
      return Reconstruct(brightness, spec, o, lineOnset, corr);
    }

    /// <summary>Per-transmitted-line sync-onset samples + the slant/clock correction (retro item F). With
    /// <see cref="SstvDecodeOptions.Track"/> the lines sit on the winning train's RLS grid — slant-corrected
    /// and coasting through fades (plan §1.6/§4.1); otherwise (or when no train was found) they are laid at
    /// the fixed nominal period from <paramref name="start"/>.</summary>
    private static (double[] onsets, double corr) LineOnsets(SstvPulseTrain? train, double fs,
      SstvModeSpec spec, SstvDecodeOptions o, int start)
    {
      var onset = new double[spec.LineCount];
      if (o.Track && train != null)
      {
        int line0 = train.Regr.GetPulseNo(start);
        for (int line = 0; line < spec.LineCount; line++)
          onset[line] = train.Regr.GetPulseTime(line0 + line);
        return (onset, train.Regr.CorrFactor);
      }
      double nominal = spec.LinePeriodMs / 1000.0 * fs;
      for (int line = 0; line < spec.LineCount; line++) onset[line] = start + line * nominal;
      return (onset, 1.0);
    }

    /// <summary>Run the per-family streaming sync detectors over the Stage-2 audio and feed the MHT
    /// extractor (plan §4.1). Every found VIS hit seeds a high-prior train. The audio is
    /// front-padded so a sync at sample 0 still has a warm left template flank, and the two detectors'
    /// differing emission latencies are re-ordered so the extractor sees pulses in onset order.</summary>
    internal static SstvPulseTrainExtractor ExtractTrains(double[] sync, double fs,
      IReadOnlyList<SstvVisResult>? visHits = null)
    {
      var extractor = new SstvPulseTrainExtractor(fs);
      if (visHits != null)
        foreach (var hit in visHits)
          if (hit.Found && hit.Mode is SstvMode vm) extractor.AddVisTrain(vm, hit.HeaderEndSample);

      var families = new List<double>();
      foreach (var spec in SstvModes.All)
        if (!families.Contains(spec.SyncMs)) families.Add(spec.SyncMs);

      int pad = (int)Math.Round(0.05 * fs);                  // lead-in: > 2× the longest sync template
      int maxLatency = (int)Math.Round(0.10 * fs);           // bound on detector emission lag past an onset
      int blockSize = (int)Math.Round(0.25 * fs);

      // the streaming soft-comb rides the detectors' un-thresholded score streams (plan §4.1): a confirmed
      // hit seeds a high-prior back-dated train — the sensitivity floor for transmissions whose single
      // pulses never separate from noise (the 04-18 class)
      var comb = new SstvSoftComb(fs);
      var detectors = new SstvPulseDetector[families.Count];
      for (int i = 0; i < families.Count; i++)
      {
        double family = families[i];
        detectors[i] = new SstvPulseDetector(fs, family)
        {
          Threshold = SstvPulseDetector.AssocThreshold,      // two-tier soft evidence (plan §4.1)
          ScoreTap = (t, s) => comb.Process(family, t - pad, s)
        };
      }

      var raw = new List<SstvPulse>();
      var pending = new List<SstvPulse>();
      var deliver = new List<SstvPulse>();
      int total = pad + sync.Length;
      for (int blockStart = 0; blockStart < total; blockStart += blockSize)
      {
        int blockEnd = Math.Min(total, blockStart + blockSize);
        raw.Clear();
        foreach (var det in detectors)
          for (int i = blockStart; i < blockEnd; i++)
            det.Process(i < pad ? 0.0 : sync[i - pad], raw);
        foreach (var p in raw) pending.Add(new SstvPulse(p.Time - pad, p.Power, p.DurMs));
        pending.Sort((a, b) => a.Time.CompareTo(b.Time));

        // a confirmed comb hit seeds (or refreshes) the high-prior back-dated train before this block's
        // pulses are folded, so they associate with it immediately
        if (comb.Check(blockEnd - pad) is SstvCombHit hit)
          extractor.AddCombTrain(hit.Mode, (int)hit.AnchorSample);

        // deliver only pulses no later emission can precede, so the extractor sees onset order
        int safeTime = blockEnd - pad - maxLatency;
        deliver.Clear();
        int cnt = 0;
        while (cnt < pending.Count && pending[cnt].Time <= safeTime) deliver.Add(pending[cnt++]);
        pending.RemoveRange(0, cnt);
        extractor.Process(deliver, blockEnd - pad);

        // a retiring train's family rings hold only its residue — flush them so the decaying ridge
        // cannot re-fire as a phantom seed (it stays over threshold for ~ln(z/HitZ) comb memories)
        if (extractor.RetiredTrain is SstvPulseTrain retired)
          comb.ResetFamily(SstvModes.Get(retired.Format).SyncMs);
      }

      raw.Clear();
      foreach (var det in detectors) det.Flush(raw);
      foreach (var p in raw) pending.Add(new SstvPulse(p.Time - pad, p.Power, p.DurMs));
      pending.Sort((a, b) => a.Time.CompareTo(b.Time));
      extractor.Process(pending, sync.Length);
      extractor.Finish(sync.Length);
      return extractor;
    }

    /// <summary>Scan the whole stream for a VIS header and return the first hit (plan §4).
    /// <see cref="SstvVisResult.Found"/> is false when no valid, parity-checked header is present
    /// anywhere; the byte may map to no supported mode.</summary>
    public static SstvVisResult DetectVis(Complex32[] iq, SstvDecodeOptions? options = null)
      => DetectVis(Discriminator(iq, options ?? new SstvDecodeOptions()), options);

    /// <summary>VIS scan from the discriminated audio (the shared-pass form, retro item O).</summary>
    public static SstvVisResult DetectVis(double[] disc, SstvDecodeOptions? options = null)
    {
      var o = options ?? new SstvDecodeOptions();
      double[] sync = SyncAudio(disc, o.SampleRate, o);
      var hits = SstvVisDetector.DetectAll(sync, o.SampleRate);
      return hits.Count > 0 ? hits[0] : new SstvVisResult(false, -1, null, -1, -1, 0, false);
    }

    /// <summary>Infer the SSTV mode of <paramref name="iq"/> (plan §4): a valid VIS header if present, else
    /// the sync cadence/duration via the MHT. <see cref="SstvModeResult.Found"/> is false when neither a VIS
    /// header nor a coherent sync train is present.</summary>
    public static SstvModeResult DetectMode(Complex32[] iq, SstvDecodeOptions? options = null)
      => DetectMode(Discriminator(iq, options ?? new SstvDecodeOptions()), options);

    /// <summary>Mode inference from the discriminated audio (the shared-pass form, retro item O).</summary>
    public static SstvModeResult DetectMode(double[] disc, SstvDecodeOptions? options = null)
    {
      var o = options ?? new SstvDecodeOptions();
      double[] sync = SyncAudio(disc, o.SampleRate, o);
      return SstvModeDetector.Detect(sync, o.SampleRate, o);
    }

    /// <summary>Channel FIR + FM discriminator: the outer FM demod that recovers the SSTV subcarrier audio
    /// <c>f_doppler + dev·audio(t)</c> (Hz). Run it ONCE per capture and hand the output to the
    /// disc-based <see cref="DetectMode(double[], SstvDecodeOptions?)"/> /
    /// <see cref="Decode(double[], SstvMode, SstvDecodeOptions?)"/> overloads (retro item O). The
    /// discriminator itself stays the hand-rolled <c>Math.Atan2</c> loop rather than the wrapped liquid
    /// <c>freqdem</c> native: its cost is negligible next to the FIR stages, and the deterministic
    /// double-precision arithmetic keeps the tuned detection statistics stable (freqdem computes the same
    /// phase difference in float with an approximated atan2 — no measurable win, a precision risk).</summary>
    public static double[] Discriminator(Complex32[] iq, SstvDecodeOptions o)
    {
      double fs = o.SampleRate;
      Complex32[] chan = ChannelFilter(iq, fs, o.ChannelBwHz);
      return Discriminate(chan, fs);
    }

    /// <summary>Stage-2 audio bandpass (plan §3, retro item J): band-limit the discriminated audio to the
    /// SSTV tone band before the sync / VIS / mode statistics. The coherence statistic divides by total
    /// window energy, and the post-discriminator FM noise is parabolic in frequency, so without this stage
    /// the out-of-band noise inflates the denominator and crushes real-signal scores. A cosine-modulated
    /// BlackmanSinc low-pass gives a linear-phase bandpass; it also removes the DC Doppler term, unifying
    /// the DC handling of every detection path. The brightness path keeps its own complex low-pass.</summary>
    internal static double[] SyncAudio(double[] disc, double fs, SstvDecodeOptions o)
    {
      double lo = o.SyncBandLowHz, hi = o.SyncBandHighHz;
      if (hi <= lo || lo <= 0 || hi >= fs / 2) return disc;   // stage disabled

      double half = (hi - lo) / 2, f0 = (lo + hi) / 2;
      float[] lp = global::VE3NEA.Dsp.BlackmanSincKernel(half / fs, KernelTaps(half, fs));
      var bp = new float[lp.Length];
      int center = lp.Length / 2;
      double w0 = 2 * Math.PI * f0 / fs;
      for (int i = 0; i < lp.Length; i++) bp[i] = 2f * lp[i] * (float)Math.Cos(w0 * (i - center));

      var x = new float[disc.Length];
      for (int i = 0; i < disc.Length; i++) x[i] = (float)disc[i];
      float[] y = VE3NEA.LiquidFir.ConvolveSame(x, bp);        // SIMD firfilt_rrrf, zero-phase
      var outp = new double[disc.Length];
      for (int i = 0; i < outp.Length; i++) outp[i] = y[i];
      return outp;
    }

    /// <summary>Per-sample brightness (subcarrier instantaneous frequency, Hz) from the discriminated audio —
    /// the <b>streaming</b> Stage-3 path (plan §1.4/§6.1, replaces the batch whole-signal FFT). Mix the real
    /// audio down by the 1900 Hz center (an NCO) so the video sits at baseband and its mirror at −3800 Hz,
    /// complex low-pass to <see cref="SstvDecodeOptions.BrightnessBwHz"/> (which also rejects the DC Doppler
    /// term the mix pushes to −1900), then instantaneous frequency + 1900. All bounded-state (NCO + FIR +
    /// one-sample diff) — no whole-signal transform.</summary>
    internal static double[] Brightness(double[] disc, double fs, SstvDecodeOptions o)
    {
      int n = disc.Length;
      double fc = SstvTones.Center;                          // 1900 Hz subcarrier center
      double w = 2 * Math.PI * fc / fs;

      // NCO mix-to-baseband (accumulated, wrapped phase for precision over long segments)
      var mixed = new Complex32[n];
      double ph = 0;
      for (int i = 0; i < n; i++)
      {
        mixed[i] = new Complex32((float)(disc[i] * Math.Cos(ph)), (float)(disc[i] * Math.Sin(ph)));
        ph -= w; if (ph < -Math.PI) ph += 2 * Math.PI;
      }

      // complex low-pass (video band) — reuses BlackmanSinc + SIMD LiquidFir (streaming firfilt)
      float[] h = global::VE3NEA.Dsp.BlackmanSincKernel(o.BrightnessBwHz / fs, KernelTaps(o.BrightnessBwHz, fs));
      Complex32[] bb = VE3NEA.LiquidFir.ConvolveSame(mixed, h);

      var brightness = new double[n];
      double k = fs / (2 * Math.PI);
      for (int i = 1; i < n; i++)
      {
        double re = (double)bb[i].Real * bb[i - 1].Real + (double)bb[i].Imaginary * bb[i - 1].Imaginary;
        double im = (double)bb[i].Imaginary * bb[i - 1].Real - (double)bb[i].Real * bb[i - 1].Imaginary;
        brightness[i] = Math.Atan2(im, re) * k + fc;
      }
      if (n > 1) brightness[0] = brightness[1];
      return brightness;
    }

    // ----------------------------------------------------------------------------------------------------
    //                                          front-end stages
    // ----------------------------------------------------------------------------------------------------


    private static Complex32[] ChannelFilter(Complex32[] iq, double fs, double bwHz)
    {
      double fc = bwHz / fs;
      if (bwHz <= 0 || fc >= 0.5) return iq;                  // disabled / already narrower than Nyquist
      float[] h = global::VE3NEA.Dsp.BlackmanSincKernel(fc, KernelTaps(bwHz, fs));
      return VE3NEA.LiquidFir.ConvolveSame(iq, h);            // SIMD firfilt_crcf, zero-phase
    }

    /// <summary>FM discriminator arg(x[n]·conj(x[n−1])) scaled to Hz — the outer FM demod.</summary>
    private static double[] Discriminate(Complex32[] x, double fs)
    {
      var f = new double[x.Length];
      double k = fs / (2 * Math.PI);
      for (int i = 1; i < x.Length; i++)
      {
        float re = x[i].Real * x[i - 1].Real + x[i].Imaginary * x[i - 1].Imaginary;
        float im = x[i].Imaginary * x[i - 1].Real - x[i].Real * x[i - 1].Imaginary;
        f[i] = Math.Atan2(im, re) * k;
      }
      if (f.Length > 1) f[0] = f[1];
      return f;
    }

    /// <summary>Odd tap count for a windowed-sinc LPF at <paramref name="cutoffHz"/>: ~4 cycles of the
    /// cutoff period, floored so the skirt is clean.</summary>
    private static int KernelTaps(double cutoffHz, double fs)
    {
      int taps = (int)Math.Round(4.0 * fs / Math.Max(1.0, cutoffHz)) | 1;
      return Math.Max(63, Math.Min(taps, 1023));
    }


    // ----------------------------------------------------------------------------------------------------
    //                                     timing / reconstruction
    // ----------------------------------------------------------------------------------------------------


    private enum Seg { Sync, Porch, ScanY, Sep, SepPorch, ScanRY, ScanBY, ScanChromaAuto }

    /// <summary>The ordered segments of one transmitted line for the Robot layouts (mirrors the encoder).
    /// Robot36 sends one chroma per line; its identity is read from the separator tone (retro item M),
    /// with the nominal alternation (R-Y on even, B-Y on odd) only as a fallback. Robot72 sends both.</summary>
    private static IEnumerable<(Seg kind, double ms)> LineSegments(SstvModeSpec spec, int line)
    {
      yield return (Seg.Sync, spec.SyncMs);
      yield return (Seg.Porch, spec.SyncPorchMs);
      yield return (Seg.ScanY, spec.ScanYMs);
      if (spec.Layout == SstvColorLayout.Robot36)
      {
        yield return (Seg.Sep, spec.SepMs);
        yield return (Seg.SepPorch, spec.SepPorchMs);
        yield return (Seg.ScanChromaAuto, spec.ScanChromaMs);
      }
      else // Robot72
      {
        yield return (Seg.Sep, spec.SepMs);
        yield return (Seg.SepPorch, spec.SepPorchMs);
        yield return (Seg.ScanRY, spec.ScanChromaMs);
        yield return (Seg.Sep, spec.SepMs);
        yield return (Seg.SepPorch, spec.SepPorchMs);
        yield return (Seg.ScanBY, spec.ScanChromaMs);
      }
    }

    /// <summary><paramref name="corr"/> is the RLS slant/clock correction (period / nominal): it scales the
    /// intra-line segment and pixel widths so a sample-clock error does not shear pixels within a line
    /// (retro item F; Hopper's <c>TimeScale = samplesPerMs·CorrFactor</c>).</summary>
    private static RgbImage Reconstruct(double[] brightness, SstvModeSpec spec, SstvDecodeOptions o,
      double[] lineOnset, double corr)
      => spec.Layout == SstvColorLayout.Pd
         ? ReconstructPd(brightness, spec, o, lineOnset, corr)
         : ReconstructRobot(brightness, spec, o, lineOnset, corr);

    private static RgbImage ReconstructRobot(double[] brightness, SstvModeSpec spec, SstvDecodeOptions o,
      double[] lineOnset, double corr)
    {
      int w = spec.Width, h = spec.Height;
      double fs = o.SampleRate * corr;                       // clock-corrected pixel time scale (retro F)
      var y = new double[h * w];
      var cr = new double[h * w];
      var cb = new double[h * w];
      var hasCr = new bool[h];
      var hasCb = new bool[h];

      for (int line = 0; line < spec.LineCount && line < h; line++)
      {
        double cursor = lineOnset[line];       // each line re-anchored to its own tracked sync onset
        double sepFreq = 0;                    // last separator tone (Hz) — identifies Robot36's chroma
        foreach (var (kind, ms) in LineSegments(spec, line))
        {
          int n = (int)Math.Round(ms / 1000.0 * fs);
          int segStart = (int)Math.Round(cursor);
          cursor += n;
          switch (kind)
          {
            case Seg.ScanY: ReadScan(brightness, segStart, n, w, o, y, line * w); break;
            case Seg.Sep: sepFreq = SegmentFreq(brightness, segStart, n); break;
            case Seg.ScanRY: ReadScan(brightness, segStart, n, w, o, cr, line * w); hasCr[line] = true; break;
            case Seg.ScanBY: ReadScan(brightness, segStart, n, w, o, cb, line * w); hasCb[line] = true; break;
            case Seg.ScanChromaAuto:
              // Robot36: the separator tone names the chroma (1500 = R-Y, 2300 = B-Y, retro item M);
              // an ambiguous read falls back to the nominal even/odd alternation.
              bool ry = sepFreq < 1700 || (sepFreq < 2100 && (line & 1) == 0);
              if (ry) { ReadScan(brightness, segStart, n, w, o, cr, line * w); hasCr[line] = true; }
              else { ReadScan(brightness, segStart, n, w, o, cb, line * w); hasCb[line] = true; }
              break;
          }
        }
      }

      FillMissingChroma(cr, hasCr, w, h);
      FillMissingChroma(cb, hasCb, w, h);

      var img = new RgbImage(w, h);
      for (int row = 0; row < h; row++)
        for (int x = 0; x < w; x++)
        {
          int i = row * w + x;
          var (r, g, b) = YCrCb.ToRgb(y[i], cr[i], cb[i]);
          img.Set(x, row, (byte)Math.Round(r), (byte)Math.Round(g), (byte)Math.Round(b));
        }
      return img;
    }

    /// <summary>PD layout: each transmitted line is sync → porch → Y(even row) → R-Y → B-Y → Y(odd row),
    /// no separators, and the one chroma pair is shared by the two luma rows (plan §1.8, §2).</summary>
    private static RgbImage ReconstructPd(double[] brightness, SstvModeSpec spec, SstvDecodeOptions o,
      double[] lineOnset, double corr)
    {
      int w = spec.Width, h = spec.Height;
      double fs = o.SampleRate * corr;                       // clock-corrected pixel time scale (retro F)
      var y = new double[h * w];
      var cr = new double[h * w];
      var cb = new double[h * w];

      int Ms(double ms) => (int)Math.Round(ms / 1000.0 * fs);
      for (int line = 0; line < spec.LineCount && 2 * line + 1 < h; line++)
      {
        int rowA = 2 * line, rowB = 2 * line + 1;
        double cursor = lineOnset[line] + Ms(spec.SyncMs) + Ms(spec.SyncPorchMs);   // skip this line's sync+porch

        int n = Ms(spec.ScanYMs); ReadScan(brightness, (int)Math.Round(cursor), n, w, o, y, rowA * w); cursor += n;
        n = Ms(spec.ScanChromaMs); ReadScan(brightness, (int)Math.Round(cursor), n, w, o, cr, rowA * w); cursor += n;
        n = Ms(spec.ScanChromaMs); ReadScan(brightness, (int)Math.Round(cursor), n, w, o, cb, rowA * w); cursor += n;
        n = Ms(spec.ScanYMs); ReadScan(brightness, (int)Math.Round(cursor), n, w, o, y, rowB * w); cursor += n;

        Array.Copy(cr, rowA * w, cr, rowB * w, w);           // one chroma pair serves both rows
        Array.Copy(cb, rowA * w, cb, rowB * w, w);
      }

      var img = new RgbImage(w, h);
      for (int row = 0; row < h; row++)
        for (int x = 0; x < w; x++)
        {
          int i = row * w + x;
          var (r, g, b) = YCrCb.ToRgb(y[i], cr[i], cb[i]);
          img.Set(x, row, (byte)Math.Round(r), (byte)Math.Round(g), (byte)Math.Round(b));
        }
      return img;
    }

    /// <summary>Matched integrator: average the brightness over the centered fraction of each pixel's sample
    /// span, map Hz→value, store into <paramref name="dst"/> at <paramref name="rowOffset"/>.</summary>
    private static void ReadScan(double[] brightness, int segStart, int n, int w, SstvDecodeOptions o,
      double[] dst, int rowOffset)
    {
      if (n <= 0) return;
      double frac = Math.Clamp(o.PixelWindowFraction, 0.05, 1.0);
      for (int p = 0; p < w; p++)
      {
        long iLo = (long)Math.Ceiling((double)p * n / w);
        long iHi = (long)Math.Ceiling((double)(p + 1) * n / w);
        if (iHi <= iLo) iHi = iLo + 1;
        long span = iHi - iLo;
        long trim = (long)(span * (1 - frac) / 2);
        long a = iLo + trim, b = iHi - trim;
        if (b <= a) { a = iLo; b = iHi; }

        double sum = 0; int cnt = 0;
        for (long i = a; i < b; i++)
        {
          int idx = segStart + (int)i;
          if (idx >= 0 && idx < brightness.Length) { sum += brightness[idx]; cnt++; }
        }
        double f = cnt > 0 ? sum / cnt : SstvTones.Center;
        dst[rowOffset + p] = SstvTones.FreqToValue(f);
      }
    }

    /// <summary>Mean brightness frequency (Hz) over the central half of a segment — used to read the
    /// Robot36 separator tone (1500 = R-Y line, 2300 = B-Y line) while skipping the edge transitions.</summary>
    private static double SegmentFreq(double[] brightness, int segStart, int n)
    {
      int a = segStart + n / 4, b = segStart + 3 * n / 4;
      double sum = 0; int cnt = 0;
      for (int i = a; i < b; i++)
        if (i >= 0 && i < brightness.Length) { sum += brightness[i]; cnt++; }
      return cnt > 0 ? sum / cnt : 0;
    }

    /// <summary>Robot36 sends a given chroma only on alternate lines; fill each missing row from its nearest
    /// neighbor that carries it (vertical chroma upsampling).</summary>
    private static void FillMissingChroma(double[] chroma, bool[] has, int w, int h)
    {
      for (int row = 0; row < h; row++)
      {
        if (has[row]) continue;
        int src = -1;
        for (int d = 1; d < h; d++)
        {
          if (row - d >= 0 && has[row - d]) { src = row - d; break; }
          if (row + d < h && has[row + d]) { src = row + d; break; }
        }
        if (src < 0) continue;                             // no chroma anywhere (e.g. Robot72 fills every row)
        Array.Copy(chroma, src * w, chroma, row * w, w);
      }
    }
  }
}
