using System;
using System.IO;

namespace VE3NEA.SkyFM.Tests
{
  /// <summary>16-bit mono PCM WAV writer/reader for the manual harnesses (the core does no file IO —
  /// plan G6). Samples are normalized by the TRUE peak to <c>peakTarget</c> of full scale, so the clamp
  /// never fires (the spike's percentile normalization clipped impulses into audible clicks).</summary>
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

    /// <summary>Reads a 16-bit mono PCM WAV (as written above) back into ±1 floats, walking the RIFF
    /// chunks so extra chunks are tolerated.</summary>
    public static (float[] Samples, int Rate) Read(string path)
    {
      using var r = new BinaryReader(File.OpenRead(path));
      r.ReadBytes(12);                                       // RIFF size WAVE
      int rate = 0;
      while (true)
      {
        string id = new(r.ReadChars(4));
        int size = r.ReadInt32();
        if (id == "fmt ")
        {
          short format = r.ReadInt16(), channels = r.ReadInt16();
          rate = r.ReadInt32();
          r.ReadInt32(); r.ReadInt16();                      // byte rate, block align
          short bits = r.ReadInt16();
          if (format != 1 || channels != 1 || bits != 16)
            throw new InvalidDataException($"{path}: expected 16-bit mono PCM");
          r.ReadBytes(size - 16);
        }
        else if (id == "data")
        {
          if (rate == 0) throw new InvalidDataException($"{path}: data chunk before fmt");
          var samples = new float[size / 2];
          for (int i = 0; i < samples.Length; i++) samples[i] = r.ReadInt16() / 32768f;
          return (samples, rate);
        }
        else r.ReadBytes(size + (size & 1));                 // skip unknown chunk (word-aligned)
      }
    }
  }
}
