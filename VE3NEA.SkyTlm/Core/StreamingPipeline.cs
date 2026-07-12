using MathNet.Numerics;
using Serilog;
using VE3NEA;                 // shared DSP helpers (Fft, BlackmanHarrisWindow, Median, NativeFftw)
using VE3NEA.SkyTlm.Dsp;        // DSP stages + LearnedShape (expected-shape template)

namespace VE3NEA.SkyTlm.Core
{
  /// <summary>
  /// One decoded burst, surfaced to the WinForms debug view as it happens (see <see cref="StreamingPipeline.BurstDecoded"/>).
  /// <see cref="Burst"/> spans are <b>absolute</b> stream-sample indices, so the inspector can re-derotate/re-trace
  /// against the original samples exactly as the batch path does. <paramref name="MatchedFraction"/> /
  /// <paramref name="MeanFrameMatch"/> are the per-frame shape statistics behind <paramref name="Validated"/>.
  /// <paramref name="Template"/> is the bank hypothesis the measured spectrum matched best (see
  /// <see cref="CpmTemplate.SynthesizeBank"/>); null on the blind-FSK path, where no shape matching runs.
  /// </summary>
  public sealed record StreamingBurstReport(
    int Index, long StartSample, int Length, double TimeSeconds, bool Validated,
    Burst Burst, SoftSymbols Soft, GmskTrace? Trace, IReadOnlyList<Frame> Frames,
    LearnedShape MeasuredShape, double[] CorrelationLagHz, double[] Correlation,
    double MatchedFraction, double MeanFrameMatch, int ShapedFrames,
    ShapeHypothesis? Template = null, float[]? AveragedSpectrumRaw = null);

  /// <summary>
  /// One STFT frame's detection internals, surfaced to the Detection Inspector when a subscriber attaches to
  /// <see cref="StreamingPipeline.DetectionFrameProcessed"/>. Emitted for <b>every</b> frame past warm-up
  /// (not just signal-bearing ones), so the trace shows what the detector saw between bursts too. Zero-cost
  /// when nobody subscribes (the full spectrum is not even captured). The spectrum is the raw, un-notched,
  /// fftshifted per-bin power (DC at index <c>FftSize/2</c>), so the inspector plots exactly the input the
  /// notch would otherwise alter.
  /// </summary>
  public sealed record DetectionFrame(
    long AbsFrame, double TimeSeconds,
    float[] PowerSpectrum,   // full 2048-bin fftshifted power, NOT DC-notched (DC at index FftSize/2)
    double InbandPower,      // Σ in-band bins (pre-notch)
    double OobMeanRaw,       // this frame's raw OOB per-bin mean
    double NoiseFloorTrim,   // rolling interquartile trimmed-mean floor (npb)
    double ZStat,            // averaged matched statistic, max over shifts
    int BestShiftBins,       // argmax shift → CFO ≈ BestShiftBins*binHz
    bool InBurst);           // Schmitt state after this frame

  /// <summary>Domain the per-frame detection statistics correlate in (<see cref="StreamingOptions.TemplateDomain"/>).
  /// Linear power is the likelihood-ratio-optimal choice under AWGN; the case for the alternatives is robustness:
  /// per-bin power is exponentially distributed (heavy-tailed), so one strong interfering line dominates a linear
  /// sum, while magnitude (≈Rayleigh, variance-stabilized) and log-power (constant-variance ~Gumbel, compresses
  /// strong lines) bound any single bin's influence. Phase 2a experiment axis.</summary>
  public enum TemplateDomain
  {
    /// <summary>Noise-subtracted linear power — the classic matched statistic (pre-option behavior).</summary>
    Power,
    /// <summary>Per-bin magnitude (√power), noise mean subtracted (production default since the Phase 2a
    /// triage: ties LogPower on validation F1 with no CRC-valid-frame regression across modulations).</summary>
    Magnitude,
    /// <summary>Per-bin log power, noise mean subtracted and <b>floored</b>: without the floor the log-domain
    /// noise swings far downward (log of near-zero power → large negative excursions) and those bins dominate
    /// the correlation. See <see cref="StreamingOptions.LogFloorFrac"/>.</summary>
    LogPower
  }

  /// <summary>Tunables for <see cref="StreamingPipeline"/>.</summary>
  public sealed record StreamingOptions
  {
    /// <summary>Worst-case residual carrier offset; the matched detector slides the template over ±this.</summary>
    public double CfoMaxHz { get; init; } = 2000;

    /// <summary>Schmitt onset, in noise-sigma units of the averaged matched statistic (CFAR: each CFO shift
    /// is normalized by its own analytic noise deviation, so the false-alarm rate is independent of the
    /// template width, the baud rate and the noise level). The max-over-shifts of pure noise sits near
    /// 2.5–3σ; 5.5σ leaves a comfortable margin at ~50 STFT frames/s. The analytic model only holds on
    /// white noise: birdies/colored in-band noise inflate the statistic's floor, so the pipeline measures
    /// the no-burst floor and raises the effective threshold to floor + OnSigma·scale when that is higher
    /// (empirical CFAR; this value is the lower bound and the sigma multiplier).</summary>
    public double OnSigma { get; init; } = 5.5;

    /// <summary>Schmitt release, in the same units. Must sit above the noise max-over-shifts level (~3σ),
    /// or hangover keeps re-arming on noise and bursts never close. Gets the same empirical floor raise
    /// as <see cref="OnSigma"/>.</summary>
    public double OffSigma { get; init; } = 4.0;

    /// <summary>Level-relative segmentation: a burst closes when the averaged statistic falls below this
    /// fraction of its peak (0.25 ≈ −6 dB), not just below <see cref="OffSigma"/>; and symmetrically, a jump
    /// to more than 1/this over the burst's running level <b>splits</b> the segment so the stronger signal
    /// starts its own. A telemetry burst embedded in weaker persistent in-band interference (a CW beacon, an
    /// SSTV transmission) then gets a tight segment of its own — decoding it from one long merged window
    /// fails, because the window's CFO estimate and symbol timing are dominated by the interferer.</summary>
    public double ReleaseFraction { get; init; } = 0.25;

    /// <summary>Reject spans shorter than this.</summary>
    public double MinBurstMs { get; init; } = 30;

    /// <summary>Bridge brief fades within a burst.</summary>
    public double HangoverMs { get; init; } = 80;

    /// <summary>Pad each side of a burst so the preamble/tail isn't clipped.</summary>
    public double GuardMs { get; init; } = 20;

    /// <summary>Force-flush a burst that runs longer than this (capped flush). Bounds memory and keeps a
    /// continuous carrier producing frames in windows instead of buffering forever.</summary>
    public double MaxBurstSeconds { get; init; } = 8.0;

    /// <summary>Rolling window the streaming noise floor is estimated over (the online stand-in for the batch
    /// detector's whole-signal statistic). Long enough to be stable, short enough to track a changing floor.</summary>
    public double NoiseWindowSeconds { get; init; } = 3.0;

    /// <summary>Noncoherent temporal integration: the per-shift matched statistic is averaged over this many
    /// consecutive STFT frames (~21 ms each at 48 kHz) <i>before</i> the max-over-shifts and the Schmitt
    /// trigger. √N less statistic variance, so weak bursts clear the onset threshold reliably.</summary>
    public int DetectorAvgFrames { get; init; } = 6;

    /// <summary>Per-frame shape threshold: a signal-bearing STFT frame whose noise-subtracted spectrum has at
    /// least this Pearson correlation with the template (at the frame's best CFO shift) counts as "shaped like
    /// the expected modulation".</summary>
    public double PerFrameMatchMin { get; init; } = 0.35;

    /// <summary>Primary burst validation: at least this fraction of the burst's signal-bearing frames must be
    /// shaped (<see cref="PerFrameMatchMin"/>). Per-frame shape is the digital-vs-analog discriminator: SSTV's
    /// scanning FM line and a CW spike look nothing like the template <i>instantaneously</i>, even though their
    /// burst-averaged spectra can mimic it (measured: real GMSK ≥0.39 vs SSTV ≤0.02 at the 0.5 threshold).</summary>
    public double MinMatchedFraction { get; init; } = 0.25;

    /// <summary>The fraction gate also needs at least this many shaped frames in absolute terms — a 2-frame
    /// noise or impulse blip trivially scores frac 1/1, while any real telemetry burst (≥ ~0.3 s) has dozens
    /// of signal-bearing frames. Lowered 5→4 (2026-07-10, FN-minimization pass): 4 of the corpus FNs
    /// failed only this bar at sf=3–4 with solid matched fractions (one at mf=1.00); at 4 the corpus gains
    /// +0.01 val recall AND +1 CRC-valid frame (296→297) for −0.03 precision, and 4 dominates 3 (same
    /// recall/crc, better precision).</summary>
    public int MinShapedFrames { get; init; } = 4;

    /// <summary>Eye-based validation for FSK-family bursts: a burst demodulated on the FM-discriminator path
    /// (GMSK/GFSK/FSK) whose recovered eye is at least this clean (<see cref="SoftSymbols.EyeSnrDb"/>) and that
    /// carries at least <see cref="MinEyeSymbols"/> symbols is accepted even when its spectral shape matches no
    /// template. A clean two-level FSK eye is stronger proof of a real FSK burst than the PSD shape, and it
    /// rescues carrier-dominated FSK (e.g. CUBEBUG-2: ~93% of the power is an unmodulated carrier line, so the
    /// spectrum matches no modulation template although the data demodulates to an 11 dB eye). SSTV's scanning
    /// tone, a CW spike and noise do not resolve into two clean symbol rails, so this does not weaken the
    /// analog-interloper rejection the shape gate provides. 8 dB ≈ rails separated by 2.5σ — a decisively open
    /// eye, comfortably above the ≤5 dB a demodulated non-FSK segment reaches.</summary>
    public double MinEyeSnrDb { get; init; } = 8.0;

    /// <summary>Minimum symbol count for the <see cref="MinEyeSnrDb"/> eye path — a short blip can score a high
    /// eye from a handful of symbols by chance, while a real telemetry burst carries hundreds.</summary>
    public int MinEyeSymbols { get; init; } = 128;

    /// <summary>Secondary (high-confidence) validation: a burst whose burst-averaged spectrum correlates with
    /// a hypothesis of the shape bank (<see cref="CpmTemplate.SynthesizeBank"/>) at least this strongly
    /// (<see cref="CpmTemplate.Match"/>, magnitude domain per <see cref="MagnitudeShapeScore"/>) is accepted
    /// even if the per-frame fraction falls short — averaging over the burst recovers the shape of a very weak
    /// signal whose individual frames are too noisy to score. <c>null</c> (default) applies the flat 0.72 bar
    /// adopted 2026-07-10 for the magnitude scores (corpus: val recall 0.94→0.97+ at crc held 296; SSTV's
    /// measured magnitude ceiling on the UmKA-1 truth spans is 0.72). The historical dB-score bars were
    /// per-hypothesis (0.85 bell / 0.55 two-tone) — restore those if <see cref="MagnitudeShapeScore"/> is
    /// ever turned back off.</summary>
    public double? MinShapeScore { get; init; }

    /// <summary>The <see cref="MinShapeScore"/> actually applied for <paramref name="m"/>.</summary>
    public double EffectiveMinShapeScore(Modulation m) => MinShapeScore ?? 0.72;

    /// <summary>The <see cref="MinShapeScore"/> actually applied to bank hypothesis <paramref name="h"/>.</summary>
    public double EffectiveMinShapeScore(ShapeHypothesis h) => MinShapeScore ?? 0.72;

    /// <summary>When <c>false</c>, surface <i>every</i> detected burst (flagged validated/rejected) — for
    /// visual inspection of bursts the gate would normally suppress. Default <c>true</c>: only validated
    /// bursts are reported and only their frames returned. A CRC-valid frame always validates its burst
    /// (proof of digital signal beats any shape score).</summary>
    public bool GateByShape { get; init; } = true;

    /// <summary>When <c>true</c>, demodulate even segments whose detector statistics already preclude every
    /// validation path (noise/impulse blips), so they can be surfaced for inspection. Default <c>false</c>:
    /// such segments skip the (expensive) demodulator — the only gate they could still pass is a CRC frame,
    /// and segments this far below every shape bar have never produced one. The UI sets this together with
    /// <see cref="GateByShape"/> = false.</summary>
    public bool DecodeRejected { get; init; } = false;

    /// <summary>Demodulator tunables. Defaults to <see cref="Demodulators.DefaultGmskOptions"/> so a burst
    /// decodes identically whether it arrives via a file or the live stream.</summary>
    public GmskDemodOptions GmskOptions { get; init; } = Demodulators.DefaultGmskOptions;

    /// <summary>DC/LO-leakage notch: zero the centre 3 bins of every frame's noise-subtracted spectrum and of
    /// the burst-averaged spectrum. Default <c>false</c>: the notch's original purpose (LO feedthrough / ADC
    /// DC offset) is moot on the SkyRoof slicer's spur- and imbalance-free output, where instead it removes
    /// real near-DC signal energy — worst for bell-shaped h≈0.5 GMSK/MSK whose carrier energy concentrates
    /// in exactly the 3 zeroed bins, lowering the matched z-statistic. The corpus A/B confirmed the flip.
    /// The Detection Inspector still A/Bs this toggle.</summary>
    public bool NotchDc { get; init; } = false;

    /// <summary>Domain the per-frame matched statistic (<c>MatchedSlide</c>) and the per-frame shape Pearson
    /// (<c>MatchAtShift</c>) operate in — see <see cref="Core.TemplateDomain"/>. The CFAR normalization tracks
    /// the domain (each has its own analytic per-bin noise σ), so <see cref="OnSigma"/>/<see cref="OffSigma"/>
    /// keep their meaning; the reported burst SNR stays in linear power regardless of the domain.</summary>
    public TemplateDomain TemplateDomain { get; init; } = TemplateDomain.Magnitude;

    /// <summary>Log-power floor for <see cref="Core.TemplateDomain.LogPower"/>, as a fraction of the per-bin
    /// noise floor (0.05 ≈ −13 dB below it): bins below it clamp, so the log-domain noise's wide downward
    /// swings (log of near-zero power → large negative excursions) cannot dominate the correlation. The
    /// calibrated noise moments account for the censoring. Phase 2a experiment axis.</summary>
    public double LogFloorFrac { get; init; } = 0.05;

    /// <summary>When <c>true</c>, the burst-average shape match (<see cref="CpmTemplate.Match"/>) floors both
    /// the template and the measured spectrum at the burst's measurable dynamic range (≈ −SNR − 6 dB) instead
    /// of the fixed −40 dB: template nulls deeper than the noise floor are unreachable in the data, and
    /// correlating against them systematically punishes low-SNR bursts (Phase 2a triage, KuzGTU-1 —
    /// visually-good matches scoring ~0.6). Default <c>false</c>: pre-experiment behavior.</summary>
    public bool SnrMatchedTemplateFloor { get; init; } = false;

    /// <summary>When <c>true</c>, the burst-average shape match (<see cref="CpmTemplate.Match"/>) correlates
    /// the magnitude (√power) spectra instead of dB. The dB Pearson structurally under-scores line-dominated
    /// spectra (AFSK-over-FM: 9 of 161 window bins are template support, so the score is mostly valley noise
    /// plus the line-width mismatch next to the razor-thin carrier — visually perfect fits score 0.6–0.8),
    /// while in magnitude the same bursts score 0.90+ AND the GMSK-vs-SSTV separation improves (targets
    /// 0.81–0.98 vs SSTV ≤ 0.72 on the UmKA-1 truth spans, where the dB scores overlap outright).
    /// Default <c>true</c> — adopted 2026-07-10 together with the flat 0.72 <see cref="MinShapeScore"/> bar
    /// (corpus: val recall 0.94→0.97+, FN gated-out 20→~10, crc pinned at 296).</summary>
    public bool MagnitudeShapeScore { get; init; } = true;

    /// <summary>Widens the per-frame matched statistic's (<see cref="StreamingPipeline.MatchedSlide"/>/
    /// <see cref="StreamingPipeline.MatchAtShift"/>) support window to this factor times the template's ≥5%
    /// support half-width, with the template forced to 0 in the added skirt. The un-widened statistic only
    /// ever reads bins under the template's support, so a signal <i>wider</i> than the template (SSTV's
    /// ~3 kHz FM spread, a broadband interferer) fills that support just as well as the right-width signal —
    /// mean removal cannot penalize energy in bins it never reads. With the skirt included, those bins carry
    /// uniform negative weight after mean removal (template − mean &lt; 0 where template = 0), so a wider
    /// signal now anti-correlates. Default <c>1.0</c> (no widening — pre-Phase-2b behavior); values &gt;1
    /// reproduce the original support bins exactly and only add zero-template bins outside their outer edge,
    /// never touching any internal gap between separated support lobes (e.g. two-tone FSK). Phase 2b
    /// experiment axis.</summary>
    public double SkirtWidthFactor { get; init; } = 1.0;

    /// <summary>When <c>true</c> (default), a validated FSK/GFSK/GMSK burst that decodes ZERO frames on the
    /// curated label triggers a blind-hypothesis trial at the labeled baud, at 2×, at 4× and at ½ it
    /// (Phase U: the SNIPE-D / ERMIS / KSM1 / BRO-8 / MIMAN cluster — 617 CRC frames recovered by
    /// distrusting the curated GFSK labels; MIMAN's label is wrong in baud as well as shape; CubeSX-HSE-3's
    /// GMSK 4k8 label is really ~2k4; Luca's 2k4 label hides a ~9k6 signal — the 4× arm). The first
    /// CRC-valid trial frame at the labeled baud or above locks
    /// the discovered params for the session; a ½-baud CRC only decodes its own burst (it can come from a
    /// co-channel second transmitter — BRO8_BRO22); a CRC frame on the curated path instead proves the label
    /// and stops the trials.</summary>
    public bool BlindFallback { get; init; } = true;

    /// <summary>When <c>true</c> (default), a validated (or rejected-but-strong) GMSK/GFSK burst with no
    /// CRC-valid frame is decoded once more with the plain FM-discriminator detector (Phase U class (d):
    /// at marginal SNR the coherent MLSE/DF-DD detectors and the discriminator are complementary — each
    /// recovers bursts the other loses, e.g. QMR-KWT 2 @331 s and Luca-9k6 @110 s decode only from the
    /// discriminator's soft bits while Luca-9k6 @20 s decodes only from MLSE's). CRC-gated adopt-only,
    /// per burst — no session lock, the next burst may favor the coherent detector again.</summary>
    public bool DiscriminatorRetry { get; init; } = true;

    /// <summary>When <c>true</c> (default), every validated (or rejected-but-strong) CPM/FSK-family burst is
    /// decoded once more with the <b>other</b> symbol-timing recovery (Phase 7 feed-forward port: the
    /// whole-burst feed-forward estimate vs the Gardner loop, <see cref="Dsp.GmskDemodOptions.Timing"/>).
    /// The two timings are complementary per burst — the block estimate has no acquisition transient and
    /// nails the clock <i>rate</i> (short/marginal bursts: NIGHTJAR 29→33 crc, UmKA-1 5→7 on the timing
    /// A/B), but its single linear clock model breaks on long multi-frame bursts where the TX clock
    /// wanders within the burst (UND ROADS 2: the 8 s / 39k-symbol burst drops 47→42 frames under
    /// feed-forward while Gardner tracks the wander). CRC-gated competition, adopt-only on strictly more
    /// CRC-valid frames — per burst, no session state.</summary>
    public bool TimingRetry { get; init; } = true;

    /// <summary>Second blind-fallback trial trigger (regime 2 — cold-start wrong mode/baud): a burst that
    /// FAILED validation but whose matched SNR is at least this many dB still reaches the blind trials.
    /// Detection tolerates a wrong curated template (broad analytic shape, CFAR statistic) but validation is
    /// much tighter, so a strongly mismatched real signal is detected, rejected and never trialed — Luca-2k4
    /// 07-06: 9k6 on air vs the 2k4 label, 5 bursts at 22–26 dB SNR all rejected, 0 frames. A CRC-valid
    /// trial frame also flips the burst to validated (CRC is absolute proof; operator priority = minimize
    /// FNs). Gated by <see cref="RejectedTrialMaxSeconds"/> so noise storms (KNACKSAT-2: 1,580 rejected
    /// bursts) and SSTV scans don't pay for 3-baud trial decodes.</summary>
    public double RejectedTrialMinSnrDb { get; init; } = 15.0;

    /// <summary>Only rejected bursts at most this long qualify for the <see cref="RejectedTrialMinSnrDb"/>
    /// trigger: telemetry bursts run ~0.1–3 s, while a long strong reject is almost certainly SSTV/CW —
    /// exactly what validation rejected it for, and the worst-case CPU spend to trial-decode at 3 bauds.
    /// A continuous scan's capped-flush windows (<see cref="MaxBurstSeconds"/>, 8 s) stay above this bar.</summary>
    public double RejectedTrialMaxSeconds { get; init; } = 4.0;

    /// <summary>When <c>true</c>, a closed burst is reported with its detection-derived spectral stats
    /// (CFO, SNR, shape match) but is <b>never</b> demodulated or deframed — no <see cref="IDemodulator"/>,
    /// no blind-FSK estimation, no CRC. For the Detection Inspector, which only needs the detector's own
    /// internals and must not pay for (or be skewed by) a full decode of every recording it loads. Default
    /// <c>false</c>: the production/decode path.</summary>
    public bool DetectOnly { get; init; } = false;
  }

  /// <summary>
  /// Online (streaming) pipeline — the production decode path. The caller pushes consecutive blocks of
  /// IQ samples (any size) via <see cref="Push(System.ReadOnlySpan{Complex32})"/> and receives the
  /// CRC-checked <see cref="Frame"/>s that completed within each block; <see cref="Flush"/> drains a burst still
  /// in progress at end-of-stream. Decoding is strictly per-burst: nothing is demodulated between bursts.
  ///
  /// <para><b>Detection</b> is a matched per-frame statistic on a coarse STFT. Each frame's noise-subtracted
  /// in-band spectrum is correlated with the expected modulation shape (the broad analytic
  /// <see cref="ModulationTemplate"/> — Gaussian lobes at ±dev, or the GMSK bell) slid over the ±CfoMaxHz
  /// search span. Each shift is normalized by its analytic noise deviation (CFAR), averaged per shift over
  /// <see cref="StreamingOptions.DetectorAvgFrames"/> frames, then maxed over shifts; a Schmitt trigger in
  /// noise-sigma units with hangover and min-length segments bursts. Integrating over the signal band only
  /// (instead of the whole search band) plus the temporal averaging buys several dB of sensitivity over a
  /// whole-band energy sum at the same false-alarm rate — that is what catches weak bursts.</para>
  ///
  /// <para><b>Validation</b> separates digital bursts from analog interlopers <i>per frame</i>: SSTV is a
  /// narrow wandering FM line and CW a bare spike in any single frame, so their spectra correlate poorly with
  /// the template even when their burst-averaged spectra look template-like. A burst is validated when enough
  /// of its signal-bearing frames are shaped (<see cref="StreamingOptions.MinMatchedFraction"/>), when its
  /// averaged spectrum matches with high confidence (<see cref="StreamingOptions.MinShapeScore"/>, the
  /// weak-burst fallback), or when it yields a CRC-valid frame — every detected burst is demodulated, so an
  /// embedded telemetry frame inside e.g. an SSTV span is never lost to the gate.</para>
  ///
  /// <para>Each burst's samples (plus guard) accumulate in a sliding buffer and are decoded whole when the
  /// burst ends or hits the <see cref="StreamingOptions.MaxBurstSeconds"/> cap, via the existing
  /// CFO → demod → deframe path. Signal parameters (baud/modulation/framing/deviation) are fixed at
  /// construction — online there is no .iq.wav sidecar or SatNOGS lookup; the caller supplies the known
  /// transmitter's <see cref="SignalParams"/>.</para>
  ///
  /// <para>Not thread-safe: drive one instance from a single producer.</para>
  /// </summary>
  public sealed class StreamingPipeline : IDisposable
  {
    private readonly SignalParams p;
    // the caller's original params object (before any internal `with` copy): the locked blind-FSK deviation is
    // written back here so the caller can display the deviation actually used instead of the initial one.
    private readonly SignalParams resolvedTarget;
    private readonly StreamingOptions o;
    private readonly double fs;

    private const int Fft = StftPsd.FftSize;   // 2048
    private const int Hop = Fft / 2;           // 50% overlap, matching BurstDetector

    /// <summary>Averaged-spectrum match below which a junk-classified segment is not even worth
    /// demodulating for the CRC-rescue path (see the hopeless-segment skip in <see cref="DecodeBurst"/>).</summary>
    private const double CrcRescueMinMatch = 0.3;

    /// <summary>NRZ-measured residual carrier error (fraction of the deviation) above which a frameless
    /// decode is retried once at the corrected carrier (the decodeWith refinement in <see cref="DecodeBurst"/>).</summary>
    private const double NrzRefineMinDevFrac = 0.05;

    /// <summary>Equivalent noise bandwidth of the Blackman-Harris window in bins: neighbouring periodogram
    /// bins are correlated, inflating the variance of any sum over bins by this factor (σ by its √).</summary>
    private const double WindowEnbw = 2.0;

    // analytic per-bin noise moments of the transformed excess under exponential (χ²₂) normalized bin power
    // x = q/npb ~ Exp(1): magnitude √x is Rayleigh — mean Γ(3/2) = √π/2, σ = √(1 − π/4); the floored log
    // ln(max(x, LogFloorFrac)) has no elementary closed form, so its moments are integrated numerically once
    // per pipeline (the floor is an option now — see StreamingOptions.LogFloorFrac).
    private static readonly double MagNoiseMean = Math.Sqrt(Math.PI) / 2;
    private static readonly double MagNoiseSigma = Math.Sqrt(1 - Math.PI / 4);
    private readonly (double Mean, double Sigma) logNoise;

    /// <summary>Mean and σ of g(X), X ~ Exp(1) — numeric midpoint integration over the density e^(−x). The
    /// transforms used here are bounded near 0 (the log is floored), so the integrand has no singularity.</summary>
    private static (double Mean, double Sigma) ExpNoiseMoments(Func<double, double> g)
    {
      const double dx = 1e-3, xMax = 40;
      double m1 = 0, m2 = 0;
      for (double x = dx / 2; x < xMax; x += dx)
      {
        double w = Math.Exp(-x) * dx;
        double v = g(x);
        m1 += v * w; m2 += v * v * w;
      }
      return (m1, Math.Sqrt(Math.Max(0, m2 - m1 * m1)));
    }

    // --- detector (per-frame STFT power + matched statistic) ---
    private readonly Fft<Complex32> fft;
    private readonly float[] window;
    private readonly float[] q;               // in-band bin powers for the current frame
    private readonly float[] excess;          // q minus the rolling per-bin noise floor, DC-notched
    private readonly double binHz, occHalfHz;
    private readonly int occBins;
    private readonly float[] oobRing;         // recent out-of-band frame powers (rolling noise floor)
    private readonly float[] oobScratch;
    private int oobCount, oobHead;
    private readonly float[] zFloorRing;      // recent no-burst zStat values (empirical floor of the max statistic)
    private readonly float[] zFloorScratch;
    private int zFloorCount, zFloorHead;
    private readonly float[] zPending = new float[12];   // quarantine: a no-burst zStat enters the floor ring only
    private int zPendingCount, zPendingHead;             // after 12 more no-burst frames — a burst trigger discards
                                                         // the pending samples, so onset ramps (the trigger back-dates
                                                         // up to the averaging delay) never contaminate the floor
    private double zOnThresh, zOffThresh;     // effective Schmitt thresholds: OnSigma/OffSigma raised by the measured floor
    private readonly int warmupFrames;
    private readonly int minFrames, hangFrames, guard, keepFrames, maxBurstSamples;

    // matched template on the detector grid: the ≥5% support (t ≥ 5% of peak) plus, when
    // StreamingOptions.SkirtWidthFactor > 1, a zero-template skirt widening the window beyond it (Phase 2b).
    // Per shift s the statistic is
    // z(s) = Σ excess·(t − t̄) / (eSigma·√(Σ(t−t̄)²)·√ENBW) — DC-removed (so a flat colored-noise pedestal cancels)
    // and normalized to unit noise variance at every shift and any template width; eSigma is the per-bin noise σ
    // of excess in the configured TemplateDomain (= npb in the linear-power domain). The skirt bins (t = 0)
    // carry uniform negative weight after mean removal, so energy there (a signal wider than the template)
    // pulls z down instead of being invisible to it.
    private readonly int[] supIdx;            // support + skirt bin indices (template centred at occBins)
    private readonly float[] supT;            // template value at each bin (0 in the skirt)
    private readonly int cfoBins;             // CFO search span in bins (shifts −cfoBins..+cfoBins)
    private readonly int nShifts;
    private readonly double[] shiftTSum;      // σt over the in-band part of the support, per shift (0 = skip)
    private readonly double[] shiftT2Sqrt;    // √(Σt²) over the same, per shift
    private readonly int[] matchN;            // matchAtShift per-shift template stats (depend only on the shift):
    private readonly double[] matchTSum;      //   in-range support count, Σt, and Σ(t−mean)²
    private readonly double[] matchTVar;
    private readonly double[][] zRing;        // last DetectorAvgFrames per-shift z rows
    private readonly double[] zSum;           // per-shift running sum over the ring
    private readonly double[] frameBestZ;     // per-frame best z of the ring frames (for onset back-dating)
    private int zHead, zCount;

    // --- demod / deframe (built once for the fixed params) ---
    private readonly IDemodulator? demod;
    // linear-PSK only (non-null ⇔ this is BPSK): one demodulator per sub-mode. Which one runs is fixed by
    // signalParams.Differential (resolved from the SatNOGS precoding; coherent unless stated differential).
    private readonly BpskDemodulator? bpskCoherent;
    private readonly BpskDemodulator? bpskDifferential;
    private readonly IDeframer? deframer;
    private readonly IReadOnlyList<ShapeHypothesis> templateBank;   // expected-shape hypotheses for the averaged-spectrum match (UI + secondary gate)
    private readonly CfoEstimator cfo;        // CFO + shape from the (already-computed) averaged STFT power

    // --- per-burst averaged power spectrum (the SAME STFT frames the detector runs on, summed over the
    //     burst) — reused for CFO + shape validation so we don't recompute a separate FFT pass ---
    private readonly double[] burstPsdSum;
    private int burstPsdCount;
    private double noisePerBin;               // current rolling per-bin noise floor (for noise subtraction)

    // --- sliding sample buffer ---
    private Complex32[] buf = new Complex32[Fft * 4];
    private int len;                 // valid samples in buf
    private long bufBaseAbs;         // absolute stream index of buf[0] (multiple of Hop)
    private int nextFrameOffset;     // offset in buf of next frame to process (multiple of Hop)

    // --- Schmitt-trigger burst state ---
    private bool inBurst;
    private long startFrameAbs, lastAboveAbs;
    private double burstPeakZ;       // peak averaged statistic within the burst (for peak-relative release)
    private double burstLevelZ;      // slow EMA of the averaged statistic within the burst (for step-up split)

    // --- decode-overlap state (frames straddling public segment boundaries) ---
    private readonly int overlapSamples;
    private readonly int tailSamples;                  // post-burst decode extension: one max frame on air
    private long lastSegStartAbs = long.MinValue / 4;  // detected span of the previously closed segment
    private long lastSegEndAbs = long.MinValue / 4;
    private bool lastSegDigital;                       // previous segment passed the per-frame shape gate
    private readonly Queue<PendingDecode> pending = new();  // closed bursts awaiting their tail samples
    private readonly List<RecentFrame> recentFrames = new();  // emitted frames, for overlap dedup
    private double burstWSum;        // Σ best-shift matched power over the burst's signal-bearing frames
    private int burstSigFrames;      // frames whose own matched z cleared the release threshold
    private int burstShapedFrames;   // …of which the per-frame Pearson matched the template
    private double burstMatchSum;    // Σ per-frame Pearson over the signal-bearing frames
    private int burstCounter;
    private long framesSeen;

    // --- blind-FSK session cache (non-null from the first CRC-valid blind decode onward) ---
    private double? learnedDeviationHz;
    private IDemodulator? learnedDemod;

    // --- blind-fallback session state (Phase U: a curated FSK-family label whose validated bursts decode
    //     nothing is distrusted; locked from the first CRC-valid trial frame — see the trial block in
    //     DecodeBurst). fallbackCfo points into trialCfoCache, which owns/disposes the estimators. ---
    // set once a CRC-valid frame proves the active BPSK submode (either the resolved one or a trial win);
    // stops the per-burst coherent-vs-differential trial. The discovered submode itself is written into the
    // caller's mutable SignalParams.Differential, which the per-burst demod pick reads.
    private bool bpskSubmodeProven;

    private SignalParams? fallbackParams;
    private IDemodulator? fallbackDemod;
    private CfoEstimator? fallbackCfo;
    private double? fallbackDevHz;
    private bool curatedCrcSeen;
    private readonly Dictionary<double, CfoEstimator> trialCfoCache = new();  // per-trial-baud wide-window estimators

    // placeholder LearnedShape for blind bursts: ValidHalfBaud = 0 causes CpmTemplate.Match/Correlation to
    // short-circuit to 0 / empty without crashing; the blind path never runs the shape gate.
    private static readonly LearnedShape BlindDummyShape = new LearnedShape
    {
      DeviationHz = 0, BandwidthHz = 0, ValidHalfBaud = 0,
      Profile = new float[LearnedShape.GridPoints], Count = 0
    };

    // --- live inspection surface (consumed by the WinForms debug view; see publicsVisibleTo) ---
    /// <summary>The most recently decoded burst's spectral metadata (span is segment-local, 0-based).</summary>
    public Burst? LastBurst { get; private set; }
    /// <summary>Absolute stream time (s) of the most recently decoded burst's start.</summary>
    public double LastBurstTimeSeconds { get; private set; }
    /// <summary>Soft symbols of the most recently decoded burst.</summary>
    public SoftSymbols? LastSoftSymbols { get; private set; }
    /// <summary>Demod trace (matched-filter waveform + strobes) of the last burst, for the eye view; null for non-CPM demods.</summary>
    public GmskTrace? LastTrace { get; private set; }
    /// <summary>Whether a burst is currently being accumulated.</summary>
    public bool InBurst => inBurst;
    /// <summary>Current rolling per-bin noise-floor power (linear).</summary>
    public double NoiseRefPower { get; private set; }
    /// <summary>Total samples consumed from the stream so far.</summary>
    public long SamplesProcessed => bufBaseAbs + nextFrameOffset;
    /// <summary>Frames emitted by the most recent <see cref="Push(System.ReadOnlySpan{Complex32})"/> call.</summary>
    public IReadOnlyList<Frame> LastPushFrames { get; private set; } = Array.Empty<Frame>();

    public event Action<Frame>? FrameDecoded;
    /// <summary>Raised once per decoded burst (after demod, whether or not it deframed), for the live inspector.</summary>
    public event Action<StreamingBurstReport>? BurstDecoded;
    /// <summary>Raised once per STFT frame past warm-up (the Detection Inspector's per-frame trace). Attaching a
    /// handler turns on full-spectrum capture; leaving it null keeps the production hot loop allocation-free.</summary>
    public event Action<DetectionFrame>? DetectionFrameProcessed;

    // --- detector band geometry (read-only, for the Detection Inspector's axes/threshold lines) ---
    /// <summary>Half-width (Hz) of the in-band region the detector integrates over (the ±occHalfHz boundary).</summary>
    public double OccHalfHz => occHalfHz;
    /// <summary>Hz per STFT bin.</summary>
    public double BinHz => binHz;
    /// <summary>Detector FFT size (bins).</summary>
    public int FftSize => Fft;
    /// <summary>Detector STFT hop (samples between frames).</summary>
    public int HopSize => Hop;
    /// <summary>Stream sample rate (Hz).</summary>
    public double SampleRate => fs;
    /// <summary>Effective Schmitt onset threshold: <see cref="StreamingOptions.OnSigma"/> raised by the
    /// measured no-burst floor of the statistic (see <see cref="UpdateZThresholds"/>).</summary>
    public double OnSigma => zOnThresh;
    /// <summary>Effective Schmitt release threshold (same empirical raise as <see cref="OnSigma"/>).</summary>
    public double OffSigma => zOffThresh;

    // full un-notched fftshifted power spectrum of the current frame, captured only while a
    // DetectionFrameProcessed subscriber is attached; null (and never allocated) in production.
    private float[]? fullSpecScratch;

    public StreamingPipeline(SignalParams p, StreamingOptions? options = null)
    {
      if (p == null) throw new ArgumentNullException(nameof(p));
      resolvedTarget = p;   // hold the caller's object; run-time resolutions are written back here for display
      // wide-h GFSK/GMSK is really unfiltered 2-FSK; normalize the label up front (see Demodulators.IsWideFsk)
      // so the detector template, the shape gate and the demod all treat it as FSK. This is the pipeline-side
      // home of the GFSK→FSK reclassification that used to live in the param resolver (now shared with SkyRoof).
      if (Demodulators.IsWideFsk(p)) p = p with { Modulation = Modulation.FSK };
      this.p = p;
      o = options ?? new StreamingOptions();
      logNoise = ExpNoiseMoments(x => Math.Log(Math.Max(x, o.LogFloorFrac)));
      fs = p.SampleRate;
      if (fs <= 0) throw new ArgumentException("SignalParams.SampleRate must be positive for streaming.", nameof(p));

      binHz = fs / Fft;
      occHalfHz = StftPsd.OccupiedHalfHz(p, o.CfoMaxHz);
      occBins = (int)Math.Ceiling(occHalfHz / binHz);
      window = global::VE3NEA.Dsp.BlackmanHarrisWindow(Fft);
      q = new float[2 * occBins + 1];
      excess = new float[q.Length];
      fft = new Fft<Complex32>(Fft, NativeFftw.FftwFlags.Estimate);

      double frameMs = Hop / fs * 1000.0;
      minFrames = Math.Max(1, (int)Math.Round(o.MinBurstMs / frameMs));
      hangFrames = Math.Max(1, (int)Math.Round(o.HangoverMs / frameMs));
      guard = (int)Math.Round(o.GuardMs / 1000.0 * fs);
      maxBurstSamples = Math.Max(Fft, (int)Math.Round(o.MaxBurstSeconds * fs));

      int noiseFrames = Math.Max(8, (int)Math.Round(o.NoiseWindowSeconds * 1000.0 / frameMs));
      oobRing = new float[noiseFrames];
      oobScratch = new float[noiseFrames];
      warmupFrames = Math.Min(noiseFrames, 16);   // collect a little noise history before triggering
      // the z-floor window is much longer than the noise window: the floor raise keys on the MEDIAN of the
      // no-burst statistic, and over a short window a busy pass's signal activity drags the median (and the
      // thresholds) up transiently, costing true weak bursts; over a minute the median only reflects a
      // genuinely persistent (colored/birdie) floor.
      zFloorRing = new float[20 * noiseFrames];
      zFloorScratch = new float[20 * noiseFrames];
      zOnThresh = o.OnSigma;
      zOffThresh = o.OffSigma;

      demod = Demodulators.Create(p, o.GmskOptions);
      if (p.Modulation == Modulation.BPSK)
      {
        bpskCoherent = new BpskDemodulator(new BpskDemodOptions { Differential = false, Manchester = p.Manchester == true });
        bpskDifferential = new BpskDemodulator(new BpskDemodOptions { Differential = true, Manchester = p.Manchester == true });
      }
      deframer = Deframing.DeframerFactory.Create(p);
      templateBank = CpmTemplate.SynthesizeBank(p);   // cached; the modeled spectra for the averaged-spectrum match
      // estimator on the SAME FFT grid as the detector (Fft), so the averaged detection power spectrum feeds
      // its CFO/shape methods directly — no second FFT pass.
      cfo = new CfoEstimator(fs, o.CfoMaxHz, p, fftSize: Fft);
      burstPsdSum = new double[2 * occBins + 1];

      // matched template sampled onto the detector grid; only its support (≥5% of peak) enters the per-shift
      // statistic, so noise outside the signal band contributes nothing (the sensitivity win over a
      // whole-band energy sum). This is the broad ANALYTIC shape (Gaussian lobes at ±dev / the bell), not the
      // synthesized CPM spectrum: at integer h the synthesized FSK spectrum is almost line-spectral, and a
      // few-bin support makes the per-shift statistic too noisy — while the real per-frame tones are
      // broadened well past it anyway (Doppler rate, oscillator drift, the window's own resolution).
      int L = q.Length;
      var t = new float[L];
      float tmax = 0;
      for (int j = 0; j < L; j++)
      {
        t[j] = (float)ModulationTemplate.ShapeValue((j - occBins) * binHz, p);
        if (t[j] > tmax) tmax = t[j];
      }
      // ≥5% support half-width (bins from centre), for the skirt widening below.
      int halfWidth = 0;
      for (int j = 0; j < L; j++) if (t[j] >= 0.05f * tmax) halfWidth = Math.Max(halfWidth, Math.Abs(j - occBins));
      int loSup = occBins - halfWidth, hiSup = occBins + halfWidth;

      // Phase 2b: widen the window to SkirtWidthFactor× the support half-width, template forced to 0 in the
      // added skirt (see StreamingOptions.SkirtWidthFactor). Never shrinks below the true support, and only
      // ever adds bins strictly outside [loSup, hiSup] — so factor==1 reproduces the original bin set exactly,
      // including any internal gap between separated support lobes (e.g. two-tone FSK), which stays excluded.
      int extHalfWidth = Math.Max(halfWidth, Math.Min(occBins, (int)Math.Ceiling(o.SkirtWidthFactor * halfWidth)));
      int loExt = Math.Max(0, occBins - extHalfWidth), hiExt = Math.Min(L - 1, occBins + extHalfWidth);

      int nSup = 0;
      for (int j = loExt; j <= hiExt; j++) if (t[j] >= 0.05f * tmax || j < loSup || j > hiSup) nSup++;
      supIdx = new int[nSup];
      supT = new float[nSup];
      for (int j = loExt, k = 0; j <= hiExt; j++)
      {
        if (t[j] >= 0.05f * tmax) { supIdx[k] = j; supT[k] = t[j]; k++; }
        else if (j < loSup || j > hiSup) { supIdx[k] = j; supT[k] = 0f; k++; }
      }
      double supTSum = 0;
      foreach (var v in supT) supTSum += v;

      cfoBins = (int)Math.Ceiling(o.CfoMaxHz / binHz);
      nShifts = 2 * cfoBins + 1;

      // per-shift template norms over the in-band part of the support. Shifts that push more than half the
      // template mass out of band are disabled (Σt = 0): normalizing by a small in-band remainder would
      // inflate the noise variance and pin spurious maxima to the CFO rails.
      shiftTSum = new double[nShifts];
      shiftT2Sqrt = new double[nShifts];
      for (int si = 0; si < nShifts; si++)
      {
        int s = si - cfoBins;
        double ts = 0, t2 = 0;
        for (int k = 0; k < nSup; k++)
        {
          int j = supIdx[k] + s;
          if ((uint)j >= (uint)L) continue;
          ts += supT[k];
          t2 += (double)supT[k] * supT[k];
        }
        bool valid = ts >= 0.5 * supTSum && t2 > 0;
        shiftTSum[si] = valid ? ts : 0;
        shiftT2Sqrt[si] = valid ? Math.Sqrt(t2) : 0;
      }

      // per-shift template mean/variance for the MatchAtShift Pearson — recomputing them per call doubled
      // that function's work for values that depend only on the shift.
      matchN = new int[nShifts];
      matchTSum = new double[nShifts];
      matchTVar = new double[nShifts];
      for (int si = 0; si < nShifts; si++)
      {
        int s = si - cfoBins;
        int cnt = 0; double ts = 0, t2 = 0;
        for (int k = 0; k < nSup; k++)
        {
          int j = supIdx[k] + s;
          if ((uint)j >= (uint)L) continue;
          cnt++; ts += supT[k]; t2 += (double)supT[k] * supT[k];
        }
        matchN[si] = cnt;
        matchTSum[si] = ts;
        matchTVar[si] = cnt > 0 ? t2 - ts * ts / cnt : 0;
      }

      int navg = Math.Max(1, o.DetectorAvgFrames);
      zRing = new double[navg][];
      for (int i = 0; i < navg; i++) zRing[i] = new double[nShifts];
      zSum = new double[nShifts];
      frameBestZ = new double[navg];

      // trim() must retain enough frame history to back-date a burst start by the averaging delay + guard.
      keepFrames = (int)Math.Ceiling((double)guard / Hop) + (navg - 1);
      // post-burst soft-bit extension: one worst-case frame on air, so a frame that starts inside the burst
      // is decoded to completion. Detection/parameter estimation never look past the burst end.
      tailSamples = deframer != null ? (int)Math.Ceiling(deframer.MaxFrameBits / p.Baud * fs) : 0;
      // lead overlap across public segment boundaries (cap flush / step-up split / brief dropout): reach
      // back one worst-case frame plus a timing-acquisition lead-in (a frame whose sync sits at the very
      // edge of the window is lost while Gardner is still locking — measured: exactly one frame on HADES
      // 06-07), so a frame cut by the boundary is decoded whole in the follow-up segment (duplicates removed
      // by content + time). Derived from the deframer so "how long can a frame be" has one source of truth.
      overlapSamples = tailSamples + (int)Math.Round(0.5 * fs);
    }

    /// <summary>Feed the next contiguous block of IQ samples; returns any frames that completed within it.</summary>
    public IReadOnlyList<Frame> Push(ReadOnlySpan<Complex32> block)
    {
      Append(block);
      List<Frame>? frames = null;

      while (nextFrameOffset + Fft <= len)
      {
        long absFrame = (bufBaseAbs + nextFrameOffset) / Hop;
        // capture the whole un-notched spectrum only when the inspector is listening (else no allocation)
        float[]? full = DetectionFrameProcessed != null ? (fullSpecScratch ??= new float[Fft]) : null;
        var (oob, _) = StftPsd.Frame(fft, buf, nextFrameOffset, window, binHz, occHalfHz, occBins, q, full);
        ProcessFrame(absFrame, oob, full, ref frames);
        nextFrameOffset += Hop;
      }

      DrainPending(ref frames);   // decode closed bursts whose tail-extension samples have now arrived
      Trim();
      LastPushFrames = (IReadOnlyList<Frame>?)frames ?? Array.Empty<Frame>();
      return LastPushFrames;
    }

    /// <summary>Convenience overload for an array block.</summary>
    public IReadOnlyList<Frame> Push(Complex32[] block) => Push((block ?? throw new ArgumentNullException(nameof(block))).AsSpan());

    /// <summary>Decode a burst still in progress at end-of-stream. Call once after the last <see cref="Push"/>.</summary>
    public IReadOnlyList<Frame> Flush()
    {
      List<Frame>? frames = null;
      if (inBurst)
      {
        // no future samples will arrive — decode immediately with whatever is buffered.
        CloseSpan(startFrameAbs, lastAboveAbs, startGuard: true, endGuard: true, defer: false, ref frames);
        inBurst = false;
      }
      DrainPending(ref frames, force: true);
      LastPushFrames = (IReadOnlyList<Frame>?)frames ?? Array.Empty<Frame>();
      return LastPushFrames;
    }

    // --- detector state machine ----------------------------------------------------------------

    private void ProcessFrame(long absFrame, double oob, float[]? fullSpectrum, ref List<Frame>? frames)
    {
      // rolling noise floor: trimmed mean of recent out-of-band frame powers (out-of-band is always noise, so we
      // update it every frame, including during a burst).
      oobRing[oobHead] = (float)oob;
      oobHead = (oobHead + 1) % oobRing.Length;
      if (oobCount < oobRing.Length) oobCount++;

      double npb = RollingNoiseFloor();
      if (npb <= 0) npb = 1e-12;
      noisePerBin = npb;
      NoiseRefPower = npb;

      // noise-subtracted frame spectrum in the configured correlation domain (NOT clamped at 0 in the power
      // domain — zero-mean under noise, which keeps the matched statistic unbiased; the magnitude/log variants
      // subtract their own analytic noise mean instead), DC-notched so LO leakage can't masquerade as a matched
      // carrier. eSigma is the per-bin noise deviation of excess in the same units — the CFAR normalizer.
      double eSigma;
      switch (o.TemplateDomain)
      {
        case TemplateDomain.Magnitude:
          double sqrtNpb = Math.Sqrt(npb);
          for (int j = 0; j < q.Length; j++) excess[j] = (float)(Math.Sqrt(Math.Max(q[j], 0)) - MagNoiseMean * sqrtNpb);
          eSigma = MagNoiseSigma * sqrtNpb;
          break;
        case TemplateDomain.LogPower:
          // floored at LogFloorFrac·npb: without the clamp the log-domain noise swings far downward (log of
          // near-zero power) and those bins dominate the correlation.
          double floor = o.LogFloorFrac;
          for (int j = 0; j < q.Length; j++) excess[j] = (float)(Math.Log(Math.Max(q[j] / npb, floor)) - logNoise.Mean);
          eSigma = logNoise.Sigma;
          break;
        default:
          for (int j = 0; j < q.Length; j++) excess[j] = (float)(q[j] - npb);
          eSigma = npb;
          break;
      }
      if (o.NotchDc)
        for (int j = occBins - 1; j <= occBins + 1; j++)
          if ((uint)j < (uint)excess.Length) excess[j] = 0;

      // this frame's per-shift matched z into the averaging ring (replace the oldest row).
      var row = zRing[zHead];
      for (int i = 0; i < nShifts; i++) zSum[i] -= row[i];
      (double bestZ, double bestW, int bestS) = MatchedSlide(row, npb, eSigma);
      for (int i = 0; i < nShifts; i++) zSum[i] += row[i];
      frameBestZ[zHead] = bestZ;
      zHead = (zHead + 1) % zRing.Length;
      if (zCount < zRing.Length) zCount++;

      // detection statistic: per-shift sum over the ring scaled back to unit noise variance, THEN max over
      // shifts. CFO is stable across the few averaged frames, so averaging before the max keeps the noise
      // floor of the max statistic at ~2.5–3σ regardless of template width.
      double zStat = double.MinValue;
      double norm = Math.Sqrt(zCount);
      for (int i = 0; i < nShifts; i++) { double v = zSum[i] / norm; if (v > zStat) zStat = v; }

      // empirical CFAR on the statistic itself: the analytic normalization holds on white noise, but birdies
      // and colored in-band noise inflate the no-burst floor of the max statistic (CUBEBUG-2: floor ≈ 3.5σ with
      // excursions to 8σ against OnSigma 5.5 → dozens of false bursts while the true bursts sit at 75σ).
      // Track the median of the QUARANTINED no-burst zStat (a sample is committed only after 12 more no-burst
      // frames, so a burst trigger can discard its own onset ramp) and raise the Schmitt thresholds when the
      // median floor is elevated — see UpdateZThresholds; clean recordings are unchanged.
      if (!inBurst)
      {
        if (zPendingCount == zPending.Length)
        {
          zFloorRing[zFloorHead] = zPending[zPendingHead];   // commit the oldest quarantined sample
          zFloorHead = (zFloorHead + 1) % zFloorRing.Length;
          if (zFloorCount < zFloorRing.Length) zFloorCount++;
          UpdateZThresholds();
        }
        zPending[zPendingHead] = (float)zStat;
        zPendingHead = (zPendingHead + 1) % zPending.Length;
        if (zPendingCount < zPending.Length) zPendingCount++;
      }

      framesSeen++;
      // the state machine may return early (warm-up, no-burst, step-up split); the finally emits the
      // per-frame diagnostic trace for EVERY frame regardless, so the inspector's spectrogram is complete.
      try
      {
        if (framesSeen < warmupFrames) return;   // not enough noise history to trust the threshold yet

        if (!inBurst)
        {
          if (zStat > zOnThresh)
          {
            inBurst = true;
            zPendingCount = 0; zPendingHead = 0;   // the quarantined samples are this burst's onset ramp — discard
            startFrameAbs = OnsetFrame(absFrame);
            lastAboveAbs = absFrame;
            ResetBurstStats();
            burstPeakZ = burstLevelZ = zStat;
            NoteSignalFrame(bestZ, bestW, bestS);
          }
          return;
        }

        if (zStat > Math.Max(zOnThresh, burstLevelZ / o.ReleaseFraction))
        {
          // step-up split: a much stronger signal turned on inside the current (weaker) burst. Close the weak
          // segment and re-trigger, so the strong burst gets a tight segment of its own — its CFO and symbol
          // timing must not be estimated over seconds of the weaker interferer.
          CloseSpan(startFrameAbs, absFrame - 1, startGuard: true, endGuard: false, defer: false, ref frames);
          startFrameAbs = OnsetFrame(absFrame);
          lastAboveAbs = absFrame;
          ResetBurstStats();
          burstPeakZ = burstLevelZ = zStat;
          NoteSignalFrame(bestZ, bestW, bestS);
          return;
        }

        burstLevelZ += 0.1 * (zStat - burstLevelZ);   // ~10-frame (≈0.2 s) time constant
        if (zStat > burstPeakZ) burstPeakZ = zStat;
        if (zStat > Math.Max(zOffThresh, burstPeakZ * o.ReleaseFraction)) lastAboveAbs = absFrame;
        NoteSignalFrame(bestZ, bestW, bestS);

        if ((absFrame - startFrameAbs) * (long)Hop >= maxBurstSamples)
        {
          // capped flush: emit what we have as a window (no end guard — later samples may not be in yet) and
          // continue accumulating a fresh segment from here, so a continuous carrier keeps producing frames.
          CloseSpan(startFrameAbs, absFrame, startGuard: true, endGuard: false, defer: false, ref frames);
          startFrameAbs = absFrame + 1;
          lastAboveAbs = absFrame;
          ResetBurstStats();
          burstPeakZ = burstLevelZ = zStat;   // the next window's release/split track their own level
        }
        else if (absFrame - lastAboveAbs > hangFrames)
        {
          // true end-of-burst: defer the decode so the soft-bit window can extend a max frame past the end.
          CloseSpan(startFrameAbs, lastAboveAbs, startGuard: true, endGuard: true, defer: true, ref frames);
          inBurst = false;
        }
      }
      finally
      {
        if (DetectionFrameProcessed != null && fullSpectrum != null)
          EmitDetectionFrame(absFrame, oob, fullSpectrum, npb, zStat, bestS);
      }
    }

    /// <summary>Publish this frame's detection internals to the inspector. Clones the captured full spectrum
    /// (the frame keeps a snapshot) and sums the in-band bins pre-notch; only called with a live subscriber.</summary>
    private void EmitDetectionFrame(long absFrame, double oob, float[] fullSpectrum, double npb, double zStat, int bestS)
    {
      double inband = 0;
      for (int j = 0; j < q.Length; j++) inband += q[j];
      DetectionFrameProcessed!.Invoke(new DetectionFrame(
        absFrame, absFrame * (double)Hop / fs, (float[])fullSpectrum.Clone(),
        inband, oob, npb, zStat, bestS, inBurst));   // bestS is already in shift units (−cfoBins..+cfoBins)
    }

    /// <summary>
    /// Where the burst actually began: the averaged statistic crosses the threshold up to ring-length−1
    /// frames after a weak burst's true onset, so walk the ring back to the earliest recent frame whose own
    /// best z already cleared the release threshold; if none did (an onset too weak to see per frame),
    /// back-date by the full averaging delay.
    /// </summary>
    private long OnsetFrame(long absFrame)
    {
      int n = zCount;
      for (int back = 0; back < n; back++)
      {
        int slot = ((zHead - 1 - back) % n + n) % n;       // newest → oldest
        if (back > 0 && frameBestZ[slot] <= zOffThresh)
          return Math.Max(0, absFrame - back + 1);
      }
      return Math.Max(0, absFrame - (n - 1));
    }

    /// <summary>
    /// Slide the template's support across the CFO span over the frame's noise-subtracted spectrum. Per
    /// shift s: z(s) = Σ excess·(t − t̄) / (eSigma·√(Σ(t−t̄)²)·√ENBW) — the matched correlation normalized to unit
    /// variance under noise (CFAR), with the bin-correlation of the analysis window folded in; eSigma is the
    /// per-bin noise σ of excess in the configured <see cref="StreamingOptions.TemplateDomain"/> (= npb in the
    /// linear-power domain), so the false-alarm rate is domain-independent. The template is
    /// DC-removed (t̄ = Σt/n subtracted over the in-band support at this shift) so it is NOT all-positive: a flat
    /// pedestal — a colored-noise floor sitting above the wide out-of-band reference npb — integrates to
    /// Σ(t−t̄) = 0 instead of being summed as c·Σt amplified over √(support), which otherwise storms false bursts
    /// on colored noise; only the template's SHAPE drives the statistic. Fills <paramref name="row"/> (indexed
    /// s+cfoBins; disabled shifts stay 0) and returns the best z, the corresponding template-weighted mean
    /// excess power W (for SNR reporting), and the best shift.
    /// </summary>
    private (double bestZ, double bestW, int bestS) MatchedSlide(double[] row, double npb, double eSigma)
    {
      int L = excess.Length;
      double bestZ = double.MinValue, bestW = 0; int bestS = 0;
      double sqrtEnbw = Math.Sqrt(WindowEnbw);
      for (int si = 0; si < nShifts; si++)
      {
        int n = matchN[si];
        if (shiftTSum[si] <= 0 || n < 8) { row[si] = 0; continue; }
        int s = si - cfoBins;
        double tSum = matchTSum[si];
        double tBar = tSum / n;
        double varT = shiftT2Sqrt[si] * shiftT2Sqrt[si] - tSum * tSum / n;   // Σ(t − t̄)² = Σt² − (Σt)²/n
        if (varT <= 0) { row[si] = 0; continue; }
        double se = 0, set = 0;
        for (int k = 0; k < supIdx.Length; k++)
        {
          int j = supIdx[k] + s;
          if ((uint)j >= (uint)L) continue;
          double e = excess[j];
          se += e; set += e * supT[k];
        }
        double acc = set - tBar * se;                                         // Σ excess·(t − t̄)
        double z = acc / (eSigma * Math.Sqrt(varT) * sqrtEnbw);
        row[si] = z;
        if (z > bestZ) { bestZ = z; bestW = set / shiftTSum[si]; bestS = s; }
      }
      // the reported W feeds the burst SNR, which stays in linear power: in the transformed domains recompute
      // it from the linear noise-subtracted power at the winning shift (same DC notch as excess).
      if (o.TemplateDomain != TemplateDomain.Power && bestZ > double.MinValue)
      {
        double setLin = 0;
        for (int k = 0; k < supIdx.Length; k++)
        {
          int j = supIdx[k] + bestS;
          if ((uint)j >= (uint)L) continue;
          if (o.NotchDc && j >= occBins - 1 && j <= occBins + 1) continue;
          setLin += (q[j] - npb) * supT[k];
        }
        bestW = setLin / shiftTSum[bestS + cfoBins];
      }
      return (bestZ, bestW, bestS);
    }

    /// <summary>
    /// Pearson correlation of the frame's noise-subtracted spectrum with the template over the support
    /// shifted by <paramref name="s"/> bins. Mean-subtracted on both sides, so flat noise scores ~0 — and a
    /// narrow line (CW, or SSTV's instantaneous FM carrier) scores low against the full-width template even
    /// when it sits right on the template's peak.
    /// </summary>
    private double MatchAtShift(int s)
    {
      int L = excess.Length;
      int si = s + cfoBins;
      int n = matchN[si];
      if (n < 8) return 0;
      double tSum = matchTSum[si], tv = matchTVar[si];
      double se = 0, set = 0, se2 = 0;
      for (int k = 0; k < supIdx.Length; k++)
      {
        int j = supIdx[k] + s;
        if ((uint)j >= (uint)L) continue;
        double e = excess[j];
        se += e; set += e * supT[k]; se2 += e * e;
      }
      // pearson from the raw sums: Σ(e−ē)(t−t̄) = Σe·t − t̄·Σe,  Σ(e−ē)² = Σe² − (Σe)²/n.
      double num = set - tSum / n * se;
      double ev = se2 - se * se / n;
      return ev > 0 && tv > 0 ? num / Math.Sqrt(ev * tv) : 0;
    }

    /// <summary>Per-burst accounting for one in-burst frame: when the frame's own matched z clears the
    /// release threshold it is signal-bearing — accumulate it into the averaged burst PSD and score its
    /// instantaneous spectrum against the template (the digital-vs-SSTV/CW discriminator).</summary>
    private void NoteSignalFrame(double bestZ, double bestW, int bestS)
    {
      if (bestZ <= o.OffSigma) return;
      AccumulateBurstPsd();
      burstWSum += bestW;
      burstSigFrames++;
      double r = MatchAtShift(bestS);
      burstMatchSum += r;
      if (r >= o.PerFrameMatchMin) burstShapedFrames++;
    }

    private void ResetBurstStats()
    {
      ResetBurstPsd();
      burstWSum = 0;
      burstSigFrames = 0;
      burstShapedFrames = 0;
      burstMatchSum = 0;
    }

    private double RollingNoiseFloor()
    {
      if (oobCount == 0) return 0;
      Array.Copy(oobRing, oobScratch, oobCount);   // valid entries occupy [0, oobCount)
      // interquartile trimmed mean of the per-frame OOB means: same level the median estimated
      // (the per-frame means are near-symmetric), ~half the variance — a steadier threshold reference.
      return NoiseFloor.TrimmedMeanInPlace(oobScratch, oobCount);
    }

    /// <summary>Top of the no-burst max-over-shifts floor on WHITE noise (~2.5–3σ; see
    /// <see cref="StreamingOptions.OnSigma"/>). Only a median floor above this counts as colored-noise
    /// inflation and raises the thresholds.</summary>
    private const double NormalZFloor = 3.0;

    /// <summary>Slope of the threshold raise per unit of median-floor elevation. Calibrated on the corpus:
    /// CUBEBUG-2 (persistent birdie floor, median 3.4, false bursts at 6–8σ against true bursts at 75σ)
    /// needs the onset near 6.5–7σ, while HADES-SA (clean floor, median 2.6, REAL weak bursts at 5.5–6.5σ)
    /// must stay at the analytic 5.5σ — the deadband keeps HADES untouched and the slope lifts CUBEBUG.</summary>
    private const double ZFloorRaiseSlope = 3.0;

    /// <summary>Refresh the effective Schmitt thresholds from the no-burst zStat ring: both thresholds are
    /// shifted by <see cref="ZFloorRaiseSlope"/>× the measured MEDIAN floor's elevation above
    /// <see cref="NormalZFloor"/>, while a recording with a normal floor keeps the analytic
    /// OnSigma/OffSigma exactly, so borderline true bursts there are untouched. The median (over a
    /// minute-scale window) tolerates the ring being partly contaminated by sub-threshold signal; scale
    /// estimates (MAD, lower-quantile) were tried and rejected — on busy passes they overshoot and cost
    /// true weak bursts.</summary>
    private void UpdateZThresholds()
    {
      if (zFloorCount < 16) return;   // keep the analytic thresholds until there is enough floor history
      Array.Copy(zFloorRing, zFloorScratch, zFloorCount);
      Array.Sort(zFloorScratch, 0, zFloorCount);
      double med = zFloorScratch[zFloorCount / 2];
      double raise = ZFloorRaiseSlope * Math.Max(0, med - NormalZFloor);
      zOnThresh = o.OnSigma + raise;
      zOffThresh = o.OffSigma + raise;
    }

    private void ResetBurstPsd() { Array.Clear(burstPsdSum); burstPsdCount = 0; }

    /// <summary>Add the current frame's in-band power spectrum (<see cref="q"/>) to the burst accumulator.</summary>
    private void AccumulateBurstPsd()
    {
      for (int j = 0; j < burstPsdSum.Length; j++) burstPsdSum[j] += q[j];
      burstPsdCount++;
    }

    /// <summary>Mean in-band power spectrum over the burst (the same STFT frames the detector ran on),
    /// noise-subtracted and — when <paramref name="notch"/> — DC-notched, the form
    /// <see cref="CfoEstimator.AnalyzeSpectrum"/> expects. The inspector also builds the un-notched form
    /// (<paramref name="notch"/> false) for its detail chart, so the near-DC energy the notch removes is visible.</summary>
    private float[] BuildAveragedSpectrum(bool notch)
    {
      int L = burstPsdSum.Length;
      var q = new float[L];
      if (burstPsdCount == 0) return q;
      for (int j = 0; j < L; j++) q[j] = (float)Math.Max(0, burstPsdSum[j] / burstPsdCount - noisePerBin);
      if (notch)
        for (int j = occBins - 1; j <= occBins + 1; j++) if ((uint)j < (uint)L) q[j] = 0;   // notch DC/LO leakage
      return q;
    }

    // --- per-burst decode (reuses the batch CFO → demod → deframe path) ------------------------

    /// <summary>One closed burst awaiting decode: the detector has fixed the span and captured every
    /// parameter estimate from the burst's own in-burst STFT frames (averaged PSD → CFO/shape, SNR,
    /// per-frame shape stats). The main decode runs on [SegStartAbs, BareEndAbs); when TargetEndAbs is
    /// further, a second tail-pass window ending there finishes a frame still in flight at the close —
    /// the decode is deferred until those samples have arrived.</summary>
    private sealed record PendingDecode(long SegStartAbs, long DetStartAbs, long DetEndAbs, long BareEndAbs,
      long TargetEndAbs, float[] AvgQ, float[] AvgQRaw, double Snr, double MatchedFrac, int ShapedFrames, double MeanMatch);

    /// <summary>
    /// Close a detected span: capture the burst's parameter estimates — <b>strictly</b> from its own
    /// in-burst frames — then decode. At a true end-of-burst (<paramref name="defer"/>) the soft-bit decode
    /// is deferred until <see cref="IDeframer.MaxFrameBits"/> worth of post-burst samples have arrived, so a
    /// frame that begins near the burst end is demodulated to completion; detection and parameter estimation
    /// never see that extension. public boundaries (cap flush, step-up split) decode immediately — the
    /// follow-up segment's lead overlap covers them.
    /// </summary>
    private void CloseSpan(long startFrameAbs, long endFrameAbs, bool startGuard, bool endGuard, bool defer, ref List<Frame>? frames)
    {
      long sStartAbs = startFrameAbs * Hop - (startGuard ? guard : 0);
      long sEndAbs = endFrameAbs * Hop + Fft + (endGuard ? guard : 0);
      sStartAbs = Math.Max(sStartAbs, bufBaseAbs);
      sEndAbs = Math.Min(sEndAbs, bufBaseAbs + this.len);
      int len = (int)(sEndAbs - sStartAbs);
      if (len < minFrames * Hop) return;   // too short to be a real burst (matches the batch min-length gate)

      // lead overlap: a segment that follows hard on a previous segment of the SAME digital signal — a
      // cap flush, a brief fade dropout — reaches back into that segment's tail, so a frame cut by the
      // boundary is decoded whole here (and deduplicated against the previous segment's output). The
      // previous segment must have passed the per-frame shape gate: extending into an SSTV/CW tail (or
      // across a step-up split, whose predecessor is the weaker interferer) prepends a foreign signal to
      // the demod window and ruins its timing recovery.
      long segStartAbs = sStartAbs;
      if (lastSegDigital && sStartAbs - lastSegEndAbs <= overlapSamples)
        segStartAbs = Math.Max(Math.Max(lastSegStartAbs, lastSegEndAbs - overlapSamples), bufBaseAbs);
      if (segStartAbs > sStartAbs) segStartAbs = sStartAbs;

      double matchedFrac = burstSigFrames > 0 ? (double)burstShapedFrames / burstSigFrames : 0;
      lastSegStartAbs = sStartAbs;
      lastSegEndAbs = sEndAbs;
      lastSegDigital = matchedFrac >= o.MinMatchedFraction && burstShapedFrames >= o.MinShapedFrames;

      // averaged power spectrum from the SAME STFT frames the detector ran on (no second FFT pass), and the
      // band-limited matched SNR: mean best-shift matched power over the signal-bearing frames vs the
      // per-bin noise floor (≈ signal-band SNR, not diluted by the CFO search margin).
      var avgQ = BuildAveragedSpectrum(o.NotchDc);
      // un-notched twin for the inspector's detail chart; identical array when the notch is already off.
      var avgQRaw = o.NotchDc ? BuildAveragedSpectrum(false) : avgQ;
      double meanW = burstSigFrames > 0 ? burstWSum / burstSigFrames : 0;
      double snr = 10.0 * Math.Log10((Math.Max(0, meanW) + noisePerBin) / Math.Max(noisePerBin, 1e-30));
      double meanMatch = burstSigFrames > 0 ? burstMatchSum / burstSigFrames : 0;

      // tail-extend only bursts with enough signal frames to be worth it (noise blips decode bare), and
      // only when the peak-relative release — not the absolute noise floor — closed the burst: that is the
      // case where the signal can still be decodable past the detector's cut and a frame may be in flight.
      // A burst that faded below OffSigma has nothing decodable in its tail.
      long tail = defer && burstSigFrames >= o.MinShapedFrames
                  && burstPeakZ * o.ReleaseFraction > o.OffSigma
                  ? tailSamples : 0;
      var pend = new PendingDecode(segStartAbs, sStartAbs, sEndAbs, sEndAbs, sEndAbs + tail,
        avgQ, avgQRaw, snr, matchedFrac, burstShapedFrames, meanMatch);
      if (tail > 0) pending.Enqueue(pend);
      else DecodePending(pend, ref frames);
    }

    /// <summary>Decode the pending segments whose tail samples have arrived (all of them when
    /// <paramref name="force"/>, at end-of-stream).</summary>
    private void DrainPending(ref List<Frame>? frames, bool force = false)
    {
      while (pending.Count > 0 && (force || bufBaseAbs + len >= pending.Peek().TargetEndAbs))
        DecodePending(pending.Dequeue(), ref frames);
    }

    private void DecodePending(PendingDecode p, ref List<Frame>? frames)
    {
      long segStart = Math.Max(p.SegStartAbs, bufBaseAbs);
      long bareEnd = Math.Min(p.BareEndAbs, bufBaseAbs + len);
      if (bareEnd - segStart <= 0) return;
      var seg = new Complex32[(int)(bareEnd - segStart)];
      Array.Copy(buf, (int)(segStart - bufBaseAbs), seg, 0, seg.Length);

      // tail pass: a SEPARATE demod window around the burst end plus one max frame beyond it, to finish a
      // frame still in flight at the close. Separate, because appending post-burst noise to the main window
      // shifts its DC/timing estimates and degrades the in-burst decode (measured: it costs CRC frames).
      Complex32[]? tailSeg = null;
      long tailStartAbs = 0;
      long tail = p.TargetEndAbs - p.BareEndAbs;
      if (tail > 0)
      {
        tailStartAbs = Math.Max(segStart, p.DetEndAbs - tail - (long)(0.25 * fs));  // lead-in for timing lock
        long tailEnd = Math.Min(p.TargetEndAbs, bufBaseAbs + len);
        if (tailEnd - tailStartAbs > Fft)
        {
          tailSeg = new Complex32[(int)(tailEnd - tailStartAbs)];
          Array.Copy(buf, (int)(tailStartAbs - bufBaseAbs), tailSeg, 0, tailSeg.Length);
        }
      }

      DecodeBurst(seg, p.AvgQ, p.AvgQRaw, segStart, p.DetStartAbs, (int)(p.DetEndAbs - p.DetStartAbs),
        p.Snr, p.MatchedFrac, p.ShapedFrames, p.MeanMatch, tailSeg, tailStartAbs, ref frames);
    }

    /// <summary><paramref name="seg"/> starts at <paramref name="segStartAbs"/> (the overlap-extended decode
    /// span); <paramref name="detStartAbs"/>/<paramref name="detLen"/> is the detected burst proper, used for
    /// the reported span and time.</summary>
    private void DecodeBurst(Complex32[] seg, float[] avgQ, float[] avgQRaw, long segStartAbs, long detStartAbs, int detLen, double snr,
      double matchedFrac, int shapedFrames, double meanMatch, Complex32[]? tailSeg, long tailStartAbs, ref List<Frame>? frames)
    {
      if (o.DetectOnly)
      {
        EmitDetectOnlyReport(seg, avgQ, avgQRaw, segStartAbs, detStartAbs, detLen, snr, matchedFrac, shapedFrames, meanMatch);
        return;
      }
      if (demod == null) return;   // modulation we don't demodulate (CW/SSTV/PSK) — nothing to do
      try
      {
        // --- three-tier deviation lookup ---
        // 1. Curated: p.Deviation known → existing CfoEstimator template-CFO + pre-built demod (unchanged).
        // 2. Learned: blind but dev confirmed by a prior CRC-valid frame → cached demod + carrier-only CFO.
        // 3. Blind (cold start): BlindFskEstimator recovers dev + CFO; drop burst when gates fail (not FSK).
        BurstSpectralInfo info;
        LearnedShape measured;
        double match;
        ShapeHypothesis? matchedHyp;  // the bank hypothesis behind `match` (null on the blind path)
        bool shapePass;               // some hypothesis cleared its own per-shape threshold
        IDemodulator activeDemod;
        SignalParams pEffective;     // p with the resolved Deviation, used for all demod calls
        double? burstBlindDevHz;     // non-null on the blind/learned path

        if (fallbackParams != null)
        {
          // session-locked blind fallback (Phase U): the curated label was proven wrong — validated bursts
          // decoded nothing until a blind trial produced a CRC-valid frame — so decode with the discovered
          // params. The burst PSD is rebuilt over the WIDE blind window (un-notched) because the curated
          // occupied window can clip the real signal (MIMAN: real bandwidth ≈ 2× the labeled GMSK bell).
          // Validation still rides the curated detector's matchedFrac/shapedFrames, which stayed healthy
          // across the whole wrong-label cluster.
          var wideQ = fallbackCfo!.AveragedSpectrum(seg, 0, seg.Length);
          double fbCfoHz = fallbackDevHz.HasValue
            ? BlindFskEstimator.EstimateCarrierFromKnownDev(wideQ, fallbackCfo.CenterBin, fallbackCfo.BinHz, o.CfoMaxHz, fallbackDevHz.Value)
            : BlindFskEstimator.Estimate(wideQ, fallbackCfo.CenterBin, fallbackCfo.BinHz, fallbackParams.Baud, o.CfoMaxHz).CfoHz;
          info = new BurstSpectralInfo(fbCfoHz, 0, 0);
          measured = fallbackCfo.EstimateShapeFromSpectrum(wideQ, fbCfoHz);
          (match, matchedHyp, shapePass) = MatchBank(measured, snr, BlindBank(fallbackDevHz, fallbackParams));
          activeDemod = fallbackDemod!;
          pEffective = fallbackParams;
          burstBlindDevHz = fallbackDevHz;
        }
        else if (!p.IsBlind)
        {
          // curated path: unchanged behavior
          info = cfo.AnalyzeSpectrum(avgQ);
          measured = cfo.EstimateShapeFromSpectrum(avgQ, info.CfoHz);
          (match, matchedHyp, shapePass) = MatchBank(measured, snr);
          activeDemod = bpskCoherent != null
            ? (p.Differential == true && p.Framing != Framing.CCSDS ? bpskDifferential! : bpskCoherent)
            : this.demod!;
          pEffective = p;
          burstBlindDevHz = null;
        }
        else if (learnedDeviationHz.HasValue)
        {
          // session-learned path: deviation already confirmed; find carrier only, reuse cached demod.
          // The shape is still scored per burst (learned-deviation two-tone + the canonical blind bank):
          // a locked deviation proves an EARLIER burst was FSK, not that this one is — without the score,
          // any noise blip after the lock validated unconditionally.
          double cfoHz = BlindFskEstimator.EstimateCarrierFromKnownDev(avgQ, occBins, binHz, o.CfoMaxHz, learnedDeviationHz.Value);
          info = new BurstSpectralInfo(cfoHz, 0, 0);
          measured = cfo.EstimateShapeFromSpectrum(avgQ, cfoHz);
          (match, matchedHyp, shapePass) = MatchBank(measured, snr, BlindBank(learnedDeviationHz.Value, p));
          activeDemod = learnedDemod!;
          pEffective = p with { Deviation = learnedDeviationHz.Value };
          burstBlindDevHz = learnedDeviationHz.Value;
        }
        else
        {
          // cold-start blind path: estimate deviation + carrier from the burst's own averaged PSD.
          // avgQ covers the wide detection band (devMax = min(3·baud, fs/2−baud/2−cfoMax)) so the
          // estimator can see tones from h ≈ 0.3 up to h ≈ 6 when they fall within that window.
          // Score the measured shape against the estimated-deviation two-tone (when found) + the
          // canonical blind bank, so blind bursts ride the same validation paths as curated ones —
          // the old binary IsFsk auto-validate both passed zero-stat noise (HADES-SA FPs) and left
          // real bell-shaped blind bursts (IsFsk=false) with no shape score at all (match=0), which
          // made them unrescuable by any threshold (4 of the 8 corpus FNs at the adopted 2d settings).
          var est = BlindFskEstimator.Estimate(avgQ, occBins, binHz, p.Baud, o.CfoMaxHz);
          info = new BurstSpectralInfo(est.CfoHz, 0, 0);
          measured = cfo.EstimateShapeFromSpectrum(avgQ, est.CfoHz);
          (match, matchedHyp, shapePass) = MatchBank(measured, snr, BlindBank(est.IsFsk ? est.DeviationHz : null, p));
          if (est.IsFsk)
          {
            // two-tone structure confirmed: demod with the estimated deviation
            pEffective = p with { Deviation = est.DeviationHz };
            activeDemod = Demodulators.Create(pEffective, o.GmskOptions) ?? this.demod!;
            burstBlindDevHz = est.DeviationHz;
          }
          else
          {
            // two-tone structure not found: spectrum is bell-shaped (h ≤ 1, like MSK) or the burst is
            // CW / SSTV / noise.  Fall back to the baud/4 discriminator scale (null Deviation); the
            // deframer's CRC gate filters any false positives.  Don't cache deviation — not confirmed.
            pEffective = p;
            activeDemod = this.demod!;
            burstBlindDevHz = null;
          }
        }

        // hopeless-segment skip: skip for the blind path — the FSK gate
        // already filtered junk above; for the curated path apply the existing shape-based check.
        bool canValidate = (matchedFrac >= o.MinMatchedFraction && shapedFrames >= o.MinShapedFrames)
                           || shapePass;
        if (!burstBlindDevHz.HasValue && !canValidate && !o.DecodeRejected
            && shapedFrames < o.MinShapedFrames && match < CrcRescueMinMatch)
          return;

        double timeSeconds = detStartAbs / fs;
        int idx = burstCounter;
        double detEndSec = (detStartAbs + detLen) / fs;

        // demodulate + deframe this burst with one demodulator (main pass + tail pass) → soft symbols, optional
        // eye trace, and the in-burst frames. Called once normally, or twice for a PSK burst whose sub-mode is
        // still unknown (the coherent-vs-differential trial below), or once per blind-fallback trial (Phase U):
        // the caller passes the demod, params and carrier of its hypothesis, so the derotation bursts, the
        // samples-per-symbol and the frame stamps all follow that hypothesis rather than the curated label.
        // Every call goes through the decodeWith wrapper below, which adds the NRZ carrier refinement.
        (SoftSymbols soft, GmskTrace? trace, List<Frame> frames) decodeOnce(IDemodulator demod, SignalParams pe, double cfoHz)
        {
          // segment-local burst (indices into seg) for derotation; absolute time is stamped separately.
          var local = new Burst(0, seg.Length, fs, cfoHz, snr)
          {
            ShapeScore = match,
            BandwidthHz = info.BandwidthHz
          };
          GmskTrace? tr = null;
          SoftSymbols sf;
          if (demod is CpmFskDemodulator cpm)
          {
            tr = cpm.Trace(Acquisition.Derotate(seg, local), pe);   // keep the trace for the eye/inspection view
            sf = tr.Symbols;
          }
          else sf = demod.Demodulate(seg, local, pe);

          var frs = new List<Frame>();
          if (deframer != null)
          {
            double sps = sf.SamplesPerSymbol > 0 ? sf.SamplesPerSymbol : pe.SampleRate / pe.Baud;
            foreach (var f in deframer.Deframe(sf, pe))
            {
              // frame position → absolute stream time (SoftBitOffset is a symbol index into seg; −1 = unknown).
              double ft = f.SoftBitOffset >= 0 ? (segStartAbs + f.SoftBitOffset * sps) / fs : timeSeconds;
              // A frame must START inside the detected burst (or its lead overlap): the tail extension exists
              // only to FINISH frames already in flight — a sync found in the post-burst noise is discarded.
              if (ft > detEndSec + 0.1) continue;
              // the frame's absolute on-air span (when the deframer reports it) drives the overlap dedup.
              long startAbs = f.SoftBitOffset >= 0 ? segStartAbs + (long)(f.SoftBitOffset * sps) : -1;
              long endAbs = f.SoftBitEnd >= 0 ? segStartAbs + (long)(f.SoftBitEnd * sps) : -1;
              if (IsDuplicateFrame(f.Bytes, ft, startAbs, endAbs)) continue;   // same frame re-decoded via the overlap
              frs.Add(f with { BurstIndex = idx, TimeSeconds = ft, CfoHz = cfoHz, SnrDb = snr });
            }

            // tail pass: same CFO, separate window ending one max frame past the burst end — finishes a frame
            // that was still in flight when the detector closed the burst. Only frames that START inside the
            // detected span count; everything else in the tail is post-burst noise.
            if (tailSeg != null)
            {
              var tailLocal = new Burst(0, tailSeg.Length, fs, cfoHz, snr);
              SoftSymbols tailSoft = demod is CpmFskDemodulator c2
                ? c2.Trace(Acquisition.Derotate(tailSeg, tailLocal), pe).Symbols
                : demod.Demodulate(tailSeg, tailLocal, pe);
              double tsps = tailSoft.SamplesPerSymbol > 0 ? tailSoft.SamplesPerSymbol : pe.SampleRate / pe.Baud;
              foreach (var f in deframer.Deframe(tailSoft, pe))
              {
                double ft = f.SoftBitOffset >= 0 ? (tailStartAbs + f.SoftBitOffset * tsps) / fs
                                                 : detEndSec + 1;   // unknown position → not provably in-burst
                if (ft > detEndSec + 0.1) continue;
                long startAbs = f.SoftBitOffset >= 0 ? tailStartAbs + (long)(f.SoftBitOffset * tsps) : -1;
                long endAbs = f.SoftBitEnd >= 0 ? tailStartAbs + (long)(f.SoftBitEnd * tsps) : -1;
                if (IsDuplicateFrame(f.Bytes, ft, startAbs, endAbs)) continue;
                frs.Add(f with { BurstIndex = idx, TimeSeconds = ft, CfoHz = cfoHz, SnrDb = snr });
              }
            }
          }
          return (sf, tr, frs);
        }

        // NRZ carrier refinement: the discriminator's cluster-midpoint centring measures the residual
        // carrier error the spectral CFO estimate left behind (SoftSymbols.ResidualCfoHz). The slicer is
        // immune to that error, but the channel filter is centred on the estimate — a few-hundred-Hz
        // error attenuates one tone and kills marginal decodes (SNIPE B @146.65 s/@177.09 s decode at
        // the true carrier but not 350 Hz off it). A partial decode is not proof the carrier was right:
        // a burst several hundred Hz off can still decode its strongest frames and lose the marginal
        // ones (LASARsat/NIGHTJAR/AISTECHSAT-2 under the normalized SymmetryCfo), so the retry fires on
        // ANY decode whose NRZ says the carrier is off by more than NrzRefineMinDevFrac of the deviation,
        // and the two decodes COMPETE: the retry is adopted only on strictly more CRC-valid frames (so a
        // good first pass — and any decode on a CRC-less framing, where the retry can't prove itself —
        // is kept), with the same dedup-registry rollback the competing trials use (the first pass's
        // registered frames would otherwise suppress the retry's).
        (SoftSymbols soft, GmskTrace? trace, List<Frame> frames) decodeWith(IDemodulator demod, SignalParams pe, double cfoHz)
        {
          var preDecode = new List<RecentFrame>(recentFrames);
          var first = decodeOnce(demod, pe, cfoHz);
          double dev = pe.Deviation ?? pe.Baud / 4.0;
          double residual = first.soft.ResidualCfoHz;
          int firstCrc = first.frames.Count(f => f.CrcValid == true);
          if (Math.Abs(residual) > NrzRefineMinDevFrac * dev && Math.Abs(residual) <= o.CfoMaxHz)
          {
            var afterFirst = new List<RecentFrame>(recentFrames);
            restoreFrames(preDecode);
            var second = decodeOnce(demod, pe, cfoHz + residual);
            if (second.frames.Count(f => f.CrcValid == true) > firstCrc)
            {
              Log.Information("NRZ carrier refinement: {Residual:F0} Hz correction decoded {N} CRC-valid frame(s) at {Time:F2} s",
                residual, second.frames.Count(f => f.CrcValid == true), timeSeconds);
              return second;
            }
            restoreFrames(afterFirst);
          }
          return first;
        }

        // dedup-registry snapshot from before ANY of this burst's decodes: the timing retry below
        // re-decodes the same burst, and only a rollback to this state lets its identical frames
        // register so the CRC-count comparison is unbiased.
        var preMainFrames = new List<RecentFrame>(recentFrames);

        var (soft, trace, burstFrames) = decodeWith(activeDemod, pEffective, info.CfoHz);

        // the curated label produced a CRC-valid frame before any fallback lock — the label is proven, so
        // the blind-fallback trials below stop considering this session's bursts.
        if (!p.IsBlind && fallbackParams == null && burstFrames.Any(f => f.CrcValid == true))
          curatedCrcSeen = true;

        // a CRC-valid frame from the active BPSK submode proves it — the coherent-vs-differential trial
        // below stops considering this session's bursts.
        if (bpskCoherent != null && burstFrames.Any(f => f.CrcValid == true))
          bpskSubmodeProven = true;

        // promotion on the first CRC-valid blind frame: gate strictly on CRC-valid, not blind
        // confidence — a frame that passes CRC proves the deviation it used actually works. Guarded on
        // p.IsBlind because the blind-fallback path also sets burstBlindDevHz — its lock is fallbackParams,
        // not the blind-path learnedDeviationHz.
        if (p.IsBlind && burstBlindDevHz.HasValue && !learnedDeviationHz.HasValue && burstFrames.Any(f => f.CrcValid == true))
        {
          learnedDeviationHz = burstBlindDevHz.Value;
          learnedDemod = Demodulators.Create(p with { Deviation = burstBlindDevHz.Value }, o.GmskOptions) ?? this.demod!;
          resolvedTarget.ResolvedDeviation = burstBlindDevHz.Value;   // surface the actual deviation to the caller's UI
          Log.Information("Blind FSK: locked deviation {Dev:F0} Hz from first CRC-valid frame", burstBlindDevHz.Value);
        }

        // validation: per-frame shape (digital vs SSTV/CW) is primary; the averaged-spectrum match rescues
        // very weak bursts whose individual frames are too noisy to score; a clean FSK eye rescues real FSK
        // bursts whose spectrum matches no template (carrier-dominated FSK); and a CRC-valid frame is absolute
        // proof of digital signal — every detected burst was demodulated, so a telemetry frame embedded in
        // e.g. an SSTV span is never lost to the gate. Blind bursts ride the same paths: their shape is
        // scored against the estimated/learned-deviation two-tone + the canonical blind bank (the former
        // `validated |= blind` auto-pass validated zero-stat noise and is gone).
        // AfskDemodulator is the other FM-discriminator-path demod with a real eye metric — it computes
        // EyeSnrDb via the same CpmFskDemodulator.EyeQuality() helper (see AfskDemodulator.DemodulateSegment),
        // just from its own tone-correlator front end. It hits exactly the carrier-dominated case this rescue
        // documents (CUBEBUG-2), so excluding it here left every AFSK burst without this rescue path.
        bool eyePass = activeDemod is CpmFskDemodulator or AfskDemodulator
                       && soft.EyeSnrDb >= o.MinEyeSnrDb
                       && soft.Count >= o.MinEyeSymbols;
        bool validated = (matchedFrac >= o.MinMatchedFraction && shapedFrames >= o.MinShapedFrames)
                         || shapePass
                         || eyePass
                         || burstFrames.Any(f => f.CrcValid == true);

        // Phase U blind fallback: a validated FSK-family burst that decodes zero frames on the curated label
        // is the signature of a wrong DB label (the SNIPE-D/ERMIS/KSM1/BRO-8/MIMAN cluster — 617 CRC frames
        // recovered blind). Trial the blind hypothesis at the labeled baud, at 2× it (MIMAN's label is
        // wrong in baud too), at 4× it (Luca-2k4: labeled GMSK 2k4 USP, actually ~9k6) and at ½ it
        // (CubeSX-HSE-3: labeled GMSK 4k8 USP, actually ~2k4), over the
        // WIDE blind-window PSD (the curated occupied window can clip the real
        // signal). The first CRC-valid trial frame at the labeled baud or above locks the discovered params
        // for the session and replaces this burst's decode; a premature lock on a coincidental CRC is
        // accepted — CRC is strong proof. When the discriminator retry above already CRC'd this burst,
        // the trials compete instead: a trial adopts only on STRICTLY more CRC frames and never locks —
        // a curated CRC proves the label decodes, so the session stays uncommitted and each burst picks
        // its better hypothesis (the wrong-label recordings split both ways: ERMIS decodes far more via
        // curated label + discriminator, KSM1-A/BRO-8 via the blind estimate).
        // The ½ trial ADOPTS the burst's frames but never locks: a
        // half-rate CRC can come from a co-channel second transmitter (the BRO8_BRO22 recording — a ½
        // lock on one BRO-22-class burst cost the session BRO-8's 29 frames at the labeled baud), so
        // each burst must re-earn it while the full-rate trials keep competing for the lock.
        // Second trigger (regime 2): a REJECTED burst that is strong (snr ≥ RejectedTrialMinSnrDb) and
        // short (≤ RejectedTrialMaxSeconds — long strong rejects are SSTV/CW, exactly what validation
        // rejected) also runs the trials: validation is much tighter than detection, so a strongly
        // mismatched real signal (Luca-2k4: 9k6 on air vs the 2k4 label) never reached them at all.
        bool rejectedStrong = !validated && snr >= o.RejectedTrialMinSnrDb
                              && detLen / fs <= o.RejectedTrialMaxSeconds;

        // Phase U detector retry (class (d)): at marginal SNR the coherent MLSE/DF-DD detectors and the
        // plain FM discriminator are complementary — the sync/PLS regions decode cleanly under both, but
        // the RS-critical body bit errors cluster differently, so each detector recovers bursts the other
        // loses (QMR-KWT 2 @331 s and Luca-9k6 @110 s decode only via the discriminator; Luca-9k6 @20 s
        // only via MLSE). A CRC-valid frame adopts the retry for this burst only and flips validated; it
        // sets neither a session lock nor curatedCrcSeen — the blind trials below still run on the SAME
        // burst and compete (adopt on strictly more CRC frames), because neither hypothesis dominates:
        // on the wrong-label ERMIS recordings the curated-label discriminator beats the blind lock by
        // tens of frames, on the equally wrong-label KSM1-A/BRO8_BRO22 the blind decode wins — the
        // session commits to blind only when the curated label decodes nothing (the lock rule below).
        // Only GMSK/GFSK profiles resolve their detector from the options (FSK pins the orthogonal MF),
        // and the retry is skipped when the primary detector already IS the discriminator.
        int retryCrc = 0;
        List<RecentFrame>? preRetryFrames = null;
        void restoreFrames(List<RecentFrame> snap) { recentFrames.Clear(); recentFrames.AddRange(snap); }
        if (o.DiscriminatorRetry && (validated || rejectedStrong)
            && !burstFrames.Any(f => f.CrcValid == true)
            && pEffective.Modulation is Modulation.GMSK or Modulation.GFSK
            && (o.GmskOptions.UseMlse || o.GmskOptions.DifferentialOrder >= 2))
        {
          var retryDemod = Demodulators.Create(pEffective, o.GmskOptions with { UseMlse = false, DifferentialOrder = 0 });
          if (retryDemod != null)
          {
            // snapshot the dedup registry: the retry's frames register in it, and the competing blind
            // trials below re-decode the SAME burst — without a reset their overlapping frames would be
            // suppressed as duplicates and the competition could never out-score the retry.
            preRetryFrames = new List<RecentFrame>(recentFrames);
            var (tSoft, tTrace, tFrames) = decodeWith(retryDemod, pEffective, info.CfoHz);
            if (tFrames.Any(f => f.CrcValid == true))
            {
              Log.Information("Detector retry: discriminator recovered {N} CRC-valid frame(s) at {Time:F2} s that the coherent detector lost",
                tFrames.Count(f => f.CrcValid == true), timeSeconds);
              soft = tSoft;
              trace = tTrace;
              burstFrames = tFrames;
              validated = true;
              retryCrc = tFrames.Count(f => f.CrcValid == true);
            }
            else
            {
              restoreFrames(preRetryFrames);   // a failed retry must not ghost-suppress the trials' frames
              preRetryFrames = null;
            }
          }
        }

        // Phase-3 AFSK MLSE retry: the mirror image of the detector retry above — AFSK's default chain
        // is the non-coherent tone correlator, so on a zero-CRC burst the coherent generalized MLSE
        // (h = 5/6 trellis over the analytic subcarrier, AfskDemodulator.DemodulateSegmentMlse) gets one
        // CRC-gated attempt. Adoption is per burst only; no session state changes hands, and AFSK never
        // enters the blind trials below, so the shared retry state stays inert.
        if (o.DiscriminatorRetry && (validated || rejectedStrong)
            && !burstFrames.Any(f => f.CrcValid == true)
            && pEffective.Modulation == Modulation.AFSK
            && o.GmskOptions.UseMlse)
        {
          var retryDemod = new AfskDemodulator(o.GmskOptions) { UseMlseDetector = true };
          preRetryFrames = new List<RecentFrame>(recentFrames);
          var (tSoft, tTrace, tFrames) = decodeWith(retryDemod, pEffective, info.CfoHz);
          if (tFrames.Any(f => f.CrcValid == true))
          {
            Log.Information("Detector retry: AFSK MLSE recovered {N} CRC-valid frame(s) at {Time:F2} s that the correlator lost",
              tFrames.Count(f => f.CrcValid == true), timeSeconds);
            soft = tSoft;
            trace = tTrace;
            burstFrames = tFrames;
            validated = true;
            retryCrc = tFrames.Count(f => f.CrcValid == true);
          }
          else
          {
            restoreFrames(preRetryFrames);   // a failed retry must not ghost-suppress later decodes
            preRetryFrames = null;
          }
        }

        // the hypothesis the timing retry (below the trials) competes against — the blind-trial
        // adoption swaps it to the winning trial's params.
        SignalParams timingParams = pEffective;

        if (o.BlindFallback && fallbackParams == null && !curatedCrcSeen && !p.IsBlind
            && (validated || rejectedStrong)
            && (burstFrames.Count == 0 || retryCrc > 0)
            && p.Modulation is Modulation.FSK or Modulation.GFSK or Modulation.GMSK)
        {
          // Phase 4 baud verification: before the CRC-gated trials, measure the on-air baud from this
          // burst's own symbol-rate line (BaudVerifier: squared-discriminator cyclostationary statistic,
          // zoom-DTFT over {label, 2×, ½, 1200, 2400, 4800, 9600}). A line that contradicts the label is
          // flagged and its baud leads the trial order — and is ADDED when the established {b, 2b, 4b, ½b}
          // set lacks it (the label-9600/on-air-2400 class is unreachable by those factors). Adoption and
          // locking stay CRC-gated below, so a spurious line costs one extra trial, never a wrong decode.
          var trialBauds = new List<double> { p.Baud, 2 * p.Baud, 4 * p.Baud, p.Baud / 2 };
          var baudCandidates = BaudVerifier.CandidateBauds(p.Baud, fs);
          double maxCandidate = 0;
          foreach (double b in baudCandidates) maxCandidate = Math.Max(maxCandidate, b);
          // the pre-filter must pass the outer tone plus the transition band of the highest candidate, or a
          // faster-than-label signal loses its line before the discriminator ever sees it
          double discCutoffHz = Math.Max(info.BandwidthHz, (pEffective.Deviation ?? p.Baud) + 0.75 * maxCandidate);
          var baudLine = BaudVerifier.StrongestLine(seg, fs, info.CfoHz, discCutoffHz, baudCandidates);
          if (baudLine != null && Math.Abs(baudLine.CandidateBaud - p.Baud) > 0.05 * p.Baud)
          {
            Log.Information("Baud verification: measured {Measured:F0} Bd (line score {Score:F1}) contradicts the labeled {Label:F0} Bd at {Time:F2} s",
              baudLine.MeasuredBaud, baudLine.Score, p.Baud, timeSeconds);
            trialBauds.RemoveAll(b => Math.Abs(b - baudLine.CandidateBaud) < 0.05 * b);
            trialBauds.Insert(0, baudLine.CandidateBaud);
          }

          // 4× reaches the Luca-2k4 class (label 2k4, ~9k6 on air — forced blind FSK 9600 CRCs where
          // {b, 2b, ½b} cannot; a lock-on-CRC session can never chain two doublings). Trial bauds the
          // sample rate cannot carry (< 2 samples/symbol) are skipped.
          foreach (double b in trialBauds)
          {
            if (fs / b < 2) continue;
            var pBlind = p with { Modulation = Modulation.FSK, Baud = b, Deviation = null };
            var trialCfo = TrialCfo(pBlind);
            var wideQ = trialCfo.AveragedSpectrum(seg, 0, seg.Length);
            var est = BlindFskEstimator.Estimate(wideQ, trialCfo.CenterBin, trialCfo.BinHz, b, o.CfoMaxHz);
            var pTrial = est.IsFsk ? pBlind with { Deviation = est.DeviationHz } : pBlind;
            var trialDemod = Demodulators.Create(pTrial, o.GmskOptions);
            if (trialDemod == null) continue;
            // fair competition against an adopted retry: decode over the pre-retry dedup registry, and
            // put the retry's registrations back if this arm does not win.
            List<RecentFrame>? retryRegistry = null;
            if (retryCrc > 0 && preRetryFrames != null)
            {
              retryRegistry = new List<RecentFrame>(recentFrames);
              restoreFrames(preRetryFrames);
            }
            var (tSoft, tTrace, tFrames) = decodeWith(trialDemod, pTrial, est.CfoHz);
            if (tFrames.Count(f => f.CrcValid == true) <= retryCrc)
            {
              if (retryRegistry != null) restoreFrames(retryRegistry);
              continue;
            }
            double? trialDevHz = est.IsFsk ? est.DeviationHz : null;
            if (b >= p.Baud && retryCrc == 0)
            {
              // CRC-proven at the labeled baud or above: lock the fallback session state.
              fallbackParams = pTrial;
              fallbackDemod = trialDemod;
              fallbackCfo = trialCfo;
              fallbackDevHz = trialDevHz;
              resolvedTarget.ResolvedDeviation = fallbackDevHz;   // surface the discovered deviation to the caller's UI
              Log.Information("Blind fallback: curated {Mod} {LabelBaud:F0} Bd label distrusted — locked blind FSK at {Baud:F0} Bd, deviation {Dev:F0} Hz from first CRC-valid frame",
                p.Modulation, p.Baud, b, fallbackDevHz ?? 0);
            }
            else
              Log.Information("Blind fallback: trial decoded this burst at {Baud:F0} Bd (deviation {Dev:F0} Hz) — adopted without locking the session",
                b, trialDevHz ?? 0);
            // adopt the trial decode for this burst. The CRC-valid trial frame is absolute proof of a real
            // digital burst, so a rejected-trigger burst flips to validated and passes the gate below.
            soft = tSoft;
            trace = tTrace;
            burstFrames = tFrames;
            validated = true;
            info = new BurstSpectralInfo(est.CfoHz, 0, 0);
            measured = trialCfo.EstimateShapeFromSpectrum(wideQ, est.CfoHz);
            (match, matchedHyp, shapePass) = MatchBank(measured, snr, BlindBank(trialDevHz, pTrial));
            timingParams = pTrial;   // the timing retry below competes against the adopted hypothesis
            break;
          }
        }

        // Phase 7 timing retry: the whole-burst feed-forward timing (CpmFskDemodulator.FeedforwardSync)
        // and the Gardner loop are complementary per burst — the block estimate has no acquisition
        // transient and nails the clock RATE (short/marginal bursts: NIGHTJAR 29→34 crc, UmKA-1 5→6,
        // HADES-SA +1 on the timing A/B), but its single linear clock model breaks on long multi-frame
        // bursts whose TX clock wanders within the burst (UND ROADS 2: the 8 s / 39k-symbol burst drops
        // 47→42 frames under feed-forward while Gardner tracks the wander). So every CPM burst decodes
        // under both timings and the CRC count picks the winner: adopt-only on STRICTLY more CRC-valid
        // frames (the blind-trial competition semantics), decoded over the pre-decode dedup registry so
        // the earlier decodes' registrations don't suppress the retry's identical frames and bias the
        // count. Runs AFTER the blind trials, against whichever hypothesis won the burst so far
        // (timingParams + the post-adoption info.CfoHz): placed before them it CRC'd QMR-KWT 2's
        // marginal @151 s burst on the curated label, which blocked the blind trial's session lock
        // (retryCrc gate) — and the @331 s burst decodes only through the locked-session machinery
        // (7→6 crc). Per burst only — no session state, no effect on locking; a win flips validated
        // (CRC = absolute proof). The demod-type guard keeps this to the CPM/discriminator path
        // (BPSK defaults to feed-forward already; AFSK has its own DPLL timing).
        if (o.TimingRetry && (validated || rejectedStrong))
        {
          var retryTiming = o.GmskOptions.Timing == PskTiming.Feedforward ? PskTiming.Gardner : PskTiming.Feedforward;
          var retryDemod = Demodulators.Create(timingParams, o.GmskOptions with { Timing = retryTiming });
          if (retryDemod is CpmFskDemodulator)
          {
            int haveCrc = burstFrames.Count(f => f.CrcValid == true);
            var afterCurrent = new List<RecentFrame>(recentFrames);
            restoreFrames(preMainFrames);
            var (tSoft, tTrace, tFrames) = decodeWith(retryDemod, timingParams, info.CfoHz);
            if (tFrames.Count(f => f.CrcValid == true) > haveCrc)
            {
              Log.Information("Timing retry: {Timing} timing decoded {N} CRC-valid frame(s) at {Time:F2} s (vs {Have} before)",
                retryTiming, tFrames.Count(f => f.CrcValid == true), timeSeconds, haveCrc);
              soft = tSoft;
              trace = tTrace;
              burstFrames = tFrames;
              validated = true;
            }
            else restoreFrames(afterCurrent);   // a lost retry must not ghost-suppress later decodes
          }
        }

        // BPSK coherent-vs-differential trial (the dual-submode trial decodeWith documents): the resolved
        // Differential is a guess whenever satyaml states no precoding (the resolver defaults to coherent —
        // Waratah Seed-1 is differential on air with no precoding in the DB, 0 frames coherent). On a
        // validated burst with no CRC-valid frame, decode once more with the OTHER submode; a CRC-valid
        // frame locks the winner into the caller's mutable SignalParams.Differential (the per-burst demod
        // pick reads it, so subsequent bursts start on the proven submode). CCSDS framing keeps the
        // existing coherent-only rule.
        if (bpskCoherent != null && !bpskSubmodeProven && p.Framing != Framing.CCSDS && validated
            && !burstFrames.Any(f => f.CrcValid == true))
        {
          var trialDemod = ReferenceEquals(activeDemod, bpskDifferential) ? bpskCoherent : bpskDifferential!;
          var (tSoft, tTrace, tFrames) = decodeWith(trialDemod, pEffective, info.CfoHz);
          if (tFrames.Any(f => f.CrcValid == true))
          {
            bool diff = ReferenceEquals(trialDemod, bpskDifferential);
            resolvedTarget.Differential = diff;   // lock the proven submode for the session (and the caller's UI)
            bpskSubmodeProven = true;
            soft = tSoft;
            trace = tTrace;
            burstFrames = tFrames;
            Log.Information("BPSK submode trial: locked {Mode} from first CRC-valid frame",
              diff ? "differential" : "coherent");
          }
        }

        // locked-session curated re-trial: the fallback lock sets the DEFAULT demod, not a monopoly.
        // On a locked burst that decodes zero frames, the curated label gets one CRC-gated attempt
        // (QMR-KWT 2: the GMSK 9k6 USP label is RIGHT, but one marginal burst's blind trial CRC'd
        // before any curated frame and the lock then cost two curated-decodable bursts — 5 crc
        // without the fallback vs 4 with it). Adopt-only, like the ½-baud trial: the session stays
        // locked, the curated hypothesis just keeps competing burst by burst.
        if (fallbackParams != null && ReferenceEquals(activeDemod, fallbackDemod) && validated
            && !burstFrames.Any(f => f.CrcValid == true) && this.demod != null)
        {
          var curInfo = cfo.AnalyzeSpectrum(avgQ);
          var (cSoft, cTrace, cFrames) = decodeWith(this.demod, p, curInfo.CfoHz);
          // detector retry, locked-session flavor (class (d)): a fallback-locked burst never reaches the
          // retry block above (pEffective is the locked blind FSK), so the curated re-trial carries the
          // discriminator arm here — QMR-KWT 2 @331 s and Luca-9k6 @110 s decode on the curated label
          // ONLY via the discriminator, and both sessions are locked long before those bursts arrive.
          // The carrier is the fallback's known-deviation estimate (info.CfoHz), not curInfo's template
          // correlation: the QMR @331 s decode tolerates < ±100 Hz of carrier error and only the
          // known-dev estimate sits inside that window.
          if (!cFrames.Any(f => f.CrcValid == true) && o.DiscriminatorRetry
              && p.Modulation is Modulation.GMSK or Modulation.GFSK
              && (o.GmskOptions.UseMlse || o.GmskOptions.DifferentialOrder >= 2))
          {
            var discDemod = Demodulators.Create(p, o.GmskOptions with { UseMlse = false, DifferentialOrder = 0 });
            if (discDemod != null) (cSoft, cTrace, cFrames) = decodeWith(discDemod, p, info.CfoHz);
          }
          if (cFrames.Any(f => f.CrcValid == true))
          {
            soft = cSoft;
            trace = cTrace;
            burstFrames = cFrames;
            info = curInfo;
            measured = cfo.EstimateShapeFromSpectrum(avgQ, curInfo.CfoHz);
            (match, matchedHyp, shapePass) = MatchBank(measured, snr);
            Log.Information("Blind fallback: curated re-trial decoded this burst on the labeled params — adopted without unlocking the session");
          }
        }

        if (o.GateByShape && !validated) return;

        if (burstFrames.Count > 0) { frames ??= new List<Frame>(); frames.AddRange(burstFrames); }

        // absolute-span burst (the detected span, not the overlap-extended decode span) for display.
        var abs = new Burst((int)detStartAbs, (int)(detStartAbs + detLen), fs, info.CfoHz, snr)
        {
          ShapeScore = match,
          BandwidthHz = info.BandwidthHz
        };
        LastBurst = abs;
        LastBurstTimeSeconds = timeSeconds;
        LastSoftSymbols = soft;
        LastTrace = trace;
        burstCounter = idx + 1;

        // correlation curve behind the match (for the inspector's correlation plot), in Hz — against the
        // best-matching bank hypothesis (the labeled model when no shape matching ran, i.e. the blind path).
        var (lagBaud, corr) = CpmTemplate.Correlation(measured, (matchedHyp ?? templateBank[0]).Shape);
        var lagHz = new double[lagBaud.Length];
        for (int k = 0; k < lagBaud.Length; k++) lagHz[k] = lagBaud[k] * p.Baud;

        if (frames != null)
          foreach (Frame frame in frames)
            FrameDecoded?.Invoke(frame);

        BurstDecoded?.Invoke(new StreamingBurstReport(idx, segStartAbs, seg.Length, timeSeconds, validated,
          abs, soft, trace, burstFrames, measured, lagHz, corr, matchedFrac, meanMatch, shapedFrames, matchedHyp, avgQRaw));
      }
      catch (Exception ex)
      {
        // one bad burst must not sink the live stream (mirrors the batch pipeline's per-burst guards).
        Log.Warning(ex, "Streaming burst decode failed at sample {Start}", segStartAbs);
      }
    }

    /// <summary>Detection-only twin of <see cref="DecodeBurst"/>: computes the same burst spectral stats
    /// (CFO, shape, matched-bank score) the curated decode path derives from <paramref name="avgQ"/>, but
    /// never runs the demodulator/deframer — no blind-FSK estimation, no CRC, no frames. Used by
    /// <see cref="StreamingOptions.DetectOnly"/> so the Detection Inspector gets a <see cref="StreamingBurstReport"/>
    /// per closed span without paying for (or being skewed by) a full decode.</summary>
    private void EmitDetectOnlyReport(Complex32[] seg, float[] avgQ, float[] avgQRaw, long segStartAbs, long detStartAbs, int detLen,
      double snr, double matchedFrac, int shapedFrames, double meanMatch)
    {
      var info = cfo.AnalyzeSpectrum(avgQ);
      var measured = cfo.EstimateShapeFromSpectrum(avgQ, info.CfoHz);
      var (match, matchedHyp, shapePass) = MatchBank(measured, snr);

      bool validated = (matchedFrac >= o.MinMatchedFraction && shapedFrames >= o.MinShapedFrames) || shapePass;
      if (o.GateByShape && !validated) return;

      double timeSeconds = detStartAbs / fs;
      int idx = burstCounter;

      var abs = new Burst((int)detStartAbs, (int)(detStartAbs + detLen), fs, info.CfoHz, snr)
      {
        ShapeScore = match,
        BandwidthHz = info.BandwidthHz
      };
      LastBurst = abs;
      LastBurstTimeSeconds = timeSeconds;
      burstCounter = idx + 1;

      var (lagBaud, corr) = CpmTemplate.Correlation(measured, matchedHyp.Shape);
      var lagHz = new double[lagBaud.Length];
      for (int k = 0; k < lagBaud.Length; k++) lagHz[k] = lagBaud[k] * p.Baud;

      var soft = new SoftSymbols { Soft = Array.Empty<float>(), SymbolRate = p.Baud };
      BurstDecoded?.Invoke(new StreamingBurstReport(idx, segStartAbs, seg.Length, timeSeconds, validated,
        abs, soft, null, Array.Empty<Frame>(), measured, lagHz, corr, matchedFrac, meanMatch, shapedFrames, matchedHyp, avgQRaw));
    }

    /// <summary>Match the burst's measured averaged spectrum against every bank hypothesis. Returns the best
    /// raw score and its hypothesis (for display and the CRC-rescue bar), plus whether ANY hypothesis cleared
    /// its own per-shape threshold — a bell fit must clear the higher bell bar even when it outscores a
    /// failing two-tone fit, so best-fit and pass are separate decisions.</summary>
    private (double match, ShapeHypothesis best, bool pass) MatchBank(LearnedShape measured, double snrDb)
      => MatchBank(measured, snrDb, templateBank);

    private (double match, ShapeHypothesis best, bool pass) MatchBank(LearnedShape measured, double snrDb,
      IReadOnlyList<ShapeHypothesis> bank)
    {
      // SNR-matched floor: clamp the dB comparison at the burst's measurable dynamic range (≈ −SNR − 6 dB
      // below the peak-normalized spectrum) so template nulls the noise floor hides can't drag the Pearson
      // down on low-SNR bursts. Off by default (fixed −40 dB floor).
      double floor = o.SnrMatchedTemplateFloor
        ? Math.Max(CpmTemplate.LogFloor, Math.Pow(10, -(snrDb + 6.0) / 10.0))
        : CpmTemplate.LogFloor;
      double best = double.NegativeInfinity;
      ShapeHypothesis bestHyp = bank[0];
      bool pass = false;
      foreach (var h in bank)
      {
        double m = CpmTemplate.Match(measured, h.Shape, floor, o.MagnitudeShapeScore);
        if (m > best) { best = m; bestHyp = h; }
        if (m >= o.EffectiveMinShapeScore(h)) pass = true;
      }
      return (best, bestHyp, pass);
    }

    /// <summary>Shape-hypothesis bank for one blind/learned/fallback burst: the canonical bank
    /// (<see cref="CpmTemplate.SynthesizeBank"/> — rect h=1, rect h=0.5, Gaussian) plus, when the estimator
    /// found (or a CRC lock confirmed) a deviation, the two-tone template at that deviation. The deviation
    /// is rounded to 25 Hz so the per-deviation synthesis cache stays bounded over a long session of
    /// per-burst estimates (the template grid is far coarser anyway). <paramref name="pb"/> carries the
    /// hypothesis params: when its baud differs from the pipeline's, the canonical part is synthesized for
    /// THAT baud instead of reusing <see cref="templateBank"/> — <see cref="LearnedShape"/> grids are
    /// baud-normalized, so mixing bauds mis-scales the bank.</summary>
    private IReadOnlyList<ShapeHypothesis> BlindBank(double? devHz, SignalParams pb)
    {
      var canonical = pb.Baud != p.Baud ? CpmTemplate.SynthesizeBank(pb) : templateBank;
      if (devHz is not double d || d <= 0) return canonical;
      double dev = Math.Round(d / 25.0) * 25.0;
      var shape = CpmTemplate.Synthesize(pb with { Deviation = dev });
      var bank = new List<ShapeHypothesis>(canonical.Count + 1)
      {
        new ShapeHypothesis($"blind h={2 * dev / pb.Baud:0.##}", shape, Bell: shape.SampleAtBaud(0) >= 0.5),
      };
      bank.AddRange(canonical);
      return bank;
    }

    /// <summary>Wide-window <see cref="CfoEstimator"/> for one blind-fallback trial baud, on the detector's
    /// FFT grid. Cached per baud (the trial set is tiny — labeled baud and 2×) and disposed with the
    /// pipeline; <see cref="fallbackCfo"/> aliases a cache entry, so the cache is the single owner.</summary>
    private CfoEstimator TrialCfo(SignalParams pBlind)
    {
      if (!trialCfoCache.TryGetValue(pBlind.Baud, out var est))
        trialCfoCache[pBlind.Baud] = est = new CfoEstimator(fs, o.CfoMaxHz, pBlind, fftSize: Fft);
      return est;
    }

    /// <summary>An emitted frame, kept for overlap dedup: its bytes/time and — when the deframer reported the
    /// frame's on-air span — its absolute start/end sample (StartAbs < 0 when unknown).</summary>
    private readonly record struct RecentFrame(byte[] Bytes, double Time, long StartAbs, long EndAbs);

    /// <summary>True when this frame duplicates one already emitted (the overlap re-decode of a
    /// boundary-straddling frame); otherwise records it. When the frame's on-air span is known
    /// (<paramref name="startAbs"/>/<paramref name="endAbs"/> ≥ 0) it is rejected if it overlaps an
    /// already-emitted span by more than half the shorter span — this catches a frame re-decoded in another
    /// window even when each window's independent CFO/timing flipped a few bits (the CRC-less HADES SSDV/CODEC2
    /// types defeat byte-equality). Framings that don't report a span (USP/AX.25/CCSDS) fall back to exact
    /// byte-equality within ±1 s; those are CRC-gated so that already works. Entries older than 30 s are pruned.</summary>
    private bool IsDuplicateFrame(byte[] bytes, double time, long startAbs, long endAbs)
    {
      recentFrames.RemoveAll(r => time - r.Time > 30.0);
      bool havePos = startAbs >= 0 && endAbs > startAbs;
      foreach (var r in recentFrames)
      {
        if (havePos && r.EndAbs > r.StartAbs)
        {
          if (OverlapFraction(startAbs, endAbs, r.StartAbs, r.EndAbs) > 0.5) return true;
        }
        else if (Math.Abs(time - r.Time) < 1.0 && r.Bytes.AsSpan().SequenceEqual(bytes)) return true;
      }
      recentFrames.Add(new RecentFrame(bytes, time, startAbs, endAbs));
      return false;
    }

    /// <summary>Fraction of the shorter of the two spans [a0,a1) / [b0,b1) covered by their intersection.</summary>
    private static double OverlapFraction(long a0, long a1, long b0, long b1)
    {
      long inter = Math.Min(a1, b1) - Math.Max(a0, b0);
      if (inter <= 0) return 0;
      long shorter = Math.Min(a1 - a0, b1 - b0);
      return shorter > 0 ? (double)inter / shorter : 0;
    }

    // --- sliding buffer management -------------------------------------------------------------

    private void Append(ReadOnlySpan<Complex32> block)
    {
      if (block.IsEmpty) return;
      EnsureCapacity(len + block.Length);
      block.CopyTo(buf.AsSpan(len));
      len += block.Length;
    }

    private void EnsureCapacity(int needed)
    {
      if (buf.Length >= needed) return;
      int cap = buf.Length;
      while (cap < needed) cap *= 2;
      Array.Resize(ref buf, cap);
    }

    /// <summary>Drop consumed samples from the front, keeping the guard + averaging-delay history (idle) or
    /// the whole burst (in burst), so memory stays bounded between bursts and the buffer never trims into a
    /// pending — or about-to-be-back-dated — burst.</summary>
    private void Trim()
    {
      long cursor = bufBaseAbs + nextFrameOffset;
      long keepFromAbs;
      if (inBurst)
        keepFromAbs = startFrameAbs * Hop - guard;                       // hold the whole burst + start guard
      else
        keepFromAbs = cursor - (long)keepFrames * Hop;                    // retain history behind the cursor

      // hold the previous segment's tail while a follow-up segment could still reach back into it (overlap).
      if (lastSegDigital && cursor - lastSegEndAbs <= overlapSamples + (long)keepFrames * Hop)
        keepFromAbs = Math.Min(keepFromAbs, Math.Max(lastSegStartAbs, lastSegEndAbs - overlapSamples));

      // hold everything a deferred (tail-extended) decode still needs.
      if (pending.Count > 0)
        keepFromAbs = Math.Min(keepFromAbs, pending.Peek().SegStartAbs);

      keepFromAbs = Math.Max(keepFromAbs, bufBaseAbs);
      keepFromAbs -= ((keepFromAbs % Hop) + Hop) % Hop;                    // align down to the Hop grid
      keepFromAbs = Math.Max(keepFromAbs, bufBaseAbs);

      int drop = (int)(keepFromAbs - bufBaseAbs);
      if (drop <= 0) return;
      Array.Copy(buf, drop, buf, 0, len - drop);
      len -= drop;
      bufBaseAbs += drop;
      nextFrameOffset -= drop;
    }

    public void Dispose()
    {
      fft.Dispose();
      cfo.Dispose();
      foreach (var est in trialCfoCache.Values) est.Dispose();
    }
  }
}
