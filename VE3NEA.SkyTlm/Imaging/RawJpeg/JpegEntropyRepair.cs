using System;
using System.Collections.Generic;

namespace VE3NEA.SkyTlm.Imaging.RawJpeg
{
  /// <summary>
  /// The repaired file: every MCU the walk placed written back where the encoder put it, and a neutral
  /// mid-gray MCU standing in for each one the gaps destroyed. It is a re-encode and not a patch — the
  /// coefficients are decoded, the DC chain is rebuilt continuous over the whole scan, and the result is
  /// written out with the file's <b>own</b> Huffman tables, so the header the reader already has stays
  /// correct and nothing is dequantised, inverse-transformed or otherwise made lossy on the way through.
  /// Structurally this is what <c>jpegtran</c> does.
  /// <para>
  /// That is why it fixes the archived <c>.jpg</c> rather than only the bitmap on screen: what comes back
  /// is a portable baseline JPEG any viewer opens correctly. The received bytes are not lost either — the
  /// sidecar still holds the fragments, so the repair is reproducible and reversible.
  /// </para>
  /// <para>
  /// <b>Two things the walk hands over are used here and derived elsewhere.</b> The MCU counts come from
  /// <see cref="JpegEntropyWalk.MissingMcus"/>, which counts rather than estimates them; the per-segment
  /// <see cref="JpegEntropySegment.DcOffsets"/> come from <see cref="JpegDcSeam"/> and are the one
  /// quantity in the whole repair that is an estimate. This class only adds them.
  /// </para>
  /// <para>
  /// <b>Refusing stays first-class.</b> Anything that cannot be re-encoded exactly — a walk that places
  /// nothing, a DC difference the file's own table has no code for, a segment that stops decoding short
  /// of the MCU count the walk claims — returns null, and the caller emits what it would have emitted
  /// before. A repair that is not exact is not offered.
  /// </para>
  /// </summary>
  internal static class JpegEntropyRepair
  {
    /// <summary>
    /// <paramref name="jpeg"/> re-emitted with its entropy data repaired, or null when there is nothing
    /// to repair or no way to repair it exactly. <paramref name="gaps"/> is in file coordinates, sorted
    /// and disjoint, as <see cref="SparseImageBuffer.Gaps"/> returns it.
    /// <para>
    /// The single-scan test is not so much a limitation as a restatement of what baseline is: every file
    /// in the corpus carries one scan, and a baseline file carrying several would need the walk run over
    /// each of them separately.
    /// </para>
    /// </summary>
    public static byte[]? TryRepair(ReadOnlySpan<byte> jpeg, IReadOnlyList<(int Start, int End)> gaps) =>
      TryRepair(jpeg, gaps, out _);

    /// <summary>
    /// The same repair, also handing back the MCU map it worked from, so a caller can say what it did.
    /// <paramref name="walk"/> is null exactly when the return value is.
    /// </summary>
    public static byte[]? TryRepair(ReadOnlySpan<byte> jpeg, IReadOnlyList<(int Start, int End)> gaps,
      out JpegEntropyWalk? walk)
    {
      walk = null;

      var table = JpegScanTable.Read(jpeg);
      if (table == null || !table.Baseline || table.Scans.Count != 1) return null;

      var scan = table.Scans[0];
      walk = JpegEntropyWalker.TryWalk(jpeg, table, scan, gaps);
      if (walk == null) return null;

      var decoder = JpegBaselineDecoder.TryCreate(table, scan);
      if (decoder == null) { walk = null; return null; }

      var encoders = TryEncoders(table, scan);
      if (encoders == null) { walk = null; return null; }

      // everything up to the scan's entropy data is the header and the SOS, and both stay exactly as they
      // are: the re-encode uses the tables they declare, so they still describe the file afterwards
      var output = new List<byte>(jpeg.Length);
      foreach (byte b in jpeg[..scan.Start]) output.Add(b);

      var bits = new JpegBitWriter(output);
      if (!TryWriteScan(bits, decoder, encoders, walk, jpeg[scan.Start..scan.End])) { walk = null; return null; }
      bits.Flush();

      output.Add(0xFF);
      output.Add(0xD9);
      return output.ToArray();
    }

    /// <summary>
    /// The scan, MCU 0 to the last one the frame header declares, as one continuous entropy stream. The
    /// walk partitions that range exactly, so writing it is alternating between the neutral MCUs of a
    /// missing range and the decoded MCUs of a segment until the count runs out.
    /// <para>
    /// The decoder's predictors restart at zero for every segment, because that is what the segment's
    /// bits were decoded against and what its <see cref="JpegEntropySegment.DcOffsets"/> correct for. The
    /// encoder's predictors do not restart at all: the output is one stream the encoder never broke, so
    /// its DC chain runs unbroken from the first block to the last.
    /// </para>
    /// </summary>
    private static bool TryWriteScan(JpegBitWriter bits, JpegBaselineDecoder decoder,
      (JpegHuffmanEncoder Dc, JpegHuffmanEncoder Ac)[] encoders, JpegEntropyWalk walk, ReadOnlySpan<byte> data)
    {
      var coefficients = new short[64 * decoder.BlocksPerMcu];
      var predictors = new int[decoder.ComponentCount];
      var written = new int[decoder.ComponentCount];
      int at = 0;

      foreach (var segment in walk.Segments)
      {
        if (!TryWriteNeutral(bits, decoder, encoders, written, segment.FirstMcu - at)) return false;

        var reader = new JpegBitReader(data[(segment.Start + segment.SkipBytes)..segment.End], segment.SkipBits);
        Array.Clear(predictors);

        for (int i = 0; i < segment.McuCount; i++)
        {
          if (!decoder.TryDecodeMcu(ref reader, coefficients, predictors)) return false;
          if (!TryWriteMcu(bits, decoder, encoders, written, coefficients, segment.DcOffsets)) return false;
        }

        at = segment.FirstMcu + segment.McuCount;
      }

      return TryWriteNeutral(bits, decoder, encoders, written, walk.McuCount - at);
    }

    /// <summary>
    /// <paramref name="mcus"/> MCUs of mid-gray, which is what a destroyed range is honestly worth. Every
    /// coefficient is zero, and a zero DC is the block average the level shift puts at 128 — mid-gray in
    /// luma and neutral in chroma — so a missing range reads as flat gray rather than as the noise the
    /// undecodable bytes would have produced.
    /// </summary>
    private static bool TryWriteNeutral(JpegBitWriter bits, JpegBaselineDecoder decoder,
      (JpegHuffmanEncoder Dc, JpegHuffmanEncoder Ac)[] encoders, int[] written, int mcus)
    {
      if (mcus < 0) return false;                         // the walk's ranges overlap: not repairable

      var block = new short[64];

      for (int i = 0; i < mcus; i++)
        for (int c = 0; c < decoder.ComponentCount; c++)
        {
          var (_, h, v) = decoder.Layout[c];
          for (int b = 0; b < h * v; b++)
            if (!TryWriteBlock(bits, encoders[c], block, 0, ref written[c])) return false;
        }

      return true;
    }

    /// <summary>
    /// One MCU, every block of every component in the interleaved order the decoder read them in. The
    /// component's <see cref="JpegEntropySegment.DcOffsets"/> entry is added to the DC as it goes past,
    /// which is the whole of what the seam estimate does to the picture.
    /// </summary>
    private static bool TryWriteMcu(JpegBitWriter bits, JpegBaselineDecoder decoder,
      (JpegHuffmanEncoder Dc, JpegHuffmanEncoder Ac)[] encoders, int[] written,
      ReadOnlySpan<short> coefficients, int[] dcOffsets)
    {
      for (int c = 0; c < decoder.ComponentCount; c++)
      {
        var (firstBlock, h, v) = decoder.Layout[c];

        for (int b = 0; b < h * v; b++)
        {
          var block = coefficients.Slice(64 * (firstBlock + b), 64);
          if (!TryWriteBlock(bits, encoders[c], block, block[0] + dcOffsets[c], ref written[c])) return false;
        }
      }

      return true;
    }


  // ----------------------------------------------------------------------------------------------------
  //                                     one 8x8 block, written back
  // ----------------------------------------------------------------------------------------------------
    /// <summary>
    /// One block: the DC as a difference from the block before it, then the AC coefficients as
    /// run-length pairs, in the zig-zag order they were decoded in. <paramref name="dc"/> is the absolute
    /// coefficient wanted; <paramref name="predictor"/> carries the chain and is updated to it.
    /// <para>
    /// Every refusal here is the same refusal: the file's own Huffman table has no code for a symbol this
    /// block needs. It cannot happen for a symbol that was decoded from that table, but it can for the
    /// ones this class invents — a DC difference across a splice, or the size-zero difference every
    /// neutral block writes. The magnitude bounds are the decoder's, kept here so that what is written is
    /// something the decoder would read back.
    /// </para>
    /// </summary>
    private static bool TryWriteBlock(JpegBitWriter bits, (JpegHuffmanEncoder Dc, JpegHuffmanEncoder Ac) encoder,
      ReadOnlySpan<short> coefficients, int dc, ref int predictor)
    {
      int difference = dc - predictor;
      predictor = dc;

      int size = Magnitude(difference);
      if (size > 11 || !encoder.Dc.TryWrite(bits, (byte)size)) return false;
      if (size > 0) bits.Write(difference < 0 ? difference - 1 : difference, size);

      int run = 0;

      for (int k = 1; k <= 63; k++)
      {
        int value = coefficients[k];
        if (value == 0) { run++; continue; }

        for (; run > 15; run -= 16)
          if (!encoder.Ac.TryWrite(bits, 0xF0)) return false;   // ZRL, a run of sixteen zeros

        size = Magnitude(value);
        if (size > 10 || !encoder.Ac.TryWrite(bits, (byte)(run << 4 | size))) return false;
        bits.Write(value < 0 ? value - 1 : value, size);
        run = 0;
      }

      return run == 0 || encoder.Ac.TryWrite(bits, 0x00);       // end of block
    }

    /// <summary>
    /// How many bits a JPEG magnitude needs, which is the size half of every symbol written above and the
    /// inverse of <c>Extend</c>: zero for zero, and otherwise one more than the position of the top set
    /// bit of the absolute value.
    /// </summary>
    private static int Magnitude(int value)
    {
      int size = 0;
      for (int a = Math.Abs(value); a > 0; a >>= 1) size++;
      return size;
    }

    /// <summary>
    /// The DC and AC encoder each component of the scan writes with, in the order an MCU visits them, or
    /// null when the scan names a table the file does not usably declare. Deliberately the same
    /// resolution <see cref="JpegBaselineDecoder.TryCreate"/> does, including the last-declaration-wins
    /// rule, so the encode and the decode cannot disagree about which table a component uses.
    /// </summary>
    private static (JpegHuffmanEncoder Dc, JpegHuffmanEncoder Ac)[]? TryEncoders(JpegScanTable table, JpegScan scan)
    {
      var dcTables = new JpegHuffmanEncoder?[4];
      var acTables = new JpegHuffmanEncoder?[4];

      foreach (var t in table.HuffmanTables)
      {
        if (t.Id is < 0 or > 3) continue;
        if (t.Class == 0) dcTables[t.Id] = JpegHuffmanEncoder.TryCreate(t);
        else if (t.Class == 1) acTables[t.Id] = JpegHuffmanEncoder.TryCreate(t);
      }

      var encoders = new (JpegHuffmanEncoder Dc, JpegHuffmanEncoder Ac)[scan.Components.Count];

      for (int c = 0; c < scan.Components.Count; c++)
      {
        var sc = scan.Components[c];
        if (sc.DcTable is < 0 or > 3 || sc.AcTable is < 0 or > 3) return null;

        var dc = dcTables[sc.DcTable];
        var ac = acTables[sc.AcTable];
        if (dc == null || ac == null) return null;

        encoders[c] = (dc, ac);
      }

      return encoders;
    }
  }




  // ----------------------------------------------------------------------------------------------------
  //                                          the entropy walker
  // ----------------------------------------------------------------------------------------------------
  /// <summary>
  /// Where the MCUs of a holey baseline scan actually are, worked out by counting rather than by
  /// guessing. Two facts make that possible and neither is available in the pixel domain:
  /// <see cref="SparseImageBuffer"/> records exactly which bytes are missing, and the encoder wrote
  /// <b>every</b> MCU of the frame — so a run of entropy data that reaches the end of the scan
  /// necessarily ends on the last MCU, and a run that starts at the beginning of it necessarily starts on
  /// MCU 0.
  /// <para>
  /// Those are the two anchors, and they are the only two. The run that starts the scan begins on a byte
  /// and bit boundary the encoder chose, so it is decoded outright and its MCUs are 0 upwards. The run
  /// that ends the scan is counted backwards from the total: its MCU count subtracted from
  /// <see cref="JpegScanTable.McuCount"/> is where it begins. With a single gap that pins both runs and
  /// the gap's cost exactly — the cost is the total less the two counts, derived rather than estimated.
  /// </para>
  /// <para>
  /// <b>A run between two gaps cannot be placed, and that is arithmetic rather than a shortcoming of the
  /// decoder.</b> Its MCU count is known exactly, but its position is not: each gap destroyed some unknown
  /// number of MCUs, and <i>k</i> gaps give <i>k</i> unknowns against the one equation saying the counts
  /// and the losses sum to the frame. Only <i>k</i> = 1 is determined. Chaining the count leftwards from
  /// run to run — which is what the plan this implements proposed — silently sets every gap's cost to
  /// zero and slides everything left of a gap out of place by however many MCUs that gap really cost.
  /// Middle runs are therefore declined and their MCUs reported missing.
  /// </para>
  /// <para>
  /// <b>Refusing is a first-class outcome.</b> A run whose resync search cannot place it is left
  /// unplaced, as is a walk whose two anchors overlap. The failure this guards against is a confident
  /// misplacement, which is worse than the visibly broken image the code produces without any of this.
  /// </para>
  /// </summary>
  internal sealed class JpegEntropyWalker
  {
    /// <summary>
    /// How many bytes at the head of a run the resync search tries first, at all eight bit phases of each.
    /// The gap before the run ends part-way through an MCU, so the run's first whole MCU begins within one
    /// MCU's worth of bytes of its start — ten to thirteen on average across the corpus.
    /// <para>
    /// This has to be wide enough to contain that first whole MCU, because that offset being among the
    /// candidates is what makes <see cref="TryPlace"/>'s answer sound rather than merely plausible. It
    /// should also not be wider than it needs to be: a candidate starting further into the run rejoins
    /// the reference further in, and the run is placed at the latest of those, so every extra byte here
    /// costs MCUs at the head of the run. Sixty-four covers several MCUs of every photograph in the
    /// corpus and costs a handful.
    /// </para>
    /// <para>
    /// It is a <b>starting</b> width and not the width, because no constant can be the width. An MCU is as
    /// long as its part of the picture is busy, and a synthetic test card runs to nearly two hundred bytes
    /// where a photograph runs to thirty — measured, and it is the case where a fixed sixty-four placed a
    /// run off the encoder's grid and three MCUs of the wrong picture with it. So <see cref="TryPlace"/>
    /// grows this to what the placement it produced says the file's MCUs actually measure, and refuses if
    /// that never settles.
    /// </para>
    /// </summary>
    private const int ResyncBytes = 64;

    /// <summary>
    /// How many times the resync window may grow before the search gives up. The window starts at
    /// <see cref="ResyncBytes"/> and widens to whatever the placement it produced says the file's MCUs
    /// actually measure, which is a fixed point rather than a sequence: on every file in the corpus it
    /// either settles on the first pass or on the second. A window that is still growing after this many
    /// passes is a file whose MCU sizes the search cannot pin down, and refusing is the answer.
    /// </summary>
    private const int ResyncGrowthLimit = 4;

    /// <summary>
    /// How many MCUs after the placement are measured to decide whether the window reached far enough.
    /// The question is the size of the MCU the gap cut in half, and its nearest observable neighbours are
    /// the ones the run resumes with — MCU sizes track the local busyness of the picture, so a handful of
    /// neighbours says far more than the run's average and cannot be dragged by a busy patch elsewhere.
    /// </summary>
    private const int ResyncSampleMcus = 8;

    /// <summary>
    /// The MCU map of <paramref name="scan"/>, or null when there is nothing here to walk: the file is
    /// not baseline, it carries restart markers and so resynchronises by itself, no gap falls inside the
    /// scan, or neither end of the scan survived to anchor from. <paramref name="gaps"/> is in file
    /// coordinates, sorted and disjoint, as <see cref="SparseImageBuffer.Gaps"/> returns it.
    /// </summary>
    public static JpegEntropyWalk? TryWalk(ReadOnlySpan<byte> jpeg, JpegScanTable table, JpegScan scan,
      IReadOnlyList<(int Start, int End)> gaps)
    {
      if (table.RestartInterval > 0) return null;         // RSTn resynchronises the stream on its own

      var decoder = JpegBaselineDecoder.TryCreate(table, scan);
      if (decoder == null) return null;

      var runs = ReceivedRuns(scan, gaps);
      if (runs.Count < 2) return null;                    // no hole inside the scan: nothing to repair

      var data = jpeg.Slice(scan.Start, scan.End - scan.Start);
      var segments = new List<JpegEntropySegment>();

      // the left anchor: the run that starts the scan starts on MCU 0, at a bit offset the encoder chose,
      // so it is decoded rather than searched for
      var leftBoundaries = new List<int>();
      if (runs[0].Start == 0)
      {
        var bits = new JpegBitReader(data[runs[0].Start..runs[0].End]);
        if (decoder.TryDecodeRun(ref bits, decoder.McuCount, out int mcus, leftBoundaries) && mcus > 0)
          segments.Add(new JpegEntropySegment(runs[0].Start, runs[0].End, 0, 0, 0, mcus,
            new int[decoder.ComponentCount]));
        else leftBoundaries.Clear();
      }

      // the right anchor: the run that ends the scan ends on the last MCU the encoder wrote, so counting
      // its MCUs is the same thing as finding where it begins
      if (runs[^1].End == data.Length)
      {
        var run = data[runs[^1].Start..runs[^1].End];
        if (TryPlace(decoder, run, decoder.McuCount, ResyncFloor(leftBoundaries, runs[^1].Start),
          out int skipBytes, out int skipBits, out int mcus))
          segments.Add(new JpegEntropySegment(runs[^1].Start, runs[^1].End, skipBytes, skipBits,
            decoder.McuCount - mcus, mcus, new int[decoder.ComponentCount]));
      }

      // the two anchors cannot overlap: between them lies at least one gap, and a gap costs MCUs rather
      // than returning them. A walk that fails this is wrong somewhere it cannot point at, so all of it
      // is refused.
      if (segments.Count == 2 && segments[1].FirstMcu < segments[0].McuCount) return null;

      // the right anchor was decoded against a predictor chain restarting from zero, so every DC it holds
      // is one constant per component away from the one the encoder wrote. That constant is the only part
      // of the repair that is estimated rather than counted, and the seam is where to estimate it.
      if (segments.Count == 2)
        segments[1] = segments[1] with
        {
          DcOffsets = JpegDcSeam.Estimate(decoder, table.McusPerRow, data, segments[0], segments[1])
        };

      return segments.Count > 0 ? new JpegEntropyWalk(decoder.McuCount, segments, runs.Count - segments.Count) : null;
    }

    /// <summary>
    /// The scan's entropy data less the gaps: the runs of bytes that did arrive, in scan-relative
    /// coordinates.
    /// </summary>
    private static List<(int Start, int End)> ReceivedRuns(JpegScan scan, IReadOnlyList<(int Start, int End)> gaps)
    {
      var runs = new List<(int Start, int End)>();
      int at = scan.Start;

      foreach (var (start, end) in gaps)
      {
        int from = Math.Max(start, scan.Start), to = Math.Min(end, scan.End);
        if (to <= from) continue;
        if (from > at) runs.Add((at - scan.Start, from - scan.Start));
        at = Math.Max(at, to);
      }
      if (scan.End > at) runs.Add((at - scan.Start, scan.End - scan.Start));

      return runs;
    }

    /// <summary>
    /// The bit offset a run resumes on and how many MCUs it then holds, or false when the search cannot
    /// say.
    /// <para>
    /// The obvious test — decode from a bit offset and accept it if the decode stays valid and consumes
    /// the run — does not work, and it is worth saying why, because it is what the plan for this called
    /// for. Measured on the corpus, 3,193 of 3,200 bit offsets at the head of a run decode to the end of
    /// it without ever meeting a bit pattern that is no Huffman code, and every single one of them ends
    /// its last whole MCU within seven bits of the run's last byte. Both halves of that test accept
    /// essentially everything, and the MCU counts they hand back spread over a range of eighteen either
    /// side of the truth. A baseline stream started at the wrong bit resynchronises itself within a few
    /// codes, which is exactly what makes the wrong answers look so much like the right one.
    /// </para>
    /// <para>
    /// That same self-resynchronisation is what the search uses instead. Every candidate rejoins the
    /// encoder's real MCU grid within a handful of MCUs — measured, all 3,193 of them — and once two
    /// decodes share an MCU boundary they are the same decode from there on, because an MCU boundary
    /// fixes the bit position and the block position together. So one candidate is decoded in full as a
    /// <b>reference</b>, every other candidate is decoded only until it lands on a boundary the reference
    /// passed through, and the run is placed at the <b>latest</b> boundary any of them needed to get
    /// there. Past that point the reference is provably on the encoder's grid: the offset that is the
    /// run's true first MCU is itself one of the candidates, and it lands on the reference's grid at the
    /// first boundary the two share.
    /// </para>
    /// <para>
    /// The price is the MCUs before that point, a handful at the head of each run, which are counted as
    /// destroyed rather than guessed at. It is not recoverable: whether the reference was already on the
    /// encoder's grid one MCU earlier is a question no forward decode can answer.
    /// </para>
    /// </summary>
    private static bool TryPlace(JpegBaselineDecoder decoder, ReadOnlySpan<byte> run, int mcuBudget,
      int minWindow, out int skipBytes, out int skipBits, out int mcus)
    {
      skipBytes = skipBits = mcus = 0;

      int window = Math.Min(Math.Max(ResyncBytes, minWindow), run.Length);

      for (int attempt = 0; attempt < ResyncGrowthLimit; attempt++)
      {
        if (!TryPlaceInWindow(decoder, run, mcuBudget, 8 * window,
          out skipBytes, out skipBits, out mcus, out int longest)) return false;

        // The window was wide enough exactly when it was at least as wide as the MCUs around the run's
        // head, because the MCU the gap cut in half is one of those and the run's first whole MCU begins
        // where it ends. `longest` is what the placement itself just measured, so this is the decode's own
        // answer to a question the search had to assume: if the window did not reach that far, the offset
        // the whole argument rests on was never among the candidates and the answer is unsupported.
        if (longest <= window) return true;
        if (window >= run.Length) return false;

        window = Math.Min(run.Length, longest);
      }

      return false;                                      // the window never settled: refuse rather than guess
    }

    /// <summary>
    /// How wide the resync window has to be before it is worth opening, in bytes, read off the <b>other</b>
    /// side of the gap.
    /// <para>
    /// The window has to reach the run's first whole MCU, and that MCU begins where the one the gap cut in
    /// half ends. The cut MCU's own length is the thing nobody can measure — but the left anchor decoded
    /// its immediate predecessors exactly, with no resync anywhere, and MCU lengths track how busy that
    /// part of the picture is. So the longest of those is the estimate, less the part of the cut MCU that
    /// arrived before the gap and the gap itself, both of which are known to the byte.
    /// </para>
    /// <para>
    /// It has to be read from the left, and this is the whole reason the reading is not taken from the
    /// placed run instead: the MCUs after the gap are neighbours of the cut MCU only in the file, not in
    /// the picture, and on a test card whose busy region ends at the gap they measure a third of what the
    /// cut MCU did. Zero when there is no left anchor to read, which leaves <see cref="ResyncBytes"/>.
    /// </para>
    /// </summary>
    private static int ResyncFloor(List<int> leftBoundaries, int runStart)
    {
      if (leftBoundaries.Count < 2) return 0;

      int longest = 0;
      for (int i = Math.Max(1, leftBoundaries.Count - ResyncSampleMcus); i < leftBoundaries.Count; i++)
        longest = Math.Max(longest, leftBoundaries[i] - leftBoundaries[i - 1]);

      int need = longest - (8 * runStart - leftBoundaries[^1]);
      return need <= 0 ? 0 : (need + 7) / 8;
    }

    /// <summary>
    /// One pass of the resync search at a given window width, and the longest MCU in bytes it saw at the
    /// head of what it placed — which is what <see cref="TryPlace"/> checks the width against.
    /// </summary>
    private static bool TryPlaceInWindow(JpegBaselineDecoder decoder, ReadOnlySpan<byte> run, int mcuBudget,
      int window, out int skipBytes, out int skipBits, out int mcus, out int longest)
    {
      skipBytes = skipBits = mcus = longest = 0;

      var coefficients = new short[64 * decoder.BlocksPerMcu];
      var predictors = new int[decoder.ComponentCount];
      var boundaries = new List<int>();

      window = Math.Min(window, run.Length * 8);
      int reference = 0;
      while (reference < window &&
        !TryDecodeReference(decoder, run, reference, mcuBudget, coefficients, predictors, boundaries))
        reference++;
      if (reference == window) return false;

      var grid = new Dictionary<int, int>(boundaries.Count);
      for (int i = 0; i < boundaries.Count; i++) grid[boundaries[i]] = i;

      int agreed = 0;
      for (int offset = reference + 1; offset < window; offset++)
        if (TryMeet(decoder, run, offset, mcuBudget, coefficients, predictors, grid, out int meet))
          agreed = Math.Max(agreed, meet);

      // the MCUs immediately after the placement, which are the ones the cut MCU is a neighbour of. Past
      // the agreed boundary the reference is on the encoder's grid, so these lengths are the file's own.
      int head = Math.Min(boundaries.Count - 1, agreed + ResyncSampleMcus);
      for (int i = agreed; i < head; i++)
        longest = Math.Max(longest, (boundaries[i + 1] - boundaries[i] + 7) / 8);

      mcus = boundaries.Count - 1 - agreed;
      if (mcus <= 0) return false;

      skipBytes = boundaries[agreed] / 8;
      skipBits = boundaries[agreed] % 8;
      return true;
    }

    /// <summary>
    /// The reference decode: from <paramref name="offset"/> to wherever the run's data runs out, with the
    /// position of every MCU boundary it passed through collected into <paramref name="boundaries"/> —
    /// one per MCU, plus the position it ended on, so the list is one longer than the MCU count.
    /// <para>
    /// False when the offset is not usable as a reference at all: a bit pattern that is no Huffman code,
    /// no whole MCU in the run, or — the one chain check in here — more MCUs than the budget the walk
    /// has left to give, which is a contradiction rather than a near miss.
    /// </para>
    /// </summary>
    private static bool TryDecodeReference(JpegBaselineDecoder decoder, ReadOnlySpan<byte> run, int offset,
      int mcuBudget, short[] coefficients, int[] predictors, List<int> boundaries)
    {
      boundaries.Clear();
      int skip = offset / 8;
      var bits = new JpegBitReader(run[skip..], offset % 8);
      Array.Clear(predictors);

      int mcus = 0;
      while (true)
      {
        var (b, p) = bits.Position;
        boundaries.Add((skip + b) * 8 + p);
        if (mcus == mcuBudget) return false;

        var mark = bits;
        if (!decoder.TryDecodeMcu(ref bits, coefficients, predictors))
        {
          if (!bits.Exhausted) return false;
          bits = mark;
          break;
        }
        mcus++;
      }

      return mcus > 0;
    }

    /// <summary>
    /// Decode from <paramref name="offset"/> only as far as the first MCU boundary the reference decode
    /// also passed through, and report which of the reference's boundaries that was. False when the
    /// offset meets a bit pattern that is no Huffman code first, or runs out of data without ever
    /// rejoining — neither of which says anything about the reference, so neither constrains the answer.
    /// </summary>
    private static bool TryMeet(JpegBaselineDecoder decoder, ReadOnlySpan<byte> run, int offset,
      int mcuBudget, short[] coefficients, int[] predictors, Dictionary<int, int> grid, out int meet)
    {
      meet = 0;
      int skip = offset / 8;
      var bits = new JpegBitReader(run[skip..], offset % 8);
      Array.Clear(predictors);

      for (int mcus = 0; mcus <= mcuBudget; mcus++)
      {
        var (b, p) = bits.Position;
        if (grid.TryGetValue((skip + b) * 8 + p, out meet)) return true;
        if (!decoder.TryDecodeMcu(ref bits, coefficients, predictors)) return false;
      }

      return false;
    }
  }




  // ----------------------------------------------------------------------------------------------------
  //                                    the DC level across the seam
  // ----------------------------------------------------------------------------------------------------
  /// <summary>
  /// The one quantity in the repair that is estimated rather than counted. A baseline DC coefficient is
  /// written as a difference from the block before it, so a segment the walker placed exactly still
  /// decodes its DCs against a predictor chain that restarted from zero instead of from the value the
  /// missing bytes carried. Every block of that segment is therefore one constant per component away from
  /// the truth, and no amount of counting recovers it: the constant is precisely what was destroyed.
  /// <para>
  /// What makes it estimable is that the placement is already exact. The MCUs the segment starts on sit
  /// directly below MCUs the other anchor decoded correctly, one MCU row up — real spatial neighbours,
  /// not a correlation over the whole picture — and a DC coefficient is the average of its block, which
  /// across a block boundary is very nearly continuous. So the offset is read off the seam: the bottom
  /// row of blocks above it against the top row of blocks below it, differenced block for block.
  /// </para>
  /// <para>
  /// The <b>median</b> of those differences rather than the mean, because the two rows are neighbours
  /// and not copies: an edge crossing the seam makes a handful of columns disagree violently, which
  /// drags a mean and does not move a median. And when the seam has no neighbouring rows at all — a gap
  /// wider than a whole MCU row, so nothing in the segment sits under anything placed — the estimate is
  /// declined and the offset left at zero. Its failure mode is the mildest one in the repair: a flat
  /// colour cast over a correctly placed image.
  /// </para>
  /// </summary>
  internal static class JpegDcSeam
  {
    /// <summary>
    /// The per-component DC offset to add to every block of <paramref name="below"/>, in the order an MCU
    /// visits the scan's components. Zeros when the seam cannot be read, which is an answer and not a
    /// failure. <paramref name="above"/> is a segment whose DCs are already true — the run that starts
    /// the scan — and <paramref name="data"/> is the scan's entropy data, which is what both segments'
    /// offsets are relative to.
    /// </summary>
    public static int[] Estimate(JpegBaselineDecoder decoder, int mcusPerRow, ReadOnlySpan<byte> data,
      JpegEntropySegment above, JpegEntropySegment below)
    {
      var offsets = new int[decoder.ComponentCount];
      if (mcusPerRow <= 0) return offsets;

      // the seam: the MCUs of `below` that sit one row under an MCU of `above`. There are at most a row of
      // them, and none at all once the gap between the two is wider than a row.
      int first = Math.Max(below.FirstMcu, above.FirstMcu + mcusPerRow);
      int last = Math.Min(below.FirstMcu + below.McuCount, above.FirstMcu + above.McuCount + mcusPerRow);
      int count = last - first;
      if (count <= 0) return offsets;

      var top = DecodeDc(decoder, data, above, first - mcusPerRow, count);
      var bottom = DecodeDc(decoder, data, below, first, count);
      if (top == null || bottom == null) return offsets;

      var differences = new List<int>(count * decoder.BlocksPerMcu);

      for (int c = 0; c < decoder.ComponentCount; c++)
      {
        var (firstBlock, h, v) = decoder.Layout[c];
        differences.Clear();

        // the component's bottom row of blocks in the MCU above against its top row in the MCU below: the
        // pairs that actually touch
        for (int i = 0; i < count; i++)
          for (int x = 0; x < h; x++)
            differences.Add(
              top[i * decoder.BlocksPerMcu + firstBlock + (v - 1) * h + x] -
              bottom[i * decoder.BlocksPerMcu + firstBlock + x]);

        differences.Sort();
        offsets[c] = differences[differences.Count / 2];
      }

      return offsets;
    }

    /// <summary>
    /// The DC coefficient of every block of <paramref name="count"/> MCUs of <paramref name="segment"/>,
    /// starting at MCU <paramref name="firstMcu"/> of the frame, or null when the segment does not decode
    /// that far. Only the DCs are kept: the seam is a question about block averages, and holding the
    /// whole coefficient array of a row of MCUs to answer it would be wasteful.
    /// </summary>
    private static int[]? DecodeDc(JpegBaselineDecoder decoder, ReadOnlySpan<byte> data,
      JpegEntropySegment segment, int firstMcu, int count)
    {
      var coefficients = new short[64 * decoder.BlocksPerMcu];
      var predictors = new int[decoder.ComponentCount];
      var bits = new JpegBitReader(data[(segment.Start + segment.SkipBytes)..segment.End], segment.SkipBits);
      var dc = new int[count * decoder.BlocksPerMcu];

      int skip = firstMcu - segment.FirstMcu;
      if (skip < 0 || skip + count > segment.McuCount) return null;

      for (int i = 0; i < skip + count; i++)
      {
        if (!decoder.TryDecodeMcu(ref bits, coefficients, predictors)) return null;
        if (i < skip) continue;
        for (int b = 0; b < decoder.BlocksPerMcu; b++)
          dc[(i - skip) * decoder.BlocksPerMcu + b] = coefficients[64 * b];
      }

      return dc;
    }
  }




  // ----------------------------------------------------------------------------------------------------
  //                                         what the walk found
  // ----------------------------------------------------------------------------------------------------
  /// <summary>
  /// One run of entropy data that survived, placed in the MCU sequence.
  /// <see cref="Start"/> and <see cref="End"/> are scan-relative byte offsets of the run itself;
  /// <see cref="SkipBytes"/> and <see cref="SkipBits"/> are where inside it the first whole MCU begins,
  /// which for the run that starts the scan is byte zero bit zero and for every other run is what the
  /// resync search found.
  /// <see cref="DcOffsets"/> is what to add to every DC coefficient the run decodes to, one per component
  /// in the order an MCU visits them: zero for the run that starts the scan, whose predictor chain starts
  /// where the encoder's did, and <see cref="JpegDcSeam"/>'s estimate for one that resumes after a gap.
  /// </summary>
  internal readonly record struct JpegEntropySegment(int Start, int End, int SkipBytes, int SkipBits,
    int FirstMcu, int McuCount, int[] DcOffsets);


  /// <summary>
  /// The MCU map of one holey scan: where each surviving run of entropy data sits in the MCU sequence,
  /// and which MCUs no data survives for. The two partition [0, <see cref="McuCount"/>) exactly, which is
  /// the point of the exercise — the re-emission has to know both what to copy and how many neutral MCUs
  /// to splice in between the copies.
  /// </summary>
  internal sealed class JpegEntropyWalk
  {
    public JpegEntropyWalk(int mcuCount, IReadOnlyList<JpegEntropySegment> segments, int declinedRuns)
    {
      McuCount = mcuCount;
      Segments = segments;
      DeclinedRuns = declinedRuns;

      var missing = new List<(int FirstMcu, int McuCount)>();
      int at = 0;
      foreach (var s in segments)
      {
        if (s.FirstMcu > at) missing.Add((at, s.FirstMcu - at));
        at = s.FirstMcu + s.McuCount;
        RecoveredMcus += s.McuCount;
      }
      if (mcuCount > at) missing.Add((at, mcuCount - at));

      MissingMcus = missing;
    }

    /// <summary>How many MCUs the encoder wrote, which is what the two lists below add up to.</summary>
    public int McuCount { get; }

    /// <summary>The runs that were placed, in MCU order.</summary>
    public IReadOnlyList<JpegEntropySegment> Segments { get; }

    /// <summary>The MCU ranges no surviving data covers: what a gap actually cost, counted.</summary>
    public IReadOnlyList<(int FirstMcu, int McuCount)> MissingMcus { get; }

    /// <summary>MCUs the walk can place exactly.</summary>
    public int RecoveredMcus { get; }

    /// <summary>Runs the resync search refused to place, whose MCUs are counted as missing.</summary>
    public int DeclinedRuns { get; }
  }




  // ----------------------------------------------------------------------------------------------------
  //                                        the baseline decoder
  // ----------------------------------------------------------------------------------------------------
  /// <summary>
  /// Reads a baseline scan's entropy-coded data back into coefficients, which is the first half of
  /// repairing a JPEG that arrived with holes in it. Coefficients only: no dequantisation and no IDCT,
  /// because the repair happens in the coefficient domain and re-encodes with the file's own tables, so
  /// nothing here may be lossy.
  /// <para>
  /// The decode is deliberately <b>fragile</b>. Every way a baseline stream can be malformed — a bit
  /// pattern matching no Huffman code, a DC magnitude a baseline encoder cannot have written, an AC run
  /// walking past coefficient 63 — returns false rather than guessing. That is not defensiveness: the
  /// resync search tries every bit phase of every candidate start byte and relies on a wrong phase
  /// failing quickly and definitely. A decoder that recovered from bad input would make the search
  /// accept garbage.
  /// </para>
  /// <para>
  /// Running out of data is the one thing that is <b>not</b> a failure, because that is exactly how a
  /// segment cut short by a hole ends. Telling a truncated segment from a wrong bit phase is not this
  /// decoder's job — it reports the MCU count and where it stopped, and the resync search is what
  /// demands that the segment be consumed exactly.
  /// </para>
  /// <para>
  /// Baseline SOF0 interleaved scans only. Progressive is a different and much larger problem and keeps
  /// the scan-coverage cut in <see cref="RawJpegEmitter"/>; a file with a DRI resynchronises by itself
  /// and needs none of this.
  /// </para>
  /// </summary>
  internal sealed class JpegBaselineDecoder
  {
    private readonly JpegHuffmanDecoder?[] dcTables = new JpegHuffmanDecoder?[4];
    private readonly JpegHuffmanDecoder?[] acTables = new JpegHuffmanDecoder?[4];
    private readonly List<(int Component, int Blocks, JpegHuffmanDecoder Dc, JpegHuffmanDecoder Ac)> plan = [];
    private readonly List<(int FirstBlock, int H, int V)> layout = [];

    private JpegBaselineDecoder(int mcuCount)
    {
      McuCount = mcuCount;
    }

    /// <summary>How many MCUs the encoder wrote, from the frame header. The decode never runs past it.</summary>
    public int McuCount { get; }

    /// <summary>Blocks in one MCU, summed over the scan's components: 6 for the 4:2:0 the whole corpus uses.</summary>
    public int BlocksPerMcu { get; private set; }

    /// <summary>Components in the scan, in the order an MCU visits them.</summary>
    public int ComponentCount => plan.Count;

    /// <summary>
    /// For each component, in that same order: where its blocks begin inside an MCU, and how they are
    /// arranged there — <c>H</c> across by <c>V</c> down, written row by row. Block <c>y * H + x</c> of a
    /// component is the one at column <c>x</c> and row <c>y</c> of its part of the MCU, which is what
    /// anything reasoning about neighbouring blocks rather than about the stream needs to know.
    /// </summary>
    public IReadOnlyList<(int FirstBlock, int H, int V)> Layout => layout;

    /// <summary>
    /// A decoder for one scan of one file, or null when the file is not something this can decode: not
    /// baseline, no frame header, no MCUs, or a scan naming a component or a table the file never
    /// declared. Refusing here is what keeps every caller from having to check any of it.
    /// </summary>
    public static JpegBaselineDecoder? TryCreate(JpegScanTable table, JpegScan scan)
    {
      if (!table.Baseline || table.McuCount <= 0 || scan.Components.Count == 0) return null;

      var decoder = new JpegBaselineDecoder(table.McuCount);

      // a table may be declared more than once, in which case the last declaration is the one in force
      foreach (var t in table.HuffmanTables)
      {
        if (t.Id is < 0 or > 3) continue;
        if (t.Class == 0) decoder.dcTables[t.Id] = JpegHuffmanDecoder.TryCreate(t);
        else if (t.Class == 1) decoder.acTables[t.Id] = JpegHuffmanDecoder.TryCreate(t);
      }

      foreach (var sc in scan.Components)
      {
        int frameIndex = -1;
        for (int i = 0; i < table.Components.Count; i++)
          if (table.Components[i].Id == sc.Id) frameIndex = i;
        if (frameIndex < 0) return null;

        var fc = table.Components[frameIndex];
        if (fc.H <= 0 || fc.V <= 0) return null;
        if (sc.DcTable is < 0 or > 3 || sc.AcTable is < 0 or > 3) return null;

        var dc = decoder.dcTables[sc.DcTable];
        var ac = decoder.acTables[sc.AcTable];
        if (dc == null || ac == null) return null;

        decoder.plan.Add((frameIndex, fc.H * fc.V, dc, ac));
        decoder.layout.Add((decoder.BlocksPerMcu, fc.H, fc.V));
        decoder.BlocksPerMcu += fc.H * fc.V;
      }

      return decoder;
    }

    /// <summary>
    /// Decode MCUs until the data runs out or <paramref name="mcuLimit"/> is reached, whichever comes
    /// first. Returns false the moment the stream is malformed, with <paramref name="mcus"/> holding the
    /// count that did decode cleanly — the count is the answer either way, because the walker's
    /// accounting is a count and not a picture.
    /// <para>
    /// On an MCU the data ran out inside, the reader is left where that MCU began rather than part-way
    /// through it, so <see cref="JpegBitReader.ConsumedBytes"/> always describes a whole number of MCUs.
    /// </para>
    /// </summary>
    public bool TryDecodeRun(ref JpegBitReader bits, int mcuLimit, out int mcus,
      List<int>? boundaries = null)
    {
      var coefficients = new short[64 * BlocksPerMcu];
      var predictors = new int[plan.Count];

      mcus = 0;
      while (mcus < mcuLimit && mcus < McuCount)
      {
        var mark = bits;
        if (boundaries != null) { var (b, p) = bits.Position; boundaries.Add(8 * b + p); }

        if (!TryDecodeMcu(ref bits, coefficients, predictors))
        {
          bool ranOut = bits.Exhausted;
          bits = mark;
          return ranOut;
        }

        mcus++;
      }

      if (boundaries != null) { var (b, p) = bits.Position; boundaries.Add(8 * b + p); }
      return true;
    }

    /// <summary>
    /// One MCU: every block of every component of the scan, in the interleaved order, into
    /// <paramref name="coefficients"/> in zig-zag order — the order they were written in and the order
    /// they will be written back in, so no de-zigzag is needed anywhere.
    /// </summary>
    public bool TryDecodeMcu(ref JpegBitReader bits, short[] coefficients, int[] predictors)
    {
      int block = 0;

      for (int c = 0; c < plan.Count; c++)
      {
        var (_, blocks, dc, ac) = plan[c];
        for (int b = 0; b < blocks; b++, block++)
        {
          var into = coefficients.AsSpan(64 * block, 64);
          into.Clear();
          if (!TryDecodeBlock(ref bits, dc, ac, into, ref predictors[c])) return false;
        }
      }

      return true;
    }


    // ----------------------------------------------------------------------------------------------------
    //                                          one 8x8 block
    // ----------------------------------------------------------------------------------------------------
    private static bool TryDecodeBlock(ref JpegBitReader bits, JpegHuffmanDecoder dc, JpegHuffmanDecoder ac,
      Span<short> coefficients, ref int predictor)
    {
      if (!dc.TryDecode(ref bits, out byte s)) return false;
      if (s > 11) return false;                           // a baseline DC difference is at most 11 bits

      if (s > 0)
      {
        if (!bits.TryRead(s, out int raw)) return false;
        predictor += Extend(raw, s);
      }
      if (predictor is < short.MinValue or > short.MaxValue) return false;
      coefficients[0] = (short)predictor;

      for (int k = 1; k <= 63;)
      {
        if (!ac.TryDecode(ref bits, out byte rs)) return false;
        int run = rs >> 4, size = rs & 0x0F;

        if (size == 0)
        {
          if (run != 15) break;                           // end of block
          k += 16;                                        // ZRL, a run of sixteen zeros
          continue;
        }

        if (size > 10) return false;                      // a baseline AC coefficient is at most 10 bits
        k += run;
        if (k > 63) return false;

        if (!bits.TryRead(size, out int raw)) return false;
        coefficients[k] = (short)Extend(raw, size);
        k++;
      }

      return true;
    }

    /// <summary>
    /// The sign convention of a JPEG magnitude: <paramref name="size"/> bits whose top half is the value
    /// itself and whose bottom half is that value, a full scale less one below zero.
    /// </summary>
    private static int Extend(int raw, int size) =>
      raw < 1 << size - 1 ? raw - (1 << size) + 1 : raw;
  }




  // ----------------------------------------------------------------------------------------------------
  //                                      the canonical code table
  // ----------------------------------------------------------------------------------------------------
  /// <summary>
  /// A DHT table turned into the three arrays a canonical Huffman decode needs: for each code length,
  /// the smallest and largest code of that length and where its values start. Decoding is then reading
  /// one bit at a time until the accumulated code stops exceeding the largest code of its length, which
  /// is the whole of the algorithm.
  /// </summary>
  internal sealed class JpegHuffmanDecoder
  {
    private readonly int[] minCode = new int[17];
    private readonly int[] maxCode = new int[17];
    private readonly int[] valuePtr = new int[17];
    private readonly IReadOnlyList<byte> values;

    private JpegHuffmanDecoder(IReadOnlyList<byte> values)
    {
      this.values = values;
    }

    /// <summary>
    /// The decoder for a table, or null when the table is not one: more codes of some length than that
    /// length can hold, or a value count the lengths do not add up to. Both are what a DHT segment with
    /// a hole in it looks like, and neither can be decoded from.
    /// </summary>
    public static JpegHuffmanDecoder? TryCreate(JpegHuffmanTable table)
    {
      if (table.Counts.Count != 16) return null;

      var decoder = new JpegHuffmanDecoder(table.Values);

      int code = 0, index = 0;
      for (int length = 1; length <= 16; length++)
      {
        int count = table.Counts[length - 1];

        decoder.valuePtr[length] = index;
        decoder.minCode[length] = code;
        code += count;
        index += count;
        decoder.maxCode[length] = count > 0 ? code - 1 : -1;

        if (code > 1 << length) return null;              // an over-full table: the codes do not fit
        code <<= 1;
      }

      return index == table.Values.Count ? decoder : null;
    }

    /// <summary>
    /// The value the next code stands for, or false when the next 16 bits match no code in the table or
    /// the data runs out first. False is the ordinary answer on a wrong bit phase and is what makes the
    /// resync search cheap.
    /// </summary>
    public bool TryDecode(ref JpegBitReader bits, out byte value)
    {
      value = 0;

      if (!bits.TryRead(1, out int code)) return false;
      for (int length = 1; length <= 16; length++)
      {
        if (maxCode[length] >= 0 && code <= maxCode[length])
        {
          int index = valuePtr[length] + code - minCode[length];
          if (index < 0 || index >= values.Count) return false;
          value = values[index];
          return true;
        }

        if (length == 16) return false;
        if (!bits.TryRead(1, out int bit)) return false;
        code = code << 1 | bit;
      }

      return false;
    }
  }




  // ----------------------------------------------------------------------------------------------------
  //                                  the canonical code table, written
  // ----------------------------------------------------------------------------------------------------
  /// <summary>
  /// The same DHT table turned round: for each value, the code that stands for it and how many bits long
  /// that code is. Canonical Huffman makes both sides the same walk — the codes are handed out in value
  /// order, shortest length first — so this is <see cref="JpegHuffmanDecoder.TryCreate"/>'s loop with the
  /// value and the code changing places.
  /// <para>
  /// <see cref="TryWrite"/> reports a value the table has no code for rather than inventing one, because
  /// the repair re-encodes with the file's own tables and a file's table need not contain every symbol a
  /// splice wants. That is a refusal, not an error.
  /// </para>
  /// </summary>
  internal sealed class JpegHuffmanEncoder
  {
    private readonly int[] codes = new int[256];
    private readonly byte[] lengths = new byte[256];

    /// <summary>
    /// The encoder for a table, or null when the table is not one: more codes of some length than that
    /// length can hold, a value count the lengths do not add up to, or one value given two codes. The
    /// first two are what a DHT segment with a hole in it looks like, and are what the decoder refuses.
    /// </summary>
    public static JpegHuffmanEncoder? TryCreate(JpegHuffmanTable table)
    {
      if (table.Counts.Count != 16) return null;

      var encoder = new JpegHuffmanEncoder();

      int code = 0, index = 0;
      for (int length = 1; length <= 16; length++)
      {
        for (int i = 0; i < table.Counts[length - 1]; i++, index++, code++)
        {
          if (index >= table.Values.Count) return null;

          byte value = table.Values[index];
          if (encoder.lengths[value] != 0) return null;   // one value, two codes: not a canonical table

          encoder.codes[value] = code;
          encoder.lengths[value] = (byte)length;
        }

        if (code > 1 << length) return null;              // an over-full table: the codes do not fit
        code <<= 1;
      }

      return index == table.Values.Count ? encoder : null;
    }

    /// <summary>
    /// The code for <paramref name="value"/>, written to <paramref name="bits"/>, or false when this
    /// table has no code for it.
    /// </summary>
    public bool TryWrite(JpegBitWriter bits, byte value)
    {
      if (lengths[value] == 0) return false;

      bits.Write(codes[value], lengths[value]);
      return true;
    }
  }




  // ----------------------------------------------------------------------------------------------------
  //                                           the bit stream
  // ----------------------------------------------------------------------------------------------------
  /// <summary>
  /// The entropy-coded data as a stream of bits, most significant first, with the <c>0xFF 0x00</c>
  /// stuffing removed. It can be started at any bit of its first byte, which is what the resync search
  /// needs: a segment after a hole begins at a known byte and an unknown phase.
  /// <para>
  /// It is a value, and copying it is how a caller saves a position to come back to — there is no
  /// rewind, because every use of it either succeeds or is abandoned.
  /// </para>
  /// </summary>
  internal ref struct JpegBitReader
  {
    private readonly ReadOnlySpan<byte> data;
    private int at;
    private int lastByte;
    private uint accumulator;
    private int held;
    private bool exhausted;

    /// <summary>
    /// A reader over <paramref name="data"/>, starting <paramref name="skipBits"/> bits into it. The
    /// span is the entropy-coded data alone, so running off its end is the same event as reaching a
    /// marker.
    /// </summary>
    public JpegBitReader(ReadOnlySpan<byte> data, int skipBits = 0)
    {
      this.data = data;
      at = 0;
      lastByte = 0;
      accumulator = 0;
      held = 0;
      exhausted = false;

      for (int i = 0; i < skipBits; i++) TryRead(1, out _);
    }

    /// <summary>A read has run off the end of the data. Reaching this at an MCU boundary is the normal
    /// end of a segment; reaching it inside one is the hole that cut the segment short.</summary>
    public readonly bool Exhausted => exhausted;

    /// <summary>
    /// Bytes taken out of the data so far, stuffing bytes counted. A segment consumed exactly ends with
    /// this at the segment's length and at most seven bits of padding still held.
    /// </summary>
    public readonly int ConsumedBytes => at;

    /// <summary>Bits read out of the data but not yet handed to a caller, which at the end of a
    /// correctly consumed segment is the encoder's padding.</summary>
    public readonly int HeldBits => held;

    /// <summary>
    /// Where the next bit is: the data byte holding it, and how many of that byte's bits have already
    /// been handed out. A reader started at that byte and bit resumes exactly here, which is how one
    /// decode hands a boundary it found to another.
    /// <para>
    /// This is not <see cref="ConsumedBytes"/> less <see cref="HeldBits"/>: when the byte in hand is a
    /// stuffed <c>0xFF</c> the pair was taken together, and the byte to resume on is the <c>0xFF</c>
    /// rather than the <c>0x00</c> that follows it.
    /// </para>
    /// </summary>
    public readonly (int Byte, int Bits) Position => held == 0 ? (at, 0) : (lastByte, 8 - held);

    /// <summary>
    /// The next <paramref name="count"/> bits as an unsigned value, or false when the data runs out
    /// before they are all there.
    /// </summary>
    public bool TryRead(int count, out int value)
    {
      value = 0;

      while (held < count)
        if (!TryFill()) return false;

      value = (int)(accumulator >> held - count & (1u << count) - 1);
      held -= count;
      return true;
    }

    /// <summary>
    /// One more byte into the accumulator, unstuffing as it goes. An <c>0xFF</c> followed by <c>0x00</c>
    /// is a literal <c>0xFF</c>; an <c>0xFF</c> followed by anything else is a marker, which ends the
    /// data whatever the span still holds.
    /// </summary>
    private bool TryFill()
    {
      if (exhausted || at >= data.Length) { exhausted = true; return false; }

      byte b = data[at];
      lastByte = at;
      if (b == 0xFF)
      {
        if (at + 1 >= data.Length || data[at + 1] != 0x00) { exhausted = true; return false; }
        at += 2;
      }
      else at++;

      accumulator = accumulator << 8 | b;
      held += 8;
      return true;
    }
  }



  // ----------------------------------------------------------------------------------------------------
  //                                       the bit stream, written
  // ----------------------------------------------------------------------------------------------------
  /// <summary>
  /// The entropy-coded data as a stream of bits, most significant first, with the <c>0xFF 0x00</c>
  /// stuffing put back in: <see cref="JpegBitReader"/> run the other way. Every <c>0xFF</c> the codes
  /// happen to produce is followed by a <c>0x00</c>, so no byte of the output can be mistaken for a
  /// marker.
  /// <para>
  /// It appends to a caller's list rather than owning a buffer, because the header has already been
  /// copied into that list and the scan simply continues it.
  /// </para>
  /// </summary>
  internal sealed class JpegBitWriter
  {
    private readonly List<byte> output;
    private uint accumulator;
    private int held;

    public JpegBitWriter(List<byte> output)
    {
      this.output = output;
    }

    /// <summary>
    /// The low <paramref name="count"/> bits of <paramref name="value"/>, most significant first. A
    /// negative value is written as its two's complement low bits, which is the sign convention
    /// <c>Extend</c> reads back. At most sixteen bits go in at a time and at most seven are ever held
    /// over, so the accumulator cannot overflow.
    /// </summary>
    public void Write(int value, int count)
    {
      if (count <= 0) return;

      accumulator = accumulator << count | (uint)value & (1u << count) - 1;
      held += count;

      while (held >= 8)
      {
        held -= 8;
        byte b = (byte)(accumulator >> held);
        output.Add(b);
        if (b == 0xFF) output.Add(0x00);
      }
    }

    /// <summary>
    /// The last partial byte, padded to a byte boundary with one bits, which is what an encoder writes at
    /// the end of a scan and what the decoder's Huffman walk reads as the end of the data.
    /// </summary>
    public void Flush()
    {
      if (held > 0) Write((1 << 8 - held) - 1, 8 - held);
    }
  }
}
