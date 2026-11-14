using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using Xunit;

namespace RbgCanInsight.GuiTests;

/// <summary>Analysis tools on the Trace page open their evidence tables in separate windows.</summary>
[Trait("Category", "WindowsFlaUI")]
public sealed class GuiProtocolToolsTests
{
    private static GuiSession Loaded()
    {
        var ui = new GuiSession();
        ui.Navigate("nav.dbc");
        ui.Click("dbc.load");
        ui.ChooseFile(ui.DbcPath);
        ui.AwaitStatus("1 DBC");
        ui.Navigate("nav.trace");
        ui.Click("trace.open");
        ui.ChooseFile(ui.TracePath);
        ui.AwaitStatus("Trace loaded:");
        // The tools live inside a collapsed expander on the Trace page.
        ui.Visible("trace.moreTools").Patterns.ExpandCollapse.Pattern.Expand();
        return ui;
    }

    [Fact]
    public void SuggestBestDbcRanksTheLoadedDatabaseInItsOwnWindow()
    {
        using var ui = Loaded();
        ui.Click("tools.suggestDbc");
        var window = ui.WindowWithTitle("DBC suggestion");
        var grid = ui.Until(() => window.FindFirstDescendant(cf => cf.ByAutomationId("report.grid")),
            "suggestion table");
        Assert.NotNull(grid);
        var names = string.Join("|", window.FindAllDescendants().Select(e => e.Name).Where(n => !string.IsNullOrEmpty(n)).Take(40));
        Assert.True(window.FindFirstDescendant(cf => cf.ByName("fixture")) is not null, "cells: " + names);
        window.Close();
    }

    [Fact]
    public void ProtocolToolsOpenWithoutErrorsOnATraceWithoutThatProtocol()
    {
        using var ui = Loaded();
        foreach (var (button, title) in new[]
                 {
                     ("tools.j1939", "J1939 messages"), ("tools.obd", "OBD-II"),
                     ("tools.candidates", "Counters and checksums"), ("tools.busload", "Bus load timeline")
                 })
        {
            ui.Click(button);
            var window = ui.WindowWithTitle(title);
            Assert.NotNull(ui.Until(() => window.FindFirstDescendant(cf => cf.ByAutomationId("report.grid")), title + " table"));
            window.Close();
        }
    }

    [Fact]
    public void SignalAnalysisOpensStatisticsForTheSelectedRecordedSignal()
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
        ui.Navigate("nav.signals");
        ui.Fill("signals.search", "EngCoolantTemp");
        var row = ui.Until(() => ui.Visible("signals.grid")
            .FindFirstDescendant(cf => cf.ByControlType(ControlType.DataItem)), "engine signal row");
        row.Patterns.SelectionItem.Pattern.Select();
        ui.Until(() =>
        {
            var button = ui.Find("signals.analyze");
            if (button is null || !button.Patterns.Invoke.IsSupported) return null;
            button.Patterns.Invoke.Pattern.Invoke();
            return "invoked";
        }, "Signal analysis button");
        var window = ui.WindowWithTitle("Signal analysis");
        Assert.NotNull(ui.Until(() => window.FindFirstDescendant(cf => cf.ByName("Min / Max")), "summary rows"));
        window.Close();
    }
}
