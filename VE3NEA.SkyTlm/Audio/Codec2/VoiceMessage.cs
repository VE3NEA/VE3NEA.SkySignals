using System;
using System.Collections.Generic;
using System.Linq;

namespace VE3NEA.SkyTlm.Audio.Codec2
{
  /// <summary>
  /// One voice message being received: the sub-frames heard so far, keyed by their on-air number.
  /// <para>
  /// The audio counterpart of <see cref="Imaging.Ssdv.SsdvImage"/>, but it cannot be keyed the way an
  /// image is. An SSDV packet names its image; a voice sub-frame carries <b>only</b> an 8-bit position
  /// within a message that is never identified. So a message here is defined by what a run of numbers
  /// arriving close together means, and <see cref="Codec2VoiceAssembler"/> owns that rule.
  /// </para>
  /// </summary>
  public sealed class VoiceMessage
  {
    private readonly SortedDictionary<int, byte[]> payloads = [];

    /// <summary>Time of the first sub-frame accepted, from the frame's own timestamp.</summary>
    public double StartTime { get; private set; }

    /// <summary>Time of the most recent sub-frame accepted — what the gap rule is measured from.</summary>
    public double LastTime { get; private set; }

    /// <summary>The most recent sub-frame number accepted, which is what a restart is judged against.
    /// Deliberately not <see cref="LastNumber"/>: a sub-frame arriving late out of a decode-window
    /// overlap must not make the next in-order one look like a new message.</summary>
    public int LastAccepted { get; private set; } = -1;

    /// <summary>Lowest and highest sub-frame numbers held.</summary>
    public int FirstNumber => payloads.Count == 0 ? -1 : payloads.Keys.First();
    public int LastNumber => payloads.Count == 0 ? -1 : payloads.Keys.Last();

    /// <summary>How many distinct sub-frames have been accepted.</summary>
    public int Count => payloads.Count;

    /// <summary>Sub-frames the reconstruction spans, holes included. See
    /// <see cref="VoiceProduct.SubFramesExpected"/> for why this is not the message's true length.</summary>
    public int Span => payloads.Count == 0 ? 0 : LastNumber - FirstNumber + 1;

    /// <summary>Numbers inside the span that never arrived.</summary>
    public IEnumerable<int> MissingNumbers =>
      Enumerable.Range(FirstNumber, Math.Max(Span, 0)).Where(n => !payloads.ContainsKey(n));

    /// <summary>No holes between the first and last sub-frame held. Not "the whole message arrived" —
    /// nothing on air says how long a message is.</summary>
    public bool IsContiguous => payloads.Count == Span;

    /// <summary>The payloads, for the decoder.</summary>
    public IReadOnlyDictionary<int, byte[]> Payloads => payloads;

    /// <summary>
    /// Add one sub-frame. Returns false when it duplicates one already held — which happens routinely,
    /// because a burst that straddles two decode windows is decoded twice. The first copy wins: with no
    /// checksum anywhere there is no basis for preferring the second.
    /// </summary>
    public bool Add(int number, byte[] payload, double time)
    {
      LastAccepted = number;
      LastTime = time;
      if (!payloads.TryAdd(number, payload)) return false;
      if (payloads.Count == 1) StartTime = time;
      return true;
    }

    /// <summary>The sub-frames held, ascending by number, in the form they arrived in.</summary>
    public VoiceFragment[] ToFragments() =>
      payloads.Select(kv => new VoiceFragment(kv.Key, kv.Value)).ToArray();
  }
}
