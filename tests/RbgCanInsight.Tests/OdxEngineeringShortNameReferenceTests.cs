using System.Xml.Linq;
using RbgCanInsight.DiagData;
using Xunit;

namespace RbgCanInsight.Tests;

public sealed class OdxEngineeringShortNameReferenceTests
{
    [Fact]
    public void GloballyUniqueDopShortNameReferenceExpandsNestedEngineeringParameters()
    {
        var catalog = OdxCatalogReader.LoadFromDocuments(
            [XDocument.Parse(SourceWithStructures(ambiguous: false))], "unique.odx");
        var did = Assert.Single(Assert.Single(catalog).Parameters);
        var root = Assert.Single(did.EngineeringParameters.Where(node => node.Name == "EngineeringRoot"));
        var leaf = Assert.Single(did.EngineeringParameters.Where(node => node.Name == "SubParameter"));
        Assert.Equal(root.Path, leaf.ParentPath);
        Assert.Equal("", root.ReferenceStatus);
        Assert.Equal("STRUCTURE", root.DataType);
        Assert.Equal(0xF190, did.Did);
    }

    [Fact]
    public void ScalarOdxDopMetadataIsRetainedWithoutFabricatingAPayloadPosition()
    {
        var source = SourceWithStructures(ambiguous: false).Replace(
            "<DOP-SNREF SHORT-NAME=\"SharedType\"/>",
            "<DOP-SNREF SHORT-NAME=\"MeasuredType\"/>",
            StringComparison.Ordinal).Replace(
            "<DIAG-SERVICE><SHORT-NAME>Read_Engineering</SHORT-NAME>",
            """
            <DATA-OBJECT-PROP ID="DOP_SCALAR">
              <SHORT-NAME>MeasuredType</SHORT-NAME>
              <DIAG-CODED-TYPE BASE-DATA-TYPE="A_UINT16">
                <BIT-LENGTH>16</BIT-LENGTH>
              </DIAG-CODED-TYPE>
              <UNIT-REF ID-REF="UNIT_KMH"/>
            </DATA-OBJECT-PROP>
            <UNIT ID="UNIT_KMH"><DISPLAY-NAME>km/h</DISPLAY-NAME></UNIT>
            <DIAG-SERVICE><SHORT-NAME>Read_Engineering</SHORT-NAME>
            """, StringComparison.Ordinal);

        var did = Assert.Single(Assert.Single(OdxCatalogReader.LoadFromDocuments(
            [XDocument.Parse(source)], "scalar.odx")).Parameters);
        var root = Assert.Single(did.EngineeringParameters);
        Assert.Equal(16, root.DeclaredBitLength);
        Assert.Equal("A_UINT16", root.DataType);
        Assert.Equal("km/h", root.Unit);
        var rawField = Assert.Single(did.Fields);
        Assert.Equal(-1, rawField.BitOffset);
        Assert.False(rawField.SupportsPhysicalConversion);
        Assert.Null(did.BitLength); // No verified position in response.
    }

    [Fact]
    public void UniqueNamedScalarCanDecodeOnlyWhenResponseRecordPositionIsExplicit()
    {
        var xml = SourceWithStructures(ambiguous: false)
            .Replace("<DOP-SNREF SHORT-NAME=\"SharedType\"/>",
                "<BYTE-POSITION>3</BYTE-POSITION>" +
                "<DOP-SNREF SHORT-NAME=\"MeasuredType\"/>",
                StringComparison.Ordinal)
            .Replace("<DIAG-SERVICE><SHORT-NAME>Read_Engineering</SHORT-NAME>",
                """
                <DATA-OBJECT-PROP ID="MEASURED">
                  <SHORT-NAME>MeasuredType</SHORT-NAME>
                  <DIAG-CODED-TYPE BASE-DATA-TYPE="A_UINT16">
                    <BIT-LENGTH>16</BIT-LENGTH>
                  </DIAG-CODED-TYPE>
                  <UNIT-REF ID-REF="KMH"/>
                </DATA-OBJECT-PROP>
                <UNIT ID="KMH"><DISPLAY-NAME>km/h</DISPLAY-NAME></UNIT>
                <DIAG-SERVICE><SHORT-NAME>Read_Engineering</SHORT-NAME>
                """, StringComparison.Ordinal);

        var did = Assert.Single(Assert.Single(OdxCatalogReader.LoadFromDocuments(
            [XDocument.Parse(xml)], "positioned.odx")).Parameters);
        var field = Assert.Single(did.Fields);
        Assert.Equal(0, field.BitOffset);
        Assert.Equal(16, field.BitLength);
        Assert.True(field.SupportsPhysicalConversion);
        Assert.Equal("A_UINT16", field.DataType);
        Assert.Equal("km/h", field.Unit);
        Assert.Equal(16, did.BitLength);
        Assert.Equal("km/h", did.Unit);
        Assert.Equal("A_UINT16", did.DataType);
    }

    [Fact]
    public void ReusedShortNameIsFlaggedAndNeverGuessedAcrossPossibleVariants()
    {
        var catalog = OdxCatalogReader.LoadFromDocuments(
            [XDocument.Parse(SourceWithStructures(ambiguous: true))], "ambiguous.odx");
        var did = Assert.Single(Assert.Single(catalog).Parameters);
        var root = Assert.Single(did.EngineeringParameters);
        Assert.Equal("EngineeringRoot", root.Name);
        Assert.Equal("Ambiguous short-name reference (multiple type matches)",
            root.ReferenceStatus);
        Assert.DoesNotContain(did.EngineeringParameters,
            node => node.Name == "SubParameter");
    }

    [Fact]
    public void MixedIdRefAndShortNameCyclesAreDetectedByTargetIdentity()
    {
        var xml = SourceWithStructures(ambiguous: false).Replace(
            "<PARAM><SHORT-NAME>SubParameter</SHORT-NAME></PARAM>",
            """
            <PARAM><SHORT-NAME>SubParameter</SHORT-NAME>
              <DOP-REF ID-REF="STRUCT_A"/>
            </PARAM>
            """, StringComparison.Ordinal);
        var did = Assert.Single(Assert.Single(OdxCatalogReader.LoadFromDocuments(
            [XDocument.Parse(xml)], "cycle.odx")).Parameters);
        Assert.Equal(2, did.EngineeringParameters.Count);
        Assert.Equal("Cyclic reference, expansion stopped",
            did.EngineeringParameters[1].ReferenceStatus);
    }

    private static string SourceWithStructures(bool ambiguous) => ("""
        <ODX>
          <REQUEST ID="REQ">
            <PARAM SEMANTIC="SERVICE-ID">
              <CODED-VALUE>34</CODED-VALUE>
              <DIAG-CODED-TYPE><BIT-LENGTH>8</BIT-LENGTH></DIAG-CODED-TYPE>
            </PARAM>
            <PARAM SEMANTIC="ID">
              <CODED-VALUE>61840</CODED-VALUE>
              <DIAG-CODED-TYPE><BIT-LENGTH>16</BIT-LENGTH></DIAG-CODED-TYPE>
            </PARAM>
          </REQUEST>
          <POS-RESPONSE ID="RESPONSE">
            <PARAM SEMANTIC="DATA"><SHORT-NAME>EngineeringRoot</SHORT-NAME>
              <DOP-SNREF SHORT-NAME="SharedType"/>
            </PARAM>
          </POS-RESPONSE>
          <STRUCTURE ID="STRUCT_A">
            <SHORT-NAME>SharedType</SHORT-NAME>
            <PARAMS>
              <PARAM><SHORT-NAME>SubParameter</SHORT-NAME></PARAM>
            </PARAMS>
          </STRUCTURE>
          <!--SECOND_STRUCTURE-->
          <DIAG-SERVICE><SHORT-NAME>Read_Engineering</SHORT-NAME>
            <REQUEST-REF ID-REF="REQ"/>
            <POS-RESPONSE-REF ID-REF="RESPONSE"/>
          </DIAG-SERVICE>
        </ODX>
        """).Replace("<!--SECOND_STRUCTURE-->", ambiguous
            ? "<STRUCTURE ID=\"STRUCT_B\"><SHORT-NAME>SharedType</SHORT-NAME>" +
              "<PARAMS><PARAM><SHORT-NAME>DifferentVariantParameter</SHORT-NAME>" +
              "</PARAM></PARAMS></STRUCTURE>"
            : string.Empty, StringComparison.Ordinal);
}
