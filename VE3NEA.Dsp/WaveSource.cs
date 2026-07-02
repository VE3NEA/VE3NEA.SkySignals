using CSCore;
using MathNet.Numerics;

namespace VE3NEA
{
  // <T> is float or Complex32
  public class WaveSource<T> : IWaveSource
  {
    private readonly WaveFormat format;
    public RingBuffer<T> Buffer { get; private set; }

    public WaveSource(int samplingRate)
    {
      int channelCount = typeof(T) == typeof(Complex32) ? 2 : 1;
      format = new WaveFormat(samplingRate, 32, channelCount, AudioEncoding.IeeeFloat);
      Buffer = new(2 * samplingRate);
    }

  public void AddSamples(T[] samples, int offset = 0, int? count = null)
    {
      Buffer.Write(samples, offset, count ?? samples.Length);
    }




    //------------------------------------------------------------------------------------------------------------------
    //                                             IWaveSource
    //------------------------------------------------------------------------------------------------------------------
    public bool CanSeek => false;
    public WaveFormat WaveFormat => format;
    public long Position { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }
    public long Length => throw new NotImplementedException();
    public int Read(byte[] buffer, int offset, int count)
    {
      Buffer.ReadBytes(buffer, offset, count);
      return count;
    }




    //------------------------------------------------------------------------------------------------------------------
    //                                             IDisposable
    //------------------------------------------------------------------------------------------------------------------
    public void Dispose()
    {
      // nothing to dispose of
    }
  }
}
