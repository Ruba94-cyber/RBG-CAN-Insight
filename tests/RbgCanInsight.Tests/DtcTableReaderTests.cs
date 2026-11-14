using RbgCanInsight.DiagData;
using RbgCanInsight.Diagnostics;
using Xunit;

namespace RbgCanInsight.Tests;

public sealed class DtcTableReaderTests
{
    private static void Check(string extension, string contents, Action<string> test)
    {
        var path = Path.Combine(Path.GetTempPath(), $"rbg-dtc-{Guid.NewGuid():N}{extension}");
        try
        {
            File.WriteAllText(path, contents);
            test(path);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static DecodedUdsOperation Decode(IReadOnlyList<DidCatalog> catalogs, string ecu)
    {
        var request = new IsoTpMessage(0, 0x7E0, false, 0, 0, [0],
            [0x19, 0x02, 0xFF], true, string.Empty);
        var response = new IsoTpMessage(0, 0x7E8, false, 1, 1, [1],
            [0x59, 0x02, 0xFF, 0x12, 0x34, 0x56, 0x08], true, string.Empty);
        return UdsServiceDecoder.Decode(request, [response], catalogs, ecu);
    }

    [Fact]
    public void SemicolonExcelExportSeparatesEcusAndPreservesDtcDescriptions()
    {
        Check(".csv", "DTC;ECU;Name;Text;Description\n" +
            "123456;ECM;PressureFault;Low pressure;\"Below threshold; at idle\"\n" +
            "123456;BCM;DoorFault;Door open;\"Door open\nwhile driving\"\n", path =>
        {
            var catalogs = DidCatalog.LoadAll(path);
            Assert.Equal(2, catalogs.Count);
            var ecm = Assert.Single(catalogs.Where(c => c.Ecu == "ECM"));
            var bcm = Assert.Single(catalogs.Where(c => c.Ecu == "BCM"));
            Assert.Equal(Path.GetFullPath(path), ecm.DtcDefinitions[0].SourceFile);
            Assert.Equal(0x123456, ecm.DtcDefinitions[0].Code);
            Assert.Equal("Below threshold; at idle", ecm.DtcDefinitions[0].Description);
            Assert.Equal("Door open\nwhile driving", bcm.DtcDefinitions[0].Description);
            Assert.Equal("Low pressure", Assert.Single(Decode(catalogs, "ECM").Dtcs).Name);
            Assert.Equal("Door open", Assert.Single(Decode(catalogs, "BCM").Dtcs).Name);
        });
    }

    [Fact]
    public void TabSeparatedDecimalCodeHeaderIsExplicitlyRespected()
    {
        Check(".tsv", "DTC decimal\tECU\tName\n1193046\tECM\tFault\n", path =>
        {
            var code = Assert.Single(Assert.Single(DidCatalog.LoadAll(path)).DtcDefinitions);
            Assert.Equal(0x123456, code.Code);
        });
    }

    [Fact]
    public void MissingEcuNeverBecomesAConfirmedEcuSpecificDtcInterpretation()
    {
        Check(".csv", "Code,Description\n0x123456,Possible generic meaning\n", path =>
        {
            var catalogs = DidCatalog.LoadAll(path);
            Assert.Equal("", Assert.Single(catalogs).Ecu);
            Assert.Equal("", Assert.Single(Decode(catalogs, "ECM").Dtcs).Name);
        });
    }

    [Fact]
    public void DuplicateConflictingDefinitionForSameEcuIsRejected()
    {
        Check(".csv", "DTC,ECU,Name\n123456,ECM,Fault A\n123456,ECM,Fault B\n", path =>
        {
            var error = Assert.Throws<InvalidDataException>(() => DidCatalog.LoadAll(path));
            Assert.Contains("Conflicting definitions", error.Message);
        });
    }

    [Fact]
    public void IdenticalDuplicateForSameEcuIsDeduplicated()
    {
        Check(".csv", "DTC,ECU,Name\n123456,ECM,Fault A\n123456,ECM,Fault A\n", path =>
        {
            Assert.Single(Assert.Single(DidCatalog.LoadAll(path)).DtcDefinitions);
        });
    }

    [Theory]
    [InlineData("P0123")]
    [InlineData("1234")]
    [InlineData("0x1000000")]
    [InlineData("")]
    public void AmbiguousObdOrInvalidCodesAreRejectedInsteadOfGuessed(string code)
    {
        Check(".csv", $"DTC,ECU,Name\n{code},ECM,Fault\n", path =>
        {
            Assert.Throws<InvalidDataException>(() => DidCatalog.LoadAll(path));
        });
    }

    [Fact]
    public void EscapedQuotesInsideQuotedCsvFieldsArePreserved()
    {
        Check(".csv", "DTC,ECU,Name,Description\n" +
            "0x001122,ECM,\"Pressure, low\",\"Threshold \"\"too low\"\"\"\n", path =>
        {
            var definition = Assert.Single(Assert.Single(DidCatalog.LoadAll(path)).DtcDefinitions);
            Assert.Equal("Pressure, low", definition.Name);
            Assert.Equal("Threshold \"too low\"", definition.Description);
        });
    }

    [Fact]
    public void AnUnclosedQuoteDoesNotProducePartialDefinitions()
    {
        Check(".csv", "DTC,ECU,Name\n123456,ECM,\"Unclosed\n", path =>
        {
            Assert.Throws<InvalidDataException>(() => DidCatalog.LoadAll(path));
        });
    }

    [Fact]
    public void CompatibleDtcTableAndOdxMetadataCombineWithoutLosingExtendedDataLayout()
    {
        Check(".csv",
            "DTC,ECU,Name,Text,Description,Failure type,Severity\n" +
            "123456,ECM,PressureFault,Low pressure,Fuel pressure below threshold,Open circuit,High\n", path =>
        {
            var table = Assert.Single(DidCatalog.LoadAll(path));
            var field = new DiagnosticFieldDefinition(
                "Counter", "Occurrence counter", "", 0, 8, "A_UINT8", "",
                null, null, 1, 0, false, "BIG_ENDIAN");
            var odxDtc = new DiagnosticTroubleCode(
                0x123456, "PressureFault", "Low pressure", "", "ECM", "engine.odx",
                null, [field]);
            var odx = new DidCatalog("engine", "ECM", "engine.odx", [], [odxDtc]);
            var request = new IsoTpMessage(0, 0x7E0, false, 0, 0, [0],
                [0x19, 0x06, 0x12, 0x34, 0x56, 0x01], true, "");
            var response = new IsoTpMessage(0, 0x7E8, false, 1, 1, [1],
                [0x59, 0x06, 0x12, 0x34, 0x56, 0x08, 0x01, 0x05], true, "");
            var decoded = UdsServiceDecoder.Decode(request, [response], [odx, table], "ECM");
            Assert.Equal("Low pressure", Assert.Single(decoded.Dtcs).Name);
            var record = Assert.Single(decoded.DtcRecords);
            Assert.True(record.IsFullyDecoded);
            Assert.Equal("5", Assert.Single(record.Values).Physical);

            var transaction = new DiagnosticTransaction(
                0, request, [response], new DiagnosticEndpoint(
                    "Tester", "ECM", DiagnosticDirection.EcuToTester,
                    DiagnosticEvidence.Confirmed, "physical"),
                DiagnosticOutcome.Positive, decoded, 0, 1, null, [], "");
            var fault = Assert.Single(DiagnosticInsightAnalyzer.Analyze(
                [transaction], [odx, table]).Faults);
            Assert.Equal("Fuel pressure below threshold", fault.Description);
            Assert.True(fault.DefinitionEcuMatched);
            Assert.Equal(2, fault.DefinitionSourceFiles.Count);
            Assert.Equal("Open circuit", fault.DefinitionAttributes["Failure type"]);
            Assert.Equal("High", fault.DefinitionAttributes["Severity"]);
            Assert.Contains("engine.odx", fault.DefinitionText);
            Assert.Contains(Path.GetFileName(path), fault.DefinitionText);
        });
    }

    [Fact]
    public void ConflictingDtcTableAndOdxNamesCannotSilentlyOverrideOneAnother()
    {
        Check(".csv", "DTC,ECU,Name,Text\n123456,ECM,OtherFault,Other fault\n", path =>
        {
            var table = Assert.Single(DidCatalog.LoadAll(path));
            var odxDtc = new DiagnosticTroubleCode(
                0x123456, "PressureFault", "Low pressure", "", "ECM", "engine.odx");
            var odx = new DidCatalog("engine", "ECM", "engine.odx", [], [odxDtc]);
            var decoded = Decode([odx, table], "ECM");
            Assert.Equal("", Assert.Single(decoded.Dtcs).Name);
            var transaction = new DiagnosticTransaction(
                1, null, [], new DiagnosticEndpoint(
                    "Tester", "ECM", DiagnosticDirection.EcuToTester,
                    DiagnosticEvidence.Confirmed, "physical"),
                DiagnosticOutcome.Positive, decoded, 0, 1, null, [], "");
            var fault = Assert.Single(DiagnosticInsightAnalyzer.Analyze(
                [transaction], [odx, table]).Faults);
            Assert.Equal(string.Empty, fault.Name);
            Assert.Equal(2, fault.DefinitionConflicts.Count);
            Assert.Contains(fault.DefinitionConflicts, candidate => candidate.Contains("engine.odx"));
            Assert.Contains(fault.DefinitionConflicts, candidate => candidate.Contains(Path.GetFileName(path)));
            var csv = FaultMemoryTable.ToCsv(FaultMemoryTable.Build([fault]));
            Assert.Contains("Competing DTC definitions", csv);
            Assert.Contains("Other fault", csv);
            Assert.Contains("Low pressure", csv);
        });
    }

    [Fact]
    public void ContradictoryOemTableAttributesPreventFalseReconciliation()
    {
        Check(".csv", "DTC,ECU,Name,Severity\n123456,ECM,Fault,High\n", first =>
        {
            Check(".csv", "DTC,ECU,Name,Severity\n123456,ECM,Fault,Low\n", second =>
            {
                var firstCatalog = Assert.Single(DidCatalog.LoadAll(first));
                var secondCatalog = Assert.Single(DidCatalog.LoadAll(second));
                Assert.Equal("", Assert.Single(Decode(
                    [firstCatalog, secondCatalog], "ECM").Dtcs).Name);
            });
        });
    }

    private static void CheckWorkbook(
        string faultSheet, Action<string> inspect,
        string? workbookRelationships = null)
    {
        var path = Path.Combine(Path.GetTempPath(), $"rbg-dtc-excel-{Guid.NewGuid():N}.xlsx");
        try
        {
            using (var zip = System.IO.Compression.ZipFile.Open(
                path, System.IO.Compression.ZipArchiveMode.Create))
            {
                void Add(string name, string content)
                {
                    var entry = zip.CreateEntry(name);
                    using var writer = new StreamWriter(entry.Open(), new System.Text.UTF8Encoding(false));
                    writer.Write(content);
                }
                Add("xl/workbook.xml",
                    "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"" +
                    " xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">" +
                    "<sheets><sheet name=\"Cover\" sheetId=\"1\" r:id=\"rId1\"/>" +
                    "<sheet name=\"Fault codes\" sheetId=\"2\" r:id=\"rId2\"/></sheets></workbook>");
                Add("xl/_rels/workbook.xml.rels",
                    workbookRelationships ??
                    "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                    "<Relationship Id=\"rId1\" " +
                    "Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" " +
                    "Target=\"worksheets/sheet1.xml\"/>" +
                    "<Relationship Id=\"rId2\" " +
                    "Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" " +
                    "Target=\"worksheets/sheet2.xml\"/></Relationships>");
                Add("xl/worksheets/sheet1.xml",
                    "<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" +
                    "<sheetData><row r=\"1\"><c r=\"A1\" t=\"inlineStr\">" +
                    "<is><t>Instructions</t></is></c></row></sheetData></worksheet>");
                Add("xl/worksheets/sheet2.xml", faultSheet);
                Add("xl/sharedStrings.xml",
                    "<sst xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" +
                    "<si><t>DTC</t></si><si><t>ECU</t></si>" +
                    "<si><t>Name</t></si><si><t>Severity</t></si>" +
                    "<si><t>ECM</t></si><si><t>BCM</t></si>" +
                    "<si><t>Pressure low</t></si><si><t>Door open</t></si>" +
                    "<si><r><t>Critical</t></r><r><t> condition</t></r></si>" +
                    "</sst>");
            }
            inspect(path);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private const string ExcelWorksheetStart =
        "<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>";

    [Fact]
    public void OemDtcTableHeaderAliasesWorkWithoutRequiringEnglishNameColumn()
    {
        Check(".csv",
            "DTC code (hex),ECU ID,DTC description,Failure type\n" +
            "0x123456,ECM,Intermittent pressure signal,Open circuit\n", path =>
        {
            var definition = Assert.Single(Assert.Single(DidCatalog.LoadAll(path)).DtcDefinitions);
            Assert.Equal(0x123456, definition.Code);
            Assert.Equal("ECM", definition.Ecu);
            Assert.Equal("Intermittent pressure signal", definition.Description);
            Assert.Equal("Open circuit", definition.TableAttributes["Failure type"]);
        });
    }

    [Fact]
    public void NativeExcelWorkbookImportsOnlyDtcSheetsAndKeepsSourceAndOemColumns()
    {
        var faults = ExcelWorksheetStart +
            "<row r=\"1\">" +
            "<c r=\"A1\" t=\"s\"><v>0</v></c>" +
            "<c r=\"B1\" t=\"s\"><v>1</v></c>" +
            "<c r=\"C1\" t=\"s\"><v>2</v></c>" +
            "<c r=\"D1\" t=\"s\"><v>3</v></c></row>" +
            "<row r=\"2\">" +
            "<c r=\"A2\"><v>123456</v></c>" +
            "<c r=\"B2\" t=\"s\"><v>4</v></c>" +
            "<c r=\"C2\" t=\"s\"><v>6</v></c>" +
            "<c r=\"D2\" t=\"s\"><v>8</v></c><c r=\"DZ2\" s=\"1\"/></row>" +
            "<row r=\"3\">" +
            "<c r=\"A3\" t=\"inlineStr\"><is><t>0x123456</t></is></c>" +
            "<c r=\"B3\" t=\"s\"><v>5</v></c>" +
            "<c r=\"C3\" t=\"s\"><v>7</v></c></row>" +
            "</sheetData></worksheet>";
        CheckWorkbook(faults, path =>
        {
            var catalogs = DidCatalog.LoadAll(path);
            Assert.Equal(2, catalogs.Count);
            var engine = Assert.Single(catalogs, catalog => catalog.Ecu == "ECM");
            var body = Assert.Single(catalogs, catalog => catalog.Ecu == "BCM");
            Assert.Contains("Fault codes", engine.Name);
            var dtc = Assert.Single(engine.DtcDefinitions);
            Assert.Equal(0x123456, dtc.Code);
            Assert.Equal(Path.GetFullPath(path), dtc.SourceFile);
            Assert.Equal("Fault codes", dtc.SourceSheet);
            Assert.Equal("Critical condition", dtc.TableAttributes["Severity"]);
            Assert.Equal("Pressure low", Assert.Single(Decode(catalogs, "ECM").Dtcs).Name);
            Assert.Equal("Door open", Assert.Single(Decode(catalogs, "BCM").Dtcs).Name);
        });
    }

    [Fact]
    public void NativeExcelWorkbookRejectsFormulaCellsInDtcSheet()
    {
        var sheet = ExcelWorksheetStart +
            "<row r=\"1\">" +
            "<c r=\"A1\" t=\"s\"><v>0</v></c>" +
            "<c r=\"B1\" t=\"s\"><v>2</v></c></row>" +
            "<row r=\"2\">" +
            "<c r=\"A2\"><f>123456</f><v>123456</v></c>" +
            "<c r=\"B2\" t=\"s\"><v>6</v></c></row>" +
            "</sheetData></worksheet>";
        CheckWorkbook(sheet, path =>
        {
            var error = Assert.Throws<InvalidDataException>(() => DidCatalog.LoadAll(path));
            Assert.Contains("formula", error.Message, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void NativeExcelWorkbookRejectsExternalOrEscapingWorksheetRelationships()
    {
        var sheet = ExcelWorksheetStart +
            "<row r=\"1\"><c r=\"A1\" t=\"s\"><v>0</v></c>" +
            "<c r=\"B1\" t=\"s\"><v>2</v></c></row>" +
            "<row r=\"2\"><c r=\"A2\"><v>123456</v></c>" +
            "<c r=\"B2\" t=\"s\"><v>6</v></c></row>" +
            "</sheetData></worksheet>";
        var rels =
            "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            "<Relationship Id=\"rId1\" " +
            "Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" " +
            "Target=\"worksheets/sheet1.xml\"/>" +
            "<Relationship Id=\"rId2\" " +
            "Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" " +
            "Target=\"../evil.xml\"/></Relationships>";
        CheckWorkbook(sheet, path =>
        {
            var error = Assert.Throws<InvalidDataException>(() => DidCatalog.LoadAll(path));
            Assert.Contains("unsafe", error.Message, StringComparison.OrdinalIgnoreCase);
        }, rels);
    }

    [Fact]
    public void NativeExcelWorksheetCanHaveIntroductoryRowsBeforeItsHeader()
    {
        var sheet = ExcelWorksheetStart +
            "<row r=\"1\"><c r=\"A1\" t=\"inlineStr\"><is><t>ECU dictionary</t></is></c></row>" +
            "<row r=\"4\"><c r=\"B4\" t=\"s\"><v>0</v></c>" +
            "<c r=\"C4\" t=\"s\"><v>1</v></c><c r=\"D4\" t=\"s\"><v>2</v></c></row>" +
            "<row r=\"5\"><c r=\"B5\"><v>123456</v></c>" +
            "<c r=\"C5\" t=\"s\"><v>4</v></c><c r=\"D5\" t=\"s\"><v>6</v></c></row>" +
            "</sheetData></worksheet>";
        CheckWorkbook(sheet, path =>
        {
            var catalogs = DidCatalog.LoadAll(path);
            Assert.Equal("Pressure low", Assert.Single(Decode(catalogs, "ECM").Dtcs).Name);
        });
    }


    [Fact]
    public void NativeExcelAndOdxComplementaryDtcDefinitionsRetainExactWorksheetProvenance()
    {
        var sheet = ExcelWorksheetStart +
            "<row r=\"1\"><c r=\"A1\" t=\"s\"><v>0</v></c>" +
            "<c r=\"B1\" t=\"s\"><v>1</v></c>" +
            "<c r=\"C1\" t=\"s\"><v>2</v></c>" +
            "<c r=\"D1\" t=\"s\"><v>3</v></c></row>" +
            "<row r=\"2\"><c r=\"A2\"><v>123456</v></c>" +
            "<c r=\"B2\" t=\"s\"><v>4</v></c>" +
            "<c r=\"C2\" t=\"s\"><v>6</v></c>" +
            "<c r=\"D2\" t=\"inlineStr\"><is><t>High</t></is></c></row>" +
            "</sheetData></worksheet>";
        CheckWorkbook(sheet, path =>
        {
            var table = Assert.Single(DidCatalog.LoadAll(path));
            var counter = new DiagnosticFieldDefinition(
                "Count", "Occurrence counter", "", 0, 8, "A_UINT8", "",
                null, null, 1, 0, false, "BIG_ENDIAN");
            var odx = new DidCatalog("ECM", "ECM", "reference.odx", [],
                [new DiagnosticTroubleCode(0x123456, "", "", "ODX layout", "ECM",
                    "reference.odx", null, [counter])]);
            var request = new IsoTpMessage(0, 0x7E0, false, 0, 0, [0],
                [0x19, 0x06, 0x12, 0x34, 0x56, 0x01], true, "");
            var response = new IsoTpMessage(0, 0x7E8, false, 1, 1, [1],
                [0x59, 0x06, 0x12, 0x34, 0x56, 0x08, 0x01, 0x05], true, "");
            var decoded = UdsServiceDecoder.Decode(request, [response], [odx, table], "ECM");
            Assert.Equal("Pressure low", Assert.Single(decoded.Dtcs).Name);
            Assert.True(Assert.Single(decoded.DtcRecords).IsFullyDecoded);

            var transaction = new DiagnosticTransaction(
                0, request, [response], new DiagnosticEndpoint(
                    "Tester", "ECM", DiagnosticDirection.EcuToTester,
                    DiagnosticEvidence.Confirmed, "physical"),
                DiagnosticOutcome.Positive, decoded, 0, 1, null, [], "");
            var fault = Assert.Single(DiagnosticInsightAnalyzer.Analyze(
                [transaction], [odx, table]).Faults);
            Assert.Equal("Fault codes", fault.DefinitionSourceSheet);
            Assert.Equal("High", fault.DefinitionAttributes["Severity"]);
            Assert.Equal(2, fault.DefinitionSourceFiles.Count);
            Assert.Contains("reference.odx", fault.DefinitionText);
            Assert.Contains("Excel sheet: Fault codes", fault.DefinitionText);
            var csv = FaultMemoryTable.ToCsv(FaultMemoryTable.Build([fault]));
            Assert.Contains("ECU definition verified,All definition files,Excel worksheet", csv);
            Assert.Contains("Fault codes", csv);
            Assert.Contains("reference.odx", csv);
            Assert.Contains("Severity: High", csv);
        });
    }

}
