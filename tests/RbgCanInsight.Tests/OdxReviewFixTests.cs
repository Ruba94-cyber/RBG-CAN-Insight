using System.Xml.Linq;
using RbgCanInsight.DiagData;
using Xunit;

namespace RbgCanInsight.Tests;

/// <summary>Regression tests for the 5.0.3 independent review findings on the ODX COMPU evaluator.</summary>
public sealed class OdxReviewFixTests
{
    private static OdxFieldCoding Coding(string compuXml, string extra = "") =>
        OdxFieldCoding.Create(
            XElement.Parse($"<DATA-OBJECT-PROP ID=\"D\">{compuXml}{extra}<DIAG-CODED-TYPE BASE-DATA-TYPE=\"A_UINT32\"><BIT-LENGTH>16</BIT-LENGTH></DIAG-CODED-TYPE></DATA-OBJECT-PROP>"),
            new Dictionary<string, XElement>(), "Value");

    private const string Identical =
        "<COMPU-METHOD><CATEGORY>IDENTICAL</CATEGORY></COMPU-METHOD>";

    private static string Text(string category, string scales) =>
        $"<COMPU-METHOD><CATEGORY>{category}</CATEGORY><COMPU-INTERNAL-TO-PHYS><COMPU-SCALES>{scales}</COMPU-SCALES></COMPU-INTERNAL-TO-PHYS></COMPU-METHOD>";

    private static string Single(string lower, string text) =>
        $"<COMPU-SCALE><LOWER-LIMIT>{lower}</LOWER-LIMIT><COMPU-CONST><VT>{text}</VT></COMPU-CONST></COMPU-SCALE>";

    [Fact]
    public void TextTableScaleWithoutUpperLimitIsASinglePoint()
    {
        var c = Coding(Text("TEXTTABLE", Single("1", "On") + Single("2", "Error")));
        Assert.Equal("On", c.Evaluate(1, "0x1").Physical);
        Assert.Equal("Error", c.Evaluate(2, "0x2").Physical);
        var miss = c.Evaluate(5, "0x5");
        Assert.Equal(OdxConversionStatus.OutOfRange, miss.Status);
        Assert.Null(miss.Numeric);
        Assert.DoesNotContain("On", miss.Physical);
        Assert.DoesNotContain("Error", miss.Physical);
        Assert.Contains("unmapped raw value", miss.Physical);
    }

    [Fact]
    public void InternalConstrUnparsableUpperLimitIsMalformedNotUnbounded()
    {
        var c = Coding(Identical, "<INTERNAL-CONSTR><LOWER-LIMIT>0</LOWER-LIMIT><UPPER-LIMIT>1O0</UPPER-LIMIT></INTERNAL-CONSTR>");
        Assert.False(c.IsEvaluable);
        var r = c.Evaluate(5000, "0x1388");
        Assert.Equal(OdxConversionStatus.Malformed, r.Status);
        Assert.Null(r.Numeric);
    }

    [Fact]
    public void InternalConstrUnparsableLowerLimitIsMalformed()
    {
        var c = Coding(Identical, "<INTERNAL-CONSTR><LOWER-LIMIT>x</LOWER-LIMIT></INTERNAL-CONSTR>");
        Assert.Equal(OdxConversionStatus.Malformed, c.Evaluate(1, "0x1").Status);
    }

    [Fact]
    public void ScaleConstrUnparsableLimitIsMalformedNotUnbounded()
    {
        var c = Coding(Identical,
            "<INTERNAL-CONSTR><SCALE-CONSTRS><SCALE-CONSTR VALIDITY=\"NOT-VALID\"><LOWER-LIMIT>10</LOWER-LIMIT><UPPER-LIMIT>2O</UPPER-LIMIT></SCALE-CONSTR></SCALE-CONSTRS></INTERNAL-CONSTR>");
        var r = c.Evaluate(5, "0x5");
        Assert.Equal(OdxConversionStatus.Malformed, r.Status);
        Assert.Null(r.Numeric);
    }

    [Fact]
    public void AbsentConstraintBoundStillMeansUnbounded()
    {
        var c = Coding(Identical, "<INTERNAL-CONSTR><LOWER-LIMIT>10</LOWER-LIMIT></INTERNAL-CONSTR>");
        Assert.Equal(OdxConversionStatus.Converted, c.Evaluate(5000, "0x1388").Status);
        Assert.Equal(OdxConversionStatus.OutOfRange, c.Evaluate(5, "0x5").Status);
    }

    [Fact]
    public void MissingCompuMethodStaysFlaggedNoDefinitionAndKeepsTheCodedNumber()
    {
        // Deliberately NOT nulled: OdxDtcEnvironmentDataTests relies on this Numeric for counter-style DOPs
        // (e.g. DTC occurrence counters without a COMPU-METHOD). The status/meaning flag the value as uncertain.
        var r = Coding("").Evaluate(7, "0x7");
        Assert.Equal(OdxConversionStatus.NoDefinition, r.Status);
        Assert.Equal("7", r.Physical);
        Assert.Equal(7d, r.Numeric);
        Assert.Contains("uncertain", r.Meaning);
    }

    [Fact]
    public void SixteenDigitHexIsNeverWrappedToNegative()
    {
        // 0xFFFFFFFFFFFFFFFF used to wrap to -1 and silently turn the upper bound into "x <= -1".
        var wide = Coding(Identical, "<INTERNAL-CONSTR><UPPER-LIMIT>0xFFFFFFFFFFFFFFFF</UPPER-LIMIT></INTERNAL-CONSTR>");
        Assert.Equal(OdxConversionStatus.Malformed, wide.Evaluate(5, "0x5").Status);
        // A representable hex bound still works.
        var ok = Coding(Identical, "<INTERNAL-CONSTR><UPPER-LIMIT>0xFF</UPPER-LIMIT></INTERNAL-CONSTR>");
        Assert.Equal(OdxConversionStatus.Converted, ok.Evaluate(255, "0xFF").Status);
        Assert.Equal(OdxConversionStatus.OutOfRange, ok.Evaluate(256, "0x100").Status);
    }

    [Fact]
    public void UnrepresentableHexLimitMakesTheMethodMalformed()
    {
        var m = OdxCompuMethod.Parse(XElement.Parse(Text("LINEAR",
            "<COMPU-SCALE><UPPER-LIMIT>0xFFFFFFFFFFFFFFFF</UPPER-LIMIT><COMPU-RATIONAL-COEFFS><COMPU-NUMERATOR><V>0</V><V>1</V></COMPU-NUMERATOR></COMPU-RATIONAL-COEFFS></COMPU-SCALE>")))!;
        Assert.True(m.Malformed);
        Assert.Null(m.Convert(5));
    }
}
