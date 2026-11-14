using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using Xunit;

namespace RbgCanInsight.GuiTests;

/// <summary>
/// End-to-end content checks against workbooks produced through actual WPF
/// buttons and Windows Save dialogs. Fixtures are synthetic and source-only.
/// These assertions complement the pure parser tests: a visible export action
/// must preserve bit significance, typed raw limits and PAR lineage.
/// </summary>
[Trait("Category", "WindowsFlaUI")]
public sealed class GuiExportContentWorkflows
{
    private static readonly XNamespace MainNs =
        "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    [Fact]
    public void DbcExport_ActualUiWorkbookPreservesBitCoordinatesRawLimitsAndEnumMeanings()
    {
        using var ui = new GuiSession();
        // This fixture is deliberately independent from the default DBC.
        // Motorola @0 start=7 produces 7,6,...0,15,14,13,12.
        // Full unsigned 64-bit raw limits must not be rounded by Excel.
        var fixture = Path.Combine(ui.Root, "bit-precision.dbc");
        const string source = """
            VERSION ""
            BO_ 256 Precision: 8 ECM
             SG_ BigEndian : 7|12@0+ (1,0) [0|4095] "" Vector__XXX
             SG_ UInt64 : 0|64@1+ (1,0) [0|18446744073709551615] "" Vector__XXX
             SG_ State : 16|2@1+ (1,0) [0|3] "" Vector__XXX
            VAL_ 256 State 0 "Stopped" 1 "Running";
            """;
        File.WriteAllText(fixture, source);
        ui.Navigate("nav.dbc");
        ui.Click("dbc.load");
        ui.ChooseFile(fixture);
        ui.AwaitStatus("1 DBC");
        var workbook = Path.Combine(ui.Root, "bit-precision.xlsx");
        ui.Click("dbc.exportDefinitions");
        ui.ChooseFile(workbook);
        ui.AwaitExport(workbook);

        using var zip = ZipFile.OpenRead(workbook);
        var bitSheet = Sheet(zip, "Bit Positions");
        var rawSheet = Sheet(zip, "Raw Physical Limits");
        var valuesSheet = Sheet(zip, "Value Meanings");

        var motorola = Rows(bitSheet).Where(row => Value(row, "E") == "BigEndian").ToArray();
        Assert.Equal(12, motorola.Length);
        Assert.Equal(
            new[] { "7", "6", "5", "4", "3", "2", "1", "0", "15", "14", "13", "12" },
            motorola.Select(row => Value(row, "K")).ToArray());
        Assert.Equal("11", Value(motorola[0], "L"));
        Assert.Equal("0", Value(motorola[^1], "L"));
        Assert.All(motorola, row => Assert.Equal("YES", Value(row, "P")));

        var uint64 = Rows(rawSheet).Single(row => Value(row, "E") == "UInt64");
        Assert.Equal("0", Value(uint64, "I"));
        Assert.Equal("18446744073709551615", Value(uint64, "J"));
        Assert.Equal("inlineStr", Cell(uint64, "J")?.Attribute("t")?.Value);
        Assert.Empty(rawSheet.Descendants(MainNs + "f"));

        Assert.Contains(Rows(valuesSheet), row => Value(row, "E") == "State" &&
            Value(row, "G") == "Running");
        Assert.Equal(source, File.ReadAllText(fixture));
    }

    [Fact]
    public void MappingCsv_ActualUiPreservesNumericIdentifiersAndNeverEvaluatesUntrustedText()
    {
        using var ui = new GuiSession();
        ui.Navigate("nav.dbc");
        ui.Click("dbc.load");
        ui.ChooseFile(ui.DbcPath);
        ui.AwaitStatus("1 DBC");
        ui.Navigate("nav.mapping");
        ui.Click("mapping.loadCatalog");
        ui.ChooseFile(ui.OdxPath);
        ui.AwaitMappingStatus("from 1 file");
        ui.Click("mapping.correlate");
        ui.AwaitMappingStatus("Auto-correlation done");

        var csvPath = Path.Combine(ui.Root, "mapping-safety.csv");
        ui.Click("mapping.export");
        ui.ChooseFile(csvPath);
        ui.Until(() => File.Exists(csvPath) && new FileInfo(csvPath).Length > 50
            ? "ready" : null, "real CSV export");
        var bytes = File.ReadAllBytes(csvPath);
        Assert.Equal(Encoding.UTF8.GetPreamble(), bytes[..3]);
        var text = File.ReadAllText(csvPath, Encoding.UTF8);
        Assert.StartsWith("\"", text);
        Assert.Contains("DidDefinitionFingerprint", text);
        Assert.Contains("CanSignalFingerprint", text);
        Assert.DoesNotContain('\0', text);
        Assert.Equal(Fixtures.Dbc, File.ReadAllText(ui.DbcPath));
        Assert.Equal(Fixtures.Odx, File.ReadAllText(ui.OdxPath));
    }

    private static XDocument Sheet(ZipArchive zip, string name)
    {
        using var workbookStream = zip.GetEntry("xl/workbook.xml")!.Open();
        var workbook = XDocument.Load(workbookStream);
        var sheet = workbook.Descendants(MainNs + "sheet")
            .Single(node => node.Attribute("name")?.Value == name);
        // The production exporter writes worksheets in workbook order.
        var position = workbook.Descendants(MainNs + "sheet").ToList().IndexOf(sheet) + 1;
        using var stream = zip.GetEntry($"xl/worksheets/sheet{position}.xml")!.Open();
        return XDocument.Load(stream);
    }

    private static IEnumerable<XElement> Rows(XDocument sheet) =>
        sheet.Descendants(MainNs + "row").Skip(1);

    private static XElement? Cell(XElement row, string column) =>
        row.Elements(MainNs + "c").FirstOrDefault(cell =>
        {
            var address = cell.Attribute("r")?.Value ?? string.Empty;
            return address.StartsWith(column, StringComparison.Ordinal) &&
                address.Length > column.Length && char.IsDigit(address[column.Length]);
        });

    private static string Value(XElement row, string column)
    {
        var cell = Cell(row, column);
        return cell?.Descendants(MainNs + "t").FirstOrDefault()?.Value ??
            cell?.Element(MainNs + "v")?.Value ?? string.Empty;
    }
}
