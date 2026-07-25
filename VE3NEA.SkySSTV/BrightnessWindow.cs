namespace VE3NEA.SkySSTV
{
  /// <summary>
  /// An absolute-indexed view over a stretch of the brightness stream (P7.5): the batch reconstruction
  /// wraps its whole array (base 0), the streaming image builder wraps the rolling ring's linear span.
  /// Reads outside the window report absence, so a line whose samples fell off the ring (or have not
  /// arrived yet) renders from what exists — the same skip the batch out-of-array guard performs.
  ///
  /// The window carries BOTH Stage-3 branches (§6.3) — the narrow and the wide low-pass, sample-aligned
  /// on one timeline — because the reconstruction blends them per line against the measured noise.
  /// </summary>
  internal readonly struct BrightnessWindow
  {
    private readonly double[] buf;
    private readonly double[] wideBuf;
    private readonly long baseIdx;
    private readonly int len;

    public BrightnessWindow(double[] buf, double[]? wideBuf, long baseIdx, int len)
    {
      this.buf = buf;
      this.wideBuf = wideBuf ?? buf;
      this.baseIdx = baseIdx;
      this.len = len;
    }

    /// <summary>Single-branch window: the wide branch reads the same samples as the narrow one, so any
    /// blend weight collapses to the unadapted result.</summary>
    public BrightnessWindow(double[] buf, long baseIdx, int len) : this(buf, null, baseIdx, len) { }

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

    /// <summary>Both branches' samples at one absolute index.</summary>
    public bool TryGet(long abs, out double narrow, out double wide)
    {
      long i = abs - baseIdx;
      if ((ulong)i < (ulong)len) { narrow = buf[(int)i]; wide = wideBuf[(int)i]; return true; }
      narrow = wide = 0;
      return false;
    }

    /// <summary>The wide branch's sample at one absolute index.</summary>
    public bool TryGetWide(long abs, out double v)
    {
      long i = abs - baseIdx;
      if ((ulong)i < (ulong)len) { v = wideBuf[(int)i]; return true; }
      v = 0;
      return false;
    }
  }
}
