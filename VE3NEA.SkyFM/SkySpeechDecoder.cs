using System;
using System.Collections.Generic;
using MathNet.Numerics;

namespace VE3NEA.SkyFM
{
  /// <summary>
  /// Streaming FM-speech <b>transcript</b> decoder for the SkyRoof Telemetry panel (integration plan A2,
  /// ASR-plan.md §10.2/§10.3): I/Q blocks in, decoded-transcript lines out. It shares the proven
  /// <see cref="FmFrontEnd"/> (channel FIR → discriminator → squelch segmenter → 16 kHz voice) with
  /// <see cref="SkyFmStreamingDecoder"/>, but its consumer is the display path, not the identifier
  /// assembly: each finalized transmission is transcribed and its words fed to an
  /// <see cref="FmTranscriptBuilder"/>, which emits pause-formatted lines. The precision-first
  /// callsign/grid extraction (§5.4/§5.5) stays in <see cref="SkyFmStreamingDecoder"/> for a later
  /// richer view; this first integration is the raw symbol stream (§10.2).
  ///
  /// <para>The whole-pass 16 kHz voice is retained (not trimmed to the pending horizon) so
  /// <see cref="GetAudio"/> can serve the §10.4 click-to-play span; a full pass is ~19 MB/10 min. The
  /// engine is <b>not owned</b> (the host shares one lazily-loaded instance across transmitter changes,
  /// integration plan A2 engine-lifetime note) and is called synchronously from
  /// <see cref="Process"/>/<see cref="Flush"/> on the decode worker thread.</para>
  /// </summary>
  public sealed class SkySpeechDecoder : IDisposable
  {
    private const float PeakTarget = 0.7f;

    private readonly FmFrontEnd frontEnd;
    private readonly IAsrEngine engine;
    private readonly FmTranscriptBuilder builder;

    private readonly List<float> voice = new();   // whole-pass 16 kHz voice (Hz units), never trimmed
    private int consumed;                          // finalized transmissions transcribed so far

    /// <summary>A transcript line closed (its text and audio span are final). Fires on the decode worker
    /// thread; the payload is immutable and safe to marshal. <c>Index</c> is its position in
    /// <see cref="Lines"/>.</summary>
    public event Action<FmTranscriptLine, int>? LineCompleted;

    /// <summary>The in-progress (open) line advanced — its latest snapshot, for the live tail. May fire
    /// repeatedly for the same line as more words arrive.</summary>
    public event Action<FmTranscriptLine>? LineUpdated;

    /// <summary>Output audio sample rate (Hz).</summary>
    public int OutputSampleRate => frontEnd.OutputSampleRate;

    /// <summary>Transmissions finalized so far (grows as the stream advances).</summary>
    public IReadOnlyList<FmTransmission> Transmissions => frontEnd.Transmissions;

    /// <summary>Closed transcript lines so far, in time order.</summary>
    public IReadOnlyList<FmTranscriptLine> Lines => builder.Lines;

    /// <summary>The in-progress (not-yet-closed) line, or null.</summary>
    public FmTranscriptLine? Pending => builder.Pending;

    public SkySpeechDecoder(IAsrEngine engine, FmDecodeOptions? fmOptions = null,
      FmTranscriptOptions? transcriptOptions = null)
    {
      this.engine = engine;
      frontEnd = new FmFrontEnd(fmOptions ?? new FmDecodeOptions());
      builder = new FmTranscriptBuilder(transcriptOptions);
    }

    /// <summary>Feed the next IQ block; transcribes and emits lines for any transmissions it
    /// finalized.</summary>
    public void Process(ReadOnlySpan<Complex32> iq)
    {
      Append(frontEnd.Process(iq));
      Advance(final: false);
    }

    public void Process(Complex32[] block) => Process(block.AsSpan());

    /// <summary>End of pass: drain the front end, transcribe every remaining transmission, and close the
    /// open line. Call once after the last <see cref="Process"/>.</summary>
    public void Flush()
    {
      Append(frontEnd.Flush());
      Advance(final: true);

      int before = builder.Lines.Count;
      builder.Flush();
      for (int i = before; i < builder.Lines.Count; i++) LineCompleted?.Invoke(builder.Lines[i], i);
    }

    public void Dispose() => frontEnd.Dispose();

    /// <summary>The 16 kHz voice audio spanning <paramref name="startSeconds"/>..<paramref
    /// name="endSeconds"/> (recording-relative), true-peak-normalized for playback (§10.4 click-to-play).
    /// Empty when the range is outside the retained audio.</summary>
    public float[] GetAudio(double startSeconds, double endSeconds)
    {
      long s = Math.Clamp((long)(startSeconds * OutputSampleRate), 0, voice.Count);
      long e = Math.Clamp((long)(endSeconds * OutputSampleRate), 0, voice.Count);
      return e <= s ? Array.Empty<float>() : Normalized((int)s, (int)(e - s));
    }


    // ----------------------------------------------------------------------------------------------------
    //                                        streaming internals
    // ----------------------------------------------------------------------------------------------------
    private void Append(ReadOnlySpan<float> samples)
    {
      foreach (float v in samples) voice.Add(v);
    }

    /// <summary>Transcribe every finalized transmission whose audio has fully arrived and fold its words
    /// into the transcript builder, firing line events for what closed/advanced.</summary>
    private void Advance(bool final)
    {
      var transmissions = frontEnd.Transmissions;
      double availableS = voice.Count / (double)OutputSampleRate;

      while (consumed < transmissions.Count)
      {
        var t = transmissions[consumed];
        if (!final && t.EndSeconds > availableS) break;
        Consume(t);
        consumed++;
      }
    }

    private void Consume(FmTransmission t)
    {
      long s = Math.Max(0, (long)(t.StartSeconds * OutputSampleRate));
      long e = Math.Min(voice.Count, (long)(t.EndSeconds * OutputSampleRate));
      if (e <= s) return;
      var clip = Normalized((int)s, (int)(e - s));

      // the whole squelch-open interval is one line (merged with a near-abutting neighbor by the builder),
      // its span the transmission times; an interval the engine heard nothing in still prints "???"
      var hyps = engine.Transcribe(clip, OutputSampleRate);
      IEnumerable<AsrWord> words = hyps.Count > 0 ? hyps[0].Words : Array.Empty<AsrWord>();

      int linesBefore = builder.Lines.Count;
      var pendingBefore = builder.Pending;
      builder.Add(t.StartSeconds, t.EndSeconds, words);

      for (int i = linesBefore; i < builder.Lines.Count; i++) LineCompleted?.Invoke(builder.Lines[i], i);
      var pending = builder.Pending;
      if (pending != null && pending != pendingBefore) LineUpdated?.Invoke(pending);
    }

    /// <summary>True-peak normalize voice[at..at+len) to <see cref="PeakTarget"/> in a fresh buffer
    /// (§5.1).</summary>
    private float[] Normalized(int at, int len)
    {
      float peak = 0f;
      for (int i = 0; i < len; i++) { float m = Math.Abs(voice[at + i]); if (m > peak) peak = m; }
      float scale = peak > 1e-9f ? PeakTarget / peak : 1f;
      var clip = new float[len];
      for (int i = 0; i < len; i++) clip[i] = voice[at + i] * scale;
      return clip;
    }
  }
}
