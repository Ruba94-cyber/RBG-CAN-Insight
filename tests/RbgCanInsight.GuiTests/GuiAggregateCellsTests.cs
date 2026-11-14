using System.Text.Json;
using Xunit;

namespace RbgCanInsight.GuiTests;

/// <summary>Source-backed read-only aggregation cells in the guided Data Studio.
/// Runs only on an interactive Windows desktop with the built WPF application.</summary>
[Trait("Category", "WindowsFlaUI")]
public sealed class GuiAggregateCellsTests
{
    [Fact]
    public void AnalystCanGroupActualVisibleFramesAndSaveOneReproducibleCell()
    {
        using var ui = new GuiSession();
        ui.Navigate("nav.dataStudio");
        ui.Click("studio.openRecords");
        ui.ChooseFile(ui.TracePath);
        ui.AwaitStatus("Trace loaded:");

        ui.Click("studio.exploreRecords");
        ui.Visible("studio.aggregatePanel").Patterns.ExpandCollapse.Pattern.Expand();
        ui.Click("studio.aggregateRun");
        ui.Until(() => ui.Find("studio.aggregateStatus")?.Name.Contains(
                "recorded frames", StringComparison.OrdinalIgnoreCase) == true ? "ready" : null,
            "source-backed aggregate finished", 60);
        Assert.NotNull(ui.Visible("studio.aggregateGrid"));
        var grid = ui.Visible("studio.aggregateGrid");
        Assert.NotNull(grid);
        ui.Click("studio.aggregateSave");
        ui.AwaitStatus("Analysis cell saved");
        Assert.False(ui.Visible("studio.savedCells").IsOffscreen);
        Assert.NotNull(ui.Visible("studio.rerunCell"));

        // The session stores a calculation recipe only: never a cached row
        // or a made-up frame witness.
        var session = Path.Combine(ui.Root, "aggregate-cell.rbgsession");
        ui.Click("studio.saveSession");
        ui.ChooseFile(session);
        ui.AwaitStatus("Session saved:");
        using var json = JsonDocument.Parse(File.ReadAllText(session));
        var tabs = json.RootElement.GetProperty("TraceNotebooks");
        var tab = tabs[0];
        Assert.Equal(1, tab.GetProperty("Cells").GetArrayLength());
        var cell = tab.GetProperty("Cells")[0];
        Assert.Equal(0, cell.GetProperty("Kind").GetInt32());
        Assert.False(cell.TryGetProperty("Frames", out _));
        Assert.False(cell.TryGetProperty("FirstFrameIndex", out _));

        // Reopen and actively recompute from the original file.
        ui.Visible("utilities.panel").Patterns.ExpandCollapse.Pattern.Expand();
        ui.Click("utilities.loadSession");
        ui.ChooseFile(session);
        ui.AwaitStatus("Session restored:");
        ui.Navigate("nav.dataStudio");
        ui.Click("studio.analyses");
        ui.Click("studio.rerunCell");
        ui.Until(() => ui.Find("studio.aggregateStatus")?.Name.Contains(
                "recorded frames", StringComparison.OrdinalIgnoreCase) == true ? "ready" : null,
            "saved cell rerun", 60);
        Assert.False(ui.Visible("studio.aggregateGrid").IsOffscreen);
    }

    [Fact]
    public void SourceGroupingRemainsIndependentAcrossTwoLoggers()
    {
        using var ui = new GuiSession();
        var other = Path.Combine(ui.Root, "second-aggregate.asc");
        File.Copy(ui.TracePath, other);
        ui.Navigate("nav.dataStudio");
        ui.Click("studio.openRecords");
        ui.ChooseFiles(ui.TracePath, other);
        var mode = ui.WindowWithTitle("Merge mode");
        mode.FindFirstDescendant(cf => cf.ByAutomationId("merge.ok"))!
            .Patterns.Invoke.Pattern.Invoke();
        ui.AwaitStatus("Trace loaded:");
        ui.Click("studio.results");
        ui.Visible("studio.aggregatePanel").Patterns.ExpandCollapse.Pattern.Expand();
        ui.Visible("studio.aggregateKind").AsComboBox().Select(1);
        ui.Click("studio.aggregateRun");
        ui.Until(() => ui.Find("studio.aggregateStatus")?.Name.Contains(
                "2 group", StringComparison.OrdinalIgnoreCase) == true ? "ready" : null,
            "two physical loggers aggregated", 60);
        Assert.False(ui.Visible("studio.aggregateGrid").IsOffscreen);
    }


    [Fact]
    public void AnalystCanInspectSourceScopedOneSecondTrafficPeaksAndSaveRecipe()
    {
        using var ui = new GuiSession();
        ui.Navigate("nav.dataStudio");
        ui.Click("studio.openRecords");
        ui.ChooseFile(ui.TracePath);
        ui.AwaitStatus("Trace loaded:");
        ui.Click("studio.results");
        ui.Visible("studio.aggregatePanel").Patterns.ExpandCollapse.Pattern.Expand();
        ui.Visible("studio.aggregateKind").AsComboBox().Select(2);
        ui.Click("studio.aggregateRun");
        ui.Until(() => ui.Find("studio.aggregateStatus")?.Name.Contains(
            "source-clock windows", StringComparison.OrdinalIgnoreCase) == true ? "ready" : null,
            "one-second source-backed traffic peaks", 60);
        Assert.NotNull(ui.Visible("studio.aggregateGrid"));
        ui.Click("studio.aggregateSave");
        ui.AwaitStatus("Analysis cell saved");
        var session = Path.Combine(ui.Root, "traffic-peak-cell.rbgsession");
        ui.Click("studio.saveSession");
        ui.ChooseFile(session);
        ui.AwaitStatus("Session saved:");
        using var document = JsonDocument.Parse(File.ReadAllText(session));
        Assert.Equal(2, document.RootElement.GetProperty("TraceNotebooks")[0]
            .GetProperty("Cells")[0].GetProperty("Kind").GetInt32());
    }

    [Fact]
    public void CsvExportUsesOnlyComputedRowsAndKeepsSourceWitnessColumns()
    {
        using var ui = new GuiSession();
        ui.Navigate("nav.dataStudio");
        ui.Click("studio.openRecords");
        ui.ChooseFile(ui.TracePath);
        ui.AwaitStatus("Trace loaded:");
        ui.Click("studio.results");
        ui.Visible("studio.aggregatePanel").Patterns.ExpandCollapse.Pattern.Expand();
        Assert.False(ui.Visible("studio.aggregateExport").IsEnabled);
        ui.Click("studio.aggregateRun");
        ui.Until(() => ui.Find("studio.aggregateStatus")?.Name.Contains(
            "recorded frames", StringComparison.OrdinalIgnoreCase) == true ? "ready" : null,
            "computed CSV source", 60);
        Assert.True(ui.Visible("studio.aggregateExport").IsEnabled);

        var destination = Path.Combine(ui.Root, "analyzed-frames.csv");
        ui.Click("studio.aggregateExport");
        ui.ChooseFile(destination);
        ui.AwaitStatus("Exported");
        Assert.True(File.Exists(destination));
        var csv = File.ReadAllText(destination);
        Assert.Contains("First merged frame (1-based)", csv);
        Assert.Contains("Truncated results", csv);
        Assert.Contains(Path.GetFileName(ui.TracePath), csv);
    }

    [Fact]
    public void UpdatingFilterInvalidatesAggregationBeforeItCanBeSavedOrExported()
    {
        using var ui = new GuiSession();
        ui.Navigate("nav.dataStudio");
        ui.Click("studio.openRecords");
        ui.ChooseFile(ui.TracePath);
        ui.AwaitStatus("Trace loaded:");
        ui.Click("studio.results");
        ui.Visible("studio.aggregatePanel").Patterns.ExpandCollapse.Pattern.Expand();
        ui.Click("studio.aggregateRun");
        ui.Until(() => ui.Find("studio.aggregateStatus")?.Name.Contains(
            "recorded frames", StringComparison.OrdinalIgnoreCase) == true ? "ready" : null,
            "computed initial summary", 60);
        ui.Fill("studio.query", "id:7E0");
        ui.Click("studio.runFilter");
        ui.Click("studio.aggregateSave");
        ui.Until(() => ui.Find("studio.aggregateStatus")?.Name.Contains(
            "Run the summary again", StringComparison.OrdinalIgnoreCase) == true ? "stale" : null,
            "stale aggregation rejected");
    }
}
