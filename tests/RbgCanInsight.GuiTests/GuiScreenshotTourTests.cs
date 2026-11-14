using FlaUI.Core.AutomationElements;
using FlaUI.Core.Capturing;
using FlaUI.Core.Definitions;
using Xunit;

namespace RbgCanInsight.GuiTests;

/// <summary>
/// Usability review aid: captures every main page and every pop-out window of the real app into
/// the folder named by RBG_SCREENSHOT_DIR. It is skipped (passes immediately) when the variable is
/// not set, so the regular suite is unaffected.
/// </summary>
[Trait("Category", "WindowsFlaUI")]
public sealed class GuiScreenshotTourTests
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetProcessDPIAware();

    private static void Shot(AutomationElement element, string dir, string name)
    {
        Thread.Sleep(400);
        Capture.Element(element).ToFile(Path.Combine(dir, name + ".png"));
    }

    private static Window Pop(GuiSession ui, string button, string title, string dir, string shot)
    {
        ui.Click(button);
        var window = ui.WindowWithTitle(title);
        Shot(window, dir, shot);
        return window;
    }

    [Fact]
    public void CaptureEveryPageAndPopOut()
    {
        var dir = Environment.GetEnvironmentVariable("RBG_SCREENSHOT_DIR");
        if (string.IsNullOrWhiteSpace(dir))
            return;
        Directory.CreateDirectory(dir);
        // Without this the capture is taken in scaled coordinates and crops the window on high-DPI screens.
        SetProcessDPIAware();

        using var ui = new GuiSession();
        ui.Navigate("nav.dashboard");
        Shot(ui.Main, dir, "01-dashboard-empty");
        ui.Navigate("nav.dbc");
        ui.Click("dbc.load");
        ui.ChooseFile(ui.DbcPath);
        ui.AwaitStatus("1 DBC");
        ui.Navigate("nav.trace");
        ui.Click("trace.open");
        ui.ChooseFile(ui.TracePath);
        ui.AwaitStatus("Trace loaded:");
        ui.Navigate("nav.dashboard");
        Shot(ui.Main, dir, "02-dashboard-loaded");

        ui.Navigate("nav.trace");
        Shot(ui.Main, dir, "03-trace");
        ui.Visible("trace.workspaceFiles").Patterns.ExpandCollapse.Pattern.Expand();
        Shot(ui.Main, dir, "03b-trace-source-explorer");
        ui.Visible("trace.workspaceFiles").Patterns.ExpandCollapse.Pattern.Collapse();
        ui.Visible("trace.moreTools").Patterns.ExpandCollapse.Pattern.Expand();
        Shot(ui.Main, dir, "04-trace-analysis-tools-open");

        foreach (var (button, title, shot) in new[]
                 {
                     ("tools.j1939", "J1939 messages", "05-report-j1939"),
                     ("tools.obd", "OBD-II", "06-report-obd"),
                     ("tools.candidates", "Counters and checksums", "07-report-candidates"),
                     ("tools.busload", "Bus load timeline", "08-report-busload"),
                     ("tools.suggestDbc", "DBC suggestion", "09-report-suggest-dbc"),
                     ("tools.traceTools", "Trace tools", "10-trace-tools"),
                     ("tools.annotations", "Notes", "11-notes"),
                     ("tools.e2e", "E2E check", "12-e2e-empty-selection")
                 })
        {
            var w = Pop(ui, button, title, dir, shot);
            w.Close();
        }

        ui.Navigate("nav.signals");
        ui.Fill("signals.search", "EngCoolantTemp");
        var row = ui.Until(() => ui.Visible("signals.grid")
            .FindFirstDescendant(cf => cf.ByControlType(ControlType.DataItem)), "signal row");
        row.Patterns.SelectionItem.Pattern.Select();
        Shot(ui.Main, dir, "13-signals");
        ui.Until(() =>
        {
            var b = ui.Find("signals.analyze");
            if (b is null) return null;
            b.Patterns.Invoke.Pattern.Invoke();
            return "ok";
        }, "analyze");
        var analysis = ui.WindowWithTitle("Signal analysis");
        Shot(analysis, dir, "14-signal-analysis");
        analysis.Close();

        var charts = Pop(ui, "signals.charts", "Signal charts", dir, "15-charts-histogram");
        charts.FindFirstDescendant(cf => cf.ByAutomationId("charts.tab.xy"))!.Patterns.SelectionItem.Pattern.Select();
        Shot(charts, dir, "16-charts-xy");
        charts.FindFirstDescendant(cf => cf.ByAutomationId("charts.tab.spectrum"))!.Patterns.SelectionItem.Pattern.Select();
        Shot(charts, dir, "17-charts-spectrum");
        charts.Close();

        var derived = Pop(ui, "tools.derived", "Derived signals", dir, "18-derived-empty");
        derived.FindFirstDescendant(cf => cf.ByAutomationId("derived.expression"))!.AsTextBox().Text = "a * 2";
        Thread.Sleep(600);
        Shot(derived, dir, "19-derived-expression");
        derived.Close();

        ui.Navigate("nav.dbc");
        Shot(ui.Main, dir, "20-dbc-manager");
        var inspector = Pop(ui, "dbc.openInspector", "DBC signal inspector", dir, "21-dbc-inspector");
        inspector.Close();

        ui.Navigate("nav.uds");
        Shot(ui.Main, dir, "22-uds");

        ui.Navigate("nav.statistics");
        Shot(ui.Main, dir, "23-statistics");
        var reports = Pop(ui, "statistics.popOut", "Statistics reports", dir, "24-statistics-reports");
        reports.Close();

        ui.Navigate("nav.mapping");
        Shot(ui.Main, dir, "25-mapping");
    }

    private static readonly (string Nav, string Name)[] Pages =
    [
        ("nav.dashboard", "dashboard"), ("nav.trace", "trace"), ("nav.dataStudio", "data-studio"), ("nav.uds", "uds"), ("nav.dbc", "dbc"),
        ("nav.signals", "signals"), ("nav.matrix", "change-matrix"), ("nav.byteMatrix", "byte-matrix"),
        ("nav.statistics", "statistics"), ("nav.mapping", "mapping"), ("nav.investigation", "investigation")
    ];

    /// <summary>Every main page in the dark, light and high-contrast themes with a trace, DBC and catalog loaded,
    /// plus every page again in a small window (1100 x 700), so clipping and contrast defects are visible.</summary>
    [Fact]
    public void CaptureEveryPageInEveryThemeAndInASmallWindow()
    {
        var dir = Environment.GetEnvironmentVariable("RBG_SCREENSHOT_DIR");
        if (string.IsNullOrWhiteSpace(dir)) return;
        dir = Path.Combine(dir, "pages");
        Directory.CreateDirectory(dir);
        SetProcessDPIAware();

        for (var theme = 0; theme < 3; theme++)
        {
            using var ui = new GuiSession(themeIndex: theme);
            ui.Navigate("nav.dbc");
            ui.Click("dbc.load");
            ui.ChooseFile(ui.DbcPath);
            ui.AwaitStatus("1 DBC");
            ui.Navigate("nav.trace");
            ui.Click("trace.open");
            ui.ChooseFile(ui.TracePath);
            ui.AwaitStatus("Trace loaded:");
            ui.Navigate("nav.mapping");
            ui.Click("mapping.loadCatalog");
            ui.ChooseFile(ui.OdxPath);
            Thread.Sleep(1500);
            foreach (var (nav, name) in Pages)
            {
                ui.Navigate(nav);
                Shot(ui.Main, dir, $"theme{theme}-{name}");
            }
            ui.Navigate("nav.dataStudio");
            foreach (var section in new[] { "studio.datasets", "studio.analyses", "studio.results", "studio.compare" })
            {
                ui.Click(section);
                Shot(ui.Main, dir, $"theme{theme}-{section}");
            }

            if (theme != 0) continue;
            ui.Navigate("nav.dataStudio");
            ui.Click("studio.results");
            ui.Visible("studio.aggregatePanel").Patterns.ExpandCollapse.Pattern.Expand();
            ui.Click("studio.aggregateRun");
            ui.Until(() => ui.Find("studio.aggregateStatus")?.Name.Contains(
                    "recorded frames", StringComparison.OrdinalIgnoreCase) == true ? "ready" : null,
                "studio aggregation screenshot ready", 60);
            Shot(ui.Main, dir, "data-studio-aggregation");
            ui.ResizeTo(1100, 700);
            Thread.Sleep(800);
            foreach (var (nav, name) in Pages)
            {
                ui.Navigate(nav);
                Shot(ui.Main, dir, $"small-{name}");
            }

            ui.Navigate("nav.dataStudio");
            foreach (var section in new[] { "studio.datasets", "studio.analyses", "studio.results", "studio.compare" })
            {
                ui.Click(section);
                Shot(ui.Main, dir, $"small-{section}");
            }

            ui.Navigate("nav.dataStudio");
            ui.Click("studio.results");
            Shot(ui.Main, dir, "small-data-studio-aggregation");
            ui.Navigate("nav.trace");
            ui.Visible("trace.workspaceFiles").Patterns.ExpandCollapse.Pattern.Expand();
            Shot(ui.Main, dir, "small-trace-sources-open");
            ui.Visible("trace.workspaceFiles").Patterns.ExpandCollapse.Pattern.Collapse();
            ui.Visible("trace.moreTools").Patterns.ExpandCollapse.Pattern.Expand();
            Shot(ui.Main, dir, "small-trace-tools-open");
        }
    }
}
