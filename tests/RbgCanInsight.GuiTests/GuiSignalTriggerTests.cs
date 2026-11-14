using System.IO.Compression;
using System.Text.Json;
using FlaUI.Core.Definitions;
using Xunit;

namespace RbgCanInsight.GuiTests;

/// <summary>Windows-only WPF/UIA3 acceptance of the passive signal trigger
/// workbench and its source-backed evidence exports. No real OEM data.</summary>
[Trait("Category", "WindowsFlaUI")]
public sealed class GuiSignalTriggerTests
{
    [Fact]
    public void ThresholdTriggerCanSelectExactDecodedSignalAndOpenObservedTraceFrame()
    {
        using var ui = LoadedSyntheticModeTransition();
        OpenTriggers(ui);
        SelectModeSignal(ui);
        ui.Visible("statistics.triggerMode").AsComboBox().Select(0);
        ui.Fill("statistics.triggerThreshold", "0");
        ui.Click("statistics.triggerEvaluate");

        var record = ui.Until(() => ui.Visible("statistics.triggerGrid")
            .FindFirstDescendant(cf => cf.ByControlType(ControlType.DataItem)),
            "observed signal threshold crossing", 35);
        record.Press();
        Assert.True(ui.Visible("statistics.triggerOpenAfter").IsEnabled);
        ui.Click("statistics.triggerOpenAfter");
        Assert.NotNull(ui.Visible("trace.grid"));
    }

    [Fact]
    public void NamedDbcStateTriggerSupportsObservedStateChangesWithoutRewritingSource()
    {
        using var ui = LoadedSyntheticModeTransition();
        var originalDbc = File.ReadAllText(ui.DbcPath);
        var originalTrace = File.ReadAllText(ui.TracePath);
        OpenTriggers(ui);
        SelectModeSignal(ui);
        ui.Visible("statistics.triggerMode").AsComboBox().Select(3);
        ui.Fill("statistics.triggerNamedState", "On");
        ui.Click("statistics.triggerEvaluate");
        var record = ui.Until(() => ui.Visible("statistics.triggerGrid")
            .FindFirstDescendant(cf => cf.ByControlType(ControlType.DataItem)),
            "recorded named-state entry", 35);
        record.Press();
        Assert.True(ui.Visible("statistics.triggerOpenBefore").IsEnabled);
        ui.Click("statistics.triggerOpenBefore");
        Assert.NotNull(ui.Visible("trace.grid"));
        Assert.Equal(originalDbc, File.ReadAllText(ui.DbcPath));
        Assert.Equal(originalTrace, File.ReadAllText(ui.TracePath));
    }

    [Fact]
    public void EvaluatedSignalRuleIsExportedWithExplicitEventCountsAndFrameReferences()
    {
        using var ui = LoadedSyntheticModeTransition();
        OpenTriggers(ui);
        SelectModeSignal(ui);
        ui.Visible("statistics.triggerMode").AsComboBox().Select(0);
        ui.Fill("statistics.triggerThreshold", "0");
        ui.Click("statistics.triggerEvaluate");
        ui.Until(() => ui.Visible("statistics.triggerGrid")
            .FindFirstDescendant(cf => cf.ByControlType(ControlType.DataItem)),
            "trigger evaluation completed", 35);

        ui.Visible("utilities.panel").Patterns.ExpandCollapse.Pattern.Expand();
        var output = Path.Combine(ui.Root, "trigger-report.zip");
        ui.Click("utilities.exportReport");
        ui.ChooseFile(output);
        ui.AwaitStatus("Analysis report exported:");
        ui.AwaitExport(output);
        using var archive = ZipFile.OpenRead(output);
        using var coverageStream = new StreamReader(
            archive.GetEntry("signal-trigger-coverage.json")!.Open());
        using var coverage = JsonDocument.Parse(coverageStream.ReadToEnd());
        Assert.True(coverage.RootElement.GetProperty("evaluated").GetBoolean());
        Assert.True(coverage.RootElement.GetProperty("matchedEvents").GetInt64() > 0);
        Assert.Equal(0, coverage.RootElement.GetProperty("omittedEvents").GetInt64());
        using var data = new StreamReader(archive.GetEntry("signal-trigger-evidence.csv")!.Open());
        var csv = data.ReadToEnd();
        Assert.Contains("CurrentSourceFrameIndex0", csv);
        Assert.Contains("CrossesAbove", csv);
        Assert.NotNull(archive.GetEntry("frame-evidence.csv"));
    }

    [Fact]
    public void LocalSavedRuleReloadsExactSignalAndRestoresItsConfiguration()
    {
        using var ui = LoadedSyntheticModeTransition();
        OpenTriggers(ui);
        SelectModeSignal(ui);
        ui.Visible("statistics.triggerMode").AsComboBox().Select(0);
        ui.Fill("statistics.triggerThreshold", "2");
        ui.Fill("statistics.triggerPresetName", "Observed speed change");
        Assert.True(ui.Visible("statistics.triggerSaveRule").IsEnabled);
        ui.Click("statistics.triggerSaveRule");
        ui.Until(() => ui.Find("statistics.triggerLoadRule") is { IsEnabled: true }
            ? "rule saved" : null, "saved trigger rule available");

        ui.Fill("statistics.triggerThreshold", "5");
        Assert.Equal("5", ui.Text("statistics.triggerThreshold"));
        ui.Click("statistics.triggerLoadRule");
        ui.Until(() => ui.Text("statistics.triggerThreshold") == "2"
            ? "loaded" : null, "saved trigger threshold restored");
        Assert.True(ui.Visible("statistics.triggerEvaluate").IsEnabled);

        Assert.Equal(Fixtures.Dbc, File.ReadAllText(ui.DbcPath));
        Assert.Contains("0.100000", File.ReadAllText(ui.TracePath));
    }

    [Fact]
    public void EditingThresholdAfterEvaluationClearsPreviouslyObservedEvidence()
    {
        using var ui = LoadedSyntheticModeTransition();
        OpenTriggers(ui);
        SelectModeSignal(ui);
        ui.Visible("statistics.triggerMode").AsComboBox().Select(0);
        ui.Fill("statistics.triggerThreshold", "0");
        ui.Click("statistics.triggerEvaluate");
        var eventRow = ui.Until(() => ui.Visible("statistics.triggerGrid")
            .FindFirstDescendant(cf => cf.ByControlType(ControlType.DataItem)),
            "initial recorded threshold event", 35);
        eventRow.Press();
        Assert.True(ui.Visible("statistics.triggerOpenAfter").IsEnabled);
        ui.Fill("statistics.triggerThreshold", "2");

        ui.Until(() => ui.Visible("statistics.triggerGrid")
            .FindFirstDescendant(cf => cf.ByControlType(ControlType.DataItem)) is null
            ? "cleared" : null, "stale trigger evidence cleared");
        Assert.False(ui.Visible("statistics.triggerOpenAfter").IsEnabled);
        Assert.Equal(Fixtures.Dbc, File.ReadAllText(ui.DbcPath));
    }

    private static GuiSession LoadedSyntheticModeTransition()
    {
        var ui = new GuiSession();
        const string before = "0.100000 1 100 Rx d 8 37 28 00 00";
        const string after = "0.100000 1 100 Rx d 8 37 28 01 00";
        var trace = File.ReadAllText(ui.TracePath);
        Assert.Contains(before, trace);
        File.WriteAllText(ui.TracePath, trace.Replace(before, after,
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

    private static void OpenTriggers(GuiSession ui)
    {
        ui.Navigate("nav.statistics");
        ui.Visible("statistics.triggerTab").AsTabItem().Select();
        ui.Click("statistics.openTriggerEditor");
        Assert.NotNull(ui.WindowWithTitle("Signal triggers —"));
        Assert.NotNull(ui.Visible("statistics.triggerSignal"));
        Assert.NotNull(ui.Visible("statistics.triggerGrid"));
    }

    private static void SelectModeSignal(GuiSession ui)
    {
        ui.Fill("statistics.triggerSignalSearch", "EngineData.Mode");
        var choices = ui.Visible("statistics.triggerSignal").AsComboBox();
        ui.Until(() => choices.Items.Length > 0 ? "found" : null,
            "decoded EngineData.Mode signal", 35);
        choices.Select(0);
        Assert.True(ui.Visible("statistics.triggerEvaluate").IsEnabled);
    }
}
