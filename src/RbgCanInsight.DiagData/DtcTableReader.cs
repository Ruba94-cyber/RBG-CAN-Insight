using System.Globalization;
using System.Text;

namespace RbgCanInsight.DiagData;

/// <summary>Offline UTF-8 DTC-table importer. Descriptions are ECU-scoped metadata, never
/// a substitute for observed 0x19 statuses or CDD/ODX snapshot field layouts.</summary>
public static class DtcTableReader
{
    public static IReadOnlyList<DidCatalog> LoadAll(string path)
    {
        var source = Path.GetFullPath(path);
        if (new FileInfo(source).Length > 16 * 1024 * 1024)
            throw new InvalidDataException("DTC table exceeds 16 MiB.");
        var extension = Path.GetExtension(source).ToLowerInvariant();
        if (extension is not (".csv" or ".tsv"))
            throw new NotSupportedException("DTC tables require UTF-8 CSV or TSV.");
        var text = File.ReadAllText(source);
        var delimiter = extension == ".tsv" ? '\t' : DetectDelimiter(text);
        var rows = ReadRows(text, delimiter);
        return FromRows(source, rows);
    }

    /// <summary>Common validation and ECU reconciliation for delimited and native Excel tables.
    /// A nonempty worksheet name identifies the exact workbook sheet that supplied each definition.</summary>
    internal static IReadOnlyList<DidCatalog> FromRows(string source,
        IReadOnlyList<string[]> rows, string sheetName = "")
    {
        if (rows.Count < 2)
            throw new InvalidDataException("DTC table requires a header and at least one row.");

        var headers = rows[0].Select(value => new string(value
            .Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray())).ToArray();
        int Find(params string[] names) => Array.FindIndex(headers,
            name => names.Contains(name, StringComparer.Ordinal));
        var codeColumn = Find("DTC", "DTCCODE", "DTCHEX", "DTCDECIMAL",
            "CODE", "CODEDECIMAL", "TROUBLECODE", "TROUBLECODEDECIMAL",
            "DTCID", "DTCNUMBER", "DTCIDENTIFIER", "DTCCODEHEX", "DTCCODEDECIMAL",
            "FAULTCODE", "FAULTCODEDECIMAL", "ERRORCODE", "DIAGNOSTICTROUBLECODE");
        var ecuColumn = Find("ECU", "ECUNAME", "ECUVARIANT", "ECUID", "MODULE", "CONTROLUNIT");
        var nameColumn = Find("NAME", "SHORTNAME", "DTCNAME", "FAULTNAME");
        var titleColumn = Find("TEXT", "DISPLAYTEXT", "DTCTEXT", "FAULTTEXT", "TITLE");
        var descriptionColumn = Find("DESCRIPTION", "FAULTDESCRIPTION", "DTCDESCRIPTION", "MEANING",
            "DTCMEANING", "DETAILS");
        if (codeColumn < 0 || nameColumn < 0 && titleColumn < 0 && descriptionColumn < 0)
            throw new InvalidDataException("DTC table requires Code/DTC and Name, Text or Description columns.");
        var isDecimal = headers[codeColumn].EndsWith("DECIMAL", StringComparison.Ordinal);
        var primaryColumns = new HashSet<int>(new[]
        {
            codeColumn, ecuColumn, nameColumn, titleColumn, descriptionColumn
        }.Where(column => column >= 0));
        var definitions = new Dictionary<(string Ecu, int Code), DiagnosticTroubleCode>();
        for (var index = 1; index < rows.Count; index++)
        {
            var cells = rows[index];
            if (cells.Length > headers.Length)
                throw new InvalidDataException($"DTC table row {index + 1} has extra columns.");
            string Get(int column) => column >= 0 && column < cells.Length
                ? cells[column].Trim() : string.Empty;
            var rawCode = Get(codeColumn);
            if (!TryParseCode(rawCode, isDecimal, out var code))
                throw new InvalidDataException(
                    $"Invalid DTC '{rawCode}' on row {index + 1}. Use six hex digits, 0x-prefixed hex, " +
                    "or an explicitly labelled DTC Decimal column. OBD P/B/C/U codes need an OEM mapping.");
            var ecu = Get(ecuColumn);
            var name = Get(nameColumn);
            var title = Get(titleColumn);
            var description = Get(descriptionColumn);
            if (name.Length == 0 && title.Length == 0 && description.Length == 0)
                throw new InvalidDataException($"DTC table row {index + 1} has no meaning.");
            // Preserve every additional OEM column without trying to turn table
            // notes, fault conditions or severity labels into measured ECU data.
            var extra = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var column = 0; column < cells.Length; column++)
            {
                if (primaryColumns.Contains(column) || Get(column).Length == 0) continue;
                var heading = rows[0][column].Trim();
                if (heading.Length == 0 || !extra.TryAdd(heading, Get(column)))
                    throw new InvalidDataException(
                        $"DTC table row {index + 1} has an unnamed or duplicate OEM column.");
            }
            var current = new DiagnosticTroubleCode(code, name, title, description, ecu, source)
            {
                TableAttributesOverride = extra,
                SourceSheet = sheetName
            };
            var key = (ecu.ToUpperInvariant(), code);
            if (definitions.TryGetValue(key, out var previous))
            {
                if (previous.Name != name || previous.DisplayText != title ||
                    previous.Description != description ||
                    previous.TableAttributes.Count != current.TableAttributes.Count ||
                    current.TableAttributes.Any(pair =>
                        !previous.TableAttributes.TryGetValue(pair.Key, out var earlier) ||
                        !string.Equals(earlier, pair.Value, StringComparison.Ordinal)))
                    throw new InvalidDataException(
                        $"Conflicting definitions for ECU '{ecu}' DTC 0x{code:X6} at row {index + 1}.");
            }
            else definitions.Add(key, current);
        }
        if (definitions.Count == 0)
            throw new InvalidDataException("No DTC definitions in table.");
        return definitions.Values.GroupBy(item => item.Ecu, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => new DidCatalog(
                Path.GetFileNameWithoutExtension(source) +
                (sheetName.Length == 0 ? " DTC table" : $" · {sheetName} DTC table"),
                group.Key, source, [],
                group.OrderBy(item => item.Code).ToArray())).ToArray();
    }

    private static bool TryParseCode(string input, bool decimalCode, out int code)
    {
        code = 0;
        var text = input.Trim();
        if (decimalCode)
            return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out code)
                && code is >= 0 and <= 0xFFFFFF;
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            text = text[2..];
        else if (text.Length != 6)
            return false;
        return text.Length is > 0 and <= 6 &&
            int.TryParse(text, NumberStyles.AllowHexSpecifier,
                CultureInfo.InvariantCulture, out code) && code <= 0xFFFFFF;
    }

    private static char DetectDelimiter(string text)
    {
        var first = text.AsSpan();
        var index = first.IndexOfAny('\r', '\n');
        if (index >= 0) first = first[..index];
        var comma = 0;
        var semicolon = 0;
        var quoted = false;
        foreach (var ch in first)
        {
            if (ch == '"') quoted = !quoted;
            else if (!quoted && ch == ',') comma++;
            else if (!quoted && ch == ';') semicolon++;
        }
        return semicolon > comma ? ';' : ',';
    }

    private static List<string[]> ReadRows(string text, char separator)
    {
        var rows = new List<string[]>();
        var cells = new List<string>();
        var value = new StringBuilder();
        var quoted = false;
        var afterQuote = false;
        void AddCell()
        {
            if (cells.Count >= 80) throw new InvalidDataException("Too many DTC table columns.");
            cells.Add(value.ToString().Trim());
            value.Clear();
            afterQuote = false;
        }
        void AddRow()
        {
            AddCell();
            if (cells.Any(cell => cell.Length > 0))
            {
                rows.Add(cells.ToArray());
                if (rows.Count > 100_000) throw new InvalidDataException("Too many DTC table rows.");
            }
            cells.Clear();
        }
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (quoted)
            {
                if (ch == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        value.Append('"');
                        i++;
                    }
                    else { quoted = false; afterQuote = true; }
                }
                else value.Append(ch);
            }
            else if (ch == '"' && value.Length == 0 && !afterQuote) quoted = true;
            else if (ch == separator) AddCell();
            else if (ch is '\n' or '\r')
            {
                AddRow();
                if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
            }
            else if (afterQuote && !char.IsWhiteSpace(ch))
                throw new InvalidDataException("Unexpected characters after a quoted field.");
            else if (ch == '"') throw new InvalidDataException("Unexpected quote in an unquoted field.");
            else if (!afterQuote) value.Append(ch);
            if (value.Length > 8192) throw new InvalidDataException("DTC table field exceeds 8192 characters.");
        }
        if (quoted) throw new InvalidDataException("Unterminated quoted DTC table field.");
        if (value.Length > 0 || afterQuote || cells.Count > 0) AddRow();
        return rows;
    }
}
