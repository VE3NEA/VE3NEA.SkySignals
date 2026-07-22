using System;
using System.Collections.Generic;
using System.Text;

namespace VE3NEA.SkyFM
{
  /// <summary>Tunables of the §10.2/§10.3 transcript stream.</summary>
  public sealed record FmTranscriptOptions
  {
    /// <summary>A line is one squelch-open interval; when the next interval starts within this gap (s) of
    /// the current line's end, the two are merged onto one line.</summary>
    public double MergeGapS { get; init; } = 0.5;

    /// <summary>The predefined prowords shown verbatim (§10.2; list TBD beyond these), lower
    /// case.</summary>
    public IReadOnlyCollection<string> Prowords { get; init; } = ["qsl", "copy"];
  }

  /// <summary>One display line: the text (timestamp not included — the panel renders it from
  /// <paramref name="StartSeconds"/> minus AOS) and the audio range it came from, the §10.4
  /// click-to-play span.</summary>
  public sealed record FmTranscriptLine(string Text, double StartSeconds, double EndSeconds);

  /// <summary>
  /// Builds the §10.2/§10.3 decoded-transcript stream from recognized words: only the display
  /// vocabulary survives — phonetic words verbatim, numbers as digits, spoken letters collapsed to the
  /// letter, prowords upper-case, plus collapsed identifier-shaped fragments ("EM85") as the engines
  /// emit them; every other token is ignored, never shown. Line boundaries follow the squelch, not the
  /// pauses between words: each line is one squelch-open interval (merged with the next when they nearly
  /// abut, <see cref="FmTranscriptOptions.MergeGapS"/>), its tokens single-spaced; an interval with no
  /// display vocabulary prints <see cref="NoText"/>. Streaming: <see cref="Add"/> intervals in time
  /// order, <see cref="Flush"/> at end of pass; <see cref="Lines"/> grows as lines close.
  ///
  /// <para>Spoken letters are display-only vocabulary: the identifier path deliberately refuses them
  /// (<see cref="PhoneticDecoder"/> — the spike's precision trap), but the transcript shows the raw
  /// stream, where "en ee ar" reading as N E A is information, and an occasional English homophone
  /// ("you" → U) is acceptable noise.</para>
  /// </summary>
  public sealed class FmTranscriptBuilder
  {
    /// <summary>Shown in place of the text when a squelch-open interval yielded no display vocabulary.</summary>
    public const string NoText = "???";

    private static readonly Dictionary<string, char> SpokenLetters = new()
    {
      ["ay"] = 'A', ["bee"] = 'B', ["cee"] = 'C', ["dee"] = 'D', ["ee"] = 'E', ["ef"] = 'F',
      ["gee"] = 'G', ["aitch"] = 'H', ["eye"] = 'I', ["jay"] = 'J', ["kay"] = 'K', ["el"] = 'L',
      ["em"] = 'M', ["en"] = 'N', ["oh"] = 'O', ["pee"] = 'P', ["cue"] = 'Q', ["ar"] = 'R',
      ["ess"] = 'S', ["tee"] = 'T', ["you"] = 'U', ["vee"] = 'V', ["ex"] = 'X', ["why"] = 'Y',
      ["zee"] = 'Z', ["zed"] = 'Z'
    };

    private readonly FmTranscriptOptions options;
    private readonly List<FmTranscriptLine> lines = new();
    private readonly StringBuilder text = new();
    private double lineStart, lineEnd;
    private bool open;

    public FmTranscriptBuilder(FmTranscriptOptions? options = null)
      => this.options = options ?? new FmTranscriptOptions();

    /// <summary>Lines closed so far, in time order.</summary>
    public IReadOnlyList<FmTranscriptLine> Lines => lines;

    /// <summary>The in-progress (not-yet-closed) line, or null when none is open — the live tail the
    /// panel shows before the next interval closes it (§10.3). A snapshot; safe to marshal to the UI
    /// thread.</summary>
    public FmTranscriptLine? Pending =>
      open ? new FmTranscriptLine(text.Length == 0 ? NoText : text.ToString(), lineStart, lineEnd) : null;

    /// <summary>Feed one squelch-open interval and the words recognized within it (recording-relative
    /// times, non-decreasing across calls). The interval opens a new line unless it starts within
    /// <see cref="FmTranscriptOptions.MergeGapS"/> of the current line's end, in which case the two merge.
    /// Words outside the display vocabulary are dropped; an interval with no display vocabulary prints
    /// <see cref="NoText"/>.</summary>
    public void Add(double startSeconds, double endSeconds, IEnumerable<AsrWord> words)
    {
      if (open && startSeconds - lineEnd > options.MergeGapS) CloseLine();

      if (!open) { lineStart = startSeconds; lineEnd = endSeconds; open = true; }
      else lineEnd = Math.Max(lineEnd, endSeconds);

      foreach (var word in words)
      {
        string? token = DisplayToken(word.Text);
        if (token == null) continue;
        if (text.Length > 0) text.Append(' ');
        text.Append(token);
      }
    }

    /// <summary>End of pass: close the in-progress line. Call once after the last
    /// <see cref="Add"/>.</summary>
    public void Flush() => CloseLine();

    private void CloseLine()
    {
      if (!open) return;
      lines.Add(new FmTranscriptLine(text.Length == 0 ? NoText : text.ToString(), lineStart, lineEnd));
      text.Clear();
      open = false;
    }


    // ----------------------------------------------------------------------------------------------------
    //                                       display vocabulary
    // ----------------------------------------------------------------------------------------------------

    /// <summary>The §10.2 display form of a raw transcript word, or null when the word must never be
    /// shown.</summary>
    internal string? DisplayToken(string word)
    {
      string w = PhoneticDecoder.Normalize(word);
      if (w.Length == 0) return null;
      if (options.Prowords.Contains(w)) return w.ToUpperInvariant();
      if (SpokenLetters.TryGetValue(w, out char letter)) return letter.ToString();

      string symbols = PhoneticDecoder.ToSymbols(word);
      if (symbols.Length == 1 && char.IsLetter(symbols[0])) return w;   // phonetic word, verbatim
      if (symbols.Length > 0) return symbols;                           // digits / collapsed fragments

      // a bare single-letter token ("B", "n") is identifier-symbol noise to the assembler but
      // legitimate display content in the raw stream
      string au = PhoneticDecoder.Alnum(word);
      return au.Length == 1 ? au : null;
    }
  }
}
