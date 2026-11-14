using System.Xml.Linq;
using RbgCanInsight.DiagData;
using Xunit;

namespace RbgCanInsight.Tests;

public sealed class OdxCompuMethodTests
{
    private static OdxCompuMethod Parse(string category, string body) =>
        OdxCompuMethod.Parse(XElement.Parse(
            $"<COMPU-METHOD><CATEGORY>{category}</CATEGORY><COMPU-INTERNAL-TO-PHYS>{body}</COMPU-INTERNAL-TO-PHYS></COMPU-METHOD>"))!;

    private static string Rat(string num, string den = "") =>
        $"<COMPU-RATIONAL-COEFFS><COMPU-NUMERATOR>{num}</COMPU-NUMERATOR>" +
        (den.Length == 0 ? "" : $"<COMPU-DENOMINATOR>{den}</COMPU-DENOMINATOR>") + "</COMPU-RATIONAL-COEFFS>";

    [Fact]
    public void IdenticalReturnsTheInternalValue()
    {
        var m = OdxCompuMethod.Parse(XElement.Parse("<COMPU-METHOD><CATEGORY>IDENTICAL</CATEGORY></COMPU-METHOD>"))!;
        Assert.Equal(42d, m.Convert(42)!.Value.Number);
    }

    [Fact]
    public void LinearUsesOffsetThenFactorCoefficients()
    {
        var m = Parse("LINEAR", $"<COMPU-SCALE>{Rat("<V>-40</V><V>0.5</V>")}</COMPU-SCALE>");
        Assert.Equal(-40d + 0.5 * 100, m.Convert(100)!.Value.Number);
    }

    [Fact]
    public void MissingCategoryWithAffineCoefficientsStillConvertsAsBefore()
    {
        var m = OdxCompuMethod.Parse(XElement.Parse(
            "<COMPU-METHOD><COMPU-INTERNAL-TO-PHYS><COMPU-SCALES><COMPU-SCALE>" +
            Rat("<V>-40</V><V>1</V>") + "<LOWER-LIMIT>-40</LOWER-LIMIT><UPPER-LIMIT>215</UPPER-LIMIT>" +
            "</COMPU-SCALE></COMPU-SCALES></COMPU-INTERNAL-TO-PHYS></COMPU-METHOD>"))!;
        Assert.Equal(string.Empty, m.RawCategory);
        Assert.Equal(10d, m.Convert(50)!.Value.Number);
    }

    [Fact]
    public void MissingCategoryWithoutAffineCoefficientsStaysUnsupported()
    {
        var m = OdxCompuMethod.Parse(XElement.Parse(
            "<COMPU-METHOD><COMPU-INTERNAL-TO-PHYS><COMPU-SCALES><COMPU-SCALE>" +
            Rat("<V>0</V><V>1</V><V>2</V>") + "</COMPU-SCALE></COMPU-SCALES></COMPU-INTERNAL-TO-PHYS></COMPU-METHOD>"))!;
        Assert.Equal(OdxCompuCategory.Unsupported, m.Category);
        Assert.Null(m.Convert(3));
    }

    [Fact]
    public void LinearWithDenominatorDividesTheWholeNumerator()
    {
        var m = Parse("LINEAR", $"<COMPU-SCALE>{Rat("<V>10</V><V>1</V>", "<V>4</V>")}</COMPU-SCALE>");
        Assert.Equal((10d + 6) / 4, m.Convert(6)!.Value.Number);
    }

    [Fact]
    public void ScaleLinearSelectsTheScaleByLimitsAndHonoursOpenIntervals()
    {
        var m = Parse("SCALE-LINEAR",
            $"<COMPU-SCALE><LOWER-LIMIT>0</LOWER-LIMIT><UPPER-LIMIT INTERVAL-TYPE=\"OPEN\">100</UPPER-LIMIT>{Rat("<V>0</V><V>1</V>")}</COMPU-SCALE>" +
            $"<COMPU-SCALE><LOWER-LIMIT>100</LOWER-LIMIT><UPPER-LIMIT>200</UPPER-LIMIT>{Rat("<V>-100</V><V>2</V>")}</COMPU-SCALE>");
        Assert.Equal(50d, m.Convert(50)!.Value.Number);
        Assert.Equal(100d, m.Convert(100)!.Value.Number); // 100 belongs to the second scale: -100 + 2*100
        Assert.Equal(300d, m.Convert(200)!.Value.Number);
        var outside = m.Convert(201)!.Value;
        Assert.False(outside.InRange);
        Assert.Null(outside.Number);
    }

    [Fact]
    public void RationalFunctionEvaluatesNumeratorOverDenominatorPolynomials()
    {
        // (1 + 2x) / (1 + x) at x = 3  ->  7 / 4
        var m = Parse("RAT-FUNC", $"<COMPU-SCALE>{Rat("<V>1</V><V>2</V>", "<V>1</V><V>1</V>")}</COMPU-SCALE>");
        Assert.Equal(7d / 4, m.Convert(3)!.Value.Number);
        // Quadratic numerator: x^2 at x = 5
        var q = Parse("RAT-FUNC", $"<COMPU-SCALE>{Rat("<V>0</V><V>0</V><V>1</V>")}</COMPU-SCALE>");
        Assert.Equal(25d, q.Convert(5)!.Value.Number);
    }

    [Fact]
    public void ZeroDenominatorIsRefusedNotInfinity()
    {
        var m = Parse("RAT-FUNC", $"<COMPU-SCALE>{Rat("<V>1</V>", "<V>0</V><V>1</V>")}</COMPU-SCALE>");
        Assert.Null(m.Convert(0));
        Assert.Equal(1d, m.Convert(1)!.Value.Number);
    }

    [Fact]
    public void TextTableMapsCodesRangesAndDefault()
    {
        var m = Parse("TEXTTABLE",
            "<COMPU-SCALE><LOWER-LIMIT>0</LOWER-LIMIT><UPPER-LIMIT>0</UPPER-LIMIT><COMPU-CONST><VT>Off</VT></COMPU-CONST></COMPU-SCALE>" +
            "<COMPU-SCALE><LOWER-LIMIT>1</LOWER-LIMIT><UPPER-LIMIT>0x0F</UPPER-LIMIT><COMPU-CONST><VT>On</VT></COMPU-CONST></COMPU-SCALE>" +
            "<COMPU-DEFAULT-VALUE><VT>Reserved</VT></COMPU-DEFAULT-VALUE>");
        Assert.Equal("Off", m.Convert(0)!.Value.Text);
        Assert.Equal("On", m.Convert(9)!.Value.Text);
        var dflt = m.Convert(200)!.Value;
        Assert.Equal("Reserved", dflt.Text);
        Assert.False(dflt.InRange);
    }

    [Fact]
    public void UnsupportedCategoryAndMalformedCoefficientsReturnNull()
    {
        var tab = Parse("COMPUCODE", "<COMPU-SCALE/>");
        Assert.Equal(OdxCompuCategory.Unsupported, tab.Category);
        Assert.Null(tab.Convert(1));
        // A TAB-INTP table without points is malformed, not a number.
        Assert.Null(Parse("TAB-INTP", "<COMPU-SCALE/>").Convert(1));
        var bad = Parse("LINEAR", $"<COMPU-SCALE>{Rat("<V>abc</V><V>1</V>")}</COMPU-SCALE>");
        Assert.Null(bad.Convert(1));
    }

    [Fact]
    public void ElementWithoutCompuMethodYieldsNull() =>
        Assert.Null(OdxCompuMethod.Parse(XElement.Parse("<DATA-OBJECT-PROP/>")));
}
