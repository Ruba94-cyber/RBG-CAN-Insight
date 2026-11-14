using System.IO.Compression;
using System.Xml.Linq;
using RbgCanInsight.DiagData;
using RbgCanInsight.TraceIO;
using Xunit;

namespace RbgCanInsight.Tests;

public sealed class DiagnosticConditionsExportTests
{
    private static string OdxText => typeof(DiagnosticConditionsTests)
        .GetField("Odx", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
        .GetRawConstantValue()!.ToString()!;

    private static DidCatalog LoadOdx() =>
        Assert.Single(OdxCatalogReader.LoadFromDocuments([XDocument.Parse(OdxText)], "ecm.odx"));

    [Fact]
    public void DocumentationListsDeclaredConditionsLayoutsAndStatesWhatIsMissing()
    {
        var sheets = DiagnosticConditionsExport.CreateSheets([LoadOdx()]);
        var definitions = sheets.Single(sheet => sheet.Name == "Definitions");
        var conditions = sheets.Single(sheet => sheet.Name == "Declared conditions");
        var about = sheets.Single(sheet => sheet.Name == "About");

        Assert.Equal(3, conditions.Rows.Count(row => row[3] == "Calibrate"));
        var calibrate = definitions.Rows.Single(row => row[3] == "0x0203");
        Assert.Equal("Routine", calibrate[2]);
        Assert.Equal("Extended", calibrate[7]);
        Assert.Equal("Level_01", calibrate[8]);
        Assert.Equal("Standstill", calibrate[9]);
        Assert.Equal("Pre-conditions declared in the file", calibrate[10]);
        Assert.Contains("31 <01 start|02 stop|03 results> 02 03", calibrate[5]);

        var read = definitions.Rows.Single(row => row[3] == "0xF405");
        Assert.Equal("DID read", read[2]);
        Assert.Contains("22 F4 05", read[5]);
        Assert.Contains("does not prove none is required", read[10]);          // no condition ≠ no requirement
        Assert.Contains(about.Rows, row => row[0] == "Meaning of 'none'");
        Assert.Contains(about.Rows, row => row[0] == "Negative responses");
    }

    [Fact]
    public void CddCatalogsAreMarkedAsNotExtractedInsteadOfAsUnconditional()
    {
        var cdd = new DidCatalog("c", "BodyEcu", @"C:\data\body.cdd",
            [new DidParameter(0xF190, "Vin", "", "", "", null, null, null, null, "", null, "BodyEcu", "body.cdd")]);
        var definitions = DiagnosticConditionsExport.CreateSheets([cdd]).Single(sheet => sheet.Name == "Definitions");
        var row = Assert.Single(definitions.Rows);
        Assert.StartsWith("Not extracted: CDD", row[10]);
        Assert.Equal(string.Empty, row[7]);
    }

    [Fact]
    public void SheetsCanBeWrittenAsWorkbookAndCsvArchive()
    {
        var sheets = DiagnosticConditionsExport.CreateSheets([LoadOdx()]);
        var folder = Path.Combine(Path.GetTempPath(), "rbg-cond-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var xlsx = Path.Combine(folder, "conditions.xlsx");
            var zip = Path.Combine(folder, "conditions.zip");
            StructuredWorkbookExport.Write(xlsx, sheets);
            StructuredCsvArchiveExport.Write(zip, sheets);
            Assert.True(new FileInfo(xlsx).Length > 1000);
            using var archive = ZipFile.OpenRead(zip);
            Assert.Contains(archive.Entries, entry => entry.Name == "Definitions.csv");
            using var reader = new StreamReader(archive.GetEntry("Declared conditions.csv")?.Open() ??
                archive.Entries.First(entry => entry.Name.StartsWith("Declared")).Open());
            Assert.Contains("Calibrate", reader.ReadToEnd());
        }
        finally { Directory.Delete(folder, true); }
    }
}
