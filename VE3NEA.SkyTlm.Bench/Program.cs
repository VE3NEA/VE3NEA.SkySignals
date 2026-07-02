using System.Diagnostics;
using MathNet.Numerics;
using VE3NEA.SkyTlm.IO;
using VE3NEA.SkyTlm.Core;

// headless decode bench over the regression corpus, for profiling the production StreamingPipeline in a
// single process (no xUnit / VSTest host). Run it under the VS Performance Profiler (Alt+F2 -> CPU Usage)
// or `dotnet-trace collect --format speedscope -- <this exe> bpsk 30`.
//
//   usage: VE3NEA.SkyTlm.Bench [filter] [iterations]
//     filter      substring matched against the wav file name (default "" = all 7 flavors); e.g. "bpsk"
//     iterations  timed decode passes per file after one untimed warm-up (default 20)

string filter = args.Length > 0 ? args[0] : "";
int iters = args.Length > 1 ? int.Parse(args[1]) : 20;
string wavDir = FindWavDir();

var files = Directory.GetFiles(wavDir, "*.wav")
  .Where(f => Path.GetFileName(f).Contains(filter, StringComparison.OrdinalIgnoreCase))
  .OrderBy(f => f)
  .ToList();

if (files.Count == 0)
{
  Console.WriteLine($"no corpus wavs matching \"{filter}\" in {wavDir}");
  return;
}

Console.WriteLine($"corpus: {wavDir}");
Console.WriteLine($"{files.Count} file(s), {iters} timed iteration(s) each (after 1 warm-up)\n");
Console.WriteLine($"{"File",-36} {"ms/run",9}  {"crc",4}");
Console.WriteLine($"{new string('-', 36)} {new string('-', 9)}  {new string('-', 4)}");

double grand = 0;
foreach (var path in files)
{
  string name = Path.GetFileName(path);
  var (samples, fs) = WavIqReader.Read(path);
  var p = SignalParamsSidecar.Load(path + ".json") with { SampleRate = fs };

  Decode(samples, fs, p);                       // warm up JIT / native init, untimed

  var sw = Stopwatch.StartNew();
  int crc = 0;
  for (int i = 0; i < iters; i++) crc = Decode(samples, fs, p);
  sw.Stop();

  double msPerRun = sw.Elapsed.TotalMilliseconds / iters;
  grand += msPerRun;
  Console.WriteLine($"{name,-36} {msPerRun,9:N1}  {crc,4}");
}
Console.WriteLine($"{new string('-', 36)} {new string('-', 9)}");
Console.WriteLine($"{"TOTAL (one pass over the set)",-36} {grand,9:N1}");

// one full pipeline decode of a corpus recording -> CRC-valid frame count. Mirrors
// corpusDecodeTests.DecodeCrcFrames so the bench exercises exactly the regression-tested path.
static int Decode(Complex32[] samples, int fs, SignalParams p)
{
  int crc = 0;
  using var sp = new StreamingPipeline(p, new StreamingOptions());
  sp.BurstDecoded += r => crc += r.Frames.Count(f => f.CrcValid == true);
  int block = Math.Max(1, (int)(0.1 * fs));
  for (int i = 0; i < samples.Length; i += block)
    sp.Push(samples.AsSpan(i, Math.Min(block, samples.Length - i)));
  sp.Flush();
  return crc;
}

// walk up from the bench output dir to the repo root (the folder holding VE3NEA.SkyTlm.slnx), then into the
// committed corpus under the test project. Mirrors TestPaths so the wavs are read in place, not copied.
static string FindWavDir()
{
  var dir = new DirectoryInfo(AppContext.BaseDirectory);
  while (dir != null)
  {
    if (File.Exists(Path.Combine(dir.FullName, "VE3NEA.SkyTlm.slnx")))
      return Path.Combine(dir.FullName, "VE3NEA.SkyTlm.Tests", "Data", "Wav");
    dir = dir.Parent;
  }
  throw new DirectoryNotFoundException("could not locate VE3NEA.SkyTlm.slnx above " + AppContext.BaseDirectory);
}
