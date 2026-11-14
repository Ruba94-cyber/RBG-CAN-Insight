using System.Xml.Linq;
using RbgCanInsight.DiagData;
using RbgCanInsight.TraceIO;
using Xunit;

namespace RbgCanInsight.Tests;

public sealed class CddDeclaredValueMeaningTests
{
    [Fact]
    public void RealisticCandelaTextMapPreservesRangesLanguagesAndUnknownBoundaries()
    {
        var type = XElement.Parse("""
            <TEXTTBL id="t.map">
              <NAME><TUV xml:lang="en-US">Mode table</TUV></NAME>
              <CVALUETYPE bl="8" enc="uns"/>
              <TEXTMAP s="0" e="0">
                <TEXT><TUV xml:lang="de-DE">Aus</TUV>
                      <TUV xml:lang="en-US">Off</TUV></TEXT>
              </TEXTMAP>
              <TEXTMAP s="1" e="7">
                <TEXT><TUV xml:lang="de-DE">Reserviert</TUV>
                      <TUV xml:lang="en-US">Reserved</TUV></TEXT>
                <DESC><TUV xml:lang="en-US">Not available</TUV></DESC>
              </TEXTMAP>
              <TEXTMAP s="0x10" e="0x10">
                <TEXT><TUV xml:lang="de-DE">Ein</TUV></TEXT>
              </TEXTMAP>
              <TEXTMAP e="19">
                <TEXT><TUV xml:lang="en-US">Missing start</TUV></TEXT>
              </TEXTMAP>
              <TEXTMAP s="9" e="4">
                <TEXT><TUV xml:lang="en-US">Invalid interval</TUV></TEXT>
              </TEXTMAP>
              <TEXTMAP s="21" e="21"/>
            </TEXTTBL>
            """);

        var values = CddDeclaredValueReader.Read(type);
        Assert.Equal(6, values.Count);
        Assert.Equal("Off", values[0].Meaning);
        Assert.Equal("Exact code", values[0].Status);
        Assert.Equal("1", values[1].LowerRaw);
        Assert.Equal("7", values[1].UpperRaw);
        Assert.Equal("Reserved", values[1].Meaning);
        Assert.Equal("Not available", values[1].Description);
        Assert.Equal("Declared range", values[1].Status);
        Assert.Equal("Ein", values[2].Meaning); // Fallback to the only available language.
        Assert.Equal("Raw boundary absent", values[3].Status);
        Assert.Equal("Invalid declared range", values[4].Status);
        Assert.Equal("21", values[5].LowerRaw);
        Assert.Equal("", values[5].Meaning);
        Assert.Equal("Missing text meaning", values[5].Status);

        var exact = OdxDeclaredValueReader.ExactIntegerValues(values);
        Assert.Equal(new long[] { 0, 16 }, exact.Select(item => item.Value).ToArray());
        Assert.DoesNotContain(exact, value => value.Value is 1 or 7 or 9);
    }

    [Fact]
    public void ReferencedNestedCddTextTableKeepsDidAndSubparAndExportValues()
    {
        const string xml = """
            <CANDELA><ECU qual="BodyEcu"/><ECUDOC>
              <DATATYPES>
                <STRUCT id="t.packet"><QUAL>EngineeringPacket</QUAL>
                  <DATAOBJ id="mode.definition" dtref="t.mode" par="0x23">
                    <QUAL>Mode</QUAL><DESC>
                      <TUV xml:lang="en-US">Operating mode state</TUV>
                    </DESC>
                  </DATAOBJ>
                </STRUCT>
                <TEXTTBL id="t.mode">
                  <QUAL>ModeTable</QUAL>
                  <CVALUETYPE bl="8" enc="uns"/>
                  <TEXTMAP s="0" e="0">
                    <TEXT><TUV xml:lang="en-US">Off</TUV></TEXT>
                  </TEXTMAP>
                  <TEXTMAP s="1" e="4">
                    <TEXT><TUV xml:lang="en-US">Active</TUV></TEXT>
                  </TEXTMAP>
                  <TEXTMAP s="5" e="5">
                    <TEXT><TUV xml:lang="en-US">Maintenance</TUV></TEXT>
                  </TEXTMAP>
                </TEXTTBL>
                <TEXTTBL id="unrelated">
                  <TEXTMAP s="99" e="99">
                    <TEXT><TUV xml:lang="en-US">Unrelated value</TUV></TEXT>
                  </TEXTMAP>
                </TEXTTBL>
              </DATATYPES>
              <DIAGCLASS><QUAL>IDENTIFICATION</QUAL>
                <DIAGINST><QUAL>EngineeringStatus</QUAL>
                  <SERVICE><QUAL>Read</QUAL></SERVICE>
                  <STATICVALUE v="0xF190"/>
                  <SIMPLECOMPCONT>
                    <DATAOBJ id="master" dtref="t.packet">
                      <QUAL>EngineeringStatusData</QUAL>
                    </DATAOBJ>
                  </SIMPLECOMPCONT>
                </DIAGINST>
              </DIAGCLASS>
              </ECUDOC>
            </CANDELA>
            """;
        var path = Path.Combine(Path.GetTempPath(),
            "rbg-cdd-textmap-" + Guid.NewGuid().ToString("N") + ".cdd");
        File.WriteAllText(path, xml);
        try
        {
            var catalog = DidCatalog.Load(path);
            var did = Assert.Single(catalog.Parameters);
            Assert.Equal(0xF190, did.Did);
            var master = Assert.Single(did.EngineeringParameters.Where(item =>
                item.Name == "EngineeringStatusData"));
            var mode = Assert.Single(did.EngineeringParameters.Where(item =>
                item.Name == "Mode"));
            Assert.Equal(master.Path, mode.ParentPath);
            Assert.Equal("mode.definition", mode.SourceId);
            Assert.Equal("0x23", mode.DeclaredParCode);
            Assert.Equal("Operating mode state", mode.Description);
            Assert.Equal(8, mode.DeclaredBitLength);
            Assert.Equal(3, mode.DeclaredValues.Count);
            Assert.Empty(master.DeclaredValues);
            Assert.DoesNotContain(mode.DeclaredValues, value =>
                value.Meaning == "Unrelated value");

            var sheets = CatalogDefinitionExport.CreateDiagnosticSheets(
                [catalog], engineeringOnly: true);
            Assert.Equal(3, sheets.Count);
            var values = sheets.Single(sheet =>
                sheet.Name == "Engineering PAR Values").Rows;
            Assert.Equal(3, values.Count);
            Assert.Contains(values, row =>
                row[2] == "0xF190" && row[4] == "EngineeringStatusData" &&
                row[5] == "Mode" && row[7] == "0x23" &&
                row[10] == "1" && row[11] == "4" &&
                row[12] == "Active" && row[14] == "Declared range");

            var coverage = Assert.Single(sheets.Single(sheet =>
                sheet.Name == "PAR Extraction Coverage").Rows);
            Assert.Equal("3", coverage[14]);

            var filtered = CatalogDefinitionExport.CreateDiagnosticSheets(
                [catalog], search: "Maintenance", engineeringOnly: true);
            Assert.Equal(3, filtered.Single(sheet =>
                sheet.Name == "Engineering PAR Values").Rows.Count);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void CddResponseFieldInheritsReferencedTextTableValuesButNeverAnInventedOffset()
    {
        const string xml = """
            <CANDELA><ECU qual="Powertrain"/>
              <DATATYPES><TEXTTBL id="status.table">
                <CVALUETYPE bl="8" enc="uns"/>
                <TEXTMAP s="0" e="0"><TEXT>
                  <TUV xml:lang="en-US">Idle</TUV>
                </TEXT></TEXTMAP>
                <TEXTMAP s="1" e="2"><TEXT>
                  <TUV xml:lang="en-US">Active range</TUV>
                </TEXT></TEXTMAP>
                <TEXTMAP s="3" e="3"><TEXT>
                  <TUV xml:lang="en-US">Fault</TUV>
                </TEXT></TEXTMAP>
              </TEXTTBL></DATATYPES>
              <DIAGCLASS>
                <SERVICE>
                  <SHORTCUTQUAL>OperatingStatus_Read</SHORTCUTQUAL>
                  <SEMANTIC>CURRENTDATA</SEMANTIC>
                  <RESPONSE>
                    <DATAOBJ qual="ModeStatus" dtref="status.table">
                      <BITOFFSET>24</BITOFFSET>
                    </DATAOBJ>
                  </RESPONSE>
                </SERVICE>
                <STATICVALUE v="0xF194"/>
              </DIAGCLASS>
            </CANDELA>
            """;
        var path = Path.Combine(Path.GetTempPath(),
            "rbg-cdd-response-values-" + Guid.NewGuid().ToString("N") + ".cdd");
        File.WriteAllText(path, xml);
        try
        {
            var catalog = DidCatalog.Load(path);
            var did = Assert.Single(catalog.Parameters);
            Assert.Equal(0xF194, did.Did);
            var field = Assert.Single(did.Fields);
            Assert.Equal("ModeStatus", field.Name);
            Assert.Equal(8, field.BitLength);
            Assert.Equal(-1, field.BitOffset);
            Assert.False(field.SupportsPhysicalConversion);
            Assert.Contains("CDD declared position 24", field.Description);
            Assert.Equal(new long[] { 0, 3 },
                field.EnumValues.Select(value => value.Value).ToArray());
            Assert.DoesNotContain(field.EnumValues, value => value.Text == "Active range");

            var param = Assert.Single(did.EngineeringParameters);
            Assert.Equal("ModeStatus", param.Name);
            Assert.Equal("24", param.DeclaredBitOffset);
            Assert.Equal("", param.DeclaredBytePosition);
            Assert.Equal(3, param.DeclaredValues.Count);

            var sheets = CatalogDefinitionExport.CreateDiagnosticSheets([catalog]);
            var rawCodes = sheets.Single(sheet => sheet.Name == "DID Values").Rows;
            Assert.Equal(2, rawCodes.Count);
            Assert.Contains(rawCodes, row => row[2] == "0xF194" &&
                row[5] == "0" && row[6] == "Idle");
            Assert.Contains(rawCodes, row => row[5] == "3" && row[6] == "Fault");
            var positions = Assert.Single(sheets.Single(sheet =>
                sheet.Name == "Engineering Parameters").Rows);
            Assert.Equal("24", positions[25]);
            Assert.Equal("", positions[23]); // CDD BITOFFSET != ODX BYTE-POSITION.
            var declared = sheets.Single(sheet => sheet.Name == "Engineering PAR Values").Rows;
            Assert.Equal(3, declared.Count);
            Assert.Contains(declared, row => row[12] == "Active range" &&
                row[10] == "1" && row[11] == "2");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void UnrelatedAndNotReferencedTextTablesDoNotLeakIntoParameters()
    {
        var node = XElement.Parse("""
            <TEXTTBL id="unused"><TEXTMAP s="0" e="0"><TEXT>
              <TUV xml:lang="en-US">Unrelated</TUV></TEXT></TEXTMAP></TEXTTBL>
            """);
        Assert.Empty(CddDeclaredValueReader.Read(
            XElement.Parse("<IDENT id='scalar'><CVALUETYPE bl='8'/></IDENT>")));
        Assert.Single(CddDeclaredValueReader.Read(node));
    }

    [Fact]
    public void InvalidCddIntervalsArePreservedButCoverageMustSayPartial()
    {
        var malformed = new DiagnosticEngineeringParameter(
            "FaultMode", "FaultMode", "Fault state", "/1:FaultMode", "",
            0, "DATAOBJ", "mode", "t.invalid", "TEXTTBL", "", 8,
            DeclaredValueDefinitions:
            [
                new DiagnosticDeclaredValueMeaning(
                    "9", "4", "Invalid source interval", "",
                    "Invalid declared range")
            ]);
        var did = new DidParameter(
            0xF191, "Status", "Status", "", "",
            null, null, null, null, "", null,
            "Body", "bad.cdd",
            EngineeringParameterDefinitions: [malformed]);
        var sheets = CatalogDefinitionExport.CreateDiagnosticSheets(
            [new DidCatalog("Body", "Body", "bad.cdd", [did])],
            engineeringOnly: true);
        var coverage = Assert.Single(sheets.Single(sheet =>
            sheet.Name == "PAR Extraction Coverage").Rows);
        Assert.Equal("0", coverage[6]); // Type reference is resolved.
        Assert.Equal("1", coverage[14]); // One source value declaration.
        Assert.Equal("1", coverage[15]); // But no reliable bounds.
        Assert.Contains("PARTIAL: declared value boundaries", coverage[7]);

        var preserved = Assert.Single(sheets.Single(sheet =>
            sheet.Name == "Engineering PAR Values").Rows);
        Assert.Equal("9", preserved[10]);
        Assert.Equal("4", preserved[11]);
        Assert.Equal("Invalid declared range", preserved[14]);
    }

    [Fact]
    public void DuplicateCddDatatypeIdsAreReportedAsAmbiguousAndNotUsed()
    {
        const string xml = """
            <CANDELA><ECU qual="Body"/>
              <DATATYPES>
                <TEXTTBL id="same.id"><CVALUETYPE bl="8"/>
                  <TEXTMAP s="0" e="0"><TEXT>
                    <TUV xml:lang="en-US">First type meaning</TUV>
                  </TEXT></TEXTMAP>
                </TEXTTBL>
                <TEXTTBL id="same.id"><CVALUETYPE bl="16"/>
                  <TEXTMAP s="1" e="1"><TEXT>
                    <TUV xml:lang="en-US">Second type meaning</TUV>
                  </TEXT></TEXTMAP>
                </TEXTTBL>
              </DATATYPES>
              <DIAGCLASS><QUAL>IDENTIFICATION</QUAL>
                <DIAGINST><QUAL>Model</QUAL>
                  <SERVICE><QUAL>Read</QUAL></SERVICE>
                  <STATICVALUE v="0xF191"/>
                  <SIMPLECOMPCONT><DATAOBJ id="par.mode" dtref="same.id">
                    <QUAL>Mode</QUAL>
                  </DATAOBJ></SIMPLECOMPCONT>
                </DIAGINST>
              </DIAGCLASS>
            </CANDELA>
            """;
        var path = Path.Combine(Path.GetTempPath(),
            "rbg-ambiguous-types-" + Guid.NewGuid().ToString("N") + ".cdd");
        File.WriteAllText(path, xml);
        try
        {
            var catalog = DidCatalog.Load(path);
            var did = Assert.Single(catalog.Parameters);
            var node = Assert.Single(did.EngineeringParameters);
            Assert.Equal("Mode", node.Name);
            Assert.Contains("Ambiguous CDD datatype ID", node.ReferenceStatus);
            Assert.Empty(node.DeclaredValues);
            Assert.Null(node.DeclaredBitLength);
            var coverage = Assert.Single(CatalogDefinitionExport.CreateDiagnosticSheets(
                [catalog], engineeringOnly: true)
                .Single(sheet => sheet.Name == "PAR Extraction Coverage").Rows);
            Assert.Equal("1", coverage[6]);
            Assert.StartsWith("PARTIAL", coverage[7]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void OverlappingSourceIntervalsCannotProduceContradictoryExactEnums()
    {
        var declarations = new[]
        {
            new DiagnosticDeclaredValueMeaning("0", "0", "Off", "", "Exact code"),
            new DiagnosticDeclaredValueMeaning("1", "7", "Enabled", "", "Declared range"),
            new DiagnosticDeclaredValueMeaning("2", "2", "Fault", "", "Exact code"),
            new DiagnosticDeclaredValueMeaning("5", "5", "Enabled", "", "Exact code"),
            new DiagnosticDeclaredValueMeaning("10", "10", "Idle", "", "Exact code")
        };
        var safe = OdxDeclaredValueReader.ExactIntegerValues(declarations);
        Assert.Equal(new long[] { 0, 5, 10 }, safe.Select(row => row.Value).ToArray());
        Assert.DoesNotContain(safe, row => row.Value == 2);
        Assert.Equal(5, declarations.Length); // No source row is lost.
    }

    [Fact]
    public void EquivalentHexAndDecimalCddBoundsRepresentOneExactCode()
    {
        var type = XElement.Parse("""
            <TEXTTBL id="t.hex">
              <TEXTMAP s="0x10" e="16"><TEXT>
                <TUV xml:lang="en-US">Maintenance</TUV>
              </TEXT></TEXTMAP>
            </TEXTTBL>
            """);
        var declaration = Assert.Single(CddDeclaredValueReader.Read(type));
        Assert.Equal("Exact code", declaration.Status);
        Assert.True(declaration.IsExact);
        var exact = Assert.Single(OdxDeclaredValueReader.ExactIntegerValues([declaration]));
        Assert.Equal(16, exact.Value);
        Assert.Equal("Maintenance", exact.Text);
    }

    [Fact]
    public void HexadecimalValuesBeyondSignedLongDoNotWrapNegative()
    {
        var type = XElement.Parse("""
            <TEXTTBL id="overflow">
              <TEXTMAP s="0xFFFFFFFFFFFFFFFF" e="0xFFFFFFFFFFFFFFFF">
                <TEXT><TUV xml:lang="en-US">Too wide for signed lookup</TUV></TEXT>
              </TEXTMAP>
            </TEXTTBL>
            """);
        var raw = Assert.Single(CddDeclaredValueReader.Read(type));
        Assert.Contains("Unsupported coded boundary", raw.Status);
        Assert.Empty(OdxDeclaredValueReader.ExactIntegerValues([raw]));
    }
}
