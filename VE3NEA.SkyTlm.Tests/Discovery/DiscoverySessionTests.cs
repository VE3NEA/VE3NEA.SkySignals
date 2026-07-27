using System;
using System.IO;
using System.Linq;
using System.Threading;
using FluentAssertions;
using MathNet.Numerics;
using VE3NEA.SkyTlm.Core;
using VE3NEA.SkyTlm.Discovery;
using VE3NEA.SkyTlm.IO;
using VE3NEA.SkyTlm.Tests.Regression;
using Xunit;

namespace VE3NEA.SkyTlm.Tests.Discovery
{
  /// <summary>
  /// The live-session contract of `discover_params_plan.md` §4.1 and §4.6a: one burst in flight at a time,
  /// arrivals during an analysis are <b>dropped and counted</b> rather than queued, and the session detaches
  /// cleanly whenever it ends. The skipped count is not bookkeeping for its own sake — it is the evidence
  /// that decides whether v.2's burst-capture store is worth building (§8.8).
  /// </summary>
  public class DiscoverySessionTests
  {
    private const double Fs = 48000;

    /// <summary>Noise, long enough to take a measurable time to search and short enough to keep the test
    /// quick. No hypothesis will decode it, so every offer runs the full set.</summary>
    private static Complex32[] Noise(int seed, double seconds = 1.0)
    {
      var rnd = new Random(seed);
      var x = new Complex32[(int)(seconds * Fs)];
      for (int i = 0; i < x.Length; i++)
        x[i] = new Complex32((float)(rnd.NextDouble() - 0.5), (float)(rnd.NextDouble() - 0.5));
      return x;
    }

    private static SignalParams Db() => new(1200, Modulation.FSK, Framing.AX25G3RUH, Fs);

    [Fact]
    public void BurstsArrivingDuringAnAnalysisAreSkippedNotQueued()
    {
      using var session = new DiscoverySession(Db());

      session.Offer(Noise(1), Fs);
      // the first offer is still in flight; these must be dropped, not stacked up behind it.
      for (int i = 0; i < 5; i++) session.Offer(Noise(2 + i), Fs);

      session.Snapshot.BurstsSkipped.Should().Be(5, "the footprint stays at exactly one burst (§4.1)");
      session.Snapshot.BurstsAnalyzed.Should().Be(0, "the first analysis has not finished yet");
    }

    [Fact]
    public void AnAnalysisThatFindsNothingLeavesTheSessionRunning()
    {
      using var session = new DiscoverySession(Db());
      var done = new ManualResetEventSlim();
      session.Progress += _ => done.Set();

      session.Offer(Noise(11), Fs);
      done.Wait(TimeSpan.FromMinutes(2)).Should().BeTrue("the analysis must complete");

      session.Snapshot.BurstsAnalyzed.Should().Be(1);
      session.Snapshot.HypothesesTried.Should().BeGreaterThan(0, "progress must show the search working");
      session.IsRunning.Should().BeTrue("no result yet, and the pass has not ended (§4.6a)");
    }

    [Fact]
    public void StopEndsTheSessionOnceAndRefusesFurtherBursts()
    {
      using var session = new DiscoverySession(Db());
      int ended = 0;
      session.Ended += () => ended++;

      session.Stop();
      session.Stop();

      ended.Should().Be(1, "Ended is raised once however often the operator presses the button");
      session.IsRunning.Should().BeFalse();

      session.Offer(Noise(21), Fs);
      session.Snapshot.BurstsSkipped.Should().Be(0, "a stopped session ignores bursts, it does not count them");
      session.Snapshot.BurstsAnalyzed.Should().Be(0);
    }

    /// <summary>
    /// The whole live path end to end (§7 P2), on a real burst: the pipeline reports a burst, the report is
    /// offered to the session exactly as <c>BurstDecodedHandler</c> does it, the search finds parameters
    /// that decode, and the session ends itself on that first CRC-valid frame (§4.6a). This is the test
    /// that would catch the feed being wired up wrongly — the unit tests above all use noise, which can
    /// never reach the <see cref="DiscoverySession.Found"/> path.
    /// </summary>
    [Fact]
    [Trait("Category", "Regression")]
    public void AFoundResultEndsTheSessionAndReportsTheParameters()
    {
      string path = Path.Combine(TestPaths.WavDir, "fsk_ax100_NUSHSat1.wav");
      Assert.True(File.Exists(path), "corpus file must be generated (run CorpusBuilder)");

      var (samples, fs) = WavIqReader.Read(path);
      var truth = SignalParamsSidecar.Load(path + ".json") with { SampleRate = fs };
      // the operator's starting point: the right framing family, the wrong everything else.
      var wrong = new SignalParams(4800, Modulation.GMSK, truth.Framing, fs);

      using var session = new DiscoverySession(wrong);
      DiscoveryCandidate? found = null;
      var done = new ManualResetEventSlim();
      session.Found += c => { found = c; done.Set(); };

      // drive the production pipeline over the clip and hand it every report, one at a time, waiting for
      // each analysis so the skip-don't-queue rule does not drop the bursts this test needs.
      using (var sp = new StreamingPipeline(truth, new StreamingOptions()))
      {
        sp.BurstDecoded += r =>
        {
          if (!session.IsRunning) return;
          session.Offer(r);
          SpinWait.SpinUntil(() => !session.IsRunning || session.Snapshot.BurstsAnalyzed > 0
                                   || session.Snapshot.BurstsSkipped > 0, TimeSpan.FromMinutes(1));
        };
        int block = Math.Max(1, (int)(0.1 * fs));
        for (int i = 0; i < samples.Length && session.IsRunning; i += block)
          sp.Push(samples.AsSpan(i, Math.Min(block, samples.Length - i)));
        sp.Flush();
      }

      done.Wait(TimeSpan.FromMinutes(2)).Should().BeTrue("the search must reach a decoding parameter set");
      found!.Params.Baud.Should().BeApproximately(truth.Baud, 0.05 * truth.Baud);
      found.Params.Framing.Should().Be(truth.Framing);
      found.CrcFrames.Should().BeGreaterThan(0);
      session.IsRunning.Should().BeFalse("one CRC-valid frame ends the session (§4.6a)");
    }

    [Fact]
    public void ADetectOnlyReportCarriesNoSamplesAndIsIgnored()
    {
      using var session = new DiscoverySession(Db());
      var burst = new Burst(0, 1000, Fs, 0, 20);
      var report = new StreamingBurstReport(0, 0, 1000, 0, true, burst,
        new SoftSymbols { Soft = Array.Empty<float>(), SymbolRate = 1200 }, null, Array.Empty<Frame>(),
        null!, Array.Empty<double>(), Array.Empty<double>(), 0, 0, 0);

      session.Offer(report);

      session.Snapshot.BurstsAnalyzed.Should().Be(0);
      session.Snapshot.BurstsSkipped.Should().Be(0);
    }
  }
}
