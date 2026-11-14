using System.Collections;
using System.IO.Compression;
using System.Xml.Linq;
using RbgCanInsight.TraceIO;
using Xunit;

namespace RbgCanInsight.Tests;

public sealed class StructuredWorkbookPartitionTests
{
    [Fact]
    public void PartPlannerSplitsLargeLogicalSheetsWithHeaderCapacityReserved()
    {
        var source = new ExportSheet("Engineering Parameters", ["DID", "Parameter"],
            new RepeatedRows(1_048_576, ["0xF190", "Parameter"]));
        var parts = StructuredWorkbookExport.PlanWorksheetParts([source]);
        Assert.Equal(2, parts.Count);
        Assert.Equal("Engineering Parameters", parts[0].Name);
        Assert.Equal(0, parts[0].StartRow);
        Assert.Equal(1_048_575, parts[0].RowCount);
        Assert.Equal("Engineering Parameters (2)", parts[1].Name);
        Assert.Equal(1_048_575, parts[1].StartRow);
        Assert.Equal(1, parts[1].RowCount);
    }

    [Fact]
    public void PlannerAvoidsCollisionsWithAllOriginalSheetNamesAndKeepsOrder()
    {
        var first = new ExportSheet("Engineering Parameters", ["DID"],
            new RepeatedRows(5, ["0xF190"]));
        var reserved = new ExportSheet("Engineering Parameters (2)", ["DID"],
            new RepeatedRows(1, ["0xF191"]));
        var parts = StructuredWorkbookExport.PlanWorksheetParts([first, reserved], maxDataRows: 2);
        Assert.Equal(4, parts.Count);
        Assert.Equal(new[] { "Engineering Parameters", "Engineering Parameters (3)",
                "Engineering Parameters (4)", "Engineering Parameters (2)" },
            parts.Select(part => part.Name).ToArray());
        Assert.Equal(new[] { 2, 2, 1, 1 }, parts.Select(part => part.RowCount).ToArray());
        Assert.Equal(new[] { 0, 2, 4, 0 }, parts.Select(part => part.StartRow).ToArray());
        Assert.Equal(new[] { 0, 0, 0, 1 }, parts.Select(part => part.SourceSheetIndex).ToArray());
    }

    [Fact]
    public void PlannerSupportsEmptySheetsAndNameLengthTruncation()
    {
        var source = new ExportSheet("1234567890123456789012345678901", ["A"],
            new RepeatedRows(3, ["value"]));
        var parts = StructuredWorkbookExport.PlanWorksheetParts([source], maxDataRows: 2);
        Assert.Equal(2, parts.Count);
        Assert.Equal(31, parts[1].Name.Length);
        Assert.EndsWith(" (2)", parts[1].Name);
        Assert.Empty(StructuredWorkbookExport.PlanWorksheetParts(
            [new ExportSheet("Empty", ["A"], new RepeatedRows(0, ["value"]))])
            .Skip(1));
    }

    [Fact]
    public void SmallWorkbookKeepsTypedColumnsAndSafeTextUnderPartPlanner()
    {
        var path = Path.Combine(Path.GetTempPath(),
            "rbg-parted-export-" + Guid.NewGuid().ToString("N") + ".xlsx");
        try
        {
            StructuredWorkbookExport.Write(path,
            [
                new ExportSheet("Engineering Parameters", ["DID", "Level", "Parameter"],
                    [new string[] { "0xF190", "2", "=HYPERLINK(\"bad\")" }],
                    new HashSet<int> { 1 }),
                new ExportSheet("PAR Extraction Coverage", ["DID", "Source nodes"],
                    [new string[] { "0xF190", "1" }], new HashSet<int> { 1 })
            ]);
            using var zip = ZipFile.OpenRead(path);
            Assert.NotNull(zip.GetEntry("xl/worksheets/sheet1.xml"));
            Assert.NotNull(zip.GetEntry("xl/worksheets/sheet2.xml"));
            using var reader = zip.GetEntry("xl/worksheets/sheet1.xml")!.Open();
            var xml = XDocument.Load(reader);
            XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            Assert.Empty(xml.Descendants(ns + "f"));
            Assert.Contains(xml.Descendants(ns + "c"), cell =>
                (string?)cell.Attribute("t") == "n" && (string?)cell.Element(ns + "v") == "2");
            Assert.Contains(xml.Descendants(ns + "t"), cell =>
                cell.Value == "=HYPERLINK(\"bad\")");
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }


    [Fact]
    public void WorkbookEscapesXmlInvalidMetadataWithoutDroppingValidEmojiOrLineBreaks()
    {
        var path = Path.Combine(Path.GetTempPath(),
            "rbg-par-xml-control-" + Guid.NewGuid().ToString("N") + ".xlsx");
        try
        {
            StructuredWorkbookExport.Write(path,
                [new ExportSheet("Engineering Parameters", ["Name", "Description"],
                    [new string[] { "0xF190", "A\u0000B\u0001C\uD800😀\nline" }])]);
            using var zip = ZipFile.OpenRead(path);
            using var stream = zip.GetEntry("xl/worksheets/sheet1.xml")!.Open();
            var xml = XDocument.Load(stream);
            XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            Assert.Contains(xml.Descendants(ns + "t"), text =>
                text.Value == "A\\u0000B\\u0001C\\uD800😀\nline");
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Theory]
    [InlineData("'Leading")]
    [InlineData("Trailing'")]
    [InlineData("Contains\u0001Control")]
    public void WorkbookRejectsInvalidWorksheetNamesBeforeWriting(string name)
    {
        var path = Path.Combine(Path.GetTempPath(),
            "rbg-par-invalid-sheet-" + Guid.NewGuid().ToString("N") + ".xlsx");
        try
        {
            Assert.Throws<ArgumentException>(() => StructuredWorkbookExport.Write(path,
                [new ExportSheet(name, ["DID"], [new string[] { "0xF190" }])]));
            Assert.False(File.Exists(path));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    private sealed class RepeatedRows(int count, IReadOnlyList<string> row)
        : IReadOnlyList<IReadOnlyList<string>>
    {
        public int Count => count;
        public IReadOnlyList<string> this[int index] =>
            index >= 0 && index < count ? row : throw new ArgumentOutOfRangeException(nameof(index));
        public IEnumerator<IReadOnlyList<string>> GetEnumerator()
        {
            for (var i = 0; i < count; i++) yield return row;
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
