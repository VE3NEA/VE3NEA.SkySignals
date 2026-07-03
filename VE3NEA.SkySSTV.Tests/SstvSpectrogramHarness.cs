using System;
using System.IO;
using MathNet.Numerics;
using ScottPlot;
using VE3NEA.SkyTlm.Dsp;
using VE3NEA.SkyTlm.IO;
using Xunit;
using Xunit.Abstractions;

namespace VE3NEA.SkySSTV.Tests
{
  /// <summary>
  /// Spectrogram-inspection harness: render an SSTV <c>.iq.wav</c> capture to a PNG a coding assistant can
  /// open and read, using the same ScottPlot Viridis heatmap as the FskDemod reference tool
  /// (<c>C:\Proj\Try\FskDemod</c>). Two complementary views per capture:
  /// <list type="bullet">
  /// <item><b>RF</b> — the raw complex IQ STFT (−Fs/2…+Fs/2), the FskDemod view: shows the FM carrier, its
  /// Carson width / deviation, whether it is Doppler-centered, and any interfering bursts.</item>
  /// <item><b>Audio</b> — the STFT of the discriminated audio (0…3 kHz), the SSTV-relevant view: the
  /// 1200 Hz sync ladder, the VIS header, and the 1500–2300 Hz brightness sweep, with the reference tones
  /// drawn as faint horizontal lines so a feature's frequency reads off directly.</item>
  /// </list>
  /// A <see cref="ManualFact"/> (opt-in via <c>SSTV_RUN_MANUAL=1</c>): it processes multi-hundred-MB real
  /// captures, too heavy for the normal suite.
  /// </summary>
  public class SstvSpectrogramHarness
  {
    private static readonly string RecordingsDir =
      @"C:\Users\alsho\AppData\Roaming\Afreet\Products\SkyRoof\Recordings\SSTV";
    private static readonly string OutDir = Path.Combine(RecordingsDir, "spectrograms");

    private readonly ITestOutputHelper output;
    public SstvSpectrogramHarness(ITestOutputHelper o) => output = o;

    [ManualFact("Spectrogram-inspection harness: writes <stem>_rf.png (raw IQ, the FskDemod view) and " +
      "<stem>_audio.png (discriminated audio 0-3 kHz, sync ladder + tone lines) for every capture, for a " +
      "coding assistant to open. Set SSTV_ONE=<substring> to render just one capture.")]
    public void Real_SpectrogramProbe()
    {
      if (!Directory.Exists(RecordingsDir))
      {
        output.WriteLine($"recordings folder absent ({RecordingsDir}); skipping");
        return;
      }
      Directory.CreateDirectory(OutDir);

      // SSTV_ONE=<substring> narrows the run to one capture (the whole corpus is many GB of PNGs otherwise).
      string? only = Environment.GetEnvironmentVariable("SSTV_ONE");

      foreach (string wav in Directory.GetFiles(RecordingsDir, "*.iq.wav"))
      {
        if (only != null && !Path.GetFileName(wav).Contains(only, StringComparison.OrdinalIgnoreCase)) continue;
        try
        {
          var (iq, sr) = WavIqReader.Read(wav);
          string stem = Path.GetFileNameWithoutExtension(wav);   // strips .wav; keeps .iq
          output.WriteLine($"{stem}: {iq.Length:N0} IQ samples @ {sr} Hz, {iq.Length / sr:0.0} s");

          string rf = Path.Combine(OutDir, $"{stem}_rf.png");
          RenderIq(iq, sr, $"{stem}  —  raw IQ (RF)", rf);
          output.WriteLine($"  RF    -> {Path.GetFileName(rf)}");

          double[] disc = SstvDecoder.Discriminator(iq, new SstvDecodeOptions { SampleRate = sr });
          string au = Path.Combine(OutDir, $"{stem}_audio.png");
          RenderAudio(disc, sr, $"{stem}  —  discriminated audio", au);
          output.WriteLine($"  audio -> {Path.GetFileName(au)}");
        }
        catch (Exception ex) { output.WriteLine($"{Path.GetFileName(wav)}: {ex.GetType().Name} {ex.Message}"); }
      }
    }


    // ----------------------------------------------------------------------------------------------------
    //                                            rendering
    // ----------------------------------------------------------------------------------------------------


    /// <summary>Full-band complex-IQ spectrogram (−Fs/2…+Fs/2) — the FskDemod RF view.</summary>
    private static void RenderIq(Complex32[] iq, double sr, string title, string path)
    {
      var spec = Spectrogram.Compute(iq, sr, fftSize: 1024);
      var plot = NewPlot();
      Heatmap(plot, spec, -sr / 2, sr / 2);
      plot.Axes.Title.Label.Text = title;
      plot.Axes.Bottom.Label.Text = "Time (s)";
      plot.Axes.Left.Label.Text = "Frequency (Hz)";
      plot.Axes.AutoScale();
      plot.Axes.Margins(0, 0);
      plot.SavePng(path, 1600, 700);
    }

    /// <summary>Discriminated-audio spectrogram cropped to the 0…3 kHz SSTV band, with the reference tones
    /// (1200 sync, 1500 black, 1900 center, 2300 white) as faint horizontal lines. The audio is real, so its
    /// spectrogram is symmetric about 0 — the positive half carries the subcarrier.</summary>
    private static void RenderAudio(double[] disc, double sr, string title, string path)
    {
      var cx = new Complex32[disc.Length];
      for (int i = 0; i < disc.Length; i++) cx[i] = new Complex32((float)disc[i], 0f);

      var spec = Spectrogram.Compute(cx, sr, fftSize: 2048);   // finer bins for the narrow tone band
      var plot = NewPlot();
      Heatmap(plot, spec, -sr / 2, sr / 2);

      // faint reference lines at the SSTV tones (sync 1200, black 1500, center 1900, white 2300); the
      // frequency axis names them, so no text label (which clips at the frame edge).
      foreach (double freq in new[] { SstvTones.Sync, SstvTones.Black, SstvTones.Center, SstvTones.White })
      {
        var line = plot.Add.HorizontalLine(freq);
        line.Color = Colors.White.WithAlpha(0.30);
        line.LineWidth = 1;
      }

      plot.Axes.Title.Label.Text = title;
      plot.Axes.Bottom.Label.Text = "Time (s)";
      plot.Axes.Left.Label.Text = "Frequency (Hz)";
      plot.Axes.SetLimits(0, spec.DurationSeconds, 0, 3000);   // subcarrier band only
      plot.SavePng(path, 1600, 700);
    }

    /// <summary>Add the Viridis heatmap with the percentile color range and time/frequency extent (modeled
    /// on the FskDemod <c>SpectrogramPlot.Render</c> so the PNGs match the reference).</summary>
    private static void Heatmap(Plot plot, SpectrogramResult spec, double yLo, double yHi)
    {
      var hm = plot.Add.Heatmap(spec.Db);
      hm.Colormap = new ScottPlot.Colormaps.Viridis();
      hm.ManualRange = new ScottPlot.Range(spec.DbLow, spec.DbHigh);   // noise floor → dark, peaks → bright
      hm.Extent = new CoordinateRect(0, spec.DurationSeconds, yLo, yHi);
    }

    private static Plot NewPlot()
    {
      var plot = new Plot();
      plot.FigureBackground.Color = ScottPlot.Color.FromHex("#101010");
      plot.DataBackground.Color = ScottPlot.Color.FromHex("#101010");
      plot.Axes.Color(Colors.LightGray);
      return plot;
    }
  }
}
