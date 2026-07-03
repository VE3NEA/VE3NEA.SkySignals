# SSTV Decoder Plan (VE3NEA.Sstv)

Status: design agreed via grill-me interview 2026-06-29. Not yet implemented.

## Current Status

**Phase: train-accuracy overhaul DONE (2026-07-03) — the RLS gate mis-scaling root-caused (Hopper sample
counts not rescaled to 48 kHz), duplicate trains and the Robot72 harmonic gone, the no-overlap spawn rule
in: 18 of 19 ground-truth transmissions detected with exactly one train each, 0 false. Next: the
cross-pulse soft-comb, then the remaining P6(c) experiments, then P7.** The code lives in the
`VE3NEA.SkySignals` repo, branch `sstv`, project **`VE3NEA.SkySSTV`**. Suite: **91 pass / 10 manual-skip**.

What landed (the Hopper port, plan §4.1/§6.1):

- `SstvPulseTrain` extended to Hopper's `TPulseTrain` (pulse storage, ±4-slot `GetPower` smoothing with
  edge/spike rejection, `AddOldPulses` back-fill + regressor rebuild on promote, `IsRetiredAt` with the
  weak-tail hold, `RevisionDue` dirty marks, sync-duration **family gate on association**) + a
  `SstvVisPulseTrain` (VIS-seeded high-prior train: promotes on 3 confirming pulses, anchor-gated first
  pulse, triplet adoption via grid extrapolation to the VIS anchor).
- **`SstvPulseTrainExtractor`** — associate-first (which also kills the Robot36→Robot72 half-rate harmonic
  spawn), per-mode triplet spawn gated by the pulse's `DurMs` family (±3 % period gates separate every PD
  pair), candidate→active→retired lifecycle, best-train-per-block with 1.5× hysteresis + incumbent
  preference, dirty-block scan-line re-extraction, bounded pulse buffer (8 s tail). 6 unit tests (clean /
  clutter / fade / sequential bursts / VIS seed / off-anchor VIS).
- Wired in: `DetectMode` = VIS prior + extractor's `BestTrain` (whole-file streaming scan — this is what
  found the real bursts); `Decode` acquires from VIS or the winning train and lays lines on the train's
  **RLS grid** (missed pulses coast free); **retro F done** — `CorrFactor` scales the intra-line pixel
  clock in both reconstructions. The driver pads the lead-in (a sync at sample 0 detects) and re-orders the
  two family detectors' differing emission latencies.
- **Retro G done:** `SstvSyncFilter`, `SstvSyncCorrelator`, `SstvSyncTracker` deleted; `SstvModeDetector`
  rewritten on the extractor. `SstvToneBank` stays (VIS-only, bounded ~2 s window = allowed §1.13 block
  processing). `SstvPulseDetector` gained a `MaxScore` probe for the score measurements.

**Real-capture results (Real_DecodesToPng, 2026-07-02 — the first real PNGs):** 6 of 9 captures localize
and decode. **Monitor-3: readable text card** (RA3PPY, "status: Operational", launch date); **UTMN2 (both
passes): the SPUTNIX winged-satellite image with readable banner**; geometry is straight — RLS slant
correction works on real signals. All via MHT (`fromVis=false`), measured period exactly 150.0 ms.
Remaining quality gap: heavy speckle noise = the P6(c) front-end fidelity work.

**Reference decodes (2026-07-02): `C:\Ham\RX-SSTV-2\History` holds RXSSTV's decodes of the same
transmissions** (match by timestamp: capture filename = recording start, RXSSTV filename = image
completion). Confirmed same-image pairs: Monitor-3 text card ends 11:07:49; UTMN2 SPUTNIX ends 11:35:09.
Findings from the comparison:

- **VIZARD-meteo is Robot36 confirmed** — RXSSTV also decoded it as Robot36 (the "Robot 72" transmitter tag
  is wrong); its transmissions were genuinely weak (RXSSTV's decodes are mostly noise too).
- **UmKA-1 12:19:50 explained**: RXSSTV's image completed at 12:19:42 — the transmission ended just before
  our recording started; our noise-only decode is correct behavior.
- **The 22:36 UTMN2 capture holds ≥2 bursts**: ours decoded the weaker one at +30 s; RXSSTV's clean
  SPUTNIX (22:40:05) is the stronger ~+197 s burst. The harness currently decodes only `BestTrain` —
  **decode one image per train** (the P8 leaf-per-image behavior) so no burst is dropped.
- **Quality target for P6(c)**: RXSSTV is visibly cleaner on smooth areas (less RGB speckle) but shows torn
  rows where it lost sync; our geometry is straighter. So the timing chain is ahead, the video filtering is
  behind — the gap is front-end fidelity, exactly §6 items 1–3.

**First P6(c) data point (Real_FilterSweepProbe, 2026-07-02, on the matched Monitor-3 text card):**
sweeping `ChannelBwHz` × `BrightnessBwHz` over the real burst, the defaults (15000 / 1800) leave heavy
speckle; **chan 4000–5000 + video 500–650 gives an essentially clean, fully readable image that beats the
RXSSTV reference** ("Launch site: Leninabad, USSR" crisp, the bottom flag row visible). Brackets: chan 3000
clips the FM tails (speckle returns — the knee), video 350 over-smooths. Hopper's ±500 Hz video band-limit
is vindicated on real IQ. **Implication: the real audio deviation is far below the synthetic encoder's
5 kHz** — the P6(c) pass must measure/settle the real deviation, align the encoder to it, re-baseline the
synthetic PSNR bars, and then lock the new defaults (a naive default switch would clip the current
synthetic signal, whose Carson width is ~±7.3 kHz).

**P6(c) first pass DONE (2026-07-02, same day): deviation measured, defaults locked, continuous VIS.**
Suite: **86 pass / 3 manual-skip.**

- **Real deviation measured ≈ 3.3 kHz** (`Real_DeviationProbe`: Monitor-3 3310, UTMN2 3303/3368 Hz; weaker
  bursts read higher from noise inflation; corroborated by the FskDemod spectrogram — occupied width
  ≈ ±5 kHz, carrier centered). Encoder default `DeviationHz` = 3300 so the synthetic loop exercises the
  real Carson width.
- **Defaults locked from the sweep + measurement:** `ChannelBwHz` 15000 → **6000** (Carson ≈ ±5.6 kHz with
  margin), `BrightnessBwHz` 1800 → **600**. Cost/benefit on synthetic Robot36: clean ceiling ~32 → 27.2 dB
  (video-filter smear on 0.275 ms pixels) but the noise curve is now nearly **flat to σ=0.1** (27.0 dB
  where the old floor was 20). PSNR floors re-baselined accordingly (Robot72 barely moved — longer pixels).
  A possible later refinement: SNR-adaptive video bandwidth (wide when clean, narrow when noisy).
- **Continuous VIS (follow-up 3 done):** `SstvVisDetector.DetectAll` tiles bounded ~3 s windows over the
  whole stream (allowed §1.13 block processing); every hit seeds a high-prior train; `AcquireSearchSamples`
  removed. A corrupted VIS self-arbitrates (its train starves) — the explicit override logic is gone.
- **Real results with the new defaults: 8 of 9 captures decode (was 6).** Monitor-3 text card essentially
  **clean — beats the RXSSTV reference**; UTMN2 22:36 now selects the stronger ~183 s burst (near-clean
  SPUTNIX); **UmKA-1 anchors `fromVis=true`** — the continuous scan found its header ~297 s in — showing a
  Cyrillic card RXSSTV never captured. Remaining defect: 12_37_50 Monitor-3 decodes noise from a weak
  train (score 0.30) — the retro-D threshold case. *(Correction 2026-07-02 late: NOT noise — the FskDemod
  spectrogram shows a real SSTV burst at ~157 s in that capture; see the retro D/E/O entry.)*

**Multi-image decode DONE (2026-07-02):** the harness decodes one image per promoted train (≥¼ of the
mode's lines claimed) instead of only `BestTrain` — **18 images from 8 of 9 captures**. Both UTMN2 22:36
bursts decode nearly clean (the 27 s copy is the better one — its earlier bad decode was the old wide
filters, not the burst); UTMN2 11:29 yields five bursts whose ~13 s pairs match RXSSTV's paired history
entries; UmKA-1 shows a second burst at ~318 s after its VIS-anchored ~297 s one. *(Correction
2026-07-02 late: the "~318 s second burst" was an artifact of the VIS triplet-adoption hijack — those
pulses belong to the VIS train; the real second image sits at ~133 s. See the retro D/E/O entry.)*

**Investigated (2026-07-02): why `2026-04-18_12_36_09_UmKA-1.iq.wav` decodes nothing.** The user confirmed
(spectrogram + audio) an SSTV signal is genuinely present from 0 s, already in progress at recording start,
ending ~24 s in. Diagnosis (ad hoc probe, not kept): this is a **low-SNR pass across the board, not an
SSTV-specific detector bug** — `FskDemod` finds 6 GMSK telemetry bursts on the same pass but **validates
zero frames**, an independent decoder failing the same way. On the SSTV side: raw IQ RMS is *higher* than
Monitor-3's (0.0041 vs 0.0013–0.0018, which decodes cleanly), so it is not simply too quiet; but the FM
discriminator saturates to near ±24 kHz (full π-radian phase jumps — classic FM-threshold click noise) on
almost every 0.5 s block for the whole 24 s span, and the sync-pulse detector finds only 11 scattered
sub-threshold blips (max score 0.221, barely above the 0.18 gate) over 29 s with **spacings that never once
land near Robot36's 150 ms line period** (1000, 2000, 0, 0, 1000, 8000, 4000, 1000, 1000 ms) — with either
the current filters (chan 6 kHz/video 600 Hz) or the old wide ones (15 kHz/1800 Hz, which found only 1
pulse). No three pulses ever form a period-consistent triplet, so the extractor has no evidence to spawn a
train from at all — not a promotion-threshold problem, an absence-of-coherent-evidence problem. A human
(eye or ear) tolerates far weaker, more broken evidence than a fixed 4 ms coherent matched filter; lowering
`ScoreThreshold` would not fix this specific file (there is no 150 ms periodicity at any threshold) and
would invite spurious triplets elsewhere in the corpus. What would actually help is longer coherent
integration than a single 4 ms window (e.g. a longer matched-filter window, or accumulating soft evidence
across several line periods before requiring a hard triplet) — a genuine front-end sensitivity increase, not
a threshold tweak. **Keep this file as the corpus's hardest-case target** for that future work (folds into
P6(c) next action 2 below, not action 1 — it is a sensitivity-floor problem, not a threshold-tuning one).

**Retro D/E/O work-off DONE (2026-07-02 late).** What landed:

- **Retro D — resolved with NO new rejection gate** (conclusion revised same day on user evidence): the
  trains the plan had labeled "weak-train noise decodes" are **real transmissions** — the FskDemod
  spectrogram shows a genuine SSTV burst at ~157 s in 12_37_50 (= our @158.2 train, score 0.30), so the
  fill-ratio gate first built on that labeling (noise ≤ 0.34 vs real ≥ 0.46 pulses/claimed) was rejecting
  real weak images and was reverted. **No promoted train in the corpus is noise**: pure noise already
  fails at promotion (MHT triplet + N-of-M gates, pinned by the synthetic clutter tests), so the image
  gate stays claimed ≥ ¼·LineCount only, `ScoreThreshold` stays 0.18, and the measured fill ratio
  survives as `SstvPulseTrainExtractor.FillRatio` — a per-image quality/confidence diagnostic for the
  future META panel, not a gate.
- **VIS triplet-adoption hijack fixed** (found by the probe): `SstvVisPulseTrain.TryAddPulses` accepted any
  triplet whose grid extrapolated to the anchor within ±18 ms — over ~1000 periods that gate is nearly
  vacuous, and the UmKA-1 04-19 VIS train had adopted a cluster 116–169 s BEFORE its anchor (pulses at
  pulseNo −1125..−777, 1271 claimed lines). Now the triplet must lie in the anchor-forward image span.
  **Bonus: the un-hijacked region revealed a real, previously never-decoded image at ~133 s** (80 pulses,
  mean 0.367 — a SpacePi/Earth card, top third clean); the earlier "second burst at ~318 s" claim below
  was an artifact of the hijack (those pulses belong to the VIS train itself).
- **Retro E — frequency gate dropped** (user decision: FM-on-FM puts every sync at exactly 1200 Hz, no
  per-pulse frequency estimation warranted): `SstvPulse.Freq`, the trains' ±150 Hz gate and smoothing, and
  the extractor's triplet frequency check are deleted.
- **Retro O — one discriminator pass per capture**: `Decode`/`DetectMode`/`DetectVis` gained overloads
  taking the discriminated audio; the IQ forms are thin wrappers, and the harness discriminates once and
  slices `disc` (not IQ) per image. The discriminator stays the hand-rolled double-precision `Math.Atan2`
  loop (decision recorded in `SstvDecoder.Discriminator` doc): its cost is negligible next to the FIR
  stages and the deterministic arithmetic keeps the tuned statistics stable; liquid's `freqdem` computes
  the same phase difference in float with an approximated atan2 — no measurable win.
- **`DopplerRateHzPerSec` encoder knob (§8) DONE**: the carrier drift renders as the DC ramp on the
  discriminator (verified +446 → −526 Hz over 36 s); the closed loop under a TCA-like sweep (+500 Hz,
  −30 Hz/s) holds 28.5 dB PSNR with VIS detection intact — the §1.6 no-AFC design confirmed against a
  *drifting* carrier, not just a constant offset.
- **Real corpus result: 18 images from 8 of 9 captures** — the previous 18 minus the @318 s hijack
  artifact, plus the new UmKA-1 133 s image; the weak low-fill decodes (12_37_50 @158 s, Monitor-3
  @152 s, UTMN2 @26 s …) are real transmissions and are kept.
- **04-18 UmKA-1 hardest case re-diagnosed (`Real_UmKa0418ChannelSweep`, user spectrogram in hand):** the
  0–24 s transmission is **low-deviation FM** — the spectrogram shows a weak carrier plus only the
  first-order sideband pair tracing the 1.2–2.3 kHz subcarrier (vs the multi-line Bessel comb of the
  3.3 kHz-deviation bursts); devEst reads 1.3–2.1 kHz (noise-inflated). The ±6 kHz default channel is
  ~2× the matched noise bandwidth and keeps the discriminator below the FM threshold (2.4 % π-jump
  clicks). **Channel ±4 kHz is the matched sweet spot**: clicks 1.2 %, sync maxScore 0.221 → 0.286,
  on-grid sync gaps 3 → 11. A per-pulse threshold sweep there (detector `Threshold` is now an instance
  knob, default 0.18 unchanged) reaches an actual MHT **lock at thr 0.10** (156 pulses, 24 on-grid, an
  11-pulse Robot36 train; the decode's stripes are vertical = line period correct) — but the video is
  unusable and thr 0.12 mis-locks Robot72, so a global threshold drop is not the answer. Confirms the
  path: **longer coherent sync integration + per-burst SNR-adaptive channel bandwidth** (next action 1).

**Detection-channel sweep (2026-07-02 late, `Real_DetectionChannelSweep`) — a useful negative:** a
narrower *fixed* detection channel is NOT a corpus-wide win. ±5000 ≈ ±6000 on the strong bursts (some
pulse gains) but splits Monitor-3's clean 285 s train into two partial image trains; ±4000 clips the
3.3 kHz-deviation bursts (pulse losses, 12_37_50 lost entirely); and 04-18 UmKA-1 promotes nothing at any
bandwidth at the standard threshold. **`ChannelBwHz` stays 6000**; channel adaptivity, if built, must be
per-burst deviation-aware (measure the burst's deviation, re-decode matched — the ±4 kHz gain on 04-18 is
real but transmitter-specific), and the 04-18 detection floor is the coherent-integration problem, not a
bandwidth problem.

**Coherence-window sweep (2026-07-02 late, `Real_CoherenceWindowSweep`) — second decisive negative:**
widening the detector's 4 ms coherence window to 6/8 ms LOWERS every score (hard case 0.286→0.239→0.200;
strong burst 0.420→0.377→0.329) — the 9 ms sync pulse bounds single-pulse integration, so a wider window
eats the time template's flat top instead of adding gain; 4 ms is near-optimal. And clutter tracks the
burst max at every window (0.42/0.42, 0.38/0.37, 0.33/0.32): **single-pulse scores are fundamentally
non-separable at any window length** — the "longer matched-filter window" sub-option is measured and
closed. The only remaining sensitivity path for the 04-18 class is **cross-pulse soft-evidence
accumulation** (the §4.1 soft-comb: integrate the un-thresholded matched-filter score over predicted
line slots before requiring a hard triplet). The detector's window and threshold are now constructor/init
knobs (defaults unchanged) for these experiments.

**Two-tier soft evidence IN PROGRESS (2026-07-02 late, uncommitted — REVIEW BEFORE COMMIT).** The first
streaming step of the soft-comb: the extractor's detectors now emit down to `AssocThreshold` 0.10, but
only ≥ `ScoreThreshold` 0.18 pulses may form the spawn triplet — soft pulses can only CONFIRM an existing
hypothesis through the tight RLS gate (unit test `SoftPulses_ConfirmButNeverSpawn`). `GetPower` block-claim
smoothing became **density-weighted** (empty slots count as zero — the intended half-rate-harmonic
discriminant). Suite 89 pass / 8 manual-skip. Corpus: **20 images** — gains: a NEW third UmKA-1 burst at
~490 s found at the default channel, a new UTMN2 22:36 burst at ~208 s, several bursts extend earlier
(more claimed lines); synthetic noise rejection unaffected. **One REGRESSION: UTMN2 11:29 ~176 s now
decodes as Robot72** (was Robot36) — the half-rate harmonic promotes on soft evidence and the true
Robot36 hypothesis never forms to compete, so density weighting alone cannot displace it. Fix candidates
for next session: harmonic arbitration at spawn/promotion — suppress a candidate whose period is ~2× an
existing same-family train's with aligned phase, or prefer at promotion the hypothesis whose grid
explains more spawn-tier pulses over the shared span. 04-18 still yields no image (expected — needs the
full comb accumulator, not just tiered thresholds).

**Train-accuracy overhaul DONE (2026-07-03) — the duplicate-train / false-start defect root-caused and
fixed.** The user supplied a ground-truth list of every transmission in the corpus (now embedded in
`Real_TrainAccuracyProbe`, the accuracy scorecard); the code was detecting many transmissions 2-3× with
false mid-transmission starts. Root cause: **the RLS regressor's tolerances were ported from Hopper as
raw sample counts without rescaling from its ~2.756 kHz processing rate to our 48 kHz** — the association
gate floor was ±0.25 ms against real low-SNR onset jitter of 1–3 ms (17× tighter than the proven design),
so mid-burst pulses missed the gate, went unclaimed, and re-spawned duplicate trains. (The §9 A/B lesson
a third time: port the statistic, not the number — `TripletTolMs` had been converted correctly, and its
comment named Hopper's rate.) What landed:

- `SstvSyncRegressor` tolerances now in TIME, converted by fs: observation σ = 2.2 ms, gate floor =
  4.4 ms (Hopper's 6 / 12 samples @ 2.756 kHz).
- **Guards the wide gate requires against soft (0.10-tier) in-gate noise** (~4 %/slot): back-fill adopts
  spawn-tier pulses only (else the start creeps backward through noise); retire/idle clocks — any pulse
  holds one retire timeout, soft-only life bounded at 2× (`LastStrongTime`); VIS confirmation pulses must
  be spawn-tier; **promotion requires ≥ 6 spawn-tier pulses** (`StrongCnt`) among the N-of-M total (soft-
  dominated phantom trains, mean ≈ 0.16, promoted without this).
- **Merge-on-promote**: a promoting candidate whose grid is the continuation (±20 ms within a ≤12 s fade)
  of an existing same-mode train is absorbed, not emitted — the fade-split fragment backstop.
- `GetPower` density weighting (from the harmonic fight) stays; with the wide gate the true-period train
  holds its pulses, so **the UTMN2 11:29 Robot72 harmonic mis-mode is gone** (correct Robot36).

- **No-overlap spawn rule (user decision 2026-07-03)**: one FM channel carries one transmission at a
  time, so while a promoted train is Active no new hypothesis may spawn — "finish the first one; if
  there is still signal, start a new train". Candidates still compete freely before promotion (mode
  competition preserved); a mid-burst duplicate is now categorically impossible rather than merely gated.

**Scorecard vs ground truth: 18 of 19 transmissions matched with exactly one train each, 0 false** (was
~13/18 with 5+ duplicates). The detector found a transmission missing from the original list —
11_29_08 ~478–516, fitting the transmitter's 160 s cadence — and the user confirmed it. Monitor-3 text
card still decodes clean (no quality regression). Suite: 91 pass / 10 manual-skip. Residuals: (1) the
known 04-18 miss; (2) UmKA 04-19 484–515 emits two partials: a real >6 s sync dropout at ~500 (SNR
floor) retires the first train and a continuation spawns at ~503 with a >20 ms phase step — consistent
with the no-overlap rule as specified; folding such post-dropout continuations into one image would need
either a wider merge wing (risks smearing two grids into one bad fit) or an emission-level
same-mode-within-N-seconds dedup — deferred to the soft-comb work, which should hold the first train
through that dropout in the first place. Also new: **`SstvSpectrogramHarness`**
(`Real_SpectrogramProbe`) renders RF + discriminated-audio spectrogram PNGs per capture (ScottPlot
Viridis, the FskDemod view) for visual inspection by a coding assistant.

**Soft-comb statistic VALIDATED offline (2026-07-03, `Real_SoftCombProbe`):** combing the detector's
un-thresholded score (new probe-only `ScoreTap` on `SstvPulseDetector`) over 160 Robot36 line periods of
the 04-18 burst yields a coherent ridge at **z = 4.5**, all top-20 phases within ±1 ms of one phase and
identical at chan ±6000 / ±4000 — the comb is insensitive to the FM-threshold clicking that kills single
pulses — vs z = 2.3–2.6 on an equal-duration noise control. Single-pulse scores on the same data are
non-separable (0.221–0.286 vs 0.181–0.202). Margin ≈ 2σ at 24 s, grows ~√N with transmission length. The
statistic is proven; what remains is the **streaming realization**: a leaky per-mode comb ring over the
score stream (bounded state, §1.13), a detection threshold ≈ z 3.5 with a shaped kernel / robust
normalization to widen the margin, and seeding the MHT (comb hit → high-prior train at the comb phase,
like a VIS seed).

Next actions:

1. **Streaming soft-comb accumulator** (statistic validated above) for the 04-18 class — also owns the
   sensitivity-floor residuals (the UmKA ~503 post-dropout split, late starts on weak bursts).
2. Remaining P6(c) experiments: de-emphasis, impulse blanking (mine `Hopper\Experiments\FmNoise`),
   per-burst deviation-matched (NOT fixed-narrower — see the sweep above) channel/video bandwidth.
3. P7 regression corpus: `Real_TrainAccuracyProbe` + the ground-truth table are its seed.
2. Then P7 (regression corpus) and P8 (SkyRoof integration, §5 — the per-train image emission just proven
   in the harness is exactly the panel's leaf-per-image behavior; `IsImageTrain` is the leaf-emission
   gate).

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
  2026-07-01** (continuous scaled-time cursor, §9); ~~add a `DopplerRateHzPerSec` knob~~ **DONE
  2026-07-02** — the drifting DC ramp renders faithfully and the closed loop holds 28.5 dB PSNR with VIS
  detection intact under a TCA-like sweep (see Current Status).
- ~~**Discriminator:** wire the wrapped `freqdem` native per §1.2 or record the deviation (retro O), and share
  one discriminator pass between `DetectMode` and `Decode`~~ **DONE 2026-07-02** (retro O, see §9).
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

- ~~**G/H — the extractor/MHT port, wiring, and batch-code deletion**~~ **DONE 2026-07-02** (see Current
  Status): extractor + VIS train landed, wired into `DetectMode`/`Decode`, batch detectors deleted, first
  real decodes achieved. Still folded-in leftovers: a **minimum KeyOn width / noise-floor gate** in the
  detector (a single-sample blip above threshold can still become a pulse — clutter that the train gates
  currently absorb), and the **§1.10 `T_gap` = `RetireSeconds` unification** (now one constant in
  `SstvPulseTrain`, but not yet an exposed tunable).
- ~~**D — detection thresholds are hardcoded near the real-signal margin**~~ **DONE 2026-07-02** (see
  Current Status): measured on the real corpus and resolved with **no threshold change** — the low-score
  low-fill trains are real weak transmissions (user-confirmed), pure noise already fails at promotion,
  and the fill ratio is kept as a quality diagnostic (`FillRatio`), not a gate.
- ~~**E — per-pulse frequency is a constant 1200 Hz**~~ **DONE 2026-07-02**: the gate is consciously
  dropped for the single-frequency FM case (user decision — FM-on-FM needs no per-pulse frequency);
  `SstvPulse.Freq` and all frequency gates/smoothing deleted.
- ~~**F — reconstruction ignores `CorrFactor` for the intra-line pixel clock**~~ **DONE 2026-07-02**: both
  reconstructions scale segment/pixel widths by the winning train's `CorrFactor`
  (Hopper: `TimeScale = samplesPerMs·CorrFactor`); nominal (corr = 1) when tracking is off.
- **I — duplicated magic constants** — mostly resolved by deleting the batch detectors; the remaining
  per-class constants (detector window/threshold, train timeouts, extractor gates) get one home when the
  push-based decoder class lands.
- ~~**O — hand-rolled `Math.Atan2` discriminator, run twice per capture**~~ **DONE 2026-07-02**:
  disc-based `Decode`/`DetectMode`/`DetectVis` overloads share one discriminator pass; the hand-rolled
  double-precision discriminator is kept over the `freqdem` native (decision + rationale recorded in the
  `SstvDecoder.Discriminator` doc comment).
