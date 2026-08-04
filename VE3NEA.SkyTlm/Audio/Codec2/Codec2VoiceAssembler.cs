using System;
using System.Collections.Generic;
using VE3NEA.SkyTlm.Core;

namespace VE3NEA.SkyTlm.Audio.Codec2
{
  /// <summary>
  /// Turns a satellite's frames into progressively-lengthening voice recordings. Frames become
  /// numbered sub-frames via the <see cref="Codec2Source"/>, sub-frames are grouped into messages, and
  /// every accepted one re-decodes the message it belongs to.
  /// <para>
  /// Re-decoding the whole message per sub-frame is the <see cref="Imaging.Ssdv.SsdvImageAssembler"/>
  /// bargain, and here it is not merely convenient but required: codec2 carries synthesis state across
  /// frames, so a sub-frame arriving late — which the decode-window overlap makes routine — cannot be
  /// appended to a running decoder. A whole message is a few hundred codec2 frames and costs under a
  /// millisecond.
  /// </para>
  /// </summary>
  public sealed class Codec2VoiceAssembler : IAudioAssembler, IDisposable
  {
    private readonly Codec2Source source;
    private readonly Codec2Decoder decoder;
    private VoiceMessage? current;
    private bool completed;

    public Codec2VoiceAssembler(Codec2Source source)
    {
      this.source = source;
      decoder = new Codec2Decoder(source.Variant);
    }

    public event Action<VoiceProduct>? VoiceUpdated;
    public event Action<VoiceProduct>? VoiceCompleted;

    /// <summary>
    /// Seconds without a sub-frame after which the next one starts a new message. Sub-frames of one
    /// message arrive back to back — 0.40 s apart at 800 bps, 1.6 s at 200 bps — and messages are
    /// minutes apart in the beacon schedule, so the default sits an order of magnitude above the
    /// former and well below the latter.
    /// </summary>
    public double MessageGapSeconds { get; init; } = 5.0;

    /// <summary>Sub-frames accepted so far, across every message.</summary>
    public int SubFramesAccepted { get; private set; }

    public void Push(Frame frame)
    {
      if (!source.TryExtract(frame, out int number, out var payload)) return;

      // Two things end a message, and between them they have to tell a restart from a straggler.
      //
      // Time is the primary signal and the reliable one: sub-frames of a message are 0.40 s apart at
      // 800 bps, and the beacon schedule puts minutes between transmissions.
      //
      // The numbering is the backstop, for a message that follows another with no gap. It cannot be
      // "the number did not advance": a sub-frame re-decoded from the overlap between two windows
      // arrives *after* its successors, and splitting on that would cut a message in half. Only a
      // number below where this message began means the counter restarted — and a number already held
      // is a duplicate, which is checked first so that a repeat of the very first sub-frame is not
      // mistaken for a restart.
      if (current != null && frame.TimeSeconds - current.LastTime > MessageGapSeconds) Complete();
      if (current != null && !current.Payloads.ContainsKey(number) && number < current.FirstNumber)
        Complete();

      current ??= new VoiceMessage();
      if (!current.Add(number, payload, frame.TimeSeconds)) return;   // a duplicate changes nothing
      SubFramesAccepted++;
      completed = false;

      VoiceUpdated?.Invoke(Product(current));
    }

    /// <summary>
    /// End of stream. The pass is over, so the message still being received will get nothing more —
    /// announce it, truncated as it is.
    /// </summary>
    public void Flush() => Complete();

    private void Complete()
    {
      if (current == null || completed) return;
      completed = true;
      VoiceCompleted?.Invoke(Product(current));
      current = null;
    }

    private VoiceProduct Product(VoiceMessage message)
    {
      // Losing sub-frames is what a bad link looks like here, and it is the only per-message quality
      // signal there is: no CRC, no FEC, nothing else to measure. Handing it to the codec lets it back
      // off its postfilter when the message is ragged.
      decoder.BerEstimate = message.Span == 0 ? 0f : 1f - (float)message.Count / message.Span;

      var pcm = decoder.Decode(message.Payloads, message.FirstNumber, message.LastNumber);

      return new VoiceProduct(
        FirstNumber: message.FirstNumber,
        LastNumber: message.LastNumber,
        Wav: WavWriter.Write(pcm, Codec2Decoder.SampleRate),
        SampleRate: Codec2Decoder.SampleRate,
        DurationSeconds: (double)pcm.Length / Codec2Decoder.SampleRate,
        SubFramesReceived: message.Count,
        SubFramesExpected: message.Span,
        Complete: message.IsContiguous,
        Fragments: message.ToFragments(),
        // Null until sub-frames can be verified on their own, which without a checksum means voting
        // several copies of one message against each other. See VoiceProduct.FragmentFormat.
        FragmentFormat: null);
    }

    public void Dispose() => decoder.Dispose();
  }
}
