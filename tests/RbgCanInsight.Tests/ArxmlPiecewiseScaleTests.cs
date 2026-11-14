using System.Xml;
using RbgCanInsight.Dbc;
using RbgCanInsight.Domain;
using Xunit;

namespace RbgCanInsight.Tests;

/// <summary>
/// Deterministic AUTOSAR COMPU-SCALE oracle: every expected value is calculated
/// from the source rational coefficient and bounded raw code, independently of
/// the production DBC decoder. No OEM fixtures are required.
/// </summary>
public sealed class ArxmlPiecewiseScaleTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(25, 25)]
    [InlineData(99, 99)]
    [InlineData(100, 210)]
    [InlineData(150, 310)]
    [InlineData(200, 410)]
    public void TwoBoundedScales_DecodeWithCorrectRawInterval(byte raw, double expected)
    {
        var db = Parse(Scale(0, 99, 0, 1) + Scale(100, 200, 10, 2));
        var signal = Assert.Single(Assert.Single(db.Messages.Values).Signals);
        Assert.True(signal.UsesRangeDependentScale);
        Assert.Equal(expected, Decode(db, raw)?.PhysicalValue);
        Assert.Contains(db.ParseWarnings, w => w.Contains("range-dependent", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(201)]
    [InlineData(250)]
    [InlineData(255)]
    public void UncoveredRawCodes_DoNotBecomeFabricatedPhysicalMeasurements(byte raw)
    {
        var db = Parse(Scale(0, 99, 0, 1) + Scale(100, 200, 10, 2));
        Assert.Null(Decode(db, raw));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(10, true)]
    [InlineData(15, false)]
    [InlineData(25, true)]
    [InlineData(100, false)]
    public void ConflictingOverlap_SuppressesAmbiguousValuesOnly(byte raw, bool supported)
    {
        var db = Parse(Scale(0, 20, 0, 1) + Scale(15, 50, 2, 3));
        Assert.Equal(supported, Decode(db, raw) is not null);
        // One ambiguous code must not poison an unrelated unambiguous code.
        Assert.Equal(77, Decode(db, 25)?.PhysicalValue);
    }

    [Fact]
    public void IdenticalOverlappingScales_RemainUnambiguous()
    {
        var db = Parse(Scale(0, 150, 0, 1) + Scale(100, 200, 0, 1));
        Assert.Equal(125, Decode(db, 125)?.PhysicalValue);
    }

    [Fact]
    public void ExclusiveBoundaries_DoNotIncludeExcludedRawCode()
    {
        var db = Parse("""
            <COMPU-SCALE>
              <LOWER-LIMIT INTERVAL-TYPE="OPEN">10</LOWER-LIMIT>
              <UPPER-LIMIT>20</UPPER-LIMIT>
              <COMPU-RATIONAL-COEFFS>
                <COMPU-NUMERATOR><V>2</V><V>3</V></COMPU-NUMERATOR>
                <COMPU-DENOMINATOR><V>1</V></COMPU-DENOMINATOR>
              </COMPU-RATIONAL-COEFFS>
            </COMPU-SCALE>
            """);
        Assert.Null(Decode(db, 10));
        Assert.Equal(35, Decode(db, 11)?.PhysicalValue);
        Assert.Equal(62, Decode(db, 20)?.PhysicalValue);
        Assert.Null(Decode(db, 21));
    }

    [Fact]
    public void InvalidDenominator_RejectsNumericConversionWithoutUsingUnityFallback()
    {
        var db = Parse(Scale(0, 255, 2, 4, denominator: 0));
        var signal = Assert.Single(Assert.Single(db.Messages.Values).Signals);
        Assert.True(signal.PhysicalConversionUnsupported);
        Assert.Null(Decode(db, 42));
        Assert.Contains(db.ParseWarnings, w => w.Contains("unsupported", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void NonlinearOrPartialMethod_IsRawOnlyEvenIfAnotherScaleIsValid()
    {
        var invalid = """
            <COMPU-SCALE>
              <LOWER-LIMIT>100</LOWER-LIMIT><UPPER-LIMIT>200</UPPER-LIMIT>
              <COMPU-RATIONAL-COEFFS>
                <COMPU-NUMERATOR><V>1</V><V>2</V><V>3</V></COMPU-NUMERATOR>
              </COMPU-RATIONAL-COEFFS>
            </COMPU-SCALE>
            """;
        var db = Parse(Scale(0, 99, 0, 1) + invalid);
        Assert.Null(Decode(db, 50));
        Assert.Null(Decode(db, 120));
    }

    [Fact]
    public void OneUnboundedLinearMethod_RetainsLegacyFastPath()
    {
        var db = Parse("""
            <COMPU-SCALE>
              <COMPU-RATIONAL-COEFFS>
                <COMPU-NUMERATOR><V>-40</V><V>0.5</V></COMPU-NUMERATOR>
                <COMPU-DENOMINATOR><V>1</V></COMPU-DENOMINATOR>
              </COMPU-RATIONAL-COEFFS>
            </COMPU-SCALE>
            """);
        var signal = Assert.Single(Assert.Single(db.Messages.Values).Signals);
        Assert.False(signal.UsesRangeDependentScale);
        Assert.Equal(-20, Decode(db, 40)?.PhysicalValue);
    }

    [Fact]
    public void TextTable_UsesOnlyVerbalLabelsAndDoesNotInventPhysicalScale()
    {
        var db = Parse("""
            <COMPU-SCALE>
              <LOWER-LIMIT>1</LOWER-LIMIT><UPPER-LIMIT>1</UPPER-LIMIT>
              <COMPU-CONST><VT>Running</VT></COMPU-CONST>
            </COMPU-SCALE>
            """);
        var signal = Assert.Single(Assert.Single(db.Messages.Values).Signals);
        Assert.Equal("Running", signal.GetValueDescription(1));
        Assert.Equal(1, Decode(db, 1)?.PhysicalValue);
    }

    [Fact]
    public void Arxml_DtdAndExternalEntityResolutionAreForbidden()
    {
        var path = Path.Combine(Path.GetTempPath(), "rbg-xxe-" + Guid.NewGuid().ToString("N") + ".arxml");
        File.WriteAllText(path, "<!DOCTYPE AUTOSAR [<!ENTITY xxe SYSTEM \"file:///c:/windows/win.ini\">]><AUTOSAR>&xxe;</AUTOSAR>");
        try
        {
            Assert.Throws<XmlException>(() => new ArxmlParser().ParseFile(path));
        }
        finally { File.Delete(path); }
    }

    private static SignalValue? Decode(DbcDatabase database, byte raw)
    {
        var payload = new byte[8];
        payload[0] = raw;
        var sample = new DbcDecoder(database).Decode(new CanFrame(0, 0x123, payload, 0));
        return sample.Count == 1 ? sample[0] : null;
    }

    private static string Scale(int lower, int upper, int offset, int factor, int denominator = 1) =>
        $"""
        <COMPU-SCALE>
          <LOWER-LIMIT>{lower}</LOWER-LIMIT>
          <UPPER-LIMIT>{upper}</UPPER-LIMIT>
          <COMPU-RATIONAL-COEFFS>
            <COMPU-NUMERATOR><V>{offset}</V><V>{factor}</V></COMPU-NUMERATOR>
            <COMPU-DENOMINATOR><V>{denominator}</V></COMPU-DENOMINATOR>
          </COMPU-RATIONAL-COEFFS>
        </COMPU-SCALE>
        """;

    private static DbcDatabase Parse(string scales)
    {
        var path = Path.Combine(Path.GetTempPath(), "rbg-arxml-scale-" + Guid.NewGuid().ToString("N") + ".arxml");
        var xml = $$"""
            <AUTOSAR>
              <COMPU-METHOD>
                <SHORT-NAME>Compu</SHORT-NAME>
                <COMPU-INTERNAL-TO-PHYS><COMPU-SCALES>
                  {{scales}}
                </COMPU-SCALES></COMPU-INTERNAL-TO-PHYS>
              </COMPU-METHOD>
              <I-SIGNAL>
                <SHORT-NAME>Value</SHORT-NAME><LENGTH>8</LENGTH>
                <COMPU-METHOD-REF>/Demo/Compu</COMPU-METHOD-REF>
              </I-SIGNAL>
              <I-SIGNAL-I-PDU>
                <SHORT-NAME>PDU</SHORT-NAME>
                <I-SIGNAL-TO-I-PDU-MAPPING>
                  <START-POSITION>0</START-POSITION>
                  <I-SIGNAL-REF>/Demo/Value</I-SIGNAL-REF>
                </I-SIGNAL-TO-I-PDU-MAPPING>
              </I-SIGNAL-I-PDU>
              <CAN-FRAME>
                <SHORT-NAME>Frame</SHORT-NAME><FRAME-LENGTH>8</FRAME-LENGTH>
                <PDU-REF>/Demo/PDU</PDU-REF>
              </CAN-FRAME>
              <CAN-FRAME-TRIGGERING>
                <SHORT-NAME>Triggered</SHORT-NAME>
                <IDENTIFIER>291</IDENTIFIER><FRAME-REF>/Demo/Frame</FRAME-REF>
              </CAN-FRAME-TRIGGERING>
            </AUTOSAR>
            """;
        File.WriteAllText(path, xml);
        try { return new ArxmlParser().ParseFile(path); }
        finally { File.Delete(path); }
    }
}
