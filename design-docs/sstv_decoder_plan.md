# SSTV Decoder Plan (VE3NEA.Sstv)

Status: design agreed via grill-me interview 2026-06-29. Not yet implemented.

## Current Status

**Phase: P6(b) — the pulse-train MHT extractor is the next action.** (2026-07-02) The code now lives in the
`VE3NEA.SkySignals` repo, branch `sstv`, project renamed **`VE3NEA.SkySSTV`** (the retro's VE3NEA.Tlm commit
hashes refer to the old repo). Baseline re-verified here: **build clean, suite 80 pass / 2 manual-skip** —
identical to the state recorded in §9 (retro items J, A, C, N, K, L, M, P done; **G/H open = the next
piece**). Next action, in order:

1. Extend `SstvPulseTrain` to Hopper's `TPulseTrain` (pulse storage, `GetPower` ±4-pulse smoothing,
   `AddOldPulses` back-fill on promote, `IsRetiredAt`, revision marks) + a `TVisPulseTrain` equivalent
   (VIS-seeded high-prior train, promotes on 3 pulses, `TryAddPulses` triplet adoption).
2. New `SstvPulseTrainExtractor` ported from `C:\Proj\DSP\Hopper\TrainExtr.pas` + `PulseTrn.pas` (read this
   session; no AFC/multi-frequency dimension — associate-first, then per-mode triplet spawn gated by the
   pulse's `DurMs` family; candidate→active→retired; best-train-per-block with 1.5× hysteresis; dirty-block
   scan-line list). Note: associate-first naturally kills the Robot36→Robot72 half-rate harmonic spawn, and
   ±3 % period gates separate every pair of PD modes — no extra disambiguation needed.
3. Unit tests (clean train, clutter, fade/coast, mixed FSK+SSTV bursts), then wire into `DetectMode`
   (replacing `SstvModeDetector`'s whole-file scan) and `LineOnsets` (train regressor grid replaces
   `SstvSyncTracker`; apply `CorrFactor` to the intra-line pixel clock — retro F).
4. Delete the batch detectors (`SstvSyncFilter`, `SstvSyncCorrelator`, `SstvSyncTracker`, batch
   `SstvModeDetector` internals — retro G) and update the tests that use them directly
   (`SstvP2Tests`, `SstvP6Tests.MaxSyncScore`, `SstvImageHarness.Real_SyncScoreProbe`). `SstvToneBank`
   stays for now: the VIS detector's bounded ~2 s window is allowed block processing (§1.13).
5. Re-run the real-capture probes; **unskip `Real_DecodesToPng` once the extractor localizes UTMN2's burst
   (~185–216 s into the capture)** — with Stage-2 the per-pulse scores are at synthetic level, so the train
   should stand out clearly: the **first real PNG** is the milestone. Then P6(c) threshold tuning (retro
   D/E/O fold in here); raise the `SstvNoiseTests` floors as fidelity improves.

Goal: decode satellite SSTV from a 48 kHz complex-IQ stream and surface the
progressively-built image in SkyRoof's TelemetryPanel. Satellites generate SSTV
audio and feed it to an FM transmitter, so the chain is **FM-on-FM**: an outer FM
discriminator recovers the audio, then the SSTV subcarrier (1100-2300 Hz) is
decoded into an image.

Reference (classic, not modern): `C:\Proj\Forks\mmsstv` (`sstv.cpp`). We borrow the
mode/VIS constants and the broad structure, not the algorithms.

---

## 1. Decisions (locked during the interview)

1. **New assembly `VE3NEA.Sstv`**, depending only on `VE3NEA.Dsp` (shared DSP via
   project-path reference, per the SkyRoof-LiquidFir memory). 
   
   The new assembly lives in the VE3NEA.Tlm solution, side by side with VE3NEA.Tlm assembly. 

   SkyRoof references
   both `VE3NEA.Tlm` and `VE3NEA.Sstv`. SSTV is continuous image reception and emits
   a bitmap, not `Frame`s; it does **not** belong in `VE3NEA.Tlm` (whose
   `StreamingPipeline` is burst->frame->CRC and literally rejects SSTV as analog noise).

2. **Front-end reuses already-wrapped natives** (`VE3NEA.Dsp.NativeLiquidDsp` /
   `LiquidFir`): complex channel FIR -> `freqdem` (outer FM demod) -> audio.
   No new P/Invoke needed for the core chain.

3. **De-emphasis: optional, OFF by default.** Brightness is the *instantaneous
   frequency* of the subcarrier (amplitude-independent), so de-emphasis cannot change
   it; it only reshapes post-FM noise the audio bandpass already handles. Keep a toggle
   (e.g. 300 us NBFM) and decide empirically in the experiment phase.

4. **Brightness measurement: quadrature downconvert + phase-diff.** Mix audio down by
   1900 Hz (`nco_crcf`), lowpass (`LiquidFir`) -> instantaneous freq
   `Im(conj(z[n-1])*z[n])`. The lowpass IS the stage-3 brightness filter. Fully reuses
   wrapped natives; amplitude-independent.

5. **Mode detection: VIS as a strong prior, MHT always running.** Decode VIS when
   present (high-prior hypothesis); run MHT continuously, scoring every candidate mode
   against the observed sync cadence/line-period. MHT confirms VIS, fills in when VIS is
   absent, and can override a corrupted-but-parseable VIS when sync evidence strongly
   contradicts it.

6. **Kalman: ONE filter (KF1), sync timing/slant only.** value = sync sub-pixel phase,
   rate = line-rate vs the *receiver sample clock* -> slant correction + missed-pulse
   prediction (coasting). Reuses `VE3NEA.Dsp.KalmanFilter2nd`.
   - **AFC/KF2 dropped.** Established during the interview: for FM-on-FM, RF Doppler
     becomes a constant DC offset *added to the audio waveform* (`demod = f_doppler +
     k*audio(t)`); it does not move the audio tones. The only audio-frequency effect is
     time-scaling beta = v/c ~ 2.5e-5 -> 1200 Hz shifts ~0.03 Hz (negligible). The DC
     offset is removed for free by the stage-4 downconvert-by-1900 + lowpass (DC maps to
     -1900 Hz, rejected). mmsstv needs AFC only because classic HF SSTV is **SSB**, where
     tuning error shifts the whole audio spectrum - not applicable to FM.
   - The tiny Doppler-rate time-scaling of the line period is absorbed by KF1's rate state.

7. **Sync detector: fixed 1200 Hz complex correlator** on the audio (window ~ mode sync
   duration). `|corr|` -> presence (MHT + coast); `arg` -> sub-sample phase (KF1). Its
   frequency-error output is a **diagnostic that should read ~0** (sanity check), not an
   AFC driver. Independent of any AFC, so no sync<->AFC chicken-and-egg.

8. **Supported modes: the YCrCb family (Robot + PD).** Robot36, Robot72, PD50, PD90,
   PD120, PD160, PD180, PD240, PD290. Mode table is data-driven (timing constants +
   color-layout enum). Three color layouts to implement:
   - Robot36: YCrCb, alternating half-rate chroma (Cr/Cb on alternate lines)
   - Robot72: YCrCb, full chroma per line
   - PDxx: YCrCb, one Cr/Cb pair shared by two consecutive luma lines
   - RGB Martin/Scottie deferred (rare from satellites; a 4th layout + table rows later).

9. **SkyRoof integration via `TelemetryDecocder` as dispatcher.** Add
   `SstvDecoder? Sstv` alongside `StreamingPipeline? Pipeline`; select by
   `SignalParams.Modulation == Modulation.SSTV`. `Process()` pushes IQ to whichever is
   active. `ThreadedProcessor<Complex32>` already supplies the worker thread, so
   `SstvDecoder` is a push-based class like `StreamingPipeline`.
   - TelemetryPanel shows pass/transmitter nodes the same as telemetry, but with **SSTV
     image leaves** instead of frame leaves (one leaf per image).
   - Selecting an image leaf shows the **progressively-built image + a META section**
     in the right pane. The panel receives **scan lines one at a time**.

10. **Image lifecycle: coast through short fades.** Open an image leaf on VIS or
    sync-onset after idle. During a fade KF1 predicts line timing through the gap
    (missed syncs tolerated; affected lines held/blank) so one fade does not fragment the
    image. Finalize on: mode line-count reached, a new VIS/leader, or a long dropout
    (`T_gap` ~3-5 s, tunable). Partial image retained if the pass ends.

11. **Persistence: auto-save PNG + JSON sidecar** - controlled by Enabled option in Settings, (mirrors the .iq.wav + .json recording
    convention). On finalize (complete OR partial) write
    `<timestamp>_<sat>_<mode>.png` + `.png.json` (mode, sat name, norad id, transmitter uuid, start time, line count,
    slant/clock estimate, mean DC-offset diagnostic, SNR) to an SSTV folder under user
    data. Plus right-click Save As / Copy in the panel.

12. **Verification: synthetic encoder + real regression.** Build a small SSTV
    *modulator* (image -> subcarrier audio -> FM -> IQ, with optional noise/Doppler/slant)
    for closed-loop unit tests with exact ground truth (PSNR/pixel match), PLUS
    IQ recordings in `C:\Users\alsho\AppData\Roaming\Afreet\Products\SkyRoof\Recordings\SSTV`
    for regression decode. The encoder also drives the filter experiment (PSNR vs injected
    impairment).

    **Real captures on hand (mode from SkyRoof transmitter tag):**
    - `2026-07-01_11_02_25_Monitor-3.iq.wav` - Monitor-3 (57180), "Mode U - Robot 36 SSTV",
      tx `AUF5QGKakLf5FGfK5aCZfZ` -> **Robot36**
    - `2026-07-01_11_29_08_UTMN2.iq.wav` - UTMN2 (57203), "Mode U - Robot 36 SSTV",
      tx `JnBA9dq5WnWQugErAnPDcz` -> **Robot36**
    - `2026-06-30_22_36_37_UTMN2_Robot36.iq.wav` - UTMN2 (57203); sidecar tags the GMSK
      TLM tx but a mode-event switches to FM and the filename annotates **Robot36**
    - `2026-07-01_11_09_11_VIZARD-meteo.iq.wav` (+ short `..._11_15_34_...`) - VIZARD-meteo
      (57189), "SSTV - Robot 72", tx `kreKfmt4vs4n22TyJZ4Mft` -> **Robot72**
    - `2026-04-18..._UmKA-1.iq.wav`, `2026-04-19..._UmKA-1.iq.wav` - UmKA-1 (57172); sidecar
      tags GMSK 9k6 TLM but these files **alternate FSK telemetry and SSTV transmissions**.
      Valid SSTV anchors + a test of ignoring interleaved FSK bursts (mode via VIS/MHT,
      SSTV mode not yet confirmed - identify from the decode).

    Modes present so far are Robot36 and Robot72 (both supported); **no PD or RGB capture
    yet** - PD paths remain synthetic-only until a real capture appears.

13. **Streaming-first architecture, no whole-signal batch (DECISION 2026-07-01).** Every DSP stage must be
    reusable in a real-time streaming decoder from the start: sample-in / block-in, incremental state, no
    operation that requires the entire recording in hand. **Moderate bounded-block processing is fine** -
    block/overlap FFT, a rolling few-second sample buffer, a fixed-lag Kalman smoother over a window of
    lines. What is banned is *unbounded* batch: whole-file FFTs, whole-array prefix sums, whole-file peak
    scans, `GroupBy`/median over all detections. The P1-P4 code took batch shortcuts for expedience (§3 P1
    note, the tone-bank prefix sums, the mode detector's whole-file scan); **these must be refactored to
    streaming form before the P6 experimentation** (inventory in §6.0). Corollary accepted by the user: the
    displayed image **may be re-rendered from scratch** from the rolling buffer once the RLS/Kalman timing
    estimate converges (a few seconds / ~a dozen lines in) - we do not need the final geometry from sample 0,
    only bounded latency and eventual convergence.

---

## 2. Reference constants (web + mmsstv `sstv.cpp`)

- Subcarrier: 1500 Hz = black, 2300 Hz = white, 1900 Hz = center; 1200 Hz = horizontal
  sync (below black). Tone offset knob `g_dblToneOffset` in mmsstv; default 0.
- **VIS** (optional digital header): 300 ms leader 1900 Hz, 10 ms break 1200 Hz,
  300 ms 1900 Hz, then 10 bits x 30 ms: start 1200 Hz, 7 data bits LSB-first
  (1100 Hz = 1, 1300 Hz = 0), even parity, stop 1200 Hz.
  VIS codes: **Robot36 = 0x88, PD120 = 0x5f, PD180 = 0x60** (mmsstv-confirmed).
  16-bit extended VIS exists (mmsstv 0x23); standard 8-bit first, extended optional later.
- **PD** sync = 20.0 ms @ 1200 Hz + 2.08 ms porch @ 1500 Hz; YCrCb; two image lines
  share one Cr/Cb pair. PD120 colorscan 121.6 ms/line, 640x496, ~126 s.
  PD180 colorscan 183.04 ms/line, 640x496, ~187 s.
- **Robot36** = 320x240, ~36 s, YCrCb with Cr/Cb on alternating lines.

Exact per-mode timing constants to be transcribed into the mode table from
`sstv.cpp` and cross-checked against the N7CXI "Proposal for SSTV Mode Specifications"
(barberdsp Dayton paper) and classicsstv.com/pdmodes.php.

---

## 3. Signal chain (rates and tentative filters)

All processing stays at **48 kHz** (oversampled; no resampling). Input IQ is complex,
centered on the FM carrier (SkyRoof Doppler-tunes the SDR, so residual Doppler is small;
any residual becomes a DC offset that is removed downstream).

```
IQ 48k complex
  |-- Stage 1: complex channel FIR (LiquidFir)
  |       passband ~ Carson: 2*(dev + 2.3 kHz) + Doppler margin
  |       tentative cutoff +/-8 kHz (dev~5 kHz). improves FM threshold.
  v
  freqdem (outer FM demod, NativeLiquidDsp)  ->  real audio = f_doppler + k*audio(t)
  |
  |-- Stage 2: real audio bandpass (LiquidFir)
  |       passband ~1000-2400 Hz. removes DC/Doppler offset, hum, HF noise.
  |       (optional de-emphasis toggle here, OFF by default)
  v
  +--> Sync path: 1200 Hz complex correlator (L ~ mode sync duration)
  |       |corr| -> presence;  arg -> sub-sample phase (KF1);  df -> diagnostic ~0
  |
  +--> Brightness path:
          x e^{-j2pi 1900 t}  (nco_crcf)            -> complex baseband
          Stage 3 lowpass (LiquidFir)               -> anti-alias / noise reject
          instantaneous freq Im(conj(z[n-1])*z[n])  -> brightness(t) in Hz
          per-pixel matched integrator (boxcar = 1 pixel period from mode + KF1 clock)
                                                     -> one brightness sample per pixel
```

**Stage-3 design note:** rather than a single fixed lowpass, the brightness "filter" is
a **per-pixel matched integrator** whose width = one pixel period (derived from the mode
and the KF1 clock estimate). This auto-matches resolution-vs-noise to each mode. A modest
fixed lowpass precedes it only as anti-alias. Final cutoff/integrator widths are tuned in
the experiment phase.

**P1 implementation note (measured):** the brightness "downconvert-by-1900 + Stage-3 lowpass"
above is realized as an **exact FFT analytic signal** of the discriminator output (zero DC +
the negative half-spectrum, inverse-transform), then instantaneous frequency. The literal
real-mix path leaves a downconvert image at −3100..−4200 Hz whose rejection needs a lowpass so
long it smears Robot36's ~13-sample pixels (193-tap LPF measured 28.8 dB PSNR vs 35.6 dB for
the analytic path); the single-sideband analytic signal has no image and no in-band smear, so
the per-pixel integrator alone sets resolution. **UPDATE (P6a, 2026-07-01): the whole-signal FFT is gone.**
It is replaced by the **streaming** mix-to-baseband + complex low-pass (`BrightnessBwHz`, default 1800 Hz) +
instantaneous frequency. The earlier "mix+lowpass smears pixels" worry was for a *narrow* LPF; a wide
(±1800 Hz) cutoff keeps the mix image at −3800 Hz far outside the passband, so clean fidelity stays high
(Robot36 ~32 dB) **and** — the big win — band-limiting the video rejects the FM phase noise the full-band
analytic passed through: σ=0.05 (~23 dB SNR) went from ~11 dB PSNR to ~30 dB. Final bandwidth tuned in P6(c).
Separately, **Stage-1's cutoff must clear the full
FM Carson width** — clipping the constant-envelope tails makes the discriminator spike
(brightness std 252 Hz @ ±8 kHz vs 3.6 Hz @ ±15 kHz for dev 5 kHz); default ±15 kHz, tuned to
the real deviation in P6.

Brightness -> pixel value: linear map 1500 Hz -> 0, 2300 Hz -> 255, clamped.

---

## 4. Mode detection (VIS prior + MHT)

- **VIS decoder — normalized 2D zero-mean matched filter (computed from 5 scalars/slice).**
  The VIS header is a fixed 2D pattern on the (time, frequency) surface (300 ms @1900 leader,
  10 ms @1200 break, 300 ms @1900, then start @1200, 7 data bits @1100/1300 LSB-first,
  even-parity bit, stop @1200 — each 30 ms). Detect it as a **zero-mean matched filter on both
  axes**, realized **separably** (not a dense spectrogram × 2D kernel) so each element
  integrates over its own natural duration. The whole statistic is built per time-slice from
  just `{P_1100, P_1200, P_1300, P_1900, P_total}`:
  - **Frequency axis — sparse tone bank + total power.** Matched complex correlators
    (Goertzel) at exactly 1100 / 1200 / 1300 / 1900 Hz on the Stage-2-bandpassed audio, plus one
    **total in-band power** track (Σ|audio|², Parseval — no FFT). Because this is FM-on-FM,
    Doppler is a DC term the Stage-2 bandpass already removes (plan §1.6), so the tones are at
    **absolute** frequencies: no frequency-offset search, and the 910 ms header is short enough
    that slant time-scaling is negligible.
  - **Frequency-domain mean subtraction (rejects wrong-frequency energy).** Per slice, use the
    zero-mean-in-frequency numerator `e = P_expected_tone − α·P_total`, where
    `α = B_bin / B_band` is the tone bin's fractional share of the in-band width. A flat /
    broadband slice has `P_tone ≈ α·P_total` ⇒ `e ≈ 0`; a pure expected tone ⇒ `e ≈ P_tone`.
    This removes the broadband pedestal (the absence-of-energy-elsewhere check that full 2D
    correlation gets from normalization) using only the tone energies and the total — no
    spectrum. Note the identity `e / P_total = P_tone/P_total − α`: it is exactly the
    tone-dominance ratio recentered so flat/noise reads 0. Set `α` from the true broadband
    share (not `1/N_bins`) so the subtraction has division-strength.
  - **Time-domain mean subtraction (rejects right-frequency-wrong-time energy).** A zero-mean
    **time** template: mismatched slices score **negative**, not zero. Without it the statistic
    only *fails to reward* a mismatch, so a sustained carrier sitting on a template tone (e.g. a
    continuous 1900 Hz signal) still collects the whole 600 ms leader (~0.66 of a full match).
    The bipolar template keys on the pattern's **contrast over time**; a constant tone — no
    contrast — is driven down.
  - **Normalization + hard structural gates.** Normalize the accumulated score by the patch
    energy `Σ_t P_total` (amplitude-invariant, bounded), then require the leader ridge, the
    **10 ms break notch**, and a **parity-valid data decode** to each pass — not just a high
    global sum. The break (a mandatory dip to 1200 in the middle of the 1900 leader) is a
    feature a constant carrier physically cannot fake, and kills the sustained-carrier false
    positive outright.
  - **Bit decode.** After the correlation peak pins t0, read the 7 data + parity bits as
    **soft** decisions off the same tone-bank tracks at the known offsets; parity validates.
  The detector emits a **soft score** (the VIS-hypothesis likelihood), not a boolean: a valid
  VIS seeds the MHT with a high-prior hypothesis, an absent/weak one just lowers the prior.
- **Sync detector — separable zero-mean, unit-variance matched filter (DECISION 2026-07-01,
  supersedes the P3 coherence-centroid).** The horizontal sync is a rank-1 separable patch on the
  (time, frequency) surface: the outer product of a **frequency profile** (+1 at the 1200 Hz bin,
  zero-mean across frequency — the `P_1200 − α·P_total` broadband-rejection term, identical to the VIS
  filter above) and a **time profile** (+1 over the mode's pulse length, negative over the flanking
  spans, zero-mean across time), the whole normalized to unit variance so the score is amplitude-
  invariant and bounded. Realized separably: a short-window 1200 Hz coherence track `g(t)` (the freq
  axis, O(1) via `SstvToneBank` prefix sums) convolved with a bipolar boxcar time template of the
  mode's sync length (O(1) via prefix sums of `g`). **The time-axis zero-mean is what turns the
  flat-topped coherence into a triangular peak** — it supplies the time contrast the P3 window-length
  coherence lacked (any fully-inside constant-envelope window reads 0.5), so the onset comes from a
  parabolic peak-fit with no centroid heuristic. **Mode-specific pulse length doubles as a discriminant:**
  a small template bank (Robot 9 ms vs PD 20 ms) scores the sync-duration family for free, and its per-
  length peak train feeds the MHT below. Unifies P2's sync correlator + P3's centroid into one detector
  family with the VIS matched filter; the P3 KF1 tracker consumes its peaks in place of the centroid.
- **MHT:** maintain one hypothesis per candidate mode. Each predicts the next sync time
  from its line period and the KF1 clock; score by alignment of detected sync pulses with
  predictions (+ sync duration match: PD 20 ms vs Robot36's, from the matched-filter template bank).
  Prune low-scoring hypotheses; commit when one dominates by a margin. Can override a contradicted VIS.
- Commitment sets the mode -> fixes line period, color layout, image dimensions, pixel
  clock -> KF1 initial conditions.

### 4.1 Robust mode & timing recovery (streaming-first) — design for P6

The P4.5 real-capture probe reframed this as **detection + estimation in clutter**: the burst sits somewhere
in a long recording, real sync pulses are *weak* (matched-filter score ~0.24) and **not threshold-separable
from noise spikes (~0.18)**, and we must recover `(mode, burst-start t0, line-period P, per-line phase)` while
tolerating fades (missed syncs) and clutter (spurious spikes). The current first-peak-over-threshold logic is
a batch heuristic and fails on real data (it locked onto a 0.18 noise spike at 32.9 s instead of the real
burst near 196.9 s; carrier was centered at -32 Hz, so this is not an AFC problem).

> **NOTE (review 2026-07-01):** the ~0.24 real-sync operating point was measured **without the §3 Stage-2
> band-limit** (never implemented — retro item J): the coherence denominator includes the parabolic
> out-of-band FM noise (2.4–15 kHz carries ~240× the in-band noise energy), inflating it by noise the
> 1200 Hz numerator never sees. Implement the sync-path band-limit and **re-measure before** tuning
> thresholds or concluding on sync/noise separability — the design below stays valid either way, but its
> weak-pulse premise may relax considerably.
>
> **MEASURED (same day, `Real_SyncScoreProbe` on UTMN2_Robot36):** with the Stage-2 bandpass the real
> burst's per-pulse score rises **0.243 → 0.420 — the synthetic level** (J confirmed on real IQ; the
> front end no longer crushes real syncs, so per-line tracking inside a located burst now operates at
> synthetic margins). **But** band-limited noise is more tone-like within a 4 ms window, so clutter
> maxima rise too (0.406 over a 25 s noise region): single-pulse thresholds remain non-separable and
> everything below — soft-score train integration, triplet spawn, MHT — remains required as designed.

- **Integrate soft evidence, never hard-threshold single pulses.** No lone ~0.24 pulse is decisive, but the
  **coherent sum of the soft matched-filter score over ~240 predicted line slots** buries strong-but-isolated
  clutter. Weak-and-consistent >> strong-and-scattered. This is the core principle; it also means the front-
  end SNR (P6 filtering) and the recovery are partly separable - comb integration can localize the burst even
  while each sync is weak.
- **Batch view (for intuition only): a soft-score periodic comb / Hough.**
  `Score(mode, t0) = Σ_k matchedFilterScore(t0 + k·P_mode, pulseLen_mode)`, maximized over the discrete mode
  periods and t0. This is the ML detector for the whole periodic train - no per-pulse sync/noise decision, no
  detection threshold. **But a whole-file comb is banned by §1.13.**
- **Streaming realization (what we actually build): sparse, event-driven pulse-train hypotheses = the
  sequential MHT.** This is the streaming-correct form of the user's "spawn a hypothesis per detected pulse",
  and the proven form is the Hopper reference (§6.1): rather than a dense (mode × phase-bin) accumulator grid,
  keep a small list of **pulse-train objects**, each a hypothesis carrying `{format, RLS period+phase, freq,
  state, pulses[]}`. On each detected pulse: (1) try to **associate** it with an existing candidate/active
  train (frequency gate + RLS time-tolerance gate); (2) else **spawn** a new train only when this pulse plus
  two buffered past pulses form a **period-consistent 3-pulse triplet** for some format (spacing within
  ±3 % of that format's nominal, 3rd pulse at ~2× the 1st→2nd gap, matching frequency). Three collinear pulses
  is a mini-comb that clutter almost never fakes, so this answers "spawn on every pulse?" - **no**, spawn on a
  period-consistent triplet, which is both cheaper than a dense bank and self-gating against noise spikes.
  Manage the list with: **candidate → active → retired** states, **N-of-M promotion** (enough pulses within a
  promote timeout), **back-fill** buffered past pulses on promotion, **timeout pruning**, and per line-block
  pick the **best train by smoothed sync power with hysteresis** (switch only if >1.5× the incumbent). A valid
  **VIS seeds a high-prior train** that promotes on ~3 confirming pulses.
- **KF1/RLS timing** — port Hopper's `TSstvSyncRegressor` (§6.1): a 2-state **information-filter linear
  regression on `(period, phase)` against pulse-NUMBER** (`pulseTime ≈ period·pulseNo + phase`, 1 % prior on
  period). Regressing on pulse *number* makes **missed pulses free** (skip an index - no coasting bookkeeping),
  and `CorrFactor = period/nominal` is the slant applied directly in reconstruction. Association uses the
  regression's growing time-tolerance as the **innovation gate** (clutter outside the gate is ignored, not
  fitted). Optional **fixed-lag RTS smoother** over a bounded window of lines (allowed block processing, §1.13)
  refines timing further on weak data.
- **Convergence & re-render (§1.13).** Buffer the demod/video in a rolling few-second window; the extractor
  marks a **dirty-block** whenever a train is promoted / revised / retired, and only those lines are
  re-rendered (`RebuildImage` for a full pass). Bounded latency, eventual convergence - not a whole-file pass.
- **Concurrency we DO need (vs Hopper's we do not):** multiple **format** hypotheses for one signal, and
  **sequential/mixed bursts** over a pass (UmKA-1 alternates FSK+SSTV; a pass may carry several images) -
  handled natively by the train list + best-per-block selection. We do **not** need Hopper's **multi-signal-
  at-nearby-frequencies** tracking or its **AFC bin bank** (§6.1): those were for SSB on the crowded HF bands;
  a satellite pass is a single, Doppler-centered FM signal (measured carrier −32 Hz).

---

## 5. SkyRoof integration

- `TelemetryDecocder` (`SkyRoof/DSP/TelemetryDecocder.cs`): add `SstvDecoder? Sstv`,
  build `Sstv` vs `Pipeline` from `signalParams.Modulation`; `Process()` and `Dispose()`
  branch accordingly.
- Add `Modulation.SSTV` to TelemetryPanel's decodable set (currently FSK/GFSK/GMSK/BPSK)
  and to `SignalParamsResolver` so SSTV transmitters resolve to `SignalParams` with
  `Modulation.SSTV`.
- `SstvDecoder` events: `ModeDetected(mode)`, `ImageStarted(meta)`,
  `ScanLineDecoded(lineIndex, pixels)`, `ImageCompleted(image, meta)`. Panel subscribes
  the way it subscribes to `FrameDecoded`/`BurstDecoded`.
- TelemetryPanel: image leaves under the pass node; right pane = progressive `PictureBox`
  + META; auto-save PNG + JSON sidecar on finalize; Save As / Copy context menu.

### 5.1 Burst recognition & mixed-mode dispatch (no spectrum-shape template)

SSTV is recognized in the **demod domain, not the RF spectrum** - there is deliberately
**no SSTV PSD matched-filter analogous to `ModulationTemplate`** (the FSK/GMSK/PSK
shape templates used by `CfoEstimator`/`StreamingPipeline`). Rationale: SSTV here is
FM-on-FM, so the RF-level PSD is approximately the histogram of the instantaneous
frequency `f_doppler + k*audio(t)` - a bumpy, **image-content-dependent** plateau, not a
stable parametric shape. A template for it would be a loose "wide flat lobe of Carson
width" that discriminates poorly. The reliable SSTV signatures live elsewhere:

- **Continuous FM demodulation** runs over the stream; SSTV appears as a long,
  uninterrupted transmission (tens of seconds: Robot36 ~36 s, Robot72 ~72 s), unlike the
  short FSK telemetry packets.
- **VIS + sync-pulse detection** (the §4 correlators) positively identify an SSTV
  transmission: the VIS leader (1900 Hz + 1200 Hz break) and/or the periodic 1200 Hz sync
  cadence are things FSK simply does not produce. This doubles as the mode detector.

So spectrum/bandwidth is used only as a **coarse pre-gate** (via the existing energy
`BurstDetector`: a wide-occupancy, long-duration span is an SSTV candidate; short narrow
bursts go to the FSK path), and the VIS/sync test in the demod domain makes the final
call. No new shape-matching stage is built for SSTV.

**Mixed-mode consequence (UmKA-1):** because one transmitter can alternate FSK and SSTV
within a pass, the dispatcher can no longer be a static per-transmitter switch on
`SignalParams.Modulation`. Options (decide in implementation): route stream segments to
FSK vs SSTV by the pre-gate + VIS/sync confirmation above, or run both decoders
concurrently and let each self-gate (FSK bursts fail the SSTV VIS/sync test and are
ignored by `SstvDecoder`; SSTV segments present no valid FSK frames).

---

## 6. Experimentation phase (requested)

### 6.0 Streaming-first refactor (do FIRST, before any tuning) — batch-removal inventory

Per §1.13, the batch shortcuts taken in P1-P4 must be converted to streaming (sample/block-in, bounded
state) **before** the filter sweep and before implementing §4.1 recovery. **Judgment call (agreed
2026-07-01): the remaining items below are folded into the P6(b) Hopper port** — the port replaces them
wholesale, so converting them to streaming form first would be throwaway work; they are deleted, not
refactored, when the extractor lands. Current batch operations to remove (all in `VE3NEA.Sstv`):

- ~~**`SstvDecoder.AnalyticSignal` — whole-signal FFT** (the brightness path).~~ **DONE (P6a):** replaced by
  the streaming NCO mix-to-baseband + complex BlackmanSinc low-pass (`BrightnessBwHz`) + instantaneous
  frequency. Batch FFT removed; big noise-robustness gain (§3 P1 note).
- **`SstvToneBank` — whole-array prefix sums** of the mixed tone + energy. Replace with **recursive windowed
  tone power** (Goertzel / sliding-DFT / single-pole complex resonator) producing 1100/1200/1300/1900 Hz
  power incrementally over a sliding window.
- **`SstvSyncFilter` — whole-array `g[]` + prefix sum.** Compute `g(t)` from the streaming tone tracks; the
  bipolar boxcar time template is a short FIR over recent `g` (streamable with a small ring buffer).
- **`SstvModeDetector` — whole-file peak scan + `GroupBy` + median.** Replace with the §4.1 streaming
  accumulator bank (per mode × phase soft-score, gated/pruned).
- **`SstvSyncTracker`** already uses a streaming Kalman, but is driven from a whole-file onset array; refactor
  to consume per-block sync measurements. Add the fixed-lag RTS smoother (bounded block, allowed).
- **`ChannelFilter`/`Discriminate`** are inherently streamable (FIR + 1-sample state); only the whole-array
  *return* shape changes to block I/O.
- **`SstvVisDetector`** already searches a bounded (~2 s) window; re-express its tone reads on the streaming
  tone tracks so it shares machinery. The batch closed-loop encoder and PSNR harness stay batch (test-only).

Additional requirements on the streaming replacements (review 2026-07-01):

- **Long-run numerical stability (retro N).** Streaming accumulators must survive a 10+ min pass (~3·10⁷
  samples): oscillator phases via a **wrapped-phase recurrence**, never `cos(w·i)` with an unbounded absolute
  index (argument-reduction precision degrades after ~10⁷ samples), and add/subtract running sums must be
  **periodically re-anchored** (or block-re-accumulated) so floating-point drift cannot grow without bound.
  The batch prefix sums quietly masked this; no short unit test will catch it.
- **The §3 Stage-2 audio bandpass is specified but was never implemented** (retro J) — the tone banks and
  coherence currently run on the raw discriminator output, so the energy normalizer is inflated by parabolic
  out-of-band FM noise. Implement it (or Hopper's ±120 Hz sync-band extraction) as part of this refactor, and
  make **all** sync/VIS statistics share the same DC/band handling (today `SstvToneBank` removes the mean,
  `SstvPulseDetector` does not — a silent batch-vs-streaming divergence, the A/B failure pattern again).

Target shape: a push-based `SstvDecoder` (like `StreamingPipeline`) fed IQ blocks, emitting scan lines /
mode events incrementally, with a rolling few-second buffer for the §1.13 re-render on convergence.

### 6.1 Reference blueprint: the Hopper decoder (port map)

`C:\Proj\DSP\Hopper` is our earlier **streaming** FM-SSTV decoder (Delphi). It already implements - and thus
proves - the exact streaming + MHT architecture §1.13/§4.1/§6.0 call for, so P6(a)/(b) is largely a **port**,
not a new design. Structure: `TSstvReceiver.Process(block)` -> `TFrontEnd` -> `TPulseTrainExtractor` (the MHT)
-> `TSstvDemodulator`, with a rolling buffer (`Dump`/`SamplesDumped`) and incremental re-render. Port map to
`VE3NEA.Sstv`:

| Hopper unit | Target module | Idea to port |
| --- | --- | --- |
| `TFrontEnd` | streaming front-end | **Overlap-save block FFT** once per block, then frequency-domain multiply to extract the **1200 Hz sync band (±120 Hz)** and the **FM-video band (±500 Hz)**; IFFT back. One structure replaces the batch `AnalyticSignal` **and** the tone banks. Band-limiting the video to ±500 Hz **before** the discriminator is the noise-reject filter we lack (our 37→23 dB cliff). *(UPDATE 2026-07-01: the video band-limit now exists as the streaming `BrightnessBwHz` LPF (P6a) — only its width, ±1800 vs Hopper's ±500, remains a P6(c) tuning item. The **sync-band** extraction is the part still missing — retro J.)* |
| `TSyncTracker` | sync detector (replaces `SstvSyncFilter`) | Streaming pulse matched filter = **short moving-avg − long moving-avg** (a zero-mean bipolar pulse template as two running means) + a **morphological min/max noise-floor** estimator; `KeyOn = Pwr > 1.55·Noise ∧ Pwr > 0.5·Envelope`. Amplitude-/noise-normalized, spike-robust. |
| `TPulseTrainExtractor` + `TPulseTrain` | `SstvModeDetector` + tracker (the MHT) | Sparse per-format **pulse-train hypotheses**; **spawn from a period-consistent 3-pulse triplet**; associate-or-spawn per pulse with frequency + RLS-tolerance gates; candidate→active→retired, N-of-M promote, back-fill on promote, timeout prune; **best train per block with 1.5× hysteresis**; **VIS-seeded high-prior** train (`TVisPulseTrain`). |
| `TSstvSyncRegressor` | KF1 / RLS | 2-state **information-filter regression on `(period, phase)` vs pulse-NUMBER**; missed pulses free; `CorrFactor` = slant. |
| `TSstvDemodulator` | reconstruction | Per-line demod on the RLS grid with `TimeScale = samplesPerMs·CorrFactor` (clock-corrected pixel width); **2-point interpolated instantaneous-frequency** brightness; **incremental** `DemodulateLine` on dirty lines + full `RebuildImage`; emit a complete frame when a train retires with ≥ 0.33·`Lines`; per-pixel **overshoot/confidence** in the alpha channel. |
| `Dump` / `DirtyBlock` | rolling buffer + re-render | Bounded-latency streaming with incremental re-extraction (§1.13). |

**Deliberately NOT ported (Hopper design choices specific to its use case):** the **AFC bin bank** and
**multi-signal-at-nearby-frequencies** tracking. Hopper decoded **SSB** SSTV on the crowded HF bands, where a
tuning error shifts the whole audio spectrum (hence AFC) and several signals sit near one another (hence
per-frequency concurrency). Our case is **FM-on-FM**, one **Doppler-centered** satellite signal (§1.6;
measured carrier −32 Hz), so we extract sync/video at a **single fixed frequency** - which collapses Hopper's
per-AFC-bin filter bank to a single band extraction and drops the whole frequency-diversity dimension. We keep
only the concurrency we need: multiple **format** hypotheses and **sequential/mixed bursts** in a pass (§4.1).

**Mine before P6(c) filtering:** `Hopper\Experiments\FmNoise\` empirically characterizes FM click/spike noise
vs signal magnitude (`Spikes.txt`, `DevVsMag.txt`, `NsToSig.txt`) - directly relevant to our brightness noise
fragility and any impulse-noise blanking.

Real SSTV IQ recordings now exist in the SSTV recordings folder (see §12), with the
mode confirmed by the SkyRoof transmitter tag. The primary anchors are the **Robot36**
captures (Monitor-3, UTMN2) and the **Robot72** capture (VIZARD-meteo) - both in the
supported YCrCb family, so a real decode is reachable in P6 without waiting for more
data. The UmKA-1 captures **alternate FSK telemetry and SSTV transmissions** in one file
(the sidecar tags the GMSK TLM tx), so they are also valid SSTV anchors and additionally
exercise the mid-pass transition: the decoder must ignore the FSK bursts and open/close
image leaves around the SSTV segments (mode from VIS/MHT, not the sidecar tag). Goals:

1. **Filter bandwidths:** sweep Stage-1 channel BW, Stage-2 audio BP edges, Stage-3
   brightness lowpass / pixel-integrator width. Metric: PSNR vs the synthetic ground
   truth (encoder + injected noise/Doppler/slant matched to the real-capture conditions),
   plus reference-free metrics on the real Robot36/Robot72 decodes (sync-pulse SNR,
   line-to-line brightness consistency, edge sharpness).
2. **De-emphasis on/off** and time constant: does it improve real-IQ PSNR / reference-free
   metrics, or only color noise? Set the default from the result.
3. **Noise shaping:** evaluate whether any pre/de-emphasis or shaped brightness filtering
   improves perceived/measured image SNR at the FM threshold.
4. Lock the chosen defaults into the mode/chain config.

---

## 7. Phasing

- **P0** Assembly scaffold; data-driven mode table; synthetic SSTV encoder + closed-loop
  test harness.
- **P1** Front-end (channel FIR, freqdem, downconvert, brightness) on a clean synthetic
  signal at fixed timing (no Kalman) -> decode a known image -> PSNR test.
- **P2** Sync correlator + VIS decoder + per-mode color layouts.
- **P3** KF1 sync/slant tracking + coast-through-fades.
- **P4** MHT mode inference (VIS prior). Refactor sync detection into the separable zero-mean,
  unit-variance matched-filter template bank (mode-specific pulse length, §4), superseding the P3
  coherence-centroid; build the MHT (line-period + sync-duration scoring) on it.
- **P4.5 (done 2026-07-01)** End-to-end decode-to-PNG harness. Run the full chain
  (`DetectMode` -> `Decode` -> `RgbImage` -> **PNG** + JSON sidecar) on **both** synthetic signals and the
  **real `.iq.wav` captures** (§12), writing images to disk for visual inspection. Makes the engine work end
  to end and produce images as early as practical — validating real-signal decoding (and exposing the P6
  filtering work with concrete pictures) well before the SkyRoof UI exists. Filter fine-tuning stays in P6;
  this is just the image-out path + a runnable harness.
- **P6** Largely a **port of the Hopper reference decoder** (§6.1), in order: (a) **Streaming-first refactor**
  — remove all batch operations (§6.0) so every stage is streaming-reusable (§1.13); the overlap-save FFT
  filter-bank front-end + `TSyncTracker`-style detector replace `AnalyticSignal` and the tone banks;
  (b) **robust mode+timing recovery** — the sparse pulse-train MHT (spawn on a period-consistent triplet) +
  pulse-number RLS regressor + best-train-per-block, minus Hopper's AFC/multi-frequency machinery (§4.1/§6.1);
  (c) experimentation on the real Robot36/Robot72 IQ (pre-discrimination video band-limit, de-emphasis,
  noise-shaping) to lock defaults. Do (a) and (b) before (c). **Work off the alignment-retro items (§9)
  inside these steps:** J, A, C, N, K, L, M and most of P are done (§9 "Done"); the open remainder — G/H
  (this port), D (thresholds), E (freq gate), F (CorrFactor pixel clock), I (constants), O (freqdem +
  single discriminator pass) — folds into (b)/(c) per §9 "Open".
- **P7** Real regression corpus (Robot36 + Robot72 captures) + docs.
- **P8 (last)** SkyRoof integration (dispatcher, image leaves, progressive render, META, auto-save) — see
  §5. Deliberately the final phase: the decoder must decode real captures to PNG standalone (P6/P7) before
  it is wired into the UI.

---

## 8. Open items / to confirm during implementation

- First real regression modes confirmed: **Robot36** (Monitor-3, UTMN2) and **Robot72**
  (VIZARD-meteo). UmKA-1's captures alternate FSK telemetry and SSTV in one file - usable
  as SSTV anchors, but the decoder must ride through the FSK bursts; UmKA-1's SSTV mode is
  not yet confirmed (read it off the decode).
- Exact per-mode timing constants transcribed + cross-checked.
- FM deviation assumption for satellite SSTV (drives Stage-1 BW); make configurable.
- Extended 16-bit VIS support (defer unless a target sat needs it).
- Image dimensions / aspect handling per layout; partial-line rendering policy.
- Mixed-mode dispatch (§5.1): segment routing vs concurrent FSK+SSTV decoders - pick one
  during SkyRoof integration.
- **Real-capture findings (P4.5 harness, 2026-07-01)** — synthetic decode-to-PNG works end to end
  (DetectMode -> Decode -> PNG, correct images for Robot36/Robot72/PD). Real `.iq.wav` captures do **not**
  yet decode: on UTMN2_Robot36 the carrier is centered (mean inst-freq −32 Hz, **confirms the §1.6 no-AFC
  decision on real data**), but the strongest 1200 Hz sync anywhere is only ~0.24 (vs ~0.40 synthetic) and
  the naive "first peak > threshold" burst finder locks onto a 0.18 noise peak instead of the real burst.
  Two P6/P7 items: **(a) burst localization** — replace first-threshold with a periodic comb/Hough over the
  sync train (align a mode's period grid against detected peaks; the densest run is the burst); **(b) front-
  end conditioning** — the weak real sync/brightness SNR needs the §3 Stage-3 brightness LPF + a channel-BW
  sweep tuned to the real deviation to lift it before localization and imaging are reliable.

Review addenda (2026-07-01, item letters per §9):

- ~~**Stage-2 audio bandpass (§3) is specified but unimplemented** (retro J)~~ **DONE 2026-07-01** as
  `SstvDecoder.SyncAudio` — it was indeed the weak-real-sync culprit (0.243 → 0.420 on real IQ, §9).
- **Front-end fork to resolve:** the P6a streaming NCO-mix + FIR brightness (done, §6.0) and the §6.1
  Hopper overlap-save FFT bank ("replaces `AnalyticSignal` **and** the tone banks") are competing designs.
  Recommendation: keep NCO+FIR for brightness and port only the sync-band extraction (≈ the Stage-2 item
  above); decide explicitly and record it here so the P6 port does not re-litigate it.
- ~~**Robot36 chroma parity must come from the separator tone**~~ **DONE 2026-07-01** (retro M, §9) —
  read from the 1500/2300 Hz separator with parity fallback; a mid-image lock no longer swaps red/blue.
- **Unify the dropout knobs:** §1.10 `T_gap` (3–5 s) and the pulse-train `RetireSeconds` (6 s) are the same
  physical event — make them one tunable (lands with the extractor, §9 G/H).
- **Encoder impairment fidelity for P6(c):** ~~fix the per-segment slant rounding (retro K)~~ **DONE
  2026-07-01** (continuous scaled-time cursor, §9); still open — add a `DopplerRateHzPerSec` knob: constant
  Doppler is removed by design (§1.6); the drifting DC ramp is what a real pass actually produces, and it
  is what stresses the brightness LPF and the tone banks' constant-mean assumption.
- **Discriminator:** wire the wrapped `freqdem` native per §1.2 or record the deviation (retro O), and share
  one discriminator pass between `DetectMode` and `Decode` (currently each rediscriminates the full capture).
- Phasing (§7) has no P5 — renumber or mark the gap intentional.

---

## 9. Alignment retro — consolidated status (retro file retired 2026-07-02)

A 2026-07-01 design/implementation alignment review (originally `sstv_alignment_retro.md`, items A–P;
file deleted, everything still useful lives here) found where the P6(b) streaming port had diverged from
the design. The recurring failure pattern — worth remembering — was **the streaming piece silently dropping
a property the batch piece had** (the A/B divergence below); when porting, diff the *statistic*, not just
the code shape.

### Done (validated 2026-07-01; suite 80 pass / 2 manual-skip; VE3NEA.Tlm-era commits 84c689f…6038b17)

- **J — Stage-2 sync-path bandpass** (`SstvDecoder.SyncAudio`, 1000–2400 Hz cosine-modulated BlackmanSinc,
  SIMD LiquidFir) feeds every sync/VIS/mode/tracker statistic. Validated on real IQ (§4.1 MEASURED note):
  real burst score 0.243 → **0.420 = the synthetic level**; but band-limited noise coherence rises too
  (clutter max 0.406), so single-pulse thresholds remain non-separable and the §4.1 train integration
  stays required.
- **A/C/N — `SstvPulseDetector` is now the full separable zero-mean 2D matched filter**: bipolar time
  template (a sustained 1200 Hz carrier's interior emits nothing — tested), onset = argmax (the P3 centroid
  heuristic is gone), one detector instance per sync-duration family with `DurMs` carried on each pulse
  (the Robot-vs-PD family discriminant for the MHT), mixer recurrence + ring-re-anchored running sums for
  multi-minute numeric stability.
- **B — root-caused lesson (keep):** the sync statistic must be **energy-normalized coherence, never raw
  band power**. FM is constant-envelope: measured window energy at a real sync vs a separator step differed
  by 0.2 % while coherence differed **250:1** (0.493 vs 0.002); a raw-power detector fired 481 pulses vs
  the true 240. Bandpass skirts leak broadband clicks — normalization, not filtering, is what rejects them.
- **K** — the encoder keeps a continuous scaled-time cursor across segments, so tens-of-ppm slants render
  faithfully (per-segment rounding used to quantize slant to zero below ~118 ppm). **L** — the P4.5 harness
  decodes at the detected onset with a PSNR gate (it used to re-acquire inside the slice and mislock on the
  VIS start bit). **M** — Robot36 chroma identity is read from the 1500/2300 Hz separator tone with parity
  fallback (mid-image lock test). **P (part)** — candidate promote-age bound + duplicate line-slot rejection
  in `SstvPulseTrain`.

### Open (work off during the rest of P6)

- **G/H — the extractor/MHT port, wiring, and batch-code deletion** — the next piece; concrete steps in
  `## Current Status`. Port requirements folded in from the review: a **minimum KeyOn width / noise-floor
  gate** (Hopper's `TSyncTracker` has one) so a single-sample blip above threshold cannot become a pulse,
  and the **§1.10 `T_gap` = `SstvPulseTrain.RetireSeconds` unification** (one tunable for "the transmission
  ended").
- **D — detection thresholds are hardcoded near the real-signal margin** (`ScoreThreshold` 0.18 in
  `SstvPulseDetector`; ~0.25 in the batch detectors until they are deleted). Tune on real IQ in P6(c) after
  the extractor lands (A+J already raised the margin); consider a relative/adaptive threshold rather than a
  fixed constant.
- **E — per-pulse frequency is a constant 1200 Hz**, so `SstvPulseTrain`'s ±150 Hz frequency gate is inert
  (Hopper used it for clutter rejection on crowded HF). Either estimate per-pulse frequency (baseband phase
  slope) or consciously drop the gate for the single-frequency FM case and simplify the train.
- **F — reconstruction ignores `CorrFactor` for the intra-line pixel clock**: segment widths are computed
  as nominal `round(ms/1000·fs)`; only the line onsets are slant-corrected. Scale segment/pixel widths by
  the winning train's `CorrFactor` when the extractor lands (Hopper: `TimeScale = samplesPerMs·CorrFactor`).
- **I — duplicated magic constants** (coherence window, thresholds, sync/freq tolerances, min-spacing
  re-declared per detector) — centralize while deleting the batch detectors.
- **O — hand-rolled `Math.Atan2` discriminator, run twice per capture** (`DetectMode` and `Decode` each
  rediscriminate the full IQ — multi-hundred-MB real captures). Wire the wrapped `freqdem` native (§1.2) or
  record the deviation and why; share one discriminator pass between detection and decode.
