using System.Drawing;
using System.Drawing.Imaging;

namespace VE3NEA.SkySSTV
{
  /// <summary>
  /// Lightweight row-major RGB image used by the encoder (and later the decoder's output), kept free
  /// of GDI in its core so it is trivial to synthesize test patterns. A <see cref="FromBitmap"/>
  /// adapter bridges <see cref="System.Drawing.Bitmap"/> for real images.
  /// </summary>
  public sealed class RgbImage
  {
    public int Width { get; }
    public int Height { get; }
    public byte[] R { get; }
    public byte[] G { get; }
    public byte[] B { get; }

    public RgbImage(int width, int height)
    {
      Width = width;
      Height = height;
      R = new byte[width * height];
      G = new byte[width * height];
      B = new byte[width * height];
    }

    public void Set(int x, int y, byte r, byte g, byte b)
    {
      int i = y * Width + x;
      R[i] = r; G[i] = g; B[i] = b;
    }

    public (byte r, byte g, byte b) Get(int x, int y)
    {
      int i = y * Width + x;
      return (R[i], G[i], B[i]);
    }

    /// <summary>Copy a GDI bitmap into an <see cref="RgbImage"/> (32bpp read; alpha ignored).</summary>
    public static RgbImage FromBitmap(Bitmap bmp)
    {
      var img = new RgbImage(bmp.Width, bmp.Height);
      var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
      var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
      try
      {
        int stride = data.Stride;
        unsafe
        {
          byte* basePtr = (byte*)data.Scan0;
          for (int y = 0; y < bmp.Height; y++)
          {
            byte* row = basePtr + y * stride;
            for (int x = 0; x < bmp.Width; x++)
            {
              // 32bppArgb is B,G,R,A in memory (little-endian).
              byte b = row[x * 4 + 0], g = row[x * 4 + 1], r = row[x * 4 + 2];
              img.Set(x, y, r, g, b);
            }
          }
        }
      }
      finally { bmp.UnlockBits(data); }
      return img;
    }

    /// <summary>Write this image to <paramref name="path"/> as a PNG (creates the directory if needed).</summary>
    public void SavePng(string path)
    {
      string? dir = System.IO.Path.GetDirectoryName(path);
      if (!string.IsNullOrEmpty(dir)) System.IO.Directory.CreateDirectory(dir);
      using var bmp = ToBitmap();
      bmp.Save(path, ImageFormat.Png);
    }

    /// <summary>Materialize this image as a 24bpp GDI bitmap.</summary>
    public Bitmap ToBitmap()
    {
      var bmp = new Bitmap(Width, Height, PixelFormat.Format24bppRgb);
      var rect = new Rectangle(0, 0, Width, Height);
      var data = bmp.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
      try
      {
        int stride = data.Stride;
        unsafe
        {
          byte* basePtr = (byte*)data.Scan0;
          for (int y = 0; y < Height; y++)
          {
            byte* row = basePtr + y * stride;
            for (int x = 0; x < Width; x++)
            {
              var (r, g, b) = Get(x, y);
              row[x * 3 + 0] = b; row[x * 3 + 1] = g; row[x * 3 + 2] = r;
            }
          }
        }
      }
      finally { bmp.UnlockBits(data); }
      return bmp;
    }
  }
}
