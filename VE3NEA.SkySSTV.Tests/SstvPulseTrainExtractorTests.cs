using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace VE3NEA.SkySSTV.Tests
{
  /// <summary>
  /// P6(b) tests for the MHT pulse-train extractor (plan §4.1), driven by synthetic pulse lists: a clean
  /// periodic train must spawn/promote one hypothesis and claim the scan lines; scattered clutter must
  /// never promote; a fade must coast within one train; sequential bursts (the UmKA-1 pattern) must come
  /// out as separate trains; a VIS seed must promote on just 3 confirming pulses.
  /// </summary>
  public class SstvPulseTrainExtractorTests
  {
    private const double Fs = 48000.0;
    private const int Block = 12000;                        // extractor block (0.25 s @ 48 kHz)
    private readonly ITestOutputHelper output;
    public SstvPulseTrainExtractorTests(ITestOutputHelper o) => output = o;

    private static SstvPulse P(int t, double durMs = 9.0, float power = 0.4f)
      => new SstvPulse(t, power, (float)durMs);

    /// <summary>Feed <paramref name="pulses"/> (time-sorted) block-wise, as the streaming driver would.</summary>
    private static SstvPulseTrainExtractor Run(List<SstvPulse> pulses, int endTime)
    {
      var extractor = new SstvPulseTrainExtractor(Fs);
      Feed(extractor, pulses, endTime);
      return extractor;
    }

    private static void Feed(SstvPulseTrainExtractor extractor, List<SstvPulse> pulses, int endTime)
    {
      int i = 0;
      for (int blockEnd = Block; blockEnd < endTime + Block; blockEnd += Block)
      {
        var batch = new List<SstvPulse>();
        while (i < pulses.Count && pulses[i].Time < blockEnd) batch.Add(pulses[i++]);
        extractor.Process(batch, Math.Min(blockEnd, endTime));
      }
      extractor.Finish(endTime);
    }

    /// <summary>Robot36 sync-pulse train: <paramref name="count"/> pulses from <paramref name="start"/>.</summary>
    private static List<SstvPulse> Robot36Train(int start, int count, double period = 7200.0)
    {
      var pulses = new List<SstvPulse>();
      for (int k = 0; k < count; k++) pulses.Add(P(start + (int)Math.Round(k * period)));
      return pulses;
    }


    // ----------------------------------------------------------------------------------------------------
    //                                       spawn / promote / claim
    // ----------------------------------------------------------------------------------------------------


    [Fact]
    public void CleanTrain_PromotesOneHypothesis_AndClaimsLines()
    {
      var pulses = Robot36Train(36000, 100);
      int end = pulses[^1].Time + 12000;
      var extractor = Run(pulses, end);

      var active = extractor.Trains.Where(t => t.State == SstvTrainState.Active).ToList();
      output.WriteLine($"trains: {extractor.Trains.Count}, active: {active.Count}, lines: {extractor.Lines.Count}");
      active.Should().HaveCount(1, "one periodic train must yield exactly one promoted hypothesis");
      active[0].Format.Should().Be(SstvMode.Robot36);
      active[0].PulseCnt.Should().Be(100, "back-fill must adopt the pulses that preceded promotion");

      var best = extractor.BestTrain();
      best.Should().BeSameAs(active[0]);
      Math.Round(best!.Regr.GetPulseTime(0)).Should().BeApproximately(36000, 3);
      best.Regr.Period.Should().BeApproximately(7200.0, 0.1);

      // the scan lines claimed by the train must be consecutive pulse numbers at its grid
      var claimed = extractor.Lines.Where(l => l.Train == best).ToList();
      claimed.Count.Should().BeGreaterThan(90, "nearly every line of the burst must be claimed");
      for (int i = 1; i < claimed.Count; i++)
        (claimed[i].PulseNo - claimed[i - 1].PulseNo).Should().Be(1, "claimed lines are consecutive");
    }

    [Fact]
    public void ScatteredClutter_NeverPromotes()
    {
      // strong-but-scattered: random spikes at 2–5 per second with no period consistency (plan §4.1 —
      // weak-and-consistent must beat strong-and-scattered, and scattered alone must yield nothing)
      var rng = new Random(11);
      var pulses = new List<SstvPulse>();
      int t = 5000;
      while (t < 60 * 48000)
      {
        pulses.Add(P(t, durMs: rng.Next(2) == 0 ? 9.0 : 20.0, power: 0.5f));
        t += 10000 + rng.Next(30000);
      }
      var extractor = Run(pulses, 60 * 48000);

      extractor.Trains.Where(t => t.State == SstvTrainState.Active).Should().BeEmpty(
        "clutter with no period consistency must never promote");
      extractor.BestTrain().Should().BeNull();
      extractor.Lines.Should().BeEmpty();
    }

    [Fact]
    public void Fade_CoastsWithinOneTrain()
    {
      // a 12-line fade (1.8 s < the 6 s retire timeout): the regressor rides through the gap and the
      // pulses after it must associate with the SAME train — no second hypothesis, no split image
      var pulses = Robot36Train(36000, 40);
      pulses.AddRange(Robot36Train(36000 + 52 * 7200, 40));
      pulses.Sort((a, b) => a.Time.CompareTo(b.Time));
      int end = pulses[^1].Time + 12000;
      var extractor = Run(pulses, end);

      var active = extractor.Trains.Where(t => t.State == SstvTrainState.Active).ToList();
      output.WriteLine($"active trains: {active.Count}, pulses on first: {active.FirstOrDefault()?.PulseCnt}");
      active.Should().HaveCount(1, "a fade shorter than the retire timeout must not fragment the train");
      active[0].PulseCnt.Should().Be(80, "both sides of the fade belong to the same hypothesis");
    }

    [Fact]
    public void SequentialBursts_YieldSeparateTrains()
    {
      // the UmKA-1 pattern: two SSTV bursts separated by >6 s of other traffic — the first train must
      // retire and the second burst must spawn a fresh hypothesis; both claim their own lines
      var pulses = Robot36Train(48000, 60);
      int gapStart = pulses[^1].Time;
      int burst2 = gapStart + 10 * 48000;                    // 10 s gap > the 6 s retire timeout
      pulses.AddRange(Robot36Train(burst2, 60));
      int end = pulses[^1].Time + 8 * 48000;
      var extractor = Run(pulses, end);

      var used = extractor.Lines.Select(l => l.Train).Distinct().ToList();
      output.WriteLine($"trains: {extractor.Trains.Count}, line-claiming trains: {used.Count}");
      used.Should().HaveCount(2, "two separated bursts are two pulse trains");
      used[0].State.Should().Be(SstvTrainState.Retired, "the first burst ended >6 s before the stream end");

      int firstOfSecond = (int)Math.Round(used[1].Regr.GetPulseTime(0));
      firstOfSecond.Should().BeCloseTo(burst2, 5);
    }


    // ----------------------------------------------------------------------------------------------------
    //                                          VIS-seeded train
    // ----------------------------------------------------------------------------------------------------


    [Fact]
    public void VisSeed_PromotesOnThreePulses()
    {
      // the high-prior path: with a VIS anchor, 3 on-grid pulses suffice (a triplet-spawned candidate
      // needs ~11) — verify the VIS train owns the pulses and promotes while a 3-pulse plain candidate
      // could not have
      var extractor = new SstvPulseTrainExtractor(Fs);
      extractor.AddVisTrain(SstvMode.Robot36, 48000);
      var pulses = Robot36Train(48000, 3);
      Feed(extractor, pulses, pulses[^1].Time + 12000);

      var vis = extractor.Trains.OfType<SstvVisPulseTrain>().Single();
      output.WriteLine($"vis train: state={vis.State} pulses={vis.PulseCnt}");
      vis.State.Should().Be(SstvTrainState.Active, "a VIS-seeded train promotes on just 3 confirming pulses");
      vis.PulseCnt.Should().Be(3);
      Math.Round(vis.Regr.GetPulseTime(0)).Should().BeApproximately(48000, 3);
    }

    [Fact]
    public void MidBurstPhaseJump_YieldsOneTrain()
    {
      // a mid-burst fade where the sync timing wanders past the RLS gate (here an 8.3 ms phase jump)
      // used to re-spawn a duplicate hypothesis on the same transmission (the duplicate-image defect,
      // fixed 2026-07-03). Now: while the first train is Active no new spawn is allowed; once it
      // retires, the continuation spawns — and merge-on-promote folds it back into the original train
      // because the old grid still predicts its start within the merge wing.
      var pulses = Robot36Train(36000, 40);
      pulses.AddRange(Robot36Train(36000 + 40 * 7200 + 400, 90));   // 400 samples = 8.3 ms jump
      int end = pulses[^1].Time + 12000;
      var extractor = Run(pulses, end);

      var promoted = extractor.Trains
        .Where(t => t.State == SstvTrainState.Active || t.State == SstvTrainState.Retired).ToList();
      output.WriteLine($"promoted trains: {promoted.Count}, pulses on first: {promoted.FirstOrDefault()?.PulseCnt}");
      promoted.Should().HaveCount(1, "one transmission must yield one train despite the phase jump");
      promoted[0].PulseCnt.Should().BeGreaterThan(70, "the merged train holds both sides of the jump");
    }

    [Fact]
    public void SeparateTransmissions_DoNotMerge()
    {
      // two bursts a minute apart (far beyond the merge gap) must stay two trains even if their grids
      // happen to align — merging is only for fade-split fragments
      var pulses = Robot36Train(48000, 60);
      pulses.AddRange(Robot36Train(48000 + 60 * 7200 + 60 * 48000, 60));   // 60 s gap, grid-aligned
      int end = pulses[^1].Time + 8 * 48000;
      var extractor = Run(pulses, end);

      extractor.Trains
        .Where(t => t.State == SstvTrainState.Active || t.State == SstvTrainState.Retired)
        .Should().HaveCount(2, "transmissions separated by a minute are distinct images");
    }

    [Fact]
    public void SoftPulses_ConfirmButNeverSpawn()
    {
      // two-tier soft evidence (plan §4.1): associate-tier pulses (score < 0.18) can never seed a
      // hypothesis, however period-consistent — but once a strong triplet exists, they confirm it and
      // carry it to promotion (the weak-and-consistent evidence a hard 0.18 gate used to discard)
      var soft = Robot36Train(36000, 60);
      for (int i = 0; i < soft.Count; i++) soft[i] = P(soft[i].Time, power: 0.12f);
      var extractor = Run(soft, soft[^1].Time + 12000);
      extractor.Trains.Should().BeEmpty("soft pulses alone must not spawn any hypothesis");

      // 6 strong pulses (the MinStrongPromote floor) + soft confirmations — the train must promote, and
      // every on-grid soft pulse must associate
      var mixed = Robot36Train(36000, 60);
      for (int i = 6; i < mixed.Count; i++) mixed[i] = P(mixed[i].Time, power: 0.12f);
      extractor = Run(mixed, mixed[^1].Time + 12000);
      var train = extractor.Trains.Single();
      output.WriteLine($"mixed train: state={train.State} pulses={train.PulseCnt}");
      train.State.Should().Be(SstvTrainState.Active, "soft pulses must carry 6 strong ones to promotion");
      train.PulseCnt.Should().Be(60, "every on-grid soft pulse associates");

      // a bare triplet + soft-only confirmations must NOT promote: with the wide RLS gate, in-gate noise
      // could fake that pattern (the measured soft-dominated phantom trains, mean power ≈ 0.16)
      var phantom = Robot36Train(36000, 60);
      for (int i = 3; i < phantom.Count; i++) phantom[i] = P(phantom[i].Time, power: 0.12f);
      extractor = Run(phantom, phantom[^1].Time + 12000);
      extractor.Trains.Where(t => t.State == SstvTrainState.Active || t.State == SstvTrainState.Retired)
        .Should().BeEmpty("a spawn triplet plus only soft evidence must never promote");
    }

    [Fact]
    public void VisSeed_TripletBeforeAnchor_IsNotAdopted()
    {
      // the UmKA-1 hijack (found 2026-07-02): a period-consistent triplet minutes BEFORE the VIS anchor
      // extrapolates to it within the ±18 ms wing by chance (over ~1000 periods the gate is nearly
      // vacuous) — the VIS train must reject it on the anchor-forward span gate, leaving it to spawn a
      // plain candidate
      int anchor = 200 * 48000;
      var extractor = new SstvPulseTrainExtractor(Fs);
      extractor.AddVisTrain(SstvMode.Robot36, anchor);
      var pulses = Robot36Train(anchor - 170 * 48000, 12);   // a burst ~170 s before the anchor, on-grid
      Feed(extractor, pulses, anchor + 12000);

      var vis = extractor.Trains.OfType<SstvVisPulseTrain>().Single();
      output.WriteLine($"vis train pulses: {vis.PulseCnt}");
      vis.PulseCnt.Should().Be(0, "a pre-anchor triplet must not hijack the VIS train");
    }

    [Fact]
    public void ImageGate_RequiresQuarterOfTheLines_KeepsSparseWeakBursts()
    {
      // retro item D (resolved): a full burst qualifies as an image, and so does a SPARSE weak one — the
      // real corpus's low-fill trains turned out to be genuine transmissions (12_37_50 @157 s), so the
      // fill ratio is a quality diagnostic, not a rejection gate. Only a below-¼-lines fragment is dropped.
      var pulses = Robot36Train(36000, 100);
      int end = pulses[^1].Time + 12000;
      var extractor = Run(pulses, end);
      var dense = extractor.Trains.Single(t => t.State != SstvTrainState.Candidate);
      extractor.IsImageTrain(dense).Should().BeTrue("a full burst is an image train");
      extractor.FillRatio(dense).Should().BeGreaterThan(0.9, "a pulse on nearly every claimed line");

      // a weak burst: a dense clump promotes, then a sparse tail (one pulse per 5 line slots) — the RLS
      // grid coasts over the misses; the image is ratty but REAL and must still be emitted
      var sparse = Robot36Train(36000, 12);
      for (int k = 12; k < 77; k += 5) sparse.Add(P(36000 + (int)Math.Round(k * 7200.0)));
      end = sparse[^1].Time + 12000;
      extractor = Run(sparse, end);
      var weak = extractor.Trains.Single(t => t.State != SstvTrainState.Candidate);
      int claimed = extractor.ClaimedLines(weak);
      output.WriteLine($"sparse train: pulses={weak.PulseCnt} claimed={claimed} fill={extractor.FillRatio(weak):0.00}");
      claimed.Should().BeGreaterThanOrEqualTo(60, "the coasting grid still claims the lines");
      extractor.IsImageTrain(weak).Should().BeTrue("a sparse weak burst is still a real image");
      extractor.FillRatio(weak).Should().BeLessThan(0.5, "…and the fill ratio records its low confidence");

      // a fragment claiming under a quarter of the mode's lines is not an image
      var frag = Robot36Train(36000, 15);
      end = frag[^1].Time + 12000;
      extractor = Run(frag, end);
      var tiny = extractor.Trains.Single(t => t.State != SstvTrainState.Candidate);
      output.WriteLine($"fragment: pulses={tiny.PulseCnt} claimed={extractor.ClaimedLines(tiny)}");
      extractor.IsImageTrain(tiny).Should().BeFalse("a below-¼-lines fragment is not an image");
    }

    [Fact]
    public void VisSeed_OffsetGrid_IsNotConfirmed()
    {
      // pulses whose grid does NOT extrapolate to the VIS anchor (offset by half a line) must not be
      // adopted by the VIS train — they spawn a plain candidate instead, and 5 pulses cannot promote it
      var extractor = new SstvPulseTrainExtractor(Fs);
      extractor.AddVisTrain(SstvMode.Robot36, 48000);
      var pulses = Robot36Train(48000 + 3600, 5);
      Feed(extractor, pulses, pulses[^1].Time + 12000);

      extractor.Trains.OfType<SstvVisPulseTrain>().Should().OnlyContain(t => t.PulseCnt == 0,
        "an off-anchor grid must not confirm the VIS hypothesis");
      extractor.Trains.Where(t => t.State == SstvTrainState.Active).Should().BeEmpty();
    }
  }
}
