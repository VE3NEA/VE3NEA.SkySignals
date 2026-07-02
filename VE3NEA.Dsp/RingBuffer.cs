using System.Numerics;
using MathNet.Numerics;

namespace VE3NEA
{
  public class RingBuffer<T>
  {
    private T[] ringBuffer;
    private int readPos = 0;
    private int writePos = 0;
    public readonly int bytesPerSample;
    private readonly object lockObject = new object();

    public int Count { get; private set; }

    public RingBuffer(int capacity)
    {
      if (capacity <= 0) throw new ArgumentException(nameof(capacity));

      if (typeof(T) == typeof(Int16)) bytesPerSample = 2;
      else if (typeof(T) == typeof(Int32) || typeof(T) == typeof(float)) bytesPerSample = 4;
      else if (typeof(T) == typeof(double) || typeof(T) == typeof(Complex32)) bytesPerSample = 8;
      else if (typeof(T) == typeof(Complex)) bytesPerSample = 16;
      else throw new ArgumentException($"Invalid RingBuffer data type: {typeof(T)}");

      ringBuffer = new T[capacity];
    }

    public void Resize(int capacity)
    {
      if (capacity <= 0) throw new ArgumentException(nameof(capacity));

      lock (lockObject)
      {
        ringBuffer = new T[capacity];
        Clear();
      }
    }

    public void Clear()
    {
      lock (lockObject)
      {
        readPos = writePos = Count = 0;
      }
    }

    public void Write(T[] data, int offset, int count)
    {
      if (offset < 0) throw new ArgumentException(nameof(offset));
      if (count < 0) throw new ArgumentException(nameof(count));
      if (offset + count > data.Length) throw new ArgumentException("Array too short");
      if (count == 0) return;

      lock (lockObject)
      {
        if (count > ringBuffer.Length) Resize(count);

        int spaceAvailable = ringBuffer.Length - Count;
        if (count > spaceAvailable) 
          Dump(count - spaceAvailable);

        int count1 = Math.Min(count, ringBuffer.Length - writePos);
        int count2 = count - count1;

        if (count1 > 0)
        {
          Array.Copy(data, offset, ringBuffer, writePos, count1);
          writePos += count1;
          if (writePos == ringBuffer.Length) writePos = 0;
        }

        if (count2 > 0)
        {
          Array.Copy(data, offset + count1, ringBuffer, 0, count2);
          writePos = count2;
        }

        Count += count;
        if (Count < 0) throw new Exception("count < 0");
      }
    }

    public int Read(T[] buffer, int offset, int count)
    {
      throw new NotImplementedException();
    }

    public int ReadBytes(byte[] buffer, int offset, int count)
    {
      if (offset < 0) throw new ArgumentException(nameof(offset));
      if (count < 0) throw new ArgumentException(nameof(count));
      if (offset + count > buffer.Length) throw new ArgumentException("Array too short");
      if (count == 0) return 0;

      lock (lockObject)
      {
        int readSampleCount = Math.Min(Count, count / bytesPerSample);
        int count1 = Math.Min(readSampleCount, ringBuffer.Length - readPos);
        int count2 = readSampleCount - count1;

        if (count1 > 0)
        {
          CopyBytes(ringBuffer, readPos * bytesPerSample, buffer, offset, count1 * bytesPerSample);
          readPos += count1;
          if (readPos == ringBuffer.Length) readPos = 0;
        }

        if (count2 > 0)
        {
          CopyBytes(ringBuffer, 0, buffer, offset + count1 * bytesPerSample, count2 * bytesPerSample);
          readPos = count2;
        }

        Count -= readSampleCount;

        int readByteCount = readSampleCount * bytesPerSample;
        if (readByteCount < count)
        {
          Array.Clear(buffer, offset + readByteCount, count - readByteCount);
        }
      }

      return count;
    }

    // perform multi-step operations, e.g., replace buffer data: read Count, Clear(), Write().
    public void ExecuteLocked(Action action)
    {
      if (action == null) throw new ArgumentNullException(nameof(action));

      lock (lockObject)
      {
        action();
      }
    }

    private void Dump(int dumpCount)
    {
      Count -= dumpCount;
      if (Count < 0) throw new Exception("count < 0");
      readPos += dumpCount;
      if (readPos >= ringBuffer.Length) readPos -= ringBuffer.Length;
    }

    private unsafe void CopyBytes(T[] src, int srcByteOffset, byte[] dst, int dstByteOffset, int byteCount)
    {
      fixed (T* pSrc = src)
      fixed (byte* pDst = dst)
      {
        Buffer.MemoryCopy((byte*)pSrc + srcByteOffset, pDst + dstByteOffset, byteCount, byteCount);
      }
    }
  }
}
