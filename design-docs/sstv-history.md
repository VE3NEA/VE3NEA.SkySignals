# SSTV Decoder — Status History

This file preserves the accumulated **Current Status** log from
[sstv_decoder_plan.md](sstv_decoder_plan.md). Earlier in the project the plan's
"Current Status" section was an append-only journal of every status save; it was
later trimmed to hold only the latest state (per the plan-doc style: current state +
next steps only, history lives here). The stacked history below was extracted from
its last full form (commit `9b19e1d`, 2026-07-02, before commit `bba6804` collapsed
it). Newest summary is at the top; the dated entries below it accumulated
chronologically.

---

**Phase: train-accuracy overhaul DONE (2026-07-03; 18 of 19 ground-truth transmissions, one train each,
0 false) + the streaming soft-comb BUILT and standalone-validated (04-18 fires at z 5.8 — the hardest
case is detectable at last). Next: finish the comb's variance normalization, wire it into the extractor,
then the remaining P6(c) experiments, then P7.** The code lives in the `VE3NEA.SkySignals` repo, branch
`sstv`, project **`VE3NEA.SkySSTV`**. Suite: **94 pass / 12 manual-skip**.

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

**Streaming soft-comb BUILT, standalone-validated (2026-07-03 late, `SstvSoftComb` + 3 unit tests +
`Real_StreamingCombProbe`; not yet wired into the extractor).** One leaky ring of `period` bins per mode
(λ = 0.99/period touch ⇒ ~100-period memory, retro-N safe — the leak bounds every bin), fed by the
family's un-thresholded score stream via the detector's new probe `ScoreTap`; block-rate O(P) scans.
**The 04-18 burst fires decisively: first confirmed hit 9.5 s in, z = 5.8, anchor 77.1 ms = the
batch-validated phase.** Design points measured on the way (each a probe-caught failure):

- The leaky form **erases** the fundamental's √2 z-advantage over its half-rate harmonic (both saturate
  to the same per-bin height — the batch advantage came from fixed-time integration) → per-ring
  **mirror-ridge suppression**: a ring showing a comparable ridge at phase + P/2 is the harmonic and
  yields to the true mode's own ring.
- A single ring holds only ~period/2L independent noise samples, so its self-estimated σ has Student-t
  tails that fire falsely → **pooled per-family noise statistics**, gated until every ring of the family
  is warm.
- **Period-aware threshold** `1.6·√(2·ln(period/2L))` (extreme-value scaling; a flat threshold cannot
  serve 7 200- and 45 000-bin rings at once).

Remaining before extractor wiring: the noise control still grazes z = 3.6 at 13.2 s — between family
warm-up and leak steady state (9–30 s) the rings' variances fill at different rates and the pool
underestimates σ; fix analytically by normalizing each ring's bins by its touch-count variance factor
`(1−λ^{2k})/(1−λ²)` before pooling. Then: comb hit → high-prior train seeded at the comb phase (like a
VIS seed), back-dated one comb memory so the accumulated span's lines are claimed; re-run the accuracy
scorecard (04-18 should finally decode; must stay 18/19+ with no new false trains).

Next actions:

1. **Finish the soft-comb**: the touch-count variance normalization above, then wire into
   `ExtractTrains`/the extractor (comb hit seeds a high-prior train), validate on the scorecard — this
   also owns the sensitivity-floor residuals (the UmKA ~503 post-dropout split, late starts on weak
   bursts).
2. Remaining P6(c) experiments: de-emphasis, impulse blanking (mine `Hopper\Experiments\FmNoise`),
   per-burst deviation-matched (NOT fixed-narrower — see the sweep above) channel/video bandwidth.
3. P7 regression corpus: `Real_TrainAccuracyProbe` + the ground-truth table are its seed.
2. Then P7 (regression corpus) and P8 (SkyRoof integration, §5 — the per-train image emission just proven
   in the harness is exactly the panel's leaf-per-image behavior; `IsImageTrain` is the leaf-emission
   gate).

---

**Status save 2026-07-03 (commit `f3dbde2`, latest before this section was trimmed to current-only):**

**Phase: P6(b) robust mode+timing recovery COMPLETE (2026-07-03).** The extractor MHT, VIS seeding and
the streaming soft-comb (`SstvSoftComb` + `SstvCombPulseTrain`) are all built and wired. Accuracy
scorecard (`Real_TrainAccuracyProbe`): **20 of 20 ground-truth transmissions matched, one train each,
0 false, 0 missed** — including the 04-18 hardest case (below-FM-threshold, comb-seeded) and a
transmission the comb itself discovered (12_37_50 1–38 s, user-confirmed). 21 images decode from all
9 real captures (`Real_DecodesToPng`). Code: repo `VE3NEA.SkySignals`, branch `sstv`, project
`VE3NEA.SkySSTV`. Suite: **97 pass / 12 manual-skip**. Each probe's latest findings (and the run
history) live in the `[ManualFact]` annotations in the test harnesses; RXSSTV reference decodes of the
same transmissions are in `C:\Ham\RX-SSTV-2\History` (match by timestamp) for quality comparison.

Known residuals (accepted, documented in the probe annotations):

- UmKA 04-19 484–515 emits a second partial after a real >6 s sync dropout at ~500 s (the scorecard's
  one DUP): the family comb rings are reset when the first train retires, so the comb cannot bridge it.
- The below-FM-threshold transmissions (04-18 0–24 s, 12_37_50 1–38 s) detect and time-lock but decode
  to RGB speckle — the P6(c) front-end fidelity gap.

**P6(c) IN PROGRESS (2026-07-03):** the envelope-gated impulse blanker is implemented
(`SstvDecodeOptions.BlankerThreshold`, default 0 = off; design mined from Hopper `FmNoise` DevVsMag —
clicks live in envelope fades) and the decode-stage chan-BW × blanker grid ran on 4 real bursts
(`Real_P6cDecodeGridProbe`): blanker wins on ALL real bursts (clicks 2.4→0 %, 04-18 maxScore
0.221→0.324 at chan ±4000 + blank 0.5, rowNoise down everywhere, strong bursts don't regress); the
synthetic closed loop (`Frontend_BlankerAndChannelSweep`) shows the opposite because AWGN barely
clicks — trust the real grid. `p6c_*.png` written to the recordings `decoded` folder.

**User judgment (2026-07-03):** `p6c_m3_1237` shows no image at any setting (12_37_50 is
detect-only, unrecoverable); on the other 3 bursts **chan4000 + blank 0.5 is visually best**.
Defaults LOCKED accordingly: `VideoChannelBwHz = 4000` (new option; `Decode(Complex32[],…)` runs the
video chain with it, detection keeps `ChannelBwHz = 6000`), `BlankerThreshold = 0.5` (both chains);
harness image decodes now go through the video chain (decode from the IQ slice, not the detection
disc).

**INTERRUPTED mid-validation (session limit):** with the new defaults the fast suite is 97 pass /
**1 FAIL: `SstvP3Tests.Tracking_CoastsThroughMidImageFade`** — not yet investigated (suspect: the
blanker gates the test's simulated fade envelope and perturbs what the tracker sees, or the ±4000
video chain shifted its PSNR/assertion margin). Nothing else run after the lock.

Next steps:

1. Fix/diagnose `Tracking_CoastsThroughMidImageFade` under the locked defaults (first look: does the
   test's fade simulation drop the envelope so the blanker rewrites the fade? If so the test may need
   `BlankerThreshold = 0` locally, or the blanker's max-gap bound is wrong for multi-line fades).
2. Re-run `Real_TrainAccuracyProbe` (must stay 20/20 with blanker 0.5 in the detection chain) and
   `Real_DecodesToPng` (regenerate images with the locked video chain), update their annotations.
3. De-emphasis experiment (not yet run), then P6(c) is done.
4. P7 regression corpus (seed: `Real_TrainAccuracyProbe` + its ground-truth table).
5. P8 SkyRoof integration (§5; `IsImageTrain` is the leaf-emission gate).

