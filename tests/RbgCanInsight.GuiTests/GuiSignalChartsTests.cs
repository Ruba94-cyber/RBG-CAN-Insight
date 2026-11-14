using FlaUI.Core.AutomationElements;
using Xunit;

namespace RbgCanInsight.GuiTests;

/// <summary>The Signal charts window offers histogram, XY and spectrum views of recorded signals.</summary>
[Trait("Category", "WindowsFlaUI")]
public sealed class GuiSignalChartsTests
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
        ui.Navigate("nav.signals");
        return ui;
    }

    private static AutomationElement InWindow(GuiSession ui, FlaUI.Core.AutomationElements.Window window, string id) =>
        ui.Until(() => window.FindFirstDescendant(cf => cf.ByAutomationId(id)), "chart control " + id);

    [Fact]
    public void ChartsWindowShowsHistogramXyAndSpectrumTabs()
    {
        using var ui = Loaded();
        ui.Click("signals.charts");
        var window = ui.WindowWithTitle("Signal charts");

        foreach (var tab in new[] { "charts.tab.histogram", "charts.tab.xy", "charts.tab.spectrum" })
            Assert.NotNull(InWindow(ui, window, tab));

        var summary = InWindow(ui, window, "charts.histogram.summary");
        ui.Until(() => string.IsNullOrWhiteSpace(summary.Name) ? null : summary.Name, "histogram summary text");
        Assert.False(string.IsNullOrWhiteSpace(summary.Name));

        InWindow(ui, window, "charts.tab.xy").Patterns.SelectionItem.Pattern.Select();
        var y = InWindow(ui, window, "charts.xy.signalY").AsComboBox();
        y.Select(1);
        var correlation = InWindow(ui, window, "charts.xy.correlation");
        ui.Until(() => correlation.Name.Contains("r =", StringComparison.Ordinal) ? correlation.Name : null,
            "correlation text containing 'r ='");

        InWindow(ui, window, "charts.tab.spectrum").Patterns.SelectionItem.Pattern.Select();
        Assert.NotNull(InWindow(ui, window, "charts.spectrum.rate"));
        window.Close();
    }
}
