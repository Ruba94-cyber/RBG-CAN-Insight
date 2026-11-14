using FlaUI.Core.Definitions;
using Xunit;

namespace RbgCanInsight.GuiTests;

/// <summary>One synthetic DTC 0x19 observation and real DBC decoded changes
/// in a single original trace. No ECU action and no OEM source files.</summary>
[Trait("Category", "WindowsFlaUI")]
public sealed class GuiFaultContextTests
{
    [Fact]
    public void SelectedRecordedDtcEventOpensOriginalSourceBackedConditionsAndTrend()
    {
        using var ui = new GuiSession();
        File.WriteAllText(ui.TracePath, """
            date Tue Jul 07 10:00:00.000 2026
            base hex  timestamps absolute
            internal events logged
            0.000000 1 100 Rx d 8 32 20 00 00 00 00 00 00
            0.100000 1 100 Rx d 8 37 28 00 00 00 00 00 00
            0.200000 1 100 Rx d 8 3C 30 00 00 00 00 00 00
            0.210000 1 7E0 Rx d 8 03 19 02 FF 00 00 00 00
            0.220000 1 7E8 Rx d 8 07 59 02 FF 12 34 56 0D
            """);

        ui.Navigate("nav.dbc");
        ui.Click("dbc.load");
        ui.ChooseFile(ui.DbcPath);
        ui.AwaitStatus("1 DBC");

        ui.Navigate("nav.dataStudio");
        ui.Click("studio.openRecords");
        ui.ChooseFile(ui.TracePath);
        ui.AwaitStatus("Trace loaded:");
        ui.Click("studio.compare");
        ui.Until(() => ui.Visible("studio.dtcGrid")
            .FindFirstDescendant(cf => cf.ByControlType(ControlType.DataItem)) is not null
            ? "recorded DTC" : null, "DTC history from synthetic 0x19");

        ui.Click("studio.investigateFaultContext");
        var popup = ui.WindowWithTitle("Fault context —");
        var grid = popup.FindFirstDescendant(cf => cf.ByAutomationId("faultContext.changesGrid"));
        Assert.NotNull(grid);
        Assert.NotNull(popup.FindFirstDescendant(cf => cf.ByAutomationId("faultContext.chart")));
        Assert.NotNull(popup.FindFirstDescendant(cf => cf.ByAutomationId("faultContext.openEventFrame")));

        // The selected fault was read after the engine values changed; the app
        // should offer those recorded transitions but NEVER assert their cause.
        ui.Until(() => grid.Patterns.Grid.PatternOrDefault?.RowCount.ValueOrDefault > 0
            ? "observed nearby changes" : null, "signal evidence near recorded DTC");
        Assert.NotNull(popup.FindFirstDescendant(cf => cf.ByAutomationId("faultContext.addTopFive")));
        var didTab = popup.FindAllDescendants(cf => cf.ByControlType(ControlType.TabItem))
            .FirstOrDefault(item => item.Name.StartsWith("Snapshot DID", StringComparison.Ordinal));
        Assert.NotNull(didTab);
        didTab!.AsTabItem().Select();
        var leadsGrid = ui.Until(() => popup.FindFirstDescendant(
            cf => cf.ByAutomationId("faultContext.snapshotLeadsGrid")),
            "separate source-backed DID-to-CAN evidence list");
        Assert.NotNull(leadsGrid);
        Assert.False(popup.FindFirstDescendant(
            cf => cf.ByAutomationId("faultContext.addDidCan"))!.IsEnabled);
        popup.Close();
        Assert.NotNull(ui.Visible("studio.dtcTimeline"));
    }

    [Fact]
    public void EmptyDiagnosticHistoryDoesNotInventFaultContext()
    {
        using var ui = new GuiSession();
        ui.Navigate("nav.dataStudio");
        ui.Click("studio.compare");
        ui.Click("studio.investigateFaultContext");
        Assert.False(ui.IsWindowOpen("Fault context —"));
        Assert.Contains("Select a DTC", ui.Status);
    }
}
