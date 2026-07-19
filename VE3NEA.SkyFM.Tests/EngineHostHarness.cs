using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;
using Xunit.Abstractions;

namespace VE3NEA.SkyFM.Tests
{
  /// <summary>
  /// Engine-host end-to-end (M3 Pass A / M5 Pass B / hybrid): a real sidecar engine transcribes the
  /// decoded per-transmission clips and the result is scored against corpus truth at the same
  /// <see cref="HeadlessRunner.Run"/> seam the stored-transcript baseline uses. Engine words are
  /// cached beside each clip directory (<c>*.words.json</c> — delete after re-decoding the clips), so
  /// the slow Whisper transcription happens once per recording. The hybrid is candidate-level
  /// Pass A+B fusion (§5.5): both engines' transmissions are pooled and <c>CandidateFusion</c>
  /// corroborates identifiers across engine paths exactly as it does across repeat mentions. The
  /// <c>All_*</c> tests sweep every labeled corpus recording and pool char counts into corpus-level
  /// P/R/F1. Manual: needs <c>asr-spike\.venv</c> (faster-whisper, vosk, cached models) and the
  /// decoded recordings from <see cref="FmDemodHarness"/> on disk.
  /// </summary>
  public class EngineHostHarness : IDisposable
  {
    private static readonly string DecodedDir =
      @"C:\Users\alsho\AppData\Roaming\Afreet\Products\SkyRoof\Recordings\FM\decoded";
    private static readonly string ArissClipDir = Path.Combine(DecodedDir, "2026-07-04_23_03_57_ARISS");

    private readonly ITestOutputHelper output;
    private readonly Dictionary<string, IAsrEngine> engines = new();

    public EngineHostHarness(ITestOutputHelper o) => output = o;

    public void Dispose()
    {
      foreach (var e in engines.Values) e.Dispose();
    }


    // ----------------------------------------------------------------------------------------------------
    //                                 single-recording harnesses (ARISS)
    // ----------------------------------------------------------------------------------------------------
    [ManualFact("2026-07-18: 56/56 clips decoded with speech, 12.5 min CPU — audio-E2E free-ASR baseline " +
      "(post partition/fusion/partials): callsigns P 0.80 R 0.62 F1 0.70 (EM85KR resolved to EM85|KR4 " +
      "by the grid-anchored partial), grids P 0.75 R 0.94 F1 0.83, symbols P 0.78 R 0.97 F1 0.87; " +
      "per-clip decode trades symbol precision for grid recall vs the stored transcript (FM17 FM22 " +
      "recovered; WN3Y artifact remains)")]
    public void Ariss_FasterWhisper_DecodedClips_ScoreAgainstCorpusTruth()
      => Score("faster-whisper large-v3 int8", CachedWords("whisper", SidecarEngine.FasterWhisper, ArissClipDir),
        ArissRecording());

    [ManualFact("2026-07-18: 16/56 clips with speech (structural abstention on noise), 1m16s — " +
      "callsigns P 0.82 R 0.20 F1 0.32, grids P 1.00 R 0.25, symbols P 0.79 R 0.67, ZERO unmatched " +
      "emissions (EM85 conf 1.00, KD0QPC, R4J→KR4JIQ): the spike's precision/abstention prediction " +
      "holds end-to-end; recall is the hybrid's job")]
    public void Ariss_VoskGrammar_DecodedClips_ScoreAgainstCorpusTruth()
      => Score("vosk lgraph grammar", CachedWords("vosk", SidecarEngine.VoskGrammar, ArissClipDir),
        ArissRecording());

    [ManualFact("2026-07-18 (post partials): hybrid beats whisper-alone on callsigns " +
      "P 0.82 R 0.62 F1 0.71 vs 0.80/0.62 (KR4JIQ corroborated by vosk), grids P 0.75 R 0.94, symbol " +
      "recall 1.00 — all 12 Gold covered; caches make re-runs fast (symbol row counts both engines' " +
      "words)")]
    public void Ariss_HybridWhisperVosk_DecodedClips_ScoreAgainstCorpusTruth()
      => Score("hybrid whisper+vosk", CachedWords("whisper", SidecarEngine.FasterWhisper, ArissClipDir)
        .Concat(CachedWords("vosk", SidecarEngine.VoskGrammar, ArissClipDir)).ToList(), ArissRecording());

    [ManualFact("2026-07-18 (score 2.5): callsigns P 0.68 R 0.42 (AB2IW KB2IW K2HZV KC1ZFD), " +
      "grids P 0.88 R 0.44, symbols P 0.71 R 0.92 vs unbiased R 0.77 — the hotword gain is real and " +
      "grows with the swept strength; ~5 s for 56 clips, ~150x realtime")]
    public void Ariss_SherpaHotwords_DecodedClips_ScoreAgainstCorpusTruth()
      => Score("sherpa zipformer hotwords", CachedWords("sherpa25", () => SherpaOnnxEngine.Hotwords(), ArissClipDir),
        ArissRecording());

    [ManualFact("2026-07-18 (post partition/fusion): callsigns P 0.53 R 0.29, grids P 0.88 R 0.44 " +
      "(EM85 FM22), symbols P 0.68 R 0.77 — the no-hotwords control for the biasing measurement")]
    public void Ariss_SherpaUnbiased_DecodedClips_ScoreAgainstCorpusTruth()
      => Score("sherpa zipformer unbiased", CachedWords("sherpa0", () => SherpaOnnxEngine.Unbiased(), ArissClipDir),
        ArissRecording());


    // ----------------------------------------------------------------------------------------------------
    //                                    full-corpus harnesses
    // ----------------------------------------------------------------------------------------------------
    [ManualFact("2026-07-18 (symbol-utterance truth): CORPUS symbols P 0.75 R 0.15 (51/68 emitted " +
      "correct, 57/372 known recalled), ARISS identifier rows unchanged; near-zero emissions on the " +
      "SO-50 passes and 07-12 AO-123 — vosk abstains where whisper must now be measured; dead " +
      "recording clean (no emissions)")]
    public void All_VoskGrammar_ScoreAgainstCorpusTruth()
      => ScoreAll(dir => CachedWords("vosk", SidecarEngine.VoskGrammar, dir));

    [ManualFact("2026-07-18 (symbol-utterance truth): CORPUS symbols P 0.84 R 0.62 F1 0.72 (231/372 " +
      "known recalled) — the free-ASR ceiling to beat; strong on both ARISS and 07-12 AO-123, zero on " +
      "07-12 SO-50 (3IW4HM unheard), R 0.19 on 07-13 AO-123 (WN3Y/FM18 mostly missed). Post split: " +
      "train symbols 0.88/0.59, TEST symbols 0.86/0.68 F1 0.76 — the best test symbol precision")]
    public void All_FasterWhisper_ScoreAgainstCorpusTruth()
      => ScoreAll(dir => CachedWords("whisper", SidecarEngine.FasterWhisper, dir));

    [ManualFact("2026-07-18 (symbol-utterance truth): CORPUS symbols P 0.84 R 0.63 F1 0.72 — at the " +
      "symbol level vosk adds only +3 recalled symbols over whisper-alone (its decodes overlap), but " +
      "the ARISS identifier track keeps the hybrid gain (callsigns P 0.82 R 0.62 vs 0.80/0.62): " +
      "fusion pays at assembly, not at raw symbols")]
    public void All_HybridWhisperVosk_ScoreAgainstCorpusTruth()
      => ScoreAll(dir => CachedWords("whisper", SidecarEngine.FasterWhisper, dir)
        .Concat(CachedWords("vosk", SidecarEngine.VoskGrammar, dir)).ToList());

    [ManualFact("2026-07-18 (score 2.5 per the §13 sweep): CORPUS symbols P 0.65 R 0.42 F1 0.51 " +
      "(158/372) vs unbiased 0.60/0.33, callsigns 0.68/0.42 vs 0.53/0.29, grids 0.88/0.44 — hotword " +
      "biasing at the swept strength buys recall AND precision at decode time (the ATCO2 bet " +
      "confirmed), though the engine alone stays below whisper; junk 'A'/'ARE' single words on noise " +
      "clips are its filler mode")]
    public void All_SherpaHotwords_ScoreAgainstCorpusTruth()
      => ScoreAll(dir => CachedWords("sherpa25", () => SherpaOnnxEngine.Hotwords(), dir));

    [ManualFact("2026-07-18: CORPUS symbols P 0.58 R 0.33 F1 0.42 (122/372) — the no-hotwords control")]
    public void All_SherpaUnbiased_ScoreAgainstCorpusTruth()
      => ScoreAll(dir => CachedWords("sherpa0", () => SherpaOnnxEngine.Unbiased(), dir));

    [ManualFact("2026-07-18 (partials + sherpa@2.5 + grid-glue prior + depth weight): the three-engine " +
      "pool in one CandidateFusion pass — callsigns P 0.76 R 0.67 raw (glue prior killed EM85KR: " +
      "0.69 → 0.76) → P 0.85 R 0.67 with the calibrated policy → **P 0.93 R 0.67 F1 0.78, ZERO " +
      "unmatched emissions** with policy + DepthConfidence (all 6 recoverable Gold callsigns incl. " +
      "KR4JIQ; the depth weight killed the shallow-burst WN3Y/N3Y leak; only the KF4UJU→KF4UJ " +
      "near-miss chars remain), grids 0.94/0.94, symbols P 0.77 R 0.68; vs w+s policy+depth 0.97/0.62 " +
      "(more P, less R) and w+v gated 0.79/0.42 — vosk adds corroboration the policy can then afford " +
      "to emit; G2 train precision 0.93 ≥ the 0.90 floor. 2026-07-18 later (operator: WN3Y → ARISS " +
      "Partial; train/test split per §11): train callsigns raw 0.88 / policy 0.94 / policy+depth " +
      "0.93 R 0.67 zero unmatched, grids 0.94/0.94; first held-out row — TEST symbols P 0.72 R 0.74 " +
      "F1 0.73 vs train F1 0.72 (whisper-alone TEST symbols 0.86/0.68: sherpa buys test symbol " +
      "recall at precision cost; identifier-level test needs identifier labels)")]
    public void All_HybridWhisperVoskSherpa_ScoreAgainstCorpusTruth()
      => ScoreAll(dir => CachedWords("whisper", SidecarEngine.FasterWhisper, dir)
        .Concat(CachedWords("vosk", SidecarEngine.VoskGrammar, dir))
        .Concat(CachedWords("sherpa25", () => SherpaOnnxEngine.Hotwords(), dir)).ToList());

    [ManualFact("2026-07-18 (partials + sherpa@2.5 + grid-glue prior + depth weight): CORPUS symbols " +
      "P 0.77 R 0.68 F1 0.72, symbol recall 1.00 on ARISS (all 12 Gold); callsigns P 0.75 R 0.67 raw " +
      "(glue prior killed EM85KR: 0.67 → 0.75 — whisper's EM85 grid mentions coincide with every " +
      "sherpa EM85KR mention, so the fusion-level cross-validation drops what the run-level partial " +
      "anchor could not) → 0.88/0.62 with the calibrated policy → **0.97/0.62, zero unmatched** with " +
      "policy + DepthConfidence, grids 0.94/0.94; sherpa-ALONE still shows EM85KR — its own grid " +
      "mentions don't cover every glue mention. Post WN3Y-partial/split: train callsigns policy " +
      "0.98/0.62, +depth 0.97/0.62; TEST symbols 0.73/0.74")]
    public void All_HybridWhisperSherpa_ScoreAgainstCorpusTruth()
      => ScoreAll(dir => CachedWords("whisper", SidecarEngine.FasterWhisper, dir)
        .Concat(CachedWords("sherpa25", () => SherpaOnnxEngine.Hotwords(), dir)).ToList());

    [ManualFact("§5.5 emit/abstain/partial calibration on the hybrid whisper+sherpa candidates: sweeps " +
      "EmitThreshold × CharThreshold over the cached words and prints corpus identifier P/R/F1 per row " +
      "(the symbol track is pre-assembly and unaffected); each row doubles as an independent per-kind " +
      "calibration because scoring is per-kind. 2026-07-18 (partials + sherpa@2.5 + grid-glue prior): " +
      "callsigns best at emit/char 0.85 — P 0.88 R 0.62 vs baseline 0.75/0.67 (kills the flat-0.80 " +
      "sherpa singletons); " +
      "grids best at emit 0.75 char 0.70 — P 0.94 R 0.94, dropping only the low-conf FN20 artifact → " +
      "the per-kind EmitPolicy defaults; the final 'calibrated per-kind' row prints the frozen " +
      "operating point")]
    public void All_HybridWhisperSherpa_EmitPolicySweep()
    {
      var corpus = Corpus.Load(RepoFiles.Find(Path.Combine("corpus", "ground-truth.json")));
      var runs = new List<(List<AsrWord[]> Words, CorpusRecording Rec)>();
      foreach (var rec in corpus.Recordings)
      {
        string clipDir = Path.Combine(DecodedDir, rec.File.Replace(".iq.wav", ""));
        if (rec.Role == "test" || rec.Identifiers.Count == 0 || !Directory.Exists(clipDir)) continue;
        runs.Add((CachedWords("whisper", SidecarEngine.FasterWhisper, clipDir)
          .Concat(CachedWords("sherpa25", () => SherpaOnnxEngine.Hotwords(), clipDir)).ToList(), rec));
      }

      SweepRow("baseline (no policy)", null, runs);
      foreach (float emit in new[] { 0.75f, 0.80f, 0.82f, 0.85f, 0.90f, 0.95f })
        foreach (float ch in new[] { 0.70f, 0.80f, 0.85f, 0.90f })
          SweepRow($"emit {emit:0.00} char {ch:0.00}", new EmitPolicy
            { Callsigns = new(emit, ch), Grids = new(emit, ch) }, runs);
      SweepRow("calibrated per-kind", new EmitPolicy(), runs);
    }

    [ManualFact("§5.2 role-b depth-weight sweep on the three-engine hybrid + calibrated policy: scales " +
      "per-mention confidence by the transmission's quieting depth (DepthConfidence ramp) before " +
      "fusion, sweeping MinWeight × FullDb at ShallowDb 4, and prints raw + gated identifier P/R/F1 " +
      "per row against the no-depth baseline — does demoting weak-burst mentions buy precision " +
      "before it costs the cross-repeat weak-burst recall (KQ4GIK class)? 2026-07-18: with grids " +
      "exempt, a stable plateau at gated callsigns P 0.93 R 0.67 (grids hold 0.94/0.94) spans " +
      "minW 0.7–0.8 × full 14–16 dB (and full 12 at minW 0.5–0.6); harder demotion collapses " +
      "weak-burst recall 0.67 → 0.53/0.47 exactly as predicted → defaults ShallowDb 4 FullDb 14 " +
      "MinWeight 0.7 GridMinWeight 1; weighting grids too only cost their recall (0.94 → 0.81)")]
    public void All_HybridWhisperVoskSherpa_DepthWeightSweep()
    {
      var corpus = Corpus.Load(RepoFiles.Find(Path.Combine("corpus", "ground-truth.json")));
      var runs = new List<(List<AsrWord[]> Words, List<double>? Depths, CorpusRecording Rec)>();
      foreach (var rec in corpus.Recordings)
      {
        string clipDir = Path.Combine(DecodedDir, rec.File.Replace(".iq.wav", ""));
        if (rec.Role == "test" || rec.Identifiers.Count == 0 || !Directory.Exists(clipDir)) continue;
        var words = CachedWords("whisper", SidecarEngine.FasterWhisper, clipDir)
          .Concat(CachedWords("vosk", SidecarEngine.VoskGrammar, clipDir))
          .Concat(CachedWords("sherpa25", () => SherpaOnnxEngine.Hotwords(), clipDir)).ToList();
        var d = HeadlessRunner.LoadDepths(clipDir);
        var depths = d == null ? null : d.Concat(d).Concat(d).ToList();
        runs.Add((words, depths, rec));
      }

      DepthRow("baseline (no depth)", null, runs);
      foreach (float minW in new[] { 0.5f, 0.6f, 0.7f, 0.8f })
        foreach (double full in new[] { 12.0, 14.0, 16.0 })
          DepthRow($"minW {minW:0.0} full {full:0} dB",
            new DepthConfidence { ShallowDb = 4, FullDb = full, MinWeight = minW }, runs);
      DepthRow("calibrated defaults", new DepthConfidence(), runs);
    }

    private void DepthRow(string label, DepthConfidence? weight,
      List<(List<AsrWord[]> Words, List<double>? Depths, CorpusRecording Rec)> runs)
    {
      var policy = new EmitPolicy();
      (int E, int C, int G, int R) calls = default, gCalls = default, gGrids = default;
      foreach (var (words, depths, rec) in runs)
      {
        var d = weight == null ? null : depths;
        calls = Add(calls, HeadlessRunner.Run(words, rec, null, d, weight).Callsigns);
        var gated = HeadlessRunner.Run(words, rec, policy, d, weight);
        gCalls = Add(gCalls, gated.Callsigns);
        gGrids = Add(gGrids, gated.Grids);
      }
      output.WriteLine($"{label,-22} raw callsigns {Fmt(calls)}   gated callsigns {Fmt(gCalls)}   " +
        $"gated grids {Fmt(gGrids)}");
    }

    private void SweepRow(string label, EmitPolicy? policy, List<(List<AsrWord[]> Words, CorpusRecording Rec)> runs)
    {
      (int E, int C, int G, int R) calls = default, grids = default;
      foreach (var (words, rec) in runs)
      {
        var r = HeadlessRunner.Run(words, rec, policy);
        calls = Add(calls, r.Callsigns);
        grids = Add(grids, r.Grids);
      }
      output.WriteLine($"{label,-22} callsigns {Fmt(calls)}   grids {Fmt(grids)}");
    }

    [ManualFact("plan S0 int8-vs-fp32 gate: decodes every corpus recording with sherpa@2.5 in both " +
      "quantizations (fp32 cache tag sherpa25, int8 tag sherpa25int8), reports the corpus symbol/" +
      "callsign/grid tracks side by side plus the FmTranscriptBuilder line/token counts (the shipped " +
      "view), and times a fresh ARISS decode (ctor load + 56-clip decode) for each. 2026-07-19 RESULT " +
      "— int8 ≈ fp32 on the shipped tracks: CORPUS symbols int8 0.69/0.45 F1 0.55 vs fp32 0.67/0.42 " +
      "F1 0.52 (int8 marginally better, within the ~3% metric resolution), transcript 105 lines/199 " +
      "tokens vs 110/210, grids identical 0.88/0.44; the only regression is ARISS callsign precision " +
      "0.75 → 0.65 (the identifier-assembly path, NOT shipped in the v1 transcript view). int8 is " +
      "~20% faster (ARISS decode 2770 vs 3446 ms, load 1547 vs 1691) and ~4.5x smaller (encoder 73 vs " +
      "263 MB → ~71 MB vs ~326 MB pack). DECISION: ship int8.")]
    public void All_SherpaInt8VsFp32_Comparison()
    {
      var corpus = Corpus.Load(RepoFiles.Find(Path.Combine("corpus", "ground-truth.json")));
      (int E, int C, int G, int R) fpSym = default, iqSym = default, fpCall = default, iqCall = default,
        fpGrid = default, iqGrid = default;
      int fpLines = 0, iqLines = 0, fpTokens = 0, iqTokens = 0;

      foreach (var rec in corpus.Recordings)
      {
        string clipDir = Path.Combine(DecodedDir, rec.File.Replace(".iq.wav", ""));
        if (!Directory.Exists(clipDir)) { output.WriteLine($"skip {rec.File}: no clips"); continue; }

        var fp = CachedWords("sherpa25", () => SherpaOnnxEngine.Hotwords(), clipDir);
        var iq = CachedWords("sherpa25int8", () => SherpaOnnxEngine.Hotwords(2.5f, int8: true), clipDir, engineKey: "sherpa25int8");
        var fpRes = HeadlessRunner.Run(fp, rec);
        var iqRes = HeadlessRunner.Run(iq, rec);

        fpSym = Add(fpSym, fpRes.Symbols); iqSym = Add(iqSym, iqRes.Symbols);
        var (fl, ft) = TranscriptSize(fp); var (il, it) = TranscriptSize(iq);
        fpLines += fl; fpTokens += ft; iqLines += il; iqTokens += it;

        output.WriteLine($"--- {rec.File} [{rec.Role}]");
        output.WriteLine($"  fp32 symbols {Fmt2(fpRes.Symbols)}  transcript {fl} lines / {ft} tokens");
        output.WriteLine($"  int8 symbols {Fmt2(iqRes.Symbols)}  transcript {il} lines / {it} tokens");
        if (rec.Identifiers.Count > 0)
        {
          fpCall = Add(fpCall, fpRes.Callsigns); iqCall = Add(iqCall, iqRes.Callsigns);
          fpGrid = Add(fpGrid, fpRes.Grids); iqGrid = Add(iqGrid, iqRes.Grids);
          output.WriteLine($"  fp32 callsigns {Fmt2(fpRes.Callsigns)}  grids {Fmt2(fpRes.Grids)}");
          output.WriteLine($"  int8 callsigns {Fmt2(iqRes.Callsigns)}  grids {Fmt2(iqRes.Grids)}");
        }
      }

      output.WriteLine("");
      output.WriteLine($"CORPUS fp32: symbols {Fmt(fpSym)}  callsigns {Fmt(fpCall)}  grids {Fmt(fpGrid)}  " +
        $"transcript {fpLines} lines / {fpTokens} tokens");
      output.WriteLine($"CORPUS int8: symbols {Fmt(iqSym)}  callsigns {Fmt(iqCall)}  grids {Fmt(iqGrid)}  " +
        $"transcript {iqLines} lines / {iqTokens} tokens");

      // speed: fresh ARISS decode (ctor load + 56 clips) for each quantization
      output.WriteLine("");
      TimeDecode("fp32", () => SherpaOnnxEngine.Hotwords());
      TimeDecode("int8", () => SherpaOnnxEngine.Hotwords(2.5f, int8: true));
    }

    // total closed lines and non-empty display tokens the FmTranscriptBuilder (the shipped §10.2 view)
    // produces from one recording's per-transmission words — the metric that speaks to the transcript, not
    // the identifier assembly
    private static (int Lines, int Tokens) TranscriptSize(List<AsrWord[]> transmissions)
    {
      var builder = new FmTranscriptBuilder();
      foreach (var tx in transmissions.Where(t => t.Length > 0).OrderBy(t => t[0].StartSeconds))
        builder.Add(tx.Min(w => w.StartSeconds), tx.Max(w => w.EndSeconds), tx);
      builder.Flush();
      int tokens = builder.Lines.Sum(l => l.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);
      return (builder.Lines.Count, tokens);
    }

    private void TimeDecode(string label, Func<SherpaOnnxEngine> factory)
    {
      var sw = System.Diagnostics.Stopwatch.StartNew();
      using var engine = factory();
      long loadMs = sw.ElapsedMilliseconds;
      sw.Restart();
      var words = HeadlessRunner.TranscribeClips(engine, ArissClipDir);
      long decodeMs = sw.ElapsedMilliseconds;
      output.WriteLine($"{label}: load {loadMs} ms, ARISS 56-clip decode {decodeMs} ms " +
        $"({words.Sum(t => t.Length)} words)");
    }

    private static string Fmt2(EvalScore s) => $"P {s.Precision:0.00} R {s.Recall:0.00} F1 {s.F1:0.00}";

    [ManualFact("§13 hotwords-score sweep: re-decodes the corpus clips with sherpa hotword biasing at " +
      "each strength (cache tags sherpa10/sherpa/sherpa20/sherpa25/sherpa30) and reports the corpus " +
      "symbol track alone plus the policy-gated three-engine hybrid identifier tracks per score — the " +
      "§5.3/§13 question: does stronger biasing buy recall before it starts hallucinating phonetic " +
      "words from noise? 2026-07-18: monotone gains 1.0→3.0 on sherpa-alone symbols (0.62/0.39 → " +
      "0.66/0.46) and callsigns (0.53/0.29 → 0.63/0.53); the gated hybrid peaks at 2.5 (grids hold " +
      "0.94/0.94, at 3.0 they fall to 0.92/0.69) → 2.5 is the production default")]
    public void All_SherpaHotwordsScoreSweep()
    {
      var corpus = Corpus.Load(RepoFiles.Find(Path.Combine("corpus", "ground-truth.json")));
      var policy = new EmitPolicy();
      foreach (float score in new[] { 1.0f, 1.5f, 2.0f, 2.5f, 3.0f })
      {
        string tag = score == 1.5f ? "sherpa" : $"sherpa{(int)(score * 10)}";
        (int E, int C, int G, int R) sym = default, calls = default, grids = default,
          hybCalls = default, hybGrids = default;

        foreach (var rec in corpus.Recordings)
        {
          string clipDir = Path.Combine(DecodedDir, rec.File.Replace(".iq.wav", ""));
          if (rec.Role == "test" || !Directory.Exists(clipDir)) continue;
          var sherpa = CachedWords(tag, () => SherpaOnnxEngine.Hotwords(score), clipDir);
          var alone = HeadlessRunner.Run(sherpa, rec);
          sym = Add(sym, alone.Symbols);
          if (rec.Identifiers.Count == 0) continue;
          calls = Add(calls, alone.Callsigns);
          grids = Add(grids, alone.Grids);
          var hybrid = HeadlessRunner.Run(CachedWords("whisper", SidecarEngine.FasterWhisper, clipDir)
            .Concat(CachedWords("vosk", SidecarEngine.VoskGrammar, clipDir))
            .Concat(sherpa).ToList(), rec, policy);
          hybCalls = Add(hybCalls, hybrid.Callsigns);
          hybGrids = Add(hybGrids, hybrid.Grids);
        }

        output.WriteLine($"score {score:0.0}: symbols {Fmt(sym)}  callsigns {Fmt(calls)}  " +
          $"grids {Fmt(grids)}  | w+v+s policy: callsigns {Fmt(hybCalls)}  grids {Fmt(hybGrids)}");
      }
    }

    [ManualFact("§13 M6 fan-out measurement: whisper re-hears the ARISS pass through the @pad40 and " +
      "@sq85 DSP variants (Real_DemodAriss_FanOutVariants) and each variant pool joins the standard " +
      "three-engine pool at the identifier level — utterance-based corroboration counts a variant " +
      "duplicate as the same acoustic event, so fusion can only add content, not votes. Scores " +
      "raw + policy+depth per pool vs the single-DSP baseline; the recovered/unmatched lists show " +
      "whether the dropped-leading-Kilo class (KQ4GIK/KQ4RGI) or new weak partials appear. " +
      "2026-07-18 VERDICT — NOT WORTH IT: recall froze at 0.67 in every pool (zero new content — the " +
      "misses are acoustic, spike 2's teacher-forced finding), while precision fell (gated callsigns " +
      "0.93 → 0.72 with @pad40's K2H artifact + resurfaced WN3Y/W3NY; 0.93 → 0.85 with @sq85; grids " +
      "0.94 → 0.69/0.81 recall — @pad40's merged segments lose FM17). Variant mentions only add junk " +
      "utterance corroboration that pushes leaks past the calibrated gates; M6 closes")]
    public void Ariss_FanOutVariants_ScoreAgainstCorpusTruth()
    {
      var rec = ArissRecording();
      var policy = new EmitPolicy();
      var weight = new DepthConfidence();

      var baseWords = CachedWords("whisper", SidecarEngine.FasterWhisper, ArissClipDir)
        .Concat(CachedWords("vosk", SidecarEngine.VoskGrammar, ArissClipDir))
        .Concat(CachedWords("sherpa25", () => SherpaOnnxEngine.Hotwords(), ArissClipDir)).ToList();
      var d = HeadlessRunner.LoadDepths(ArissClipDir)!;
      var baseDepths = d.Concat(d).Concat(d).ToList();

      FanOutRow("baseline w+v+s", baseWords, baseDepths, rec, policy, weight);

      var pooledWords = new List<AsrWord[]>(baseWords);
      var pooledDepths = new List<double>(baseDepths);
      foreach (string variant in new[] { "@pad40", "@sq85" })
      {
        string vDir = ArissClipDir + variant;
        Assert.True(Directory.Exists(vDir), $"variant clips expected under {vDir} (run Real_DemodAriss_FanOutVariants)");
        var vWords = CachedWords("whisper" + variant, SidecarEngine.FasterWhisper, vDir, engineKey: "whisper");
        var vDepths = HeadlessRunner.LoadDepths(vDir)!;
        FanOutRow($"+ whisper{variant}", baseWords.Concat(vWords).ToList(),
          baseDepths.Concat(vDepths).ToList(), rec, policy, weight);
        pooledWords.AddRange(vWords);
        pooledDepths.AddRange(vDepths);
      }
      FanOutRow("+ both variants", pooledWords, pooledDepths, rec, policy, weight);
    }

    private void FanOutRow(string label, List<AsrWord[]> words, List<double> depths, CorpusRecording rec,
      EmitPolicy policy, DepthConfidence weight)
    {
      var raw = HeadlessRunner.Run(words, rec);
      var gated = HeadlessRunner.Run(words, rec, policy, depths, weight);
      output.WriteLine($"--- {label}");
      Report("  raw callsigns  ", raw.Callsigns);
      Report("  gated callsigns", gated.Callsigns);
      Report("  gated grids    ", gated.Grids);
    }

    private static (int, int, int, int) Add((int E, int C, int G, int R) t, EvalScore s)
      => (t.E + s.EmittedChars, t.C + s.CorrectChars, t.G + s.GoldChars, t.R + s.RecalledChars);

    private static string Fmt((int E, int C, int G, int R) t)
    {
      var s = new EvalScore
      {
        EmittedChars = t.E, CorrectChars = t.C, GoldChars = t.G, RecalledChars = t.R,
        RecoveredGold = [], Unmatched = []
      };
      return $"P {s.Precision:0.00} R {s.Recall:0.00} F1 {s.F1:0.00}";
    }

    private void ScoreAll(Func<string, List<AsrWord[]>> wordsFor)
    {
      var corpus = Corpus.Load(RepoFiles.Find(Path.Combine("corpus", "ground-truth.json")));
      var policy = new EmitPolicy();
      var depthWeight = new DepthConfidence();
      // totals split by corpus role (§11: the held-out test set is never tuned on — and never pooled
      // into the train numbers)
      var totals = new Dictionary<string, (int Emitted, int Correct, int Known, int Recalled)>();
      string role = "train";
      void Accumulate(string key, EvalScore s)
      {
        var t = totals.GetValueOrDefault($"{role} {key}");
        totals[$"{role} {key}"] = (t.Emitted + s.EmittedChars, t.Correct + s.CorrectChars,
          t.Known + s.GoldChars, t.Recalled + s.RecalledChars);
      }

      foreach (var rec in corpus.Recordings)
      {
        string clipDir = Path.Combine(DecodedDir, rec.File.Replace(".iq.wav", ""));
        if (!Directory.Exists(clipDir))
        {
          output.WriteLine($"skip {rec.File}: no decoded clips");
          continue;
        }
        role = rec.Role == "test" ? "TEST" : "train";
        var result = HeadlessRunner.Run(wordsFor(clipDir), rec);

        output.WriteLine($"--- {rec.File} [{rec.Role}]");
        foreach (var c in result.Fused)
          output.WriteLine($"{c.StartSeconds,7:0.0}s  {c.Kind,-8} {c.Text,-9} conf {c.Confidence:0.00}");

        // identifier tracks only where whole-identifier truth exists; the symbol track everywhere
        if (rec.Identifiers.Count > 0)
        {
          Report("callsigns", result.Callsigns);
          Report("grids", result.Grids);
          Accumulate("callsigns", result.Callsigns);
          Accumulate("grids", result.Grids);
          var gated = HeadlessRunner.Run(wordsFor(clipDir), rec, policy);
          Accumulate("callsigns+policy", gated.Callsigns);
          Accumulate("grids+policy", gated.Grids);

          // the full operating point: policy + the §5.2 role-b depth weight (per-clip depths repeated
          // per engine, since every engine contributes one word list per clip)
          var words = wordsFor(clipDir);
          var d = HeadlessRunner.LoadDepths(clipDir);
          var depths = d != null && d.Count > 0 && words.Count % d.Count == 0
            ? Enumerable.Repeat(d, words.Count / d.Count).SelectMany(x => x).ToList()
            : null;
          var gatedDepth = HeadlessRunner.Run(words, rec, policy, depths, depthWeight);
          Report("callsigns+policy+depth", gatedDepth.Callsigns);
          Accumulate("callsigns+policy+depth", gatedDepth.Callsigns);
          Accumulate("grids+policy+depth", gatedDepth.Grids);
        }
        Report("symbols", result.Symbols);
        Accumulate("symbols", result.Symbols);
      }

      foreach (var (key, t) in totals.OrderBy(kv => kv.Key.StartsWith("TEST") ? 1 : 0))
      {
        var s = new EvalScore
        {
          EmittedChars = t.Emitted, CorrectChars = t.Correct, GoldChars = t.Known, RecalledChars = t.Recalled,
          RecoveredGold = [], Unmatched = []
        };
        output.WriteLine($"CORPUS {key}: P {s.Precision:0.00} R {s.Recall:0.00} F1 {s.F1:0.00} " +
          $"({t.Correct}/{t.Emitted} emitted correct, {t.Recalled}/{t.Known} known recalled)");
      }
    }


    // ----------------------------------------------------------------------------------------------------
    //                                          shared plumbing
    // ----------------------------------------------------------------------------------------------------
    private static CorpusRecording ArissRecording()
      => Corpus.Load(RepoFiles.Find(Path.Combine("corpus", "ground-truth.json")))
        .Recordings.Single(r => r.File == "2026-07-04_23_03_57_ARISS.iq.wav");

    private void Score(string name, List<AsrWord[]> transmissions, CorpusRecording rec)
    {
      var result = HeadlessRunner.Run(transmissions, rec);

      output.WriteLine($"{name}: {transmissions.Count} transmissions, " +
        $"{transmissions.Count(t => t.Length > 0)} with speech");
      foreach (var c in result.Fused)
        output.WriteLine($"{c.StartSeconds,7:0.0}s  {c.Kind,-8} {c.Text,-9} conf {c.Confidence:0.00}");
      Report("callsigns", result.Callsigns);
      Report("grids", result.Grids);
      Report("symbols", result.Symbols);
      output.WriteLine("stored-transcript baseline (post partition): callsigns P 0.75 R 0.62 F1 0.68, " +
        "grids P 0.67 R 0.50, symbols P 0.82 R 0.95 F1 0.88");
      output.WriteLine("audio-E2E whisper baseline (post partition/fusion): callsigns P 0.68 R 0.56 F1 0.61, " +
        "grids P 0.75 R 0.94, symbols P 0.78 R 0.97 F1 0.87");
    }

    private List<AsrWord[]> CachedWords(string tag, Func<IAsrEngine> engineFactory, string clipDir,
      string? engineKey = null)
    {
      Assert.True(Directory.Exists(clipDir), $"decoded clips expected under {clipDir} (run FmDemodHarness first)");
      string cache = $"{clipDir}.{tag}.words.json";
      if (File.Exists(cache)) return HeadlessRunner.LoadWords(cache);

      engineKey ??= tag;
      if (!engines.TryGetValue(engineKey, out var engine)) engines[engineKey] = engine = engineFactory();
      var transmissions = HeadlessRunner.TranscribeClips(engine, clipDir);
      HeadlessRunner.SaveWords(cache, transmissions);
      return transmissions;
    }

    private void Report(string kind, EvalScore s)
      => output.WriteLine($"{kind}: P {s.Precision:0.00} R {s.Recall:0.00} F1 {s.F1:0.00}  " +
        $"recovered [{string.Join(" ", s.RecoveredGold)}]  unmatched [{string.Join(" ", s.Unmatched)}]  " +
        $"near [{string.Join(" ", s.NearMisses)}]");
  }
}
