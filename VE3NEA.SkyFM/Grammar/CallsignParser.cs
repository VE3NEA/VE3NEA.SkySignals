using System.Text.RegularExpressions;

namespace VE3NEA.SkyFM
{
  /// <summary>Amateur callsign structure validation (plan §5.4) — the production regex: optional
  /// portable prefix, prefix, suffix, optional modifier.</summary>
  public static class CallsignParser
  {
    public static Regex CallsignRegex = new Regex(
      // portable prefix
      @"^((?:(?:[A-PR-Z](?:(?:[A-Z](?:\d[A-Z]?)?)|(?:\d[\dA-Z]?))?)|(?:[2-9][A-Z]{1,2}\d?))\/)?" +
      // prefix
      @"((?:(?:[A-PR-Z][A-Z]?)|(?:[2-9][A-Z]{1,2}))\d)" +
      // suffix
      @"(\d{0,3}[A-Z]{1,8})" +
      // modifier
      @"(\/[\dA-Z]{1,4})?$",
      RegexOptions.Compiled
    );

    /// <summary>True when <paramref name="s"/> (upper-case alphanumeric + '/') is a structurally valid
    /// callsign.</summary>
    public static bool IsValid(string s) => s.Length > 0 && CallsignRegex.IsMatch(s);
  }

  /// <summary>Maidenhead grid locator validation (plan §5.4). 4-character squares only — 6-char
  /// subsquares are never sent on the satellites.</summary>
  public static class MaidenheadParser
  {
    public static Regex GridSquare4Regex = new Regex(@"^[A-R]{2}\d{2}$", RegexOptions.Compiled);

    /// <summary>True when <paramref name="s"/> is a valid 4-character grid square.</summary>
    public static bool IsValid(string s) => GridSquare4Regex.IsMatch(s);
  }
}
