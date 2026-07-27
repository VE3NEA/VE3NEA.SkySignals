using MathNet.Numerics;
using VE3NEA.SkyTlm.Core;

namespace VE3NEA.SkyTlm.Discovery
{
  /// <summary>What a running discovery session has done so far — the progress display of §7 P3. The skipped
  /// count is the interesting one: it is the signal that v.2's burst-capture store would pay off (§8.8).</summary>
  public sealed record DiscoveryProgress(int BurstsAnalyzed, int BurstsSkipped, int HypothesesTried,
    string? CurrentHypothesis);

  /// <summary>
  /// A live discovery session (discover_params_plan.md §4.1, §4.6a): bursts arriving from the live pipeline
  /// are offered to it one at a time, it searches each in the background, and it ends on the first
  /// CRC-valid frame, on end of pass, or when the operator presses the button again.
  ///
  /// <b>v.1 keeps no burst history.</b> Only bursts arriving after the session starts are analyzed; nothing
  /// is retained from before. That is what lets the whole feature ship without a capture store, an eviction
  /// policy or a memory budget — the footprint is exactly one burst.
  ///
  /// <b>Skip, don't queue.</b> A burst arriving while the previous one is still under analysis is dropped,
  /// not queued: queueing would build an unbounded backlog on a busy pass, and every burst is an equally
  /// good sample of the same signal, so the newest is as informative as the one that would have waited.
  /// </summary>
  public sealed class DiscoverySession : IDisposable
  {
    private readonly SignalParams db;
    private readonly Func<IEnumerable<SignalParams>>? coChannel;
    private readonly DiscoveryOptions options;
    private readonly CancellationTokenSource cts = new();
    private readonly object gate = new();

    private int analyzed, skipped, hypotheses;
    private string? currentHypothesis;
    private bool busy, finished;

    /// <summary>Raised once, on the first hypothesis that produces a CRC-valid frame. The session is over by
    /// the time this fires; the caller applies the parameters (§4.5).</summary>
    public event Action<DiscoveryCandidate>? Found;

    /// <summary>Raised when a session ends without a result — a legitimate and useful outcome: it tells the
    /// operator the problem is not the parameters (§4.5).</summary>
    public event Action? Ended;

    /// <summary>Raised after each burst is analyzed and whenever one is skipped.</summary>
    public event Action<DiscoveryProgress>? Progress;

    /// <param name="db">The DB-resolved parameters of the selected transmitter — the starting point.</param>
    /// <param name="coChannel">Supplies the tier-1 co-channel hypotheses at analysis time (resolution lives
    /// in the app). Evaluated per burst so a transmitter change mid-pass is picked up.</param>
    public DiscoverySession(SignalParams db, Func<IEnumerable<SignalParams>>? coChannel = null,
      DiscoveryOptions? options = null)
    {
      this.db = db;
      this.coChannel = coChannel;
      this.options = options ?? new DiscoveryOptions();
    }

    /// <summary>True until a result is found or the session is stopped.</summary>
    public bool IsRunning { get { lock (gate) return !finished; } }

    public DiscoveryProgress Snapshot
    {
      get { lock (gate) return new DiscoveryProgress(analyzed, skipped, hypotheses, currentHypothesis); }
    }



    // ----------------------------------------------------------------------------------------------------
    //                                            session control
    // ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// Offer a burst the live pipeline just reported. This is the whole of the live feed (§7 P2): call it
    /// from the <c>BurstDecoded</c> handler for as long as a session is running and do nothing else — no
    /// store, no eviction, no memory cap. A report without samples (detect-only) is ignored.
    /// </summary>
    public void Offer(StreamingBurstReport report)
    {
      if (report.Segment is { Length: > 0 } seg)
        Offer(seg, report.Burst.SampleRate, report.Burst.SnrDb);
    }

    /// <summary>
    /// Offer one burst's samples to the search. Returns immediately: the analysis runs on a background
    /// thread at lowered priority so it never starves the live decode path (§4.4). A burst offered while
    /// the previous one is still being analyzed is <b>dropped</b> and counted, not queued.
    /// </summary>
    public void Offer(Complex32[] segment, double sampleRate, double snrDb = 20)
    {
      lock (gate)
      {
        if (finished) return;
        if (busy) { skipped++; ReportLocked(); return; }
        busy = true;
      }

      Task.Factory.StartNew(() => Run(segment, sampleRate, snrDb),
        cts.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    /// <summary>End the session: the operator pressed the button again, or the pass ended. Idempotent, and
    /// safe to call from any thread — an analysis already in flight is cancelled and its result discarded.</summary>
    public void Stop()
    {
      bool raise;
      lock (gate)
      {
        raise = !finished;
        finished = true;
      }
      if (!raise) return;
      cts.Cancel();
      Ended?.Invoke();
    }

    public void Dispose()
    {
      Stop();
      cts.Dispose();
    }



    // ----------------------------------------------------------------------------------------------------
    //                                           the analysis thread
    // ----------------------------------------------------------------------------------------------------

    private void Run(Complex32[] segment, double sampleRate, double snrDb)
    {
      // below-normal priority so a long search never competes with the live decode path for CPU.
      var thread = Thread.CurrentThread;
      var priority = thread.Priority;
      try { thread.Priority = ThreadPriority.BelowNormal; } catch { /* not permitted: run at normal */ }

      DiscoveryCandidate? result = null;
      try
      {
        result = BurstDiscovery.Analyze(segment, sampleRate, db, coChannel?.Invoke(), options,
          cts.Token, OnHypothesis, snrDb);
      }
      catch (OperationCanceledException) { return; }
      catch { /* one burst failing to analyze must never end the session */ }
      finally
      {
        try { thread.Priority = priority; } catch { }
        lock (gate)
        {
          analyzed++;
          busy = false;
        }
      }

      lock (gate)
      {
        currentHypothesis = null;
        if (finished) return;
        if (result != null) finished = true;
      }
      Progress?.Invoke(Snapshot);

      if (result == null) return;
      cts.Cancel();
      Found?.Invoke(result);
    }

    private void OnHypothesis(string label)
    {
      lock (gate)
      {
        hypotheses++;
        currentHypothesis = label;
      }
    }

    private void ReportLocked()
    {
      var snapshot = new DiscoveryProgress(analyzed, skipped, hypotheses, currentHypothesis);
      Progress?.Invoke(snapshot);
    }
  }
}
