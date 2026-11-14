using System.Globalization;
using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;

namespace RbgCanInsight.DiagData;

/// <summary>
/// Reads text/numeric DTC tables directly from Open XML .xlsx workbooks without
/// Excel automation or external libraries. Only actual worksheet cell values
/// are imported; formulas are rejected because cached results can be stale.
/// </summary>
public static class ExcelDtcTableReader
{
    private static readonly XNamespace Spreadsheet =
        "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace OfficeRelationships =
        "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace PackageRelationships =
        "http://schemas.openxmlformats.org/package/2006/relationships";

    private const long MaxPackageBytes = 32L * 1024 * 1024;
    private const long MaxPartBytes = 64L * 1024 * 1024;
    private const long MaxUncompressedBytes = 128L * 1024 * 1024;
    private const int MaxCells = 1_000_000;
    private const int MaxRows = 100_000;
    private const int MaxColumns = 80;

    public static IReadOnlyList<DidCatalog> LoadAll(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var source = Path.GetFullPath(path);
        if (new FileInfo(source).Length > MaxPackageBytes)
            throw new InvalidDataException("Excel DTC workbook exceeds the 32 MiB limit.");

        using var stream = File.OpenRead(source);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        if (zip.Entries.Count > 10_000)
            throw new InvalidDataException("Excel workbook has too many ZIP parts.");
        var total = 0L;
        foreach (var entry in zip.Entries)
        {
            if (entry.Length > MaxPartBytes || entry.Length > MaxUncompressedBytes - total)
                throw new InvalidDataException("Excel workbook exceeds its safe XML expansion limit.");
            total += entry.Length;
        }

        var workbook = ReadXml(Require(zip, "xl/workbook.xml"));
        var rels = ReadXml(Require(zip, "xl/_rels/workbook.xml.rels"));
        var sharedEntry = zip.GetEntry("xl/sharedStrings.xml");
        var shared = sharedEntry is null ? Array.Empty<string>() : ReadSharedStrings(ReadXml(sharedEntry));

        var relationships = rels.Root?.Elements(PackageRelationships + "Relationship")
            .Where(element => ((string?)element.Attribute("Type"))?.EndsWith(
                "/worksheet", StringComparison.OrdinalIgnoreCase) == true)
            .ToDictionary(element => (string?)element.Attribute("Id") ?? string.Empty,
                element => element, StringComparer.Ordinal) ??
                new Dictionary<string, XElement>(StringComparer.Ordinal);

        var sheets = workbook.Root?.Element(Spreadsheet + "sheets")?.
            Elements(Spreadsheet + "sheet").ToArray() ?? [];
        if (sheets.Length == 0)
            throw new InvalidDataException("Excel workbook contains no accessible worksheets.");

        var catalogs = new List<DidCatalog>();
        foreach (var sheet in sheets)
        {
            var sheetName = ((string?)sheet.Attribute("name") ?? string.Empty).Trim();
            var relationId = (string?)sheet.Attribute(OfficeRelationships + "id");
            if (string.IsNullOrWhiteSpace(sheetName) || string.IsNullOrWhiteSpace(relationId) ||
                !relationships.TryGetValue(relationId, out var relation))
                throw new InvalidDataException("Excel workbook contains a worksheet without an internal relationship.");

            if (string.Equals((string?)relation.Attribute("TargetMode"), "External",
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Worksheet '{sheetName}' points outside the workbook.");
            var target = (string?)relation.Attribute("Target") ?? string.Empty;
            var partName = ValidateWorksheetTarget(target);
            var worksheet = ReadXml(Require(zip, partName));
            var (rows, hasFormula, hasError) = ReadRows(worksheet, shared);
            var firstTableRow = rows.FindIndex(row => IsTableHeader(row));
            if (firstTableRow < 0) continue; // cover pages and unrelated reference sheets

            if (hasFormula || hasError)
                throw new InvalidDataException($"DTC worksheet '{sheetName}' includes formula or Excel error cells. " +
                    "Replace them with validated literal values before importing.");
            if (rows.Count - firstTableRow < 2)
                throw new InvalidDataException($"DTC worksheet '{sheetName}' has a header but no data rows.");
            try
            {
                catalogs.AddRange(DtcTableReader.FromRows(source,
                    rows.Skip(firstTableRow).ToArray(), sheetName));
            }
            catch (InvalidDataException ex)
            {
                throw new InvalidDataException($"Excel worksheet '{sheetName}': {ex.Message}", ex);
            }
        }
        if (catalogs.Count == 0)
            throw new InvalidDataException("No DTC table worksheets found. A sheet requires " +
                "a DTC/Code column and Name, Text or Description column.");

        return catalogs;
    }

    private static bool IsTableHeader(string[] cells)
    {
        var normalized = cells.Select(cell => new string(cell
            .Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray())).ToArray();
        var hasCode = normalized.Any(value => value is "DTC" or "DTCCODE" or "DTCHEX" or
            "DTCDECIMAL" or "CODE" or "CODEDECIMAL" or "TROUBLECODE" or "TROUBLECODEDECIMAL" or
            "DTCID" or "DTCNUMBER" or "DTCIDENTIFIER" or "DTCCODEHEX" or "DTCCODEDECIMAL" or
            "FAULTCODE" or "FAULTCODEDECIMAL" or "ERRORCODE" or "DIAGNOSTICTROUBLECODE");
        var hasMeaning = normalized.Any(value => value is "NAME" or "SHORTNAME" or "DTCNAME" or
            "FAULTNAME" or "TEXT" or "DISPLAYTEXT" or "DTCTEXT" or "FAULTTEXT" or "TITLE" or
            "DESCRIPTION" or "FAULTDESCRIPTION" or "DTCDESCRIPTION" or
            "MEANING" or "DTCMEANING" or "DETAILS");
        return hasCode && hasMeaning;
    }

    private static string ValidateWorksheetTarget(string target)
    {
        var name = target.Replace('\\', '/');
        if (name.StartsWith("/", StringComparison.Ordinal)) name = name.TrimStart('/');
        else if (!name.StartsWith("xl/", StringComparison.OrdinalIgnoreCase)) name = "xl/" + name;
        var parts = name.Split('/');
        if (parts.Any(part => part.Length == 0 || part is "." or "..") ||
            !name.StartsWith("xl/worksheets/", StringComparison.OrdinalIgnoreCase) ||
            !name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Unsupported or unsafe Excel worksheet relationship target.");
        return name;
    }

    private static ZipArchiveEntry Require(ZipArchive zip, string name) =>
        zip.GetEntry(name) ?? throw new InvalidDataException($"Excel workbook missing required part '{name}'.");

    private static XDocument ReadXml(ZipArchiveEntry entry)
    {
        using var reader = XmlReader.Create(entry.Open(), new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaxPartBytes
        });
        return XDocument.Load(reader, LoadOptions.None);
    }

    private static string[] ReadSharedStrings(XDocument document)
    {
        var strings = new List<string>();
        foreach (var si in document.Root?.Elements(Spreadsheet + "si") ?? [])
        {
            if (strings.Count >= 200_000)
                throw new InvalidDataException("Excel shared string table exceeds 200,000 entries.");
            var value = string.Concat(si.Descendants(Spreadsheet + "t")
                .Where(text => !text.Ancestors(Spreadsheet + "rPh").Any())
                .Select(text => text.Value));
            if (value.Length > 8192)
                throw new InvalidDataException("Excel shared string exceeds 8192 characters.");
            strings.Add(value);
        }
        return strings.ToArray();
    }

    private static (List<string[]> Rows, bool HasFormula, bool HasError) ReadRows(
        XDocument worksheet, string[] shared)
    {
        var rows = new List<string[]>();
        var hasFormula = false;
        var hasError = false;
        var cellCount = 0;
        var sheetData = worksheet.Root?.Element(Spreadsheet + "sheetData");
        if (sheetData is null) return (rows, false, false);

        foreach (var row in sheetData.Elements(Spreadsheet + "row"))
        {
            if (rows.Count >= MaxRows)
                throw new InvalidDataException("Excel DTC worksheet exceeds 100,000 rows.");
            var cells = new Dictionary<int, string>();
            var nextColumn = 0;
            foreach (var cell in row.Elements(Spreadsheet + "c"))
            {
                if (++cellCount > MaxCells)
                    throw new InvalidDataException("Excel DTC worksheet exceeds 1,000,000 cells.");
                var address = (string?)cell.Attribute("r");
                var column = string.IsNullOrWhiteSpace(address)
                    ? nextColumn : ReadColumn(address);
                nextColumn = column + 1;
                if (cell.Element(Spreadsheet + "f") is not null) hasFormula = true;
                var type = (string?)cell.Attribute("t");
                if (type == "e") hasError = true;
                var raw = cell.Element(Spreadsheet + "v")?.Value ?? string.Empty;
                string value;
                if (type == "s")
                {
                    if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var index)
                        || index < 0 || index >= shared.Length)
                        throw new InvalidDataException("Excel DTC worksheet has an invalid shared-string reference.");
                    value = shared[index];
                }
                else if (type == "inlineStr")
                    value = string.Concat(cell.Element(Spreadsheet + "is")?
                        .Descendants(Spreadsheet + "t").Select(text => text.Value) ??
                        Enumerable.Empty<string>());
                else
                    value = raw;
                if (value.Length > 8192)
                    throw new InvalidDataException("Excel DTC cell exceeds 8192 characters.");
                // Excel commonly serializes styled but empty cells far beyond
                // the actual DTC table. Do not turn formatting into data columns.
                if (string.IsNullOrWhiteSpace(value)) continue;
                if (column is < 0 or >= MaxColumns)
                    throw new InvalidDataException($"Excel DTC worksheet has data outside the supported 80 columns ({address}).");
                if (!cells.TryAdd(column, value))
                    throw new InvalidDataException($"Duplicate Excel DTC cell address in row ({address}).");
            }
            if (cells.Count > 0 && cells.Values.Any(value => !string.IsNullOrWhiteSpace(value)))
            {
                var array = Enumerable.Repeat(string.Empty, cells.Keys.Max() + 1).ToArray();
                foreach (var pair in cells) array[pair.Key] = pair.Value;
                rows.Add(array);
            }
        }
        return (rows, hasFormula, hasError);
    }

    private static int ReadColumn(string cellReference)
    {
        var index = 0;
        var position = 0;
        while (position < cellReference.Length && (cellReference[position] is >= 'A' and <= 'Z' or >= 'a' and <= 'z'))
        {
            index = checked(index * 26 + char.ToUpperInvariant(cellReference[position]) - 'A' + 1);
            position++;
            if (index > MaxColumns) return MaxColumns;
        }
        if (position == 0 || position == cellReference.Length ||
            !int.TryParse(cellReference[position..], NumberStyles.None,
                CultureInfo.InvariantCulture, out var row) || row < 1)
            throw new InvalidDataException($"Invalid Excel cell reference '{cellReference}'.");
        return index - 1;
    }
}
