using System.Xml.Linq;
using RbgCanInsight.DiagData;
using Xunit;

namespace RbgCanInsight.Tests;

/// <summary>ODX parser-to-diff regressions with synthetic safe read-only input.</summary>
public sealed class OdxDefinitionFileComparisonTests
{
    private const string WriteDid = """
        <ODX>
          <REQUEST ID="WRITE_REQ">
            <PARAMS>
              <PARAM SEMANTIC="SERVICE-ID">
                <SHORT-NAME>SID</SHORT-NAME><CODED-VALUE>46</CODED-VALUE>
                <DIAG-CODED-TYPE><BIT-LENGTH>8</BIT-LENGTH></DIAG-CODED-TYPE>
              </PARAM>
              <PARAM SEMANTIC="ID">
                <SHORT-NAME>Header</SHORT-NAME><CODED-VALUE>61840</CODED-VALUE>
                <DIAG-CODED-TYPE><BIT-LENGTH>16</BIT-LENGTH></DIAG-CODED-TYPE>
              </PARAM>
              <PARAM SEMANTIC="DATA">
                <SHORT-NAME>Configuration</SHORT-NAME>
                <DOP-REF ID-REF="WRITE_STRUCTURE"/>
              </PARAM>
            </PARAMS>
          </REQUEST>
          <STRUCTURE ID="WRITE_STRUCTURE">
            <SHORT-NAME>ConfigurationType</SHORT-NAME>
            <PARAMS>
              <PARAM><SHORT-NAME>Mode</SHORT-NAME><PAR-ID>17</PAR-ID>
                <DESC><L-2>Mode parameter</L-2></DESC>
                <DOP-REF ID-REF="MODE_TYPE"/>
              </PARAM>
              <PARAM><SHORT-NAME>Flags</SHORT-NAME><PAR-ID>18</PAR-ID></PARAM>
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
    public void WriteDidSubParCodeAndValueTableDifferencesAreSourceBacked()
    {
        var current = WriteDid.Replace("<PAR-ID>17</PAR-ID>",
                "<PAR-ID>99</PAR-ID>", StringComparison.Ordinal)
            .Replace("<VT>Off</VT>", "<VT>Disabled</VT>", StringComparison.Ordinal);
        var baseline = OdxCatalogReader.LoadFromDocuments(
            [XDocument.Parse(WriteDid)], "ecu.odx");
        var updated = OdxCatalogReader.LoadFromDocuments(
            [XDocument.Parse(current)], "ecu.odx");
        var beforeDid = Assert.Single(Assert.Single(baseline).Parameters);
        Assert.Equal(0xF190, beforeDid.Did);
        Assert.Empty(beforeDid.Fields);
        Assert.Equal(3, beforeDid.EngineeringParameters.Count);
        var report = DiagnosticDefinitionComparer.Compare(baseline, updated);
        var mode = Assert.Single(report.Rows.Where(row =>
            row.Scope == "Engineering PAR" &&
            row.BaselineDefinition.Contains("Mode parameter", StringComparison.Ordinal)));
        Assert.Equal(DiagnosticDefinitionChange.Changed, mode.Change);
        Assert.Equal(DiagnosticDefinitionImpact.Structure, mode.Impact);
        Assert.Contains("Declared PAR code", mode.ChangedFields);
        Assert.Contains("Raw declared meanings", mode.ChangedFields);
        Assert.Contains("99", mode.CurrentDefinition);
        Assert.Contains("Disabled", mode.CurrentDefinition);
        Assert.Contains(report.Rows, row => row.Scope == "DID" &&
            row.Change == DiagnosticDefinitionChange.Changed &&
            row.ChangedFields.Contains("Nested", StringComparison.Ordinal));
    }

    [Fact]
    public void UnchangedWriteDidDoesNotInventPhysicalReadFields()
    {
        var baseline = OdxCatalogReader.LoadFromDocuments(
            [XDocument.Parse(WriteDid)], "ecu.odx");
        var current = OdxCatalogReader.LoadFromDocuments(
            [XDocument.Parse(WriteDid)], "ecu.odx");
        var report = DiagnosticDefinitionComparer.Compare(baseline, current);
        Assert.Equal(0, report.RequiresReview);
        Assert.DoesNotContain(report.Rows, row => row.Scope == "DID Field");
        Assert.Contains(report.Rows, row => row.Scope == "Engineering PAR" &&
            row.Change == DiagnosticDefinitionChange.Unchanged);
    }
}
