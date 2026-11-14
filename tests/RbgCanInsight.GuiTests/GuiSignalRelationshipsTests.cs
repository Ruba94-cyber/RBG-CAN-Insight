using System.Globalization;
using FlaUI.Core.Definitions;
using Xunit;

namespace RbgCanInsight.GuiTests;

/// <summary>Windows desktop acceptance for generic signal-to-signal comparison
/// from the simple CANalyzer-style workspace. Fixtures are synthetic only.</summary>
[Trait("Category", "WindowsFlaUI")]
public sealed class GuiSignalRelationshipsTests
{
    [Fact]
    public void SimpleWorkspaceFindsRealSourcePairedSignalsAndKeepsMainTrend()
    {
        using var ui = new GuiSession();
        var records = Enumerable.Range(0, 30).Select(index =>
        {
            var t = index * 0.1;
            var c = 50 + index * 2;
            var l = 20 + index * 3;
            return $"{t.ToString("F6", CultureInfo.InvariantCulture)} 1 100 Rx d 8 {c:X2} {l:X2} 00 00 00 00 00 00";
        });
        File.WriteAllText(ui.TracePath,
            "date Tue Jul 07 10:00:00.000 2026\nbase hex  timestamps absolute\ninternal events logged\n" +
            string.Join("\n", records));

        ui.Navigate("nav.dbc");
        ui.Click("dbc.load");
        ui.ChooseFile(ui.DbcPath);
        ui.AwaitStatus("1 DBC");
        ui.Navigate("nav.trace");
        ui.Click("trace.open");
        ui.ChooseFile(ui.TracePath);
        ui.AwaitStatus("Trace loaded:");

        ui.Navigate("nav.investigation");
        Assert.NotNull(ui.Visible("investigation.signalsGrid"));
        Assert.NotNull(ui.Visible("investigation.signalChart"));
        Assert.NotNull(ui.Visible("investigation.findRelationsMain"));

        // No manual DTC/UDS and no need to configure a custom pane.
        // When none is selected the app picks an active observed signal as
        // a clearly identified exploration starting point.
        ui.Click("investigation.findRelationsMain");
        var popup = ui.WindowWithTitle("Explore related signals —");
        var grid = popup.FindFirstDescendant(cf => cf.ByAutomationId("relations.grid"));
        Assert.NotNull(grid);
        ui.Until(() => grid!.Patterns.Grid.PatternOrDefault?.RowCount.ValueOrDefault > 0
            ? "recorded association result" : null, "two source-paired changing CAN signals");
        Assert.NotNull(popup.FindFirstDescendant(cf => cf.ByAutomationId("relations.previewChart")));
        Assert.NotNull(popup.FindFirstDescendant(cf => cf.ByAutomationId("relations.addBoth")));

        popup.FindFirstDescendant(cf => cf.ByAutomationId("relations.addBoth"))!.Press();
        Assert.Contains("Added", ui.Status, StringComparison.OrdinalIgnoreCase);
        popup.Close();
        Assert.NotNull(ui.Visible("investigation.signalChart"));
    }

    [Fact]
    public void NoDbcDataRequiresSelectionWithoutInventingCorrelation()
    {
        using var ui = new GuiSession();
        ui.Navigate("nav.investigation");
        ui.Click("investigation.findRelationsMain");
        Assert.False(ui.IsWindowOpen("Explore related signals —"));
        Assert.Contains("Select a decoded recorded signal", ui.Status);
        Assert.NotNull(ui.Visible("investigation.openTrace"));
    }
}
