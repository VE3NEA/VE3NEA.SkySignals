# SSTV decoder — design/implementation alignment retrospective

Date: 2026-07-01. Trigger: the streaming sync detector fired on video/separator splatter because it used raw
band power instead of the planned **zero-mean 2D correlation**. That prompted a review for *other* places
where the code diverged from the design (plan §1.x/§3/§4.1/§6.1). This file is documentation only — no code
changes — to be worked off during the rest of P6.

The recurring theme: **the correct design already existed in the P4 batch `SstvSyncFilter`, and the P6(b)
streaming port has so far reproduced only part of it.** Several items below are "the streaming piece dropped a
property the batch piece had."

---

## A. Streaming detector implements only HALF the 2D zero-mean correlation (the frequency axis) — MAIN ISSUE

- **Design (§4.1):** the sync detector is a **separable zero-mean 2D matched filter** — zero-mean in
  **frequency** (energy normalization / `P_1200 − α·P_total`, rejects wrong-frequency & broadband splatter)
  **AND** zero-mean in **time** (a bipolar `pulse − flanks` template, rejects *right-frequency-wrong-time*
  energy: a sustained 1200 Hz carrier has no time contrast and must score ~0).
- **P4 `SstvSyncFilter` (batch) had both:** `g(t)` = energy-normalized coherence (frequency axis) convolved
  with a mode-length bipolar boxcar `Score = mean(g over pulse) − ½·mean(g over flanks)` (time axis). This is
  the full, correct design.
- **P6(b) `SstvPulseDetector` (streaming) has only the frequency axis:** coherence `|Σ disc·e^{−jωn}|²/(W·E)`
  with a plain threshold. The **time-axis bipolar template is missing.** It works on normal SSTV only because
  the sync is *naturally* brief (coherence falls after the pulse as the porch/video begins), so KeyOn runs are
  self-limiting.
- **Impact:** a **sustained 1200 Hz** input produces one long KeyOn run → a single false "pulse" (centroid in
  the middle), not rejection. Real culprits: the VIS start/stop/break bits (10–30 ms @ 1200), CW/carrier
  interference, a stuck transmitter. Also the effective detection SNR is lower than the full 2D statistic.
- **Fix (P6b):** port the bipolar time template into the streaming detector too (short-window coherence −
  flanking-window coherence), i.e. finish the port so the streaming detector equals `SstvSyncFilter`'s
  statistic, not just its frequency half.

## B. (root-caused + fixed) Raw band power instead of energy-normalized coherence

- **Design:** frequency zero-mean = divide by total energy.
- **First cut of `SstvPulseDetector`:** raw 1200-band power after a 200 Hz bandpass + a short/long ratio — no
  `/E`. Fired on separator/porch splatter (481 pulses vs 240).
- **Measured why:** FM is constant-envelope, so total window energy is ~identical at a real sync (5.408e9)
  and a separator step (5.396e9); only the *fraction at 1200* differs — coh **0.493 vs 0.002** (250:1). Raw
  power is blind to that; the bandpass skirts even leak the broadband click in.
- **Status:** fixed (commit `2c30182`) by switching to coherence. **Lesson to keep: the sync statistic must be
  energy-normalized; never raw band power** — constant-envelope FM defeats power-based detection.

## C. Mode-specific sync-duration discriminant lost in the streaming detector

- **Design (§4.1/§6.1):** the sync template is **tuned to the mode's pulse length** (Robot 9 ms vs PD 20 ms),
  and the per-length score **doubles as the Robot-vs-PD family discriminant** feeding the MHT.
- **Code:** `SstvSyncFilter` took `pulseLen` and `SstvModeDetector` scored the two families. The streaming
  `SstvPulseDetector` uses a **fixed 7 ms window** and emits **duration-agnostic** pulses.
- **Impact:** the sync-duration family cue is not available at the detector; the extractor must recover the
  family from the sync *period* alone (weaker; two families can have overlapping period ranges).
- **Fix:** carry a pulse-duration estimate on `SstvPulse`, or run a small per-length template bank, so the
  family discriminant survives into the MHT (Piece 4).

## D. `CoherenceThreshold = 0.25` is hardcoded and near the real-signal margin

- **Code:** `SstvPulseDetector`, `SstvSyncFilter`, `SstvSyncCorrelator` all hardcode ~0.25.
- **Concern:** the P4.5 real-capture probe measured a real sync **matched-filter score ≈ 0.24** (a different,
  flank-subtracted metric, but the same ballpark) — i.e. the threshold sits right at the real-signal margin.
  Weak real syncs may be missed.
- **Fix (P6c):** tune against real IQ; adding the time template (A) raises the effective SNR; consider a
  relative/adaptive threshold rather than a fixed constant.

## E. Per-pulse frequency hardcoded to 1200; the pulse-train frequency gate is inert

- **Design/Hopper:** each pulse carries its measured tone frequency, used for association and clutter
  rejection; `SstvPulseTrain.TryAddPulse` gates on `|pulse.Freq − train.Freq| > 150 Hz` and the triplet spawn
  matches frequencies.
- **Code:** `SstvPulseDetector` sets `pulse.Freq = 1200f` constant (the earlier per-pulse estimate was
  removed), so the frequency gate/association is a **no-op**.
- **Impact:** low, because we are single-frequency by design (§1.6, Doppler-centered) — but the freq-based
  clutter rejection Hopper relied on is absent, and the gate code is currently dead weight.
- **Fix:** either estimate per-pulse frequency (baseband phase slope) or consciously drop the gate for the
  single-frequency case and simplify the train.

## F. Reconstruction ignores the RLS/KF `CorrFactor` for the intra-line pixel clock

- **Design (§6.1 / Hopper `TimeScale = samplesPerMs · CorrFactor`):** the slant/clock estimate scales both the
  line spacing **and** the intra-line pixel width.
- **Code:** P3 tracks the per-line **onset** (good), but `ReconstructRobot`/`ReconstructPd` compute each
  segment length as `round(ms/1000·fs)` — **nominal**, not scaled by `CorrFactor`. Only vertical re-anchoring
  is slant-corrected; intra-line pixel spacing is not.
- **Impact:** residual horizontal slant within a line at high ppm / long lines; minor for satellite Doppler,
  but a real deviation.
- **Fix:** when the MHT/RLS lands, scale pixel/segment widths by `CorrFactor` in reconstruction.

## G. §1.13 batch debt + three overlapping sync detectors coexist

- **Still batch (banned by §1.13, not yet removed):** `SstvToneBank` (prefix sums), `SstvSyncCorrelator`
  (P2), `SstvSyncFilter` (P4), `SstvModeDetector` (whole-file scan), `SstvSyncTracker` (array-driven).
- **Three sync-detection implementations now exist** with different statistics: `SstvSyncCorrelator`
  (coherence, acquisition), `SstvSyncFilter` (coherence + time template, tracking/mode), `SstvPulseDetector`
  (coherence, streaming). Divergent constants and code paths.
- **Impact:** duplication, drift (exactly how A/B happened), and §1.13 violations remain live.
- **Fix:** complete the Piece-4 MHT port, then **delete** the batch sync/mode/tracker code and converge on one
  streaming detector (the completed `SstvPulseDetector`).

## H. The new streaming pieces are not yet wired into any decode path (hybrid decoder)

- `SstvSyncRegressor`, `SstvPulseTrain`, `SstvPulseDetector` exist with unit tests but **nothing calls them**;
  `SstvDecoder.Decode` still runs the batch P1–P4 chain. Only the P6(a) streaming *brightness* is live.
- **Impact:** the decoder is a hybrid mid-port; the streaming pieces are unverified end-to-end. Expected at
  this stage, but a reader must know the pieces are not yet load-bearing.
- **Fix:** Piece 4 (extractor) + wiring is what makes them live; until then treat P6(b) as scaffolding.

## I. Minor: duplicated magic constants

- Coherence window (4 ms / 7 ms), threshold (0.25), sync/freq tolerances, min-spacing are re-declared per
  detector. Centralize when converging on the single streaming detector (G).

---

Items J–P below were appended 2026-07-01 from a full plan+code review (a second pass, after A–I).

## J. Stage-2 audio bandpass (plan §3) was never implemented — likely the weak-real-sync culprit

- **Design (§3):** a real audio bandpass (~1000–2400 Hz) after the discriminator, feeding the sync, VIS and
  brightness paths. A–I missed this divergence: **no such filter exists anywhere in the code** — the tone
  banks and coherence run on the raw discriminator output.
- **Why it matters:** the sync/VIS statistic is coherence = tone power / **total window energy**, and the
  discriminator noise PSD is parabolic in f — the out-of-band (2.4–15 kHz) noise energy is ~two orders of
  magnitude larger than the in-band share (∫f² from 2.4k→15k ≈ 240× the 0–2.4k integral). So on a weak real
  signal the denominator is inflated by noise the numerator never sees. This is the likely mechanism behind
  the P4.5 measurement of **real sync ≈ 0.24 vs 0.49 clean** — the operating point that drove the §4.1
  "not threshold-separable" premise. The same principle was already proven on the brightness path
  (`BrightnessBwHz` took σ=0.05 from ~11 to ~30 dB PSNR); the sync/VIS path lacks its band-limit.
- **Related inconsistency:** `SstvToneBank` subtracts the region mean before the energy sum, but
  `SstvPulseDetector` subtracts nothing — a residual Doppler DC of D depresses its coherence by
  1/(1+2D²/A²). Small at the measured −32 Hz, but it is a silent batch-vs-streaming statistic divergence
  (the exact failure pattern of A/B). A Stage-2 bandpass fixes both at once.
- **Fix (do FIRST, before threshold tuning or the MHT on real IQ):** implement the band-limit — either the
  planned Stage-2 bandpass feeding everything, or Hopper's ±120 Hz sync-band extraction — then re-measure
  the real sync score; the §4.1 operating point (and D's margin) may improve substantially.

## K. Encoder slant is quantized away below ~120 ppm

- **Code:** `SstvEncoder.Samples()` rounds each segment independently, so slant shows only when
  `segmentSamples · ppm·1e-6 ≥ 0.5`. Robot36's longest segment is 4224 samples → anything under ~118 ppm
  renders as **zero** slant; the P3 test's 200 ppm renders ~139 ppm effective.
- **Impact:** the P6 "impairments matched to real-capture conditions" experiment (real clock errors are tens
  of ppm) would silently test no slant at all.
- **Fix:** keep a continuous scaled-time cursor across segments and emit
  `round(cursorEnd) − samplesEmittedSoFar` per segment.

## L. P4.5 harness re-acquires inside the slice and mislocks on the VIS bits

- **Code:** `SstvImageHarness.DecodeToImage` slices `FirstSyncSample − 0.5 s`, but the VIS header is 0.91 s,
  so the header tail (leader-2 + the ten 30 ms bits, several at 1200 Hz) lands inside the segment. `Decode`
  then re-acquires: the truncated VIS fails detection and `FindFirstSync` locks the 30 ms VIS **start bit**
  instead of line 0's sync. For Robot36 the error happens to be exactly 2 line periods, so the synthetic
  test still "passes" — with the first lines decoding VIS bits as video.
- **Fix:** `DetectMode` already returns the onset — pass `Acquire = false, StartSample = margin` instead of
  paying for (and mistrusting) a second acquisition, or make the margin exceed the header length. Add a
  PSNR assertion to `Synthetic_DecodesToPng` so misalignment fails the test (it currently only asserts that
  *some* image came out).

## M. Robot36 chroma parity assumed from the line index, not read from the signal

- **Signal truth:** the Robot36 separator tone identifies each line's chroma — 1500 Hz on R-Y lines,
  2300 Hz on B-Y lines (our encoder emits exactly this).
- **Code:** `ReconstructRobot` trusts `line & 1`, correct only when line 0 was acquired. On a real pass the
  decoder will routinely lock mid-image after a fade — every line's chroma then has a 50 % chance of being
  swapped (red↔blue). None of §4.1's machinery addresses this.
- **Fix:** classify the separator tone per line (one extra tone-bank read), fall back to parity when
  ambiguous. Also recorded as a plan §8 open item.

## N. Streaming-detector numerics will not survive a real pass

- **Code:** `SstvPulseDetector.CoherenceTrack` computes `Math.Cos(w · i)` with an unbounded absolute index
  (argument-reduction precision degrades after ~1e7 samples ≈ minutes) and its add/subtract running sums
  (`sc`, `ss`, `se`) accumulate floating-point drift indefinitely.
- **Impact:** fine in the current array-in convenience form, but this class is *the* one slated to survive
  into the true streaming decoder — the port would inherit a slow-burning bug no short test catches.
- **Fix:** wrapped-phase oscillator recurrence + periodically re-anchored / block-re-accumulated sums.
  Recorded as a §6.0 requirement in the plan.

## O. Hand-rolled Atan2 discriminator; discriminator runs twice per capture

- **Plan §1.2** says reuse the wrapped `freqdem` native; the code hand-rolls a managed `Math.Atan2` loop,
  and `DetectMode` + `Decode` each discriminate the full IQ — multi-hundred-MB captures, twice.
- **Fix:** wire `freqdem` or record the deviation and its reason (e.g. wanting doubles in Hz); share one
  discriminator pass between detection and decode.

## P. Minor code-review items (2026-07-01)

- `for (int i = 0; i < n + 0; i++)` leftover in `SstvPulseDetector.CoherenceTrack`.
- No minimum KeyOn width in `SstvPulseDetector` — a single-sample blip above 0.25 becomes a pulse (make
  sure Hopper's noise-floor gate survives the port).
- `SstvPulseTrain.HasEnoughPulses` counts **lifetime** pulses, not N-of-M within a bounded window: clutter
  trickling one associated pulse every <4 s eventually promotes. Bound M as Hopper does.
- Two pulses can associate into the same pulse-number slot; both are fitted and both count toward promotion.
- §1.10 `T_gap` (3–5 s) and `SstvPulseTrain.RetireSeconds` (6 s) describe the same physical event — should
  be one tunable.

---

## Priorities for the rest of P6

(Renumbered 2026-07-01 after the J–P review addenda.)

**Status after the 2026-07-01 implementation pass** (VE3NEA.Tlm commits 84c689f, e8421c1, b5d526a,
6f85e02, 6038b17 — suite 80 pass / 2 manual-skip):

- **DONE: J** — `SstvDecoder.SyncAudio` (1000–2400 Hz cosine-modulated BlackmanSinc, SIMD LiquidFir) feeds
  every sync/VIS/mode/tracker statistic. **Validated on real IQ** (`Real_SyncScoreProbe`, UTMN2_Robot36):
  raw burst 0.243 / clutter 0.163 (reproduces P4.5); bandpassed burst **0.420 = the synthetic level**.
  Caveat: band-limited noise coherence rises too (clutter max 0.406 over a 25 s search), so single-pulse
  thresholds remain non-separable — the §4.1 train integration is unchanged as the next step.
- **DONE: A, C, N** — `SstvPulseDetector` is now the full 2D filter (bipolar time template; onset argmax,
  centroid gone; sustained-carrier interior emits nothing — tested), one instance per sync-duration family
  with `DurMs` carried on each pulse, mixer recurrence + ring-buffered re-anchored sums for long-run
  stability. D's margin improves with A+J but the 0.18/0.25 constants still get tuned in P6(c).
- **DONE: K** (continuous-time encoder cursor; 50 ppm slant test), **L** (harness decodes at the detected
  onset + PSNR gate), **M** (separator-tone chroma with parity fallback; mid-lock test), **P** (promote age
  bound + duplicate-slot rejection; the `T_gap`/`RetireSeconds` unification waits for the extractor).
- **OPEN: G/H** (the extractor/MHT port + batch-code deletion — the next piece), **D** (threshold tuning,
  P6(c)), **E** (per-pulse frequency or drop the gate), **F** (CorrFactor pixel clock — lands with the
  extractor), **I** (constants consolidation — lands with G), **O** (freqdem + single discriminator pass).

1. **J** — sync-path band-limit (Stage-2 bandpass or ±120 Hz sync-band extraction) **before any threshold
   tuning or real-IQ MHT work**: it changes the operating point that D's margin and the §4.1
   "not threshold-separable" premise were measured at.
2. **A** — restore the time-axis bipolar template in the streaming detector (finish the 2D port). Highest
   correctness value; prevents sustained-carrier false pulses and raises SNR (helps D).
3. **C** — carry sync duration so the MHT keeps the Robot-vs-PD discriminant.
4. **G/H** — complete the MHT extractor (Piece 4), wire it in, then delete the batch detectors (§1.13);
   **N** (streaming numerics) is a requirement on that port, not a separate task.
5. **K/L** — encoder slant fidelity + harness re-acquisition mislock, **before the P6(c) experiments**
   (otherwise the impairment sweep tests the wrong slant and the synthetic baseline is silently misaligned).
6. **F**, **D**, **E**, **M**, **O**, **P** — fold into P6(b)/P6(c) as the streaming path lands and is
   tuned on real IQ.
