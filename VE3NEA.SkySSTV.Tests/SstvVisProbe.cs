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


    [ManualFact("Result 2026-07-30 (after the duration-weighted score replaced the per-element level " +
      "gates): 35 headers over the 27 captures, up from 26, with every one of the 26 retained to within a " +
      "sample. Every hit decodes 0x88 = Robot 36, matching each recording's transmitter metadata, with no " +
      "other byte reported anywhere in the corpus — which is the false-alarm evidence, since a false hit " +
      "would have to land on the one valid byte by luck. The stop-lag column still splits the corpus into " +
      "the two header families exactly along transmitter lines: 0 ms for UmKA-1 and SAKHACUBE-CHOLBON, " +
      "38-57 ms for UTMN2, Monitor-3 and VIZARD-meteo. The 9 newly found headers are all on weak passes; " +
      "3 are on the 07-30 SAKHACUBE capture that prompted the fix, whose three headers sit on an exactly " +
      "180 s cycle (76.2 / 255.2 / 435.4 s). Set SSTV_PROBE_WAV to check a single capture.")]
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

    [ManualFact("Result 2026-07-30 (the reported capture, after the fix): 2 completed Robot36 images, both " +
      "VIS-seeded (fromVis=True), @77.12 s and @256.14 s, 240 rows each — noisy but geometrically stable, " +
      "so the seeds are on the right sample. The third header @435.4 s starts 4 s before the signal fades " +
      "out at the end of the pass, so no image completes from it. Before the fix: 0 VIS, 0 images.")]
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


    [ManualFact("Result 2026-07-30: what sizes HeaderGate. Over 3552 tiles of the 27 captures the " +
      "duration-weighted score separates 2.1x with nothing in the gap — detected headers run 0.222 (the " +
      "weakest, on the 07-30 capture) to 0.987, and the loudest tile holding no header reaches 0.104, with " +
      "every other file at or under 0.087. HeaderGate = 0.15 is essentially the geometric mean of the two. " +
      "That margin is modest, and it is the honest one: it is what a 910 ms coherent statistic buys on " +
      "passes this weak. No single element comes close — over the same corpus noise sits within a factor " +
      "of 2 of real signal on the 30 ms bits and ABOVE it on the 10 ms break, which is why level is " +
      "decided once, here, rather than per element.")]
    public void VisScoreFloorDump()
    {
      if (!Directory.Exists(RecordingsDir)) { output.WriteLine("recordings absent"); return; }
      var files = Environment.GetEnvironmentVariable("SSTV_PROBE_WAV") is string one && one.Length > 0
        ? new[] { Path.Combine(RecordingsDir, one) }
        : Directory.GetFiles(RecordingsDir, "*.iq.wav");

      double worstHit = 1.0, bestMiss = 0.0;
      int tiles = 0, hits = 0;
      foreach (string wav in files)
      {
        var (iq, sr) = WavIqReader.Read(wav);
        var o = new SstvDecodeOptions { SampleRate = sr };
        double[] band = SstvDecoder.SyncAudio(SstvDecoder.Discriminator(iq, o), sr, o);

        int header = SstvVisDetector.HeaderSamples(sr);
        int step = (int)Math.Round(3.0 * sr);
        int leader = (int)Math.Round(SstvTones.VisLeaderMs / 1000.0 * sr);
        int oLeader2 = leader + (int)Math.Round(SstvTones.VisBreakMs / 1000.0 * sr);

        // a rejected tile only measures the NOISE floor if no header is present in it, and there are three
        // ways one can be. Two are excluded here: the 300 ms leader pair is the signal-independent way to
        // say a header is absent (its own noise level, 0.019, sits an order of magnitude under any real
        // header), and a tile adjacent to a found header is dropped because its best in-range t0 gets
        // pinned to the tile edge, where it partially overlaps the real thing. The third — a header that
        // fades mid-way, so its bits are unreadable and parity fails honestly — the ridge test catches
        // (07-08_23_31 @65.5 s: L1=0.447, L2=0.108, data slots at noise).
        var found = new List<int>();
        var rest = new List<(int T0, double Score)>();
        double fileWorst = 1.0;
        for (int start = 0; start + header < band.Length; start += step)
        {
          var hit = SstvVisDetector.Detect(band, sr, start, step);
          tiles++;
          if (hit.Found) { hits++; found.Add(hit.T0Sample); fileWorst = Math.Min(fileWorst, hit.Score); continue; }

          var b1900 = new SstvToneBank(band, sr, SstvTones.Center, hit.T0Sample, oLeader2 + leader);
          double ridge = Math.Min(b1900.Coherence(hit.T0Sample, hit.T0Sample + leader),
            b1900.Coherence(hit.T0Sample + oLeader2, hit.T0Sample + oLeader2 + leader));
          if (ridge < 0.04) rest.Add((hit.T0Sample, hit.Score));
        }

        double fileMiss = 0, missAt = 0;
        foreach (var (t0, sc) in rest)
        {
          if (sc <= fileMiss) continue;
          if (found.Exists(f => Math.Abs(f - t0) < header)) continue;
          fileMiss = sc;
          missAt = t0 / (double)sr;
        }
        output.WriteLine($"{Path.GetFileName(wav),-48} worstHit={(fileWorst > 1 ? 0 : fileWorst),6:0.000} " +
          $"bestMiss={fileMiss,6:0.000} @{missAt,8:0.000}s");
        if (fileWorst <= 1.0) worstHit = Math.Min(worstHit, fileWorst);
        bestMiss = Math.Max(bestMiss, fileMiss);
      }
      output.WriteLine($"{tiles} tiles, {hits} headers: weakest header {worstHit:0.000}, " +
        $"loudest non-header {bestMiss:0.000}, separation {worstHit / bestMiss:0.0}x");
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


    [ManualFact("Result 2026-07-30 (pre-fix, on the 07-30 SAKHACUBE capture): the three real headers all " +
      "read the right t0 and decoded 0x88 with valid parity, and all three were thrown away by a level " +
      "gate — 435.408 s by the stop (0.071 against BitGate 0.20), 255.228 s by the break (0.089) and the " +
      "start (0.177), 76.211 s by all of them plus the leader. What the ridgeMax column shows is why no " +
      "constant could have worked: the leader-pair noise floor is 0.019 over 300 ms, but the tiles that " +
      "outscored the real headers are pure noise carrying brk1200 = 0.37-0.41, because a 10 ms coherence " +
      "has ~30x the noise variance of a 300 ms one and the old search weighted the two equally. " +
      "(2026-07-29, same probe: the one real header at 345.666 s read L1=0.237 L2=0.224 stop=0.002 — the " +
      "11-element family, see VisSlotDump — against the then-0.25 LeaderGate.)")]
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
