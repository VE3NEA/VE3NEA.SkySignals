using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using VE3NEA.SkyTlm.Imaging;
using VE3NEA.SkyTlm.Imaging.Ssdv;
using VE3NEA.SkyTlm.Tests.Regression;
using Xunit;

namespace VE3NEA.SkyTlm.Tests.Unit
{
  /// <summary>
  /// Rebuilding one picture from fragments heard on separate occasions. The premise is that SSDV packets
  /// of an image are interchangeable however far apart they were received, so the load-bearing test is
  /// the one that splits a complete off-air image in two, hands the halves back as if they had come from
  /// two passes, and requires the result to be the original picture byte for byte.
  /// </summary>
  public class SsdvMergeTests
  {
    private const string Format = "Standard256";

    /// <summary>The off-air HADES-SA fixture as stored: concatenated canonical 256-byte packets, ascending
    /// by packet ID. Read here as archived <see cref="ImageFragment"/>s rather than as packets, which is
    /// the form a merge consumes.</summary>
    private static ImageFragment[] ReadFragments(string name)
    {
      var bytes = File.ReadAllBytes(Path.Combine(TestPaths.DataDir, "Ssdv", name + ".ssdv"));

      return Enumerable.Range(0, bytes.Length / 256).Select(i =>
      {
        var packet = bytes[(i * 256)..((i + 1) * 256)];
        SsdvPacket.TryParse(packet, SsdvVariant.HadesSa251, out var p).Should().BeTrue();
        return new ImageFragment(p!.PacketId, packet, 0);
      }).ToArray();
    }

    private static byte[] Reference(string name) =>
      File.ReadAllBytes(Path.Combine(TestPaths.DataDir, "Ssdv", name + ".ref.jpg"));

    private static ImageProduct? Merge(params IReadOnlyList<ImageFragment>[] receptions) =>
      SsdvMerge.Build(receptions, Format, null);



    // ---- two receptions of one picture -------------------------------------------------------------

    [Fact]
    public void TwoDisjointHalves_RebuildTheWholePicture()
    {
      var all = ReadFragments("hades-sa_img235_complete");
      var first = all.Where((_, i) => i % 2 == 0).ToArray();
      var second = all.Where((_, i) => i % 2 != 0).ToArray();

      var merged = Merge(first, second);

      merged.Should().NotBeNull();
      merged!.FragmentsReceived.Should().Be(all.Length);
      merged.Complete.Should().BeTrue("the EOI packet is in one of the halves and every ID is present");
      merged.Jpeg.Should().Equal(Reference("hades-sa_img235_complete"),
        "packets carry their own position, so where they were heard cannot affect the picture they make");
    }

    [Fact]
    public void ASecondReceptionFillsTheHolesInTheFirst()
    {
      // 12 of 15 packets from one pass, the whole image from another: the archived copy supplies exactly
      // the three that were lost, which is the case the feature exists for
      var partial = ReadFragments("hades-sa_img235_12of15");
      var complete = ReadFragments("hades-sa_img235_complete");

      var merged = Merge(partial, complete);

      merged!.FragmentsReceived.Should().Be(complete.Length);
      merged.Jpeg.Should().Equal(Reference("hades-sa_img235_complete"));
    }

    [Fact]
    public void OneReceptionAloneIsUnchangedByTheMerge()
    {
      var partial = ReadFragments("hades-sa_img235_12of15");

      var merged = Merge(partial);

      merged!.FragmentsReceived.Should().Be(partial.Length);
      merged.Complete.Should().BeFalse("three packets are still missing and one of them is the last");
      merged.Jpeg.Should().Equal(Reference("hades-sa_img235_12of15"));
    }

    [Fact]
    public void FragmentsComeBackOrderedByIdWhateverOrderTheReceptionsAreIn()
    {
      var all = ReadFragments("hades-sa_img231_complete");
      var reversed = all.Reverse().ToArray();

      var merged = Merge(reversed);

      merged!.Fragments.Select(f => f.Id).Should().BeInAscendingOrder(
        "the transcoder requires ascending packet IDs, so the merge cannot pass its input through");
    }



    // ---- choosing between two copies of one fragment ------------------------------------------------

    [Fact]
    public void TheCleanerCopyOfADuplicateWins()
    {
      var all = ReadFragments("hades-sa_img235_complete");
      // the same fragment twice: the archived copy is the clean one, the copy just received needed 12
      // bytes of RS repair. Cleanliness decides, not recency.
      var repaired = all.Select(f => f with { CorrectedBytes = 12 }).ToArray();

      var merged = Merge(repaired, all);

      merged!.Fragments.Should().OnlyContain(f => f.CorrectedBytes == 0);
    }

    [Fact]
    public void AnExactTieGoesToTheEarlierReception()
    {
      var all = ReadFragments("hades-sa_img235_complete");
      // two copies equally clean and byte-identical, held in different arrays so that which one survived
      // can be told apart by reference. The caller lists the pass it is watching first, and what it just
      // heard must not be displaced by an archived copy of equal standing.
      var archived = all.Select(f => f with { Bytes = f.Bytes.ToArray() }).ToArray();

      var resolved = SsdvMerge.Resolve([all, archived]);

      resolved.Should().HaveCount(all.Length);
      foreach (var f in resolved)
        f.Bytes.Should().BeSameAs(all.Single(a => a.Id == f.Id).Bytes,
          "the first reception's own fragment is kept, not an equal copy from the archive");
    }

    [Fact]
    public void ACleanerArchivedCopyDisplacesTheOneJustHeard()
    {
      var all = ReadFragments("hades-sa_img235_complete");
      var thisPass = all.Select(f => f with { CorrectedBytes = 9 }).ToArray();

      var resolved = SsdvMerge.Resolve([thisPass, all]);

      resolved.Should().OnlyContain(f => f.CorrectedBytes == 0,
        "recency is the tiebreak, not the rule — a copy that needed no repair is the better one");
    }



    // ---- what must not be merged --------------------------------------------------------------------

    [Fact]
    public void AnAlteredFragmentIsDroppedRatherThanTrusted()
    {
      var all = ReadFragments("hades-sa_img235_complete");
      // an archived packet damaged past what RS can put back — 40 bytes against a capacity of 16
      var damaged = all[3].Bytes.ToArray();
      for (int i = 100; i < 140; i++) damaged[i] ^= 0xFF;
      var tampered = new[] { all[3] with { Bytes = damaged } };

      var merged = Merge(all.Where((_, i) => i != 3).ToArray(), tampered);

      merged!.Fragments.Should().NotContain(f => f.Id == all[3].Id,
        "a stored fragment that no longer passes its CRC is not this picture's, whatever the file says");
    }

    [Fact]
    public void ASlightlyDamagedFragmentIsRepairedRatherThanLost()
    {
      var all = ReadFragments("hades-sa_img235_complete");
      // a byte lost to the disk rather than to the link. The stored packet is a whole RS codeword, so
      // reading an archive re-runs the same repair that reception did, and small damage costs nothing.
      var damaged = all[3].Bytes.ToArray();
      damaged[100] ^= 0xFF;
      var repairable = new[] { all[3] with { Bytes = damaged } };

      var merged = Merge(all.Where((_, i) => i != 3).ToArray(), repairable);

      merged!.FragmentsReceived.Should().Be(all.Length);
      merged.Jpeg.Should().Equal(Reference("hades-sa_img235_complete"));
    }

    [Fact]
    public void AFragmentOfADifferentPictureIsRefused()
    {
      // two images that reused nothing but this test's imagination: image 231's packets offered alongside
      // image 235's. The geometry differs, and a merge that accepted them would render one inside the other.
      var wanted = ReadFragments("hades-sa_img235_complete");
      var other = ReadFragments("hades-sa_img231_complete");

      var merged = Merge(wanted, other);

      merged!.ImageId.Should().Be(235);
      merged.Jpeg.Should().Equal(Reference("hades-sa_img235_complete"),
        "packets keyed to another image must not reach the transcoder");
    }

    [Fact]
    public void AnUnknownFormatYieldsNothing()
    {
      var all = ReadFragments("hades-sa_img235_complete");

      SsdvMerge.Build([all], "Standard512", null).Should().BeNull(
        "an archive written by a later build is a file to skip, not one to guess at");
      SsdvMerge.Build([all], null, null).Should().BeNull();
    }

    [Fact]
    public void NoFragmentsYieldsNothing() =>
      SsdvMerge.Build([], Format, null).Should().BeNull();



    // ---- the archived form itself -------------------------------------------------------------------

    [Fact]
    public void AValidatedPacketReparsesToItself()
    {
      var all = ReadFragments("hades-sa_img226_5pkt");

      foreach (var f in all)
      {
        SsdvPacket.TryParse(f.Bytes, SsdvVariant.HadesSa251, out var p).Should().BeTrue();
        p!.Bytes.Should().Equal(f.Bytes, "SsdvPacket.Bytes is what an archive stores and hands back");
      }
    }

    [Fact]
    public void OnlyAVariantWithItsOwnCrcIsArchivable()
    {
      // the gate that keeps JY1SAT out of the archive: its packets carry no check of their own, so a
      // stored copy could not later be told from a damaged one, nor from a different picture's
      SsdvVariant.HadesSa251.HasCrc.Should().BeTrue();
      SsdvVariant.Jy1Sat200.HasCrc.Should().BeFalse();
      SsdvVariant.Dslwp218.HasCrc.Should().BeFalse();
    }

    [Theory]
    [InlineData("Standard256")]
    [InlineData("NoFec256")]
    [InlineData("Jy1Sat200")]
    [InlineData("SilverSat195")]
    [InlineData("Dslwp218")]
    public void EveryVariantIsFoundByTheNameItIsStoredUnder(string name)
    {
      var variant = SsdvVariant.ByName(name);

      variant.Should().NotBeNull("archived fragments name their format and must be readable again");
      variant!.Name.Should().Be(name);
    }

    [Fact]
    public void AnUnknownVariantNameIsNotGuessedAt() => SsdvVariant.ByName("Standard512").Should().BeNull();
  }
}
