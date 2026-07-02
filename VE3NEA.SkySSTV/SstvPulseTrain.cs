using System;

namespace VE3NEA.SkySSTV
{
  /// <summary>One detected 1200 Hz sync pulse: absolute sample time, matched-filter power, tone frequency
  /// (Hz), the sync-template duration that detected it (ms — the Robot-vs-PD family discriminant, retro
  /// item C), and a flag marking it already consumed by a train.</summary>
  public struct SstvPulse
  {
    public int Time;
    public float Power;
    public float Freq;
    public float DurMs;
    public bool Used;
    public SstvPulse(int time, float power, float freq, float durMs = 0)
    { Time = time; Power = power; Freq = freq; DurMs = durMs; Used = false; }
  }

  /// <summary>Lifecycle of a pulse-train hypothesis (plan §4.1).</summary>
  public enum SstvTrainState { Candidate, Active, Retired }

  /// <summary>
  /// One MHT hypothesis (plan §4.1/§6.1, ported from Hopper's <c>TPulseTrain</c>): a candidate SSTV sync train
  /// of a specific <see cref="SstvMode"/>, seeded by a period-consistent 3-pulse triplet and tracked by an
  /// <see cref="SstvSyncRegressor"/>. New pulses are <b>associated</b> through <see cref="TryAddPulse"/> — a
  /// frequency gate plus the regressor's growing time-tolerance gate — so clutter outside the gate is ignored,
  /// not fitted. A candidate <b>promotes</b> to active once it has enough pulses within the promote timeout,
  /// and <b>retires</b> after a run of inactivity. The extractor (a later piece) owns the state transitions and
  /// picks the best train per line block.
  /// </summary>
  internal sealed class SstvPulseTrain
  {
    private const double FreqTolHz = 150.0;      // reject pulses off the train's tone frequency
    private const double PromoteSeconds = 4.0;   // promote-or-kill a candidate by this idle time
    private const double RetireSeconds = 6.0;    // retire an active train after this idle time

    private readonly double promoteTimeout;
    private readonly double retireTimeout;
    private readonly int createdTime;            // seed time — bounds the promote window (N-of-M, retro P)
    private int lastPulseNo = int.MinValue;      // rejects a second pulse landing in the same line slot

    public SstvTrainState State { get; set; }
    public SstvMode Format { get; }
    public SstvSyncRegressor Regr { get; }
    public double Freq { get; private set; }
    public int PulseCnt { get; private set; }

    /// <summary>Seed a train from three period-consistent pulses of <paramref name="mode"/>.</summary>
    public SstvPulseTrain(SstvMode mode, SstvPulse p0, SstvPulse p1, SstvPulse p2, double fs)
    {
      Format = mode;
      var spec = SstvModes.Get(mode);
      double nominalPeriod = spec.LinePeriodMs / 1000.0 * fs;
      promoteTimeout = PromoteSeconds * fs;
      retireTimeout = RetireSeconds * fs;

      Freq = (p0.Freq + p1.Freq + p2.Freq) / 3.0;
      Regr = new SstvSyncRegressor(p0.Time, nominalPeriod);
      Regr.ProcessPulse(p0.Time);
      Regr.ProcessPulse(p1.Time);
      Regr.ProcessPulse(p2.Time);
      PulseCnt = 3;
      createdTime = p0.Time;
      lastPulseNo = Regr.GetPulseNo(p2.Time);
      State = SstvTrainState.Candidate;
    }

    /// <summary>Try to associate a pulse with this train: rejected if past the image end, off-frequency,
    /// outside the regressor's time gate, or landing in an already-filled line slot (a duplicate would be
    /// double-fitted and double-counted toward promotion, retro P); otherwise folded in. Returns whether it
    /// was accepted.</summary>
    public bool TryAddPulse(in SstvPulse pulse)
    {
      var spec = SstvModes.Get(Format);
      if (pulse.Time > Regr.GetPulseTime(spec.LineCount + 50)) return false;
      if (Math.Abs(pulse.Freq - Freq) > FreqTolHz) return false;

      int pulseNo = Regr.GetPulseNo(pulse.Time);
      if (pulseNo == lastPulseNo) return false;
      double expected = Regr.GetPulseTime(pulseNo);
      if (Math.Abs(pulse.Time - expected) > Regr.GetMaxError(pulseNo)) return false;

      AddPulse(pulse);
      return true;
    }

    /// <summary>Fold an accepted pulse in: update the smoothed tone frequency and the regressor.</summary>
    public void AddPulse(in SstvPulse pulse)
    {
      Freq = 0.97 * Freq + 0.03 * pulse.Freq;
      lastPulseNo = Regr.GetPulseNo(pulse.Time);
      Regr.ProcessPulse(pulse.Time);
      PulseCnt++;
    }

    /// <summary>A candidate has enough pulses to promote to active (N-of-M over the promote window).</summary>
    public bool HasEnoughPulses => PulseCnt >= Math.Max(6, (int)Math.Round(0.4 * promoteTimeout / Regr.NominalPeriod));

    /// <summary>A candidate is due to be killed: idle too long, or — bounding the N-of-M promote window
    /// (retro P) — older than the promote timeout without having promoted. Without the age bound, clutter
    /// that trickles one associated pulse per few seconds would stay alive and eventually promote.</summary>
    public bool IsCandidateIdle(int time)
      => (time - Regr.LastPulseTime) > promoteTimeout || (time - createdTime) > promoteTimeout;

    /// <summary>An active train has gone idle long enough to retire.</summary>
    public bool CanRetire(int time) => (time - Regr.LastPulseTime) > retireTimeout;
  }
}
