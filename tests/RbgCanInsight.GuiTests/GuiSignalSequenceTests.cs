using System.IO.Compression;
using System.Text.Json;
using FlaUI.Core.Definitions;
using Xunit;

namespace RbgCanInsight.GuiTests;

/// <summary>
/// Live Windows UIA3 tests with only synthetic source data. Mode at 0.1s is
/// On; Mode at 0.2s is Off: independent A-rise then B-fall with known frames.
/// No CAN transmit and no private automotive input data.
/// </summary>
[Trait("Category", "WindowsFlaUI")]
public sealed class GuiSignalSequenceTests
{
    [Fact]
    public void TwoExplicitSignalEventsProduceNavigableAThenBAndBoundedContext()
    {
        using var ui = LoadedSequence();
        OpenSequence(ui);
        ConfigureTwoEvents(ui);
        ui.Click("statistics.sequenceEvaluate");

        var first = ui.Until(() => ui.Visible("statistics.sequenceGrid")
            .FindFirstDescendant(cf => cf.ByControlType(ControlType.DataItem)),
            "observed A then B pair", 35);
        first.Press();
        Assert.True(ui.Visible("statistics.sequenceOpenA").IsEnabled);
        Assert.True(ui.Visible("statistics.sequenceOpenB").IsEnabled);

        var context = ui.Until(() => ui.Visible("statistics.sequenceContextGrid")
            .FindFirstDescendant(cf => cf.ByControlType(ControlType.DataItem)),
            "bounded context frame", 35);
        context.Press();
        Assert.True(ui.Visible("statistics.sequenceOpenContext").IsEnabled);
        ui.Click("statistics.sequenceOpenContext");
        Assert.NotNull(ui.Visible("trace.grid"));
        Assert.Equal(Fixtures.Dbc, File.ReadAllText(ui.DbcPath));
    }

    [Fact]
    public void ChangingTemporalWindowInvalidatesOldPairEvidence()
    {
        using var ui = LoadedSequence();
        OpenSequence(ui);
        ConfigureTwoEvents(ui);
        ui.Click("statistics.sequenceEvaluate");
        var found = ui.Until(() => ui.Visible("statistics.sequenceGrid")
            .FindFirstDescendant(cf => cf.ByControlType(ControlType.DataItem)),
            "initial sequence pair", 35);
        found.Press();
        Assert.True(ui.Visible("statistics.sequenceOpenA").IsEnabled);

        ui.Fill("statistics.sequenceMaxMs", "5");
        ui.Until(() => ui.Visible("statistics.sequenceGrid")
            .FindFirstDescendant(cf => cf.ByControlType(ControlType.DataItem)) is null
                ? "cleared" : null, "stale paired evidence invalidated");
        Assert.False(ui.Visible("statistics.sequenceOpenA").IsEnabled);
        Assert.False(ui.Visible("statistics.sequenceOpenB").IsEnabled);
    }

    [Fact]
    public void ExportedSequenceCoverageContainsTwoStagePredicateAndFrameProof()
    {
        using var ui = LoadedSequence();
        OpenSequence(ui);
        ConfigureTwoEvents(ui);
        ui.Click("statistics.sequenceEvaluate");
        ui.Until(() => ui.Visible("statistics.sequenceGrid")
            .FindFirstDescendant(cf => cf.ByControlType(ControlType.DataItem)),
            "completed sequence evaluation", 35);

        ui.Visible("utilities.panel").Patterns.ExpandCollapse.Pattern.Expand();
        var zipPath = Path.Combine(ui.Root, "two-event-analysis.zip");
        ui.Click("utilities.exportReport");
        ui.ChooseFile(zipPath);
        ui.AwaitStatus("Analysis report exported:");
        ui.AwaitExport(zipPath);
        using var zip = ZipFile.OpenRead(zipPath);
        using (var stream = new StreamReader(zip.GetEntry("signal-sequence-evidence.csv")!.Open()))
        {
            var body = stream.ReadToEnd();
            Assert.Contains("FirstMatchFrameIndex0", body);
            Assert.Contains("SecondMatchFrameIndex0", body);
            Assert.Contains("ContextStartNs", body);
        }
        using (var stream = new StreamReader(zip.GetEntry("signal-sequence-coverage.json")!.Open()))
        using (var document = JsonDocument.Parse(stream.ReadToEnd()))
        {
            var root = document.RootElement;
            Assert.True(root.GetProperty("evaluated").GetBoolean());
            Assert.Equal("ThenWithin", root.GetProperty("sequenceMode").GetString());
            Assert.Equal(1, root.GetProperty("retainedInputPairs").GetInt64());
            Assert.True(root.GetProperty("completeEvidence").GetBoolean());
            Assert.Equal(0, root.GetProperty("detailedPairsOmitted").GetInt64());
        }
        Assert.NotNull(zip.GetEntry("frame-evidence.csv"));
        Assert.Contains("0.100000", File.ReadAllText(ui.TracePath));
    }

    [Fact]
    public void SequencePanelPreservesNonDestructiveSourceFilesAndRuleFieldsOnNavigation()
    {
        using var ui = LoadedSequence();
        OpenSequence(ui);
        ConfigureTwoEvents(ui);
        ui.Fill("statistics.sequenceMaxMs", "250");
        ui.Navigate("nav.dbc");
        ui.Navigate("nav.statistics");
        OpenSequence(ui);
        Assert.Equal("250", ui.Text("statistics.sequenceMaxMs"));
        Assert.True(ui.Visible("statistics.sequenceEvaluate").IsEnabled);
        Assert.Equal(Fixtures.Dbc, File.ReadAllText(ui.DbcPath));
    }

    private static GuiSession LoadedSequence()
    {
        var ui = new GuiSession();
        var trace = File.ReadAllText(ui.TracePath);
        const string original = "0.100000 1 100 Rx d 8 37 28 00 00";
        const string activated = "0.100000 1 100 Rx d 8 37 28 01 00";
        Assert.Contains(original, trace);
        File.WriteAllText(ui.TracePath, trace.Replace(original, activated,
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

    private static void OpenSequence(GuiSession ui)
    {
        ui.Navigate("nav.statistics");
        ui.Visible("statistics.sequenceTab").AsTabItem().Select();
        Assert.NotNull(ui.Visible("statistics.sequenceSignalA"));
        Assert.NotNull(ui.Visible("statistics.sequenceSignalB"));
        Assert.NotNull(ui.Visible("statistics.sequenceGrid"));
    }

    private static void ConfigureTwoEvents(GuiSession ui)
    {
        ui.Fill("statistics.sequenceSearchA", "EngineData.Mode");
        var first = ui.Visible("statistics.sequenceSignalA").AsComboBox();
        ui.Until(() => first.Items.Length > 0 ? "A" : null, "decoded mode A", 35);
        first.Select(0);

        ui.Fill("statistics.sequenceSearchB", "EngineData.Mode");
        var second = ui.Visible("statistics.sequenceSignalB").AsComboBox();
        ui.Until(() => second.Items.Length > 0 ? "B" : null, "decoded mode B", 35);
        second.Select(0);

        ui.Visible("statistics.sequenceModeA").AsComboBox().Select(0); // rising
        ui.Visible("statistics.sequenceModeB").AsComboBox().Select(1); // falling
        ui.Fill("statistics.sequenceThresholdA", "0.5");
        ui.Fill("statistics.sequenceThresholdB", "0.5");
        ui.Visible("statistics.sequenceRelation").AsComboBox().Select(0);
        ui.Fill("statistics.sequenceMaxMs", "500");
        Assert.True(ui.Visible("statistics.sequenceEvaluate").IsEnabled);
    }
}
