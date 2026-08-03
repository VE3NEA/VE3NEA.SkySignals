using System;
using System.Collections.Generic;
using System.Linq;

namespace VE3NEA.SkyTlm.Imaging.Ssdv
{
  /// <summary>
  /// Rebuilds one picture from fragments heard on more than one occasion. SSDV is designed for this —
  /// a packet carries its own position and its own CRC, so packets of the same image are interchangeable
  /// however far apart they were received — and a satellite that repeats an image over successive passes
  /// makes it worth doing: two half-heard receptions of the same picture are often a whole one.
  /// <para>
  /// This is deliberately separate from <see cref="SsdvImageAssembler"/>, which accumulates a live
  /// stream and owns the events that go with it. Here there is no stream: the caller has fragments from
  /// an archive, decides which ones belong together, and asks for the picture they make.
  /// </para>
  /// <para>
  /// Deciding <b>which</b> fragments belong together is not this class's job and cannot be — an SSDV
  /// image ID is 8 bits and is reused within hours, so identity across receptions rests on evidence
  /// (which satellite, how long ago, what the operator asked for) that lives with the caller.
  /// </para>
  /// </summary>
  public static class SsdvMerge
  {
    /// <summary>
    /// The picture several receptions of one image make together, or null when nothing usable came of
    /// them: an unknown format, or no fragment that parses.
    /// </summary>
    /// <param name="receptions">One fragment list per reception, <b>best first</b> — see
    /// <see cref="Resolve"/> for what that decides.</param>
    /// <param name="format">The <see cref="SsdvVariant.Name"/> every fragment is in. Receptions in
    /// different formats cannot be merged and must not be passed together.</param>
    /// <param name="source">Sender label for the resulting <see cref="ImageProduct"/>, which cannot be
    /// derived here: whether the callsign field of this satellite means anything is a property of the
    /// <see cref="SsdvSource"/>, not of the packets.</param>
    public static ImageProduct? Build(IEnumerable<IReadOnlyList<ImageFragment>> receptions, string? format,
      string? source)
    {
      var variant = SsdvVariant.ByName(format);
      if (variant == null) return null;

      var packets = Parse(Resolve(receptions), variant);
      if (packets.Count == 0) return null;

      // Every packet repeats the geometry, so the image the first one opens is the image all of them
      // describe — unless a stored fragment is a different picture that reused the ID, which is what the
      // key check rejects. SsdvImage sorts by packet ID, which is what the transcoder requires.
      var image = new SsdvImage(packets[0]);
      foreach (var p in packets.Skip(1))
        if (p.CallsignCode == image.Key.CallsignCode && p.ImageId == image.ImageId) image.Add(p);

      return new ImageProduct(
        ImageId: image.ImageId,
        Source: source,
        Jpeg: SsdvTranscoder.Transcode(image.Packets),
        Width: image.Width,
        Height: image.Height,
        FragmentsReceived: image.PacketsReceived,
        FragmentsExpected: image.PacketsExpected,
        FirstGapOffset: -1,
        Complete: image.IsComplete,
        // what the picture was actually built from, which is not what was offered: a fragment that failed
        // to parse, or that belonged to another image, is not in the image and must not be counted in it
        Fragments: image.ToFragments(),
        FragmentFormat: variant.HasCrc ? variant.Name : null);
    }

    /// <summary>
    /// One copy of each fragment ID, drawn from several receptions of the same image. Where two
    /// receptions hold the same ID the cleaner copy wins — fewer bytes repaired by the FEC — and an
    /// exact tie goes to the earlier reception in <paramref name="receptions"/>, so a caller that lists
    /// the pass it is watching first never has what it just heard displaced by an archived copy of
    /// equal standing.
    /// </summary>
    /// <remarks>
    /// The correction count is the only quality signal a packet carries. It settles the case that
    /// matters — the same packet heard twice, once through a deep fade — but it cannot detect the case
    /// where the two copies are not the same packet at all, because the satellite reused the image ID
    /// for a new picture. Nothing in the packet can: that is why the callers that merge across days are
    /// the ones that must be sure of identity first.
    /// </remarks>
    public static ImageFragment[] Resolve(IEnumerable<IReadOnlyList<ImageFragment>> receptions)
    {
      var best = new Dictionary<int, ImageFragment>();

      foreach (var reception in receptions)
        foreach (var f in reception)
          if (!best.TryGetValue(f.Id, out var held) || f.CorrectedBytes < held.CorrectedBytes)
            best[f.Id] = f;

      return best.Values.OrderBy(f => f.Id).ToArray();
    }

    /// <summary>
    /// Parse stored fragments back into packets, dropping any that no longer validate. On a variant with
    /// a CRC that re-check is what makes an archive safe to read: a file that was edited, truncated or
    /// written by something else fails here rather than becoming a malformed picture. The stored
    /// correction count is put back afterwards, since bytes already repaired re-parse as clean.
    /// </summary>
    private static List<SsdvPacket> Parse(IReadOnlyList<ImageFragment> fragments, SsdvVariant variant)
    {
      var packets = new List<SsdvPacket>(fragments.Count);

      foreach (var f in fragments)
        if (SsdvPacket.TryParse(f.Bytes, variant, out var packet))
          packets.Add(packet! with { CorrectedBytes = f.CorrectedBytes });

      return packets;
    }
  }
}
