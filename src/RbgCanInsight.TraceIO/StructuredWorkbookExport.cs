using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;

namespace RbgCanInsight.TraceIO;

/// <summary>A catalog definition worksheet. Numeric columns are explicit; CAN IDs and
/// hexadecimal identifiers must remain strings to preserve exact identity.</summary>
public sealed record ExportSheet(
    string Name,
    IReadOnlyList<string> Headers,
    IReadOnlyList<IReadOnlyList<string>> Rows,
    IReadOnlySet<int>? NumericColumns = null);

/// <summary>Streamed, read-only OOXML export without Excel or external package dependencies.
/// Creates independent worksheets, headers, AutoFilter and a frozen header row.</summary>
public static class StructuredWorkbookExport
{
    private const string ContentTypeNs = "http://schemas.openxmlformats.org/package/2006/content-types";
    private const string MainNs = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string RelNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string PackageRelNs = "http://schemas.openxmlformats.org/package/2006/relationships";
    private const int MaxExcelRows = 1_048_576;
    private const int MaxExcelColumns = 16_384;
    private const int MaxCellLength = 32_767;

    /// <summary>Physical worksheet segment; StartRow is a zero-based data-row offset
    /// (the header is written separately to every segment).</summary>
    public sealed record WorksheetPart(int SourceSheetIndex, string Name, int StartRow, int RowCount);

    /// <summary>
    /// Split large logical sheets without copying data rows. A separate public planner
    /// allows small boundary tests; Write always uses Excel's actual row limit.
    /// Original sheet names are reserved so generated part names cannot hijack them.
    /// </summary>
    public static IReadOnlyList<WorksheetPart> PlanWorksheetParts(
        IReadOnlyList<ExportSheet> sheets, int maxDataRows = MaxExcelRows - 1)
    {
        ArgumentNullException.ThrowIfNull(sheets);
        if (sheets.Count == 0)
            throw new ArgumentException("At least one worksheet is required.", nameof(sheets));
        if (maxDataRows is < 1 or >= MaxExcelRows)
            throw new ArgumentOutOfRangeException(nameof(maxDataRows));
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sheet in sheets)
        {
            if (string.IsNullOrWhiteSpace(sheet.Name) || sheet.Name.Length > 31 ||
                sheet.Name.IndexOfAny([':', '\\', '/', '?', '*', '[', ']']) >= 0 ||
                sheet.Name[0] == '\'' || sheet.Name[^1] == '\'' ||
                ExportTextSafety.EscapeInvalidXmlCharacters(sheet.Name) != sheet.Name ||
                !usedNames.Add(sheet.Name))
                throw new ArgumentException(
                    "Worksheet names must be unique, valid and at most 31 characters.", nameof(sheets));
            if (sheet.Headers.Count is < 1 or > MaxExcelColumns)
                throw new ArgumentException("Worksheet exceeds Excel column limits.", nameof(sheets));
            if (sheet.NumericColumns?.Any(index => index < 0 || index >= sheet.Headers.Count) == true)
                throw new ArgumentException("Numeric column index is outside the worksheet.", nameof(sheets));
            if (sheet.Rows.Any(row => row.Count != sheet.Headers.Count))
                throw new ArgumentException("Worksheet row width differs from its headers.", nameof(sheets));
        }

        var parts = new List<WorksheetPart>();
        for (var index = 0; index < sheets.Count; index++)
        {
            var sheet = sheets[index];
            var partNumber = 1;
            for (long start = 0; start < Math.Max(1, sheet.Rows.Count); start += maxDataRows)
            {
                var count = (int)Math.Min(maxDataRows, Math.Max(0, (long)sheet.Rows.Count - start));
                var name = sheet.Name;
                if (partNumber > 1)
                {
                    // Reserve all original names first. A generated suffix must
                    // never collide with a different user-selected worksheet.
                    do
                    {
                        var suffix = " (" + partNumber.ToString(CultureInfo.InvariantCulture) + ")";
                        name = sheet.Name[..Math.Min(sheet.Name.Length, 31 - suffix.Length)] + suffix;
                        partNumber++;
                    }
                    while (!usedNames.Add(name));
                }
                else partNumber++;

                parts.Add(new WorksheetPart(index, name, (int)start, count));
                if (parts.Count > 512)
                    throw new InvalidDataException("Workbook would exceed the 512-sheet safety limit.");
            }
        }
        return parts;
    }

    private sealed class RowWindow(
        IReadOnlyList<IReadOnlyList<string>> source, int start, int count)
        : IReadOnlyList<IReadOnlyList<string>>
    {
        public int Count => count;
        public IReadOnlyList<string> this[int index] =>
            index >= 0 && index < count
                ? source[start + index]
                : throw new ArgumentOutOfRangeException(nameof(index));

        public IEnumerator<IReadOnlyList<string>> GetEnumerator()
        {
            for (var i = 0; i < count; i++) yield return source[start + i];
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() =>
            GetEnumerator();
    }

    public static void Write(string path, IReadOnlyList<ExportSheet> sheets)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var parts = PlanWorksheetParts(sheets);
        var physicalSheets = parts.Select(part =>
        {
            var source = sheets[part.SourceSheetIndex];
            return new ExportSheet(part.Name, source.Headers,
                new RowWindow(source.Rows, part.StartRow, part.RowCount),
                source.NumericColumns);
        }).ToArray();

        // The UI writes to a temporary path and promotes only a completed ZIP.
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        WriteXml(zip, "[Content_Types].xml", x =>
        {
            x.WriteStartElement("Types", ContentTypeNs);
            x.WriteStartElement("Default", ContentTypeNs); x.WriteAttributeString("Extension", "rels");
            x.WriteAttributeString("ContentType", "application/vnd.openxmlformats-package.relationships+xml");
            x.WriteEndElement();
            x.WriteStartElement("Default", ContentTypeNs); x.WriteAttributeString("Extension", "xml");
            x.WriteAttributeString("ContentType", "application/xml"); x.WriteEndElement();
            WriteOverride(x, "/xl/workbook.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml");
            for (var i = 0; i < physicalSheets.Length; i++)
                WriteOverride(x, $"/xl/worksheets/sheet{i + 1}.xml",
                    "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml");
            x.WriteEndElement();
        });
        WriteXml(zip, "_rels/.rels", x =>
        {
            x.WriteStartElement("Relationships", PackageRelNs);
            WriteRelationship(x, "rId1", RelNs + "/officeDocument", "xl/workbook.xml");
            x.WriteEndElement();
        });
        WriteXml(zip, "xl/_rels/workbook.xml.rels", x =>
        {
            x.WriteStartElement("Relationships", PackageRelNs);
            for (var i = 0; i < physicalSheets.Length; i++)
                WriteRelationship(x, $"rId{i + 1}", RelNs + "/worksheet", $"worksheets/sheet{i + 1}.xml");
            x.WriteEndElement();
        });
        WriteXml(zip, "xl/workbook.xml", x =>
        {
            x.WriteStartElement("workbook", MainNs);
            x.WriteAttributeString("xmlns", "r", null, RelNs);
            x.WriteStartElement("sheets", MainNs);
            for (var i = 0; i < physicalSheets.Length; i++)
            {
                x.WriteStartElement("sheet", MainNs);
                x.WriteAttributeString("name", physicalSheets[i].Name);
                x.WriteAttributeString("sheetId", (i + 1).ToString(CultureInfo.InvariantCulture));
                x.WriteAttributeString("r", "id", RelNs, $"rId{i + 1}");
                x.WriteEndElement();
            }
            x.WriteEndElement();
            x.WriteEndElement();
        });
        for (var i = 0; i < physicalSheets.Length; i++)
        {
            var sheet = physicalSheets[i];
            WriteXml(zip, $"xl/worksheets/sheet{i + 1}.xml", x =>
            {
                x.WriteStartElement("worksheet", MainNs);
                x.WriteStartElement("sheetViews", MainNs);
                x.WriteStartElement("sheetView", MainNs);
                x.WriteAttributeString("workbookViewId", "0");
                x.WriteStartElement("pane", MainNs);
                x.WriteAttributeString("ySplit", "1");
                x.WriteAttributeString("topLeftCell", "A2");
                x.WriteAttributeString("activePane", "bottomLeft");
                x.WriteAttributeString("state", "frozen");
                x.WriteEndElement();
                x.WriteEndElement();
                x.WriteEndElement();

                x.WriteStartElement("sheetData", MainNs);
                WriteRow(x, 1, sheet.Headers, null);
                for (var row = 0; row < sheet.Rows.Count; row++)
                    WriteRow(x, row + 2, sheet.Rows[row], sheet.NumericColumns);
                x.WriteEndElement();
                x.WriteStartElement("autoFilter", MainNs);
                x.WriteAttributeString("ref", $"A1:{ColumnLetters(sheet.Headers.Count)}{sheet.Rows.Count + 1}");
                x.WriteEndElement();
                x.WriteEndElement();
            });
        }
    }

    private static void WriteRow(XmlWriter x, int index, IReadOnlyList<string> values,
        IReadOnlySet<int>? numeric)
    {
        x.WriteStartElement("row", MainNs);
        x.WriteAttributeString("r", index.ToString(CultureInfo.InvariantCulture));
        for (var col = 0; col < values.Count; col++)
        {
            var value = values[col] ?? string.Empty;
            // Invalid XML control characters can legitimately survive as metadata
            // in third-party DBC files. Keep their hexadecimal code visible rather
            // than leaving a corrupt/partial XLSX or silently dropping characters.
            var safeValue = ExportTextSafety.EscapeInvalidXmlCharacters(value);
            if (safeValue.Length > MaxCellLength)
                throw new InvalidDataException("Catalog cell exceeds Excel's 32,767-character limit.");
            x.WriteStartElement("c", MainNs);
            x.WriteAttributeString("r", ColumnLetters(col + 1) + index.ToString(CultureInfo.InvariantCulture));
            if (numeric?.Contains(col) == true &&
                double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) &&
                double.IsFinite(number) && value.Length <= 15)
            {
                x.WriteAttributeString("t", "n");
                x.WriteElementString("v", MainNs, value.Trim());
            }
            else
            {
                // Inline string is never a formula, even for user-defined values starting '='.
                x.WriteAttributeString("t", "inlineStr");
                x.WriteStartElement("is", MainNs);
                x.WriteStartElement("t", MainNs);
                x.WriteAttributeString("xml", "space", "http://www.w3.org/XML/1998/namespace", "preserve");
                x.WriteString(safeValue);
                x.WriteEndElement();
                x.WriteEndElement();
            }
            x.WriteEndElement();
        }
        x.WriteEndElement();
    }

    private static string ColumnLetters(int number)
    {
        var result = new StringBuilder();
        while (number > 0)
        {
            number--;
            result.Insert(0, (char)('A' + number % 26));
            number /= 26;
        }
        return result.ToString();
    }

    private static void WriteOverride(XmlWriter x, string part, string contentType)
    {
        x.WriteStartElement("Override", ContentTypeNs);
        x.WriteAttributeString("PartName", part);
        x.WriteAttributeString("ContentType", contentType);
        x.WriteEndElement();
    }

    private static void WriteRelationship(XmlWriter x, string id, string type, string target)
    {
        x.WriteStartElement("Relationship", PackageRelNs);
        x.WriteAttributeString("Id", id);
        x.WriteAttributeString("Type", type);
        x.WriteAttributeString("Target", target);
        x.WriteEndElement();
    }

    private static void WriteXml(ZipArchive zip, string path, Action<XmlWriter> action)
    {
        using var stream = zip.CreateEntry(path, CompressionLevel.Optimal).Open();
        using var writer = XmlWriter.Create(stream, new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(false), CloseOutput = false, Indent = false
        });
        writer.WriteStartDocument();
        action(writer);
        writer.WriteEndDocument();
    }
}
