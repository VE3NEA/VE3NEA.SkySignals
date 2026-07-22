using System.IO;
using VE3NEA.SkyTlm.IO;
using Xunit.Abstractions;

namespace VE3NEA.SkyFM.Tests
{
  /// <summary>
  /// The A5 demonstration on a real capture: the complete core pipeline — I/Q + options + the C#-native
  /// sherpa engine in, policy-gated candidates out — with no file IO or scoring inside the pipeline
  /// (the harness only reads the recording and prints). This is the production shape: everything in
  /// process, no sidecars.
  /// </summary>
  public class SkyFmDecoderHarness
  {
    private static readonly string ArissIq =
      @"C:\Users\alsho\AppData\Roaming\Afreet\Products\SkyRoof\Recordings\FM\2026-07-04_23_03_57_ARISS.iq.wav";

    private readonly ITestOutputHelper output;
    public SkyFmDecoderHarness(ITestOutputHelper o) => output = o;

    [ManualFact("A5 end-to-end on the real ARISS capture with the in-process sherpa hotwords engine: " +
      "I/Q → candidates, no files in the loop. 2026-07-18: 17 s wall for the 7.5 min pass, 56 " +
      "transmissions (matches FmDemodHarness), gated output is precision-first — EM85 0.99, FM18, " +
      "FN22, all uncorroborated flat-0.80 sherpa callsigns abstained; known wart: on the float audio " +
      "path (vs the 16-bit clips the caches came from) sherpa renders K2HZV as the truncation K2H in " +
      "two mentions, which corroborates to 0.96 and gates through — engine-level audio sensitivity, " +
      "not present in the production hybrid pool, and the 3-char text is below the ≥ 4-char " +
      "containment bound so it cannot absorb into K2HZV")]
    public void Ariss_SherpaInProcess_IqToCandidates()
    {
      var (iq, sampleRate) = WavIqReader.Read(ArissIq);
      using var engine = SherpaOnnxEngine.Hotwords();
      var res = SkyFmDecoder.Decode(iq, [engine],
        new SkyFmOptions { Fm = new FmDecodeOptions { SampleRate = sampleRate } });

      output.WriteLine($"{res.Fm.Transmissions.Count} transmissions");
      output.WriteLine("fused:");
      foreach (var c in res.Fused)
        output.WriteLine($"{c.StartSeconds,7:0.0}s  {c.Kind,-8} {c.Text,-9} conf {c.Confidence:0.00}");
      output.WriteLine("gated:");
      foreach (var c in res.Candidates)
        output.WriteLine($"{c.StartSeconds,7:0.0}s  {c.Kind,-8} {c.Text,-9} conf {c.Confidence:0.00}");
    }
  }
}
