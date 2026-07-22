using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace VE3NEA.SkyFM.Tests
{
  /// <summary>End-to-end result for one recording: the fused candidates and their per-kind scores
  /// against that recording's ground truth.</summary>
  public sealed record HeadlessResult
  {
    public required IReadOnlyList<Candidate> Fused { get; init; }
    public required EvalScore Callsigns { get; init; }
    public required EvalScore Grids { get; init; }
    public required EvalScore Symbols { get; init; }
  }

  /// <summary>
  /// The headless batch scorer of plan §10 / Milestone 3, living in the Tests project until an app
  /// project exists (G6/A5): per-transmission ASR words → Assembler → CandidateFusion → Eval against
  /// the corpus truth. Words come either from a stored spike transcript (<see cref="LoadTranscript"/>,
  /// the frozen baseline) or from a real <see cref="IAsrEngine"/> over the decoded per-transmission
  /// clips (<see cref="TranscribeClips"/>).
  /// </summary>
  public static class HeadlessRunner
  {
    public static HeadlessResult Run(IEnumerable<IReadOnlyList<AsrWord>> transmissions, CorpusRecording truth,
      EmitPolicy? policy = null, IReadOnlyList<double>? depths = null, DepthConfidence? depthWeight = null)
    {
      var all = transmissions.ToList();
      var options = new SkyFmOptions
      {
        Policy = policy ?? new EmitPolicy(),
        Depth = depthWeight ?? new DepthConfidence()
      };
      var (raw, gated) = SkyFmDecoder.Extract(all, depthWeight != null ? depths : null, options);
      var fused = policy != null ? gated : raw;

      return new HeadlessResult
      {
        Fused = fused,
        Callsigns = Eval.Score(fused, truth.Identifiers, CandidateKind.Callsign),
        Grids = Eval.Score(fused, truth.Identifiers, CandidateKind.Grid),
        Symbols = truth.Utterances.Count > 0
          ? SymbolEval.Score(SymbolEval.ToSymbols(all), truth.Utterances)
          : SymbolEval.Score(SymbolEval.ToSymbols(all), truth.Identifiers)
      };
    }

    /// <summary>Runs a real engine over the per-transmission clip WAVs that <see cref="FmDemodHarness"/>
    /// wrote for one recording (<c>decoded\&lt;name&gt;\segNN_&lt;start&gt;s.wav</c>), shifting word
    /// times from clip-relative to recording-relative so Eval's ±25 s truth window applies.</summary>
    public static List<AsrWord[]> TranscribeClips(IAsrEngine engine, string clipDir)
    {
      var transmissions = new List<AsrWord[]>();
      foreach (string path in Directory.GetFiles(clipDir, "seg*.wav").OrderBy(p => p))
      {
        double offset = ClipStartSeconds(path);
        var (samples, rate) = Wav16.Read(path);
        var hyps = engine.Transcribe(samples, rate);
        transmissions.Add(hyps.Count == 0
          ? []
          : hyps[0].Words.Select(w => new AsrWord(w.Text, w.StartSeconds + offset, w.EndSeconds + offset, w.Confidence))
            .ToArray());
      }
      return transmissions;
    }

    /// <summary>Extracts the recording-relative start time from a clip name like
    /// <c>seg07_147.3s.wav</c>.</summary>
    public static double ClipStartSeconds(string path)
    {
      string name = Path.GetFileNameWithoutExtension(path);
      int sep = name.IndexOf('_');
      return double.Parse(name[(sep + 1)..^1], CultureInfo.InvariantCulture);
    }

    /// <summary>Saves per-transmission word sequences (recording-relative times) so slow engine runs
    /// are transcribed once and reused, e.g. by the hybrid harness.</summary>
    public static void SaveWords(string path, IReadOnlyList<AsrWord[]> transmissions)
      => File.WriteAllText(path, JsonSerializer.Serialize(transmissions.Select(t =>
        t.Select(w => new { w = w.Text, s = w.StartSeconds, e = w.EndSeconds, p = w.Confidence }))));

    /// <summary>Loads the per-clip quieting depths (dB; -1 = unknown) that <see cref="FmDemodHarness"/>
    /// wrote beside the clips, in the same sorted-clip order <see cref="TranscribeClips"/> uses — the
    /// index-aligned depth list for <see cref="Run"/>. Null when the recording predates depth
    /// export.</summary>
    public static List<double>? LoadDepths(string clipDir)
    {
      string path = Path.Combine(clipDir, "depths.json");
      if (!File.Exists(path)) return null;
      using var doc = JsonDocument.Parse(File.ReadAllText(path));
      var byClip = doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetDouble());
      return Directory.GetFiles(clipDir, "seg*.wav").OrderBy(p => p)
        .Select(p => byClip.GetValueOrDefault(Path.GetFileName(p), -1.0)).ToList();
    }

    /// <summary>Loads word sequences saved by <see cref="SaveWords"/>.</summary>
    public static List<AsrWord[]> LoadWords(string path)
    {
      using var doc = JsonDocument.Parse(File.ReadAllText(path));
      return doc.RootElement.EnumerateArray()
        .Select(t => t.EnumerateArray()
          .Select(w => new AsrWord(
            w.GetProperty("w").GetString()!,
            w.GetProperty("s").GetDouble(),
            w.GetProperty("e").GetDouble(),
            (float)w.GetProperty("p").GetDouble()))
          .ToArray())
        .ToList();
    }

    /// <summary>Loads a spike faster-whisper transcript JSON (segments → words {w,s,e,p}) as
    /// per-transmission word sequences.</summary>
    public static List<AsrWord[]> LoadTranscript(string path)
    {
      using var doc = JsonDocument.Parse(File.ReadAllText(path));
      return doc.RootElement.GetProperty("segments").EnumerateArray()
        .Select(seg => seg.GetProperty("words").EnumerateArray()
          .Select(w => new AsrWord(
            w.GetProperty("w").GetString()!,
            w.GetProperty("s").GetDouble(),
            w.GetProperty("e").GetDouble(),
            (float)w.GetProperty("p").GetDouble()))
          .ToArray())
        .ToList();
    }
  }
}
