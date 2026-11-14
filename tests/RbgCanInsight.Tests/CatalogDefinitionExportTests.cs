using System.IO.Compression;
using System.Xml.Linq;
using RbgCanInsight.Dbc;
using RbgCanInsight.DiagData;
using RbgCanInsight.TraceIO;
using Xunit;

namespace RbgCanInsight.Tests;

public sealed class CatalogDefinitionExportTests
{
    private static DbcDatabase SampleDbc()
    {
        DbcMessage Make(bool extended) => new(0x123, extended ? "Extended" : "Classic",
            8, "ECU", extended,
            [new DbcSignal("VehicleSpeed", 8, 16, DbcByteOrder.Intel, false,
                0.01, 0, 0, 655.35, "km/h", "Tester",
                ValueDescriptions: new Dictionary<long, string> { [0] = "Stopped", [1] = "=not a formula" })]);
        var classic = Make(false);
        var extended = Make(true);
        return new DbcDatabase("Powertrain",
            new Dictionary<uint, DbcMessage> { [0x123] = classic },
            MessageIdentities: new Dictionary<DbcMessageIdentity, DbcMessage>
            {
                [new(0x123, false)] = classic,
                [new(0x123, true)] = extended
            });
    }

    [Fact]
    public void DbcExportKeepsStandardAndExtendedFrameIdentitiesSeparate()
    {
        var sheets = CatalogDefinitionExport.CreateDbcSheets([SampleDbc()]);
        var messages = sheets.Single(sheet => sheet.Name == "Messages");
        var signals = sheets.Single(sheet => sheet.Name == "Signals");
        Assert.Equal(2, messages.Rows.Count);
        Assert.Equal(2, signals.Rows.Count);
        Assert.Contains(messages.Rows, row => row[2] == "11-bit");
        Assert.Contains(messages.Rows, row => row[2] == "29-bit");
        Assert.Equal("VehicleSpeed", signals.Rows[0][4]);
        Assert.Equal("0.01", signals.Rows[0][10]);
        Assert.Equal(4, sheets.Single(sheet => sheet.Name == "Value Meanings").Rows.Count);
    }

    [Fact]
    public void DbcBitPositionsExportEachIntelAndMotorolaBitInExactRawOrder()
    {
        var intel = new DbcSignal("Intel", 3, 10, DbcByteOrder.Intel,
            false, 1, 0, 0, 1023, "", "ECU");
        var motorola = new DbcSignal("Motorola", 7, 12, DbcByteOrder.Motorola,
            false, 1, 0, 0, 4095, "", "ECU");
        var message = new DbcMessage(0x123, "Mixed", 8, "ECU", false,
            [intel, motorola]);
        var database = new DbcDatabase("Synthetic",
            new Dictionary<uint, DbcMessage> { [message.Identifier] = message });
        var sheets = CatalogDefinitionExport.CreateDbcSheets([database]);
        var bits = sheets.Single(sheet => sheet.Name == "Bit Positions");

        Assert.Equal(22, bits.Rows.Count);
        Assert.Equal(17, bits.Headers.Count);
        Assert.Equal(10, bits.Rows.Count(row => row[4] == "Intel"));
        Assert.Equal(12, bits.Rows.Count(row => row[4] == "Motorola"));
        var intelFirst = bits.Rows.First(row => row[4] == "Intel");
        Assert.Equal("3", intelFirst[5]); // original DBC start bit
        Assert.Equal("0", intelFirst[7]); // first encoded bit
        Assert.Equal("0", intelFirst[8]); // physical byte
        Assert.Equal("3", intelFirst[9]); // LSB0 in that byte
        Assert.Equal("0", intelFirst[11]); // Intel raw LSB
        var bigEndian = bits.Rows.Where(row => row[4] == "Motorola").ToArray();
        Assert.Equal(new[] { "7", "6", "5", "4", "3", "2", "1", "0", "15", "14", "13", "12" },
            bigEndian.Select(row => row[10]).ToArray());
        Assert.Equal("11", bigEndian[0][11]); // DBC @0 first is MSB
        Assert.Equal("0", bigEndian[^1][11]);
        Assert.All(bits.Rows, row => Assert.Equal("YES", row[15]));
        Assert.NotNull(bits.NumericColumns);
        Assert.Contains(10, bits.NumericColumns!);
    }

    [Fact]
    public void BitExportFlagsOutOfDlcAndReportsSharedMuxBitsWithoutDeclaringCorruption()
    {
        var a = new DbcSignal("BranchA", 0, 8, DbcByteOrder.Intel,
            false, 1, 0, 0, 255, "", "ECU", MultiplexValue: 1);
        var b = a with { Name = "BranchB", MultiplexValue = 2 };
        var outside = a with { Name = "Outside", StartBit = 15, BitLength = 2,
            MultiplexValue = null };
        var invalid = a with { Name = "Invalid", BitLength = 65 };
        var message = new DbcMessage(0x321, "Message", 2, "ECU", false,
            [a, b, outside, invalid]);
        var database = new DbcDatabase("Synthetic",
            new Dictionary<uint, DbcMessage> { [message.Identifier] = message });
        var sheets = CatalogDefinitionExport.CreateDbcSheets([database]);
        var bits = sheets.Single(sheet => sheet.Name == "Bit Positions");
        var issues = sheets.Single(sheet => sheet.Name == "Bit Layout Issues");
        var branch = bits.Rows.First(row => row[4] == "BranchA");
        Assert.Equal("BranchB", branch[14]);
        Assert.Contains("mux branches", branch[16]);
        Assert.Equal("NO", bits.Rows.Single(row => row[4] == "Outside" && row[10] == "16")[15]);
        Assert.DoesNotContain(bits.Rows, row => row[4] == "Invalid");
        Assert.Contains(issues.Rows, row => row[4] == "Invalid" &&
            row[5].Contains("Unsupported"));
        Assert.Contains(issues.Rows, row => row[4] == "Outside" &&
            row[5].Contains("outside"));
        Assert.Contains(issues.Rows, row => row[4] == "BranchA" &&
            row[5].Contains("multiplex"));
    }

    [Fact]
    public void BitExportRespectsSignalTextFilterButChecksOverlapAgainstFullMessage()
    {
        var a = new DbcSignal("Selected", 0, 8, DbcByteOrder.Intel,
            false, 1, 0, 0, 255, "", "ECU");
        var b = a with { Name = "FilteredOut" };
        var message = new DbcMessage(0x100, "Sample", 1, "ECU", false, [a, b]);
        var database = new DbcDatabase("Powertrain",
            new Dictionary<uint, DbcMessage> { [message.Identifier] = message });
        var bits = CatalogDefinitionExport.CreateDbcSheets([database], "Selected")
            .Single(sheet => sheet.Name == "Bit Positions");
        Assert.Equal(8, bits.Rows.Count);
        Assert.All(bits.Rows, row => Assert.Equal("FilteredOut", row[14]));
        Assert.All(CatalogDefinitionExport.CreateDbcSheets([database], "missing"),
            sheet => Assert.Empty(sheet.Rows));
    }

    [Fact]
    public void RawLimitsRetainExactSignedAndUnsigned64BitBoundsAsText()
    {
        var unsigned = new DbcSignal("Unsigned", 0, 64, DbcByteOrder.Intel,
            false, 1, 0, 0, 100, "", "ECU");
        var signed = unsigned with { Name = "Signed", IsSigned = true };
        var floats = unsigned with { Name = "Float", BitLength = 32,
            ValueType = DbcSignalValueType.Float32 };
        var invalidEnum = unsigned with { Name = "Enum", BitLength = 2,
            ValueDescriptions = new Dictionary<long, string>
            {
                [0] = "Idle",
                [4] = "Out of range"
            } };
        var msg = new DbcMessage(0x456, "Code", 8, "ECU", true,
            [unsigned, signed, floats, invalidEnum]);
        var db = new DbcDatabase("Ranges",
            new Dictionary<uint, DbcMessage> { [msg.Identifier] = msg });
        var sheet = CatalogDefinitionExport.CreateDbcSheets([db])
            .Single(x => x.Name == "Raw Physical Limits");
        var u = sheet.Rows.Single(r => r[4] == "Unsigned");
        Assert.Equal("0", u[8]);
        Assert.Equal("18446744073709551615", u[9]);
        Assert.Equal("29-bit", u[2]);
        var s = sheet.Rows.Single(r => r[4] == "Signed");
        Assert.Equal("-9223372036854775808", s[8]);
        Assert.Equal("9223372036854775807", s[9]);
        var f = sheet.Rows.Single(r => r[4] == "Float");
        Assert.Equal("", f[8]);
        Assert.Equal("", f[9]);
        Assert.Contains("IEEE-754", f[17]);
        var e = sheet.Rows.Single(r => r[4] == "Enum");
        Assert.Equal("3", e[9]);
        Assert.Equal("2", e[15]);
        Assert.Equal("1", e[16]);
        Assert.Contains("outside representable", e[17]);
        Assert.DoesNotContain(8, sheet.NumericColumns!);
        Assert.DoesNotContain(9, sheet.NumericColumns!);
    }

    [Fact]
    public void DbcExportFiltersSignalsWithoutRequiringTrace()
    {
        var sheets = CatalogDefinitionExport.CreateDbcSheets([SampleDbc()], "VehicleSpeed");
        Assert.Equal(2, sheets.Single(sheet => sheet.Name == "Signals").Rows.Count);
        var missing = CatalogDefinitionExport.CreateDbcSheets([SampleDbc()], "NotARealSignal");
        Assert.All(missing, sheet => Assert.Empty(sheet.Rows));
    }

    [Fact]
    public void DiagnosticExportRetainsIndividualDidFieldsAndEnums()
    {
        var fields = new[]
        {
            new DiagnosticFieldDefinition("Status", "Status", "", 0, 3, "A_UINT32",
                "", null, null, null, null, false, "high-low",
                [new DiagnosticEnumValue(2, "Warning")]),
            new DiagnosticFieldDefinition("Temperature", "Temperature", "", 8, 16, "A_INT32",
                "degC", -40, 150, 0.1, -40, true, "low-high")
        };
        var did = new DidParameter(0xF199, "BatteryStatus", "Battery status", "",
            "", null, null, null, null, "", 24, "ECU", "synthetic.cdd", fields);
        var catalog = new DidCatalog("Synthetic", "ECU", "synthetic.cdd", [did]);
        var sheets = CatalogDefinitionExport.CreateDiagnosticSheets([catalog], "BatteryStatus");
        Assert.Single(sheets.Single(sheet => sheet.Name == "DIDs").Rows);
        var detail = sheets.Single(sheet => sheet.Name == "DID Fields");
        Assert.Equal(2, detail.Rows.Count);
        Assert.Contains(detail.Rows, row => row[4] == "Temperature" && row[6] == "8" &&
            row[7] == "16" && row[9] == "low-high");
        var meanings = sheets.Single(sheet => sheet.Name == "DID Values");
        Assert.Single(meanings.Rows);
        Assert.Equal("Warning", meanings.Rows[0][6]);
    }

    [Fact]
    public void WorkbookEscapesXmlAndStoresUntrustedFormulaLookingTextAsString()
    {
        var file = Path.Combine(Path.GetTempPath(), "rbg-definitions-" + Guid.NewGuid().ToString("N") + ".xlsx");
        try
        {
            var sheet = new ExportSheet("Signals",
                ["Name", "Bits", "Value"], [
                    new string[] { "A & B < C", "16", "=HYPERLINK(\"https://invalid.example\")" }
                ], new HashSet<int> { 1 });
            StructuredWorkbookExport.Write(file, [sheet]);
            using var zip = ZipFile.OpenRead(file);
            Assert.NotNull(zip.GetEntry("xl/workbook.xml"));
            using var stream = zip.GetEntry("xl/worksheets/sheet1.xml")!.Open();
            var xml = XDocument.Load(stream);
            XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            Assert.Empty(xml.Descendants(ns + "f"));
            Assert.Contains(xml.Descendants(ns + "c"), cell =>
                (string?)cell.Attribute("t") == "n" && (string?)cell.Element(ns + "v") == "16");
            Assert.Contains(xml.Descendants(ns + "t"), value =>
                value.Value == "=HYPERLINK(\"https://invalid.example\")");
            Assert.Contains(xml.Descendants(ns + "t"), value => value.Value == "A & B < C");
            Assert.NotNull(xml.Descendants(ns + "autoFilter").FirstOrDefault());
        }
        finally
        {
            if (File.Exists(file)) File.Delete(file);
        }
    }

    [Fact]
    public void InvalidWorkbookRowWidthsAreRejected()
    {
        var file = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".xlsx");
        try
        {
            Assert.Throws<ArgumentException>(() => StructuredWorkbookExport.Write(file,
                [new ExportSheet("Rows", ["A", "B"], [new string[] { "one" }])]));
        }
        finally { if (File.Exists(file)) File.Delete(file); }
    }
}
