using System;
using System.Collections.Generic;
using VE3NEA.SkyTlm.Core;

namespace VE3NEA.SkyTlm.Audio
{
  /// <summary>
  /// One sub-frame of a voice message, kept in the form it was accepted in so that a caller can
  /// archive it and hand it back later.
  /// <para>
  /// Unlike <see cref="Imaging.ImageFragment"/> there is no <c>CorrectedBytes</c>, because there is no
  /// FEC and no checksum anywhere in this path: a stored sub-frame cannot be re-verified, and two
  /// copies of one sub-frame cannot be ranked against each other. Copies can still be <i>voted</i>,
  /// which is what makes them worth keeping — see the remarks on
  /// <see cref="VoiceProduct.FragmentFormat"/>.
  /// </para>
  /// </summary>
  /// <param name="Number">The sub-frame's position in the message — the on-air 8-bit frame number.</param>
  /// <param name="Payload">The payload bytes exactly as received, before the XOR mask and the repack.
  /// Storing the on-air form rather than the codec2 form is what lets a stored fragment be re-decoded
  /// if the variant is ever corrected.</param>
  public readonly record struct VoiceFragment(int Number, byte[] Payload);


  /// <summary>
  /// One voice message as it stands right now — always a playable 8 kHz mono WAV, however little of it
  /// has arrived, so a UI can offer it at any point in a pass. Missing sub-frames are silence in their
  /// correct place rather than absent, so the timeline of what did arrive is never shifted.
  /// </summary>
  /// <param name="FirstNumber">Lowest sub-frame number received. Often not 0: the start of a message
  /// is routinely missed, and there is no way to know how much of it went by before we tuned in.</param>
  /// <param name="LastNumber">Highest sub-frame number received.</param>
  /// <param name="Wav">The audio file. Never empty once any sub-frame has been accepted.</param>
  /// <param name="SampleRate">Samples per second — 8000, the only rate codec2 speaks.</param>
  /// <param name="DurationSeconds">Length of <paramref name="Wav"/>, gaps included.</param>
  /// <param name="SubFramesReceived">Distinct sub-frames accepted.</param>
  /// <param name="SubFramesExpected">Sub-frames the reconstruction spans,
  /// <c>LastNumber − FirstNumber + 1</c>. This is <b>not</b> the length of the message the satellite
  /// sent: nothing on air marks where a message ends, and the beginning may never have been heard. It
  /// is the denominator for "how much of what we heard is intact", nothing more.</param>
  /// <param name="Complete">Every sub-frame between <paramref name="FirstNumber"/> and
  /// <paramref name="LastNumber"/> is present — i.e. the reconstruction has no holes. It does
  /// <b>not</b> mean the whole message was received; see <paramref name="SubFramesExpected"/>.</param>
  /// <param name="Fragments">The sub-frames this reconstruction was built from, ascending by number.</param>
  /// <param name="FragmentFormat">Name of the <see cref="Codec2Variant"/> the fragments are in, or
  /// null when they must not be archived. Null today and for the same reason JY1SAT's SSDV packets are
  /// null: a sub-frame carries no integrity check of its own, so a stored copy cannot be told from a
  /// damaged one. Voting many copies is the way out of that, and it is not built yet.</param>
  public sealed record VoiceProduct(
    int FirstNumber, int LastNumber, byte[] Wav, int SampleRate, double DurationSeconds,
    int SubFramesReceived, int SubFramesExpected, bool Complete,
    IReadOnlyList<VoiceFragment> Fragments, string? FragmentFormat);


  /// <summary>
  /// Accumulates frames into voice messages. The audio counterpart of
  /// <see cref="Imaging.IImageAssembler"/>, deliberately the same shape so the application has one
  /// pattern for both.
  /// <para>
  /// <see cref="Push"/> may be fed every frame the deframer produces, unconditionally: on HADES-SA the
  /// voice sub-frames are interleaved with telemetry, SSDV and PN9 on one downlink, so an assembler
  /// ignoring a frame is the normal case, not an error.
  /// </para>
  /// </summary>
  public interface IAudioAssembler
  {
    /// <summary>Offer one decoded frame. Frames that are not voice sub-frames are ignored.</summary>
    void Push(Frame frame);

    /// <summary>
    /// End of the stream: finalise the message still being received. A pass almost never ends on a
    /// message boundary, so without this the last message of a pass would never be announced.
    /// </summary>
    void Flush();

    /// <summary>Fires for each accepted sub-frame, with the message it belongs to.</summary>
    event Action<VoiceProduct>? VoiceUpdated;

    /// <summary>
    /// Fires when a message will receive nothing further: the numbering restarted, the gap since the
    /// last sub-frame grew too large, or <see cref="Flush"/> was called. A completed message may still
    /// have holes, and may still be missing its beginning and end.
    /// </summary>
    event Action<VoiceProduct>? VoiceCompleted;
  }
}
