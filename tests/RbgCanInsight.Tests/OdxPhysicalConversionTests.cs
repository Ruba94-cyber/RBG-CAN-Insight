using RbgCanInsight.DiagData;
using Xunit;

namespace RbgCanInsight.Tests;

public sealed class OdxPhysicalConversionTests
{
    private const string LinearLittleEndianFixture =
        """
        <ODX>
          <DIAG-LAYER-CONTAINER><SHORT-NAME>ECM</SHORT-NAME></DIAG-LAYER-CONTAINER>
          <REQUEST ID="REQ">
            <PARAM SEMANTIC="SERVICE-ID"><CODED-VALUE>34</CODED-VALUE><DIAG-CODED-TYPE><BIT-LENGTH>8</BIT-LENGTH></DIAG-CODED-TYPE></PARAM>
            <PARAM SEMANTIC="ID"><CODED-VALUE>62469</CODED-VALUE><DIAG-CODED-TYPE><BIT-LENGTH>16</BIT-LENGTH></DIAG-CODED-TYPE></PARAM>
          </REQUEST>
          <POS-RESPONSE ID="RESP">
            <PARAM SEMANTIC="DATA"><SHORT-NAME>Measured</SHORT-NAME>
              <BYTE-POSITION>3</BYTE-POSITION><DOP-REF ID-REF="DOP"/></PARAM>
          </POS-RESPONSE>
          <DATA-OBJECT-PROP ID="DOP">
            <SHORT-NAME>MeasuredValue</SHORT-NAME>
            <DIAG-CODED-TYPE BASE-DATA-TYPE="A_UINT16" IS-HIGHLOW-BYTE-ORDER="false">
              <BIT-LENGTH>16</BIT-LENGTH>
            </DIAG-CODED-TYPE>
            <COMPU-METHOD>
              <CATEGORY>LINEAR</CATEGORY>
              <COMPU-INTERNAL-TO-PHYS><COMPU-SCALES><COMPU-SCALE>
                <COMPU-RATIONAL-COEFFS>
                  <COMPU-NUMERATOR><V>10</V><V>2</V></COMPU-NUMERATOR>
                  <COMPU-DENOMINATOR><V>4</V></COMPU-DENOMINATOR>
                </COMPU-RATIONAL-COEFFS>
              </COMPU-SCALE></COMPU-SCALES></COMPU-INTERNAL-TO-PHYS>
            </COMPU-METHOD>
          </DATA-OBJECT-PROP>
          <DIAG-SERVICE>
            <SHORT-NAME>Read_MeasuredValue</SHORT-NAME>
            <REQUEST-REF ID-REF="REQ"/><POS-RESPONSE-REF ID-REF="RESP"/>
          </DIAG-SERVICE>
        </ODX>
        """;

    private static DidParameter LoadDid(string xml)
    {
        var path = Path.Combine(Path.GetTempPath(), $"rbg-odx-conversion-{Guid.NewGuid():N}.odx");
        File.WriteAllText(path, xml);
        try
        {
            return Assert.Single(DidCatalog.Load(path).Parameters);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void OdxExplicitLowHighOrderAndRationalDenominatorArePreservedExactly()
    {
        var did = LoadDid(LinearLittleEndianFixture);
        var field = Assert.Single(did.Fields);
        Assert.Equal("LITTLE_ENDIAN", field.ByteOrder);
        Assert.Equal(16, field.BitLength);
        Assert.Equal(0, field.BitOffset);
        Assert.Equal(0.5, field.Factor);
        Assert.Equal(2.5, field.Offset);
        Assert.True(field.SupportsPhysicalConversion);
        Assert.Equal(0.5, did.Factor);
        Assert.Equal(2.5, did.Offset);
    }

    [Fact]
    public void OdxUnpositionedSingleResponseFieldRemainsRawOnlyAndHasNoInventedOrigin()
    {
        var did = LoadDid(LinearLittleEndianFixture.Replace(
            "<BYTE-POSITION>3</BYTE-POSITION>", "", StringComparison.Ordinal));
        var field = Assert.Single(did.Fields);
        Assert.Equal(-1, field.BitOffset);
        Assert.False(field.SupportsPhysicalConversion);
        Assert.Equal("LITTLE_ENDIAN", field.ByteOrder);
        Assert.Equal(16, field.BitLength); // Declared type length is still useful.
        Assert.Null(did.Factor);
        Assert.Null(did.Offset);
        Assert.Null(did.BitLength);
        Assert.Null(did.Minimum);
        Assert.Null(did.Maximum);
        Assert.Equal("", did.Unit);
        Assert.Contains(did.EngineeringParameters,
            node => node.Name == "Measured" && node.DeclaredBitLength == 16);
    }

    [Fact]
    public void OdxResponseSidDopIsNeverPromotedToWholeDidEngineeringScale()
    {
        var xml = LinearLittleEndianFixture
            .Replace("<POS-RESPONSE ID=\"RESP\">",
                """
                <POS-RESPONSE ID="RESP">
                  <PARAM SEMANTIC="SERVICE-ID"><SHORT-NAME>SID</SHORT-NAME>
                    <BYTE-POSITION>0</BYTE-POSITION>
                    <DOP-REF ID-REF="SID_DOP"/>
                  </PARAM>
                """, StringComparison.Ordinal)
            .Replace("<DATA-OBJECT-PROP ID=\"DOP\">",
                """
                <DATA-OBJECT-PROP ID="SID_DOP">
                  <SHORT-NAME>ProtocolServiceId</SHORT-NAME>
                  <DIAG-CODED-TYPE BASE-DATA-TYPE="A_UINT8">
                    <BIT-LENGTH>8</BIT-LENGTH>
                  </DIAG-CODED-TYPE>
                  <UNIT-REF ID-REF="WRONG_UNIT"/>
                </DATA-OBJECT-PROP>
                <UNIT ID="WRONG_UNIT"><DISPLAY-NAME>header</DISPLAY-NAME></UNIT>
                <DATA-OBJECT-PROP ID="DOP">
                """, StringComparison.Ordinal);

        var did = LoadDid(xml);
        var field = Assert.Single(did.Fields);
        Assert.Equal("Measured", field.Name);
        Assert.Equal(16, field.BitLength);
        Assert.Equal(0, field.BitOffset);
        Assert.Equal(0.5, field.Factor);
        Assert.Equal(0.5, did.Factor);
        Assert.NotEqual("header", did.Unit);
        Assert.Equal(16, did.BitLength);
    }

    [Fact]
    public void OdxDefaultHighLowByteOrderAppliesWhenAttributeIsAbsent()
    {
        var did = LoadDid(LinearLittleEndianFixture.Replace(
            " IS-HIGHLOW-BYTE-ORDER=\"false\"", "", StringComparison.Ordinal));
        Assert.Equal("BIG_ENDIAN", Assert.Single(did.Fields).ByteOrder);
    }

    [Fact]
    public void TruePiecewiseOdxScalingMustRemainRawOnly()
    {
        var extraScale =
            "<COMPU-SCALE><COMPU-RATIONAL-COEFFS>" +
            "<COMPU-NUMERATOR><V>0</V><V>5</V></COMPU-NUMERATOR>" +
            "<COMPU-DENOMINATOR><V>1</V></COMPU-DENOMINATOR>" +
            "</COMPU-RATIONAL-COEFFS></COMPU-SCALE>";
        var xml = LinearLittleEndianFixture.Replace(
            "</COMPU-SCALE>", "</COMPU-SCALE>" + extraScale, StringComparison.Ordinal);
        var did = LoadDid(xml);
        var field = Assert.Single(did.Fields);
        Assert.False(field.SupportsPhysicalConversion);
        Assert.Null(field.Factor);
        Assert.Null(field.Offset);
        Assert.Null(did.Factor);
    }

    [Fact]
    public void OdxMultipleRecordFieldsRetainDistinctByteOffsets()
    {
        const string xml =
            """
            <ODX>
              <DIAG-LAYER-CONTAINER><SHORT-NAME>ECM</SHORT-NAME></DIAG-LAYER-CONTAINER>
              <REQUEST ID="REQ">
                <PARAM SEMANTIC="SERVICE-ID"><CODED-VALUE>34</CODED-VALUE><DIAG-CODED-TYPE><BIT-LENGTH>8</BIT-LENGTH></DIAG-CODED-TYPE></PARAM>
                <PARAM SEMANTIC="ID"><CODED-VALUE>62469</CODED-VALUE><DIAG-CODED-TYPE><BIT-LENGTH>16</BIT-LENGTH></DIAG-CODED-TYPE></PARAM>
              </REQUEST>
              <POS-RESPONSE ID="RESP">
                <PARAM SEMANTIC="DATA"><SHORT-NAME>First</SHORT-NAME><BYTE-POSITION>0</BYTE-POSITION><DOP-REF ID-REF="DOP1"/></PARAM>
                <PARAM SEMANTIC="DATA"><SHORT-NAME>Second</SHORT-NAME><BYTE-POSITION>1</BYTE-POSITION><DOP-REF ID-REF="DOP2"/></PARAM>
              </POS-RESPONSE>
              <DATA-OBJECT-PROP ID="DOP1"><SHORT-NAME>FirstField</SHORT-NAME>
                <DIAG-CODED-TYPE BASE-DATA-TYPE="A_UINT8"><BIT-LENGTH>8</BIT-LENGTH></DIAG-CODED-TYPE>
              </DATA-OBJECT-PROP>
              <DATA-OBJECT-PROP ID="DOP2"><SHORT-NAME>SecondField</SHORT-NAME>
                <DIAG-CODED-TYPE BASE-DATA-TYPE="A_UINT8"><BIT-LENGTH>8</BIT-LENGTH></DIAG-CODED-TYPE>
              </DATA-OBJECT-PROP>
              <DIAG-SERVICE><SHORT-NAME>Read_TwoFields</SHORT-NAME>
                <REQUEST-REF ID-REF="REQ"/><POS-RESPONSE-REF ID-REF="RESP"/>
              </DIAG-SERVICE>
            </ODX>
            """;
        var did = LoadDid(xml);
        Assert.Equal(2, did.Fields.Count);
        Assert.Equal(0, did.Fields[0].BitOffset);
        Assert.Equal(8, did.Fields[1].BitOffset);
        Assert.All(did.Fields, field => Assert.Equal("BIG_ENDIAN", field.ByteOrder));
        // A two-field response has no one trustworthy whole-DID scalar.
        Assert.Null(did.BitLength);
        Assert.Equal("", did.DataType);
        Assert.Null(did.Factor);
    }

    [Fact]
    public void ZeroDenominatorCannotCreatePhysicalValues()
    {
        var did = LoadDid(LinearLittleEndianFixture.Replace(
            "<COMPU-DENOMINATOR><V>4</V></COMPU-DENOMINATOR>",
            "<COMPU-DENOMINATOR><V>0</V></COMPU-DENOMINATOR>",
            StringComparison.Ordinal));
        Assert.False(Assert.Single(did.Fields).SupportsPhysicalConversion);
    }
}
