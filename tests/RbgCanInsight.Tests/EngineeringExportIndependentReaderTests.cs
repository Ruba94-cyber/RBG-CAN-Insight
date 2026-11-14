using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using RbgCanInsight.DiagData;
using RbgCanInsight.TraceIO;
using Xunit;

namespace RbgCanInsight.Tests;

/// <summary>
/// Release-gate verification: exports are re-read by a deliberately independent
/// minimal OOXML/CSV/JSON reader (raw zip + XML resolved through workbook
/// relationships and content types the way Excel does), never by the writer's
/// own helpers. Real Excel was NOT used.
/// </summary>
public sealed class EngineeringExportIndependentReaderTests
{
    private static readonly XNamespace Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace Pkg = "http://schemas.openxmlformats.org/package/2006/relationships";
    private static readonly XNamespace Ct = "http://schemas.openxmlformats.org/package/2006/content-types";

    private sealed record Cell(string Ref, string? Type, string Text);
    private sealed record Sheet(string Name, List<List<Cell>> Rows);

    private static string Temp()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rbg-xr-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static XDocument Load(ZipArchive zip, string name)
    {
        var entry = zip.GetEntry(name) ?? throw new InvalidDataException("missing part " + name);
        using var s = entry.Open();
        return XDocument.Load(s);
    }

    private static IEnumerable<XElement> StreamRows(System.Xml.XmlReader reader)
    {
        reader.MoveToContent();
        var more = reader.Read();
        while (more)
        {
            if (reader.NodeType == System.Xml.XmlNodeType.Element && reader.LocalName == "row")
                yield return (XElement)XNode.ReadFrom(reader); // leaves reader on the next node
            else more = reader.Read();
        }
    }

    /// <summary>Resolves sheets via workbook.xml + rels + content types (strict, Excel-like).</summary>
    private static List<Sheet> ReadWorkbook(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        Assert.Equal(zip.Entries.Count, zip.Entries.Select(e => e.FullName).Distinct().Count());
        var types = Load(zip, "[Content_Types].xml");
        var overrides = types.Descendants(Ct + "Override")
            .ToDictionary(o => o.Attribute("PartName")!.Value, o => o.Attribute("ContentType")!.Value);
        var rootRels = Load(zip, "_rels/.rels");
        var wbTarget = rootRels.Descendants(Pkg + "Relationship")
            .Single(r => r.Attribute("Type")!.Value.EndsWith("/officeDocument")).Attribute("Target")!.Value;
        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml",
            overrides["/" + wbTarget]);
        var rels = Load(zip, "xl/_rels/workbook.xml.rels").Descendants(Pkg + "Relationship")
            .ToDictionary(r => r.Attribute("Id")!.Value, r => r.Attribute("Target")!.Value);
        var sheetEls = Load(zip, wbTarget).Descendants(Main + "sheet").ToList();
        Assert.Equal(sheetEls.Count, sheetEls.Select(s => s.Attribute("sheetId")!.Value).Distinct().Count());
        Assert.Equal(sheetEls.Count,
            sheetEls.Select(s => s.Attribute("name")!.Value.ToUpperInvariant()).Distinct().Count());

        var sharedStrings = new List<string>();
        var sst = zip.GetEntry("xl/sharedStrings.xml");
        if (sst is not null)
        {
            using var ss = sst.Open();
            sharedStrings = XDocument.Load(ss).Descendants(Main + "si")
                .Select(si => string.Concat(si.Descendants(Main + "t").Select(t => t.Value))).ToList();
        }

        var result = new List<Sheet>();
        foreach (var s in sheetEls)
        {
            var part = "xl/" + rels[s.Attribute(R + "id")!.Value];
            Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml",
                overrides["/" + part]);
            using var stream = zip.GetEntry(part)!.Open();
            using var reader = System.Xml.XmlReader.Create(stream);
            var rows = new List<List<Cell>>();
            foreach (var row in StreamRows(reader))
                rows.Add(row.Elements(Main + "c").Select(c =>
                {
                    var t = c.Attribute("t")?.Value;
                    var text = t switch
                    {
                        "inlineStr" => string.Concat(c.Descendants(Main + "t").Select(x => x.Value)),
                        "s" => sharedStrings[int.Parse(c.Element(Main + "v")!.Value, CultureInfo.InvariantCulture)],
                        _ => c.Element(Main + "v")?.Value ?? ""
                    };
                    return new Cell(c.Attribute("r")!.Value, t, text);
                }).ToList());
            result.Add(new Sheet(s.Attribute("name")!.Value, rows));
        }
        return result;
    }

    private static List<string> Values(Sheet s, int row) => s.Rows[row].Select(c => c.Text).ToList();

    // ---------- realistic data ----------

    private static ExportSheet Did() => new("DIDs",
        ["ECU", "DID", "Name", "Unit", "Factor", "Offset"],
        [
            ["Motorsteuergerät", "0xF190", "Fahrgestellnummer üöäß", "", "1", "0"],
            ["BMS", "0x0123", "Zellentemperatur", "°C", "0.0625", "-40"],
            ["BMS", "0x0124", "Leckstrom", "µA", "1e-3", "0"],
            ["INV", "0x0200", "Ohm Ω / ℃ ≠ \"q\" <&> \U0001F600", "Ω", "-3.5", "10"],
        ], new HashSet<int> { 4, 5 });

    private static ExportSheet Dtc() => new("DTCs",
        ["ECU", "DTC", "Status", "Description"],
        [
            ["BMS", "0x123456", "0x2F", "Unterspannung, Zelle 3\r\nzweite Zeile"],
            ["BMS", "0xC07388", "0x08", "=HYPERLINK(\"x\")"],
        ]);

    private static ExportSheet Signals() => new("Decoded signals",
        ["Time [s]", "Signal", "Value", "Unit"],
        [
            ["0.001000", "CellTemp", "21.5", "°C"],
            ["0.002000", "Leak", "-0.000001", "µA"],
            ["0.003000", "Big", "123456789012345678", ""],
        ], new HashSet<int> { 0, 2 });

    private static ExportSheet Transactions() => new("Diag transactions",
        ["Index", "Request", "Response", "NRC", "Latency [ms]"],
        [
            ["1", "22 F1 90", "62 F1 90 57 41 55", "", "12.5"],
            ["2", "22 01 23", "7F 22 31", "0x31", "8"],
            ["3", "19 02 FF", "59 02 FF C0 73 88 08", "", "15.25"],
        ], new HashSet<int> { 0, 4 });

    // ---------- XLSX ----------

    [Fact]
    public void XlsxRoundTripPreservesSheetOrderHeadersUnicodeAndTypes()
    {
        var dir = Temp();
        try
        {
            var path = Path.Combine(dir, "eng.xlsx");
            var input = new[] { Did(), Dtc(), Signals(), Transactions() };
            StructuredWorkbookExport.Write(path, input);

            var sheets = ReadWorkbook(path);
            Assert.Equal(input.Select(s => s.Name), sheets.Select(s => s.Name));
            for (var i = 0; i < input.Length; i++)
            {
                Assert.Equal(input[i].Headers, Values(sheets[i], 0));
                Assert.Equal(input[i].Rows.Count + 1, sheets[i].Rows.Count);
                for (var r = 0; r < input[i].Rows.Count; r++)
                {
                    Assert.Equal(input[i].Rows[r].Count, sheets[i].Rows[r + 1].Count);
                    for (var c = 0; c < input[i].Rows[r].Count; c++)
                    {
                        var cell = sheets[i].Rows[r + 1][c];
                        var src = input[i].Rows[r][c];
                        var isNum = input[i].NumericColumns?.Contains(c) == true &&
                            double.TryParse(src, NumberStyles.Float, CultureInfo.InvariantCulture, out _) &&
                            src.Length <= 15;
                        if (isNum)
                        {
                            Assert.Equal("n", cell.Type);
                            Assert.Equal(double.Parse(src, CultureInfo.InvariantCulture),
                                double.Parse(cell.Text, CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            Assert.Equal("inlineStr", cell.Type);
                            // XML parsers normalise CRLF to LF (Excel itself stores LF in cells); all else exact.
                            Assert.Equal(src.Replace("\r\n", "\n"), cell.Text);
                        }
                    }
                }
            }
            Assert.Equal("°C", sheets[0].Rows[2][3].Text);
            Assert.Equal("µA", sheets[0].Rows[3][3].Text);
            Assert.Equal("Motorsteuergerät", sheets[0].Rows[1][0].Text);
            Assert.Equal("inlineStr", sheets[0].Rows[2][1].Type);        // hex id stays text
            Assert.Equal("0x0123", sheets[0].Rows[2][1].Text);
            Assert.Equal("inlineStr", sheets[2].Rows[3][2].Type);        // 18 digits not truncated to double
            Assert.Equal("123456789012345678", sheets[2].Rows[3][2].Text);
            Assert.Equal("inlineStr", sheets[1].Rows[2][3].Type);        // '=' text is never a formula
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void XlsxHasNoSharedStringsFormulasUsesUtf8NoBomAndSchemaOrder()
    {
        var dir = Temp();
        try
        {
            var path = Path.Combine(dir, "eng.xlsx");
            StructuredWorkbookExport.Write(path, [Did(), Dtc()]);
            using var zip = ZipFile.OpenRead(path);
            Assert.Null(zip.GetEntry("xl/sharedStrings.xml")); // no shared-string index corruption possible
            foreach (var e in zip.Entries)
            {
                using var s = e.Open();
                var buf = new MemoryStream();
                s.CopyTo(buf);
                var bytes = buf.ToArray();
                Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, e.FullName);
                var text = new UTF8Encoding(false, true).GetString(bytes); // throws on invalid UTF-8
                if (!e.FullName.StartsWith("xl/worksheets/")) continue;
                Assert.DoesNotContain("<f>", text);
                var doc = XDocument.Parse(text);
                Assert.Equal(["sheetViews", "sheetData", "autoFilter"],
                    doc.Root!.Elements().Select(x => x.Name.LocalName).ToArray());
                var rowNums = doc.Descendants(Main + "row").Select(r => int.Parse(r.Attribute("r")!.Value)).ToList();
                Assert.Equal(Enumerable.Range(1, rowNums.Count), rowNums);
                foreach (var row in doc.Descendants(Main + "row"))
                    Assert.All(row.Elements(Main + "c"), c =>
                        Assert.EndsWith(row.Attribute("r")!.Value, c.Attribute("r")!.Value));
            }
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void XlsxEmptySheetStillHasHeaderRowAndValidAutoFilter()
    {
        var dir = Temp();
        try
        {
            var path = Path.Combine(dir, "e.xlsx");
            StructuredWorkbookExport.Write(path, [new ExportSheet("Empty", ["A", "B"], [])]);
            Assert.Single(Assert.Single(ReadWorkbook(path)).Rows);
            using var zip = ZipFile.OpenRead(path);
            Assert.Equal("A1:B1", Load(zip, "xl/worksheets/sheet1.xml")
                .Descendants(Main + "autoFilter").Single().Attribute("ref")!.Value);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void XlsxNumericCellsAreCanonicalXsdDoublesOrFallBackToText()
    {
        var dir = Temp();
        try
        {
            var path = Path.Combine(dir, "n.xlsx");
            var sheet = new ExportSheet("N", ["v"],
                [[" 5 "], ["+7"], ["1E+3"], ["0123"], ["NaN"], ["1,5"], ["-0.5"]],
                new HashSet<int> { 0 });
            StructuredWorkbookExport.Write(path, [sheet]);
            var cells = ReadWorkbook(path).Single().Rows.Skip(1).Select(r => r[0]).ToList();
            foreach (var c in cells.Where(c => c.Type == "n"))
                Assert.Matches(@"^[-+]?(\d+\.?\d*|\.\d+)([eE][-+]?\d+)?$", c.Text); // xsd:double, no whitespace
            Assert.Equal("inlineStr", cells[4].Type); // NaN
            Assert.Equal("inlineStr", cells[5].Type); // 1,5
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void XlsxRealDiagnosticCatalogWithUmlautsRoundTrips()
    {
        var dir = Temp();
        try
        {
            var field = new DiagnosticFieldDefinition("Temp", "Zelltemperatur °C", "", -1, 8, "A_UINT32",
                "°C", 0.0625, -40.0, null, null, false, "",
                [new DiagnosticEnumValue(1, "Übertemperatur")], SupportsPhysicalConversion: true);
            var parameter = new DidParameter(0xF190, "Kühlmittel", "Kühlmittel", "",
                "°C", 0.0625, -40.0, null, null, "", null, "BMS", "kühl.cdd", [field]);
            var catalog = new DidCatalog("Ex", "BMS", "kühl.cdd", [parameter],
                TroubleCodes: [new DiagnosticTroubleCode(0x123456, "Dtc", "Überstrom", "", "BMS", "kühl.cdd")],
                Routines: [], Services: [], Connections: [], Variants: []);
            var sheets = CatalogDefinitionExport.CreateDiagnosticSheets([catalog]);
            var path = Path.Combine(dir, "d.xlsx");
            StructuredWorkbookExport.Write(path, sheets);
            var read = ReadWorkbook(path);
            Assert.Equal(sheets.Select(s => s.Name), read.Select(s => s.Name));
            for (var i = 0; i < sheets.Count; i++)
            {
                Assert.Equal(sheets[i].Headers, Values(read[i], 0));
                Assert.Equal(sheets[i].Rows.Count + 1, read[i].Rows.Count);
                for (var r = 0; r < sheets[i].Rows.Count; r++)
                    for (var c = 0; c < sheets[i].Headers.Count; c++)
                    {
                        var cell = read[i].Rows[r + 1][c];
                        if (cell.Type == "inlineStr") Assert.Equal(sheets[i].Rows[r][c], cell.Text);
                        else Assert.Equal(double.Parse(sheets[i].Rows[r][c], CultureInfo.InvariantCulture),
                            double.Parse(cell.Text, CultureInfo.InvariantCulture));
                    }
            }
            var dids = read.Single(s => s.Name == "DIDs");
            Assert.Equal("Kühlmittel", dids.Rows[1][3].Text);
            Assert.Equal("0xF190", dids.Rows[1][2].Text);
            Assert.Contains(dids.Rows[1], c => c.Text == "°C");
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void XlsxSplitsSheetsBeyondExcelRowLimitAndKeepsEveryRowOnce()
    {
        const int rows = 1_048_575 + 7; // physical sheets: 1,048,575 data rows + 7
        var dir = Temp();
        try
        {
            var sheet = new ExportSheet("Frames", ["Seq", "Text"], new SyntheticRows(rows), new HashSet<int> { 0 });
            var other = new ExportSheet("Frames (2)", ["x"], [["collide-guard"]]);
            var path = Path.Combine(dir, "big.xlsx");
            StructuredWorkbookExport.Write(path, [sheet, other]);

            using var zip = ZipFile.OpenRead(path);
            var names = Load(zip, "xl/workbook.xml").Descendants(Main + "sheet")
                .Select(s => s.Attribute("name")!.Value).ToList();
            Assert.Equal(3, names.Count);
            Assert.Equal("Frames", names[0]);
            Assert.Equal("Frames (3)", names[1]);       // continuation avoids the user's own "Frames (2)"
            Assert.Equal("Frames (2)", names[2]);       // user's own sheet not hijacked

            var counts = new List<(int rows, string lastRef, string firstSeq, string lastSeq, string hdr)>();
            foreach (var n in new[] { "xl/worksheets/sheet1.xml", "xl/worksheets/sheet2.xml" })
            {
                int count = 0, maxRow = 0;
                string? first = null, last = null; var lastRef = ""; var hdr = "";
                using var s = zip.GetEntry(n)!.Open();
                using var rd = System.Xml.XmlReader.Create(s);
                foreach (var row in StreamRows(rd))
                {
                    var r = int.Parse(row.Attribute("r")!.Value);
                    Assert.Equal(maxRow + 1, r);
                    maxRow = r;
                    if (r == 1) { hdr = row.Descendants(Main + "t").First().Value; continue; }
                    count++;
                    var seq = row.Descendants(Main + "v").First().Value;
                    first ??= seq; last = seq; lastRef = row.Attribute("r")!.Value;
                }
                Assert.True(maxRow <= 1_048_576, "sheet exceeds Excel row limit: " + maxRow);
                counts.Add((count, lastRef, first!, last!, hdr));
            }
            Assert.Equal(1_048_575, counts[0].rows);
            Assert.Equal("1048576", counts[0].lastRef);
            Assert.Equal("0", counts[0].firstSeq);
            Assert.Equal("1048574", counts[0].lastSeq);
            Assert.Equal(7, counts[1].rows);
            Assert.Equal("1048575", counts[1].firstSeq);
            Assert.Equal((rows - 1).ToString(CultureInfo.InvariantCulture), counts[1].lastSeq);
            Assert.Equal("Seq", counts[0].hdr);
            Assert.Equal("Seq", counts[1].hdr); // header repeated on continuation sheet
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void CsvArchiveSplitsAtLimitWithRepeatedHeaders()
    {
        var dir = Temp();
        try
        {
            var path = Path.Combine(dir, "a.zip");
            StructuredCsvArchiveExport.Write(path, [new ExportSheet("T", ["H", "U"], new SyntheticRows(10))], maxDataRows: 4);
            using var zip = ZipFile.OpenRead(path);
            Assert.Equal(["T (2).csv", "T (3).csv", "T.csv"], zip.Entries.Select(e => e.FullName).Order().ToArray());
            var total = 0;
            foreach (var e in zip.Entries)
            {
                using var rd = new StreamReader(e.Open(), new UTF8Encoding(false), true);
                var rows = ParseCsv(rd.ReadToEnd());
                Assert.Equal(["H", "U"], rows[0]);
                total += rows.Count - 1;
            }
            Assert.Equal(10, total);
        }
        finally { Directory.Delete(dir, true); }
    }

    // ---------- CSV ----------

    /// <summary>Independent RFC 4180 parser (BOM stripped by caller).</summary>
    private static List<List<string>> ParseCsv(string text)
    {
        if (text.Length > 0 && text[0] == '﻿') text = text[1..];
        var rows = new List<List<string>>();
        var row = new List<string>();
        var f = new StringBuilder();
        var q = false;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (q)
            {
                if (ch == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { f.Append('"'); i++; }
                    else q = false;
                }
                else f.Append(ch);
            }
            else if (ch == '"') q = true;
            else if (ch == ',') { row.Add(f.ToString()); f.Clear(); }
            else if (ch == '\r') { }
            else if (ch == '\n') { row.Add(f.ToString()); f.Clear(); rows.Add(row); row = []; }
            else f.Append(ch);
        }
        Assert.False(q, "unterminated quote");
        if (f.Length > 0 || row.Count > 0) { row.Add(f.ToString()); rows.Add(row); }
        return rows;
    }

    [Fact]
    public void CsvHasUtf8BomCommaDelimiterCrLfAndRoundTripsEscapingAndUnicode()
    {
        var dir = Temp();
        try
        {
            var path = Path.Combine(dir, "d.csv");
            TabularExport.WriteCsv(path,
            [
                new ExportColumn("Signal, \"name\"", ["Zellentemperatur", "Leckµ", "a,b", "x\r\ny", "\"q\""]),
                new ExportColumn("Unit", ["°C", "µA", "Ω", "", "\U0001F600"]),
                new ExportColumn("Value", ["21.5", "-0.1", "text", "7", "8"])
            ]);
            var bytes = File.ReadAllBytes(path);
            Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
            Assert.False(bytes.Length > 6 && bytes[3] == 0xEF && bytes[4] == 0xBB && bytes[5] == 0xBF); // single BOM
            var text = new UTF8Encoding(false, true).GetString(bytes[3..]);
            Assert.EndsWith("\r\n", text);
            var rows = ParseCsv(text);
            Assert.Equal(6, rows.Count);
            Assert.All(rows, r => Assert.Equal(3, r.Count));
            Assert.Equal(["Signal, \"name\"", "Unit", "Value"], rows[0]);
            Assert.Equal("Zellentemperatur", rows[1][0]);
            Assert.Equal("°C", rows[1][1]);
            Assert.Equal("µA", rows[2][1]);
            Assert.Equal("a,b", rows[3][0]);
            Assert.Equal("x\r\ny", rows[4][0]);
            Assert.Equal("\"q\"", rows[5][0]);
            Assert.Equal("\U0001F600", rows[5][1]);
            Assert.Equal("'21.5", rows[1][2]); // documented protective apostrophe
            Assert.Equal("text", rows[3][2]);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void CsvArchiveNumericColumnsAreNotApostropheProtectedButTextIsExact()
    {
        var dir = Temp();
        try
        {
            var path = Path.Combine(dir, "a.zip");
            StructuredCsvArchiveExport.Write(path, [Did()]);
            using var zip = ZipFile.OpenRead(path);
            var bytes = new MemoryStream();
            using (var s = zip.GetEntry("DIDs.csv")!.Open()) s.CopyTo(bytes);
            Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes.ToArray()[..3]);
            var rows = ParseCsv(new UTF8Encoding(false, true).GetString(bytes.ToArray()));
            Assert.Equal(["ECU", "DID", "Name", "Unit", "Factor", "Offset"], rows[0]);
            Assert.Equal("0.0625", rows[2][4]);
            Assert.Equal("-40", rows[2][5]);
            Assert.Equal("-3.5", rows[4][4]);
            Assert.Equal("0x0123", rows[2][1]);
            Assert.Equal("°C", rows[2][3]);
            Assert.Equal("Ohm Ω / ℃ ≠ \"q\" <&> \U0001F600", rows[4][2]);
        }
        finally { Directory.Delete(dir, true); }
    }

    // ---------- JSON ----------

    [Fact]
    public void MappingJsonExportParsesBackWithProvenanceAndUnicode()
    {
        // No engineering-data JSON exporter exists besides the DID<->CAN mapping
        // document (.rbgmap.json); verify it with a plain JsonDocument read.
        var dir = Temp();
        try
        {
            var path = Path.Combine(dir, "m" + MappingDocument.FileExtension);
            var doc = new MappingDocument("Kühlung °C µA \U0001F600",
                new DateTimeOffset(2026, 11, 11, 10, 0, 0, TimeSpan.Zero),
                [new DidSignalMapping(0xF190, "Fahrgestellnr", "BMS", "bms.dbc", "BMS_1", "Zelltempä",
                    0.875, "\"quoted\" Grund\r\nzeile2", MappingStatus.Confirmed, "Notiz Ü",
                    0x18DAF110, true, "fp-did-abc", "fp-sig-def")]);
            doc.Save(path);

            var bytes = File.ReadAllBytes(path);
            Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
            using var json = JsonDocument.Parse(bytes);
            var root = json.RootElement;
            Assert.Equal("Kühlung °C µA \U0001F600", root.GetProperty("Title").GetString());
            Assert.Equal(DateTimeOffset.Parse("2026-11-11T10:00:00Z"), root.GetProperty("SavedAt").GetDateTimeOffset());
            var m = root.GetProperty("Mappings")[0];
            Assert.Equal(0xF190, m.GetProperty("Did").GetInt32());
            Assert.Equal("Confirmed", m.GetProperty("Status").GetString());
            Assert.Equal("fp-did-abc", m.GetProperty("DidDefinitionFingerprint").GetString());
            Assert.Equal("fp-sig-def", m.GetProperty("CanSignalFingerprint").GetString());
            Assert.Equal(0x18DAF110u, m.GetProperty("MessageIdentifier").GetUInt32());
            Assert.True(m.GetProperty("IsExtended").GetBoolean());
            Assert.Equal("Zelltempä", m.GetProperty("Signal").GetString());
            Assert.Equal("\"quoted\" Grund\r\nzeile2", m.GetProperty("Reason").GetString());
            Assert.Equal(0.875, m.GetProperty("Confidence").GetDouble());

            var back = MappingDocument.Load(path);
            Assert.Equal(doc.Title, back.Title);
            Assert.Equal(doc.Mappings[0], back.Mappings[0]);
        }
        finally { Directory.Delete(dir, true); }
    }

    private sealed class SyntheticRows(int count) : IReadOnlyList<IReadOnlyList<string>>
    {
        public int Count => count;
        public IReadOnlyList<string> this[int index] => index < 0 || index >= count
            ? throw new ArgumentOutOfRangeException(nameof(index))
            : [index.ToString(CultureInfo.InvariantCulture), "Zeile ä " + index];
        public IEnumerator<IReadOnlyList<string>> GetEnumerator()
        { for (var i = 0; i < count; i++) yield return this[i]; }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
