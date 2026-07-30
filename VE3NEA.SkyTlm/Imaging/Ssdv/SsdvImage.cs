using System;
using System.Collections.Generic;
using System.Linq;

namespace VE3NEA.SkyTlm.Imaging.Ssdv
{
  /// <summary>
  /// What identifies one image: the sender plus the image ID. The callsign field is part of the key
  /// because the image ID alone is only 8 bits and is reused within hours, so two senders on the same
  /// downlink would otherwise collide.
  /// </summary>
  public readonly record struct SsdvImageKey(uint CallsignCode, int ImageId);

  /// <summary>
  /// One image under construction: its packets, ordered by packet ID and de-duplicated, plus the
  /// geometry the header advertises and the coverage the receiver has so far. Packets may arrive in any
  /// order and across passes, and the same packet may arrive twice — the first copy of each ID wins,
  /// since every copy has already passed the CRC.
  /// </summary>
  public sealed class SsdvImage
  {
    private readonly SortedDictionary<int, SsdvPacket> packets = new();

    public SsdvImage(SsdvPacket first)
    {
      Key = new SsdvImageKey(first.CallsignCode, first.ImageId);
      Callsign = first.Callsign;
      Width = first.Width;
      Height = first.Height;
      Quality = first.Quality;
      Subsampling = first.Subsampling;
      Add(first);
    }

    public SsdvImageKey Key { get; }
    public int ImageId => Key.ImageId;
    public string Callsign { get; }

    /// <summary>Geometry and quality, taken from the first packet accepted (every packet of an image
    /// repeats them, so a later disagreement would mean a key collision rather than a resize).</summary>
    public int Width { get; }
    public int Height { get; }
    public int Quality { get; }
    public int Subsampling { get; }

    /// <summary>The packets held, ascending by packet ID.</summary>
    public IReadOnlyCollection<SsdvPacket> Packets => packets.Values;

    /// <summary>How many distinct packets have been accepted.</summary>
    public int PacketsReceived => packets.Count;

    /// <summary>Packet ID of the EOI packet, or −1 until it arrives.</summary>
    public int EoiPacketId { get; private set; } = -1;

    /// <summary>The image's last packet has been received, so its true length is known.</summary>
    public bool HasEoi => EoiPacketId >= 0;

    /// <summary>
    /// Total packets in the image — known exactly once the EOI packet has arrived, and before that only
    /// as a lower bound (the highest packet ID seen, plus one).
    /// </summary>
    public int PacketsExpected => HasEoi ? EoiPacketId + 1 : packets.Count == 0 ? 0 : packets.Keys.Last() + 1;

    /// <summary>
    /// Packet IDs not yet received, ascending. Bounded by <see cref="PacketsExpected"/>, so until EOI
    /// arrives this reports the holes inside what has been seen and cannot know about a missing tail.
    /// </summary>
    public IEnumerable<int> MissingPacketIds =>
      Enumerable.Range(0, PacketsExpected).Where(id => !packets.ContainsKey(id));

    /// <summary>Every packet of the image is present.</summary>
    public bool IsComplete => HasEoi && packets.Count == PacketsExpected;

    /// <summary>
    /// Add a packet. Returns false when a packet with that ID is already held (a duplicate from a
    /// re-transmission or an overlapping decode window), in which case the image is unchanged.
    /// </summary>
    public bool Add(SsdvPacket p)
    {
      if (p.CallsignCode != Key.CallsignCode || p.ImageId != Key.ImageId)
        throw new ArgumentException("packet belongs to a different image", nameof(p));
      if (!packets.TryAdd(p.PacketId, p)) return false;

      if (p.IsEoi) EoiPacketId = p.PacketId;
      return true;
    }
  }




  // ----------------------------------------------------------------------------------------------------
  //                                          image collection
  // ----------------------------------------------------------------------------------------------------
  /// <summary>
  /// The images seen on one downlink, keyed by <see cref="SsdvImageKey"/>. A sender interleaves nothing —
  /// it sends one image and moves to the next — but packets of the previous image keep arriving after
  /// the switch (retransmissions, late decodes), and passes are revisited, so images are kept side by
  /// side rather than closed the moment the image ID changes.
  /// </summary>
  public sealed class SsdvImageSet
  {
    private readonly Dictionary<SsdvImageKey, SsdvImage> images = new();

    /// <summary>Every image held, in the order first seen.</summary>
    public IReadOnlyCollection<SsdvImage> Images => images.Values;

    /// <summary>The image the last accepted packet belonged to — what the sender is transmitting now.</summary>
    public SsdvImage? Current { get; private set; }

    /// <summary>
    /// File the packet under its image, creating the image if this is the first packet of it. Returns
    /// false for a duplicate. <paramref name="image"/> is the image the packet belongs to either way,
    /// and <paramref name="isNewImage"/> reports that the sender has moved on, which is a caller's cue
    /// to finalise whatever it was showing before.
    /// </summary>
    public bool Add(SsdvPacket p, out SsdvImage image, out bool isNewImage)
    {
      var key = new SsdvImageKey(p.CallsignCode, p.ImageId);
      isNewImage = !images.TryGetValue(key, out var existing);

      if (isNewImage)
      {
        image = new SsdvImage(p);
        images[key] = image;
        Current = image;
        return true;
      }

      image = existing!;
      bool added = image.Add(p);
      if (added) Current = image;
      return added;
    }
  }
}
