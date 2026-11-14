using System.IO.Compression;
using System.Text;
using RbgCanInsight.TraceIO;
using Xunit;

namespace RbgCanInsight.Tests;

public sealed class StructuredCsvArchiveExportTests
{
    [Fact]
    public void EngineeringCsvArchiveKeepsAllTablesAndEscapesSourceMetadataSafely()
    {
        var path = Path.Combine(Path.GetTempPath(),
            "rbg-par-csv-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            var sheets = new[]
            {
                new ExportSheet("Engineering Parameters",
                    ["DID", "PAR", "Depth", "Description", "Coded ID"],
                    [
                        new string[] { "0xF190", "Parent.Child", "2",
                            "Line 1, \"quoted\"\nLine 2", "00123" },
                        new string[] { "0xF191", "=HYPERLINK(\"https://bad.invalid\")",
                            "-3", " \t@SUM(1)", "+18" }
                    ], new HashSet<int> { 2 }),
                new ExportSheet("Engineering PAR Values",
                    ["DID", "Meaning"],
                    [new string[] { "0xF190", "-unsafe label" }]),
                new ExportSheet("PAR Extraction Coverage",
                    ["DID", "Assessment"],
                    [new string[] { "0xF190", "PARTIAL: source has no type" }])
            };
            StructuredCsvArchiveExport.Write(path, sheets);
            using var zip = ZipFile.OpenRead(path);
            Assert.Equal(3, zip.Entries.Count);
            var engineering = ReadEntry(zip, "Engineering Parameters.csv");
            Assert.StartsWith("\"DID\",\"PAR\",\"Depth\",\"Description\",\"Coded ID\"\r\n", engineering);
            Assert.Contains("\"0xF190\",\"Parent.Child\",\"2\",\"Line 1, \"\"quoted\"\"\nLine 2\",\"'00123\"", engineering);
            Assert.Contains("\"0xF191\",\"'=HYPERLINK(\"\"https://bad.invalid\"\")\",\"-3\",\"' \t@SUM(1)\",\"'+18\"", engineering);
            Assert.Contains("\"'-unsafe label\"", ReadEntry(zip, "Engineering PAR Values.csv"));
            Assert.Contains("PARTIAL: source has no type", ReadEntry(zip, "PAR Extraction Coverage.csv"));
            using var stream = zip.GetEntry("Engineering Parameters.csv")!.Open();
            var bom = new byte[3];
            stream.ReadExactly(bom);
            Assert.Equal(Encoding.UTF8.GetPreamble(), bom);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void CsvPartsRepeatHeadersAndCannotCollideWithReservedNames()
    {
        var path = Path.Combine(Path.GetTempPath(),
            "rbg-par-parts-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            var sheets = new[]
            {
                new ExportSheet("Engineering Parameters", ["DID", "PAR"],
                [
                    new string[] { "0xF190", "one" },
                    new string[] { "0xF190", "two" },
                    new string[] { "0xF190", "three" }
                ]),
                new ExportSheet("Engineering Parameters (2)", ["DID", "PAR"],
                    [new string[] { "0xF191", "reserved" }])
            };
            StructuredCsvArchiveExport.Write(path, sheets, maxDataRows: 2);
            using var zip = ZipFile.OpenRead(path);
            Assert.Equal(3, zip.Entries.Count);
            Assert.Equal("\"DID\",\"PAR\"\r\n\"0xF190\",\"one\"\r\n\"0xF190\",\"two\"\r\n",
                ReadEntry(zip, "Engineering Parameters.csv"));
            Assert.Equal("\"DID\",\"PAR\"\r\n\"0xF190\",\"three\"\r\n",
                ReadEntry(zip, "Engineering Parameters (3).csv"));
            Assert.Contains("reserved", ReadEntry(zip, "Engineering Parameters (2).csv"));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void CsvExportRejectsMalformedTablesBeforeWriting()
    {
        var path = Path.Combine(Path.GetTempPath(),
            "rbg-par-invalid-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            Assert.Throws<ArgumentException>(() => StructuredCsvArchiveExport.Write(path,
                [new ExportSheet("Invalid", ["A", "B"], [new string[] { "only one" }])]));
            Assert.False(File.Exists(path));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }


    [Fact]
    public void CsvArchiveUsesVisibleEscapesForInvalidUnicodeInsteadOfNulBytes()
    {
        var path = Path.Combine(Path.GetTempPath(),
            "rbg-par-control-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            StructuredCsvArchiveExport.Write(path,
                [new ExportSheet("Engineering Parameters", ["Description"],
                    [new string[] { "A\u0000B\u0001C\uD800😀" }])]);
            using var zip = ZipFile.OpenRead(path);
            var csv = ReadEntry(zip, "Engineering Parameters.csv");
            Assert.Contains("A\\u0000B\\u0001C\\uD800😀", csv);
            Assert.DoesNotContain('\0', csv);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    private static string ReadEntry(ZipArchive zip, string path)
    {
        using var reader = new StreamReader(zip.GetEntry(path)!.Open(), Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }
}
