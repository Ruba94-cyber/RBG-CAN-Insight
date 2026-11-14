using RbgCanInsight.DiagData;
using Xunit;

namespace RbgCanInsight.Tests;

/// <summary>Regression gates for unverified OEM CANdela layouts. A diagnostic
/// descriptor is not proof of payload offsets or a whole-DID conversion.</summary>
public sealed class CddConservativeMetadataTests
{
    [Fact]
    public void SingleResponseFieldKeepsScalingOnFieldOnly()
    {
        var catalog = LoadCdd("""
            <CANDELA><ECU qual="Demo"><DIAGCLASS>
              <SERVICE>
                <SHORTCUTQUAL>BatteryVoltage_Read</SHORTCUTQUAL>
                <SEMANTIC>CURRENTDATA</SEMANTIC>
                <RESPONSE>
                  <DATAFIELD qual="Voltage">
                    <BITOFFSET>24</BITOFFSET><BITLENGTH>16</BITLENGTH>
                    <DATATYPE>A_UINT32</DATATYPE><UNIT>V</UNIT>
                    <FACTOR>0.01</FACTOR><OFFSET>-2</OFFSET>
                    <MINVALUE>-2</MINVALUE><MAXVALUE>300</MAXVALUE>
                  </DATAFIELD>
                </RESPONSE>
              </SERVICE>
              <STATICVALUE v="0xF191"/>
            </DIAGCLASS></ECU></CANDELA>
            """);

        var did = Assert.Single(catalog.Parameters);
        Assert.Equal(0xF191, did.Did);
        Assert.Null(did.BitLength);
        Assert.Equal("", did.Unit);
        Assert.Null(did.Factor);
        Assert.Null(did.Offset);
        Assert.Null(did.Minimum);
        Assert.Null(did.Maximum);

        var field = Assert.Single(did.Fields);
        Assert.Equal("Voltage", field.Name);
        Assert.Equal(16, field.BitLength);
        Assert.Equal(-1, field.BitOffset);
        Assert.Equal("V", field.Unit);
        Assert.Equal(0.01, field.Factor);
        Assert.Equal(-2, field.Offset);
        Assert.False(field.SupportsPhysicalConversion);
        Assert.Contains("position 24", field.Description);
    }

    [Fact]
    public void UnsignedDoesNotAccidentallyMatchSignedAndSignedIntRemainsSigned()
    {
        var catalog = LoadCdd("""
            <CANDELA><ECU qual="Demo"><DIAGCLASS>
              <SERVICE>
                <SHORTCUTQUAL>Status_Read</SHORTCUTQUAL>
                <SEMANTIC>CURRENTDATA</SEMANTIC>
                <RESPONSE>
                  <DATAFIELD qual="UnsignedStatus">
                    <BITLENGTH>8</BITLENGTH><DATATYPE>UNSIGNED</DATATYPE>
                  </DATAFIELD>
                  <DATAFIELD qual="SignedTemperature">
                    <BITLENGTH>16</BITLENGTH><DATATYPE>A_INT32</DATATYPE>
                  </DATAFIELD>
                  <DATAFIELD qual="Unspecified">
                    <BITLENGTH>8</BITLENGTH><DATATYPE>OEM_RAW</DATATYPE>
                  </DATAFIELD>
                </RESPONSE>
              </SERVICE>
              <STATICVALUE v="61840"/>
            </DIAGCLASS></ECU></CANDELA>
            """);

        var fields = Assert.Single(catalog.Parameters).Fields;
        Assert.Equal(3, fields.Count);
        Assert.False(fields.Single(field => field.Name == "UnsignedStatus").IsSigned);
        Assert.True(fields.Single(field => field.Name == "SignedTemperature").IsSigned);
        Assert.False(fields.Single(field => field.Name == "Unspecified").IsSigned);
        Assert.All(fields, field =>
        {
            Assert.Equal(-1, field.BitOffset);
            Assert.False(field.SupportsPhysicalConversion);
        });
    }

    [Fact]
    public void DirectServiceMetadataCanBePreservedWithoutInventingFieldOffsets()
    {
        var catalog = LoadCdd("""
            <CANDELA><ECU qual="Demo"><DIAGCLASS>
              <SERVICE>
                <SHORTCUTQUAL>Pressure_Read</SHORTCUTQUAL>
                <SEMANTIC>CURRENTDATA</SEMANTIC>
                <UNIT>bar</UNIT><BITLENGTH>16</BITLENGTH>
                <FACTOR>0.1</FACTOR><OFFSET>0</OFFSET>
              </SERVICE>
              <STATICVALUE v="0xF192"/>
            </DIAGCLASS></ECU></CANDELA>
            """);

        var did = Assert.Single(catalog.Parameters);
        Assert.Equal(16, did.BitLength);
        Assert.Equal("bar", did.Unit);
        Assert.Equal(0.1, did.Factor);
        Assert.Equal(0, did.Offset);
        Assert.Empty(did.Fields);
    }

    private static DidCatalog LoadCdd(string xml)
    {
        var path = Path.Combine(Path.GetTempPath(), "rbg-cdd-metadata-" +
            Guid.NewGuid().ToString("N") + ".cdd");
        File.WriteAllText(path, xml);
        try { return DidCatalog.Load(path); }
        finally { File.Delete(path); }
    }
}
