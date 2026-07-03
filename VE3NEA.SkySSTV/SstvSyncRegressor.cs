using System;

namespace VE3NEA.SkySSTV
{
  /// <summary>
  /// Recursive-least-squares estimator of an SSTV sync train's <b>period</b> and <b>phase</b> (plan §4.1/§6.1,
  /// ported from Hopper's <c>TSstvSyncRegressor</c>). The model is linear in the pulse <b>number</b>:
  /// <c>pulseTime ≈ period·pulseNo + phase</c> (times relative to <see cref="FirstPulseTime"/>). Each observed
  /// pulse is folded in with one Kalman/RLS measurement update (state = [period, phase], no process step — the
  /// sample clock is constant), so the estimate converges as pulses accumulate and a straight line is fit
  /// through the (pulseNo, time) points. Regressing on pulse <i>number</i> (not sequential index) makes a
  /// <b>missed pulse free</b> — skip an index, no coasting bookkeeping — and <see cref="CorrFactor"/> =
  /// period / nominal is the slant/clock correction applied directly in reconstruction.
  /// </summary>
  internal sealed class SstvSyncRegressor
  {
    // Hopper's tolerances, expressed in TIME and converted by the sample rate (Hopper ran its sync chain
    // at ~2.756 kHz: σ = 6 samples ≈ 2.2 ms, gate floor 12 samples ≈ 4.4 ms). The first port copied the
    // raw sample counts, making the association gate 17× tighter than the proven design (±0.25 ms vs real
    // low-SNR onset jitter of 1–3 ms) — mid-burst pulses missed the gate, went unclaimed, and seeded
    // duplicate trains with false starts inside an already-tracked transmission (found 2026-07-03 against
    // the user's ground-truth transmission list; the retro-§9 lesson again: port the statistic, not the
    // number).
    private const double ObsSigmaMs = 2.2;   // pulse-onset observation σ
    private const double MinGateMs = 4.4;    // association-gate floor

    private readonly double obsVar;          // observation variance, samples²
    private readonly double minGate;         // gate floor, samples

    // state: predicted relative pulse time = Period·pulseNo + Phase
    private double period;
    private double phase;
    // symmetric 2×2 covariance P = [[p11, p12],[p12, p22]]
    private double p11, p12, p22;

    /// <summary>Reference time; pulse numbers and predictions are relative to it.</summary>
    public int FirstPulseTime { get; set; }

    /// <summary>Latest observed pulse time (absolute samples).</summary>
    public int LastPulseTime { get; set; }

    /// <summary>The mode's tabulated sync period (samples) this train was seeded with.</summary>
    public double NominalPeriod { get; }

    /// <summary>Current period estimate (samples per line).</summary>
    public double Period => period;

    /// <summary>Slant / sample-clock correction = estimated period / nominal period.</summary>
    public double CorrFactor => period / NominalPeriod;

    public SstvSyncRegressor(int approxStartTime, double nominalPeriod, double fs)
    {
      FirstPulseTime = approxStartTime;
      LastPulseTime = approxStartTime;
      NominalPeriod = nominalPeriod;
      obsVar = Math.Pow(ObsSigmaMs / 1000.0 * fs, 2);
      minGate = MinGateMs / 1000.0 * fs;

      period = nominalPeriod;
      phase = 0;
      double sig = 0.01 * nominalPeriod;     // 1 % prior on the period
      p11 = sig * sig; p12 = 0; p22 = 1e10;  // phase almost unknown
    }

    /// <summary>Reset the phase reference to a new start time (keeps the period estimate).</summary>
    public void SetStart(int startTime) { FirstPulseTime = startTime; LastPulseTime = startTime; }

    /// <summary>Fold one observed pulse time into the period/phase estimate (one RLS measurement update).</summary>
    public void ProcessPulse(int pulseTime)
    {
      int pulseNo = GetPulseNo(pulseTime);
      double rel = pulseTime - FirstPulseTime;

      // innovation and P·Hᵀ (H = [pulseNo, 1])
      double innov = rel - (period * pulseNo + phase);
      double ph1 = p11 * pulseNo + p12;      // (P·Hᵀ)₁
      double ph2 = p12 * pulseNo + p22;      // (P·Hᵀ)₂
      double s = pulseNo * ph1 + ph2 + obsVar;   // H·P·Hᵀ + R
      double k1 = ph1 / s, k2 = ph2 / s;         // Kalman gain

      period += k1 * innov;
      phase += k2 * innov;

      // P ← P − k·(H·P);  H·P = [ph1, ph2] (P symmetric)
      p11 -= k1 * ph1;
      p12 -= k1 * ph2;
      p22 -= k2 * ph2;

      if (pulseTime > LastPulseTime) LastPulseTime = pulseTime;
    }

    /// <summary>Nearest pulse number for an absolute time, on the current line fit.</summary>
    public int GetPulseNo(int pulseTime) => (int)Math.Round((pulseTime - FirstPulseTime - phase) / period);

    /// <summary>Predicted absolute time of a given pulse number.</summary>
    public double GetPulseTime(int pulseNo) => FirstPulseTime + period * pulseNo + phase;

    /// <summary>±gate (samples) for accepting a pulse at <paramref name="pulseNo"/>: a few prediction sigmas,
    /// floored so early (uncertain) pulses are not rejected.</summary>
    public double GetMaxError(int pulseNo)
    {
      double var = (double)pulseNo * pulseNo * p11 + 2.0 * pulseNo * p12 + p22 + obsVar;
      return Math.Max(minGate, 3.0 * Math.Sqrt(Math.Max(0, var)));
    }
  }
}
