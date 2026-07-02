using MathNet.Numerics;

namespace VE3NEA.SkyTlm.IO
{
  /// <summary>
  /// Loads a complex IQ <c>.wav</c> recording (32-bit float, 2 channels I/Q, nominally 48 kHz) into a
  /// <see cref="Complex32"/> array. A small RIFF parser on top of the shared DSP library. The reader
  /// hands the pipeline only the IQ samples + the sample rate (carried in <c>SignalParams</c>), never a file or
  /// a recording object.
  /// </summary>
  public static class WavIqReader
  {
    // WAVE format tags
    private const ushort WAVE_FORMAT_IEEE_FLOAT = 3;
    private const ushort WAVE_FORMAT_EXTENSIBLE = 0xFFFE;

    /// <summary>Read the recording into IQ samples plus the sample rate (Hz).</summary>
    public static (Complex32[] Samples, int SampleRate) Read(string path)
    {
      using var fs = File.OpenRead(path);
      using var r = new BinaryReader(fs);

      if (new string(r.ReadChars(4)) != "RIFF")
        throw new InvalidDataException($"Not a RIFF file: {path}");
      r.ReadUInt32(); // overall chunk size (ignored)
      if (new string(r.ReadChars(4)) != "WAVE")
        throw new InvalidDataException($"Not a WAVE file: {path}");

      ushort formatTag = 0, channels = 0, bitsPerSample = 0;
      int sampleRate = 0;
      byte[]? data = null;

      while (fs.Position + 8 <= fs.Length)
      {
        string chunkId = new string(r.ReadChars(4));
        uint chunkSize = r.ReadUInt32();
        long next = fs.Position + chunkSize + (chunkSize & 1); // chunks are word-aligned

        if (chunkId == "fmt ")
        {
          formatTag = r.ReadUInt16();
          channels = r.ReadUInt16();
          sampleRate = (int)r.ReadUInt32();
          r.ReadUInt32();                 // byte rate
          r.ReadUInt16();                 // block align
          bitsPerSample = r.ReadUInt16();
          if (formatTag == WAVE_FORMAT_EXTENSIBLE && chunkSize >= 40)
          {
            r.ReadUInt16();               // cbSize
            r.ReadUInt16();               // valid bits
            r.ReadUInt32();               // channel mask
            formatTag = r.ReadUInt16();   // real format from the sub-format GUID's first word
          }
        }
        else if (chunkId == "data")
        {
          data = r.ReadBytes((int)chunkSize);
        }

        fs.Position = next;
      }

      if (data == null)
        throw new InvalidDataException($"No data chunk in {path}");
      if (formatTag != WAVE_FORMAT_IEEE_FLOAT)
        throw new NotSupportedException($"Expected IEEE float IQ wav, got format tag {formatTag} in {path}");
      if (bitsPerSample != 32)
        throw new NotSupportedException($"Expected 32-bit float samples, got {bitsPerSample}-bit in {path}");
      if (channels != 2)
        throw new NotSupportedException($"Expected 2 channels (I/Q), got {channels} in {path}");

      int frameCount = data.Length / (channels * 4);
      var samples = new Complex32[frameCount];
      var floats = new float[frameCount * 2];
      Buffer.BlockCopy(data, 0, floats, 0, frameCount * 2 * 4);
      for (int i = 0; i < frameCount; i++)
        samples[i] = new Complex32(floats[2 * i], floats[2 * i + 1]);

      return (samples, sampleRate);
    }
  }
}
