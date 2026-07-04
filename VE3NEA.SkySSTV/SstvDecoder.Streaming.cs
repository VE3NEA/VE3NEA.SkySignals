using System;
using System.Collections.Generic;
using MathNet.Numerics;

namespace VE3NEA.SkySSTV
{
  /// <summary>A progressive image notification from the streaming decoder (plan §5/§7 P7.5).
  /// <paramref name="Image"/> carries the per-pixel Wiener-gain confidence in its alpha plane (§6.2);
  /// rows at and beyond <paramref name="ValidRows"/> have not been received (alpha 0).</summary>
  /// <param name="ImageId">Stable id of this image across its progressive updates.</param>
  /// <param name="Mode">The train's SSTV mode.</param>
  /// <param name="StartSeconds">Stream time of the image's first line sync onset.</param>
  /// <param name="FromVis">Whether the train was seeded by a decoded VIS header.</param>
  /// <param name="Image">The current reconstruction (full mode geometry; unreceived rows black).</param>
  /// <param name="ValidRows">Rows rendered so far.</param>
  /// <param name="Final">True on the finalize notification (train retired / end of stream).</param>
  public sealed record SstvImageEvent(int ImageId, SstvMode Mode, double StartSeconds, bool FromVis,
    RgbImage Image, int ValidRows, bool Final);

  /// <summary>
  /// The push-based streaming decoder (plan §1.13/§6.0/§7 P7.5): feed consecutive IQ blocks of any size
  /// through <see cref="Process(System.ReadOnlySpan{MathNet.Numerics.Complex32})"/> and receive images
  /// incrementally — <see cref="ImageUpdated"/> as claimed scan lines render (including §1.13
  /// dirty-block re-renders when the timing estimate converges), <see cref="ImageCompleted"/> when a
  /// train retires (§1.10) or the stream ends. Internally: two bounded-state discriminator chains
  /// (detection + video), the Stage-2 sync bandpass, VIS tiling over a rolling sync buffer, the live
  /// <see cref="SstvDetectionChain"/>, and a rolling brightness buffer sized for the soft-comb's
  /// back-dated claims. Not thread-safe: drive one instance from a single producer.
  /// </summary>
  public sealed partial class SstvDecoder : IDisposable
  {
    private readonly SstvDecodeOptions o;
    private readonly double fs;

    private readonly SstvStreamingDiscriminator detDisc;
    private readonly SstvStreamingDiscriminator vidDisc;
    private readonly SyncBandpass syncBand;
    private readonly SstvStreamingBrightness brightness;
    private readonly SstvDetectionChain chain;

    // rolling sync-audio buffer (absolute-indexed): VIS tiles read ahead of the chain cursor
    private double[] syncBuf = new double[1 << 18];
    private long syncBase;
    private int syncLen;
    private long visCursor;                    // next VIS tile start
    private long chainFed;                     // sync samples handed to the detection chain
    private readonly int visStep;
    private readonly int visHeader;
    private readonly int visBit;

    // rolling brightness buffer (absolute-indexed), sized for the comb's back-dated span
    private double[] brightBuf;
    private long brightBase;
    private int brightLen;
    private readonly int brightKeep;

    // image assembly
    private readonly Dictionary<SstvPulseTrain, SstvImageBuilder> builders = new();
    private readonly List<SstvPulseTrain> retirees = new();
    private readonly HashSet<SstvPulseTrain> finalized = new();
    private int renderFrom;                    // first extractor line not yet rendered
    private int nextImageId;
    private bool flushed;

    /// <summary>Raised when an image train's reconstruction changed (new or re-rendered lines). Only
    /// trains that pass the image-emission gate (plan §4.1 / the P7 comb pulse-support guard) surface.</summary>
    public event Action<SstvImageEvent>? ImageUpdated;

    /// <summary>Raised once per image when its train retires or the stream ends.</summary>
    public event Action<SstvImageEvent>? ImageCompleted;

    public SstvDecoder(SstvDecodeOptions? options = null)
    {
      o = options ?? new SstvDecodeOptions();
      fs = o.SampleRate;
      detDisc = new SstvStreamingDiscriminator(o, o.ChannelBwHz);
      vidDisc = new SstvStreamingDiscriminator(o, o.VideoChannelBwHz);
      syncBand = new SyncBandpass(o, fs);
      brightness = new SstvStreamingBrightness(o);
      chain = new SstvDetectionChain(fs);
      chain.TrainRetired = t => retirees.Add(t);

      visStep = (int)Math.Round(3.0 * fs);
      visHeader = SstvVisDetector.HeaderSamples(fs);
      visBit = (int)Math.Round(SstvTones.VisBitMs / 1000.0 * fs);

      // the comb back-dates a seeded train one comb memory, so its claimed lines reach that far back —
      // the brightness ring must still hold them (plus revision/extraction latency slack)
      double maxPeriodMs = 0;
      foreach (var spec in SstvModes.All) maxPeriodMs = Math.Max(maxPeriodMs, spec.LinePeriodMs);
      brightKeep = (int)(maxPeriodMs / 1000.0 * fs) * (SstvSoftComb.MemoryPeriods + 60);
      brightBuf = new double[1 << 18];
    }

    /// <summary>Feed the next contiguous IQ block (any size); raises image events for what settled.</summary>
    public void Process(ReadOnlySpan<Complex32> iq)
    {
      AppendSync(syncBand.Process(detDisc.Process(iq)));
      AppendBright(brightness.Process(vidDisc.Process(iq)));
      Advance(endOfStream: false);
    }

    /// <summary>Convenience overload for an array block.</summary>
    public void Process(Complex32[] block) => Process(block.AsSpan());

    /// <summary>End of stream: drain every stage's delay line, run the remaining VIS tiles and the
    /// chain's final lifecycle pass, and finalize all open images. Call once after the last
    /// <see cref="Process(System.ReadOnlySpan{MathNet.Numerics.Complex32})"/>.</summary>
    public void Flush()
    {
      if (flushed) return;
      flushed = true;
      AppendSync(syncBand.Process(detDisc.Flush()));
      AppendSync(syncBand.Flush());
      AppendBright(brightness.Process(vidDisc.Flush()));
      AppendBright(brightness.Flush());
      Advance(endOfStream: true);

      chain.Finish();
      Reconcile();
      foreach (var builder in builders.Values)
      {
        finalized.Add(builder.Train);
        if (chain.Extractor.IsImageTrain(builder.Train)) Emit(builder, final: true);
      }
      builders.Clear();
    }

    public void Dispose()
    {
      detDisc.Dispose();
      vidDisc.Dispose();
      syncBand.Dispose();
      brightness.Dispose();
    }


    // ----------------------------------------------------------------------------------------------------
    //                                    tiling / feeding / reconciling
    // ----------------------------------------------------------------------------------------------------


    /// <summary>Run every VIS tile whose span is buffered, feed the chain up to the tiled-through point
    /// (so a hit always seeds its high-prior train before the pulses at and after its anchor are folded),
    /// then reconcile the extractor's scan lines into the image builders.</summary>
    private void Advance(bool endOfStream)
    {
      long syncEnd = syncBase + syncLen;
      while (syncEnd - visCursor >= visStep + visHeader + visBit)
      {
        RunVisTile(visCursor, visStep, visStep + visHeader + visBit);
        FeedChain(visCursor + visStep);
        visCursor += visStep;
      }

      if (endOfStream)
      {
        // the batch tiling runs any start with a full header left; mirror it over the remainder
        while (syncEnd - visCursor > visHeader)
        {
          int avail = (int)(syncEnd - visCursor);
          RunVisTile(visCursor, Math.Min(visStep, avail), avail);
          visCursor += visStep;
        }
        FeedChain(syncEnd);
      }

      Reconcile();
      TrimSync();
      TrimBright();
    }

    /// <summary>One bounded VIS search window (the batch <c>DetectAll</c> tile) over the rolling buffer.</summary>
    private void RunVisTile(long start, int searchLength, int copyLen)
    {
      copyLen = (int)Math.Min(copyLen, syncBase + syncLen - start);
      var tile = new double[copyLen];
      Array.Copy(syncBuf, (int)(start - syncBase), tile, 0, copyLen);
      var hit = SstvVisDetector.Detect(tile, fs, 0, searchLength);
      if (hit.Found && hit.Mode is SstvMode mode)
        chain.SeedVis(mode, (int)(hit.HeaderEndSample + start));
    }

    private void FeedChain(long upTo)
    {
      if (upTo <= chainFed) return;
      chain.Process(syncBuf.AsSpan((int)(chainFed - syncBase), (int)(upTo - chainFed)));
      chainFed = upTo;
    }

    /// <summary>Fold the extractor's scan-line list into the builders: re-render from the dirty rewind
    /// (§1.13), render new lines whose brightness has fully arrived, emit progressive updates for image
    /// trains, and finalize retirees (§1.10, gated by the image-emission rules incl. the P7 comb guard).</summary>
    private void Reconcile()
    {
      var lines = chain.Extractor.Lines;
      int from = Math.Min(chain.Extractor.TakeLineRewind(), renderFrom);
      var bw = new BrightnessWindow(brightBuf, brightBase, brightLen);

      for (int i = from; i < lines.Count; i++)
      {
        var line = lines[i];
        if (finalized.Contains(line.Train)) continue;        // a completed image never re-opens
        if (!builders.TryGetValue(line.Train, out var builder))
          builders[line.Train] = builder = new SstvImageBuilder(line.Train, o, nextImageId++);

        var (_, end) = builder.LineSpan(line.PulseNo);
        if (i >= renderFrom && end > brightBase + brightLen && !flushed)
        {
          // this line's samples have not fully arrived — stop here and resume next push (a re-render
          // of an OLD line, i < renderFrom, proceeds regardless: it renders from what the ring holds)
          renderFrom = i;
          goto emit;
        }
        builder.RenderLine(bw, line.PulseNo);
      }
      renderFrom = lines.Count;

    emit:
      // trains absorbed by merge-on-promote vanish from the extractor — drop their orphaned builders
      if (builders.Count > 0)
      {
        List<SstvPulseTrain>? orphans = null;
        foreach (var train in builders.Keys)
          if (!ContainsTrain(train)) (orphans ??= new List<SstvPulseTrain>()).Add(train);
        if (orphans != null)
          foreach (var train in orphans) builders.Remove(train);
      }

      // a train retires at its last line's ONSET (§1.10 / the VIS image-end clock), before that line's
      // samples have arrived — hold the finalize until every claimed line has rendered
      if (renderFrom == lines.Count || flushed)
      {
        foreach (var train in retirees)
        {
          finalized.Add(train);
          if (!builders.TryGetValue(train, out var builder)) continue;
          if (chain.Extractor.IsImageTrain(train)) Emit(builder, final: true);
          builders.Remove(train);
        }
        retirees.Clear();
      }

      foreach (var builder in builders.Values)
        if (builder.Dirty && chain.Extractor.IsImageTrain(builder.Train))
          Emit(builder, final: false);
    }

    private bool ContainsTrain(SstvPulseTrain train)
    {
      foreach (var t in chain.Extractor.Trains) if (ReferenceEquals(t, train)) return true;
      return false;
    }

    private void Emit(SstvImageBuilder builder, bool final)
    {
      var evt = new SstvImageEvent(builder.ImageId, builder.Train.Format,
        builder.Train.Regr.GetPulseTime(0) / fs, builder.Train is SstvVisPulseTrain,
        builder.Snapshot(), builder.ValidRows, final);
      if (final) ImageCompleted?.Invoke(evt);
      else ImageUpdated?.Invoke(evt);
    }


    // ----------------------------------------------------------------------------------------------------
    //                                       rolling buffers
    // ----------------------------------------------------------------------------------------------------


    private void AppendSync(ReadOnlySpan<double> samples)
    {
      EnsureCapacity(ref syncBuf, syncLen + samples.Length);
      samples.CopyTo(syncBuf.AsSpan(syncLen));
      syncLen += samples.Length;
    }

    private void AppendBright(ReadOnlySpan<double> samples)
    {
      EnsureCapacity(ref brightBuf, brightLen + samples.Length);
      samples.CopyTo(brightBuf.AsSpan(brightLen));
      brightLen += samples.Length;
    }

    private static void EnsureCapacity(ref double[] buf, int needed)
    {
      if (buf.Length >= needed) return;
      int cap = buf.Length;
      while (cap < needed) cap *= 2;
      Array.Resize(ref buf, cap);
    }

    /// <summary>Drop sync samples both the chain and the VIS tiling are past.</summary>
    private void TrimSync()
    {
      long keepFrom = Math.Min(chainFed, visCursor);
      int drop = (int)(keepFrom - syncBase);
      if (drop <= 0) return;
      Array.Copy(syncBuf, drop, syncBuf, 0, syncLen - drop);
      syncLen -= drop;
      syncBase += drop;
    }

    /// <summary>Keep one comb-memory (+ slack) of brightness for back-dated claims and dirty re-renders;
    /// lines older than that render from whatever remains (§1.13 bounded re-render).</summary>
    private void TrimBright()
    {
      long keepFrom = brightBase + brightLen - brightKeep;
      int drop = (int)(keepFrom - brightBase);
      if (drop <= 0) return;
      Array.Copy(brightBuf, drop, brightBuf, 0, brightLen - drop);
      brightLen -= drop;
      brightBase += drop;
    }


    // ----------------------------------------------------------------------------------------------------
    //                                    Stage-2 sync bandpass stage
    // ----------------------------------------------------------------------------------------------------


    /// <summary>Streaming form of <see cref="SyncAudio"/>: the cosine-modulated BlackmanSinc bandpass as
    /// a stateful FIR, group delay absorbed (output on the batch timeline). Pass-through when disabled.</summary>
    private sealed class SyncBandpass : IDisposable
    {
      private readonly StreamingFir? fir;
      private readonly int groupDelay;
      private int rampRemaining;
      private float[] xBuf = new float[8192];
      private float[] yBuf = new float[8192];
      private double[] outBuf = new double[8192];

      public SyncBandpass(SstvDecodeOptions o, double fs)
      {
        float[]? bp = SstvDecoder.SyncBandKernel(o, fs);
        if (bp == null) return;
        fir = new StreamingFir(bp);
        groupDelay = fir.GroupDelay;
        rampRemaining = groupDelay;
      }

      public ReadOnlySpan<double> Process(ReadOnlySpan<double> disc)
      {
        if (fir == null || disc.IsEmpty) return disc;
        if (xBuf.Length < disc.Length)
        {
          xBuf = new float[disc.Length];
          yBuf = new float[disc.Length];
          outBuf = new double[disc.Length];
        }
        for (int i = 0; i < disc.Length; i++) xBuf[i] = (float)disc[i];
        fir.Process(xBuf.AsSpan(0, disc.Length), yBuf);
        int skip = Math.Min(rampRemaining, disc.Length);
        rampRemaining -= skip;
        int outLen = disc.Length - skip;
        for (int i = 0; i < outLen; i++) outBuf[i] = yBuf[skip + i];
        return new ReadOnlySpan<double>(outBuf, 0, outLen);
      }

      public ReadOnlySpan<double> Flush()
      {
        if (fir == null || groupDelay == 0) return ReadOnlySpan<double>.Empty;
        var zeros = new float[groupDelay];
        if (yBuf.Length < groupDelay) { yBuf = new float[groupDelay]; outBuf = new double[groupDelay]; }
        fir.Process(zeros, yBuf);
        int skip = Math.Min(rampRemaining, groupDelay);
        rampRemaining -= skip;
        int outLen = groupDelay - skip;
        for (int i = 0; i < outLen; i++) outBuf[i] = yBuf[skip + i];
        return new ReadOnlySpan<double>(outBuf, 0, outLen);
      }

      public void Dispose() => fir?.Dispose();
    }
  }
}
