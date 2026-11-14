using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace RbgCanInsight.TraceIO;

/// <summary>
/// Streams the same catalog tables as the XLSX exporter into a ZIP of UTF-8 CSVs.
/// Each entry is independently openable, including split parts with repeated headers.
/// No original source catalog or existing ZIP is modified by this writer.
/// </summary>
public static class StructuredCsvArchiveExport
{
    private const int ExcelDataRowsPerSheet = 1_048_575;

    /// <summary>
    /// CSV is an interoperability fallback, not a byte-exact round trip for
    /// spreadsheet-looking text. Leading apostrophes prevent Excel from executing
    /// formula-shaped metadata or silently converting identifiers to numbers.
    /// maxDataRows is configurable for small deterministic partition regressions.
    /// </summary>
    public static void Write(string path, IReadOnlyList<ExportSheet> sheets,
        int maxDataRows = ExcelDataRowsPerSheet)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var parts = StructuredWorkbookExport.PlanWorksheetParts(sheets, maxDataRows);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var part in parts)
        {
            var source = sheets[part.SourceSheetIndex];
            var entry = archive.CreateEntry(part.Name + ".csv", CompressionLevel.Optimal);
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(true));
            WriteRow(writer, source.Headers, null);
            for (var row = 0; row < part.RowCount; row++)
                WriteRow(writer, source.Rows[part.StartRow + row], source.NumericColumns);
        }
    }

    private static void WriteRow(TextWriter writer, IReadOnlyList<string> cells,
        IReadOnlySet<int>? numericColumns)
    {
        for (var col = 0; col < cells.Count; col++)
        {
            if (col > 0) writer.Write(',');
            var value = ExportTextSafety.EscapeInvalidXmlCharacters(cells[col] ?? string.Empty);
            var explicitNumber = numericColumns?.Contains(col) == true &&
                value.Length <= 15 &&
                double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture,
                    out var number) && double.IsFinite(number);
            // Keep the text marker inside the quoted CSV field; placing it
            // before the opening quote would corrupt RFC 4180 field boundaries.
            // All fields are quoted: embedded commas, CR/LF and double quotes
            // remain one logical cell instead of shifting the PAR hierarchy.
            writer.Write('"');
            if (!explicitNumber && ExportTextSafety.NeedsSpreadsheetTextProtection(value))
                writer.Write('\'');
            foreach (var character in value)
            {
                if (character == '"') writer.Write('"');
                writer.Write(character);
            }
            writer.Write('"');
        }
        writer.Write("\r\n");
    }

}
