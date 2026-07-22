using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace VE3NEA.SkyFM.Tests
{
  /// <summary>
  /// An <see cref="IAsrEngine"/> hosted as a persistent Python sidecar process over the spike's
  /// <c>asr-spike\.venv</c>, speaking line-delimited JSON (request <c>{"wav", "grammar"?}</c>, reply
  /// <c>{"words": [{"w","s","e","p"}], "score"}</c>). Two engines share the protocol: the Pass-A
  /// faster-whisper host (<c>asr_sidecar.py</c>, §5.3 as-built — the exact spike-validated runtime)
  /// and the Pass-B Vosk lgraph grammar host (<c>vosk_sidecar.py</c>, the spike-validated constrained
  /// path). Pass A/B are lab tooling (§5.3), so the Python dependency stays confined to the Tests
  /// project. The model loads once at construction; each <see cref="Transcribe"/> round-trips one
  /// temp WAV.
  /// </summary>
  public sealed class SidecarEngine : IAsrEngine
  {
    private readonly Process process;
    private readonly string[]? grammar;
    private readonly StringBuilder stderr = new();

    public string Name { get; }

    /// <summary>Pass A: Whisper <c>large-v3</c> int8 via faster-whisper (free transcription).</summary>
    public static SidecarEngine FasterWhisper() => new("asr_sidecar.py", "faster-whisper large-v3 int8", null);

    /// <summary>Pass B: Vosk <c>en-us-0.22-lgraph</c> constrained to the phonetic vocabulary
    /// (+ prowords + <c>[unk]</c>) — the spike-validated grammar decode with rank-correlated
    /// confidence.</summary>
    public static SidecarEngine VoskGrammar()
      => new("vosk_sidecar.py", "vosk lgraph grammar", PhoneticDecoder.VocabularyWords
        .Concat(["over", "copy", "this", "is", "roger", "negative", "affirmative"]).ToArray());

    private SidecarEngine(string script, string name, string[]? grammar)
    {
      Name = name;
      this.grammar = grammar;
      string scriptPath = RepoFiles.Find(Path.Combine("asr-spike", script));
      string spikeDir = Path.GetDirectoryName(scriptPath)!;
      string python = Path.Combine(spikeDir, ".venv", "Scripts", "python.exe");
      process = Process.Start(new ProcessStartInfo(python, $"\"{scriptPath}\"")
      {
        WorkingDirectory = spikeDir,
        RedirectStandardInput = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true
      })!;
      process.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (stderr) stderr.AppendLine(e.Data); };
      process.BeginErrorReadLine();

      string ready = ReadReply();
      using var doc = JsonDocument.Parse(ready);
      if (!doc.RootElement.TryGetProperty("ready", out _))
        throw new InvalidOperationException($"sidecar not ready: {ready}");
    }

    public IReadOnlyList<AsrHypothesis> Transcribe(ReadOnlySpan<float> audio, int sampleRate)
    {
      string wav = Path.Combine(Path.GetTempPath(), $"skyfm_asr_{Guid.NewGuid():N}.wav");
      try
      {
        Wav16.Write(wav, audio, sampleRate);
        process.StandardInput.WriteLine(JsonSerializer.Serialize(new { wav, grammar }));
        process.StandardInput.Flush();
        return ParseReply(ReadReply());
      }
      finally { File.Delete(wav); }
    }

    /// <summary>Parses one sidecar reply line into hypotheses — a single hypothesis with per-word
    /// confidences, or empty when no speech was recognized in the clip.</summary>
    public static IReadOnlyList<AsrHypothesis> ParseReply(string json)
    {
      using var doc = JsonDocument.Parse(json);
      if (doc.RootElement.TryGetProperty("error", out var err))
        throw new InvalidOperationException($"sidecar: {err.GetString()}");

      var words = new List<AsrWord>();
      foreach (var w in doc.RootElement.GetProperty("words").EnumerateArray())
        words.Add(new AsrWord(w.GetProperty("w").GetString()!, w.GetProperty("s").GetDouble(),
          w.GetProperty("e").GetDouble(), (float)w.GetProperty("p").GetDouble()));
      if (words.Count == 0) return Array.Empty<AsrHypothesis>();
      return new[] { new AsrHypothesis { Words = words, Score = doc.RootElement.GetProperty("score").GetDouble() } };
    }

    private string ReadReply()
    {
      string? line = process.StandardOutput.ReadLine();
      if (line != null) return line;
      string err; lock (stderr) err = stderr.ToString();
      throw new InvalidOperationException($"sidecar exited unexpectedly:\n{err}");
    }

    public void Dispose()
    {
      try
      {
        process.StandardInput.Close();
        if (!process.WaitForExit(5000)) process.Kill();
      }
      catch { }
      process.Dispose();
    }
  }
}
