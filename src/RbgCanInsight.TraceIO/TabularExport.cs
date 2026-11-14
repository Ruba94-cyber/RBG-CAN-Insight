using System.Collections;
using System.Text;

namespace RbgCanInsight.TraceIO;

public sealed record ExportColumn(string Header, IReadOnlyList<string> Values);

/// <summary>
/// Legacy column-oriented reports use the same bounded, formula-safe OOXML
/// writer as catalog exports. Rows are adapted lazily, without building one
/// giant XML string. Existing destinations are preserved on export failure.
/// </summary>
public static class TabularExport
{
    public static void WriteCsv(string path, IReadOnlyList<ExportColumn> columns)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ValidateColumns(columns);
        var rows = new ColumnRows(columns);
        WriteAtomically(path, temporary =>
        {
            using var writer = new StreamWriter(temporary, false, new UTF8Encoding(true));
            WriteCsvRow(writer, columns.Select(column => column.Header ?? string.Empty).ToArray());
            for (var row = 0; row < rows.Count; row++)
                WriteCsvRow(writer, rows[row]);
        });
    }

    public static void WriteXlsx(string path, string sheetName, IReadOnlyList<ExportColumn> columns)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ValidateColumns(columns);
        // Legacy columns remain strings; a value beginning with '=' is never a formula.
        var sheet = new ExportSheet(sheetName,
            columns.Select(column => column.Header ?? string.Empty).ToArray(),
            new ColumnRows(columns));
        WriteAtomically(path, temporary => StructuredWorkbookExport.Write(temporary, [sheet]));
    }

    private static void ValidateColumns(IReadOnlyList<ExportColumn> columns)
    {
        ArgumentNullException.ThrowIfNull(columns);
        if (columns.Count == 0)
            throw new ArgumentException("At least one export column is required.", nameof(columns));
        foreach (var column in columns)
            if (column is null || column.Values is null)
                throw new ArgumentException("Export columns must contain a non-null value list.", nameof(columns));
    }

    private static void WriteAtomically(string path, Action<string> write)
    {
        var destination = Path.GetFullPath(path);
        var temporary = destination + ".partial-" + Guid.NewGuid().ToString("N");
        try
        {
            write(temporary);
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static void WriteCsvRow(TextWriter writer, IReadOnlyList<string> cells)
    {
        for (var i = 0; i < cells.Count; i++)
        {
            if (i != 0) writer.Write(',');
            var value = ExportTextSafety.EscapeInvalidXmlCharacters(cells[i] ?? string.Empty);
            // An apostrophe inside the quoted field prevents spreadsheet evaluation.
            writer.Write('"');
            if (ExportTextSafety.NeedsSpreadsheetTextProtection(value))
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

    /// <summary>Column-to-row view; short source columns yield empty cells.</summary>
    private sealed class ColumnRows : IReadOnlyList<IReadOnlyList<string>>
    {
        private readonly IReadOnlyList<ExportColumn> _columns;

        public ColumnRows(IReadOnlyList<ExportColumn> columns)
        {
            _columns = columns;
            Count = columns.Max(column => column.Values.Count);
        }

        public int Count { get; }

        public IReadOnlyList<string> this[int index]
        {
            get
            {
                if ((uint)index >= (uint)Count)
                    throw new ArgumentOutOfRangeException(nameof(index));
                var row = new string[_columns.Count];
                for (var col = 0; col < row.Length; col++)
                {
                    var values = _columns[col].Values;
                    row[col] = index < values.Count ? values[index] ?? string.Empty : string.Empty;
                }
                return row;
            }
        }

        public IEnumerator<IReadOnlyList<string>> GetEnumerator()
        {
            for (var row = 0; row < Count; row++) yield return this[row];
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
