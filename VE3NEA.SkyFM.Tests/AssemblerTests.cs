using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Xunit;

namespace VE3NEA.SkyFM.Tests
{
  /// <summary>
  /// Grammar-layer tests over the transcript shapes the spike actually observed on the ARISS capture
  /// (asr-spike/step2-ARISS.md, step3-ARISS.md): phonetic spelling, collapsed callsigns, garbles,
  /// filler — the assembler must reproduce the spike-validated behavior, including its documented
  /// misses (they are what the later constraint/fusion stages recover).
  /// </summary>
  public class AssemblerTests
  {
    private static IReadOnlyList<AsrWord> Words(params string[] texts) =>
      texts.Select((t, i) => new AsrWord(t, i * 0.5, i * 0.5 + 0.4, 0.8f)).ToArray();

    private static IReadOnlyList<Candidate> Assemble(params string[] texts) =>
      new Assembler().Assemble(Words(texts));


    // ----------------------------------------------------------------------------------------------------
    //                                     recovery (spike true positives)
    // ----------------------------------------------------------------------------------------------------

    [Fact]
    public void PhoneticSpelling_AssemblesCallsign()
    {
      // 5:11 "Kilo Delta Zero, Quebec Papa Charlie" -> KD0QPC (recovered exact in the spike)
      var c = Assemble("Kilo", "Delta", "Zero,", "Quebec", "Papa", "Charlie");
      c.Should().ContainSingle(x => x.Kind == CandidateKind.Callsign).Which.Text.Should().Be("KD0QPC");
    }

    [Fact]
    public void PhoneticSpelling_AssemblesGrid()
    {
      // 3:49 "Echo Mike 85" -> EM85
      var c = Assemble("Echo", "Mike", "85");
      c.Should().ContainSingle(x => x.Kind == CandidateKind.Grid).Which.Text.Should().Be("EM85");
    }

    [Fact]
    public void FullNatoCallsign_WithFuzzyWord_Assembles()
    {
      // the 2:28 ear truth as NATO words, with a misspelled 'Quebeck' (edit distance 1)
      var c = Assemble("Kilo", "Quebeck", "Four", "Golf", "India", "Kilo");
      c.Should().ContainSingle(x => x.Kind == CandidateKind.Callsign).Which.Text.Should().Be("KQ4GIK");
    }

    [Fact]
    public void CollapsedCallsign_EmittedDirectly()
    {
      // 4:39 "AB2IW" (Whisper collapsed the phonetics to the bare callsign)
      var c = Assemble("copy", "AB2IW", "thanks");
      var call = c.Should().ContainSingle(x => x.Kind == CandidateKind.Callsign).Which;
      call.Text.Should().Be("AB2IW");
      call.Confidence.Should().Be(0.8f);
    }

    [Fact]
    public void EnglishWordsAroundPhonetics_AreSeparators()
    {
      // "can you copy fox mike one eight" — F/M/1/8 assemble, the English words must not contribute
      var c = Assemble("can", "you", "copy", "Fox", "Mike", "one", "eight");
      c.Should().ContainSingle().Which.Text.Should().Be("FM18");
    }

    [Fact]
    public void CallsignAndGrid_BothExtractedFromOneRun()
    {
      var c = Assemble("Kilo", "Romeo", "4,", "Juliet", "India", "2-3");
      // the spike's 3:53 near-miss: suffix garbled, longest valid parse is KR4JI (LCS 5/6 of KR4JIQ) —
      // full recovery is the fusion stage's job, not the assembler's
      c.Should().Contain(x => x.Kind == CandidateKind.Callsign && x.Text == "KR4JI");
      // the overlapping JI23 grid reading of the same symbols loses to the callsign in the partition —
      // the spike's known assembly-artifact FP class, now suppressed at the source
      c.Should().NotContain(x => x.Kind == CandidateKind.Grid);
    }

    [Fact]
    public void MergedRun_GridThenCallsign_PartitionsCleanly()
    {
      // the real ARISS 3:52 run "…EM85 Kilo Romeo 4 Juliet India Quebec": independent longest-match
      // scans emitted junk EM85KR shadowing the true KR4JIQ — the partition must split EM85|KR4JIQ
      var c = Assemble("Echo", "Mike", "85.", "Kilo,", "Romeo", "4,", "Juliet", "India", "Quebec");
      c.Should().HaveCount(2);
      c.Should().Contain(x => x.Kind == CandidateKind.Grid && x.Text == "EM85");
      c.Should().Contain(x => x.Kind == CandidateKind.Callsign && x.Text == "KR4JIQ");
    }

    [Fact]
    public void CollapsedCallsignGridToken_PartitionsCleanly()
    {
      // the real ARISS 4:47 token "AB2IWFN22?": not a valid callsign, so its symbols form one run —
      // AB2IWFN must not steal the grid's FN
      var c = Assemble("can", "you", "copy", "AB2IWFN22?");
      c.Should().HaveCount(2);
      c.Should().Contain(x => x.Kind == CandidateKind.Callsign && x.Text == "AB2IW");
      c.Should().Contain(x => x.Kind == CandidateKind.Grid && x.Text == "FN22");
    }

    [Fact]
    public void TruncatedTailAfterIdentifier_EmitsPartialCallsign()
    {
      // the real hybrid ARISS run "…EM85 Kilo Romeo 4" (the rest lost to a 'Julius' separator): the
      // truncated KR4 must not glue onto the grid as the junk callsign EM85KR
      var c = Assemble("Echo", "Mike", "85.", "Kilo,", "Romeo", "4,");
      c.Should().HaveCount(2);
      c.Should().Contain(x => x.Kind == CandidateKind.Grid && x.Text == "EM85");
      c.Should().Contain(x => x.Kind == CandidateKind.Callsign && x.Text == "KR4");
    }

    [Fact]
    public void TruncatedPrefixAlone_EmitsNothing()
    {
      // with no completed grid in front of it, a bare prefix is noise, not a partial
      Assemble("Kilo", "Romeo", "4,").Should().BeEmpty();
      Assemble("Alpha", "Bravo", "Two").Should().BeEmpty();
    }

    [Fact]
    public void TruncatedTailAfterCallsign_IsNotAPartial()
    {
      // after a callsign a truncated tail is never claimed as a partial (that would fragment longer
      // valid parses, e.g. KR4JI into KR4J|I23); when it happens to extend the callsign grammar it
      // still glues on — the known junk mode whose recovery path is fusion's containment absorption
      // into the repeated true text
      var c = Assemble("Kilo", "Bravo", "Two", "India", "Whiskey", "Kilo", "Romeo", "Four");
      c.Should().ContainSingle().Which.Text.Should().Be("KB2IWKR");
    }

    [Fact]
    public void AdjacentCallsigns_BothParsed()
    {
      // the sherpa phonetic stream "…KB2IW K2HZV" with no separator: the equal-coverage tie
      // KB2I|WK2HZV must lose to KB2IW|K2HZV (longer earlier span — junk glues forward)
      var c = Assemble("Kilo", "Bravo", "Two", "India", "Whiskey", "Kilo", "Two", "Hotel", "Zulu", "Victor");
      c.Should().HaveCount(2);
      c[0].Text.Should().Be("KB2IW");
      c[1].Text.Should().Be("K2HZV");
    }


    // ----------------------------------------------------------------------------------------------------
    //                                 precision (spike noise/filler behavior)
    // ----------------------------------------------------------------------------------------------------

    [Fact]
    public void WhisperFiller_YieldsNothing()
    {
      Assemble("Thank", "you.").Should().BeEmpty();
      Assemble("We'll", "be", "right", "back").Should().BeEmpty();
      Assemble("Transcription", "by", "CastingWords").Should().BeEmpty();
    }

    [Fact]
    public void PromptRegurgitation_YieldsNothing()
    {
      // spike fix #2: on noise the primed decoder emitted the prompt back — none of it is id-shaped
      Assemble("CQ,", "QRZ,", "over,", "QSO,", "this", "is,", "copy.").Should().BeEmpty();
    }

    [Fact]
    public void BareLetterFragments_AreDroppedByDesign()
    {
      // 5:21 "K2, HZV": treating bare letter groups as symbols would swallow English words; the
      // fragment is deliberately lost here (the collapsed form "K2HZB" at 5:43 is what recovers it)
      Assemble("K2,", "HZV").Should().BeEmpty();
    }

    [Fact]
    public void DroppedLeadingKilo_DoesNotParse()
    {
      // 3:49 "4, Golf India Kilo": Whisper dropped the leading Kilo; '4GIK' cannot open a callsign —
      // the spike's documented miss, recovered only by cross-repeat fusion
      Assemble("4,", "Golf", "India", "Kilo").Should().BeEmpty();
    }

    [Fact]
    public void AssemblyArtifact_JI23_IsCharacterized()
    {
      // 'Juliet India 2-3' standing alone parses as grid-shaped JI23 — the spike's known
      // assembly-artifact FP class, suppressed later by the Pass-B constraint/abstention, not here
      var c = Assemble("Juliet", "India", "2-3");
      var art = c.Should().ContainSingle().Which;
      art.Text.Should().Be("JI23");
      art.Kind.Should().Be(CandidateKind.Grid);
    }


    // ----------------------------------------------------------------------------------------------------
    //                                     run splitting & confidence
    // ----------------------------------------------------------------------------------------------------

    [Fact]
    public void LongPause_SplitsTheRun()
    {
      var words = new List<AsrWord>
      {
        new("Echo", 0.0, 0.4, 0.8f),
        new("Mike", 0.5, 0.9, 0.8f),
        new("85", 3.0, 3.4, 0.8f)      // 2.3 s after 'Mike' — a new utterance, not the same identifier
      };
      new Assembler().Assemble(words).Should().BeEmpty();

      words[2] = new AsrWord("85", 1.0, 1.4, 0.8f);
      new Assembler().Assemble(words).Should().ContainSingle().Which.Text.Should().Be("EM85");
    }

    [Fact]
    public void CharConfidence_ComesFromSourceWords()
    {
      var words = new List<AsrWord>
      {
        new("Echo", 0.0, 0.4, 0.9f),
        new("Mike", 0.5, 0.9, 0.6f),
        new("85", 1.0, 1.4, 0.7f)
      };
      var c = new Assembler().Assemble(words).Should().ContainSingle().Which;
      c.Text.Should().Be("EM85");
      c.CharConfidence.Should().Equal(0.9f, 0.6f, 0.7f, 0.7f);
      c.Confidence.Should().BeApproximately((0.9f + 0.6f + 0.7f + 0.7f) / 4, 1e-6f);
      c.StartSeconds.Should().Be(0.0);
      c.EndSeconds.Should().Be(1.4);
    }

    [Fact]
    public void RepeatedIdentifier_IsDeduplicated()
    {
      var c = Assemble("Echo", "Mike", "85", "again", "Echo", "Mike", "85");
      c.Should().ContainSingle().Which.Text.Should().Be("EM85");
    }
  }
}
