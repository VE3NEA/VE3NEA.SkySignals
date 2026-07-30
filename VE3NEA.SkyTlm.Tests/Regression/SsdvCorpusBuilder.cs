using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MathNet.Numerics;
using VE3NEA.SkyTlm.Core;
using VE3NEA.SkyTlm.Imaging.Ssdv;
using VE3NEA.SkyTlm.IO;
using Xunit;

namespace VE3NEA.SkyTlm.Tests.Regression
{
  /// <summary>
  /// Generator for <c>Data/Wav/fsk_hades_ssdv_HADES-SA.wav</c>, the imaging corpus clip. Not a test —
  /// it reads a multi-hundred-megabyte recording that is not in the repo, so it is skipped by default
  /// and run by hand when the clip needs rebuilding.
  /// <para>
  /// It exists because the decode-regression clip cannot serve here: that one holds the three
  /// median-SNR bursts of a pass, and on HADES-SA those carry telemetry (types 8 and 15). SSDV is
  /// packet type 10, so the imaging clip is selected for the packets it validates rather than for SNR.
  /// </para>
  /// </summary>
  public class SsdvCorpusBuilder
  {
    private const string RecordingDir =
      @"C:\Users\alsho\AppData\Roaming\Afreet\Products\SkyRoof\Recordings\SSDV";

    /// <summary>The pass to cut from. Three HADES-SA passes were captured on 2026-07-30; this is the
    /// one whose SSDV packets cluster into a single image rather than scattering across five.</summary>
    private const string Recording = "2026-07-30_04_50_39_HADES-SA.iq.wav";

    /// <summary>Size budget for the clip, in seconds of 48 kHz IQ (~0.4 MB/s).</summary>
    private const double MaxSeconds = 18;
    private const double MarginSeconds = 0.5;

    [Fact(Skip = "corpus generator, not a test — remove Skip and run explicitly to rebuild the clip")]
    public void BuildSsdvClip()
    {
      var (samples, fs) = WavIqReader.Read(Path.Combine(RecordingDir, Recording));
      var p = new SignalParams(Baud: 800, Modulation.FSK, Framing.HADES, SampleRate: fs, Deviation: 800);

      // pass 1: find the bursts that carry SSDV, and which packets each one holds. Selecting on
      // "has a type-10 frame" alone is not enough — overlapping decode windows re-decode the same
      // packet, so bursts have to be picked for the distinct packets they contribute.
      var spans = new List<(long Start, int Length, string Packets)>();
      using (var sp = new StreamingPipeline(p, new StreamingOptions()))
      {
        sp.BurstDecoded += r =>
        {
          // exactly the assembler's own gate: a frame whose CRC-32 (or RS repair) fails carries a
          // garbage header, and off air those show up as packet IDs like 4098. Selecting on the
          // 251-byte type-10 shape alone would pick bursts that contribute nothing.
          var ids = r.Frames
            .Where(f => SsdvSource.HadesSa.TryExtract(f, out _))
            .Select(f => { SsdvSource.HadesSa.TryExtract(f, out var b); return b; })
            .Select(b => SsdvPacket.TryParse(b, SsdvVariant.HadesSa251, out var pkt) ? pkt : null)
            .Where(pkt => pkt != null)
            .Select(pkt => $"{pkt!.ImageId}/{pkt.PacketId}")
            .Distinct().ToList();
          if (ids.Count > 0) spans.Add((r.StartSample, r.Length, string.Join(",", ids)));
        };
        Feed(sp, samples, fs);
      }

      Console.WriteLine($"{spans.Count} SSDV-bearing bursts in {samples.Length / (double)fs:F0} s:");
      foreach (var s in spans)
        Console.WriteLine($"   t={s.Start / (double)fs,7:F1}s  len={s.Length / (double)fs,5:F1}s  packets {s.Packets}");

      // Cut one contiguous window rather than stitching bursts together. A satellite sends an image's
      // packets back to back, so the run of bursts carrying one image already is a contiguous stretch of
      // the recording — keeping it whole is both smaller than splicing overlapping decode windows and a
      // more honest input, since the detector sees the real inter-burst gaps.
      string best = spans.SelectMany(s => s.Packets.Split(',')).Select(id => id.Split('/')[0])
        .GroupBy(x => x).OrderByDescending(g => g.Count()).First().Key;
      var run = spans.Where(s => s.Packets.Split(',').Any(id => id.StartsWith(best + "/"))).ToList();

      long first = run[0].Start;
      var kept = run.Where(s => (s.Start + s.Length - first) / (double)fs <= MaxSeconds).ToList();
      long last = kept[^1].Start + kept[^1].Length;
      Console.WriteLine($"image {best}: {run.Count} bursts, keeping {kept.Count} within {MaxSeconds} s "
                      + $"-> packets {string.Join(" ", kept.Select(k => k.Packets))}");

      int margin = (int)(MarginSeconds * fs);
      int from = (int)Math.Max(0, first - margin);
      int to = (int)Math.Min(samples.Length, last + margin);
      var cut = samples[from..to];

      string outPath = Path.Combine(TestPaths.WavDir, "fsk_hades_ssdv_HADES-SA.wav");
      WriteIqWav(outPath, cut, fs);
      SignalParamsSidecar.Save(outPath + ".json", p);

      Console.WriteLine($"wrote {cut.Length} samples ({cut.Length / (double)fs:F1} s, "
                      + $"{cut.Length * 8 / 1e6:F1} MB) to {outPath}");
    }

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
