using System.Globalization;
using FlaUI.Core.Definitions;
using Xunit;

namespace RbgCanInsight.GuiTests;

/// <summary>Synthetic offline signals only; full Windows UI verification
/// requires an interactive development desktop and FlaUI.</summary>
[Trait("Category", "WindowsFlaUI")]
public sealed class GuiSignalRelationshipMapTests
{
    [Fact]
    public void AutoMapWorksWithoutChoosingADtcOrSignalAndKeepsMainPlotIntact()
    {
        using var ui = new GuiSession();
        WriteSource(ui.TracePath);
        ui.Navigate("nav.dbc");
        ui.Click("dbc.load");
        ui.ChooseFile(ui.DbcPath);
        ui.AwaitStatus("1 DBC");
        ui.Navigate("nav.trace");
        ui.Click("trace.open");
        ui.ChooseFile(ui.TracePath);
        ui.AwaitStatus("Trace loaded:");

        ui.Navigate("nav.investigation");
        Assert.NotNull(ui.Visible("investigation.discoverGroups"));
        ui.Click("investigation.discoverGroups");
        var popup = ui.WindowWithTitle("CAN signal relationship map —");
        Assert.NotNull(popup.FindFirstDescendant(
            cf => cf.ByAutomationId("relationMap.groups")));
        var links = popup.FindFirstDescendant(
            cf => cf.ByAutomationId("relationMap.links"));
        Assert.NotNull(links);
        ui.Until(() => links!.Patterns.Grid.PatternOrDefault?.RowCount.ValueOrDefault > 0
            ? "pairwise source-backed related signals" : null,
            "automatically detected group of related signals");
        Assert.NotNull(popup.FindFirstDescendant(
            cf => cf.ByAutomationId("relationMap.graph")));
        var searchGroups = popup.FindFirstDescendant(
            cf => cf.ByAutomationId("relationMap.searchGroups"))!.AsTextBox();
        searchGroups.Text = "EngineLoad";
        ui.Until(() => links!.Patterns.Grid.PatternOrDefault?.RowCount.ValueOrDefault > 0
            ? "matching engine load group" : null, "filtered CAN group");
        searchGroups.Text = "";
        Assert.NotNull(popup.FindFirstDescendant(
            cf => cf.ByAutomationId("relationMap.node")));
        Assert.NotNull(popup.FindFirstDescendant(
            cf => cf.ByAutomationId("relationMap.plot")));

        Assert.NotNull(popup.FindFirstDescendant(
            cf => cf.ByAutomationId("relationMap.inspectChanges")));
        popup.FindFirstDescendant(
            cf => cf.ByAutomationId("relationMap.inspectChanges"))!.Press();
        var events = ui.WindowWithTitle("Observed CAN changes —");
        var eventGrid = events.FindFirstDescendant(
            cf => cf.ByAutomationId("relationWitness.events"));
        Assert.NotNull(eventGrid);
        ui.Until(() => eventGrid!.Patterns.Grid.PatternOrDefault?.RowCount.ValueOrDefault > 0
            ? "observed before/after original-frame changes" : null,
            "source-backed transition witness rows");
        Assert.NotNull(events.FindFirstDescendant(
            cf => cf.ByAutomationId("relationWitness.referenceAfter")));
        Assert.NotNull(events.FindFirstDescendant(
            cf => cf.ByAutomationId("relationWitness.candidateAfter")));
        events.Close();

        popup.FindFirstDescendant(
            cf => cf.ByAutomationId("relationMap.addPair"))!.Press();
        Assert.Contains("Added", ui.Status);
        popup.Close();
        Assert.NotNull(ui.Visible("investigation.signalChart"));
    }

    [Fact]
    public void MapExplicitlyScopesOneOriginalLoggerInMultiFileSession()
    {
        using var ui = new GuiSession();
        WriteSource(ui.TracePath);
        var second = Path.Combine(ui.Root, "independent-source.asc");
        File.Copy(ui.TracePath, second);
        ui.Navigate("nav.dbc");
        ui.Click("dbc.load");
        ui.ChooseFile(ui.DbcPath);
        ui.AwaitStatus("1 DBC");
        ui.Navigate("nav.trace");
        ui.Click("trace.open");
        ui.ChooseFiles(ui.TracePath, second);
        var mode = ui.WindowWithTitle("Merge mode");
        mode.FindFirstDescendant(cf => cf.ByAutomationId("merge.ok"))!
            .Patterns.Invoke.Pattern.Invoke();
        ui.AwaitStatus("Trace loaded:");

        ui.Navigate("nav.investigation");
        ui.Visible("investigation.loggerScope").AsComboBox().Select(2);
        ui.Click("investigation.discoverGroups");
        var popup = ui.WindowWithTitle("CAN signal relationship map —");
        Assert.Contains("Logger 2", popup.Title);
        Assert.NotNull(popup.FindFirstDescendant(
            cf => cf.ByAutomationId("relationMap.links")));
        popup.Close();
    }

    [Fact]
    public void EmptyCanExplorerDoesNotInventAStatisticalRelationshipMap()
    {
        using var ui = new GuiSession();
        ui.Navigate("nav.investigation");
        ui.Click("investigation.discoverGroups");
        Assert.False(ui.IsWindowOpen("CAN signal relationship map —"));
        Assert.Contains("Load a CAN trace", ui.Status);
    }

    private static void WriteSource(string path)
    {
        // 30 independently recorded CAN frames. Both signals are decoded
        // from the SAME message: this is observable co-variation only, not
        // ECU influence or independently verified causal logic.
        var rows = Enumerable.Range(0, 30).Select(index =>
        {
            var c = 30 + (index * 7 + index * index * 3) % 90;
            var load = 20 + (index * 7 + index * index * 3) % 90;
            return $"{(index * 0.1).ToString("F6", CultureInfo.InvariantCulture)} 1 100 Rx d 8 {c:X2} {load:X2} 00 00 00 00 00 00";
        });
        File.WriteAllText(path,
            "date Tue Jul 07 10:00:00.000 2026\nbase hex  timestamps absolute\ninternal events logged\n" +
            string.Join("\n", rows));
    }
}
