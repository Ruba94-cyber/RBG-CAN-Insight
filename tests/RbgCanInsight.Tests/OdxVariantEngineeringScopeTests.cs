using System.Xml.Linq;
using RbgCanInsight.DiagData;
using RbgCanInsight.TraceIO;
using Xunit;

namespace RbgCanInsight.Tests;

public sealed class OdxVariantEngineeringScopeTests
{
    [Fact]
    public void IndependentEcuVariantsDoNotLeakServicesOrReuseSiblingSubparameters()
    {
        const string xml = """
            <ODX>
              <REQUEST ID="ReqA"><PARAM SEMANTIC="SERVICE-ID"><CODED-VALUE>34</CODED-VALUE>
                <DIAG-CODED-TYPE><BIT-LENGTH>8</BIT-LENGTH></DIAG-CODED-TYPE></PARAM>
                <PARAM SEMANTIC="ID"><CODED-VALUE>61840</CODED-VALUE>
                <DIAG-CODED-TYPE><BIT-LENGTH>16</BIT-LENGTH></DIAG-CODED-TYPE></PARAM></REQUEST>
              <REQUEST ID="ReqB"><PARAM SEMANTIC="SERVICE-ID"><CODED-VALUE>34</CODED-VALUE>
                <DIAG-CODED-TYPE><BIT-LENGTH>8</BIT-LENGTH></DIAG-CODED-TYPE></PARAM>
                <PARAM SEMANTIC="ID"><CODED-VALUE>61841</CODED-VALUE>
                <DIAG-CODED-TYPE><BIT-LENGTH>16</BIT-LENGTH></DIAG-CODED-TYPE></PARAM></REQUEST>
              <POS-RESPONSE ID="RespA"><PARAM SEMANTIC="DATA"><SHORT-NAME>EngineeringRoot</SHORT-NAME>
                <DOP-SNREF SHORT-NAME="SharedType"/></PARAM></POS-RESPONSE>
              <POS-RESPONSE ID="RespB"><PARAM SEMANTIC="DATA"><SHORT-NAME>EngineeringRoot</SHORT-NAME>
                <DOP-SNREF SHORT-NAME="SharedType"/></PARAM></POS-RESPONSE>
              <DIAG-LAYER-CONTAINER>
                <ECU-VARIANT ID="A"><SHORT-NAME>ECU_A</SHORT-NAME>
                  <DIAG-COMMS><DIAG-SERVICE><SHORT-NAME>Read_A</SHORT-NAME>
                    <REQUEST-REF ID-REF="ReqA"/><POS-RESPONSE-REF ID-REF="RespA"/>
                  </DIAG-SERVICE></DIAG-COMMS>
                  <DIAG-DATA-DICTIONARY-SPEC><STRUCTURES><STRUCTURE ID="TypeA">
                    <SHORT-NAME>SharedType</SHORT-NAME><PARAMS><PARAM>
                      <SHORT-NAME>A_Only</SHORT-NAME></PARAM></PARAMS>
                  </STRUCTURE></STRUCTURES></DIAG-DATA-DICTIONARY-SPEC>
                </ECU-VARIANT>
                <ECU-VARIANT ID="B"><SHORT-NAME>ECU_B</SHORT-NAME>
                  <DIAG-COMMS><DIAG-SERVICE><SHORT-NAME>Read_B</SHORT-NAME>
                    <REQUEST-REF ID-REF="ReqB"/><POS-RESPONSE-REF ID-REF="RespB"/>
                  </DIAG-SERVICE></DIAG-COMMS>
                  <DIAG-DATA-DICTIONARY-SPEC><STRUCTURES><STRUCTURE ID="TypeB">
                    <SHORT-NAME>SharedType</SHORT-NAME><PARAMS><PARAM>
                      <SHORT-NAME>B_Only</SHORT-NAME></PARAM></PARAMS>
                  </STRUCTURE></STRUCTURES></DIAG-DATA-DICTIONARY-SPEC>
                </ECU-VARIANT>
              </DIAG-LAYER-CONTAINER>
            </ODX>
            """;

        var catalogs = OdxCatalogReader.LoadFromDocuments([XDocument.Parse(xml)], "two-ecu.odx");
        Assert.Equal(2, catalogs.Count);
        var ecuA = Assert.Single(catalogs.Where(item => item.Ecu == "ECU_A"));
        var ecuB = Assert.Single(catalogs.Where(item => item.Ecu == "ECU_B"));
        var didA = Assert.Single(ecuA.Parameters);
        var didB = Assert.Single(ecuB.Parameters);
        Assert.Equal(0xF190, didA.Did);
        Assert.Equal(0xF191, didB.Did);
        Assert.Contains(didA.EngineeringParameters, node => node.Name == "A_Only");
        Assert.DoesNotContain(didA.EngineeringParameters, node => node.Name == "B_Only");
        Assert.Contains(didB.EngineeringParameters, node => node.Name == "B_Only");
        Assert.DoesNotContain(didB.EngineeringParameters, node => node.Name == "A_Only");
        Assert.Equal("ECU_A", Assert.Single(ecuA.ServiceDefinitions).Ecu);
        Assert.Equal("ECU_B", Assert.Single(ecuB.ServiceDefinitions).Ecu);

        var engineering = CatalogDefinitionExport.CreateDiagnosticSheets(
            catalogs, engineeringOnly: true).Single(sheet =>
                sheet.Name == "Engineering Parameters").Rows;
        Assert.Contains(engineering, row => row[1] == "ECU_A" &&
            row[2] == "0xF190" && row[9] == "A_Only");
        Assert.Contains(engineering, row => row[1] == "ECU_B" &&
            row[2] == "0xF191" && row[9] == "B_Only");
        Assert.DoesNotContain(engineering, row => row[1] == "ECU_A" && row[9] == "B_Only");

        var focused = CatalogDefinitionExport.CreateDiagnosticSheets(
            catalogs, engineeringOnly: true, ecuFilter: "ECU_A");
        Assert.All(focused.Single(sheet => sheet.Name == "Engineering Parameters").Rows,
            row => Assert.Equal("ECU_A", row[1]));
        var scopedCoverage = Assert.Single(focused.Single(sheet =>
            sheet.Name == "PAR Extraction Coverage").Rows);
        Assert.Equal("ECU_A", scopedCoverage[1]);
    }

    [Fact]
    public void InheritedServiceNamedTypeResolvesInEachEffectiveEcuVariant()
    {
        const string xml = """
            <ODX>
              <REQUEST ID="Request"><PARAM SEMANTIC="SERVICE-ID"><CODED-VALUE>34</CODED-VALUE>
                <DIAG-CODED-TYPE><BIT-LENGTH>8</BIT-LENGTH></DIAG-CODED-TYPE></PARAM>
                <PARAM SEMANTIC="ID"><CODED-VALUE>61840</CODED-VALUE>
                <DIAG-CODED-TYPE><BIT-LENGTH>16</BIT-LENGTH></DIAG-CODED-TYPE></PARAM></REQUEST>
              <POS-RESPONSE ID="Response"><PARAM SEMANTIC="DATA">
                <SHORT-NAME>EngineeringRoot</SHORT-NAME><DOP-SNREF SHORT-NAME="SharedType"/>
              </PARAM></POS-RESPONSE>
              <PROTOCOL ID="P"><SHORT-NAME>UDS</SHORT-NAME><DIAG-COMMS>
                <DIAG-SERVICE><SHORT-NAME>Read_Common</SHORT-NAME>
                  <REQUEST-REF ID-REF="Request"/><POS-RESPONSE-REF ID-REF="Response"/>
                </DIAG-SERVICE>
              </DIAG-COMMS></PROTOCOL>
              <ECU-VARIANT ID="A"><SHORT-NAME>ECU_A</SHORT-NAME>
                <PARENT-REFS><PARENT-REF ID-REF="P"/></PARENT-REFS>
                <DIAG-DATA-DICTIONARY-SPEC><STRUCTURES><STRUCTURE ID="A_TYPE">
                  <SHORT-NAME>SharedType</SHORT-NAME><PARAMS>
                    <PARAM><SHORT-NAME>A_Signal</SHORT-NAME></PARAM>
                  </PARAMS></STRUCTURE></STRUCTURES></DIAG-DATA-DICTIONARY-SPEC>
              </ECU-VARIANT>
              <ECU-VARIANT ID="B"><SHORT-NAME>ECU_B</SHORT-NAME>
                <PARENT-REFS><PARENT-REF ID-REF="P"/></PARENT-REFS>
                <DIAG-DATA-DICTIONARY-SPEC><STRUCTURES><STRUCTURE ID="B_TYPE">
                  <SHORT-NAME>SharedType</SHORT-NAME><PARAMS>
                    <PARAM><SHORT-NAME>B_Signal</SHORT-NAME></PARAM>
                  </PARAMS></STRUCTURE></STRUCTURES></DIAG-DATA-DICTIONARY-SPEC>
              </ECU-VARIANT>
            </ODX>
            """;
        var catalogs = OdxCatalogReader.LoadFromDocuments([XDocument.Parse(xml)], "inherited.odx");
        Assert.Equal(2, catalogs.Count);
        var a = Assert.Single(catalogs.Single(c => c.Ecu == "ECU_A").Parameters);
        var b = Assert.Single(catalogs.Single(c => c.Ecu == "ECU_B").Parameters);
        Assert.Equal(a.Did, b.Did);
        Assert.Contains(a.EngineeringParameters, n => n.Name == "A_Signal");
        Assert.DoesNotContain(a.EngineeringParameters, n => n.Name == "B_Signal");
        Assert.Contains(b.EngineeringParameters, n => n.Name == "B_Signal");
        Assert.DoesNotContain(b.EngineeringParameters, n => n.Name == "A_Signal");
        Assert.All(a.EngineeringParameters, n => Assert.Equal("", n.ReferenceStatus));
        Assert.All(b.EngineeringParameters, n => Assert.Equal("", n.ReferenceStatus));
    }

    [Fact]
    public void SameRequestResponseAndTypeIdsInIndependentVariantsStayLocalToEachEcu()
    {
        static string Variant(string ecu, int did, string field) => $"""
            <ECU-VARIANT ID="{ecu}">
              <SHORT-NAME>{ecu}</SHORT-NAME>
              <REQUESTS><REQUEST ID="REQ_SAME"><PARAMS>
                <PARAM SEMANTIC="SERVICE-ID"><CODED-VALUE>34</CODED-VALUE>
                  <DIAG-CODED-TYPE><BIT-LENGTH>8</BIT-LENGTH></DIAG-CODED-TYPE></PARAM>
                <PARAM SEMANTIC="ID"><CODED-VALUE>{did}</CODED-VALUE>
                  <DIAG-CODED-TYPE><BIT-LENGTH>16</BIT-LENGTH></DIAG-CODED-TYPE></PARAM>
              </PARAMS></REQUEST></REQUESTS>
              <POS-RESPONSES><POS-RESPONSE ID="RESP_SAME"><PARAMS>
                <PARAM SEMANTIC="DATA"><SHORT-NAME>{field}</SHORT-NAME>
                  <BYTE-POSITION>3</BYTE-POSITION><DOP-REF ID-REF="DOP_SAME"/>
                </PARAM>
              </PARAMS></POS-RESPONSE></POS-RESPONSES>
              <DIAG-DATA-DICTIONARY-SPEC><DATA-OBJECT-PROPS>
                <DATA-OBJECT-PROP ID="DOP_SAME"><SHORT-NAME>Type{field}</SHORT-NAME>
                  <DIAG-CODED-TYPE BASE-DATA-TYPE="A_UINT8">
                    <BIT-LENGTH>8</BIT-LENGTH></DIAG-CODED-TYPE>
                </DATA-OBJECT-PROP>
              </DATA-OBJECT-PROPS></DIAG-DATA-DICTIONARY-SPEC>
              <DIAG-COMMS><DIAG-SERVICE><SHORT-NAME>Read_{field}</SHORT-NAME>
                <REQUEST-REF ID-REF="REQ_SAME"/>
                <POS-RESPONSE-REF ID-REF="RESP_SAME"/>
              </DIAG-SERVICE></DIAG-COMMS>
            </ECU-VARIANT>
            """;

        var xml = "<ODX><DIAG-LAYER-CONTAINER>" +
            Variant("ECU_A", 61840, "A_Field") +
            Variant("ECU_B", 61841, "B_Field") +
            "</DIAG-LAYER-CONTAINER></ODX>";
        var catalogs = OdxCatalogReader.LoadFromDocuments(
            [XDocument.Parse(xml)], "duplicate-variant-id.odx");
        Assert.Equal(2, catalogs.Count);
        var a = Assert.Single(catalogs.Single(c => c.Ecu == "ECU_A").Parameters);
        var b = Assert.Single(catalogs.Single(c => c.Ecu == "ECU_B").Parameters);
        Assert.Equal(0xF190, a.Did);
        Assert.Equal(0xF191, b.Did);
        Assert.Equal("A_Field", Assert.Single(a.Fields).Name);
        Assert.Equal("B_Field", Assert.Single(b.Fields).Name);
        Assert.Equal("A_Field", Assert.Single(a.EngineeringParameters).Name);
        Assert.Equal("B_Field", Assert.Single(b.EngineeringParameters).Name);
    }

    [Fact]
    public void CollidingRequestIdsInsideOneEcuCannotSelectAnArbitraryDid()
    {
        const string xml = """
            <ODX><ECU-VARIANT ID="ECU"><SHORT-NAME>ECU</SHORT-NAME>
              <REQUESTS>
                <REQUEST ID="DUP"><PARAM SEMANTIC="SERVICE-ID">
                  <CODED-VALUE>34</CODED-VALUE>
                  <DIAG-CODED-TYPE><BIT-LENGTH>8</BIT-LENGTH></DIAG-CODED-TYPE>
                </PARAM><PARAM SEMANTIC="ID">
                  <CODED-VALUE>61840</CODED-VALUE>
                  <DIAG-CODED-TYPE><BIT-LENGTH>16</BIT-LENGTH></DIAG-CODED-TYPE>
                </PARAM></REQUEST>
                <REQUEST ID="DUP"><PARAM SEMANTIC="SERVICE-ID">
                  <CODED-VALUE>34</CODED-VALUE>
                  <DIAG-CODED-TYPE><BIT-LENGTH>8</BIT-LENGTH></DIAG-CODED-TYPE>
                </PARAM><PARAM SEMANTIC="ID">
                  <CODED-VALUE>61841</CODED-VALUE>
                  <DIAG-CODED-TYPE><BIT-LENGTH>16</BIT-LENGTH></DIAG-CODED-TYPE>
                </PARAM></REQUEST>
              </REQUESTS>
              <DIAG-COMMS><DIAG-SERVICE><SHORT-NAME>Read_Unknown</SHORT-NAME>
                <REQUEST-REF ID-REF="DUP"/>
              </DIAG-SERVICE></DIAG-COMMS>
            </ECU-VARIANT></ODX>
            """;
        var catalog = Assert.Single(OdxCatalogReader.LoadFromDocuments(
            [XDocument.Parse(xml)], "ambiguous-ids.odx"));
        Assert.Empty(catalog.Parameters);
    }

    [Fact]
    public void EmptyIndependentVariantDoesNotStealSiblingServices()
    {
        const string xml = """
            <ODX>
              <REQUEST ID="Request"><PARAM SEMANTIC="SERVICE-ID"><CODED-VALUE>34</CODED-VALUE>
                <DIAG-CODED-TYPE><BIT-LENGTH>8</BIT-LENGTH></DIAG-CODED-TYPE></PARAM>
                <PARAM SEMANTIC="ID"><CODED-VALUE>61840</CODED-VALUE>
                <DIAG-CODED-TYPE><BIT-LENGTH>16</BIT-LENGTH></DIAG-CODED-TYPE></PARAM></REQUEST>
              <ECU-VARIANT ID="A"><SHORT-NAME>ECU_A</SHORT-NAME>
                <DIAG-COMMS><DIAG-SERVICE><SHORT-NAME>Read_OnlyA</SHORT-NAME>
                  <REQUEST-REF ID-REF="Request"/></DIAG-SERVICE></DIAG-COMMS>
              </ECU-VARIANT>
              <ECU-VARIANT ID="B"><SHORT-NAME>ECU_B</SHORT-NAME>
                <DIAG-COMMS/>
              </ECU-VARIANT>
            </ODX>
            """;
        var catalogs = OdxCatalogReader.LoadFromDocuments([XDocument.Parse(xml)], "empty-sibling.odx");
        Assert.Equal(2, catalogs.Count);
        Assert.Single(catalogs.Single(c => c.Ecu == "ECU_A").Parameters);
        Assert.Empty(catalogs.Single(c => c.Ecu == "ECU_B").Parameters);
    }
}
