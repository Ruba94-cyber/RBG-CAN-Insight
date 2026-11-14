using FlaUI.Core.Definitions;
using Xunit;

namespace RbgCanInsight.GuiTests;

/// <summary>Windows-only acceptance for compact trace, direct signal plotting
/// and the source-backed CANalyzer-style investigation layout.</summary>
[Trait("Category", "WindowsFlaUI")]
public sealed class GuiSignalsTrendAndCompactToolsTests
{
    [Fact]
    public void TraceDecodedSignalAddsToPersistentMainPlotWithoutLeavingTrace()
    {
        using var ui = new GuiSession();
        LoadSyntheticTraceAndDbc(ui);
        var grid = ui.Visible("trace.grid");
        var expand = grid.FindFirstDescendant(
            cf => cf.ByName("Toggle row decoded details"));
        Assert.NotNull(expand);
        expand.Press();

        ui.Click("traceSignal.addToMain");
        Assert.NotNull(ui.Visible("trace.grid"));
        ui.Navigate("nav.signals");
        Assert.Contains("plotted here", ui.Visible("signals.summary").Name);
        Assert.NotNull(ui.Visible("signals.chart"));
    }

    [Fact]
    public void DashboardOpensSharedSignalExplorerAndTrendWithExactTimeReadout()
    {
        using var ui = new GuiSession();
        LoadSyntheticTraceAndDbc(ui);
        ui.Navigate("nav.dashboard");
        ui.Click("dashboard.signalsTrend");
        var signals = ui.Visible("investigation.signalsGrid");
        ui.Until(() => signals.Patterns.Grid.PatternOrDefault?.RowCount.ValueOrDefault > 0
            ? "recorded signals visible" : null, "source-backed signal catalog", 35);
        Assert.NotNull(ui.Visible("investigation.signalChart"));
        Assert.NotNull(ui.Visible("investigation.cursorReadout"));
        Assert.NotNull(ui.Visible("investigation.findRelationsMain"));
        Assert.NotNull(ui.Visible("investigation.openTrace"));
        Assert.NotNull(ui.Visible("investigation.loadDbc"));
        var advanced = ui.Visible("investigation.advanced");
        // Engineering-only actions are intentionally collapsed until requested.
        advanced.Patterns.ExpandCollapse.Pattern.Expand();
        Assert.NotNull(ui.Visible("investigation.layout"));
        Assert.NotNull(ui.Visible("investigation.findNearbyChanges"));
    }

    [Fact]
    public void SimpleSignalsExplorerExplicitlyScopesPhysicalLoggerWhenTwoTracesAreLoaded()
    {
        using var ui = new GuiSession();
        var other = Path.Combine(ui.Root, "other-physical-logger.asc");
        File.Copy(ui.TracePath, other);
        ui.Navigate("nav.dbc");
        ui.Click("dbc.load");
        ui.ChooseFile(ui.DbcPath);
        ui.AwaitStatus("1 DBC");
        ui.Navigate("nav.trace");
        ui.Click("trace.open");
        ui.ChooseFiles(ui.TracePath, other);
        var mode = ui.WindowWithTitle("Merge mode");
        mode.FindFirstDescendant(cf => cf.ByAutomationId("merge.ok"))!
            .Patterns.Invoke.Pattern.Invoke();
        ui.AwaitStatus("Trace loaded:");

        ui.Navigate("nav.investigation");
        var picker = ui.Visible("investigation.loggerScope").AsComboBox();
        var grid = ui.Visible("investigation.signalsGrid");
        picker.Select(2);
        ui.Until(() => grid.Patterns.Grid.PatternOrDefault?.RowCount.ValueOrDefault == 4
            ? "four observed signals from second original logger" : null,
            "source scope in simple CAN Explorer");
        Assert.NotNull(ui.Visible("investigation.signalChart"));
        picker.Select(0);
        ui.Until(() => grid.Patterns.Grid.PatternOrDefault?.RowCount.ValueOrDefault == 8
            ? "all eight source-distinct decoded curves" : null,
            "all source signals visible again");
    }

    [Fact]
    public void SignalTriggersRuleEditorAndResultsShareOneResizablePopup()
    {
        using var ui = new GuiSession();
        ui.Navigate("nav.statistics");
        ui.Visible("statistics.triggerTab").AsTabItem().Select();
        ui.Click("statistics.openTriggerEditor");
        var popup = ui.WindowWithTitle("Signal triggers —");
        Assert.NotNull(popup.FindFirstDescendant(cf => cf.ByAutomationId("statistics.triggerSignal")));
        Assert.NotNull(popup.FindFirstDescendant(cf => cf.ByAutomationId("statistics.triggerGrid")));
        popup.Close();
        Assert.NotNull(ui.Visible("statistics.openTriggerEditor"));
    }

    [Fact]
    public void OptionalTraceToolsOpenSeparatelyWithoutRemovingTraceGrid()
    {
        using var ui = new GuiSession();
        ui.Navigate("nav.trace");
        ui.Click("trace.popOutTools");
        var popup = ui.WindowWithTitle("Trace analysis tools —");
        Assert.NotNull(popup.FindFirstDescendant(cf => cf.ByAutomationId("trace.moreTools")));
        Assert.NotNull(ui.Visible("trace.grid"));
        popup.Close();
        Assert.NotNull(ui.Visible("trace.popOutTools"));
    }

    private static void LoadSyntheticTraceAndDbc(GuiSession ui)
    {
        ui.Navigate("nav.dbc");
        ui.Click("dbc.load");
        ui.ChooseFile(ui.DbcPath);
        ui.AwaitStatus("1 DBC");
        ui.Navigate("nav.trace");
        ui.Click("trace.open");
        ui.ChooseFile(ui.TracePath);
        ui.AwaitStatus("Trace loaded:");
    }
}
