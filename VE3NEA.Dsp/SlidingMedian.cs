namespace VE3NEA
{
  /// <summary>
  /// Online sliding-window median — or any decile — without sorting the window per sample: a ring buffer
  /// holds the raw samples, a parallel array holds the same window <b>kept</b> sorted, and each new sample
  /// replaces the oldest one via two binary searches and a single block move (O(log n) + memmove). No
  /// allocations after construction. Port of <c>TSlidingMedian</c> (SlidMin.pas, A.S.), based on the
  /// MIT 6.046 sliding-window handout.
  /// </summary>
  public sealed class SlidingMedian
  {
    private readonly float[] values;   // ring of raw samples, in arrival order
    private readonly float[] sorted;   // the same window, kept sorted
    private readonly int outIdx;       // index of the reported decile in _sorted
    private int currIdx;
    private bool seeded;

    /// <param name="len">Window length in samples.</param>
    /// <param name="decile">Which order statistic to report: 0.5 = median (default), 0.25 = lower
    /// quartile, etc.</param>
    public SlidingMedian(int len, double decile = 0.5)
    {
      if (len < 1) throw new ArgumentOutOfRangeException(nameof(len));
      values = new float[len];
      sorted = new float[len];
      outIdx = (int)Math.Round((len - 1) * Math.Clamp(decile, 0.0, 1.0));
    }

    public int Length => values.Length;

    /// <summary>Group delay of the window, in samples.</summary>
    public int Delay => (values.Length - 1) / 2;

    /// <summary>The current decile value over the window.</summary>
    public float Value => sorted[outIdx];

    /// <summary>Forget the window; the next <see cref="Process"/> re-seeds it.</summary>
    public void Reset() => seeded = false;

    /// <summary>Push one sample, drop the oldest, return the updated decile. The very first sample seeds
    /// the whole window (no zero-fill bias while the window fills).</summary>
    public float Process(float v)
    {
      if (!seeded)
      {
        Array.Fill(values, v);
        Array.Fill(sorted, v);
        currIdx = 0;
        seeded = true;
        return v;
      }

      Replace(values[currIdx], v);
      values[currIdx] = v;
      if (++currIdx == values.Length) currIdx = 0;
      return sorted[outIdx];
    }

    /// <summary>Swap <paramref name="oldV"/> for <paramref name="newV"/> in the sorted window: locate both
    /// by binary search, shift the span between them by one, and drop the new value into the gap.</summary>
    private void Replace(float oldV, float newV)
    {
      int oldP = LowerBound(oldV);
      int newP = LowerBound(newV);

      if (newP > oldP + 1)
      {
        Array.Copy(sorted, oldP + 1, sorted, oldP, newP - oldP - 1);
        sorted[newP - 1] = newV;
      }
      else if (newP < oldP)
      {
        Array.Copy(sorted, newP, sorted, newP + 1, oldP - newP);
        sorted[newP] = newV;
      }
      else
      {
        sorted[oldP] = newV;   // newP == oldP or oldP+1: the slot itself is the right place
      }
    }

    /// <summary>First index whose value is ≥ <paramref name="v"/> (the window always contains any value
    /// being replaced, so this finds an existing slot for olds and the insertion point for news).</summary>
    private int LowerBound(float v)
    {
      int lo = 0, hi = sorted.Length - 1;
      while (lo <= hi)
      {
        int mid = (lo + hi) >> 1;
        if (sorted[mid] < v) lo = mid + 1; else hi = mid - 1;
      }
      return lo;
    }
  }
}
