using System.Xml.Linq;
using RbgCanInsight.DiagData;
using RbgCanInsight.TraceIO;
using Xunit;

namespace RbgCanInsight.Tests;

public sealed class OdxDeclaredValueMeaningTests
{
    [Fact]
    public void MissingBoundRangesAndConflictingCodesNeverBecomeInventedZeroEnums()
    {
        var dop = XElement.Parse("""
            <DATA-OBJECT-PROP ID="DOP"><COMPU-METHOD>
              <CATEGORY>TEXTTABLE</CATEGORY>
              <COMPU-INTERNAL-TO-PHYS><COMPU-SCALES>
                <COMPU-SCALE>
                  <LOWER-LIMIT>0</LOWER-LIMIT><UPPER-LIMIT>0</UPPER-LIMIT>
                  <COMPU-CONST><VT>Off</VT></COMPU-CONST>
                </COMPU-SCALE>
                <COMPU-SCALE>
                  <LOWER-LIMIT>1</LOWER-LIMIT><UPPER-LIMIT>1</UPPER-LIMIT>
                  <COMPU-CONST><VT>On</VT></COMPU-CONST>
                  <DESC><L-2>Normal operation</L-2></DESC>
                </COMPU-SCALE>
                <COMPU-SCALE>
                  <LOWER-LIMIT>2</LOWER-LIMIT><UPPER-LIMIT>7</UPPER-LIMIT>
                  <COMPU-CONST><VT>Reserved</VT></COMPU-CONST>
                </COMPU-SCALE>
                <COMPU-SCALE><COMPU-CONST><VT>Unknown bound</VT></COMPU-CONST></COMPU-SCALE>
                <COMPU-SCALE>
                  <LOWER-LIMIT>1</LOWER-LIMIT>
                  <COMPU-CONST><VT>Conflicting on meaning</VT></COMPU-CONST>
                </COMPU-SCALE>
              </COMPU-SCALES></COMPU-INTERNAL-TO-PHYS>
            </COMPU-METHOD></DATA-OBJECT-PROP>
            """);
        var raw = OdxDeclaredValueReader.Read(dop);
        Assert.Equal(5, raw.Count);
        Assert.Equal("Exact code", raw[0].Status);
        Assert.Equal("Declared range", raw[2].Status);
        Assert.Equal("2", raw[2].LowerRaw);
        Assert.Equal("7", raw[2].UpperRaw);
        Assert.Equal("Raw boundary absent", raw[3].Status);
        Assert.Equal("Normal operation", raw[1].Description);

        var safe = OdxDeclaredValueReader.ExactIntegerValues(raw);
        var off = Assert.Single(safe);
        Assert.Equal(0, off.Value);
        Assert.Equal("Off", off.Text);
        Assert.DoesNotContain(safe, item => item.Value is 1 or 2 or 7);
    }

    [Fact]
    public void OdxTexttableRowsWithoutTranslatedMeaningAreStillAuditable()
    {
        var dop = XElement.Parse("""
            <DATA-OBJECT-PROP><COMPU-METHOD><CATEGORY>TEXTTABLE</CATEGORY>
              <COMPU-INTERNAL-TO-PHYS><COMPU-SCALES>
                <COMPU-SCALE>
                  <LOWER-LIMIT>3</LOWER-LIMIT><UPPER-LIMIT>3</UPPER-LIMIT>
                </COMPU-SCALE>
              </COMPU-SCALES></COMPU-INTERNAL-TO-PHYS>
            </COMPU-METHOD></DATA-OBJECT-PROP>
            """);
        var node = Assert.Single(OdxDeclaredValueReader.Read(dop));
        Assert.Equal("3", node.LowerRaw);
        Assert.Equal("", node.Meaning);
        Assert.Equal("Missing text meaning", node.Status);
        Assert.Empty(OdxDeclaredValueReader.ExactIntegerValues([node]));
    }

    [Fact]
    public void NonTexttableScaleLabelsNeverBecomeCodedStatusEnums()
    {
        var dop = XElement.Parse("""
            <DATA-OBJECT-PROP><COMPU-METHOD><CATEGORY>LINEAR</CATEGORY>
              <COMPU-INTERNAL-TO-PHYS><COMPU-SCALES>
                <COMPU-SCALE>
                  <LOWER-LIMIT>0</LOWER-LIMIT><UPPER-LIMIT>0</UPPER-LIMIT>
                  <SHORT-LABEL>Linear segment</SHORT-LABEL>
                </COMPU-SCALE>
                <COMPU-SCALE>
                  <LOWER-LIMIT>1</LOWER-LIMIT><UPPER-LIMIT>1</UPPER-LIMIT>
                  <COMPU-CONST><VT>OEM informational label</VT></COMPU-CONST>
                </COMPU-SCALE>
              </COMPU-SCALES></COMPU-INTERNAL-TO-PHYS>
            </COMPU-METHOD></DATA-OBJECT-PROP>
            """);
        var declarations = OdxDeclaredValueReader.Read(dop);
        var item = Assert.Single(declarations);
        Assert.Equal("OEM informational label", item.Meaning);
        Assert.Equal("Non-texttable scale: decoding unverified", item.Status);
        Assert.Empty(OdxDeclaredValueReader.ExactIntegerValues(declarations));
    }

    [Fact]
    public void DecimalAndOutOfRangeNumericBoundsAreNotSilentlyTruncated()
    {
        var meanings = new[]
        {
            new DiagnosticDeclaredValueMeaning("1.5", "1.5", "Fraction", "", "Exact code"),
            new DiagnosticDeclaredValueMeaning("9223372036854775808", "9223372036854775808",
                "Overflow", "", "Exact code"),
            new DiagnosticDeclaredValueMeaning("-4", "-4", "Negative", "", "Exact code"),
            new DiagnosticDeclaredValueMeaning("23", "23", "Positive", "", "Exact code")
        };
        var safe = OdxDeclaredValueReader.ExactIntegerValues(meanings);
        Assert.Equal(new[] { -4L, 23L }, safe.Select(value => value.Value).ToArray());
    }

    [Fact]
    public void OdxEngineeringParAndWorkbookKeepRawExactAndRangeMeanings()
    {
        const string xml = """
            <ODX>
              <REQUEST ID="REQ"><PARAM SEMANTIC="SERVICE-ID">
                <CODED-VALUE>34</CODED-VALUE>
                <DIAG-CODED-TYPE><BIT-LENGTH>8</BIT-LENGTH></DIAG-CODED-TYPE></PARAM>
                <PARAM SEMANTIC="ID"><CODED-VALUE>61840</CODED-VALUE>
                <DIAG-CODED-TYPE><BIT-LENGTH>16</BIT-LENGTH></DIAG-CODED-TYPE></PARAM>
              </REQUEST>
              <POS-RESPONSE ID="RESP"><PARAM SEMANTIC="DATA">
                <SHORT-NAME>OperatingMode</SHORT-NAME><BYTE-POSITION>3</BYTE-POSITION>
                <DOP-REF ID-REF="DOP_MODE"/>
              </PARAM></POS-RESPONSE>
              <DATA-OBJECT-PROP ID="DOP_MODE"><SHORT-NAME>Modes</SHORT-NAME>
                <DIAG-CODED-TYPE BASE-DATA-TYPE="A_UINT8"><BIT-LENGTH>8</BIT-LENGTH></DIAG-CODED-TYPE>
                <COMPU-METHOD><CATEGORY>TEXTTABLE</CATEGORY>
                  <COMPU-INTERNAL-TO-PHYS><COMPU-SCALES>
                    <COMPU-SCALE><LOWER-LIMIT>0</LOWER-LIMIT>
                      <COMPU-CONST><VT>Off</VT></COMPU-CONST></COMPU-SCALE>
                    <COMPU-SCALE><LOWER-LIMIT>1</LOWER-LIMIT><UPPER-LIMIT>1</UPPER-LIMIT>
                      <COMPU-CONST><VT>Active</VT></COMPU-CONST></COMPU-SCALE>
                    <COMPU-SCALE><LOWER-LIMIT>2</LOWER-LIMIT><UPPER-LIMIT>15</UPPER-LIMIT>
                      <COMPU-CONST><VT>Reserved</VT></COMPU-CONST></COMPU-SCALE>
                  </COMPU-SCALES></COMPU-INTERNAL-TO-PHYS>
                </COMPU-METHOD>
              </DATA-OBJECT-PROP>
              <DIAG-SERVICE><SHORT-NAME>Read_Mode</SHORT-NAME>
                <REQUEST-REF ID-REF="REQ"/><POS-RESPONSE-REF ID-REF="RESP"/>
              </DIAG-SERVICE>
            </ODX>
            """;
        var catalogs = OdxCatalogReader.LoadFromDocuments([XDocument.Parse(xml)], "meanings.odx");
        var did = Assert.Single(Assert.Single(catalogs).Parameters);
        var field = Assert.Single(did.Fields);
        Assert.Equal(new[] { 0L, 1L }, field.EnumValues.Select(value => value.Value).ToArray());
        Assert.DoesNotContain(field.EnumValues, value => value.Text == "Reserved");

        var node = Assert.Single(did.EngineeringParameters);
        Assert.Equal(3, node.DeclaredValues.Count);
        Assert.Equal("2", node.DeclaredValues[2].LowerRaw);
        Assert.Equal("15", node.DeclaredValues[2].UpperRaw);

        var sheets = CatalogDefinitionExport.CreateDiagnosticSheets(catalogs, engineeringOnly: true);
        Assert.Equal(3, sheets.Count);
        var valueSheet = Assert.Single(sheets.Where(sheet => sheet.Name == "Engineering PAR Values"));
        Assert.Equal(3, valueSheet.Rows.Count);
        Assert.Contains(valueSheet.Rows, row => row[2] == "0xF190" &&
            row[5] == "OperatingMode" && row[10] == "2" &&
            row[11] == "15" && row[12] == "Reserved" &&
            row[14] == "Declared range");
        Assert.All(valueSheet.Rows, row => Assert.Equal(node.Path, row[6]));
        var quality = Assert.Single(sheets.Single(sheet =>
            sheet.Name == "PAR Extraction Coverage").Rows);
        Assert.Equal("3", quality[14]);

        // Find the complete DID by a meaning, not just a PAR short name.
        var byMeaning = CatalogDefinitionExport.CreateDiagnosticSheets(
            catalogs, search: "Reserved", engineeringOnly: true);
        Assert.Equal(3, byMeaning.Single(sheet =>
            sheet.Name == "Engineering PAR Values").Rows.Count);
        Assert.Single(byMeaning.Single(sheet =>
            sheet.Name == "PAR Extraction Coverage").Rows);

        var noMatch = CatalogDefinitionExport.CreateDiagnosticSheets(
            catalogs, search: "no-such-parameter", engineeringOnly: true);
        Assert.Empty(noMatch.Single(sheet =>
            sheet.Name == "Engineering PAR Values").Rows);
    }
}
