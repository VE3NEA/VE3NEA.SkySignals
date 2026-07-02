using System;
using System.Collections.Generic;
using System.Globalization;

namespace VE3NEA.SkyTlm.Telemetry
{
  /// <summary>
  /// A tiny, safe expression evaluator — the constrained <c>expr</c> escape hatch of schema v2.
  /// It is precedence-climbing over a fixed operator set, NOT a general <c>eval</c>: only
  /// numeric literals, the whitelisted operators, a few named functions, and identifiers the caller
  /// resolves. One evaluator serves both field calibration (<c>expr</c>, arithmetic — calibration
  /// forms like <c>-128*x**0 + 1*x**1</c>) and conditional presence
  /// (<c>if</c>, where comparisons / logicals yield 1 or 0).
  /// </summary>
  /// <remarks>
  /// Grammar (low → high precedence): <c>?:</c> (ternary, right-assoc) ; <c>||</c> ; <c>&amp;&amp;</c> ;
  /// <c>|</c> ; <c>^</c> ; <c>&amp;</c> (bitwise) ; <c>== !=</c> ; <c>&lt; &lt;= &gt; &gt;=</c> ;
  /// <c>&lt;&lt; &gt;&gt;</c> (shift) ; <c>+ -</c> ; <c>* /</c> ; <c>**</c> (right-assoc) ;
  /// unary <c>+ -</c> ; then primaries: number, identifier, <c>fn(args)</c>, <c>( expr )</c>.
  /// The bitwise/shift operators (precedence: above comparison, shifts above additive) carry
  /// the HADES <c>_dec</c> nibble-packing formulas (e.g.
  /// <c>(vbatadc_lo_vcpuadc_hi &lt;&lt; 8) &amp; 0x0f00 | vbat2_hi &gt;&gt; 8</c>); they truncate both
  /// operands to <c>long</c>.
  /// Identifiers — notably <c>x</c> (the field's own raw value) and prior field names — come from the
  /// <c>lookup</c> delegate. The ternary handles conditional forms like
  /// <c>(raw[31] &lt; 512) ? -raw[30] : raw[30]</c>.
  /// </remarks>
  public static class ExprEvaluator
  {
    public static double Eval(string expr, Func<string, double> lookup)
    {
      var p = new Parser(expr, lookup);
      double v = p.ParseTernary();
      p.ExpectEnd();
      return v;
    }


    // ----------------------------------------------------------------------------------------------------
    //                                            tokenizer
    // ----------------------------------------------------------------------------------------------------
    private enum Kind { Num, Ident, Op, LParen, RParen, Comma, End }

    private readonly struct Token
    {
      public readonly Kind Kind;
      public readonly string Text;
      public readonly double Num;
      public Token(Kind kind, string text, double num = 0) { Kind = kind; Text = text; Num = num; }
    }

    private static List<Token> Tokenize(string s)
    {
      var toks = new List<Token>();
      int i = 0;
      while (i < s.Length)
      {
        char c = s[i];
        if (char.IsWhiteSpace(c)) { i++; continue; }

        // hex literal 0x... (the HADES nibble masks: 0x0f00, 0x0ff0, ...)
        if (c == '0' && i + 1 < s.Length && (s[i + 1] == 'x' || s[i + 1] == 'X'))
        {
          int start = i;
          i += 2;
          while (i < s.Length && Uri.IsHexDigit(s[i])) i++;
          string hex = s.Substring(start + 2, i - start - 2);
          if (hex.Length == 0) throw new FormatException($"expr: empty hex literal in \"{s}\"");
          toks.Add(new Token(Kind.Num, s.Substring(start, i - start),
            (double)Convert.ToInt64(hex, 16)));
          continue;
        }

        if (char.IsDigit(c) || (c == '.' && i + 1 < s.Length && char.IsDigit(s[i + 1])))
        {
          int start = i;
          while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.')) i++;
          if (i < s.Length && (s[i] == 'e' || s[i] == 'E'))   // exponent
          {
            i++;
            if (i < s.Length && (s[i] == '+' || s[i] == '-')) i++;
            while (i < s.Length && char.IsDigit(s[i])) i++;
          }
          string num = s.Substring(start, i - start);
          toks.Add(new Token(Kind.Num, num, double.Parse(num, CultureInfo.InvariantCulture)));
          continue;
        }

        if (char.IsLetter(c) || c == '_')
        {
          int start = i;
          while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] == '_')) i++;
          toks.Add(new Token(Kind.Ident, s.Substring(start, i - start)));
          continue;
        }

        // two-character operators first, then single-character
        string two = i + 1 < s.Length ? s.Substring(i, 2) : "";
        if (two is "**" or "==" or "!=" or "<=" or ">=" or "&&" or "||" or "<<" or ">>")
        {
          toks.Add(new Token(Kind.Op, two));
          i += 2;
          continue;
        }
        switch (c)
        {
          case '(': toks.Add(new Token(Kind.LParen, "(")); break;
          case ')': toks.Add(new Token(Kind.RParen, ")")); break;
          case ',': toks.Add(new Token(Kind.Comma, ",")); break;
          case '+': case '-': case '*': case '/': case '<': case '>': case '?': case ':':
          case '&': case '|': case '^':
            toks.Add(new Token(Kind.Op, c.ToString())); break;
          default:
            throw new FormatException($"expr: unexpected character '{c}' in \"{s}\"");
        }
        i++;
      }
      toks.Add(new Token(Kind.End, ""));
      return toks;
    }


    // ----------------------------------------------------------------------------------------------------
    //                                        precedence-climbing parser
    // ----------------------------------------------------------------------------------------------------
    private sealed class Parser
    {
      private readonly List<Token> toks;
      private readonly Func<string, double> lookup;
      private int pos;

      public Parser(string expr, Func<string, double> lookup)
      {
        toks = Tokenize(expr);
        this.lookup = lookup;
      }

      private Token Peek => toks[pos];
      private Token Next() => toks[pos++];

      public void ExpectEnd()
      {
        if (Peek.Kind != Kind.End) throw new FormatException($"expr: trailing tokens at '{Peek.Text}'");
      }

      // ternary <c>cond ? a : b</c> — lowest precedence, right-associative; cond truthiness is != 0.
      public double ParseTernary()
      {
        double cond = ParseExpr(0);
        if (Peek.Kind == Kind.Op && Peek.Text == "?")
        {
          Next();   // consume '?'
          double a = ParseTernary();
          if (!(Peek.Kind == Kind.Op && Peek.Text == ":"))
            throw new FormatException("expr: missing ':' in ternary");
          Next();   // consume ':'
          double b = ParseTernary();
          return cond != 0 ? a : b;
        }
        return cond;
      }

      // (precedence, right-associative) per binary operator; higher binds tighter.
      private static (int prec, bool right) BinOp(string op) => op switch
      {
        "||" => (1, false),
        "&&" => (2, false),
        "|" => (3, false),
        "^" => (4, false),
        "&" => (5, false),
        "==" => (6, false), "!=" => (6, false),
        "<" => (7, false), "<=" => (7, false), ">" => (7, false), ">=" => (7, false),
        "<<" => (8, false), ">>" => (8, false),
        "+" => (9, false), "-" => (9, false),
        "*" => (10, false), "/" => (10, false),
        "**" => (11, true),
        _ => (-1, false)
      };

      public double ParseExpr(int minPrec)
      {
        double left = ParseUnary();
        while (Peek.Kind == Kind.Op)
        {
          var (prec, right) = BinOp(Peek.Text);
          if (prec < minPrec || prec < 0) break;
          string op = Next().Text;
          double rhs = ParseExpr(right ? prec : prec + 1);
          left = Apply(op, left, rhs);
        }
        return left;
      }

      private double ParseUnary()
      {
        if (Peek.Kind == Kind.Op && (Peek.Text == "-" || Peek.Text == "+"))
        {
          string op = Next().Text;
          double v = ParseUnary();
          return op == "-" ? -v : v;
        }
        return ParsePrimary();
      }

      private double ParsePrimary()
      {
        Token t = Next();
        switch (t.Kind)
        {
          case Kind.Num:
            return t.Num;
          case Kind.LParen:
          {
            double v = ParseTernary();
            if (Next().Kind != Kind.RParen) throw new FormatException("expr: missing ')'");
            return v;
          }
          case Kind.Ident:
            if (Peek.Kind == Kind.LParen) return CallFunction(t.Text);
            return lookup(t.Text);
          default:
            throw new FormatException($"expr: unexpected token '{t.Text}'");
        }
      }

      private double CallFunction(string name)
      {
        Next();   // consume '('
        var args = new List<double>();
        if (Peek.Kind != Kind.RParen)
        {
          args.Add(ParseTernary());
          while (Peek.Kind == Kind.Comma) { Next(); args.Add(ParseTernary()); }
        }
        if (Next().Kind != Kind.RParen) throw new FormatException($"expr: missing ')' after {name}(");
        return ApplyFunction(name, args);
      }

      private static double Apply(string op, double a, double b) => op switch
      {
        "+" => a + b,
        "-" => a - b,
        "*" => a * b,
        "/" => a / b,
        "**" => Math.Pow(a, b),
        "==" => a == b ? 1 : 0,
        "!=" => a != b ? 1 : 0,
        "<" => a < b ? 1 : 0,
        "<=" => a <= b ? 1 : 0,
        ">" => a > b ? 1 : 0,
        ">=" => a >= b ? 1 : 0,
        "&&" => (a != 0 && b != 0) ? 1 : 0,
        "||" => (a != 0 || b != 0) ? 1 : 0,
        // bitwise / shift — integer semantics: both operands truncate to long (the HADES _dec forms)
        "&" => (long)a & (long)b,
        "|" => (long)a | (long)b,
        "^" => (long)a ^ (long)b,
        "<<" => (long)a << (int)b,
        ">>" => (long)a >> (int)b,
        _ => throw new FormatException($"expr: unknown operator '{op}'")
      };

      private static double ApplyFunction(string name, List<double> a)
      {
        void Arity(int n) { if (a.Count != n) throw new FormatException($"expr: {name}() expects {n} arg(s)"); }
        switch (name.ToLowerInvariant())
        {
          case "abs": Arity(1); return Math.Abs(a[0]);
          case "round": Arity(1); return Math.Round(a[0], MidpointRounding.AwayFromZero);
          case "floor": Arity(1); return Math.Floor(a[0]);
          case "ceil": Arity(1); return Math.Ceiling(a[0]);
          case "min": Arity(2); return Math.Min(a[0], a[1]);
          case "max": Arity(2); return Math.Max(a[0], a[1]);
          default: throw new FormatException($"expr: unknown function '{name}'");
        }
      }
    }
  }
}
