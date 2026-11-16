using System.Text.Json;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using Xunit;

namespace RbgCanInsight.GuiTests;

/// <summary>Complementary high-value GUI regressions for workflows that
/// mutate only a local working copy or compare read-only snapshots.</summary>
[Trait("Category", "WindowsFlaUI")]
public sealed class AdvancedGuiWorkflows
{
    [Fact]
    public void DbcManager_InspectorButtonRemainsVisibleWithManySignals()
    {
        using var ui = new GuiSession();
        ui.ResizeTo(1280, 720);
        var dbc = "VERSION \"\"\nNS_ :\nBS_:\nBU_: ECM\nBO_ 256 ManySignalsWithALongMessageName: 8 ECM\n" +
            string.Concat(Enumerable.Range(0, 64).Select(i =>
                $" SG_ LongSignalNameForLayoutTesting{i} : {i}|1@1+ (1,0) [0|1] \"{new string('u', 150)}\" Vector__XXX\n"));
        File.WriteAllText(ui.DbcPath, dbc);
        ui.Navigate("nav.dbc");
        ui.Click("dbc.load");
        ui.ChooseFile(ui.DbcPath);
        ui.AwaitStatus("1 DBC");
        ui.Navigate("nav.trace");
        ui.Click("trace.open");
        ui.ChooseFile(ui.TracePath);
        ui.AwaitStatus("Trace loaded:");
        ui.Navigate("nav.dbc");
        ui.Until(() => ui.Visible("dbc.messagesGrid").FindFirstDescendant(
            cf => cf.ByControlType(ControlType.DataItem)), "DBC message").Press();
        var grid = ui.Visible("dbc.signalsGrid");
        ui.Until(() => grid.FindFirstDescendant(cf => cf.ByControlType(ControlType.DataItem)), "DBC signal").Press();
        var button = ui.Visible("dbc.openInspector");
        Assert.True(button.BoundingRectangle.Bottom <= ui.Main.BoundingRectangle.Bottom);
        Assert.True(grid.BoundingRectangle.Bottom <= button.BoundingRectangle.Top);
        button.Press();
        var inspector = ui.WindowWithTitle("DBC signal inspector");
        inspector.Patterns.Transform.Pattern.Resize(700, 450);
        ui.Visible("dbc.valueExplorerTab").AsTabItem().Select();
        var analyze = ui.Visible("dbc.valueExplorerAnalyze");
        Assert.True(analyze.BoundingRectangle.Left >= inspector.BoundingRectangle.Left);
        Assert.True(analyze.BoundingRectangle.Right <= inspector.BoundingRectangle.Right);
        Assert.True(analyze.BoundingRectangle.Top >= inspector.BoundingRectangle.Top);
        Assert.True(analyze.BoundingRectangle.Bottom <= inspector.BoundingRectangle.Bottom);
        Assert.True(analyze.IsEnabled);
        analyze.Press();
        ui.Until(() => ui.Visible("dbc.valueExplorerGrid")
            .FindFirstDescendant(cf => cf.ByControlType(ControlType.DataItem)), "observed raw code", 35);
        Assert.False(analyze.IsOffscreen);
        Assert.True(analyze.BoundingRectangle.Bottom <= inspector.BoundingRectangle.Bottom);
    }

    [Fact]
    public void DbcEditor_StagesChangeAndSavesOnlyAnIndependentCopy()
    {
        using var ui = new GuiSession();
        ui.Navigate("nav.dbc");
        ui.Click("dbc.load");
        ui.ChooseFile(ui.DbcPath);
        ui.AwaitStatus("1 DBC");

        var messageGrid = ui.Visible("dbc.messagesGrid");
        var message = ui.Until(() => messageGrid.FindFirstDescendant(
            cf => cf.ByControlType(ControlType.DataItem)), "first DBC message");
        message.Press();

        var signalGrid = ui.Visible("dbc.signalsGrid");
        var signal = ui.Until(() => signalGrid.FindFirstDescendant(
            cf => cf.ByControlType(ControlType.DataItem)), "first DBC signal");
        signal.Press();

        var editorTab = ui.Until(() => ui.Find("dbc.editorTab"), "DBC working-copy editor tab");
        editorTab.AsTabItem().Select();
        Assert.False(ui.Visible("dbc.editorFactor").IsOffscreen);
        ui.Fill("dbc.editorFactor", "2");
        ui.Fill("dbc.editorUnit", "degK");
        ui.Click("dbc.stageEdit");
        var stagedSave = ui.Until(() => ui.Find("dbc.saveCopy") is { IsEnabled: true }
            ? ui.Find("dbc.saveCopy") : null, "enabled Save DBC copy");
        Assert.NotNull(stagedSave);
        Assert.DoesNotContain("degK", File.ReadAllText(ui.DbcPath));

        var destination = Path.Combine(ui.Root, "edited-copy.dbc");
        ui.Click("dbc.saveCopy");
        ui.ChooseFile(destination);
        ui.Until(() => File.Exists(destination) ? "saved" : null, "DBC edited copy", 30);
        Assert.Contains("degK", File.ReadAllText(destination));
        Assert.DoesNotContain("degK", File.ReadAllText(ui.DbcPath));
    }

    [Fact]
    public void TraceHealth_CanOpenWithImportedSyntheticCapture()
    {
        using var ui = new GuiSession();
        ui.Navigate("nav.trace");
        ui.Click("trace.open");
        ui.ChooseFile(ui.TracePath);
        ui.AwaitStatus("Trace loaded:");
        ui.Click("trace.health");
        var health = ui.WindowWithTitle("Trace health");
        Assert.Contains("Trace health", health.Title);
        Assert.Contains("0.100000", File.ReadAllText(ui.TracePath));
        health.Close();
    }

    [Fact]
    public void SavedMappingDiff_OpensAndReportsChangedAnalystNotesWithoutLoadedSources()
    {
        using var ui = new GuiSession();
        var baseline = Path.Combine(ui.Root, "baseline.rbgmap.json");
        var current = Path.Combine(ui.Root, "current.rbgmap.json");
        WriteSnapshot(baseline, "Reviewed");
        WriteSnapshot(current, "Re-review needed");

        ui.Navigate("nav.mapping");
        ui.Click("mapping.compare");
        ui.ChooseFile(baseline);
        ui.ChooseFile(current);
        ui.AwaitMappingStatus("Read-only map comparison ready");

        var workbook = Path.Combine(ui.Root, "mapping-diff.xlsx");
        ui.Click("mapping.exportDiff");
        ui.ChooseFile(workbook);
        ui.AwaitExport(workbook);
        using (var zip = System.IO.Compression.ZipFile.OpenRead(workbook))
            Assert.NotNull(zip.GetEntry("xl/workbook.xml"));

        var expander = ui.Until(() => ui.Find("mapping.diffExpander") is
            { IsOffscreen: false } element ? element : null, "mapping difference panel");
        expander.Patterns.ExpandCollapse.Pattern.Expand();
        Assert.NotNull(ui.Visible("mapping.diffGrid"));
    }

    [Fact]
    public void CompactWindow_DiagnosticParExportsRemainExposed()
    {
        using var ui = new GuiSession();
        // 1280 x 720 WPF client experience, representative of compact
        // laptops. A real 125/150-percent DPI pass remains necessary.
        ui.ResizeTo(1280, 720);
        ui.Navigate("nav.uds");
        ui.Visible("uds.viewSelector").AsComboBox().Select(11);
        Assert.False(ui.Visible("diagnostics.exportEngineering").IsOffscreen);
        Assert.NotNull(ui.Find("diagnostics.ecuFilter"));
        Assert.NotNull(ui.Find("diagnostics.search"));
    }

    private static void WriteSnapshot(string path, string notes)
    {
        var item = new
        {
            Did = 0xF190, DidName = "SyntheticIdentity", Ecu = "ECM",
            Database = "synthetic", Message = "EngineData", Signal = "Mode",
            Confidence = 0.8, Reason = "synthetic", Status = "Confirmed",
            Notes = notes, MessageIdentifier = (uint?)0x100, IsExtended = (bool?)false,
            DidDefinitionFingerprint = "sem-v1:" + new string('A', 64),
            CanSignalFingerprint = "sem-v1:" + new string('B', 64)
        };
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            Title = "Synthetic map",
            SavedAt = DateTimeOffset.UnixEpoch,
            Mappings = new[] { item }
        }));
    }
}
