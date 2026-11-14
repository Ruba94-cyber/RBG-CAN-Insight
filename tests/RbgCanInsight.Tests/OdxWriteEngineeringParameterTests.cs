using System.Xml.Linq;
using RbgCanInsight.DiagData;
using RbgCanInsight.TraceIO;
using Xunit;

namespace RbgCanInsight.Tests;

public sealed class OdxWriteEngineeringParameterTests
{
    private const string WriteDid = """
        <ODX>
          <REQUEST ID="WRITE_REQ">
            <PARAMS>
              <PARAM SEMANTIC="SERVICE-ID">
                <SHORT-NAME>WriteSID</SHORT-NAME><CODED-VALUE>46</CODED-VALUE>
                <DIAG-CODED-TYPE><BIT-LENGTH>8</BIT-LENGTH></DIAG-CODED-TYPE>
              </PARAM>
              <PARAM SEMANTIC="ID">
                <SHORT-NAME>DID_Header</SHORT-NAME><CODED-VALUE>61840</CODED-VALUE>
                <DIAG-CODED-TYPE><BIT-LENGTH>16</BIT-LENGTH></DIAG-CODED-TYPE>
              </PARAM>
              <PARAM SEMANTIC="DATA">
                <SHORT-NAME>WriteConfiguration</SHORT-NAME>
                <LONG-NAME>Write configuration</LONG-NAME>
                <BYTE-POSITION>3</BYTE-POSITION><BIT-POSITION>2</BIT-POSITION>
                <DOP-REF ID-REF="WRITE_STRUCTURE"/>
              </PARAM>
            </PARAMS>
          </REQUEST>
          <STRUCTURE ID="WRITE_STRUCTURE">
            <SHORT-NAME>ConfigurationType</SHORT-NAME>
            <PARAMS>
              <PARAM><SHORT-NAME>Mode</SHORT-NAME><PAR-ID>17</PAR-ID>
                <DESC><L-2>Operating mode</L-2></DESC>
                <DOP-REF ID-REF="MODE_TYPE"/>
              </PARAM>
              <PARAM><SHORT-NAME>Flags</SHORT-NAME><PAR-ID>18</PAR-ID>
                <DESC><L-2>Configuration flags</L-2></DESC>
              </PARAM>
            </PARAMS>
          </STRUCTURE>
          <DATA-OBJECT-PROP ID="MODE_TYPE">
            <SHORT-NAME>ModeCode</SHORT-NAME>
            <DIAG-CODED-TYPE BASE-DATA-TYPE="A_UINT8">
              <BIT-LENGTH>8</BIT-LENGTH>
            </DIAG-CODED-TYPE>
            <COMPU-METHOD><CATEGORY>TEXTTABLE</CATEGORY>
              <COMPU-INTERNAL-TO-PHYS><COMPU-SCALES>
                <COMPU-SCALE><LOWER-LIMIT>0</LOWER-LIMIT>
                  <UPPER-LIMIT>0</UPPER-LIMIT>
                  <COMPU-CONST><VT>Off</VT></COMPU-CONST>
                </COMPU-SCALE>
                <COMPU-SCALE><LOWER-LIMIT>1</LOWER-LIMIT>
                  <UPPER-LIMIT>3</UPPER-LIMIT>
                  <COMPU-CONST><VT>Enabled</VT></COMPU-CONST>
                </COMPU-SCALE>
              </COMPU-SCALES></COMPU-INTERNAL-TO-PHYS>
            </COMPU-METHOD>
          </DATA-OBJECT-PROP>
          <DIAG-SERVICE>
            <SHORT-NAME>Write_Configuration</SHORT-NAME>
            <REQUEST-REF ID-REF="WRITE_REQ"/>
          </DIAG-SERVICE>
        </ODX>
        """;

    [Fact]
    public void DirectWriteRequestReaderSkipsProtocolHeadersAndKeepsTheNestedPayload()
    {
        var doc = XDocument.Parse(WriteDid);
        var request = Assert.Single(doc.Descendants("REQUEST"));
        var byId = doc.Descendants()
            .Where(item => (string?)item.Attribute("ID") is not null)
            .ToDictionary(item => (string)item.Attribute("ID")!, item => item);

        var nodes = DiagnosticParameterHierarchyReader.FromOdxRequest(request, byId);
        Assert.Equal(3, nodes.Count);
        var root = Assert.Single(nodes.Where(node => node.Name == "WriteConfiguration"));
        Assert.Equal(0, root.Depth);
        Assert.Equal("WRITE_STRUCTURE", root.TypeReference);
        Assert.Equal("3", root.DeclaredBytePosition);
        Assert.Equal("2", root.DeclaredBitPosition);
        Assert.Equal("", root.DeclaredBitOffset);
        Assert.Contains(nodes, node => node.Name == "Mode" &&
            node.ParentPath == root.Path && node.Depth == 1 &&
            node.DeclaredParCode == "17" && node.DeclaredBitLength == 8);
        Assert.Contains(nodes, node => node.Name == "Flags" &&
            node.ParentPath == root.Path && node.DeclaredParCode == "18");
        Assert.DoesNotContain(nodes, node => node.Name is "WriteSID" or "DID_Header");
    }

    [Fact]
    public void CombinedReadWriteDidKeepsBothSourceTreesAndEveryWriteSubparameter()
    {
        var xml = WriteDid.Replace("</ODX>", """
            <REQUEST ID="READ_REQ">
              <PARAM SEMANTIC="SERVICE-ID"><CODED-VALUE>34</CODED-VALUE>
                <DIAG-CODED-TYPE><BIT-LENGTH>8</BIT-LENGTH></DIAG-CODED-TYPE>
              </PARAM>
              <PARAM SEMANTIC="ID"><CODED-VALUE>61840</CODED-VALUE>
                <DIAG-CODED-TYPE><BIT-LENGTH>16</BIT-LENGTH></DIAG-CODED-TYPE>
              </PARAM>
            </REQUEST>
            <POS-RESPONSE ID="READ_RESP">
              <PARAM SEMANTIC="DATA">
                <SHORT-NAME>ReadConfigurationStatus</SHORT-NAME>
                <BYTE-POSITION>3</BYTE-POSITION>
                <DOP-REF ID-REF="MODE_TYPE"/>
              </PARAM>
            </POS-RESPONSE>
            <DIAG-SERVICE><SHORT-NAME>Read_Configuration</SHORT-NAME>
              <REQUEST-REF ID-REF="READ_REQ"/>
              <POS-RESPONSE-REF ID-REF="READ_RESP"/>
            </DIAG-SERVICE>
            </ODX>
            """, StringComparison.Ordinal);
        var catalog = Assert.Single(OdxCatalogReader.LoadFromDocuments(
            [XDocument.Parse(xml)], "read-write.odx"));
        var did = Assert.Single(catalog.Parameters);
        Assert.Equal(0xF190, did.Did);
        Assert.Equal(4, did.EngineeringParameters.Count);
        Assert.Contains(did.EngineeringParameters, item =>
            item.Name == "WriteConfiguration" &&
            item.DefinitionOrigin.Contains("0x2E", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(did.EngineeringParameters, item =>
            item.Name == "ReadConfigurationStatus" &&
            item.DefinitionOrigin.Contains("0x22", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(did.EngineeringParameters, item => item.Name == "Mode" &&
            item.DeclaredParCode == "17");
        Assert.Contains(did.EngineeringParameters, item => item.Name == "Flags" &&
            item.DeclaredParCode == "18");
        Assert.Single(did.Fields); // From the read response only.
        var sheets = CatalogDefinitionExport.CreateDiagnosticSheets(
            [catalog], engineeringOnly: true);
        Assert.Equal(4, sheets.Single(sheet =>
            sheet.Name == "Engineering Parameters").Rows.Count);
        Assert.Equal(4, sheets.Single(sheet =>
            sheet.Name == "Engineering PAR Values").Rows.Count);
        Assert.Single(sheets.Single(sheet =>
            sheet.Name == "PAR Extraction Coverage").Rows);
    }

    [Fact]
    public void NestedSemanticIdIsNotDroppedAsAProtocolHeader()
    {
        // Top-level SID and DID are protocol headers. An ID semantic inside a
        // referenced STRUCTURE is an actual OEM engineering Sub-PAR.
        var xml = WriteDid.Replace(
            "<PARAM><SHORT-NAME>Mode</SHORT-NAME>",
            "<PARAM SEMANTIC=\"ID\"><SHORT-NAME>Mode</SHORT-NAME>",
            StringComparison.Ordinal);
        var did = Assert.Single(Assert.Single(
            OdxCatalogReader.LoadFromDocuments([XDocument.Parse(xml)],
                "write-nested-id.odx")).Parameters);
        var nodes = did.EngineeringParameters;
        Assert.Equal(3, nodes.Count);
        Assert.Contains(nodes, node => node.Name == "Mode" &&
            node.DeclaredParCode == "17" && node.Depth == 1);
        Assert.DoesNotContain(nodes, node => node.Name is "WriteSID" or "DID_Header");
    }

    [Fact]
    public void WriteOnlyDidExportsEntireRequestTreeAndSourceValueMeanings()
    {
        var catalogs = OdxCatalogReader.LoadFromDocuments(
            [XDocument.Parse(WriteDid)], "write-only.odx");
        var catalog = Assert.Single(catalogs);
        var did = Assert.Single(catalog.Parameters);
        Assert.Equal(0xF190, did.Did);
        Assert.Empty(did.Fields); // The 0x2E request is never a read decoder.
        Assert.Equal(3, did.EngineeringParameters.Count);

        var sheets = CatalogDefinitionExport.CreateDiagnosticSheets(
            catalogs, engineeringOnly: true);
        Assert.Equal(3, sheets.Count);
        var rows = sheets.Single(sheet => sheet.Name == "Engineering Parameters").Rows;
        Assert.Equal(3, rows.Count);
        Assert.Contains(rows, row => row[2] == "0xF190" &&
            row[9] == "Mode" && row[22] == "17" &&
            row[21].Contains("0x2E", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(rows, row => row[9] == "Flags" && row[22] == "18");
        Assert.Contains(rows, row => row[9] == "WriteConfiguration" &&
            row[23] == "3" && row[24] == "2" && row[25] == "");
        Assert.Contains(sheets.Single(sheet => sheet.Name == "Engineering Parameters")
            .Headers, header => header == "Source byte position (unverified)");

        var meanings = sheets.Single(sheet => sheet.Name == "Engineering PAR Values").Rows;
        Assert.Equal(2, meanings.Count);
        Assert.Contains(meanings, row => row[5] == "Mode" && row[12] == "Off");
        Assert.Contains(meanings, row => row[5] == "Mode" &&
            row[10] == "1" && row[11] == "3" && row[12] == "Enabled");

        var coverage = Assert.Single(sheets.Single(sheet =>
            sheet.Name == "PAR Extraction Coverage").Rows);
        Assert.Equal("3", coverage[4]);
        Assert.Equal("0", coverage[6]);
        Assert.Equal("2", coverage[14]);
    }
}
