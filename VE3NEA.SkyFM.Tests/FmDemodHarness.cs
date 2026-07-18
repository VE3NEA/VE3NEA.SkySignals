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

    private void DecodeOne(string path)
    {
      string name = Path.GetFileName(path);
      if (name.EndsWith(".iq.wav", StringComparison.OrdinalIgnoreCase)) name = name[..^".iq.wav".Length];

      var (iq, sampleRate) = WavIqReader.Read(path);
      var res = FmDecoder.Decode(iq, new FmDecodeOptions { SampleRate = sampleRate });

      double dur = (double)res.Voice.Length / res.SampleRate;
      double speech = 0;
      foreach (var t in res.Transmissions) speech += t.DurationSeconds;
      output.WriteLine($"{name}: {dur:0.0}s, {res.Transmissions.Count} transmissions, {speech:0.0}s keyed");

      // full-pass audio + segment list
      Wav16.Write(Path.Combine(OutDir, $"{name}.wav"), res.Voice, res.SampleRate);
      var sb = new StringBuilder();
      var ci = CultureInfo.InvariantCulture;
      foreach (var t in res.Transmissions)
        sb.AppendLine(string.Format(ci, "{0,8:0.00} {1,8:0.00} {2,6:0.00}", t.StartSeconds, t.EndSeconds, t.DurationSeconds));
      File.WriteAllText(Path.Combine(OutDir, $"{name}.segments.txt"), sb.ToString());

      // one clip per transmission, for listening / labeling / per-segment ASR
      string clipDir = Path.Combine(OutDir, name);
      Directory.CreateDirectory(clipDir);
      for (int i = 0; i < res.Transmissions.Count; i++)
      {
        var t = res.Transmissions[i];
        int s = Math.Max(0, (int)(t.StartSeconds * res.SampleRate));
        int e = Math.Min(res.Voice.Length, (int)(t.EndSeconds * res.SampleRate));
        if (e <= s) continue;
        Wav16.Write(Path.Combine(clipDir, $"seg{i:00}_{t.StartSeconds:000.0}s.wav"),
          res.Voice.AsSpan(s, e - s), res.SampleRate);
        output.WriteLine($"  seg{i:00}  {t.StartSeconds,7:0.00} – {t.EndSeconds,7:0.00}  ({t.DurationSeconds:0.0}s)");
      }

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
