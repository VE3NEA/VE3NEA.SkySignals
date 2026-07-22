using System;
using System.Globalization;
using System.IO;
using System.Text;
using FluentAssertions;
using VE3NEA.SkyTlm.IO;
using Xunit;
using Xunit.Abstractions;

namespace VE3NEA.SkyFM.Tests
{
  /// <summary>
  /// Real-capture harness (Milestone 1): demodulate every FM <c>.iq.wav</c> recording, write the 16 kHz
  /// voice WAV, the transmission list, and one padded WAV clip per transmission — the material the
  /// operator labels into the corpus JSON. Prints the segment table; on the ARISS reference capture also
  /// checks the spike's 2:28 KQ4GIK/EM85 burst (147–152 s) is covered by a segment.
  /// </summary>
  public class FmDemodHarness
  {
    private static readonly string RecordingsDir =
      @"C:\Users\alsho\AppData\Roaming\Afreet\Products\SkyRoof\Recordings\FM";
    private static readonly string OutDir = Path.Combine(RecordingsDir, "decoded");

    private readonly ITestOutputHelper output;
    public FmDemodHarness(ITestOutputHelper o) => output = o;

    [ManualFact("2026-07-18: all 9 recordings decoded to Recordings\\FM\\decoded (WAV + segments + clips); " +
      "ARISS 2026-07-04: 56 transmissions, 2:28 KQ4GIK/EM85 target covered; dead SO-50 22_45_37: 0 segments")]
    public void Real_DemodAndSegment()
    {
      Directory.Exists(RecordingsDir).Should().BeTrue($"recordings expected under {RecordingsDir}");
      Directory.CreateDirectory(OutDir);

      int processed = 0;
      foreach (string path in Directory.GetFiles(RecordingsDir, "*.iq.wav"))
      {
        try { DecodeOne(path); processed++; }
        catch (Exception ex) { output.WriteLine($"FAILED {Path.GetFileName(path)}: {ex.Message}"); }
      }
      processed.Should().BeGreaterThan(0);
    }

    [ManualFact("§13 M6 fan-out variants of the ARISS reference capture: @pad40 (SegmentPadS 0.40 — " +
      "more onset context for the dropped-leading-Kilo class) and @sq85 (SquelchOpenLevel 0.085 — " +
      "opens on weaker carriers); clips + depths.json per variant dir, base decode untouched")]
    public void Real_DemodAriss_FanOutVariants()
    {
      string path = Path.Combine(RecordingsDir, "2026-07-04_23_03_57_ARISS.iq.wav");
      File.Exists(path).Should().BeTrue($"ARISS capture expected at {path}");
      Directory.CreateDirectory(OutDir);
      DecodeOne(path, "@pad40", o => o with { SegmentPadS = 0.40 });
      DecodeOne(path, "@sq85", o => o with { SquelchOpenLevel = 0.085 });
    }

    private void DecodeOne(string path, string suffix = "", Func<FmDecodeOptions, FmDecodeOptions>? variant = null)
    {
      string name = Path.GetFileName(path);
      if (name.EndsWith(".iq.wav", StringComparison.OrdinalIgnoreCase)) name = name[..^".iq.wav".Length];
      name += suffix;

      var (iq, sampleRate) = WavIqReader.Read(path);
      var options = new FmDecodeOptions { SampleRate = sampleRate };
      if (variant != null) options = variant(options);
      var res = FmDecoder.Decode(iq, options);

      double dur = (double)res.Voice.Length / res.SampleRate;
      double speech = 0;
      foreach (var t in res.Transmissions) speech += t.DurationSeconds;
      output.WriteLine($"{name}: {dur:0.0}s, {res.Transmissions.Count} transmissions, {speech:0.0}s keyed");

      // full-pass audio + segment list (start end duration quieting-depth-dB)
      Wav16.Write(Path.Combine(OutDir, $"{name}.wav"), res.Voice, res.SampleRate);
      var sb = new StringBuilder();
      var ci = CultureInfo.InvariantCulture;
      foreach (var t in res.Transmissions)
        sb.AppendLine(string.Format(ci, "{0,8:0.00} {1,8:0.00} {2,6:0.00} {3,6:0.0}",
          t.StartSeconds, t.EndSeconds, t.DurationSeconds, t.QuietingDepthDb));
      File.WriteAllText(Path.Combine(OutDir, $"{name}.segments.txt"), sb.ToString());

      // one clip per transmission, for listening / labeling / per-segment ASR; the clip → quieting
      // depth map feeds the §5.2 role-(b) confidence input at scoring time
      string clipDir = Path.Combine(OutDir, name);
      Directory.CreateDirectory(clipDir);
      var depths = new StringBuilder("{");
      for (int i = 0; i < res.Transmissions.Count; i++)
      {
        var t = res.Transmissions[i];
        int s = Math.Max(0, (int)(t.StartSeconds * res.SampleRate));
        int e = Math.Min(res.Voice.Length, (int)(t.EndSeconds * res.SampleRate));
        if (e <= s) continue;
        string clip = $"seg{i:00}_{t.StartSeconds:000.0}s.wav";
        Wav16.Write(Path.Combine(clipDir, clip), res.Voice.AsSpan(s, e - s), res.SampleRate);
        if (depths.Length > 1) depths.Append(',');
        depths.Append(string.Format(ci, "\n  \"{0}\": {1:0.00}", clip,
          double.IsNaN(t.QuietingDepthDb) ? -1.0 : t.QuietingDepthDb));
        output.WriteLine($"  seg{i:00}  {t.StartSeconds,7:0.00} – {t.EndSeconds,7:0.00}  " +
          $"({t.DurationSeconds:0.0}s)  depth {t.QuietingDepthDb:0.0} dB");
      }
      depths.Append("\n}");
      File.WriteAllText(Path.Combine(clipDir, "depths.json"), depths.ToString());

      // the spike's low-SNR reference burst: 2:28 KQ4GIK/EM85 at 147–152 s of the ARISS capture
      if (name.Contains("2026-07-04_23_03_57_ARISS"))
      {
        bool covered = false;
        foreach (var t in res.Transmissions)
          if (t.StartSeconds < 152.0 && t.EndSeconds > 148.0) covered = true;
        output.WriteLine($"  2:28 KQ4GIK/EM85 target covered: {(covered ? "YES" : "NO — squelch missed the weak burst")}");
      }
    }
  }
}
