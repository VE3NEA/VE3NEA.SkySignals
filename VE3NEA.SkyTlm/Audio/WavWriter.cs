using System;
using System.IO;

namespace VE3NEA.SkyTlm.Audio
{
  /// <summary>
  /// Wraps 16-bit mono PCM in a minimal RIFF/WAVE container. Exists so
  /// <see cref="VoiceProduct.Wav"/> can be a self-describing file the caller saves or plays directly,
  /// the same bargain <see cref="Imaging.ImageProduct.Jpeg"/> makes: this assembly hands out bytes in a
  /// standard format and stays out of the presentation layer.
  /// </summary>
  public static class WavWriter
  {
    /// <summary>Bytes of a canonical 44-byte-header WAV holding <paramref name="pcm"/>.</summary>
    public static byte[] Write(ReadOnlySpan<short> pcm, int sampleRate)
    {
      const int channels = 1, bitsPerSample = 16;
      int dataBytes = pcm.Length * sizeof(short);
      int byteRate = sampleRate * channels * bitsPerSample / 8;

      var stream = new MemoryStream(44 + dataBytes);
      var w = new BinaryWriter(stream);

      w.Write("RIFF"u8);
      w.Write(36 + dataBytes);            // size of everything after this field
      w.Write("WAVE"u8);

      w.Write("fmt "u8);
      w.Write(16);                        // PCM fmt chunk length
      w.Write((short)1);                  // format 1 = PCM
      w.Write((short)channels);
      w.Write(sampleRate);
      w.Write(byteRate);
      w.Write((short)(channels * bitsPerSample / 8));   // block align
      w.Write((short)bitsPerSample);

      w.Write("data"u8);
      w.Write(dataBytes);
      foreach (short s in pcm) w.Write(s);

      w.Flush();
      return stream.ToArray();
    }
  }
}
