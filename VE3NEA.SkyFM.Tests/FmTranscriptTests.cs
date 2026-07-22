using System.Linq;
using FluentAssertions;
using Xunit;

namespace VE3NEA.SkyFM.Tests
{
  /// <summary>Unit tests of the §10.2/§10.3 transcript stream: display vocabulary filtering and
  /// pause-driven spacing/line breaks.</summary>
  public class FmTranscriptTests
  {
    private static AsrWord W(string text, double start, double dur = 0.3, float conf = 0.9f)
      => new(text, start, start + dur, conf);


    // ----------------------------------------------------------------------------------------------------
    //                                       display vocabulary
    // ----------------------------------------------------------------------------------------------------
    [Theory]
    [InlineData("kilo", "kilo")]          // phonetic word, verbatim — never collapsed to K
    [InlineData("Juliett,", "juliett")]   // fuzzy NATO with punctuation, normalized
    [InlineData("five", "5")]             // number word → digit
    [InlineData("85", "85")]              // digit string
    [InlineData("EM85", "EM85")]          // collapsed identifier-shaped fragment, as emitted
    [InlineData("en", "N")]               // spoken letter → collapsed
    [InlineData("vee", "V")]
    [InlineData("QSL", "QSL")]            // proword
    [InlineData("B", "B")]                // bare single-letter token
    [InlineData("thanks", null)]          // ordinary English — never shown
    [InlineData("the", null)]
    public void DisplayToken_MapsTheDisplayVocabulary(string word, string? expected)
      => new FmTranscriptBuilder().DisplayToken(word).Should().Be(expected);


    // ----------------------------------------------------------------------------------------------------
    //                                 squelch-interval line boundaries
    // ----------------------------------------------------------------------------------------------------

    // feed one squelch-open interval [start, end] and its words (spaced 0.3 s apart within the interval)
    private static void Tx(FmTranscriptBuilder b, double start, double end, params string[] words)
      => b.Add(start, end, words.Select((t, i) => W(t, start + 0.3 * i)));

    [Fact]
    public void EachIntervalIsALine_TokensSingleSpaced_SpannedByTheSquelchTimes()
    {
      var b = new FmTranscriptBuilder();
      Tx(b, 10.0, 12.0, "kilo", "delta", "five");
      Tx(b, 15.0, 16.0, "echo", "mike", "85");   // 3 s gap → its own line
      b.Flush();

      b.Lines.Should().HaveCount(2);
      b.Lines[0].Text.Should().Be("kilo delta 5");
      b.Lines[1].Text.Should().Be("echo mike 85");
      b.Lines[0].StartSeconds.Should().Be(10.0);
      b.Lines[0].EndSeconds.Should().Be(12.0, "the click-to-play span is the squelch-open interval, not the word times");
      b.Lines[1].StartSeconds.Should().Be(15.0);
    }

    [Fact]
    public void IntervalsWithinTheMergeGap_ShareALine()
    {
      var b = new FmTranscriptBuilder();
      Tx(b, 10.0, 12.0, "kilo", "delta");
      Tx(b, 12.4, 13.0, "five");          // 0.4 s gap ≤ 0.5 → merged into the same line
      Tx(b, 14.0, 15.0, "echo");          // 1.0 s gap > 0.5 → new line
      b.Flush();

      b.Lines.Should().HaveCount(2);
      b.Lines[0].Text.Should().Be("kilo delta 5");
      b.Lines[0].StartSeconds.Should().Be(10.0);
      b.Lines[0].EndSeconds.Should().Be(13.0, "a merged line spans through the last merged interval");
      b.Lines[1].Text.Should().Be("echo");
    }

    [Fact]
    public void IntervalWithNoDisplayText_PrintsQuestionMarks()
    {
      var b = new FmTranscriptBuilder();
      Tx(b, 10.0, 12.0, "thank", "very", "much");   // all outside the display vocabulary
      Tx(b, 20.0, 21.0);                             // the engine heard nothing at all
      b.Flush();

      b.Lines.Should().HaveCount(2);
      b.Lines[0].Text.Should().Be(FmTranscriptBuilder.NoText);
      b.Lines[1].Text.Should().Be(FmTranscriptBuilder.NoText);
      b.Lines[0].StartSeconds.Should().Be(10.0);
      b.Lines[0].EndSeconds.Should().Be(12.0);
    }

    [Fact]
    public void IgnoredWords_AreDroppedButTheNeighboursStaySingleSpaced()
    {
      var b = new FmTranscriptBuilder();
      Tx(b, 10.0, 12.0, "kilo", "thanks", "delta");
      b.Flush();
      b.Lines.Should().ContainSingle().Which.Text.Should().Be("kilo delta");
    }

    [Fact]
    public void StreamingAcrossIntervals_LinesCloseWhenTheNextOneStartsBeyondTheMergeGap()
    {
      var b = new FmTranscriptBuilder();
      Tx(b, 10.0, 11.0, "kilo", "bravo");
      b.Lines.Should().BeEmpty("the line stays open until the next interval decides the merge");
      b.Pending!.Text.Should().Be("kilo bravo");

      Tx(b, 100.0, 101.0, "echo");   // far beyond the merge gap → closes the previous line
      b.Lines.Should().ContainSingle();
      b.Flush();
      b.Lines.Should().HaveCount(2);
      b.Lines.Select(l => l.Text).Should().Equal("kilo bravo", "echo");
    }
  }
}
