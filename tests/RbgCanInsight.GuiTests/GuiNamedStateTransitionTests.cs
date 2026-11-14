using FlaUI.Core.Definitions;
using Xunit;

namespace RbgCanInsight.GuiTests;

/// <summary>
/// Real WPF + UIA3 acceptance for source-frame links and searchable named
/// DBC transitions. Uses only synthetic trace/DBC definitions.
/// </summary>
[Trait("Category", "WindowsFlaUI")]
public sealed class GuiNamedStateTransitionTests
{
    [Fact]
    public void RecordedModeStateChange_CanBeFilteredAndOpenedAtExactAfterFrame()
    {
        using var ui = LoadedSyntheticStates();
        OpenTransitions(ui);
        var grid = ui.Visible("statistics.stateTransitionsGrid");
        var first = ui.Until(() => grid.FindFirstDescendant(
            cf => cf.ByControlType(ControlType.DataItem)), "DBC state transition row");
        first.Press();
        Assert.True(ui.Visible("statistics.stateTransitionsOpenAfter").IsEnabled);
        ui.Click("statistics.stateTransitionsOpenAfter");
        Assert.NotNull(ui.Visible("trace.grid"));
        Assert.NotNull(ui.Visible("trace.search"));
        Assert.Equal(Fixtures.Dbc, File.ReadAllText(ui.DbcPath));
    }

    [Fact]
    public void SearchCanHideAndRestoreRecordedTransitionsWithoutChangingSource()
    {
        using var ui = LoadedSyntheticStates();
        OpenTransitions(ui);
        var before = File.ReadAllText(ui.TracePath);
        ui.Fill("statistics.stateTransitionsSearch", "DefinitelyMissingState");
        ui.Until(() =>
            ui.Visible("statistics.stateTransitionsGrid")
                .FindFirstDescendant(cf => cf.ByControlType(ControlType.DataItem)) is null
                ? "empty results" : null, "state transition search filters all rows");
        ui.Fill("statistics.stateTransitionsSearch", "Mode");
        var first = ui.Until(() =>
            ui.Visible("statistics.stateTransitionsGrid")
                .FindFirstDescendant(cf => cf.ByControlType(ControlType.DataItem)),
            "state transition search restored");
        first.Press();
        Assert.True(ui.Visible("statistics.stateTransitionsOpenBefore").IsEnabled);
        ui.Click("statistics.stateTransitionsOpenBefore");
        Assert.NotNull(ui.Visible("trace.grid"));
        Assert.Equal(before, File.ReadAllText(ui.TracePath));
    }

    [Fact]
    public void ReopeningStatisticsAfterTraceNavigationPreservesTransitionSearch()
    {
        using var ui = LoadedSyntheticStates();
        OpenTransitions(ui);
        ui.Fill("statistics.stateTransitionsSearch", "On");
        ui.Navigate("nav.trace");
        ui.Navigate("nav.statistics");
        OpenTransitions(ui);
        Assert.Equal("On", ui.Text("statistics.stateTransitionsSearch"));
        Assert.NotNull(ui.Visible("statistics.stateTransitionsGrid"));
    }

    private static GuiSession LoadedSyntheticStates()
    {
        var ui = new GuiSession();
        // First and third 0; second 1 => exactly two named state
        // transitions in the same message, with distinct physical frames.
        var asc = File.ReadAllText(ui.TracePath);
        const string previous = "0.100000 1 100 Rx d 8 37 28 00 00";
        const string changed = "0.100000 1 100 Rx d 8 37 28 01 00";
        Assert.Contains(previous, asc);
        File.WriteAllText(ui.TracePath, asc.Replace(previous, changed,
            StringComparison.Ordinal));

        ui.Navigate("nav.dbc");
        ui.Click("dbc.load");
        ui.ChooseFile(ui.DbcPath);
        ui.AwaitStatus("1 DBC");
        ui.Navigate("nav.trace");
        ui.Click("trace.open");
        ui.ChooseFile(ui.TracePath);
        ui.AwaitStatus("Trace loaded:");
        return ui;
    }

    private static void OpenTransitions(GuiSession ui)
    {
        ui.Navigate("nav.statistics");
        ui.Visible("statistics.stateTransitionsTab").AsTabItem().Select();
        Assert.NotNull(ui.Visible("statistics.stateTransitionsSearch"));
        Assert.NotNull(ui.Visible("statistics.stateTransitionsGrid"));
    }
}
