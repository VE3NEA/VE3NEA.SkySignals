# SSTV Decoder Plan (VE3NEA.SkySSTV)

Status: design agreed via grill-me interview 2026-06-29. Implemented through P8 as of 2026-07-04.

## Current Status

**Phase: ALL PHASES COMPLETE — P8 SkyRoof integration landed 2026-07-04 (uncommitted in BOTH repos);
next: visual check on a live pass, then commit.**

P8 (§5, SkyRoof repo, branch `sstv`): `TelemetryDecocder` now takes `(signalParams, telemetry, sstv)`
flags and builds `StreamingPipeline` and/or `SstvDecoder`; `Process()` feeds both (`args.Data.AsSpan(0,
args.Count)` for SSTV), `Dispose()` calls `Sstv.Flush()` after the worker joins so the partial image
finalizes at LOS / transmitter switch. **§5.1 mixed-mode DECIDED: concurrent decoders, self-gating** —
`SignalParamsResolver.HasSstv` detects "SSTV" anywhere in the mode/description strings, so a mixed
UmKA-1-style transmitter (classifies FSK) runs both decoders at once. `TelemetryPanel`: SSTV joins the
decodable set with no framing/baud requirement (`IsTelemetryDecodable`/`IsSstvDecodable` split); image
leaves under the pass node (`SstvImageInfo` in `node.Tag`, id→node map lives in the event-subscription
closure); right pane = `ImagePanel` (PictureBox Zoom + META TextBox) shown for image nodes; progressive
`ImageUpdated` re-renders in place; on finalize the PNG + JSON sidecar auto-save to
`<UserData>\SstvImages` **on the worker thread before marshaling** (a closing panel cannot lose it);
Save As / Copy context menu on the picture. `SkyRoof.csproj` vendors `VE3NEA.SkySSTV.dll` exactly like
SkyTlm (`RefreshVendoredSstv`, copy under `Vendor\VE3NEA.SkySSTV\`).

Verified 2026-07-04: `dotnet test VE3NEA.SkySSTV.Tests` → 113 pass / 16 manual-skip / 0 FAIL;
`dotnet build SkyRoof.sln -c Release -p:Platform=x64` → 0 errors, no new warnings; end-to-end wire
check drove the REAL `TelemetryDecocder` (pooled 4096-sample blocks → worker thread → dispose/flush)
over `2026-06-30_22_36_37_UTMN2_Robot36.iq.wav` → 2 final Robot36 images 240/240 rows @27.5 s /
@182.6 s + 23 progressive updates, matching the P7 scorecard for that capture.

P7/P7.5 details (regression gate, streaming core, front-end locks, known residuals) live in §7 and the
`[ManualFact]` annotations (`SstvImageHarness.cs`); RXSSTV reference decodes in `C:\Ham\RX-SSTV-2\History`.

Remaining:

1. Visual check in the live app (real pass or IQ playback) — progressive render, META pane, auto-save.
2. Commit both repos (SkySignals: P6–P7.5 decoder work; SkyRoof branch `sstv`: the P8 integration).

---

Goal: decode satellite SSTV from a 48 kHz complex-IQ stream and surface the
progressively-built image in SkyRoof's TelemetryPanel. Satellites generate SSTV
audio and feed it to an FM transmitter, so the chain is **FM-on-FM**: an outer FM
discriminator recovers the audio, then the SSTV subcarrier (1100-2300 Hz) is
decoded into an image.

Reference (classic, not modern): `C:\Proj\Forks\mmsstv` (`sstv.cpp`). We borrow the
mode/VIS constants and the broad structure, not the algorithms.

---

## 1. Decisions (locked during the interview)

1. **New assembly `VE3NEA.SkySSTV`**, depending only on `VE3NEA.Dsp` (shared DSP via
   project-path reference, per the SkyRoof-LiquidFir memory). 
   
   The new assembly lives in the VE3NEA.SkySignals solution, side by side with VE3NEA.SkyTlm assembly. 

   SkyRoof references
   both `VE3NEA.SkyTlm` and `VE3NEA.SkySSTV`. SSTV is continuous image reception and emits
   a bitmap, not `Frame`s; it does **not** belong in `VE3NEA.SkyTlm` (whose
   `StreamingPipeline` is burst->frame->CRC and literally rejects SSTV as analog noise).

2. **Front-end reuses already-wrapped natives** (`VE3NEA.Dsp.NativeLiquidDsp` /
   `LiquidFir`): complex channel FIR -> `freqdem` (outer FM demod) -> audio.
   No new P/Invoke needed for the core chain.

3. **De-emphasis: optional, OFF by default — but try the inverse of the transmitter's
   pre-emphasis.** Brightness is the *instantaneous frequency* of the subcarrier
   (amplitude-independent), so de-emphasis cannot change the *signal* brightness; it only
   reshapes the post-FM noise (whose discriminator PSD rises +6 dB/oct — exactly what a
   first-order de-emphasis low-pass cuts). But most FM voice transmitters **pre-emphasize
   the audio (+6 dB/oct) before modulation**, so the *matched* receiver de-emphasis is the
   classic FM SNR win, and its time constant is a **known standard, not a free parameter**.
   These satellites use amateur/NBFM transmitters — e.g. the **ISS runs a Kenwood TM-D710GA**,
   which pre-emphasizes the data-path audio in its 1200-baud mode (flat in 9600-baud) — so the
   amateur/land-mobile pre-emphasis (≈ 750 µs) is the first inverse to try. Keep the
   `DeEmphasisUs` toggle; default OFF, set from the P6(c) experiment (§6 item 2).

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
- `SstvDecoder` events *(as actually built in P7.5)*: `ImageUpdated(SstvImageEvent)` /
  `ImageCompleted(SstvImageEvent)` — the event carries mode, id, start time, FromVis, ValidRows and the
  full-geometry progressive `RgbImage`, subsuming the originally sketched
  `ModeDetected`/`ImageStarted`/`ScanLineDecoded` granularity. Panel subscribes
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

**DECIDED (P8, 2026-07-04): run both concurrently.** `SignalParamsResolver.HasSstv` flags "SSTV"
anywhere in the transmitter's mode/description strings; `TelemetryDecocder(signalParams, telemetry,
sstv)` builds the telemetry pipeline and/or the SSTV decoder from the two independently-derived flags,
and each self-gates (the P7 `MinCombPulses` guard is exactly the FSK-burst rejector on the SSTV side).
Segment routing was rejected: it needs a router that is itself a detector, while concurrency costs only
CPU on the few transmitters that advertise both.

---

## 6. Experimentation phase (requested)

### 6.0 Streaming-first refactor (do FIRST, before any tuning) — batch-removal inventory

Per §1.13, the batch shortcuts taken in P1-P4 must be converted to streaming (sample/block-in, bounded
state) **before** the filter sweep and before implementing §4.1 recovery. **Judgment call (agreed
2026-07-01): the remaining items below are folded into the P6(b) Hopper port** — the port replaces them
wholesale, so converting them to streaming form first would be throwaway work; they are deleted, not
refactored, when the extractor lands. Current batch operations to remove (all in `VE3NEA.SkySSTV`):

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
`VE3NEA.SkySSTV`:

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
2. **De-emphasis = the inverse of the transmitter's pre-emphasis (try the standard time
   constants).** FM voice transmitters commonly apply a first-order **+6 dB/oct pre-emphasis** to
   the audio before modulation; the matched receiver **de-emphasis** is the same first-order
   low-pass (−6 dB/oct, corner f = 1/(2πτ)) and cuts the parabolic high-frequency FM noise. The
   time constants are standardized — sweep each as `DeEmphasisUs` and keep the one that best
   improves the real-IQ reference-free metrics (rowNoise / edge SNR):
   - **750 µs (corner ≈ 212 Hz)** — EIA land-mobile / amateur narrowband-FM pre-emphasis (+6 dB/oct
     over ~300–3000 Hz). **Most likely for these satellites**, whose transmitters are NBFM-class:
     the **ISS SSTV runs a Kenwood TM-D710GA**, which pre-emphasizes in 1200-baud data mode (its
     9600-baud mode is flat). The 212 Hz corner is *below* the 1100–2300 Hz subcarrier band, so
     across the band the pre-emphasis is a near-constant +6 dB/oct tilt and the inverse tilts the
     band down toward the high end.
   - **75 µs (corner ≈ 2122 Hz)** — broadcast FM, ITU-R BS.450, Americas/Korea. Corner sits inside
     the subcarrier band.
   - **50 µs (corner ≈ 3183 Hz)** — broadcast FM, ITU-R BS.450, Europe / rest of world.
   Per §1.3 the *signal* brightness is amplitude-invariant, so a wrong (or absent-at-TX) guess only
   reshapes the noise and the invisible subcarrier amplitude — it cannot corrupt the image, only
   help the SNR or not. **Diagnostic to pick τ without a blind sweep:** if the TX pre-emphasized, the
   recovered subcarrier's *amplitude* rises with its instantaneous frequency (bright pixels are
   louder); measure that amplitude-vs-brightness slope on a strong real burst to read the
   pre-emphasis off the signal directly, then de-emphasize with the matching τ. A per-transmitter
   `9600-baud/flat` source (no pre-emphasis) is the null case — de-emphasis then only trades
   subcarrier-edge sharpness for noise, judged on the metrics.

   **RESOLVED 2026-07-04 — the null case: no TX pre-emphasis, `DeEmphasisUs` locked 0 (off).** The
   amplitude-vs-brightness diagnostic above was implemented (`Real_PreEmphasisSlopeProbe`) and read
   ≈ flat (−0.7..−1.3 dB tilt at a clipping-free channel; the −2.4..−4.2 dB seen at ±4000 is the RX
   channel clipping FM sidebands, not the TX). The blind sweep agreed: real rowNoise falls with τ but
   only as over-smoothing; the synthetic closed loop loses 0.8–1.2 dB PSNR at 300/750 µs. Stage kept
   as an option for future pre-emphasizing transmitters.
3. **Noise shaping:** evaluate whether any pre/de-emphasis or shaped brightness filtering
   improves perceived/measured image SNR at the FM threshold.
4. Lock the chosen defaults into the mode/chain config.

### 6.2 P6(d) — streaming Wiener post-filter (visual experiments BEFORE production code)

Goal (user request 2026-07-04): the areas where FM-demodulated noise dominates and no image detail is
visible make the picture look bad — detect those areas and reduce contrast in them. NLM (the SstvDens
reference, `C:\Proj\DSP\SstvTools\SstvDens\ImgDens.pas`) is explicitly **deferred**: its ±10-pixel
non-local patch search needs the whole image. The streaming-compatible form is a **local adaptive
Wiener (Lee) filter** driven by a demod-domain noise map:

- **Filter:** per plane (Y, Cr, Cb — before RGB conversion, where Robot36's alternating chroma is
  still separate): local mean `μ` and variance `σ²loc` over a small window (~7 px × 3 lines, running
  sums, 1–2 line lag); gain `g = max(0, σ²loc − σ²n)/σ²loc`; output `μ + g·(x − μ)`. Noise-dominated
  areas collapse to their local mean (= the requested contrast reduction, smoothly ramped); real
  edges/text pass at `g ≈ 1`. Chroma gets an over-weighted noise term (`k·σ²n`, k > 1) plus
  shrink-toward-neutral at very low chroma SNR — the rainbow speckle is mostly Cr/Cb noise and is the
  biggest visual win per dB.
- **Noise map σ²n (per pixel, accumulated over each pixel's integration span):** (a) **guard-band
  noise pilot** — bandpass power at ~2600–3400 Hz where no SSTV energy lives, scaled by the parabolic
  post-FM noise law into the ±`BrightnessBwHz` video band: content-free, per-sample, streaming;
  (b) **envelope** from the blanker's tracker (Hopper DevVsMag: discriminator error std ~6× larger in
  fades) for intra-line localization; (c) **blanked-sample fraction + brightness overshoot** flags per
  pixel (overshoot = the §6.1 alpha-channel item); (d) row-wise 3×3 Immerkær residual on the decoded
  lines as an image-domain calibration/fallback (itself row-local, 1-line lag).
- **Experiments (test-harness only — nothing enters production reconstruction until the PNGs are
  judged):**
  1. `Real_P6dWienerProbe`: batch prototype of the Lee filter on the decoded planes of the P6(c)
     burst set (utmn2236, m3_1102, umka0418, m3_1237, m3_1102b + one strong control), first with the
     image-domain Immerkær σ²n (zero decoder changes), writing `p6d_*.png` variants
     (window size × chroma k × shrink-to-neutral on/off) next to the raw decodes for side-by-side
     visual review.
  2. Add the demod-domain σ²n map (guard-band pilot + envelope + blank/overshoot flags) to the probe
     and A/B it against the image-domain map on the same bursts — the map choice is a visual + rowNoise
     decision.
  3. `Frontend_WienerSweep` synthetic guard: closed-loop PSNR with known noise must be ≥ the unfiltered
     decode (Wiener shrinkage with a correct σ²n is MMSE-favorable; a regression means the σ²n
     calibration is wrong).
  4. **USER reviews the PNGs and picks the variant/defaults (or rejects the stage)** — only then is the
     streaming form (per-pixel σ² accumulated during pixel integration, filter at line emission,
     per-pixel gain/σ into the alpha channel) implemented in the production reconstruction path.

  **UPDATE 2026-07-04 — items 1–3 built and run (test-only `SstvWienerPrototype` + probe + guard).**
  Two deviations from the design above, forced by measurement: **(a)** the 3×3
  Immerkær residual is NOT usable as the image-domain σ²n — it reads several× low on real bursts
  because its separable kernel needs a horizontal second difference, which vanishes on the
  horizontally-correlated (post-±600 Hz-LPF) noise blobs; with it neither map shrank anything. The
  image-domain estimator is now the **row-wise vertical first-difference median**
  (σ = median|p[y]−p[y−step]| / 0.6745 / √2 — scan lines are independent time slices so inter-line
  diffs carry the full noise power; step 2 for chroma over Robot36's duplicated rows; still row-local,
  1–2 line lag). **(b)** the guard-band pilot map (a) is calibrated median-to-median against that
  image-domain estimate rather than by the analytic parabolic-law scaling; on the first A/B the
  image-domain map filters visibly better than the pilot (the pilot's median calibration leaves half
  the map under-weighted) — the envelope/blank-flag refinements (b)–(c) stay unbuilt unless a future
  judgment asks for better fade localization.

  **RESOLVED 2026-07-04 — item 4, the user's visual judgment: variant `w9x5_k4_ns` wins on every
  burst that carries an image** ("best denoising" on utmn2236 / vz_1109 / m3_1102, "denoises and
  preserves fine structure" on the m3_1102b text card); umka0418 is better RAW (the only version
  showing some detail through below-FM-threshold noise), m3_1237 has no image at any setting.
  **Locked into production as `SstvWienerFilter`** (window 9×5, chroma k = 4, no shrink-to-neutral,
  image-domain vertical-difference map), applied to the Y/Cr/Cb planes in both reconstructions before
  the RGB conversion; default ON via `SstvDecodeOptions.WienerEnabled` (OFF = the raw inspection path
  for the umka0418 class). `Frontend_WienerSweep` now guards the production filter (+3.9 dB at σ=0.5,
  no-op clean, colorbar edges +1.0); the 21-image real baseline regenerates unchanged. The per-pixel
  gain/σ → alpha-channel idea moves to the P7.5 streaming refactor with the rest of the line-emission
  form.

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
- **P6(d) (done 2026-07-04)** Streaming Wiener post-filter experiments (§6.2): prototype the noise-map +
  Lee-filter variants in the test harness, write `p6d_*.png` for side-by-side review, and get the
  **user's visual judgment** before any of it enters the production reconstruction code — judged, and
  the winning `w9x5_k4_ns` variant locked into production (`SstvWienerFilter`, §6.2 RESOLVED).
- **P7 (done 2026-07-04)** Real regression corpus (Robot36 + Robot72 captures) + docs — the ground-truth
  scorecard `Real_TrainAccuracyProbe` is now ASSERTED (20 matched / 0 false / 0 missed / ≤1 accepted dup
  over the 9 captures), and the comb false-positive guard landed: `MinCombPulses 6` in
  `SstvPulseTrainExtractor.IsImageTrain` (the 11_09 telemetry ridge carried p=3; every real comb find has
  p≥21 blanker-era, ≥7 pre-blanker — see the IsImageTrain doc and the probe annotations).
- **P7.5 Streaming API refactor (done 2026-07-04; do before P8).** Delivered as specified below:
  `SstvDecoder` is a sealed partial class with the push-based instance surface (`Process(block)` / `Flush()`
  / `ImageUpdated`+`ImageCompleted` events, §6.2 Wiener-gain alpha in `RgbImage.A`) built on new streaming
  parts — `StreamingFir`/`StreamingFirComplex` (VE3NEA.Dsp stateful `firfilt`), `SstvStreamingDiscriminator`,
  `SstvStreamingBrightness`, `SstvDetectionChain` (the live extractor graph), VIS tiling over a rolling sync
  buffer, a comb-back-date-sized brightness ring, and `SstvImageBuilder` (dirty-rewind re-render via
  `SstvPulseTrainExtractor.TakeLineRewind`, finalize-on-retire). The static whole-array methods are thin
  wrappers over the same core; `SstvStreamingStageTests` pins sample-exact batch/streaming equivalence.
  Original spec: P6 made every *algorithm* streaming-capable (bounded
  rings, oscillator recurrences, re-anchored running sums — §6.0), but the *public surface* is still
  whole-array batch and so does not yet satisfy §1.13: `SstvDecoder` is a `static class` whose
  `Decode(Complex32[])` / `Discriminator` / `Brightness` / `SyncAudio` / `ChannelFilter` each allocate and
  process the entire recording in one call, and `ExtractTrains` scans the whole `sync[]` array. Convert this
  to the **push-based decoder** §6.0/§6.1 specify: an instance `SstvDecoder` fed IQ **blocks**
  (`Process(Complex32[] block)` like `StreamingPipeline`/`ThreadedProcessor<Complex32>`), holding a **rolling
  few-second buffer** (Hopper's `Dump`/`SamplesDumped`), running the channel FIR / discriminator / blanker /
  Stage-2 / brightness / detectors / extractor as **block-in, bounded-state** stages, and emitting scan
  lines / mode events incrementally with the §1.13 dirty-block re-render on convergence. Concretely: (a) make
  the FIR stages stream via `LiquidFir`'s stateful `firfilt` (drop the whole-array `ConvolveSame` return
  shape — the filters are already streamable, only the API changes); (b) drive one **live** extractor
  instance (this also collapses the detect-then-decode double `ExtractTrains` pass, §8); (c) fold the comb /
  detectors on the block boundary they already assume; (d) keep the current static whole-array methods as
  thin test-only wrappers over the streaming core so the closed-loop PSNR/harness tests still run. This is
  the last decoder-side work before the UI depends on the streaming contract.
- **P8 (last)** SkyRoof integration (dispatcher, image leaves, progressive render, META, auto-save) — see
  §5. Deliberately the final phase: the decoder must decode real captures to PNG standalone (P6/P7) and
  present the push-based streaming contract (P7.5) before it is wired into the UI.

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
- ~~Mixed-mode dispatch (§5.1): segment routing vs concurrent FSK+SSTV decoders - pick one
  during SkyRoof integration.~~ **DECIDED P8 2026-07-04:** concurrent, self-gating (see §5.1).
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
- **Extractor runs twice per capture (accepted for now):** retro O shares one *discriminator* pass, but a
  detect-then-decode sequence still runs `ExtractTrains` (the whole MHT scan) once in `DetectMode` and again
  in `Decode`. Harmless in the batch harness; the P7.5 push-based decoder (§7) makes the extractor a single
  live instance, so this collapses to one pass then. Not worth a shared-result cache before that refactor.
- Phasing (§7) has no P5 — renumber or mark the gap intentional (P7.5 now fills the streaming slot before P8).

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
