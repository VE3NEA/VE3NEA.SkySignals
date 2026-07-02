using System;
using System.Collections.Generic;
using MathNet.Numerics;

namespace VE3NEA.SkySSTV
{
  /// <summary>
  /// Synthetic SSTV modulator (plan §1.12): image → SSTV subcarrier audio → FM → complex IQ, with
  /// optional Doppler / slant / noise. It is the inverse of the decoder and provides exact ground
  /// truth for closed-loop tests. Pure managed (no VE3NEA.Dsp natives): a phase-continuous subcarrier
  /// oscillator drives a phase-continuous FM carrier, matching the existing GmskModulator test fixture.
  /// </summary>
  public static class SstvEncoder
  {
    /// <summary>Encode <paramref name="image"/> in <paramref name="mode"/> to complex IQ at the option's
    /// sample rate. The image is sampled to the mode's transmit dimensions (nearest-neighbor).</summary>
    public static Complex32[] Encode(RgbImage image, SstvMode mode, SstvEncoderOptions? options = null)
    {
      var o = options ?? new SstvEncoderOptions();
      float[] audio = EncodeAudio(image, mode, o);
      return FmModulate(audio, o);
    }

    /// <summary>Build just the real SSTV subcarrier audio (±1), before FM. Exposed for tests that want
    /// to inspect the subcarrier directly.</summary>
    internal static float[] EncodeAudio(RgbImage image, SstvMode mode, SstvEncoderOptions o)
    {
      var spec = SstvModes.Get(mode);
      double fs = o.SampleRate;
      double timeScale = 1.0 + o.SlantPpm * 1e-6;   // slant: uniformly stretch every segment
      var audio = new List<float>(EstimateSamples(spec, fs));
      double phase = 0.0;                            // subcarrier phase, continuous across segments
      double ideal = 0.0;                            // ideal (scaled) end time in samples, continuous
                                                     // across segments so ppm-level slant is not rounded
                                                     // away per segment (retro item K)

      if (o.IncludeVis) EmitVis(audio, spec, fs, timeScale, ref phase, ref ideal);

      for (int line = 0; line < spec.LineCount; line++)
        EmitLine(audio, image, spec, line, fs, timeScale, ref phase, ref ideal);

      return audio.ToArray();
    }


    // ----------------------------------------------------------------------------------------------------
    //                                          line / scan emit
    // ----------------------------------------------------------------------------------------------------


    private static void EmitLine(List<float> audio, RgbImage image, SstvModeSpec spec, int line,
      double fs, double timeScale, ref double phase, ref double ideal)
    {
      EmitTone(audio, SstvTones.Sync, spec.SyncMs, fs, timeScale, ref phase, ref ideal);
      EmitTone(audio, SstvTones.Black, spec.SyncPorchMs, fs, timeScale, ref phase, ref ideal);

      switch (spec.Layout)
      {
        case SstvColorLayout.Robot36:
          {
            int row = line;
            EmitScan(audio, Component(image, spec, row, Comp.Y), spec.ScanYMs, fs, timeScale, ref phase, ref ideal);
            bool ry = (line & 1) == 0;               // even lines carry R-Y, odd lines B-Y
            EmitTone(audio, ry ? SstvTones.Black : SstvTones.White, spec.SepMs, fs, timeScale, ref phase, ref ideal);
            EmitTone(audio, SstvTones.Center, spec.SepPorchMs, fs, timeScale, ref phase, ref ideal);
            EmitScan(audio, Component(image, spec, row, ry ? Comp.Cr : Comp.Cb), spec.ScanChromaMs, fs, timeScale, ref phase, ref ideal);
            break;
          }
        case SstvColorLayout.Robot72:
          {
            int row = line;
            EmitScan(audio, Component(image, spec, row, Comp.Y), spec.ScanYMs, fs, timeScale, ref phase, ref ideal);
            EmitTone(audio, SstvTones.Black, spec.SepMs, fs, timeScale, ref phase, ref ideal);
            EmitTone(audio, SstvTones.Center, spec.SepPorchMs, fs, timeScale, ref phase, ref ideal);
            EmitScan(audio, Component(image, spec, row, Comp.Cr), spec.ScanChromaMs, fs, timeScale, ref phase, ref ideal);
            EmitTone(audio, SstvTones.White, spec.SepMs, fs, timeScale, ref phase, ref ideal);
            EmitTone(audio, SstvTones.Center, spec.SepPorchMs, fs, timeScale, ref phase, ref ideal);
            EmitScan(audio, Component(image, spec, row, Comp.Cb), spec.ScanChromaMs, fs, timeScale, ref phase, ref ideal);
            break;
          }
        case SstvColorLayout.Pd:
          {
            int rowA = 2 * line, rowB = 2 * line + 1;
            EmitScan(audio, Component(image, spec, rowA, Comp.Y), spec.ScanYMs, fs, timeScale, ref phase, ref ideal);
            EmitScan(audio, ComponentAvg(image, spec, rowA, rowB, Comp.Cr), spec.ScanChromaMs, fs, timeScale, ref phase, ref ideal);
            EmitScan(audio, ComponentAvg(image, spec, rowA, rowB, Comp.Cb), spec.ScanChromaMs, fs, timeScale, ref phase, ref ideal);
            EmitScan(audio, Component(image, spec, rowB, Comp.Y), spec.ScanYMs, fs, timeScale, ref phase, ref ideal);
            break;
          }
      }
    }

    /// <summary>Samples to emit so the stream ends at the ideal (scaled) time of this segment's end:
    /// the cursor accumulates in doubles and each segment emits the rounding remainder, so slant is
    /// faithful at any ppm instead of being quantized per segment (retro item K).</summary>
    private static int NextSegmentSamples(List<float> audio, double ms, double fs, double timeScale, ref double ideal)
    {
      ideal += ms / 1000.0 * fs * timeScale;
      return Math.Max(0, (int)Math.Round(ideal) - audio.Count);
    }

    /// <summary>Emit a constant-frequency tone for <paramref name="ms"/> milliseconds.</summary>
    private static void EmitTone(List<float> audio, double freq, double ms, double fs, double timeScale,
      ref double phase, ref double ideal)
    {
      int n = NextSegmentSamples(audio, ms, fs, timeScale, ref ideal);
      double step = 2 * Math.PI * freq / fs;
      for (int i = 0; i < n; i++) { phase += step; audio.Add((float)Math.Sin(phase)); }
    }

    /// <summary>Emit an active scan: sweep the subcarrier through the pixel values of one component
    /// row (length <see cref="SstvModeSpec.Width"/>) over <paramref name="ms"/> milliseconds.</summary>
    private static void EmitScan(List<float> audio, double[] values, double ms, double fs, double timeScale,
      ref double phase, ref double ideal)
    {
      int n = NextSegmentSamples(audio, ms, fs, timeScale, ref ideal);
      int w = values.Length;
      for (int i = 0; i < n; i++)
      {
        int px = (int)((long)i * w / n);
        if (px >= w) px = w - 1;
        double freq = SstvTones.ValueToFreq(values[px]);
        phase += 2 * Math.PI * freq / fs;
        audio.Add((float)Math.Sin(phase));
      }
    }


    // ----------------------------------------------------------------------------------------------------
    //                                             VIS header
    // ----------------------------------------------------------------------------------------------------


    private static void EmitVis(List<float> audio, SstvModeSpec spec, double fs, double timeScale,
      ref double phase, ref double ideal)
    {
      EmitTone(audio, SstvTones.Center, SstvTones.VisLeaderMs, fs, timeScale, ref phase, ref ideal);
      EmitTone(audio, SstvTones.Sync, SstvTones.VisBreakMs, fs, timeScale, ref phase, ref ideal);
      EmitTone(audio, SstvTones.Center, SstvTones.VisLeaderMs, fs, timeScale, ref phase, ref ideal);

      EmitTone(audio, SstvTones.VisStartStop, SstvTones.VisBitMs, fs, timeScale, ref phase, ref ideal);   // start bit
      int code = spec.VisCode & 0x7F;
      int ones = 0;
      for (int b = 0; b < 7; b++)                     // 7 data bits, LSB first
      {
        int bit = (code >> b) & 1;
        ones += bit;
        EmitTone(audio, bit == 1 ? SstvTones.VisBitOne : SstvTones.VisBitZero, SstvTones.VisBitMs, fs, timeScale, ref phase, ref ideal);
      }
      int parity = ones & 1;                          // even parity across the 7 data bits
      EmitTone(audio, parity == 1 ? SstvTones.VisBitOne : SstvTones.VisBitZero, SstvTones.VisBitMs, fs, timeScale, ref phase, ref ideal);
      EmitTone(audio, SstvTones.VisStartStop, SstvTones.VisBitMs, fs, timeScale, ref phase, ref ideal);   // stop bit
    }


    // ----------------------------------------------------------------------------------------------------
    //                                          FM modulation
    // ----------------------------------------------------------------------------------------------------


    private static Complex32[] FmModulate(float[] audio, SstvEncoderOptions o)
    {
      double fs = o.SampleRate;
      var iq = new Complex32[audio.Length];
      double phase = 0.0;
      double dcStep = 2 * Math.PI * o.DopplerHz / fs;      // constant offset → DC on recovered audio
      double devStep = 2 * Math.PI * o.DeviationHz / fs;   // audio ±1 → ±deviation
      for (int i = 0; i < audio.Length; i++)
      {
        phase += dcStep + devStep * audio[i];
        iq[i] = new Complex32((float)Math.Cos(phase), (float)Math.Sin(phase));
      }
      if (o.NoiseStdDev > 0) AddNoise(iq, o.NoiseStdDev, o.NoiseSeed);
      return iq;
    }

    private static void AddNoise(Complex32[] iq, double sigma, int seed)
    {
      var rng = new Random(seed);
      for (int i = 0; i < iq.Length; i++)
      {
        double gr = Gauss(rng) * sigma, gi = Gauss(rng) * sigma;
        iq[i] = new Complex32(iq[i].Real + (float)gr, iq[i].Imaginary + (float)gi);
      }
    }

    private static double Gauss(Random r)
    {
      double u1 = 1.0 - r.NextDouble(), u2 = 1.0 - r.NextDouble();
      return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }


    // ----------------------------------------------------------------------------------------------------
    //                                       component extraction
    // ----------------------------------------------------------------------------------------------------


    private enum Comp { Y, Cr, Cb }

    /// <summary>One component row at the mode's transmit width, sampled from the image (nearest).</summary>
    private static double[] Component(RgbImage image, SstvModeSpec spec, int modeRow, Comp comp)
    {
      var values = new double[spec.Width];
      int imgY = MapRow(modeRow, spec.Height, image.Height);
      for (int px = 0; px < spec.Width; px++)
      {
        int imgX = (int)((long)px * image.Width / spec.Width);
        if (imgX >= image.Width) imgX = image.Width - 1;
        var (r, g, b) = image.Get(imgX, imgY);
        values[px] = Select(comp, r, g, b);
      }
      return values;
    }

    /// <summary>Chroma row averaged over two image rows (PD shares one chroma pair per two luma rows).</summary>
    private static double[] ComponentAvg(RgbImage image, SstvModeSpec spec, int modeRowA, int modeRowB, Comp comp)
    {
      var a = Component(image, spec, modeRowA, comp);
      var b = Component(image, spec, modeRowB, comp);
      var values = new double[a.Length];
      for (int i = 0; i < a.Length; i++) values[i] = 0.5 * (a[i] + b[i]);
      return values;
    }

    private static double Select(Comp comp, byte r, byte g, byte b)
    {
      var (y, cr, cb) = YCrCb.FromRgb(r, g, b);
      return comp switch { Comp.Y => y, Comp.Cr => cr, _ => cb };
    }

    private static int MapRow(int modeRow, int modeHeight, int imageHeight)
    {
      if (modeHeight == imageHeight) return modeRow;
      int y = (int)((long)modeRow * imageHeight / modeHeight);
      return y >= imageHeight ? imageHeight - 1 : y;
    }


    // ----------------------------------------------------------------------------------------------------
    //                                             utilities
    // ----------------------------------------------------------------------------------------------------


    private static int EstimateSamples(SstvModeSpec spec, double fs)
    {
      double totalMs = spec.LineCount * spec.LinePeriodMs
                     + (SstvTones.VisLeaderMs * 2 + SstvTones.VisBreakMs + SstvTones.VisBitMs * 10);
      return (int)(totalMs / 1000.0 * fs) + 16;
    }
  }
}
