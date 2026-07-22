using FluentAssertions;
using Xunit;

namespace VE3NEA.SkyFM.Tests
{
  public class PhoneticDecoderTests
  {
    [Theory]
    [InlineData("Kilo", "K")]
    [InlineData("alfa", "A")]
    [InlineData("Juliett,", "J")]      // alternate spelling + punctuation
    [InlineData("fox", "F")]           // short form of foxtrot
    [InlineData("niner", "9")]
    [InlineData("fife", "5")]
    [InlineData("85", "85")]           // digit string passes through
    [InlineData("2-3", "23")]
    [InlineData("AB2IW", "AB2IW")]     // collapsed callsign fragment
    [InlineData("K2", "K2")]           // digit-bearing fragment
    [InlineData("FN22", "FN22")]       // grid-shaped fragment
    public void ToSymbols_MapsIdentifierWords(string word, string expected)
      => PhoneticDecoder.ToSymbols(word).Should().Be(expected);

    [Theory]
    [InlineData("the")]
    [InlineData("copy")]
    [InlineData("Thank")]
    [InlineData("you.")]
    [InlineData("HZV")]                // bare letter group: no digit, not id-shaped — dropped by design
    [InlineData("bee")]                // spoken letters deliberately unmapped (English-word trap)
    [InlineData("oh")]
    [InlineData("")]
    public void ToSymbols_RejectsSeparators(string word)
      => PhoneticDecoder.ToSymbols(word).Should().BeEmpty();

    [Theory]
    [InlineData("quebeck", "Q")]       // insertion
    [InlineData("julie", "J")]         // deletion (juliet)
    [InlineData("mikee", "M")]
    [InlineData("hotal", "H")]         // substitution
    public void ToSymbols_FuzzyNatoWithinEdit1(string word, string expected)
      => PhoneticDecoder.ToSymbols(word).Should().Be(expected);

    [Theory]
    [InlineData("VE3NEA", true)]
    [InlineData("KQ4GIK", true)]
    [InlineData("ZL7/ZL2FBB", true)]   // portable prefix
    [InlineData("K2HZV", true)]
    [InlineData("KD0QPC", true)]
    [InlineData("4GIK", false)]        // dropped leading letter cannot open a callsign
    [InlineData("K2", false)]          // no suffix letter yet
    [InlineData("JI23", false)]        // suffix must end in letters — but it IS grid-shaped, see below
    [InlineData("", false)]
    public void CallsignRegex_Validates(string s, bool valid)
      => CallsignParser.IsValid(s).Should().Be(valid);

    [Theory]
    [InlineData("EM85", true)]
    [InlineData("FN03", true)]
    [InlineData("FM18", true)]
    [InlineData("JI23", true)]         // the spike's assembly-artifact FP was a grid-shaped parse
    [InlineData("SM85", false)]        // first letter beyond R
    [InlineData("EM8", false)]
    [InlineData("EM857", false)]       // 6-char subsquares not used on the satellites
    public void GridRegex_Validates(string s, bool valid)
      => MaidenheadParser.IsValid(s).Should().Be(valid);
  }
}
