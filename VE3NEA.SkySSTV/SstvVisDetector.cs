using System;
using System.Collections.Generic;

namespace VE3NEA.SkySSTV
{
  /// <summary>Outcome of a VIS-header search (plan §4).</summary>
  /// <param name="Found">All structural gates and parity passed at <see cref="T0Sample"/>.</param>
  /// <param name="VisByte">Decoded 8-bit VIS byte (7 data + even-parity MSB), or −1 if not found.</param>
  /// <param name="Mode">Supported mode the byte maps to, or null (byte valid but unsupported / not found).</param>
  /// <param name="T0Sample">Absolute sample index of the VIS leader onset.</param>
  /// <param name="HeaderEndSample">Absolute sample index just after the stop bit = the first line's sync onset.</param>
  /// <param name="Score">Soft VIS-hypothesis likelihood in [0, 1] (the §4 MHT prior, P4): the share of the
  /// header's energy lying on the expected tone track, each element weighted by its own duration. Set even
  /// when not found.</param>
  /// <param name="ParityOk">Whether even parity held over the decoded data + parity bits.</param>
  public readonly record struct SstvVisResult(
    bool Found, int VisByte, SstvMode? Mode, int T0Sample, int HeaderEndSample, double Score, bool ParityOk);

  /// <summary>
  /// VIS-header detector (plan §4): a normalized, zero-mean matched filter on the (time, tone) surface
  /// of the FM-discriminated audio, computed from a sparse Goertzel-style tone bank
  /// (<see cref="SstvToneBank"/>) at 1100 / 1200 / 1300 / 1900 Hz — no dense spectrogram. Because this is
  /// FM-on-FM, Doppler is a DC term the discriminator mean removes (plan §1.6), so the tones sit at
  /// <b>absolute</b> frequencies and no frequency-offset search is needed.
  ///
  /// <para>The header is: 300 ms leader @1900, 10 ms break @1200, 300 ms leader @1900, then ten 30 ms
  /// bits — start @1200, 7 data (LSB-first, 1=1100 / 0=1300), even-parity, stop @1200. The detector
  /// slides t0, scores the data-independent tone pattern, and at the best t0 applies hard structural
  /// gates — the leader ridge, the <b>break notch</b> (a dip a sustained carrier physically cannot fake),
  /// start/stop = 1200, and a parity-valid decode — before declaring a hit. Emits a soft score to seed the
  /// P4 MHT prior. The stop bit is the one element real transmitters do not all put where the spec says,
  /// so it is searched for rather than assumed — see <see cref="StopSlack"/>.</para>
  ///
  /// <para>Both the score and the level gate run over the <b>whole</b> header, each element weighted by its
  /// own duration, because a coherence is only as trustworthy as its window is long — see the gate
  /// constants below for the measurement that forced this and what it replaced.</para>
  /// </summary>
  internal static class SstvVisDetector
  {
    // coherence is in [0, 0.5] (a real tone splits between ±f); a clean matched tone ≈ 0.5. It is the
    // share of a window's energy sitting on one tone, so on noise it falls roughly as 1/(bandwidth ×
    // window length) — measured over the non-signal tiles of the 07-30 capture, 0.019 over a 300 ms
    // leader, 0.13 over a 30 ms bit, 0.41 over the 10 ms break.
    //
    // That 20:1 spread across the header's three window lengths is why one absolute constant per element
    // cannot work, and it is exactly how the 2026-07-30 miss happened. The old BitGate = 0.20 sat BELOW
    // the break's own noise floor — so it gated nothing there, while the equally-weighted break term in
    // the alignment search latched the tile's one candidate onto whichever noise spike was largest — and
    // ABOVE the weak 30 ms elements of real headers: that capture carries three Robot 36 headers, all
    // decoding 0x88 with valid parity, whose start and stop read as low as 0.146 and 0.069.
    //
    // So level is no longer decided per element. It is decided once, on the whole header (Score against
    // HeaderGate), where the noise floor is set by the full 910 ms rather than by one 30 ms window; the
    // individual elements are checked for tone DOMINANCE instead, which is window-length invariant.
    //
    // Both constants are sized by SstvVisProbe.VisScoreFloorDump over the 27-capture corpus: the score
    // separates 2.1x, 0.222 (weakest detected header) against 0.104 (loudest headerless tile), and the
    // gate is the geometric mean of the two.
    private const double LeaderGate = 0.04;   // 300 ms @1900 ridge, each half (coherence; floor 0.019)
    private const double HeaderGate = 0.15;   // duration-weighted whole-header Score, in its own [0, 1]

    /// <summary>How far past its nominal onset <see cref="Detect"/> will look for the stop bit, in samples.
    ///
    /// <para>The spec puts the stop bit at t0 + 610 ms + 9·30 ms and nothing else does. Two of the three
    /// transmitter families in the corpus disagree (SstvVisProbe.VisSlotDump, 2026-07-29): SAKHACUBE-CHOLBON
    /// sends the standard 10 elements, but UTMN2 and Monitor-3 send ELEVEN — start, then the full 8-bit VIS
    /// byte 0x88 LSB-first (i.e. including the parity bit the spec carries in the MSB), then an even-parity
    /// bit over those 8 (0), then the stop. Both readings are pinned by two independent checks on a clean
    /// UTMN2 header at 0.49 coherence: the 8 bits equal the known Robot 36 byte exactly, and the extra bit
    /// equals that byte's own parity. The net effect is one extra 30 ms element before the stop, and the
    /// two families' trailing elements also run long (measured 40 and 35 ms against a nominal 30), so the
    /// stop lands 38-47 ms late rather than exactly one slot late.</para>
    ///
    /// <para>So the stop is LOCATED over [nominal, nominal + 2 bits] rather than assumed. That is what
    /// makes the gate work on both families, and it is also what makes <see cref="SstvVisResult.HeaderEndSample"/>
    /// — the first line's sync onset, which seeds the MHT train — correct for the 11-element family, where
    /// the nominal offset was ~45 ms (a third of a Robot 36 line) early. The 60 ms search costs nothing in
    /// false alarms: the stop is no longer gated on level at all, only on tone dominance over its own
    /// window, and it carries 30 of the score's 910 ms.</para></summary>
    private static int StopSlack(int bit) => 2 * bit;

    /// <summary>Full VIS header length in samples (leader + break + leader + 10 bits, plus the
    /// <see cref="StopSlack"/> the stop search may run into), as <see cref="Detect"/> lays it out — the
    /// streaming decoder's tile-lookahead bound (P7.5).</summary>
    public static int HeaderSamples(double fs)
    {
      int S(double ms) => (int)Math.Round(ms / 1000.0 * fs);
      int bit = S(SstvTones.VisBitMs);
      return 2 * S(SstvTones.VisLeaderMs) + S(SstvTones.VisBreakMs) + 10 * bit + StopSlack(bit);
    }

    /// <summary>Scan the <b>whole</b> stream for VIS headers (plan §4.1 follow-up: real bursts start
    /// minutes into a pass, so a leading-window-only search never sees them). Realized as tiled
    /// bounded-length <see cref="Detect"/> windows — allowed §1.13 block processing, one candidate per
    /// ~4 s tile — returning every hit that passes the structural gates. A pass carries several images,
    /// so multiple hits are normal; each seeds its own high-prior train in the MHT.</summary>
    public static List<SstvVisResult> DetectAll(double[] disc, double fs)
    {
      var hits = new List<SstvVisResult>();
      int header = HeaderSamples(fs);
      int step = (int)Math.Round(3.0 * fs);
      for (int start = 0; start < disc.Length - header; start += step)
      {
        var hit = Detect(disc, fs, start, step);
        if (hit.Found) hits.Add(hit);
      }
      return hits;
    }

    /// <summary>Search [<paramref name="searchStart"/>, searchStart+<paramref name="searchLength"/>) for a
    /// VIS header. The tone banks span the search range plus one header length so windows near the end are
    /// covered.</summary>
    public static SstvVisResult Detect(double[] disc, double fs, int searchStart, int searchLength)
    {
      int S(double ms) => (int)Math.Round(ms / 1000.0 * fs);

      // element offsets (samples) from t0
      int leader = S(SstvTones.VisLeaderMs);
      int brk = S(SstvTones.VisBreakMs);
      int bit = S(SstvTones.VisBitMs);
      int oLeader1 = 0;
      int oBreak = leader;
      int oLeader2 = leader + brk;
      int oStart = oLeader2 + leader;         // first bit (start) onset
      int oData0 = oStart + bit;
      int oParity = oData0 + 7 * bit;
      int oStop = oParity + bit;              // EARLIEST stop-bit onset (see StopSlack)
      int headerLen = oStop + StopSlack(bit) + bit;   // through end of the latest stop bit

      int totalW = 2 * leader + brk + 10 * bit;   // scored header length, in samples

      int bankLen = searchLength + headerLen + bit;
      var b1900 = new SstvToneBank(disc, fs, SstvTones.Center, searchStart, bankLen);
      var b1200 = new SstvToneBank(disc, fs, SstvTones.Sync, searchStart, bankLen);
      var b1100 = new SstvToneBank(disc, fs, SstvTones.VisBitOne, searchStart, bankLen);
      var b1300 = new SstvToneBank(disc, fs, SstvTones.VisBitZero, searchStart, bankLen);

      // slots 1..8 carry 1100/1300 in BOTH header families (7 data + the parity-or-8th-data bit), so the
      // larger of those two banks is a data-INdependent match term. It contributes 240 ms of sharp-edged
      // evidence that the old five-term skeleton threw away, which is what lets the alignment peak stay
      // sharp once the leaders are weighted by their length.
      double DataTrack(int t0)
      {
        double sum = 0;
        for (int k = 0; k < 8; k++)
        {
          int a = t0 + oData0 + k * bit;
          sum += Math.Max(b1100.Coherence(a, a + bit), b1300.Coherence(a, a + bit));
        }
        return sum;
      }

      // the share of the header's energy that lies on the expected tone track. Weighting each element by
      // its own length is the whole point: it holds the 10 ms break — pure noise at that window size — to
      // 1% of the vote instead of letting it steer the search, and it gives the one gate that decides on
      // level the full 910 ms of evidence instead of one 30 ms window at a time.
      double Score(int t0, double stopCoh) =>
        (leader * (b1900.Coherence(t0 + oLeader1, t0 + oLeader1 + leader)
                 + b1900.Coherence(t0 + oLeader2, t0 + oLeader2 + leader))
         + brk * b1200.Coherence(t0 + oBreak, t0 + oBreak + brk)
         + bit * (b1200.Coherence(t0 + oStart, t0 + oStart + bit) + DataTrack(t0) + stopCoh)) / totalW;

      // slide t0. The two-point stop proxy keeps the full StopSlack slide out of the per-sample loop; the
      // stop is located properly once, at the winning alignment.
      int last = Math.Min(searchStart + searchLength, disc.Length - headerLen);
      double bestScore = double.NegativeInfinity;
      int bestT0 = -1;
      for (int t0 = searchStart; t0 < last; t0++)
      {
        double s = Score(t0, Math.Max(b1200.Coherence(t0 + oStop, t0 + oStop + bit),
                                      b1200.Coherence(t0 + oStop + bit, t0 + oStop + 2 * bit)));
        if (s > bestScore) { bestScore = s; bestT0 = t0; }
      }

      if (bestT0 < 0)
        return new SstvVisResult(false, -1, null, -1, -1, 0, false);

      // locate the stop bit rather than pinning it to the nominal offset (see StopSlack)
      double stopCoh = 0;
      int stopAt = bestT0 + oStop;
      for (int d = 0; d <= StopSlack(bit); d++)
      {
        double c = b1200.Coherence(bestT0 + oStop + d, bestT0 + oStop + d + bit);
        if (c > stopCoh) { stopCoh = c; stopAt = bestT0 + oStop + d; }
      }
      double score = Score(bestT0, stopCoh) / 0.5;   // normalize to [0, 1]

      // the 1200 element test: over its own window the sync tone must beat all three other tones. Being a
      // comparison of four equal-length windows it is invariant to the window length, so the same test
      // serves the 10 ms break and the 30 ms start/stop — which is what no single absolute constant could do.
      bool Sync1200(int a, int w)
      {
        double c = b1200.Coherence(a, a + w);
        return c > b1900.Coherence(a, a + w) && c > b1100.Coherence(a, a + w) && c > b1300.Coherence(a, a + w);
      }

      // structural gates at the best alignment: the leader ridge, and the break notch — a dip a sustained
      // 1900 carrier physically cannot fake, stated as the two relative facts that actually define it
      // (the break is a 1200 element, and the 1900 track drops there well below the ridge either side).
      double l1 = b1900.Coherence(bestT0 + oLeader1, bestT0 + oLeader1 + leader);
      double l2 = b1900.Coherence(bestT0 + oLeader2, bestT0 + oLeader2 + leader);
      bool ridge = l1 > LeaderGate && l2 > LeaderGate;
      double brk1900 = b1900.Coherence(bestT0 + oBreak, bestT0 + oBreak + brk);
      bool notch = Sync1200(bestT0 + oBreak, brk) && brk1900 < Math.Min(l1, l2);
      bool startStop = Sync1200(bestT0 + oStart, bit) && Sync1200(stopAt, bit);

      // decode the 7 data bits (LSB first) + parity from the 1100/1300 tone dominance
      int code = 0, ones = 0;
      for (int k = 0; k < 7; k++)
      {
        int a = bestT0 + oData0 + k * bit;
        int one = b1100.Coherence(a, a + bit) > b1300.Coherence(a, a + bit) ? 1 : 0;
        code |= one << k;
        ones += one;
      }
      int pa = bestT0 + oParity;
      int parityBit = b1100.Coherence(pa, pa + bit) > b1300.Coherence(pa, pa + bit) ? 1 : 0;
      bool parityOk = ((ones + parityBit) & 1) == 0;

      int visByte = SstvModes.EvenParityByte(code);
      var mode = SstvModes.FromVisByte(visByte)?.Mode;
      int headerEnd = stopAt + bit;

      bool found = score > HeaderGate && ridge && notch && startStop && parityOk;
      return new SstvVisResult(found, found ? visByte : -1, found ? mode : null,
        bestT0, headerEnd, score, parityOk);
    }
  }
}
