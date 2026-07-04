namespace VE3NEA.SkySSTV
{
  /// <summary>
  /// An absolute-indexed view over a stretch of the brightness stream (P7.5): the batch reconstruction
  /// wraps its whole array (base 0), the streaming image builder wraps the rolling ring's linear span.
  /// Reads outside the window report absence, so a line whose samples fell off the ring (or have not
  /// arrived yet) renders from what exists — the same skip the batch out-of-array guard performs.
  /// </summary>
  internal readonly struct BrightnessWindow
  {
    private readonly double[] buf;
    private readonly long baseIdx;
    private readonly int len;

    public BrightnessWindow(double[] buf, long baseIdx, int len)
    {
      this.buf = buf;
      this.baseIdx = baseIdx;
      this.len = len;
    }

    /// <summary>First absolute sample inside the window.</summary>
    public long Start => baseIdx;

    /// <summary>One past the last absolute sample inside the window.</summary>
    public long End => baseIdx + len;

    public bool TryGet(long abs, out double v)
    {
      long i = abs - baseIdx;
      if ((ulong)i < (ulong)len) { v = buf[(int)i]; return true; }
      v = 0;
      return false;
    }
  }
}
