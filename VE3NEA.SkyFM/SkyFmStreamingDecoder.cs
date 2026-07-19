using System;
using System.Collections.Generic;
using MathNet.Numerics;

namespace VE3NEA.SkyFM
{
  /// <summary>
  /// Streaming form of the complete SkyFM pipeline (plan §6, §1.13): feed I/Q blocks as they arrive;
  /// each transmission is transcribed and folded into the candidate set the moment the squelch
  /// finalizes it, so <see cref="Fused"/> and <see cref="Candidates"/> are live throughout the pass.
  /// Bounded state: only the voice audio a still-pending or not-yet-transcribed transmission can reach
  /// is buffered (the squelch's <see cref="FmFrontEnd.PendingStartSeconds"/> horizon); the candidate
  /// pool and fusion are per pass by design — cross-repeat fusion is required v1 function (§5.4).
  /// The engines are not owned and must outlive this object; they are called synchronously from
  /// <see cref="Process"/>/<see cref="Flush"/>.
  /// </summary>
  public sealed class SkyFmStreamingDecoder : IDisposable
  {
    private const float PeakTarget = 0.7f;

    private readonly FmFrontEnd frontEnd;
    private readonly IReadOnlyList<IAsrEngine> engines;
    private readonly SkyFmOptions options;
    private readonly Assembler assembler = new();
    private readonly List<Candidate> pool = new();
    private readonly bool keepAllVoice;

    private readonly List<float> voice = new();
    private long voiceBase;      // absolute voice-sample index of voice[0]
    private int consumed;        // finalized transmissions transcribed so far

    /// <summary>Output audio sample rate (Hz).</summary>
    public int OutputSampleRate => frontEnd.OutputSampleRate;

    /// <summary>Transmissions finalized so far (grows as the stream advances).</summary>
    public IReadOnlyList<FmTransmission> Transmissions => frontEnd.Transmissions;

    /// <summary>Fused (pre-policy) candidates over everything heard so far.</summary>
    public IReadOnlyList<Candidate> Fused { get; private set; } = [];

    /// <summary>Policy-gated candidates over everything heard so far — the product output.</summary>
    public IReadOnlyList<Candidate> Candidates { get; private set; } = [];

    /// <param name="keepAllVoice">Retain the full voice stream instead of trimming to the pending
    /// horizon — the batch wrapper's mode, where the caller wants the whole-pass audio back.</param>
    public SkyFmStreamingDecoder(IReadOnlyList<IAsrEngine> engines, SkyFmOptions? options = null,
      bool keepAllVoice = false)
    {
      this.engines = engines;
      this.options = options ?? new SkyFmOptions();
      this.keepAllVoice = keepAllVoice;
      frontEnd = new FmFrontEnd(this.options.Fm);
    }

    /// <summary>Feed the next IQ block; transcribes and fuses any transmissions it finalized.</summary>
    public void Process(ReadOnlySpan<Complex32> iq)
    {
      Append(frontEnd.Process(iq));
      Advance(final: false);
    }

    /// <summary>End of stream: drain the front end and consume every remaining transmission. Call once
    /// after the last <see cref="Process"/>.</summary>
    public void Flush()
    {
      Append(frontEnd.Flush());
      Advance(final: true);
    }

    public void Dispose() => frontEnd.Dispose();


    // ----------------------------------------------------------------------------------------------------
    //                                        streaming internals
    // ----------------------------------------------------------------------------------------------------
    private void Append(ReadOnlySpan<float> samples)
    {
      foreach (float v in samples) voice.Add(v);
    }

    /// <summary>Transcribe every finalized transmission whose audio has fully arrived, refresh the
    /// fused/gated candidate sets if anything changed, and trim the voice buffer to the pending
    /// horizon.</summary>
    private void Advance(bool final)
    {
      var transmissions = frontEnd.Transmissions;
      double availableS = (voiceBase + voice.Count) / (double)OutputSampleRate;
      bool changed = false;

      while (consumed < transmissions.Count)
      {
        var t = transmissions[consumed];
        if (!final && t.EndSeconds > availableS) break;
        Consume(t);
        consumed++;
        changed = true;
      }

      if (!keepAllVoice) Trim();
      if (changed)
      {
        Fused = CandidateFusion.Fuse(pool);
        Candidates = options.Policy.Apply(Fused);
      }
    }

    private void Consume(FmTransmission t)
    {
      long s = Math.Max(voiceBase, (long)(t.StartSeconds * OutputSampleRate));
      long e = Math.Min(voiceBase + voice.Count, (long)(t.EndSeconds * OutputSampleRate));
      if (e <= s) return;
      var clip = Normalized(s - voiceBase, (int)(e - s));

      foreach (var engine in engines)
      {
        var hyps = engine.Transcribe(clip, OutputSampleRate);
        if (hyps.Count == 0) continue;
        var words = new List<AsrWord>(hyps[0].Words.Count);
        foreach (var w in hyps[0].Words)
          words.Add(w with { StartSeconds = w.StartSeconds + t.StartSeconds, EndSeconds = w.EndSeconds + t.StartSeconds });
        pool.AddRange(options.Depth.Apply(assembler.Assemble(words), t.QuietingDepthDb));
      }
    }

    /// <summary>Per-transmission true-peak normalize (§5.1) into a fresh clip buffer.</summary>
    private float[] Normalized(long at, int len)
    {
      float peak = 0f;
      for (int i = 0; i < len; i++) { float m = Math.Abs(voice[(int)at + i]); if (m > peak) peak = m; }
      float scale = peak > 1e-9f ? PeakTarget / peak : 1f;
      var clip = new float[len];
      for (int i = 0; i < len; i++) clip[i] = voice[(int)at + i] * scale;
      return clip;
    }

    /// <summary>Discard voice no pending or unconsumed transmission can reach (minus the segment
    /// pad).</summary>
    private void Trim()
    {
      double keepFromS = frontEnd.PendingStartSeconds;
      var transmissions = frontEnd.Transmissions;
      if (consumed < transmissions.Count)
        keepFromS = Math.Min(keepFromS, transmissions[consumed].StartSeconds);
      long keepIndex = (long)((keepFromS - options.Fm.SegmentPadS) * OutputSampleRate);
      int drop = (int)Math.Clamp(keepIndex - voiceBase, 0, voice.Count);
      if (drop == 0) return;
      voice.RemoveRange(0, drop);
      voiceBase += drop;
    }


    // ----------------------------------------------------------------------------------------------------
    //                                        batch-wrapper access
    // ----------------------------------------------------------------------------------------------------
    /// <summary>The retained voice stream (requires <c>keepAllVoice</c>).</summary>
    internal float[] VoiceSnapshot()
    {
      if (!keepAllVoice) throw new InvalidOperationException("voice is trimmed unless keepAllVoice is set");
      return voice.ToArray();
    }

    internal IReadOnlyList<float> SquelchLevelDb => frontEnd.SquelchLevelDb;

    internal int BufferedVoiceSamples => voice.Count;
  }
}
