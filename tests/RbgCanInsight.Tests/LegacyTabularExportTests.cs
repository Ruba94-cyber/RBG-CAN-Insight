using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using RbgCanInsight.TraceIO;
using Xunit;

namespace RbgCanInsight.Tests;

public sealed class LegacyTabularExportTests
{
    private static string TempFolder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "rbg-tabular-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }

    [Fact]
    public void LegacyCsvProtectsFormulaIdentifiersAndEscapesQuotesAndNewlines()
    {
        var folder = TempFolder();
        try
        {
            var path = Path.Combine(folder, "legacy.csv");
            TabularExport.WriteCsv(path,
            [
                new ExportColumn("Source", [
                    "=HYPERLINK(\"https://example.invalid\")", " \t@SUM(1)", "A\u0000B"
                ]),
                new ExportColumn("Value", [
                    "00123", "Line 1, \"quoted\"\r\nLine 2", "😀"
                ])
            ]);

            var bytes = File.ReadAllBytes(path);
            Assert.Equal(Encoding.UTF8.GetPreamble(), bytes[..3]);
            var csv = File.ReadAllText(path, Encoding.UTF8);
            Assert.StartsWith("\"Source\",\"Value\"\r\n", csv);
            Assert.Contains("\"'=HYPERLINK(\"\"https://example.invalid\"\")\"", csv);
            Assert.Contains("\"'00123\"", csv);
            Assert.Contains("\"' \t@SUM(1)\"", csv);
            Assert.Contains("\"Line 1, \"\"quoted\"\"\r\nLine 2\"", csv);
            Assert.Contains("\"A\\u0000B\",\"😀\"", csv);
            Assert.DoesNotContain('\0', csv);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void LegacyXlsxUsesSafeStringsAndEscapedWorksheetMetadata()
    {
        var folder = TempFolder();
        try
        {
            var path = Path.Combine(folder, "signals.xlsx");
            TabularExport.WriteXlsx(path, "A&B report",
            [
                new ExportColumn("DID", ["0xF190", "=2+2", "A\u0001B\uD800"]),
                new ExportColumn("State", ["00123", "A<&>\"B", "😀"])
            ]);

            using var zip = ZipFile.OpenRead(path);
            using var workbookStream = zip.GetEntry("xl/workbook.xml")!.Open();
            var workbook = XDocument.Load(workbookStream);
            XNamespace main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            Assert.Equal("A&B report", workbook.Descendants(main + "sheet").Single()
                .Attribute("name")!.Value);

            using var worksheetStream = zip.GetEntry("xl/worksheets/sheet1.xml")!.Open();
            var worksheet = XDocument.Load(worksheetStream);
            Assert.Empty(worksheet.Descendants(main + "f"));
            var cells = worksheet.Descendants(main + "c").ToDictionary(
                cell => cell.Attribute("r")!.Value, cell => cell);
            Assert.Equal("inlineStr", cells["A3"].Attribute("t")!.Value);
            Assert.Equal("=2+2", cells["A3"].Descendants(main + "t").Single().Value);
            Assert.Equal("00123", cells["B2"].Descendants(main + "t").Single().Value);
            Assert.Equal("A<&>\"B", cells["B3"].Descendants(main + "t").Single().Value);
            Assert.Equal("A\\u0001B\\uD800", cells["A4"].Descendants(main + "t").Single().Value);
            Assert.Equal("😀", cells["B4"].Descendants(main + "t").Single().Value);
            Assert.Equal(4, worksheet.Descendants(main + "row").Count());
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void DifferentColumnLengthsKeepEmptyCellsAndSourceLists()
    {
        var folder = TempFolder();
        try
        {
            var path = Path.Combine(folder, "uneven.xlsx");
            var shortValues = new[] { "only" };
            var longValues = new[] { "one", "two", "three" };
            TabularExport.WriteXlsx(path, "Uneven",
            [
                new ExportColumn("Short", shortValues),
                new ExportColumn("Long", longValues)
            ]);

            Assert.Equal(new[] { "only" }, shortValues);
            Assert.Equal(new[] { "one", "two", "three" }, longValues);
            using var zip = ZipFile.OpenRead(path);
            using var stream = zip.GetEntry("xl/worksheets/sheet1.xml")!.Open();
            var worksheet = XDocument.Load(stream);
            XNamespace main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            var cell = worksheet.Descendants(main + "c")
                .Single(item => item.Attribute("r")?.Value == "A4");
            Assert.Equal(string.Empty, cell.Descendants(main + "t").Single().Value);
            Assert.Equal(4, worksheet.Descendants(main + "row").Count());
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void RejectedWorksheetNamePreservesExistingReport()
    {
        var folder = TempFolder();
        try
        {
            var path = Path.Combine(folder, "existing.xlsx");
            File.WriteAllText(path, "preserved");
            Assert.Throws<ArgumentException>(() =>
                TabularExport.WriteXlsx(path, "[invalid]",
                    [new ExportColumn("A", ["value"])]));
            Assert.Equal("preserved", File.ReadAllText(path));
            Assert.Single(Directory.GetFiles(folder));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }
}
