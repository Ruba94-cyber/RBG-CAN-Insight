using System.IO.Compression;
using System.Xml.Linq;
using RbgCanInsight.DiagData;
using RbgCanInsight.TraceIO;
using Xunit;

namespace RbgCanInsight.Tests;

public sealed class DiagnosticDefinitionComparisonExportTests
{
    [Fact]
    public void DidAndNestedParDiffHaveSourceBackedXlsxAndCsvZipRows()
    {
        static DidCatalog Source(string type, string note) =>
            new("Synthetic", "ECM", @"C:\synthetic\fixture.odx",
                [new DidParameter(0xF405, "CoolantTemp", "Coolant", "Synthetic",
                    "degC", -40, 215, 1, -40, "A_UINT8", 8, "ECM", "fixture.odx",
                    EngineeringParameterDefinitions:
                    [
                        new DiagnosticEngineeringParameter("Temperature", "", "Synthetic PAR",
                            "Temperature[1]", "", 0, "PARAM", "P1", type,
                            "A_UINT8", "degC", 8, DefinitionOrigin: "response")
                    ],
                    DefinitionIssues: [note])]);

        var before = Source("DOP_OLD", "unresolved datatype");
        var after = Source("DOP_NEW", "repaired datatype");
        var report = DiagnosticDefinitionComparer.Compare([before], [after]);
        var sheets = DiagnosticDefinitionComparisonExport.BuildSheets(
            [before], [after], report,
            "/approved/cdd/old.odx", "/approved/cdd/new.odx");
        Assert.Equal(3, sheets.Count);
        Assert.Equal("Diagnostic definitions", sheets[1].Name);
        Assert.Contains(sheets[1].Rows, row =>
            row[0] == "Engineering PAR" && row[4] == "Changed" &&
            row[7].Contains("Type reference"));
        Assert.Equal(2, sheets[2].Rows.Count);
        Assert.Equal("old.odx", sheets[0].Rows[0][1]);

        var directory = Path.Combine(Path.GetTempPath(),
            "rbg-diagnostic-compare-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var workbook = Path.Combine(directory, "diagnostic-diff.xlsx");
            var zipFile = Path.Combine(directory, "diagnostic-diff.zip");
            DiagnosticDefinitionComparisonExport.Write(workbook, sheets, csvZip: false);
            DiagnosticDefinitionComparisonExport.Write(zipFile, sheets, csvZip: true);
            using (var zip = ZipFile.OpenRead(zipFile))
            {
                Assert.Equal(3, zip.Entries.Count);
                var details = Assert.Single(zip.Entries.Where(item =>
                    item.Name == "Diagnostic definitions.csv"));
                using var reader = new StreamReader(details.Open());
                var csv = reader.ReadToEnd();
                Assert.Contains("Engineering PAR", csv);
                Assert.Contains("DOP_NEW", csv);
            }
            using (var xlsx = ZipFile.OpenRead(workbook))
            {
                using var stream = xlsx.GetEntry("xl/worksheets/sheet2.xml")!.Open();
                var xml = XDocument.Load(stream);
                Assert.NotNull(xml.Root);
                Assert.Contains(xml.Descendants(), element =>
                    element.Name.LocalName == "t" && element.Value == "Engineering PAR");
            }
            DiagnosticDefinitionComparisonExport.Write(workbook, sheets, csvZip: false);
            DiagnosticDefinitionComparisonExport.Write(zipFile, sheets, csvZip: true);
            Assert.Empty(Directory.GetFiles(directory, "*.partial-*"));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
