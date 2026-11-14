using System.IO.Compression;
using System.Text.Json;
using System.Xml.Linq;
using FlaUI.Core.Definitions;
using Xunit;

namespace RbgCanInsight.GuiTests;

/// <summary>
/// Stateful end-to-end interaction checks with synthetic offline inputs.
/// Source files are never modified and every test runs in an isolated profile.
/// No ECU or customer data is required.
/// </summary>
[Trait("Category", "WindowsFlaUI")]
public sealed class GuiWorkflowRegressionTests
{
    [Fact]
    public void TraceTransport_HeadTailStepAndJumpKeepLoadedCaptureAccessible()
    {
        using var ui = new GuiSession();
        LoadTrace(ui);
        var first = ui.Until(() => ui.Visible("trace.grid").FindFirstDescendant(
            cf => cf.ByControlType(ControlType.DataItem)), "first recorded CAN frame");
        first.Press();
        ui.ShowTraceReplayControls();
        foreach (var button in new[] { "trace.tail", "trace.previous", "trace.head", "trace.next" })
        {
            ui.Click(button);
            Assert.False(ui.Visible("trace.grid").IsOffscreen);
        }
        ui.ShowTraceSecondaryControls();
        ui.Fill("trace.goTime", "0.2");
        ui.Click("trace.jumpTime");
        Assert.Equal("0.2", ui.Text("trace.goTime"));
        Assert.Equal(Fixtures.Trace, File.ReadAllText(ui.TracePath));
    }

    [Fact]
    public void TraceMarkers_MarkAndNavigateWithoutEditingSourceCapture()
    {
        using var ui = new GuiSession();
        LoadTrace(ui);
        ui.Until(() => ui.Visible("trace.grid").FindFirstDescendant(
            cf => cf.ByControlType(ControlType.DataItem)), "trace row for bookmark").Press();
        ui.ShowTraceReplayControls();
        ui.Click("trace.markerToggle");
        Assert.NotNull(ui.Visible("trace.grid"));
        ui.Click("trace.previousMarker");
        ui.Click("trace.nextMarker");
        Assert.Equal(Fixtures.Trace, File.ReadAllText(ui.TracePath));
    }

    [Theory]
    [InlineData("100")]
    [InlineData("7E8")]
    [InlineData("NoSuchIdForUiRegression")]
    public void TraceSearch_QueryAndResetLeaveReplayOperational(string query)
    {
        using var ui = new GuiSession();
        LoadTrace(ui);
        ui.Fill("trace.search", query);
        Assert.Equal(query, ui.Text("trace.search"));
        ui.Click("trace.clearFilter");
        ui.Until(() => ui.Text("trace.search") == "" ? "clear" : null,
            "trace search clear");
        Assert.NotNull(ui.Until(() => ui.Visible("trace.grid").FindFirstDescendant(
            cf => cf.ByControlType(ControlType.DataItem)), "trace after filter clear"));
    }

    [Fact]
    public void Dbceditor_DiscardStagedEditsRestoresPristineSourceAndDisablesSave()
    {
        using var ui = new GuiSession();
        LoadDbc(ui);
        SelectFirstDbcSignal(ui);
        ui.Visible("dbc.editorTab").AsTabItem().Select();
        ui.Fill("dbc.editorFactor", "2.5");
        ui.Fill("dbc.editorUnit", "new-unit");
        ui.Click("dbc.stageEdit");
        ui.Until(() => ui.Find("dbc.saveCopy") is { IsEnabled: true }
            ? "staged" : null, "staged edit");
        Assert.Equal(Fixtures.Dbc, File.ReadAllText(ui.DbcPath));
        ui.Click("dbc.discard");
        ui.AnswerMessageBox("Discard DBC working copy");
        ui.Until(() => ui.Find("dbc.saveCopy") is { IsEnabled: false }
            ? "discarded" : null, "discarded changes");
        Assert.Equal(Fixtures.Dbc, File.ReadAllText(ui.DbcPath));
    }

    [Fact]
    public void DbcSignalInspector_TabAndNumericEditorAreAccessibleAfterSelection()
    {
        using var ui = new GuiSession();
        LoadDbc(ui);
        SelectFirstDbcSignal(ui);
        ui.Visible("dbc.bitLayoutTab").AsTabItem().Select();
        Assert.NotNull(ui.Visible("dbc.signalsGrid"));
        ui.Visible("dbc.editorTab").AsTabItem().Select();
        Assert.NotNull(ui.Visible("dbc.editorFactor"));
        Assert.NotNull(ui.Visible("dbc.editorUnit"));
        Assert.NotNull(ui.Visible("dbc.valueEditorGrid"));
        Assert.Equal(Fixtures.Dbc, File.ReadAllText(ui.DbcPath));
    }

    [Theory]
    [InlineData("EngineData")]
    [InlineData("VehicleSpeed")]
    [InlineData("Mode")]
    public void DbcCatalog_SearchDoesNotAlterImportedDefinitions(string search)
    {
        using var ui = new GuiSession();
        LoadDbc(ui);
        ui.Fill("dbc.search", search);
        Assert.Equal(search, ui.Text("dbc.search"));
        ui.Fill("dbc.search", "");
        Assert.Equal("", ui.Text("dbc.search"));
        Assert.NotNull(ui.Until(() => ui.Visible("dbc.messagesGrid").FindFirstDescendant(
            cf => cf.ByControlType(ControlType.DataItem)), "DBC catalog recovered"));
        Assert.Equal(Fixtures.Dbc, File.ReadAllText(ui.DbcPath));
    }

    [Fact]
    public void DbcCompare_ReadOnlyPreviewCanBeExpandedAndSourceFilesStayUntouched()
    {
        using var ui = new GuiSession();
        var changed = Path.Combine(ui.Root, "current.dbc");
        File.WriteAllText(changed, Fixtures.Dbc.Replace("EngineLoad", "EngineLoadV2",
            StringComparison.Ordinal));
        ui.Navigate("nav.dbc");
        ui.Click("dbc.compare");
        ui.ChooseFile(ui.DbcPath);
        ui.ChooseFile(changed);
        ui.AwaitStatus("Read-only DBC comparison ready");
        ui.Visible("dbc.diffExpander").Patterns.ExpandCollapse.Pattern.Expand();
        Assert.NotNull(ui.Visible("dbc.diffGrid"));
        Assert.Equal(Fixtures.Dbc, File.ReadAllText(ui.DbcPath));
        Assert.Contains("EngineLoadV2", File.ReadAllText(changed));
    }

    [Fact]
    public void CddCatalog_NestedParameterSearchAndInspectorAreAccessible()
    {
        using var ui = new GuiSession();
        LoadCatalog(ui, ui.CddPath);
        ui.Navigate("nav.uds");
        ui.Click("uds.openCatalog");
        Assert.NotNull(ui.Visible("diagnostics.catalogGrid"));
        ui.Fill("diagnostics.search", "ModeFlag");
        Assert.Equal("ModeFlag", ui.Text("diagnostics.search"));
        ui.Fill("diagnostics.search", "");
        ui.Until(() => ui.Visible("diagnostics.catalogGrid")
            .FindFirstDescendant(cf => cf.ByControlType(ControlType.DataItem)),
            "diagnostic DID entry").Press();
        Assert.NotNull(ui.Visible("diagnostics.fieldsGrid"));
        ui.Until(() => ui.Main.FindFirstDescendant(cf => cf.ByName("Engineering PAR meanings")),
            "meanings tab").Press();
        Assert.NotNull(ui.Until(() => ui.Find("diagnostics.meaningsGrid"), "meanings grid"));
        Assert.Equal(Fixtures.Cdd, File.ReadAllText(ui.CddPath));
    }

    [Fact]
    public void OdxCatalog_DefinitionWorkbookCanBeExportedFromActualInspector()
    {
        using var ui = new GuiSession();
        LoadCatalog(ui, ui.OdxPath);
        ui.Navigate("nav.uds");
        ui.Click("uds.openCatalog");
        var target = Path.Combine(ui.Root, "all-diagnostic-definitions.xlsx");
        ui.Click("diagnostics.exportDefinitions");
        ui.ChooseFile(target);
        ui.AwaitExport(target);
        using var zip = ZipFile.OpenRead(target);
        using var stream = zip.GetEntry("xl/workbook.xml")!.Open();
        var sheets = XDocument.Load(stream).Descendants()
            .Where(node => node.Name.LocalName == "sheet")
            .Select(node => node.Attribute("name")?.Value).ToArray();
        Assert.NotEmpty(sheets);
        Assert.Equal(Fixtures.Odx, File.ReadAllText(ui.OdxPath));
    }

    [Fact]
    public void Mapping_LoadPreviouslySavedSnapshotAndKeepOriginalCatalogs()
    {
        using var ui = new GuiSession();
        LoadDbc(ui);
        LoadCatalog(ui, ui.OdxPath);
        ui.Click("mapping.correlate");
        ui.AwaitMappingStatus("Auto-correlation done");
        var map = Path.Combine(ui.Root, "roundtrip.rbgmap.json");
        ui.Click("mapping.save");
        ui.ChooseFile(map);
        ui.Until(() => File.Exists(map) ? "saved" : null, "mapping document");
        using (var json = JsonDocument.Parse(File.ReadAllText(map)))
            Assert.True(json.RootElement.GetProperty("Mappings").GetArrayLength() > 0);
        ui.Click("mapping.load");
        ui.ChooseFile(map);
        Assert.NotNull(ui.Visible("mapping.grid"));
        Assert.Equal(Fixtures.Dbc, File.ReadAllText(ui.DbcPath));
        Assert.Equal(Fixtures.Odx, File.ReadAllText(ui.OdxPath));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Investigation_EveryArrangementCanBeSelected(int arrangement)
    {
        using var ui = new GuiSession();
        ui.ShowInvestigationAdvanced();
        ui.Visible("investigation.arrangement").AsComboBox().Select(arrangement);
        Assert.NotNull(ui.Visible("investigation.layout"));
    }

    [Fact]
    public void Investigation_CustomLayoutCanBeSavedRevisitedAndDeleted()
    {
        using var ui = new GuiSession();
        ui.ShowInvestigationAdvanced();
        const string name = "FlaUI offline layout";
        ui.Fill("investigation.layoutName", name);
        ui.Click("investigation.saveLayout");
        ui.Until(() => ui.Visible("investigation.layout").AsComboBox()
            .SelectedItem?.Text.Contains($"Name = {name},", StringComparison.Ordinal) == true ? "saved" : null,
            "custom investigation layout selected");
        ui.Navigate("nav.dashboard");
        ui.ShowInvestigationAdvanced();
        Assert.Contains($"Name = {name},", ui.Visible("investigation.layout").AsComboBox().SelectedItem?.Text);
        ui.Click("investigation.deleteLayout");
        ui.Until(() => ui.Visible("investigation.layout").AsComboBox()
            .SelectedItem?.Text.Contains($"Name = {name},", StringComparison.Ordinal) != true ? "deleted" : null,
            "custom investigation layout deleted");
    }

    [Fact]
    public void Investigation_BuiltInLayoutCannotBeDeleted()
    {
        using var ui = new GuiSession();
        ui.ShowInvestigationAdvanced();
        var selected = ui.Visible("investigation.layout").AsComboBox().SelectedItem?.Text;
        Assert.False(string.IsNullOrWhiteSpace(selected));
        ui.Click("investigation.deleteLayout");
        Assert.Equal(selected, ui.Visible("investigation.layout").AsComboBox().SelectedItem?.Text);
    }

    [Fact]
    public void LoadedTrace_UDSViewsStayInteractiveWhenNavigatingAwayAndBack()
    {
        using var ui = new GuiSession();
        LoadTrace(ui);
        ui.Navigate("nav.uds");
        ui.Visible("uds.viewSelector").AsComboBox().Select(1);
        Assert.NotNull(ui.Until(() => ui.Visible("uds.transactionsGrid").FindFirstDescendant(
            cf => cf.ByControlType(ControlType.DataItem)), "UDS transaction"));
        ui.Visible("uds.viewSelector").AsComboBox().Select(7);
        Assert.NotNull(ui.Visible("uds.findingsGrid"));
        ui.Navigate("nav.trace");
        Assert.NotNull(ui.Visible("trace.grid"));
        ui.Navigate("nav.uds");
        ui.Visible("uds.viewSelector").AsComboBox().Select(1);
        Assert.NotNull(ui.Visible("uds.transactionsGrid"));
    }

    private static void LoadTrace(GuiSession ui)
    {
        ui.Navigate("nav.trace");
        ui.Click("trace.open");
        ui.ChooseFile(ui.TracePath);
        ui.AwaitStatus("Trace loaded:");
    }

    private static void LoadDbc(GuiSession ui)
    {
        ui.Navigate("nav.dbc");
        ui.Click("dbc.load");
        ui.ChooseFile(ui.DbcPath);
        ui.AwaitStatus("1 DBC");
    }

    private static void LoadCatalog(GuiSession ui, string path)
    {
        ui.Navigate("nav.mapping");
        ui.Click("mapping.loadCatalog");
        ui.ChooseFile(path);
        ui.AwaitMappingStatus("from 1 file");
    }

    private static void SelectFirstDbcSignal(GuiSession ui)
    {
        ui.Until(() => ui.Visible("dbc.messagesGrid").FindFirstDescendant(
            cf => cf.ByControlType(ControlType.DataItem)), "first DBC message").Press();
        ui.Until(() => ui.Visible("dbc.signalsGrid").FindFirstDescendant(
            cf => cf.ByControlType(ControlType.DataItem)), "first DBC signal").Press();
    }
}
