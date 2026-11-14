using System.Globalization;
using System.Text.RegularExpressions;
using FlaUI.Core.AutomationElements;
using Xunit;

namespace RbgCanInsight.GuiTests;

/// <summary>The Derived signals window computes offline expressions over recorded signals.</summary>
[Trait("Category", "WindowsFlaUI")]
public sealed class GuiDerivedSignalsTests
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

    private static AutomationElement InWindow(GuiSession ui, Window window, string id) =>
        ui.Until(() => window.FindFirstDescendant(cf => cf.ByAutomationId(id)), id);

    private static void SetExpression(GuiSession ui, Window window, string expression) =>
        InWindow(ui, window, "derived.expression").AsTextBox().Text = expression;

    private static void BindVariable(GuiSession ui, Window window, string variable, string signalName)
    {
        var combo = InWindow(ui, window, "derived.var." + variable).AsComboBox();
        combo.Expand();
        var item = ui.Until(() => combo.Items.FirstOrDefault(
            i => (i.Text ?? i.Name ?? "").Contains(signalName, StringComparison.Ordinal)), "choice " + signalName);
        item.Select();
        combo.Collapse();
    }

    private static double MaxFromStats(string stats)
    {
        var match = Regex.Match(stats, @"Max: (?<v>[-+0-9.eE]+)");
        Assert.True(match.Success, "stats: " + stats);
        return double.Parse(match.Groups["v"].Value, CultureInfo.InvariantCulture);
    }

    private static double PreviewMax(GuiSession ui, Window window)
    {
        InWindow(ui, window, "derived.preview").Patterns.Invoke.Pattern.Invoke();
        var stats = ui.Until(() =>
        {
            var text = InWindow(ui, window, "derived.stats").Name;
            return text.Contains("Max:", StringComparison.Ordinal) ? text : null;
        }, "preview statistics");
        return MaxFromStats(stats);
    }

    [Fact]
    public void DoubledSignalHasDoubledMaximum()
    {
        using var ui = Loaded();
        ui.Click("tools.derived");
        var window = ui.WindowWithTitle("Derived signals");

        SetExpression(ui, window, "a");
        BindVariable(ui, window, "a", "EngCoolantTemp");
        var baseline = PreviewMax(ui, window);

        SetExpression(ui, window, "a * 2");
        var doubled = PreviewMax(ui, window);

        Assert.Equal(baseline * 2, doubled, 6);
        window.Close();
    }

    [Fact]
    public void DerivedSignalCanBeAddedToTheMainChartAndStaysListed()
    {
        using var ui = Loaded();
        ui.Click("tools.derived");
        var window = ui.WindowWithTitle("Derived signals");
        SetExpression(ui, window, "a * 2");
        BindVariable(ui, window, "a", "EngCoolantTemp");
        PreviewMax(ui, window);
        InWindow(ui, window, "derived.addToChart").Patterns.Invoke.Pattern.Invoke();
        window.Close();
        // The derived signal is now an ordinary, plotted row of the Signals list, clearly marked as not recorded.
        ui.Fill("signals.search", "Derived");
        var row = ui.Until(() => ui.Visible("signals.grid")
            .FindFirstDescendant(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.DataItem)), "derived row in the signal list");
        Assert.NotNull(row);
    }

    [Fact]
    public void InvalidExpressionShowsErrorWithPosition()
    {
        using var ui = Loaded();
        ui.Click("tools.derived");
        var window = ui.WindowWithTitle("Derived signals");

        SetExpression(ui, window, "a * ");
        var error = ui.Until(() =>
        {
            var element = window.FindFirstDescendant(cf => cf.ByAutomationId("derived.error"));
            return element is not null && element.Name.Contains("character", StringComparison.OrdinalIgnoreCase)
                ? element.Name : null;
        }, "expression error");
        Assert.Matches(@"character \d+", error);
        window.Close();
    }
}
