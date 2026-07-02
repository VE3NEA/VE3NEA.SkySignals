using System;
using System.Collections.Generic;
using FluentAssertions;
using VE3NEA.SkyTlm.Telemetry;
using Xunit;

namespace VE3NEA.SkyTlm.Tests.Unit
{
  /// <summary>
  /// Unit tests for the schema-v2 <see cref="ExprEvaluator"/> — the constrained, safe expression engine
  /// behind <c>expr</c> calibration and <c>if</c> conditions. Covers precedence, right-associative
  /// <c>**</c>, unary minus, comparisons/logicals, functions, identifier lookup, and the polynomial
  /// calibration form this is meant to parse.
  /// </summary>
  public class ExprEvaluatorTests
  {
    private static double E(string expr) => ExprEvaluator.Eval(expr, _ =>
      throw new InvalidOperationException("no identifiers expected"));

    private static double E(string expr, Func<string, double> lookup) => ExprEvaluator.Eval(expr, lookup);

    [Theory]
    [InlineData("1 + 2 * 3", 7)]
    [InlineData("(1 + 2) * 3", 9)]
    [InlineData("10 - 2 - 3", 5)]          // left-associative
    [InlineData("12 / 4 / 3", 1)]
    [InlineData("2 ** 3 ** 2", 512)]       // right-associative: 2**(3**2)
    [InlineData("0 - 2 ** 2", -4)]         // ** binds tighter than binary minus
    [InlineData("1.5e1 + 0.5", 15.5)]      // scientific literal
    public void Arithmetic_PrecedenceAndAssociativity(string expr, double expected)
    {
      E(expr).Should().BeApproximately(expected, 1e-9);
    }

    [Theory]
    [InlineData("3 > 2", 1)]
    [InlineData("2 >= 3", 0)]
    [InlineData("2 == 2", 1)]
    [InlineData("2 != 2", 0)]
    [InlineData("1 && 0", 0)]
    [InlineData("1 || 0", 1)]
    [InlineData("3 > 2 && 1 < 5", 1)]
    public void ComparisonsAndLogicals_YieldOneOrZero(string expr, double expected)
    {
      E(expr).Should().Be(expected);
    }

    [Theory]
    [InlineData("abs(-5)", 5)]
    [InlineData("min(3, 7)", 3)]
    [InlineData("max(3, 7)", 7)]
    [InlineData("round(2.5)", 3)]          // away-from-zero
    [InlineData("floor(2.9)", 2)]
    [InlineData("ceil(2.1)", 3)]
    public void Functions(string expr, double expected)
    {
      E(expr).Should().Be(expected);
    }

    [Fact]
    public void Identifier_X_IsResolvedByLookup()
    {
      E("x * 0.5 - 40", id => id == "x" ? 100 : throw new KeyNotFoundException(id))
        .Should().Be(10);
    }

    [Fact]
    public void EvalAdapterForm_ImportsAsLinear()
    {
      // SatsDecoder VHF1TM = '-128*x**0 + 1*x**1'  ==  x - 128
      E("-128*x**0 + 1*x**1", id => 200).Should().Be(72);
    }

    [Theory]
    [InlineData("1 ? 10 : 20", 10)]
    [InlineData("0 ? 10 : 20", 20)]
    [InlineData("3 > 2 ? 1 : -1", 1)]
    [InlineData("2 > 3 ? 1 : -1", -1)]
    [InlineData("1 ? 2 ? 30 : 40 : 50", 30)]   // right-associative nesting
    public void Ternary_SelectsBranchByCondition(string expr, double expected)
    {
      E(expr).Should().Be(expected);
    }

    [Fact]
    public void Ternary_ImportsKaitaiInstanceForm()
    {
      // RANDEV eps bat_current_ampere: '(raw[31] < 512) ? -raw[30]*0.014662757 : raw[30]*0.014662757'
      var below = new Dictionary<string, double> { ["raw31"] = 100, ["raw30"] = 1000 };
      var above = new Dictionary<string, double> { ["raw31"] = 600, ["raw30"] = 1000 };
      const string expr = "(raw31 < 512) ? -raw30*0.014662757 : raw30*0.014662757";
      E(expr, id => below[id]).Should().BeApproximately(-14.662757, 1e-6);
      E(expr, id => above[id]).Should().BeApproximately(14.662757, 1e-6);
    }

    [Fact]
    public void Ternary_MissingColon_Throws()
    {
      Action act = () => E("1 ? 2", _ => 0);
      act.Should().Throw<FormatException>();
    }

    [Theory]
    [InlineData("12 & 10", 8)]
    [InlineData("12 | 3", 15)]
    [InlineData("12 ^ 10", 6)]
    [InlineData("1 << 8", 256)]
    [InlineData("0x0f00 >> 8", 15)]
    [InlineData("0xFF", 255)]
    [InlineData("0x0ff0 & 0x00f0", 0x00f0)]
    public void BitwiseAndShift_IntegerSemantics(string expr, double expected)
    {
      E(expr).Should().Be(expected);
    }

    [Theory]
    [InlineData("1 << 4 & 0x0ff0", 16)]        // shift binds tighter than &  -> (1<<4) & 0x0ff0
    [InlineData("0x0f & 0x0c | 0x01", 0x0d)]    // & tighter than |  -> (0x0f & 0x0c) | 0x01
    [InlineData("256 >> 4 + 4", 1)]             // +  tighter than >>  -> 256 >> (4+4)
    [InlineData("1 & 0 == 0", 1)]               // & tighter than ==  -> (1 & 0) == 0  -> 0 == 0 -> 1
    public void BitwiseShift_PrecedenceMatchesKaitai(string expr, double expected)
    {
      E(expr).Should().Be(expected);
    }

    [Fact]
    public void HadesNibblePack_Vbat1_DecodesFromPackedBytes()
    {
      // satnogs-decoders hadesd.ksy  vbat1_dec:
      //   ((vbatadc_lo_vcpuadc_hi << 8) & 0x0f00 | (vbatadc_lo_vcpuadc_hi >> 8) & 0x0fff) * 1400 / 1000
      // pick a packed 16-bit value and compute the expected by hand.
      const int packed = 0xA53C;
      double expected = (((packed << 8) & 0x0f00) | ((packed >> 8) & 0x0fff)) * 1400.0 / 1000.0;
      E("((p << 8) & 0x0f00 | (p >> 8) & 0x0fff) * 1400 / 1000", id => packed)
        .Should().BeApproximately(expected, 1e-9);
    }

    [Fact]
    public void HadesNibblePack_Vcpu_ReciprocalForm()
    {
      // hadesd.ksy  vcpu_dec: 1210*4096/((vbatadc_lo_vcpuadc_hi << 4) & 0x0ff0 | vcpuadc_lo_vbus2 >> 12)
      var vars = new Dictionary<string, double> { ["hi"] = 0x12, ["lo"] = 0x3456 };
      double divisor = (((0x12 << 4) & 0x0ff0) | (0x3456 >> 12));
      E("1210*4096/((hi << 4) & 0x0ff0 | lo >> 12)", id => vars[id])
        .Should().BeApproximately(1210.0 * 4096.0 / divisor, 1e-6);
    }

    [Fact]
    public void HadesIbat_SignedViaTernaryAndBitwise()
    {
      // hadesd.ksy  ibat_dec: (m) > 2047 ? (m) - 4096 : (m)   where m is a 12-bit nibble-packed value
      var vars = new Dictionary<string, double> { ["hi"] = 0xF0, ["lo"] = 0x8000 };
      const string expr =
        "((hi << 8) & 0x0f00 | (lo >> 8) & 0x0fff) > 2047 ? " +
        "((hi << 8) & 0x0f00 | (lo >> 8) & 0x0fff) - 4096 : " +
        "((hi << 8) & 0x0f00 | (lo >> 8) & 0x0fff)";
      int m = ((0xF0 << 8) & 0x0f00) | ((0x8000 >> 8) & 0x0fff);
      double expected = m > 2047 ? m - 4096 : m;
      E(expr, id => vars[id]).Should().Be(expected);
    }

    [Fact]
    public void CrossFieldReferences_ResolveByName()
    {
      var vars = new Dictionary<string, double> { ["hi"] = 1, ["lo"] = 2 };
      E("hi * 256 + lo", id => vars[id]).Should().Be(258);
    }

    [Fact]
    public void LinearScale_MatchesSatsDecoderConstant()
    {
      E("0.020070588 * x", id => 255).Should().BeApproximately(5.118, 1e-3);
    }

    [Theory]
    [InlineData("1 + ")]        // dangling operator
    [InlineData("(1 + 2")]      // missing paren
    [InlineData("2 @ 3")]       // bad character
    [InlineData("nope(1)")]     // unknown function
    public void MalformedExpressions_Throw(string expr)
    {
      Action act = () => E(expr, _ => 0);
      act.Should().Throw<FormatException>();
    }
  }
}
