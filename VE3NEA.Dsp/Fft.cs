using System.Runtime.InteropServices;
using MathNet.Numerics;


namespace VE3NEA
{
  public class Fft<T> : IDisposable
  {
    private IntPtr InputPtr;
    private IntPtr OutputPtr;
    private IntPtr Plan;


    public T[] InputData;
    public Complex32[] OutputData;

    private readonly NativeFftw.FftwDirection Direction;


    public Fft(int size, NativeFftw.FftwFlags flags = NativeFftw.FftwFlags.Patient,
      NativeFftw.FftwDirection direction = NativeFftw.FftwDirection.Forward)
    {
      Direction = direction;
      // managed buffers visible to the calling code
      if (typeof(T) == typeof(float))
      {
        InputData = new T[size * 2];
        OutputData = new Complex32[size + 1];
      }
      else if (typeof(T) == typeof(Complex32))
      {
        InputData = new T[size];
        OutputData = new Complex32[size];
      }
      else throw new ArgumentException($"Invalid FFT data type: {typeof(T)}");


      // native buffers
      int inputSampleSize = Marshal.SizeOf(typeof(T));
      int outputSampleSize = Marshal.SizeOf(typeof(Complex32));
      InputPtr = NativeFftw.malloc(InputData.Length * inputSampleSize);
      OutputPtr = NativeFftw.malloc(OutputData.Length * outputSampleSize);


      // FFT plan
      NativeFftw.make_planner_thread_safe();

      if (typeof(T) == typeof(float))
      {
        // the real-to-complex transform is inherently forward; an inverse needs the complex element type.
        if (direction != NativeFftw.FftwDirection.Forward)
          throw new ArgumentException("Inverse FFT requires the Complex32 element type (real-to-complex is forward only).");
        Plan = NativeFftw.dft_r2c_1d(InputData.Length, InputPtr, OutputPtr, flags);
      }
      else
        Plan = NativeFftw.dft_1d(InputData.Length, InputPtr, OutputPtr, direction, flags);
    }
    
    public void Dispose()
    {
      if (Plan != IntPtr.Zero) NativeFftw.destroy_plan(Plan);
      if (InputPtr != IntPtr.Zero) NativeFftw.free(InputPtr);
      if (OutputPtr != IntPtr.Zero) NativeFftw.free(OutputPtr);

      Plan = IntPtr.Zero;
      InputPtr = IntPtr.Zero;
      OutputPtr = IntPtr.Zero;
    }

    public unsafe void Execute()
    {
      var floatSpan = new Span<T>((void*)InputPtr, InputData.Length);
      InputData.CopyTo(floatSpan);

      NativeFftw.execute(Plan);

      var complexSpan = new Span<Complex32>((void*)OutputPtr, OutputData.Length);
      complexSpan.CopyTo(OutputData);

      // FFTW's backward transform is unnormalized; scale by 1/N so a Backward plan is a true inverse DFT.
      if (Direction == NativeFftw.FftwDirection.Backward)
      {
        float norm = 1f / InputData.Length;
        for (int i = 0; i < OutputData.Length; i++) OutputData[i] *= norm;
      }
    }

    private static string? WisdomPath;

    public static void LoadWisdom(string path)
    {
      WisdomPath = path;
      if (File.Exists(path)) NativeFftw.import_wisdom_from_filename(path);
    }

    public static void SaveWisdom()
    {
      if (string.IsNullOrEmpty(WisdomPath)) return;
      Directory.CreateDirectory(Path.GetDirectoryName(WisdomPath)!);
      NativeFftw.export_wisdom_to_filename(WisdomPath);
    }
  }
}
