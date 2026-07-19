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
    //                                     pause-driven formatting
    // ----------------------------------------------------------------------------------------------------
    [Fact]
    public void PausesDriveSpacingAndLineBreaks()
    {
      var b = new FmTranscriptBuilder();
      // "kilo delta 5" — tight; then a 1.5 s pause (3 spaces); then "echo mike 85";
      // then a 4 s pause → new line "victor echo 3 N E A"
      b.Add(W("kilo", 10.0));
      b.Add(W("delta", 10.4));
      b.Add(W("five", 10.8));
      b.Add(W("echo", 12.6));
      b.Add(W("mike", 13.0));
      b.Add(W("85", 13.4));
      b.Add(W("victor", 17.7));
      b.Add(W("echo", 18.1));
      b.Add(W("three", 18.5));
      b.Add(W("en", 18.9));
      b.Add(W("ee", 19.3));
      b.Add(W("ay", 19.7));
      b.Flush();

      b.Lines.Should().HaveCount(2);
      b.Lines[0].Text.Should().Be("kilo delta 5   echo mike 85");
      b.Lines[1].Text.Should().Be("victor echo 3 N E A");
      b.Lines[0].StartSeconds.Should().Be(10.0);
      b.Lines[0].EndSeconds.Should().BeApproximately(13.7, 1e-9, "the click-to-play span covers the whole line");
      b.Lines[1].StartSeconds.Should().Be(17.7);
    }

    [Fact]
    public void IgnoredWords_DoNotAffectSpacing()
    {
      var b = new FmTranscriptBuilder();
      b.Add(W("kilo", 10.0));
      b.Add(W("thanks", 10.5));
      b.Add(W("delta", 11.2));
      b.Flush();

      b.Lines.Should().ContainSingle().Which.Text.Should().Be("kilo   delta",
        "the gap is measured across the ignored word — 0.9 s of silence in the display vocabulary");
    }

    [Fact]
    public void AllWordsIgnored_ProducesNoLines()
    {
      var b = new FmTranscriptBuilder();
      b.Add(W("thank", 10.0));
      b.Add(W("very", 10.4));
      b.Add(W("much", 10.8));
      b.Flush();
      b.Lines.Should().BeEmpty();
    }

    [Fact]
    public void StreamingAcrossTransmissions_LinesGrowIncrementally()
    {
      var b = new FmTranscriptBuilder();
      b.Add(W("kilo", 10.0));
      b.Add(W("bravo", 10.4));
      b.Lines.Should().BeEmpty("the line is still open");

      b.Add(W("echo", 100.0));
      b.Lines.Should().ContainSingle("a pause beyond the group gap closes the previous line");
      b.Flush();
      b.Lines.Should().HaveCount(2);
      b.Lines.Select(l => l.Text).Should().Equal("kilo bravo", "echo");
    }
  }
}
