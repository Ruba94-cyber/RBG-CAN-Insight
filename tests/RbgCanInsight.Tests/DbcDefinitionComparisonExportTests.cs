using System.IO.Compression;
using System.Xml.Linq;
using RbgCanInsight.Dbc;
using RbgCanInsight.TraceIO;
using Xunit;

namespace RbgCanInsight.Tests;

public sealed class DbcDefinitionComparisonExportTests
{
    private static DbcDatabase Parse(string text) =>
        new DbcParser().Parse("can", new StringReader(text));

    [Fact]
    public void ExportCarriesExactFrameIdentityAndChangedFieldDetails()
    {
        var before = Parse("""
            BO_ 256 Engine: 8 ECU
             SG_ Speed : 0|16@1+ (0.01,0) [0|250] "km/h" Node
            """);
        var after = Parse("""
            BO_ 256 Engine: 8 ECU
             SG_ Speed : 8|16@1+ (0.02,0) [0|250] "km/h" Node
            """);
        var report = DbcDefinitionComparer.Compare(before, after);
        var sheets = DbcDefinitionComparisonExport.BuildSheets(before, after,
            report, @"C:\source\before.dbc", "/source/after.dbc");
        Assert.Equal(3, sheets.Count);
        Assert.Equal("DBC differences", sheets[1].Name);
        var row = Assert.Single(sheets[1].Rows.Where(row => row[3] == "Speed"));
        Assert.Equal("0x100", row[1]);
        Assert.Equal("11-bit", row[2]);
        Assert.Equal("Changed", row[4]);
        Assert.Equal("Decoding", row[5]);
        Assert.Contains("Start bit", row[7]);
        Assert.Contains("factor=0.02", row[11]);
    }

    [Fact]
    public void ExportedWorkbookAndZipAreReadablePackagesAndCanBeOverwritten()
    {
        var before = Parse("""
            BO_ 256 Engine: 8 ECU
             SG_ State : 0|8@1+ (1,0) [0|255] "" Node
            """);
        var after = Parse("""
            BO_ 256 Engine: 8 ECU
             SG_ State : 0|8@1+ (1,0) [0|255] "" Node
            CM_ SG_ 256 State "=WARN,Check"; 
            """);
        var report = DbcDefinitionComparer.Compare(before, after);
        var sheets = DbcDefinitionComparisonExport.BuildSheets(before, after,
            report, "before.dbc", "after.dbc");
        var directory = Path.Combine(Path.GetTempPath(), "rbg-dbc-diff-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var workbook = Path.Combine(directory, "dbc-diff.xlsx");
            var zipPath = Path.Combine(directory, "dbc-diff.zip");
            DbcDefinitionComparisonExport.Write(workbook, sheets, csvZip: false);
            DbcDefinitionComparisonExport.Write(zipPath, sheets, csvZip: true);
            using (var zip = ZipFile.OpenRead(zipPath))
            {
                Assert.Equal(3, zip.Entries.Count);
                var detail = Assert.Single(zip.Entries.Where(e => e.Name == "DBC differences.csv"));
                using var reader = new StreamReader(detail.Open());
                var csv = reader.ReadToEnd();
                Assert.Contains("Changed", csv);
                Assert.Contains("Comment", csv);
            }
            using (var xlsx = ZipFile.OpenRead(workbook))
            {
                var sheet = xlsx.GetEntry("xl/worksheets/sheet2.xml");
                Assert.NotNull(sheet);
                using var stream = sheet.Open();
                Assert.NotNull(XDocument.Load(stream).Root);
            }
            DbcDefinitionComparisonExport.Write(workbook, sheets, csvZip: false);
            DbcDefinitionComparisonExport.Write(zipPath, sheets, csvZip: true);
            Assert.Empty(Directory.GetFiles(directory, "*.partial-*"));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
