using System;
using System.IO;

namespace VE3NEA.SkyFM.Tests
{
  /// <summary>16-bit mono PCM WAV writer for the manual harnesses (the core does no file IO — plan G6).
  /// Samples are normalized by the TRUE peak to <c>peakTarget</c> of full scale, so the clamp never
  /// fires (the spike's percentile normalization clipped impulses into audible clicks).</summary>
  internal static class Wav16
  {
    public static void Write(string path, ReadOnlySpan<float> samples, int rate, double peakTarget = 0.7)
    {
      float peak = 0f;
      foreach (float v in samples) { float m = Math.Abs(v); if (m > peak) peak = m; }
      double scale = peak > 1e-9f ? peakTarget / peak : 1.0;

      using var fs = File.Create(path);
      using var w = new BinaryWriter(fs);
      int dataBytes = samples.Length * 2;
      short channels = 1, bits = 16;
      int byteRate = rate * channels * bits / 8;

      w.Write("RIFF".ToCharArray());
      w.Write(36 + dataBytes);
      w.Write("WAVE".ToCharArray());
      w.Write("fmt ".ToCharArray());
      w.Write(16);                                           // PCM fmt chunk size
      w.Write((short)1);                                     // PCM
      w.Write(channels);
      w.Write(rate);
      w.Write(byteRate);
      w.Write((short)(channels * bits / 8));                 // block align
      w.Write(bits);
      w.Write("data".ToCharArray());
      w.Write(dataBytes);
      foreach (float v in samples)
        w.Write((short)Math.Round(Math.Clamp(v * scale, -1.0, 1.0) * 32767.0));
    }
  }
}
