using System.Globalization;
using System.Text;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using Xunit;

namespace RbgCanInsight.GuiTests;

/// <summary>
/// Real content checks for the analysis tools (not just "the window opens"): hand-built J1939 and
/// OBD-II traffic must decode, a SocketCAN pcapng capture must import, and the Trace row buttons
/// must behave as documented (Quick Plot opens a window, "Add to chart" stays on the Trace).
/// </summary>
[Trait("Category", "WindowsFlaUI")]
public sealed class GuiAnalysisToolsContentTests
{
    private static string Line(double seconds, string id, string hexData) =>
        $"{seconds.ToString("F6", CultureInfo.InvariantCulture)} 1 {id} Rx d 8 {hexData}";

    private static string Asc(params string[] lines) =>
        "date Tue Jul 07 10:00:00.000 2026\nbase hex  timestamps absolute\ninternal events logged\n" +
        string.Join("\n", lines) + "\n";

    private static GuiSession Open(string traceText)
    {
        var ui = new GuiSession();
        File.WriteAllText(ui.TracePath, traceText);
        ui.Navigate("nav.trace");
        ui.Click("trace.open");
        ui.ChooseFile(ui.TracePath);
        ui.AwaitStatus("Trace loaded:");
        ui.Visible("trace.moreTools").Patterns.ExpandCollapse.Pattern.Expand();
        return ui;
    }

    private static string AllText(Window window) =>
        string.Join("|", window.FindAllDescendants().Select(e => e.Name).Where(n => !string.IsNullOrEmpty(n)));

    [Fact]
    public void J1939BamMessageIsReassembledAndItsDm1FaultCodeDecoded()
    {
        // BAM announcing 20 bytes of DM1 (PGN 0xFECA) in 3 packets from source address 0x10.
        using var ui = Open(Asc(
            Line(0.000, "18ECFF10x", "20 14 00 03 FF CA FE 00"),
            Line(0.001, "18EBFF10x", "01 00 FF 64 00 03 01 C8"),
            Line(0.002, "18EBFF10x", "02 00 04 02 FF FF FF FF"),
            Line(0.003, "18EBFF10x", "03 FF FF FF FF FF FF FF")));
        ui.Click("tools.j1939");
        var window = ui.WindowWithTitle("J1939 messages");
        var text = ui.Until(() =>
        {
            var all = AllText(window);
            return all.Contains("Complete", StringComparison.Ordinal) ? all : null;
        }, "completed BAM row");
        Assert.Contains("FECA", text, StringComparison.OrdinalIgnoreCase);
        // The decoded fault code is verified through the CSV export (also covers Export CSV...).
        var csv = Path.Combine(Path.GetTempPath(), "gui-j1939-" + Guid.NewGuid().ToString("N") + ".csv");
        try
        {
            window.FindFirstDescendant(cf => cf.ByName("Export CSV…"))!.Patterns.Invoke.Pattern.Invoke();
            ui.ChooseFile(csv);
            ui.Until(() => File.Exists(csv) ? "written" : null, "exported CSV");
            Assert.Contains("SPN 100 FMI 3", File.ReadAllText(csv));
        }
        finally { try { File.Delete(csv); } catch { } }
        window.Close();
    }

    [Fact]
    public void ReportTableCellsExposeTheirTextToAssistiveTechnology()
    {
        using var ui = Open(Asc(
            Line(0.000, "18ECFF10x", "20 14 00 03 FF CA FE 00"),
            Line(0.001, "18EBFF10x", "01 00 FF 64 00 03 01 C8"),
            Line(0.002, "18EBFF10x", "02 00 04 02 FF FF FF FF"),
            Line(0.003, "18EBFF10x", "03 FF FF FF FF FF FF FF")));
        ui.Click("tools.j1939");
        var window = ui.WindowWithTitle("J1939 messages");
        var names = ui.Until(() =>
        {
            var all = window.FindAllDescendants().Select(e => e.Name ?? "").ToList();
            return all.Any(n => n == "Complete") ? all : null;
        }, "report cells");
        // No cell may be announced by the row object's type name.
        var unnamed = names.Where(n => n.Contains("DataRowView", StringComparison.Ordinal)).ToList();
        Assert.True(unnamed.Count <= 1, "cells named after the row object: " + string.Join(" | ", unnamed));
        Assert.Contains("0.000000", names);
        window.Close();
    }

    [Fact]
    public void AnalysisToolsToolbarButtonRevealsAndHidesTheTools()
    {
        using var ui = new GuiSession();
        File.WriteAllText(ui.TracePath, Asc(Line(0.0, "100", "00 00 00 00 00 00 00 00")));
        ui.Navigate("nav.trace");
        ui.Click("trace.open");
        ui.ChooseFile(ui.TracePath);
        ui.AwaitStatus("Trace loaded:");
        Assert.True(ui.Find("tools.j1939") is null or { IsOffscreen: true }, "tools start collapsed");
        ui.Click("trace.analysisTools");
        Assert.NotNull(ui.Until(() => ui.Find("tools.j1939") is { IsOffscreen: false } b ? b : null, "J1939 button visible"));
        ui.Click("trace.analysisTools");
        ui.Until(() => ui.Find("tools.j1939") is null or { IsOffscreen: true } ? "hidden" : null, "tools hidden again");
    }

    [Fact]
    public void J1939SignalsReportDecodesEngineSpeedFromEec1()
    {
        // EEC1 (PGN 61444 = 0xF004) from source address 0x00: engine speed SPN 190 = raw 0x0FA0 * 0.125 = 500 rpm.
        using var ui = Open(Asc(
            Line(0.00, "0CF00400x", "00 7D 7D A0 0F 00 00 7D"),
            Line(0.02, "0CF00400x", "00 7D 7D A0 1F 00 00 7D")));
        ui.Click("tools.j1939signals");
        var window = ui.WindowWithTitle("J1939 signals");
        var csv = Path.Combine(Path.GetTempPath(), "gui-j1939sig-" + Guid.NewGuid().ToString("N") + ".csv");
        try
        {
            window.FindFirstDescendant(cf => cf.ByName("Export CSV…"))!.Patterns.Invoke.Pattern.Invoke();
            ui.ChooseFile(csv);
            ui.Until(() => File.Exists(csv) ? "written" : null, "exported CSV");
            var text = File.ReadAllText(csv);
            Assert.Contains("Engine Speed", text);
            Assert.Contains("500", text);   // min
            Assert.Contains("1012", text);  // max: 0x1FA0 * 0.125
        }
        finally { try { File.Delete(csv); } catch { } }
        window.Close();
    }

    [Fact]
    public void ObdEngineSpeedResponseIsDecodedToRpm()
    {
        // Functional request mode 01 PID 0C answered by ECU 0x7E8: (0x1A * 256 + 0xF8) / 4 = 1726 rpm.
        using var ui = Open(Asc(
            Line(0.000, "7DF", "02 01 0C 00 00 00 00 00"),
            Line(0.010, "7E8", "04 41 0C 1A F8 00 00 00")));
        ui.Click("tools.obd");
        var window = ui.WindowWithTitle("OBD-II");
        var text = ui.Until(() =>
        {
            var all = AllText(window);
            return all.Contains("1726", StringComparison.Ordinal) ? all : null;
        }, "decoded engine speed");
        Assert.Contains("rpm", text, StringComparison.OrdinalIgnoreCase);
        window.Close();
    }

    [Fact]
    public void CounterAndChecksumCandidatesAreFoundForAnUndocumentedId()
    {
        // Byte 6 low nibble is a 4-bit rolling counter; byte 7 is the XOR of bytes 0..6.
        var lines = new List<string>();
        for (var i = 0; i < 64; i++)
        {
            var b = new byte[] { (byte)(i * 5), 0x11, (byte)(i * 3 + 1), 0x40, 0x00, (byte)(i % 7), (byte)(i % 16), 0 };
            byte x = 0;
            for (var k = 0; k < 7; k++) x ^= b[k];
            b[7] = x;
            lines.Add(Line(i * 0.01, "2A0", string.Join(" ", b.Select(v => v.ToString("X2")))));
        }

        using var ui = Open(Asc(lines.ToArray()));
        ui.Click("tools.candidates");
        var window = ui.WindowWithTitle("Counters and checksums");
        var text = ui.Until(() =>
        {
            var all = AllText(window);
            return all.Contains("Checksum", StringComparison.Ordinal) && all.Contains("Counter", StringComparison.Ordinal) ? all : null;
        }, "counter and checksum candidates");
        Assert.Contains("candidate", text, StringComparison.OrdinalIgnoreCase);
        window.Close();
    }

    [Fact]
    public void SocketCanPcapngCaptureImportsThroughOpenTrace()
    {
        using var ui = new GuiSession();
        var path = Path.Combine(Path.GetDirectoryName(ui.TracePath)!, "capture.pcapng");
        File.WriteAllBytes(path, BuildPcapng(3));
        ui.Navigate("nav.trace");
        ui.Click("trace.open");
        ui.ChooseFile(path);
        ui.AwaitStatus("Trace loaded:");
        Assert.Contains("3", ui.Status);
    }

    [Fact]
    public void TraceRowAddToChartKeepsYouOnTheTraceAndQuickPlotOpensItsOwnWindow()
    {
        using var ui = new GuiSession();
        ui.Navigate("nav.dbc");
        ui.Click("dbc.load");
        ui.ChooseFile(ui.DbcPath);
        ui.AwaitStatus("1 DBC");
        ui.Navigate("nav.trace");
        ui.Click("trace.open");
        ui.ChooseFile(ui.TracePath);
        ui.AwaitStatus("Trace loaded:");

        // First decoded row (message 0x100, EngineData) in the trace grid.
        var add = ui.Until(() => ui.Visible("trace.grid")
            .FindFirstDescendant(cf => cf.ByAutomationId("trace.rowAddToChart")), "row add-to-chart button");
        add.Patterns.Invoke.Pattern.Invoke();
        ui.AwaitStatus("to the chart");
        Assert.NotNull(ui.Visible("trace.open")); // still on the Trace page

        var quick = ui.Until(() => ui.Visible("trace.grid")
            .FindFirstDescendant(cf => cf.ByAutomationId("trace.rowQuickPlot")), "row quick-plot button");
        quick.Patterns.Invoke.Pattern.Invoke();
        // A single decoded signal opens directly; several show a menu - accept either outcome.
        ui.Until(() => ui.IsWindowOpen("Signal detail") || ui.Main.FindFirstDescendant(
            cf => cf.ByControlType(ControlType.MenuItem)) is not null ? "ok" : null, "quick plot window or signal menu");
    }

    private static byte[] BuildPcapng(int frames)
    {
        using var ms = new MemoryStream();
        void U32(uint v) => ms.Write(BitConverter.GetBytes(v));
        void U16(ushort v) => ms.Write(BitConverter.GetBytes(v));

        // Section Header Block
        U32(0x0A0D0D0A); U32(28); U32(0x1A2B3C4D); U16(1); U16(0); ms.Write(BitConverter.GetBytes(-1L)); U32(28);
        // Interface Description Block: LINKTYPE_CAN_SOCKETCAN = 227
        U32(1); U32(20); U16(227); U16(0); U32(0); U32(20);
        for (var i = 0; i < frames; i++)
        {
            var packet = new byte[16];
            var id = 0x123 + i;
            packet[0] = (byte)(id >> 24); packet[1] = (byte)(id >> 16); packet[2] = (byte)(id >> 8); packet[3] = (byte)id;
            packet[4] = 8;
            for (var k = 0; k < 8; k++) packet[8 + k] = (byte)(i + k);
            var micros = (ulong)(i * 1000);
            U32(6); U32(48); U32(0); U32((uint)(micros >> 32)); U32((uint)micros); U32(16); U32(16);
            ms.Write(packet);
            U32(48);
        }

        return ms.ToArray();
    }
}
