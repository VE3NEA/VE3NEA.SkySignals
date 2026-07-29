using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using VE3NEA.SkyTlm.IO;
using Xunit;
using Xunit.Abstractions;

namespace VE3NEA.SkySSTV.Tests
{
  /// <summary>
  /// Diagnostic probes for the VIS-header detector (<see cref="SstvVisDetector"/>). They dump the
  /// per-gate coherences the detector reduces to a single bool, which is what a capture whose VIS is
  /// plainly present but undetected needs in order to name the gate that rejected it.
  ///
  /// <para>These found the 2026-07-29 miss: real transmitters split into two header families, and the
  /// spec-shaped one is the minority. See <see cref="SstvVisDetector"/>'s StopSlack for the finding and
  /// <see cref="VisSlotDump"/> for the evidence.</para>
  /// </summary>
  public class SstvVisProbe
  {
    private static readonly string RecordingsDir =
      @"C:\Users\alsho\AppData\Roaming\Afreet\Products\SkyRoof\Recordings\SSTV";

    private readonly ITestOutputHelper output;
    public SstvVisProbe(ITestOutputHelper o) => output = o;

    private static string Wav() => Path.Combine(RecordingsDir,
      Environment.GetEnvironmentVariable("SSTV_PROBE_WAV") ?? "2026-07-29_10_01_16_Monitor-3.iq.wav");

    private static double EnvDouble(string name, double dflt)
      => double.TryParse(Environment.GetEnvironmentVariable(name), out double v) ? v : dflt;

    /// <summary>Read a capture and run it through Stage-1 + Stage-2, i.e. the exact signal the detector
    /// sees. Returns null when the capture is absent.</summary>
    private (double[] Band, int Rate, int Samples)? Load()
    {
      string wav = Wav();
      if (!File.Exists(wav)) { output.WriteLine($"absent: {wav}"); return null; }
      var (iq, sr) = WavIqReader.Read(wav);
      var o = new SstvDecodeOptions { SampleRate = sr };
      return (SstvDecoder.SyncAudio(SstvDecoder.Discriminator(iq, o), sr, o), sr, iq.Length);
    }


    // ----------------------------------------------------------------------------------------------------
    //                                       what the detector finds
    // ----------------------------------------------------------------------------------------------------


    [ManualFact("Result 2026-07-29 (after the StopSlack + LeaderGate fix): 22 headers over the 23 captures, " +
      "every one decoding 0x88 = Robot 36, matching each recording's transmitter metadata, with no other " +
      "byte reported anywhere. The stop-lag column splits the corpus into the two header families exactly " +
      "along transmitter lines: 0 ms for UmKA-1 and SAKHACUBE-CHOLBON, 38-49 ms for UTMN2, Monitor-3 and " +
      "VIZARD-meteo. Before the fix only the SAKHACUBE family was detected at all. Set SSTV_PROBE_WAV to " +
      "check a single capture instead of the whole folder.")]
    public void VisCorpusCheck()
    {
      if (!Directory.Exists(RecordingsDir)) { output.WriteLine("recordings absent"); return; }
      var files = Environment.GetEnvironmentVariable("SSTV_PROBE_WAV") is string one && one.Length > 0
        ? new[] { Path.Combine(RecordingsDir, one) }
        : Directory.GetFiles(RecordingsDir, "*.iq.wav");
      foreach (string wav in files)
      {
        var (iq, sr) = WavIqReader.Read(wav);
        var o = new SstvDecodeOptions { SampleRate = sr };
        double[] band = SstvDecoder.SyncAudio(SstvDecoder.Discriminator(iq, o), sr, o);
        var hits = SstvVisDetector.DetectAll(band, sr);
        output.WriteLine($"{Path.GetFileName(wav)} ({iq.Length / sr:0} s): {hits.Count} VIS");
        foreach (var h in hits)
          output.WriteLine($"    t0={h.T0Sample / (double)sr:0.000}s vis=0x{h.VisByte:X2} mode={h.Mode} " +
            $"score={h.Score:0.000} imageStart={h.HeaderEndSample / (double)sr:0.000}s " +
            $"(stop lag {(h.HeaderEndSample - h.T0Sample) / (double)sr * 1000 - 910:0} ms)");
      }
    }

    [ManualFact("Result 2026-07-29 (the reported capture, after the fix): 2 completed Robot36 images — " +
      "@52.4 s comb-seeded and @346.6 s VIS-seeded (fromVis=True). Before the fix the 346.6 s header was " +
      "read correctly (right t0, 0x08, valid parity) and then thrown away by the stop-bit gate.")]
    public void StreamingVisCheck()
    {
      if (!Directory.Exists(RecordingsDir)) { output.WriteLine("recordings absent"); return; }
      var files = Environment.GetEnvironmentVariable("SSTV_PROBE_WAV") is string one && one.Length > 0
        ? new[] { Path.Combine(RecordingsDir, one) }
        : Directory.GetFiles(RecordingsDir, "*.iq.wav");

      foreach (string wav in files)
      {
        var (iq, sr) = WavIqReader.Read(wav);
        using var dec = new SstvDecoder(new SstvDecodeOptions { SampleRate = sr });
        var done = new List<SstvImageEvent>();
        dec.ImageCompleted += e => done.Add(e);

        int block = (int)sr;
        for (int at = 0; at < iq.Length; at += block)
          dec.Process(iq.AsSpan(at, Math.Min(block, iq.Length - at)));
        dec.Flush();

        string? pngDir = Environment.GetEnvironmentVariable("SSTV_PROBE_PNG");
        output.WriteLine($"{Path.GetFileName(wav)}: {done.Count} completed image(s)");
        foreach (var e in done)
        {
          output.WriteLine($"    id={e.ImageId} {e.Mode} start={e.StartSeconds:0.000}s " +
            $"fromVis={e.FromVis} rows={e.ValidRows}");
          if (pngDir == null) continue;
          Directory.CreateDirectory(pngDir);
          e.Image.SavePng(Path.Combine(pngDir,
            $"{Path.GetFileNameWithoutExtension(wav)}_{e.StartSeconds:0}s_{e.Mode}.png"));
        }
      }
    }


    [ManualFact("diagnostic probe: set SSTV_PROBE_T / SSTV_PROBE_SPAN to bound the reported window")]
    public void TrainLifecycleDump()
    {
      var loaded = Load();
      if (loaded is not (double[] band, int sr, _)) return;
      double from = EnvDouble("SSTV_PROBE_T", 0.0);
      double span = EnvDouble("SSTV_PROBE_SPAN", 1e9);

      // drive the detection chain exactly as SstvDecoder.Advance does: every VIS tile runs, and seeds,
      // before the sync samples it covers are handed to the chain
      var chain = new SstvDetectionChain(sr);
      int visStep = (int)Math.Round(3.0 * sr);
      int visHeader = SstvVisDetector.HeaderSamples(sr);
      int visBit = (int)Math.Round(SstvTones.VisBitMs / 1000.0 * sr);

      var seen = new Dictionary<SstvPulseTrain, string>();
      void Report(long at)
      {
        double t = at / (double)sr;
        if (t < from || t > from + span) return;
        foreach (var train in chain.Extractor.Trains)
        {
          string now = $"{train.GetType().Name} {train.Format} state={train.State} " +
            $"line0={train.GetLineOnset(0) / sr:0.000}s pulses={train.PulseCnt} " +
            $"lines={chain.Extractor.ClaimedLines(train)}";
          if (seen.TryGetValue(train, out string? was) && was == now) continue;
          seen[train] = now;
          output.WriteLine($"  t={t,8:0.00}s  {(was == null ? "NEW " : "    ")}{now}");
        }
      }

      for (long cursor = 0; cursor + visStep + visHeader + visBit <= band.Length; cursor += visStep)
      {
        int copyLen = (int)Math.Min(visStep + visHeader + visBit, band.Length - cursor);
        var tile = new double[copyLen];
        Array.Copy(band, cursor, tile, 0, copyLen);
        var hit = SstvVisDetector.Detect(tile, sr, 0, visStep);
        if (hit.Found && hit.Mode is SstvMode mode)
        {
          double t = (hit.HeaderEndSample + cursor) / (double)sr;
          if (t >= from && t <= from + span)
            output.WriteLine($"  t={t,8:0.00}s  SEED VIS {mode} line0={t:0.000}s");
          chain.SeedVis(mode, (int)(hit.HeaderEndSample + cursor));
        }
        chain.Process(band.AsSpan((int)cursor, visStep));
        Report(cursor + visStep);
      }
      chain.Finish();
      Report(band.Length);
    }


    // ----------------------------------------------------------------------------------------------------
    //                                        why a gate rejects
    // ----------------------------------------------------------------------------------------------------


    [ManualFact("Result 2026-07-29 (pre-fix, on the reported capture): the ONE real header, at 345.666 s, " +
      "scored 0.407 with L1=0.237 L2=0.224 brk1200=0.235 start=0.321 stop=0.002, decoding 0x08 = Robot 36 " +
      "with valid parity — so t0 and the data were right and two gates were wrong. stop=0.002 is the " +
      "11-element family (see VisSlotDump); L1/L2 just under 0.25 is the old LeaderGate against a measured " +
      "ridge noise floor of 0.021. The sliding stop reads 0.333 @38 ms here and at most 0.089 on any " +
      "non-signal tile, which is what sized the replacement gate.")]
    public void VisGateDump()
    {
      var loaded = Load();
      if (loaded is not (double[] band, int sr, int samples)) return;
      output.WriteLine($"{Path.GetFileName(Wav())}: {samples} samples @ {sr} Hz = {samples / (double)sr:0.0} s");

      int S(double ms) => (int)Math.Round(ms / 1000.0 * sr);
      int leader = S(SstvTones.VisLeaderMs), brk = S(SstvTones.VisBreakMs), bit = S(SstvTones.VisBitMs);
      int oLeader2 = leader + brk, oStart = oLeader2 + leader;
      int oData0 = oStart + bit, oParity = oData0 + 7 * bit, oStop = oParity + bit;
      int headerLen = oStop + 3 * bit;

      int step = (int)Math.Round(3.0 * sr);
      var rows = new List<(double Score, string Line)>();

      for (long start = 0; start + step + headerLen < band.Length; start += step)
      {
        int bankLen = step + headerLen + bit;
        var b1900 = new SstvToneBank(band, sr, SstvTones.Center, (int)start, bankLen);
        var b1200 = new SstvToneBank(band, sr, SstvTones.Sync, (int)start, bankLen);
        var b1100 = new SstvToneBank(band, sr, SstvTones.VisBitOne, (int)start, bankLen);
        var b1300 = new SstvToneBank(band, sr, SstvTones.VisBitZero, (int)start, bankLen);

        int last = (int)Math.Min(start + step, band.Length - headerLen);
        double best = double.NegativeInfinity;
        int bestT0 = -1;
        for (int t0 = (int)start; t0 < last; t0++)
        {
          double s = b1900.Coherence(t0, t0 + leader)
                   + b1900.Coherence(t0 + oLeader2, t0 + oLeader2 + leader)
                   + b1200.Coherence(t0 + leader, t0 + leader + brk)
                   + b1200.Coherence(t0 + oStart, t0 + oStart + bit)
                   + b1200.Coherence(t0 + oStop, t0 + oStop + bit);
          if (s > best) { best = s; bestT0 = t0; }
        }
        if (bestT0 < 0) continue;

        // false-alarm reference for the ridge gate: the best leader-pair score anywhere in the tile,
        // independent of the other terms — the gate's real operating point on noise
        double ridgeMax = 0;
        for (int t0 = (int)start; t0 < last; t0 += 64)
          ridgeMax = Math.Max(ridgeMax, Math.Min(b1900.Coherence(t0, t0 + leader),
            b1900.Coherence(t0 + oLeader2, t0 + oLeader2 + leader)));

        double l1 = b1900.Coherence(bestT0, bestT0 + leader);
        double l2 = b1900.Coherence(bestT0 + oLeader2, bestT0 + oLeader2 + leader);
        double n12 = b1200.Coherence(bestT0 + leader, bestT0 + leader + brk);
        double n19 = b1900.Coherence(bestT0 + leader, bestT0 + leader + brk);
        double st = b1200.Coherence(bestT0 + oStart, bestT0 + oStart + bit);
        double sp = b1200.Coherence(bestT0 + oStop, bestT0 + oStop + bit);

        // the shipped behavior: locate the stop instead of pinning it to the nominal offset
        double spSlide = 0;
        int spOff = 0;
        for (int d = 0; d <= 2 * bit; d++)
        {
          double c = b1200.Coherence(bestT0 + oStop + d, bestT0 + oStop + d + bit);
          if (c > spSlide) { spSlide = c; spOff = d; }
        }

        int code = 0, ones = 0;
        var bits = new StringBuilder();
        for (int k = 0; k < 7; k++)
        {
          int a = bestT0 + oData0 + k * bit;
          int one = b1100.Coherence(a, a + bit) > b1300.Coherence(a, a + bit) ? 1 : 0;
          code |= one << k;
          ones += one;
          bits.Append(one);
        }
        int pa = bestT0 + oParity;
        int par = b1100.Coherence(pa, pa + bit) > b1300.Coherence(pa, pa + bit) ? 1 : 0;
        bool parityOk = ((ones + par) & 1) == 0;
        int visByte = SstvModes.EvenParityByte(code);

        string line =
          $"t={bestT0 / (double)sr,9:0.000}s score={best / 2.5,5:0.000} L1={l1:0.000} L2={l2:0.000} " +
          $"brk1200={n12:0.000} brk1900={n19:0.000} start={st:0.000} stopFixed={sp:0.000} " +
          $"stopSlide={spSlide:0.000}@{spOff * 1000.0 / sr:0}ms ridgeMax={ridgeMax:0.000} " +
          $"bits={bits} vis=0x{visByte:X2} par={(parityOk ? "ok" : "BAD")} " +
          $"mode={(SstvModes.FromVisByte(visByte)?.Name ?? "-")}";
        rows.Add((best / 2.5, line));
      }

      rows.Sort((a, b) => b.Score.CompareTo(a.Score));
      output.WriteLine($"tiles={rows.Count}; top 40 by score:");
      for (int i = 0; i < Math.Min(40, rows.Count); i++) output.WriteLine(rows[i].Line);
    }

    [ManualFact("Result 2026-07-29 (the finding): real transmitters send TWO different headers. On a clean " +
      "UTMN2 header (t0=26.5446 s, every slot at 0.49 of the 0.5 maximum) the slots read " +
      "1200 | 0 0 0 1 0 0 0 | 1 | 0 | 1200 — eleven elements, not ten. Slots 1-8 are the full 8-bit VIS " +
      "byte 0x88 LSB-first (parity MSB included), slot 9 is an even-parity bit over those 8, then the stop. " +
      "SAKHACUBE-CHOLBON at 100.3 s sends the spec's ten with the stop at slot 9. The 07-29 Monitor-3 " +
      "header shows the same eleven at ~0.2. Two independent checks pin the reading: the 8 bits equal the " +
      "known Robot 36 byte, and the extra bit equals that byte's own parity.")]
    public void VisSlotDump()
    {
      var loaded = Load();
      if (loaded is not (double[] band, int sr, _)) return;
      double from = EnvDouble("SSTV_PROBE_T", 345.0);
      double span = EnvDouble("SSTV_PROBE_SPAN", 3.0);

      int S(double ms) => (int)Math.Round(ms / 1000.0 * sr);
      int leader = S(SstvTones.VisLeaderMs), brk = S(SstvTones.VisBreakMs), bit = S(SstvTones.VisBitMs);
      int oLeader2 = leader + brk, oStart = oLeader2 + leader;

      int a = (int)(from * sr), len = (int)(span * sr);
      int bankLen = len + 2 * oStart;
      var b1900 = new SstvToneBank(band, sr, SstvTones.Center, a, bankLen);
      var b1200 = new SstvToneBank(band, sr, SstvTones.Sync, a, bankLen);
      var b1100 = new SstvToneBank(band, sr, SstvTones.VisBitOne, a, bankLen);
      var b1300 = new SstvToneBank(band, sr, SstvTones.VisBitZero, a, bankLen);

      // lock t0 on the leader/break/leader ridge alone, with no bit terms, so the bit grid is not begged
      double best = -1;
      int bestT0 = a;
      for (int t0 = a; t0 < a + len; t0++)
      {
        double s = b1900.Coherence(t0, t0 + leader) + b1900.Coherence(t0 + oLeader2, t0 + oLeader2 + leader)
                 + b1200.Coherence(t0 + leader, t0 + leader + brk);
        if (s > best) { best = s; bestT0 = t0; }
      }

      output.WriteLine($"{Path.GetFileName(Wav())}: leader lock t0={bestT0 / (double)sr:0.0000}s ridge={best:0.000}");
      output.WriteLine($"  L1={b1900.Coherence(bestT0, bestT0 + leader):0.000} " +
        $"L2={b1900.Coherence(bestT0 + oLeader2, bestT0 + oLeader2 + leader):0.000} " +
        $"brk1200={b1200.Coherence(bestT0 + leader, bestT0 + leader + brk):0.000} " +
        $"brk1900={b1900.Coherence(bestT0 + leader, bestT0 + leader + brk):0.000}");
      output.WriteLine("  slot   t(s)     c1100   c1200   c1300   c1900   winner");

      // deliberately more slots than the spec's header has, so a non-standard bit count shows up directly
      for (int k = 0; k < 14; k++)
      {
        int p = bestT0 + oStart + k * bit;
        double c11 = b1100.Coherence(p, p + bit), c12 = b1200.Coherence(p, p + bit);
        double c13 = b1300.Coherence(p, p + bit), c19 = b1900.Coherence(p, p + bit);
        string win = c11 >= c12 && c11 >= c13 && c11 >= c19 ? "1100(=1)"
                   : c12 >= c13 && c12 >= c19 ? "1200(start/stop)"
                   : c13 >= c19 ? "1300(=0)" : "1900";
        output.WriteLine($"  {k,4}  {p / (double)sr,8:0.0000}  {c11,6:0.000}  {c12,6:0.000}  " +
          $"{c13,6:0.000}  {c19,6:0.000}   {win}");
      }
    }


    // ----------------------------------------------------------------------------------------------------
    //                                       where the signal is
    // ----------------------------------------------------------------------------------------------------


    [ManualFact("Result 2026-07-29: on the reported capture only 345 s and 346 s (and weakly 189-190 s) " +
      "carry any signal at all — |iq| rms 0.0039 against a 0.0035 noise floor, in-band fraction 0.336 " +
      "against 0.225. That is what located the one header worth analyzing in a 437 s recording.")]
    public void SignalPresenceDump()
    {
      string wav = Wav();
      if (!File.Exists(wav)) { output.WriteLine($"absent: {wav}"); return; }
      var (iq, sr) = WavIqReader.Read(wav);
      var o = new SstvDecodeOptions { SampleRate = sr };
      double[] disc = SstvDecoder.Discriminator(iq, o);
      double[] band = SstvDecoder.SyncAudio(disc, sr, o);

      int sec = sr, w300 = (int)(0.3 * sr), w30 = (int)(0.03 * sr);
      output.WriteLine("  sec   |iq|rms  inBandFrac  discStd   c1900max(300ms)  c1200max(30ms)");
      for (int s = 0; s + sec <= band.Length; s += sec)
      {
        double mag = 0, be = 0, de = 0, dm = 0;
        for (int i = 0; i < sec; i++) { mag += iq[s + i].MagnitudeSquared; dm += disc[s + i]; }
        dm /= sec;
        for (int i = 0; i < sec; i++)
        {
          double d = disc[s + i] - dm;
          de += d * d;
          be += band[s + i] * band[s + i];
        }

        var b19 = new SstvToneBank(band, sr, SstvTones.Center, s, sec);
        var b12 = new SstvToneBank(band, sr, SstvTones.Sync, s, sec);
        double m19 = 0, m12 = 0;
        for (int t = s; t + w300 <= s + sec; t += w30 / 3) m19 = Math.Max(m19, b19.Coherence(t, t + w300));
        for (int t = s; t + w30 <= s + sec; t += w30 / 6) m12 = Math.Max(m12, b12.Coherence(t, t + w30));
        output.WriteLine($"{s / (double)sec,6:0}  {Math.Sqrt(mag / sec),8:0.0000}  {be / de,10:0.000}  " +
          $"{Math.Sqrt(de / sec),8:0}  {m19,10:0.000}  {m12,14:0.000}");
      }
    }

    [ManualFact("Result 2026-07-29: the 5 ms track is what resolved the header's trailing elements on a " +
      "clean UTMN2 capture — 1100 for 30 ms, then 1300 for 40 ms, then 1200 for 35 ms, then picture at " +
      "27.500 s. The nominal layout put the image start at 27.4546 s, 45 ms (a third of a Robot 36 line) " +
      "early; locating the stop puts it at 27.500 s.")]
    public void VisNeighborhoodDump()
    {
      var loaded = Load();
      if (loaded is not (double[] band, int sr, _)) return;
      double from = EnvDouble("SSTV_PROBE_T", 344.5);
      double span = EnvDouble("SSTV_PROBE_SPAN", 3.0);

      int a = (int)(from * sr), b = (int)Math.Min(band.Length, a + span * sr);
      int slice = (int)Math.Round(0.005 * sr);
      output.WriteLine($"{Path.GetFileName(Wav())}: dominant tone per 5 ms from {from:0.000}s ({sr} Hz)");
      output.WriteLine("  time      peakHz  power   c1100  c1200  c1300  c1900");

      for (int p = a; p + slice <= b; p += slice)
      {
        double mean = 0;
        for (int i = 0; i < slice; i++) mean += band[p + i];
        mean /= slice;
        double energy = 0;
        for (int i = 0; i < slice; i++) { double v = band[p + i] - mean; energy += v * v; }
        if (energy <= 0) energy = 1;

        double bestF = 0, bestP = 0;
        for (double f = 800; f <= 2600; f += 5)
        {
          double w = 2 * Math.PI * f / sr, re = 0, im = 0;
          for (int i = 0; i < slice; i++)
          {
            double v = band[p + i] - mean;
            re += v * Math.Cos(w * i);
            im -= v * Math.Sin(w * i);
          }
          double pw = (re * re + im * im) / (slice * energy);
          if (pw > bestP) { bestP = pw; bestF = f; }
        }

        double C(double f) => new SstvToneBank(band, sr, f, p, slice).Coherence(p, p + slice);
        output.WriteLine($" {p / (double)sr,9:0.000}  {bestF,6:0}  {bestP,6:0.000}  " +
          $"{C(1100),5:0.000}  {C(1200),5:0.000}  {C(1300),5:0.000}  {C(1900),5:0.000}");
      }
    }
  }
}
