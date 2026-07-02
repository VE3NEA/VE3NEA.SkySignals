using System;

namespace VE3NEA.SkySSTV
{
  /// <summary>Outcome of a VIS-header search (plan §4).</summary>
  /// <param name="Found">All structural gates and parity passed at <see cref="T0Sample"/>.</param>
  /// <param name="VisByte">Decoded 8-bit VIS byte (7 data + even-parity MSB), or −1 if not found.</param>
  /// <param name="Mode">Supported mode the byte maps to, or null (byte valid but unsupported / not found).</param>
  /// <param name="T0Sample">Absolute sample index of the VIS leader onset.</param>
  /// <param name="HeaderEndSample">Absolute sample index just after the stop bit = the first line's sync onset.</param>
  /// <param name="Score">Soft VIS-hypothesis likelihood in [0, 1] (the §4 MHT prior, P4). Set even when not found.</param>
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
  /// P4 MHT prior.</para>
  /// </summary>
  internal static class SstvVisDetector
  {
    // coherence is in [0, 0.5] (a real tone splits between ±f); a clean matched tone ≈ 0.5.
    private const double LeaderGate = 0.25;   // 300 ms @1900 ridge
    private const double BitGate = 0.20;      // 10/30 ms @1200 notch / start / stop

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
      int oStop = oParity + bit;
      int headerLen = oStop + bit;            // through end of stop bit

      int bankLen = searchLength + headerLen + bit;
      var b1900 = new SstvToneBank(disc, fs, SstvTones.Center, searchStart, bankLen);
      var b1200 = new SstvToneBank(disc, fs, SstvTones.Sync, searchStart, bankLen);
      var b1100 = new SstvToneBank(disc, fs, SstvTones.VisBitOne, searchStart, bankLen);
      var b1300 = new SstvToneBank(disc, fs, SstvTones.VisBitZero, searchStart, bankLen);

      // slide t0: score the data-independent tone pattern; the narrow 1200 elements sharpen the peak
      int last = Math.Min(searchStart + searchLength, disc.Length - headerLen);
      double bestScore = double.NegativeInfinity;
      int bestT0 = -1;
      for (int t0 = searchStart; t0 < last; t0++)
      {
        double s = b1900.Coherence(t0 + oLeader1, t0 + oLeader1 + leader)
                 + b1900.Coherence(t0 + oLeader2, t0 + oLeader2 + leader)
                 + b1200.Coherence(t0 + oBreak, t0 + oBreak + brk)
                 + b1200.Coherence(t0 + oStart, t0 + oStart + bit)
                 + b1200.Coherence(t0 + oStop, t0 + oStop + bit);
        if (s > bestScore) { bestScore = s; bestT0 = t0; }
      }

      double score = bestT0 < 0 ? 0 : bestScore / (5 * 0.5);   // normalize to [0, 1]
      if (bestT0 < 0)
        return new SstvVisResult(false, -1, null, -1, -1, 0, false);

      // structural gates at the best alignment
      bool ridge = b1900.Coherence(bestT0, bestT0 + leader) > LeaderGate
                && b1900.Coherence(bestT0 + oLeader2, bestT0 + oLeader2 + leader) > LeaderGate;
      bool notch = b1200.Coherence(bestT0 + oBreak, bestT0 + oBreak + brk) > BitGate
                && b1200.Coherence(bestT0 + oBreak, bestT0 + oBreak + brk)
                 > b1900.Coherence(bestT0 + oBreak, bestT0 + oBreak + brk);
      bool startStop = b1200.Coherence(bestT0 + oStart, bestT0 + oStart + bit) > BitGate
                    && b1200.Coherence(bestT0 + oStop, bestT0 + oStop + bit) > BitGate;

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
      int headerEnd = bestT0 + oStop + bit;

      bool found = ridge && notch && startStop && parityOk;
      return new SstvVisResult(found, found ? visByte : -1, found ? mode : null,
        bestT0, headerEnd, score, parityOk);
    }
  }
}
