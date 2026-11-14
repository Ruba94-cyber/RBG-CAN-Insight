using System.IO.Compression;
using System.Text.Json;
using FlaUI.Core.Definitions;
using Xunit;

namespace RbgCanInsight.GuiTests;

/// <summary>Real Windows UIA3 checks of source-backed exact DBC raw-code
/// exploration. Uses synthetic CAN data; never rewrites loaded DBC/ASC.</summary>
[Trait("Category", "WindowsFlaUI")]
public sealed class GuiDbcValueExplorerTests
{
    [Fact]
    public void LoadedTraceShowsObservedRawCodesWithOriginalFrameNavigation()
    {
        using var ui = LoadedExplorer();
        ui.Click("dbc.valueExplorerAnalyze");
        var grid = ui.Visible("dbc.valueExplorerGrid");
        var item = ui.Until(() => grid.FindFirstDescendant(
            cf => cf.ByControlType(ControlType.DataItem)), "observed raw value row", 35);
        item.Press();
        Assert.True(ui.Visible("dbc.valueExplorerFirst").IsEnabled);
        Assert.True(ui.Visible("dbc.valueExplorerLast").IsEnabled);
        ui.Click("dbc.valueExplorerLast");
        Assert.NotNull(ui.Visible("trace.grid"));
        Assert.Equal(Fixtures.Dbc, File.ReadAllText(ui.DbcPath));
        Assert.Equal(Fixtures.Trace, File.ReadAllText(ui.TracePath));
    }

    [Fact]
    public void SwitchingSignalInvalidatesPreviouslyRecordedCodeEvidence()
    {
        using var ui = LoadedExplorer();
        ui.Click("dbc.valueExplorerAnalyze");
        ui.Until(() => ui.Visible("dbc.valueExplorerGrid")
            .FindFirstDescendant(cf => cf.ByControlType(ControlType.DataItem)),
            "initial raw histogram", 35);

        // Selecting a new signal must invalidate the old signal's original
        // frame links. The new histogram is opt-in, never synthesized.
        ui.Visible("dbc.signalsGrid")
            .FindAllDescendants(cf => cf.ByControlType(ControlType.DataItem))[1].Press();
        ui.Visible("dbc.valueExplorerTab").AsTabItem().Select();
        ui.Until(() => ui.Visible("dbc.valueExplorerGrid")
            .FindFirstDescendant(cf => cf.ByControlType(ControlType.DataItem)) is null
                ? "reset" : null, "old histogram invalidated");
        Assert.False(ui.Visible("dbc.valueExplorerFirst").IsEnabled);
        Assert.False(ui.Visible("dbc.valueExplorerLast").IsEnabled);
        Assert.Equal(Fixtures.Dbc, File.ReadAllText(ui.DbcPath));
    }

    [Fact]
    public void WithoutLoadedTraceValueExplorerDoesNotInventRawCodes()
    {
        using var ui = new GuiSession();
        ui.Navigate("nav.dbc");
        ui.Click("dbc.load");
        ui.ChooseFile(ui.DbcPath);
        ui.AwaitStatus("1 DBC");
        SelectFirstSignal(ui);
        ui.Visible("dbc.valueExplorerTab").AsTabItem().Select();
        Assert.False(ui.Visible("dbc.valueExplorerAnalyze").IsEnabled);
        Assert.Null(ui.Visible("dbc.valueExplorerGrid")
            .FindFirstDescendant(cf => cf.ByControlType(ControlType.DataItem)));
        Assert.Equal(Fixtures.Dbc, File.ReadAllText(ui.DbcPath));
    }

    [Fact]
    public void ExportedRawCodeHistogramContainsExactSourceFrameIndicesAndCoverage()
    {
        using var ui = LoadedExplorer();
        ui.Click("dbc.valueExplorerAnalyze");
        ui.Until(() => ui.Visible("dbc.valueExplorerGrid")
            .FindFirstDescendant(cf => cf.ByControlType(ControlType.DataItem)),
            "DBC raw histogram complete", 35);

        ui.Visible("utilities.panel").Patterns.ExpandCollapse.Pattern.Expand();
        var output = Path.Combine(ui.Root, "observed-raw-codes.zip");
        ui.Click("utilities.exportReport");
        ui.ChooseFile(output);
        ui.AwaitStatus("Analysis report exported:");
        ui.AwaitExport(output);
        using var archive = ZipFile.OpenRead(output);
        using (var stream = new StreamReader(archive.GetEntry(
                   "dbc-observed-raw-codes.csv")!.Open()))
        {
            var csv = stream.ReadToEnd();
            Assert.Contains("ExactRaw", csv);
            Assert.Contains("FirstSourceFrameIndex0", csv);
            Assert.Contains("ObservedCount", csv);
        }
        using (var stream = new StreamReader(archive.GetEntry(
                   "dbc-observed-raw-coverage.json")!.Open()))
        using (var json = JsonDocument.Parse(stream.ReadToEnd()))
        {
            var root = json.RootElement;
            Assert.True(root.GetProperty("evaluated").GetBoolean());
            Assert.Equal(3, root.GetProperty("observedSamples").GetInt64());
            Assert.Equal(0, root.GetProperty(
                "observationsWithUntrackedDistinctRawCode").GetInt64());
        }
        Assert.NotNull(archive.GetEntry("frame-evidence.csv"));
        Assert.Equal(Fixtures.Dbc, File.ReadAllText(ui.DbcPath));
        Assert.Equal(Fixtures.Trace, File.ReadAllText(ui.TracePath));
    }

    private static GuiSession LoadedExplorer()
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
        ui.Navigate("nav.dbc");
        SelectFirstSignal(ui);
        ui.Visible("dbc.valueExplorerTab").AsTabItem().Select();
        Assert.True(ui.Visible("dbc.valueExplorerAnalyze").IsEnabled);
        return ui;
    }

    private static void SelectFirstSignal(GuiSession ui)
    {
        var message = ui.Until(() => ui.Visible("dbc.messagesGrid")
            .FindFirstDescendant(cf => cf.ByControlType(ControlType.DataItem)),
            "first declared DBC message");
        message.Press();
        var signal = ui.Until(() => ui.Visible("dbc.signalsGrid")
            .FindFirstDescendant(cf => cf.ByControlType(ControlType.DataItem)),
            "first DBC signal definition");
        signal.Press();
    }
}
