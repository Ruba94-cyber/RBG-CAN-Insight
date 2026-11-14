using System.IO.Compression;
using System.Xml.Linq;
using RbgCanInsight.DiagData;
using RbgCanInsight.TraceIO;
using Xunit;

namespace RbgCanInsight.Tests;

public sealed class MappingComparisonExportTests
{
    [Fact]
    public void ComparisonExportContainsReviewStateAndOriginalEvidenceColumns()
    {
        var before = new DidSignalMapping(0xF405, "Temperature", "ECM",
            "powertrain", "Engine", "Temp", 0.91, "high metadata agreement",
            MappingStatus.Confirmed, "Inspected by analyst", 256, false,
            "sem-v1:did-old", "sem-v1:can-old");
        var after = before with
        {
            CanSignalFingerprint = "sem-v1:can-new",
            Status = MappingStatus.ToVerify,
            Notes = "=HYPERLINK(\"https://example.invalid\",\"unsafe\")"
        };
        var previous = new MappingDocument("Before", DateTimeOffset.UnixEpoch, [before]);
        var current = new MappingDocument("After", DateTimeOffset.UnixEpoch, [after]);
        var comparison = MappingDocumentComparer.Compare(previous, current);
        var sheets = MappingComparisonExport.BuildSheets(previous, current,
            comparison, "/var/before.rbgmap.json", "/var/after.rbgmap.json");
        Assert.Equal(2, sheets.Count);
        Assert.Contains("Needs review", sheets[1].Headers);
        Assert.Contains("Current CAN fingerprint", sheets[1].Headers);
        Assert.Equal(25, sheets[1].Headers.Count);
        var detail = Assert.Single(sheets[1].Rows);
        Assert.Equal("0xF405", detail[0]);
        Assert.Equal("YES", detail[3]);
        Assert.Equal("Changed", detail[2]);
        Assert.Equal("sem-v1:can-new", detail[18]);
        Assert.Equal(after.Notes, detail[22]);
        Assert.Equal("before.rbgmap.json", sheets[0].Rows[0][1]);
    }

    [Fact]
    public void ComparisonSheetsCanBeWrittenToBothXlsxAndQuotedCsvArchive()
    {
        var item = new DidSignalMapping(0xF190, "VIN", "BCM",
            "body", "Identity", "VIN", 0.8, "metadata",
            MappingStatus.ToVerify, "+bad cell, with comma", 128, false,
            "sem-v1:did", "sem-v1:can");
        var baseline = new MappingDocument("A", DateTimeOffset.UnixEpoch, [item]);
        var current = new MappingDocument("B", DateTimeOffset.UnixEpoch,
            [item with { Notes = "=1+2" }]);
        var sheets = MappingComparisonExport.BuildSheets(baseline, current,
            MappingDocumentComparer.Compare(baseline, current), "a.json", "b.json");
        var dir = Path.Combine(Path.GetTempPath(), "rbg-map-diff-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var workbook = Path.Combine(dir, "diff.xlsx");
            var archivePath = Path.Combine(dir, "diff.zip");
            MappingComparisonExport.Write(workbook, sheets, csvZip: false);
            MappingComparisonExport.Write(archivePath, sheets, csvZip: true);
            using (var zip = ZipFile.OpenRead(archivePath))
            {
                var entry = Assert.Single(zip.Entries.Where(part => part.Name == "Map comparison.csv"));
                using var reader = new StreamReader(entry.Open());
                var csv = reader.ReadToEnd();
                Assert.Contains("'=1+2", csv);
                Assert.Contains("Comparison", csv);
                Assert.Contains("0xF190", csv);
            }
            using (var xlsx = ZipFile.OpenRead(workbook))
            {
                Assert.NotNull(xlsx.GetEntry("xl/worksheets/sheet1.xml"));
                using var xmlStream = xlsx.GetEntry("xl/worksheets/sheet2.xml")!.Open();
                var sheetXml = XDocument.Load(xmlStream);
                Assert.NotNull(sheetXml.Root);
                Assert.Contains(sheetXml.Descendants(),
                    element => element.Name.LocalName == "t" && element.Value == "Comparison");
            }
            // Re-export an updated snapshot into the same destination. The
            // original report is replaced, never appended to or truncated.
            var updated = current with { Title = "Updated" };
            var revised = MappingComparisonExport.BuildSheets(baseline, updated,
                MappingDocumentComparer.Compare(baseline, updated), "a.json", "new.json");
            MappingComparisonExport.Write(workbook, revised, csvZip: false);
            MappingComparisonExport.Write(archivePath, revised, csvZip: true);
            using (var overwritten = ZipFile.OpenRead(archivePath))
            {
                var summary = Assert.Single(overwritten.Entries.Where(entry =>
                    entry.Name == "Comparison summary.csv"));
                using var text = new StreamReader(summary.Open());
                Assert.Contains("new.json", text.ReadToEnd());
            }
            Assert.Empty(Directory.GetFiles(dir, "*.partial-*"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
