using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using VE3NEA.SkyTlm.Core;

namespace VE3NEA.SkyTlm.IO
{
  /// <summary>
  /// Reads/writes the per-corpus-clip <c>&lt;flavor&gt;.wav.json</c> sidecar that carries the resolved
  /// <see cref="SignalParams"/> a corpus clip decodes with. The corpus regression (CorpusDecodeTests) and the
  /// profiling bench read these instead of hard-coding the params in source, so the params live as data next to
  /// the wav. <c>SampleRate</c> is not stored — it is filled from the loaded wav by the consumer.
  /// </summary>
  public static class SignalParamsSidecar
  {
    /// <summary>Load the resolved params from <paramref name="path"/> (the wav path + <c>.json</c>).</summary>
    public static SignalParams Load(string path)
    {
      var o = JObject.Parse(File.ReadAllText(path));
      return new SignalParams(
        (double)o["baud"]!,
        Enum.Parse<Modulation>((string)o["modulation"]!, ignoreCase: true),
        Enum.Parse<Framing>((string)o["framing"]!, ignoreCase: true),
        0,
        (double?)o["deviation"])
      {
        Manchester = (bool?)o["manchester"],
        Differential = (bool?)o["differential"],
        RsBasis = (string?)o["rsBasis"],
        FrameSize = (int?)o["frameSize"],
      };
    }

    /// <summary>Write the resolved params next to a corpus clip (used by the corpus generator).</summary>
    public static void Save(string path, SignalParams p)
    {
      var o = new JObject
      {
        ["baud"] = p.Baud,
        ["modulation"] = p.Modulation.ToString(),
        ["framing"] = p.Framing.ToString(),
      };
      if (p.Deviation is { } dev) o["deviation"] = dev;
      if (p.Manchester is { } man) o["manchester"] = man;
      if (p.Differential is { } diff) o["differential"] = diff;
      if (p.RsBasis is { } basis) o["rsBasis"] = basis;
      if (p.FrameSize is { } fsz) o["frameSize"] = fsz;
      File.WriteAllText(path, o.ToString(Formatting.Indented));
    }
  }
}
