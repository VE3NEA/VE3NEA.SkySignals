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
  public sealed partial class SstvDecoder
  {
    /// <summary>Decode <paramref name="iq"/> in <paramref name="mode"/> to an image. By default the start
    /// timing is acquired from the VIS header or the winning sync train; set
    /// <see cref="SstvDecodeOptions.Acquire"/> false to decode at the fixed
    /// <see cref="SstvDecodeOptions.StartSample"/>.</summary>
    public static RgbImage Decode(Complex32[] iq, SstvMode mode, SstvDecodeOptions? options = null)
    {
      // the video/decode chain runs its own narrower Stage-1 channel (P6(c) lock, 2026-07-03): detection
      // keeps ChannelBwHz, image quality wants VideoChannelBwHz + the blanker
      var o = options ?? new SstvDecodeOptions();
      return Decode(Discriminator(iq, o with { ChannelBwHz = o.VideoChannelBwHz }), mode, o);
    }

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
    /// extractor (plan §4.1) — the batch wrapper over the persistent <see cref="SstvDetectionChain"/>
    /// (P7.5: the chain is the production streaming path; this form remains for the whole-array callers
    /// and the test harness). Every found VIS hit seeds a high-prior train up front — equivalent to the
    /// streaming caller's just-in-time seeding, because pulses before a VIS anchor never associate to
    /// its train.</summary>
    internal static SstvPulseTrainExtractor ExtractTrains(double[] sync, double fs,
      IReadOnlyList<SstvVisResult>? visHits = null)
    {
      var chain = new SstvDetectionChain(fs);
      if (visHits != null)
        foreach (var hit in visHits)
          if (hit.Found && hit.Mode is SstvMode vm) chain.SeedVis(vm, hit.HeaderEndSample);
      chain.Process(sync);
      chain.Finish();
      return chain.Extractor;
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
      // thin wrapper over the streaming chain (P7.5(d)): one implementation, the batch shape kept for
      // the whole-array callers and the test harness (equivalence pinned by SstvStreamingStageTests)
      using var chain = new SstvStreamingDiscriminator(o, o.ChannelBwHz);
      var disc = new double[iq.Length];
      int at = CopySpan(chain.Process(iq), disc, 0);
      CopySpan(chain.Flush(), disc, at);
      return disc;
    }

    private static int CopySpan(ReadOnlySpan<double> src, double[] dst, int at)
    {
      src.CopyTo(dst.AsSpan(at));
      return at + src.Length;
    }

    /// <summary>Stage-2 audio bandpass (plan §3, retro item J): band-limit the discriminated audio to the
    /// SSTV tone band before the sync / VIS / mode statistics. The coherence statistic divides by total
    /// window energy, and the post-discriminator FM noise is parabolic in frequency, so without this stage
    /// the out-of-band noise inflates the denominator and crushes real-signal scores. A cosine-modulated
    /// BlackmanSinc low-pass gives a linear-phase bandpass; it also removes the DC Doppler term, unifying
    /// the DC handling of every detection path. The brightness path keeps its own complex low-pass.</summary>
    internal static double[] SyncAudio(double[] disc, double fs, SstvDecodeOptions o)
    {
      if (SyncBandKernel(o, fs) == null) return disc;         // stage disabled
      using var stage = new SyncBandpass(o, fs);              // thin wrapper over the streaming stage (P7.5)
      var outp = new double[disc.Length];
      int at = CopySpan(stage.Process(disc), outp, 0);
      CopySpan(stage.Flush(), outp, at);
      return outp;
    }

    /// <summary>The Stage-2 bandpass kernel (a cosine-modulated BlackmanSinc low-pass), shared by the
    /// batch <see cref="SyncAudio"/> and the streaming sync stage (P7.5). Null when the stage is disabled.</summary>
    internal static float[]? SyncBandKernel(SstvDecodeOptions o, double fs)
    {
      double lo = o.SyncBandLowHz, hi = o.SyncBandHighHz;
      if (hi <= lo || lo <= 0 || hi >= fs / 2) return null;

      double half = (hi - lo) / 2, f0 = (lo + hi) / 2;
      float[] lp = global::VE3NEA.Dsp.BlackmanSincKernel(half / fs, KernelTaps(half, fs));
      var bp = new float[lp.Length];
      int center = lp.Length / 2;
      double w0 = 2 * Math.PI * f0 / fs;
      for (int i = 0; i < lp.Length; i++) bp[i] = 2f * lp[i] * (float)Math.Cos(w0 * (i - center));
      return bp;
    }

    /// <summary>Per-sample brightness (subcarrier instantaneous frequency, Hz) from the discriminated audio —
    /// the <b>streaming</b> Stage-3 path (plan §1.4/§6.1, replaces the batch whole-signal FFT). Mix the real
    /// audio down by the 1900 Hz center (an NCO) so the video sits at baseband and its mirror at −3800 Hz,
    /// complex low-pass to <see cref="SstvDecodeOptions.BrightnessBwHz"/> (which also rejects the DC Doppler
    /// term the mix pushes to −1900), then instantaneous frequency + 1900. All bounded-state (NCO + FIR +
    /// one-sample diff) — no whole-signal transform.</summary>
    internal static double[] Brightness(double[] disc, double fs, SstvDecodeOptions o)
    {
      // thin wrapper over the streaming stage (P7.5(d)); equivalence pinned by SstvStreamingStageTests
      var opts = fs == o.SampleRate ? o : o with { SampleRate = fs };
      using var stage = new SstvStreamingBrightness(opts);
      var brightness = new double[disc.Length];
      int at = CopySpan(stage.Process(disc), brightness, 0);
      CopySpan(stage.Flush(), brightness, at);
      return brightness;
    }

    /// <summary>Odd tap count for a windowed-sinc LPF at <paramref name="cutoffHz"/>: ~4 cycles of the
    /// cutoff period, floored so the skirt is clean.</summary>
    internal static int KernelTaps(double cutoffHz, double fs)
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
      var bw = new BrightnessWindow(brightness, 0, brightness.Length);
      var y = new double[h * w];
      var cr = new double[h * w];
      var cb = new double[h * w];
      var hasCr = new bool[h];
      var hasCb = new bool[h];

      for (int line = 0; line < spec.LineCount && line < h; line++)
        RenderRobotLine(bw, spec, o, lineOnset[line], corr, line, y, cr, cb, hasCr, hasCb);

      FillMissingChroma(cr, hasCr, w, h);
      FillMissingChroma(cb, hasCb, w, h);
      if (o.WienerEnabled) SstvWienerFilter.Apply(y, cr, cb, w, h);

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
      var bw = new BrightnessWindow(brightness, 0, brightness.Length);
      var y = new double[h * w];
      var cr = new double[h * w];
      var cb = new double[h * w];

      for (int line = 0; line < spec.LineCount && 2 * line + 1 < h; line++)
        RenderPdLine(bw, spec, o, lineOnset[line], corr, line, y, cr, cb);

      if (o.WienerEnabled) SstvWienerFilter.Apply(y, cr, cb, w, h);

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

    /// <summary>One transmitted Robot line rendered onto the Y/Cr/Cb planes at row <paramref name="line"/> —
    /// shared by the batch reconstruction and the streaming image builder (P7.5). <paramref name="onset"/>
    /// is the line's tracked sync-onset sample (absolute, window coordinates).</summary>
    internal static void RenderRobotLine(in BrightnessWindow bw, SstvModeSpec spec, SstvDecodeOptions o,
      double onset, double corr, int line, double[] y, double[] cr, double[] cb, bool[] hasCr, bool[] hasCb)
    {
      int w = spec.Width;
      double fs = o.SampleRate * corr;                       // clock-corrected pixel time scale (retro F)
      double cursor = onset;                                 // each line re-anchored to its own sync onset
      double sepFreq = 0;                                    // separator tone (Hz) — names Robot36's chroma
      foreach (var (kind, ms) in LineSegments(spec, line))
      {
        int n = (int)Math.Round(ms / 1000.0 * fs);
        long segStart = (long)Math.Round(cursor);
        cursor += n;
        switch (kind)
        {
          case Seg.ScanY: ReadScan(bw, segStart, n, w, o, y, line * w); break;
          case Seg.Sep: sepFreq = SegmentFreq(bw, segStart, n); break;
          case Seg.ScanRY: ReadScan(bw, segStart, n, w, o, cr, line * w); hasCr[line] = true; break;
          case Seg.ScanBY: ReadScan(bw, segStart, n, w, o, cb, line * w); hasCb[line] = true; break;
          case Seg.ScanChromaAuto:
            // Robot36: the separator tone names the chroma (1500 = R-Y, 2300 = B-Y, retro item M);
            // an ambiguous read falls back to the nominal even/odd alternation.
            bool ry = sepFreq < 1700 || (sepFreq < 2100 && (line & 1) == 0);
            if (ry) { ReadScan(bw, segStart, n, w, o, cr, line * w); hasCr[line] = true; }
            else { ReadScan(bw, segStart, n, w, o, cb, line * w); hasCb[line] = true; }
            break;
        }
      }
    }

    /// <summary>One transmitted PD line (= two image rows sharing a chroma pair) rendered onto the planes —
    /// shared by the batch reconstruction and the streaming image builder (P7.5).</summary>
    internal static void RenderPdLine(in BrightnessWindow bw, SstvModeSpec spec, SstvDecodeOptions o,
      double onset, double corr, int line, double[] y, double[] cr, double[] cb)
    {
      int w = spec.Width;
      double fs = o.SampleRate * corr;                       // clock-corrected pixel time scale (retro F)
      int Ms(double ms) => (int)Math.Round(ms / 1000.0 * fs);
      int rowA = 2 * line, rowB = 2 * line + 1;
      double cursor = onset + Ms(spec.SyncMs) + Ms(spec.SyncPorchMs);   // skip this line's sync+porch

      int n = Ms(spec.ScanYMs); ReadScan(bw, (long)Math.Round(cursor), n, w, o, y, rowA * w); cursor += n;
      n = Ms(spec.ScanChromaMs); ReadScan(bw, (long)Math.Round(cursor), n, w, o, cr, rowA * w); cursor += n;
      n = Ms(spec.ScanChromaMs); ReadScan(bw, (long)Math.Round(cursor), n, w, o, cb, rowA * w); cursor += n;
      n = Ms(spec.ScanYMs); ReadScan(bw, (long)Math.Round(cursor), n, w, o, y, rowB * w); cursor += n;

      Array.Copy(cr, rowA * w, cr, rowB * w, w);             // one chroma pair serves both rows
      Array.Copy(cb, rowA * w, cb, rowB * w, w);
    }

    /// <summary>Matched integrator: average the brightness over the centered fraction of each pixel's sample
    /// span, map Hz→value, store into <paramref name="dst"/> at <paramref name="rowOffset"/>. Samples outside
    /// the window (fallen off the streaming ring, or past the current stream end) are skipped, exactly like
    /// the batch out-of-array guard.</summary>
    private static void ReadScan(in BrightnessWindow bw, long segStart, int n, int w, SstvDecodeOptions o,
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
          if (bw.TryGet(segStart + i, out double s)) { sum += s; cnt++; }
        double f = cnt > 0 ? sum / cnt : SstvTones.Center;
        dst[rowOffset + p] = SstvTones.FreqToValue(f);
      }
    }

    /// <summary>Mean brightness frequency (Hz) over the central half of a segment — used to read the
    /// Robot36 separator tone (1500 = R-Y line, 2300 = B-Y line) while skipping the edge transitions.</summary>
    private static double SegmentFreq(in BrightnessWindow bw, long segStart, int n)
    {
      long a = segStart + n / 4, b = segStart + 3 * n / 4;
      double sum = 0; int cnt = 0;
      for (long i = a; i < b; i++)
        if (bw.TryGet(i, out double s)) { sum += s; cnt++; }
      return cnt > 0 ? sum / cnt : 0;
    }

    /// <summary>Robot36 sends a given chroma only on alternate lines; fill each missing row from its nearest
    /// neighbor that carries it (vertical chroma upsampling).</summary>
    internal static void FillMissingChroma(double[] chroma, bool[] has, int w, int h)
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
