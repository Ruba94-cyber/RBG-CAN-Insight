using System.Xml.Linq;
using RbgCanInsight.DiagData;
using RbgCanInsight.TraceIO;
using Xunit;

namespace RbgCanInsight.Tests;

public sealed class OdxDynamicEngineeringStructureTests
{
    [Fact]
    public void DynamicLengthFieldIncludesTemplateSubparametersButNotInventedRepeatCounts()
    {
        const string xml = """
            <ODX>
              <REQUEST ID="REQ"><PARAM SEMANTIC="SERVICE-ID"><CODED-VALUE>34</CODED-VALUE>
                <DIAG-CODED-TYPE><BIT-LENGTH>8</BIT-LENGTH></DIAG-CODED-TYPE></PARAM>
                <PARAM SEMANTIC="ID"><CODED-VALUE>61840</CODED-VALUE>
                <DIAG-CODED-TYPE><BIT-LENGTH>16</BIT-LENGTH></DIAG-CODED-TYPE></PARAM>
              </REQUEST>
              <POS-RESPONSE ID="RESP"><PARAM SEMANTIC="DATA"><SHORT-NAME>EngineeringArray</SHORT-NAME>
                <DOP-REF ID-REF="DYNAMIC_ARRAY"/>
              </PARAM></POS-RESPONSE>
              <DYNAMIC-LENGTH-FIELD ID="DYNAMIC_ARRAY">
                <SHORT-NAME>RepeatedRecords</SHORT-NAME>
                <BASIC-STRUCTURE-REF ID-REF="ITEM_STRUCTURE"/>
              </DYNAMIC-LENGTH-FIELD>
              <STRUCTURE ID="ITEM_STRUCTURE"><SHORT-NAME>ArrayItem</SHORT-NAME>
                <PARAMS>
                  <PARAM><SHORT-NAME>Mode</SHORT-NAME>
                    <DESC><L-2>Engineering mode</L-2></DESC></PARAM>
                  <PARAM><SHORT-NAME>Temperature</SHORT-NAME>
                    <DESC><L-2>Recorded temperature</L-2></DESC></PARAM>
                </PARAMS>
              </STRUCTURE>
              <DIAG-SERVICE><SHORT-NAME>Read_EngineeringArray</SHORT-NAME>
                <REQUEST-REF ID-REF="REQ"/><POS-RESPONSE-REF ID-REF="RESP"/>
              </DIAG-SERVICE>
            </ODX>
            """;
        var catalog = Assert.Single(OdxCatalogReader.LoadFromDocuments(
            [XDocument.Parse(xml)], "dynamic.odx"));
        var did = Assert.Single(catalog.Parameters);
        Assert.Equal(0xF190, did.Did);
        Assert.Empty(did.Fields); // No verified dynamic data-record layout.
        var nodes = did.EngineeringParameters;
        Assert.Equal(3, nodes.Count);
        var parent = Assert.Single(nodes.Where(node => node.Name == "EngineeringArray"));
        Assert.Contains("occurrence count and offsets unverified", parent.ReferenceStatus);
        Assert.Null(parent.DeclaredBitLength);
        Assert.All(nodes.Skip(1), node => Assert.Equal(parent.Path, node.ParentPath));
        Assert.Contains(nodes, node => node.Name == "Mode" &&
            node.Description == "Engineering mode");
        Assert.Contains(nodes, node => node.Name == "Temperature" &&
            node.Description == "Recorded temperature");

        var engineering = CatalogDefinitionExport.CreateDiagnosticSheets([catalog], engineeringOnly: true);
        var rows = engineering.Single(sheet => sheet.Name == "Engineering Parameters").Rows;
        Assert.Equal(3, rows.Count);
        Assert.All(rows, row => Assert.Equal("0xF190", row[2]));
        var coverage = Assert.Single(engineering.Single(sheet =>
            sheet.Name == "PAR Extraction Coverage").Rows);
        Assert.Equal("0", coverage[6]); // Template is resolved, not missing.
        Assert.Equal("1", coverage[13]); // Variable cardinality is a separate warning.
        Assert.StartsWith("PARTIAL", coverage[7]);
    }

    [Fact]
    public void BasicStructureShortNameExpandsOnlyWhenUnique()
    {
        const string xml = """
            <ODX>
              <REQUEST ID="REQ"><PARAM SEMANTIC="SERVICE-ID"><CODED-VALUE>34</CODED-VALUE>
                <DIAG-CODED-TYPE><BIT-LENGTH>8</BIT-LENGTH></DIAG-CODED-TYPE></PARAM>
                <PARAM SEMANTIC="ID"><CODED-VALUE>61840</CODED-VALUE>
                <DIAG-CODED-TYPE><BIT-LENGTH>16</BIT-LENGTH></DIAG-CODED-TYPE></PARAM>
              </REQUEST>
              <POS-RESPONSE ID="RESP"><PARAM SEMANTIC="DATA">
                <SHORT-NAME>RepeatedData</SHORT-NAME><DOP-REF ID-REF="DYN"/>
              </PARAM></POS-RESPONSE>
              <DYNAMIC-LENGTH-FIELD ID="DYN">
                <SHORT-NAME>DynamicField</SHORT-NAME>
                <BASIC-STRUCTURE-SNREF SHORT-NAME="ItemType"/>
              </DYNAMIC-LENGTH-FIELD>
              <STRUCTURE ID="STRUCT_A"><SHORT-NAME>ItemType</SHORT-NAME><PARAMS>
                <PARAM><SHORT-NAME>SubParameterA</SHORT-NAME></PARAM>
              </PARAMS></STRUCTURE>
              <DIAG-SERVICE><SHORT-NAME>Read_Dynamic</SHORT-NAME>
                <REQUEST-REF ID-REF="REQ"/><POS-RESPONSE-REF ID-REF="RESP"/>
              </DIAG-SERVICE>
            </ODX>
            """;
        var first = Assert.Single(Assert.Single(OdxCatalogReader.LoadFromDocuments(
            [XDocument.Parse(xml)], "named-array.odx")).Parameters);
        Assert.Equal(2, first.EngineeringParameters.Count);
        Assert.Contains(first.EngineeringParameters,
            node => node.Name == "SubParameterA");

        var ambiguous = xml.Replace(
            "<DIAG-SERVICE><SHORT-NAME>Read_Dynamic</SHORT-NAME>",
            """
            <STRUCTURE ID="STRUCT_B"><SHORT-NAME>ItemType</SHORT-NAME><PARAMS>
              <PARAM><SHORT-NAME>WrongSiblingParameter</SHORT-NAME></PARAM>
            </PARAMS></STRUCTURE>
            <DIAG-SERVICE><SHORT-NAME>Read_Dynamic</SHORT-NAME>
            """, StringComparison.Ordinal);
        var second = Assert.Single(Assert.Single(OdxCatalogReader.LoadFromDocuments(
            [XDocument.Parse(ambiguous)], "ambiguous-array.odx")).Parameters);
        var root = Assert.Single(second.EngineeringParameters);
        Assert.Equal("RepeatedData", root.Name);
        Assert.Contains("unresolved or ambiguous", root.ReferenceStatus);
        Assert.DoesNotContain(second.EngineeringParameters,
            node => node.Name == "WrongSiblingParameter");
    }

    [Fact]
    public void UnresolvedDynamicStructureKeepsParentAndWarningInsteadOfGuessingChildren()
    {
        const string xml = """
            <ODX>
              <REQUEST ID="REQ"><PARAM SEMANTIC="SERVICE-ID"><CODED-VALUE>34</CODED-VALUE>
                <DIAG-CODED-TYPE><BIT-LENGTH>8</BIT-LENGTH></DIAG-CODED-TYPE></PARAM>
                <PARAM SEMANTIC="ID"><CODED-VALUE>61840</CODED-VALUE>
                <DIAG-CODED-TYPE><BIT-LENGTH>16</BIT-LENGTH></DIAG-CODED-TYPE></PARAM>
              </REQUEST>
              <POS-RESPONSE ID="RESP"><PARAM SEMANTIC="DATA">
                <SHORT-NAME>UnknownArray</SHORT-NAME><DOP-REF ID-REF="DYNAMIC_ARRAY"/>
              </PARAM></POS-RESPONSE>
              <DYNAMIC-LENGTH-FIELD ID="DYNAMIC_ARRAY">
                <SHORT-NAME>RepeatedRecords</SHORT-NAME>
                <BASIC-STRUCTURE-REF ID-REF="NOT_PRESENT"/>
              </DYNAMIC-LENGTH-FIELD>
              <DIAG-SERVICE><SHORT-NAME>Read_UnknownArray</SHORT-NAME>
                <REQUEST-REF ID-REF="REQ"/><POS-RESPONSE-REF ID-REF="RESP"/>
              </DIAG-SERVICE>
            </ODX>
            """;
        var did = Assert.Single(Assert.Single(OdxCatalogReader.LoadFromDocuments(
            [XDocument.Parse(xml)], "unknown-dynamic.odx")).Parameters);
        var node = Assert.Single(did.EngineeringParameters);
        Assert.Equal("UnknownArray", node.Name);
        Assert.Contains("unresolved or ambiguous", node.ReferenceStatus);
        Assert.Empty(did.Fields);
    }
}
