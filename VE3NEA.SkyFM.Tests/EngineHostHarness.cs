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
    private readonly Dictionary<string, SidecarEngine> engines = new();

    public EngineHostHarness(ITestOutputHelper o) => output = o;

    public void Dispose()
    {
      foreach (var e in engines.Values) e.Dispose();
    }


    // ----------------------------------------------------------------------------------------------------
    //                                 single-recording harnesses (ARISS)
    // ----------------------------------------------------------------------------------------------------
    [ManualFact("2026-07-18: 56/56 clips decoded with speech, 12.5 min CPU — audio-E2E free-ASR baseline: " +
      "callsigns P 0.63 R 0.56 F1 0.59, grids P 0.55 R 0.69 F1 0.61, symbols P 0.76 R 0.97 F1 0.85; " +
      "vs stored-transcript baseline per-clip decode trades callsign/symbol precision for grid recall " +
      "(FM17 FM22 recovered; EM85KR WN3Y artifacts)")]
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

    [ManualFact("2026-07-18: hybrid beats whisper-alone on callsigns P 0.65 R 0.62 F1 0.63 (KR4JIQ added " +
      "by vosk, KD0QPC corroborated to conf 1.00), grids unchanged P 0.55 R 0.69, symbol recall 1.00 — " +
      "all 12 Gold covered; caches make re-runs fast (symbol row counts both engines' words)")]
    public void Ariss_HybridWhisperVosk_DecodedClips_ScoreAgainstCorpusTruth()
      => Score("hybrid whisper+vosk", CachedWords("whisper", SidecarEngine.FasterWhisper, ArissClipDir)
        .Concat(CachedWords("vosk", SidecarEngine.VoskGrammar, ArissClipDir)).ToList(), ArissRecording());


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
      "07-12 SO-50 (3IW4HM unheard), R 0.19 on 07-13 AO-123 (WN3Y/FM18 mostly missed)")]
    public void All_FasterWhisper_ScoreAgainstCorpusTruth()
      => ScoreAll(dir => CachedWords("whisper", SidecarEngine.FasterWhisper, dir));

    [ManualFact("2026-07-18 (symbol-utterance truth): CORPUS symbols P 0.83 R 0.63 F1 0.72 — at the " +
      "symbol level vosk adds only +3 recalled symbols over whisper-alone (its decodes overlap), but " +
      "the ARISS identifier track keeps the hybrid gain (callsigns P 0.65 R 0.62 vs 0.63/0.56): " +
      "fusion pays at assembly, not at raw symbols")]
    public void All_HybridWhisperVosk_ScoreAgainstCorpusTruth()
      => ScoreAll(dir => CachedWords("whisper", SidecarEngine.FasterWhisper, dir)
        .Concat(CachedWords("vosk", SidecarEngine.VoskGrammar, dir)).ToList());

    private void ScoreAll(Func<string, List<AsrWord[]>> wordsFor)
    {
      var corpus = Corpus.Load(RepoFiles.Find(Path.Combine("corpus", "ground-truth.json")));
      var totals = new Dictionary<string, (int Emitted, int Correct, int Known, int Recalled)>
      {
        ["callsigns"] = default,
        ["grids"] = default,
        ["symbols"] = default
      };
      void Accumulate(string key, EvalScore s)
      {
        var t = totals[key];
        totals[key] = (t.Emitted + s.EmittedChars, t.Correct + s.CorrectChars,
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
        var result = HeadlessRunner.Run(wordsFor(clipDir), rec);

        output.WriteLine($"--- {rec.File}");
        foreach (var c in result.Fused)
          output.WriteLine($"{c.StartSeconds,7:0.0}s  {c.Kind,-8} {c.Text,-9} conf {c.Confidence:0.00}");

        // identifier tracks only where whole-identifier truth exists; the symbol track everywhere
        if (rec.Identifiers.Count > 0)
        {
          Report("callsigns", result.Callsigns);
          Report("grids", result.Grids);
          Accumulate("callsigns", result.Callsigns);
          Accumulate("grids", result.Grids);
        }
        Report("symbols", result.Symbols);
        Accumulate("symbols", result.Symbols);
      }

      foreach (var (key, t) in totals)
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
      output.WriteLine("stored-transcript baseline: callsigns P 0.75 R 0.62 F1 0.68, grids P 0.50 R 0.50, " +
        "symbols P 0.82 R 0.95 F1 0.88");
      output.WriteLine("audio-E2E whisper baseline: callsigns P 0.63 R 0.56 F1 0.59, grids P 0.55 R 0.69, " +
        "symbols P 0.76 R 0.97 F1 0.85");
    }

    private List<AsrWord[]> CachedWords(string tag, Func<SidecarEngine> engineFactory, string clipDir)
    {
      Assert.True(Directory.Exists(clipDir), $"decoded clips expected under {clipDir} (run FmDemodHarness first)");
      string cache = $"{clipDir}.{tag}.words.json";
      if (File.Exists(cache)) return HeadlessRunner.LoadWords(cache);

      if (!engines.TryGetValue(tag, out var engine)) engines[tag] = engine = engineFactory();
      var transmissions = HeadlessRunner.TranscribeClips(engine, clipDir);
      HeadlessRunner.SaveWords(cache, transmissions);
      return transmissions;
    }

    private void Report(string kind, EvalScore s)
      => output.WriteLine($"{kind}: P {s.Precision:0.00} R {s.Recall:0.00} F1 {s.F1:0.00}  " +
        $"recovered [{string.Join(" ", s.RecoveredGold)}]  unmatched [{string.Join(" ", s.Unmatched)}]");
  }
}
