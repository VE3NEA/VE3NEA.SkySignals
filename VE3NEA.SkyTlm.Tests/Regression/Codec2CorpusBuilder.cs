using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MathNet.Numerics;
using VE3NEA.SkyTlm.Audio.Codec2;
using VE3NEA.SkyTlm.Core;
using VE3NEA.SkyTlm.IO;
using Xunit;

namespace VE3NEA.SkyTlm.Tests.Regression
{
  /// <summary>
  /// Generator for <c>Data/Wav/fsk_hades_codec2_HADES-SA.wav</c>, the voice corpus clip — the audio twin
  /// of <see cref="SsdvCorpusBuilder"/>. Not a test: it reads a quarter-gigabyte recording that is not in
  /// the repo, so it is skipped by default and run by hand when the clip needs rebuilding.
  /// <para>
  /// Selection is by packet type, for the same reason as SSDV: the decode-regression clip holds a pass's
  /// median-SNR bursts, and on HADES-SA those carry telemetry. What differs from imaging is the shape of
  /// the target. SSDV packets of one image arrive back to back, so the builder could look for a run;
  /// voice sub-frames do not — measured over four passes they arrive one per beacon slot, tens of seconds
  /// apart, so a whole message spans minutes and cannot be clipped. The window is therefore chosen for
  /// sub-frame density rather than for message completeness, and the clip proves the chain end to end
  /// while holding only a fragment of a message.
  /// </para>
  /// </summary>
  public class Codec2CorpusBuilder
  {
    private const string RecordingDir =
      @"C:\Users\alsho\AppData\Roaming\Afreet\Products\SkyRoof\Recordings";

    /// <summary>The pass to cut from. Of the six HADES-SA captures this one carries the most type-11
    /// frames — 60, against 49 / 33 / 32 / 1 / 0 for the others (measured 2026-08-03 with
    /// <c>FskDemod --headless --stream &lt;file&gt; --gated</c>, whose per-type histogram exists for
    /// exactly this question).</summary>
    private const string Recording = "2026-04-17_14_54_53_HADES-SA.iq.wav";

    /// <summary>Size budget for the clip, in seconds of 48 kHz IQ (~0.4 MB/s). Ample: the transmission
    /// this cuts from runs 15 s, which is the operator's own cap on a stored message.</summary>
    private const double MaxSeconds = 20;
    private const double MarginSeconds = 0.5;

    [Fact(Skip = "corpus generator, not a test — remove Skip and run explicitly to rebuild the clip")]
    public void BuildCodec2Clip()
    {
      var (samples, fs) = WavIqReader.Read(Path.Combine(RecordingDir, Recording));
      var p = new SignalParams(Baud: 800, Modulation.FSK, Framing.HADES, SampleRate: fs, Deviation: 800);

      // pass 1: find the bursts carrying voice, and which sub-frames each one contributes. The gate is
      // the assembler's own — a 37-byte type-11 frame — because a burst holding a type-10 or type-13
      // packet contributes nothing here however strong it is.
      var spans = new List<Span>();
      using (var sp = new StreamingPipeline(p, new StreamingOptions()))
      {
        sp.BurstDecoded += r =>
        {
          var numbers = r.Frames
            .Select(f => Codec2Source.HadesSa.TryExtract(f, out int n, out _) ? n : -1)
            .Where(n => n >= 0)
            .Distinct().ToList();
          if (numbers.Count > 0) spans.Add(new Span(r.StartSample, r.Length, r.TimeSeconds, numbers));
        };
        Feed(sp, samples, fs);
      }

      // the pipeline reports a burst when it closes, and a long burst can close after a short one that
      // started later — so the detection order is not the on-air order the window logic assumes.
      spans.Sort((a, b) => a.Start.CompareTo(b.Start));

      Console.WriteLine($"{spans.Count} voice-bearing bursts in {samples.Length / (double)fs:F0} s:");
      foreach (var s in spans)
        Console.WriteLine($"   t={s.Time,7:F1}s  len={s.Length / (double)fs,5:F1}s  " +
                          $"sub-frames {string.Join(",", s.Numbers)}");

      // Cut one contiguous window rather than splicing bursts: the detector then sees the real
      // inter-burst gaps, and no decode-window overlap is duplicated. Slide the budget over the bursts
      // and keep the placement holding the most *distinct* sub-frames — counting repeats instead would
      // pay seconds of clip for a sub-frame the window already has, since overlapping decode windows
      // re-deliver the same numbers.
      int bestAt = 0, bestCount = 0;
      for (int i = 0; i < spans.Count; i++)
      {
        int count = Distinct(Window(spans, i, fs)).Count;
        if (count > bestCount) { bestCount = count; bestAt = i; }
      }

      // ...then drop trailing bursts that add nothing new, so the clip is the shortest one that still
      // holds every sub-frame the budget could reach.
      var kept = Window(spans, bestAt, fs);
      while (kept.Count > 1 && Distinct(kept.GetRange(0, kept.Count - 1)).Count == bestCount)
        kept.RemoveAt(kept.Count - 1);

      Console.WriteLine($"best window: {kept.Count} burst(s) from t={kept[0].Time:F1}s carrying " +
                        $"{bestCount} distinct sub-frame(s) -> {string.Join(" ", Distinct(kept))}");

      long first = kept[0].Start, last = kept[^1].Start + kept[^1].Length;
      int margin = (int)(MarginSeconds * fs);
      int from = (int)Math.Max(0, first - margin);
      int to = (int)Math.Min(samples.Length, last + margin);
      var cut = samples[from..to];

      string outPath = Path.Combine(TestPaths.WavDir, "fsk_hades_codec2_HADES-SA.wav");
      WriteIqWav(outPath, cut, fs);
      SignalParamsSidecar.Save(outPath + ".json", p);

      Console.WriteLine($"wrote {cut.Length} samples ({cut.Length / (double)fs:F1} s, "
                      + $"{cut.Length * 8 / 1e6:F1} MB) to {outPath}");
    }

    /// <summary>The bursts from <paramref name="at"/> onwards that fit inside the size budget.</summary>
    private static List<Span> Window(List<Span> spans, int at, int fs)
    {
      var window = new List<Span>();
      for (int j = at; j < spans.Count; j++)
      {
        if ((spans[j].Start + spans[j].Length - spans[at].Start) / (double)fs > MaxSeconds) break;
        window.Add(spans[j]);
      }
      return window;
    }

    private static List<int> Distinct(List<Span> window) =>
      window.SelectMany(s => s.Numbers).Distinct().Order().ToList();

    /// <summary>One detected burst that carried voice, and the sub-frame numbers it yielded.</summary>
    private readonly record struct Span(long Start, int Length, double Time, List<int> Numbers);

    private static void Feed(StreamingPipeline sp, Complex32[] samples, int fs)
    {
      int block = Math.Max(1, (int)(0.1 * fs));
      for (int i = 0; i < samples.Length; i += block)
        sp.Push(samples.AsSpan(i, Math.Min(block, samples.Length - i)));
      sp.Flush();
    }

    /// <summary>Minimal IEEE-float stereo WAV, the format <see cref="WavIqReader"/> reads.</summary>
    private static void WriteIqWav(string path, Complex32[] samples, int sampleRate)
    {
      using var w = new BinaryWriter(File.Create(path));
      int dataBytes = samples.Length * 8;

      w.Write("RIFF"u8); w.Write(36 + dataBytes); w.Write("WAVE"u8);
      w.Write("fmt "u8); w.Write(16);
      w.Write((short)3);                      // IEEE float
      w.Write((short)2);                      // I and Q
      w.Write(sampleRate);
      w.Write(sampleRate * 8);                // byte rate
      w.Write((short)8);                      // block align
      w.Write((short)32);                     // bits per sample
      w.Write("data"u8); w.Write(dataBytes);

      foreach (var s in samples) { w.Write(s.Real); w.Write(s.Imaginary); }
    }
  }
}
