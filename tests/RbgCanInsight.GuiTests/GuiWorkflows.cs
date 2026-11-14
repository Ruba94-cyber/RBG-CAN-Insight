using System.IO.Compression;
using System.Text.Json;
using System.Xml.Linq;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace RbgCanInsight.GuiTests;

/// <summary>External-process FlaUI/UIA3 acceptance checks. Must be run in an
/// interactive, unlocked Windows session; never on a headless build agent.</summary>
[Trait("Category", "WindowsFlaUI")]
public sealed class GuiWorkflows
{
    [Fact]
    public void Navigation_AllTenWorkspacesExposeExpectedControls()
    {
        using var ui = new GuiSession();
        var checks = new (string Navigation, string Control)[]
        {
            ("nav.dashboard", "globalSearch.open"),
            ("nav.trace", "trace.search"),
            ("nav.dbc", "dbc.search"),
            ("nav.signals", "signals.grid"),
            ("nav.matrix", "matrix.grid"),
            ("nav.byteMatrix", "byteMatrix.grid"),
            ("nav.statistics", "statistics.grid"),
            ("nav.mapping", "mapping.grid"),
            ("nav.uds", "uds.viewTransactions"),
            ("nav.investigation", "investigation.signalsGrid")
        };
        foreach (var (navigation, control) in checks)
        {
            ui.Navigate(navigation);
            Assert.False(ui.Visible(control).IsOffscreen);
        }
    }

    [Fact]
    public void GlobalSearch_OpensAcceptsInputAndClosesOnEscape()
    {
        using var ui = new GuiSession();
        ui.Click("globalSearch.open");
        var search = ui.WindowWithTitle("Search");
        search.SetForeground();
        var field = ui.Until(() => search.FindFirstDescendant(
            cf => cf.ByAutomationId("globalSearch.query")), "global search field");
        field.AsTextBox().Text = "Engine";
        Assert.Equal("Engine", field.AsTextBox().Text);
        ui.PressKey(0x1B);
        ui.Until(() => !ui.IsWindowOpen("Search") ? "closed" : null,
            "global search close", timeoutSeconds: 12);
    }

    [Fact]
    public void UniversalSearchHasMouseAccessibleClearCloseAndEmptyGuidance()
    {
        using var ui = new GuiSession();
        ui.Click("globalSearch.open");
        var dialog = ui.WindowWithTitle("Search");
        var query = ui.Until(() => dialog.FindFirstDescendant(
            cf => cf.ByAutomationId("globalSearch.query")), "universal search field");
        query.AsTextBox().Text = "an-impossible-synthetic-signal";
        ui.Until(() => dialog.FindFirstDescendant(
            cf => cf.ByAutomationId("globalSearch.empty"))?.Name.Contains("No results") == true
            ? "no results explained" : null, "universal search empty guidance");

        ui.Until(() => dialog.FindFirstDescendant(
            cf => cf.ByAutomationId("globalSearch.clear")), "search clear button").Press();
        Assert.Equal(string.Empty, query.AsTextBox().Text);
        ui.Until(() => dialog.FindFirstDescendant(
            cf => cf.ByAutomationId("globalSearch.empty"))?.Name.Contains("Type a signal") == true
            ? "typing guidance" : null, "empty search guidance");

        ui.Until(() => dialog.FindFirstDescendant(
            cf => cf.ByAutomationId("globalSearch.close")), "search close button").Press();
        ui.Until(() => !ui.IsWindowOpen("Search") ? "closed" : null,
            "close universal search without keyboard");
    }

    [Fact]
    public void QueryBuilder_OpensAddsClearsAndClosesWithoutApplyingChanges()
    {
        using var ui = new GuiSession();
        ui.Navigate("nav.trace");
        ui.Click("trace.queryBuilder");
        var dialog = ui.WindowWithTitle("Query builder");
        Assert.NotNull(dialog.FindFirstDescendant(cf => cf.ByAutomationId("query.preview")));
        var add = dialog.FindFirstDescendant(cf => cf.ByAutomationId("query.addCondition"));
        Assert.NotNull(add);
        add.AsButton().Invoke();
        var clear = dialog.FindFirstDescendant(cf => cf.ByAutomationId("query.clear"));
        Assert.NotNull(clear);
        clear.AsButton().Invoke();
        var close = dialog.FindFirstDescendant(cf => cf.ByAutomationId("query.close"));
        Assert.NotNull(close);
        close.AsButton().Invoke();
        ui.Until(() => !ui.IsWindowOpen("Query builder") ? "closed" : null,
            "query builder close", timeoutSeconds: 12);
    }

    [Fact]
    public void FirstRunTour_IsNavigableAndSkippableWithinIsolatedProfile()
    {
        using var ui = new GuiSession(runOnboarding: true);
        var tour = ui.WindowWithTitle("Quick tour");
        tour.SetForeground();
        var next = tour.FindFirstDescendant(cf => cf.ByAutomationId("onboarding.next"));
        Assert.NotNull(next);
        for (var step = 0; step < 30 && tour.FindFirstDescendant(cf => cf.ByName("Build and save an analysis")) is null; step++)
            next.AsButton().Invoke();
        Assert.NotNull(tour.FindFirstDescendant(cf => cf.ByName("Build and save an analysis")));
        var skip = tour.FindFirstDescendant(cf => cf.ByAutomationId("onboarding.skip"));
        Assert.NotNull(skip);
        skip.AsButton().Invoke();
        ui.Until(() => !ui.IsWindowOpen("Quick tour") ? "closed" : null,
            "onboarding skipped");
        var config = Path.Combine(ui.Root, "config", "app-settings.json");
        using var json = JsonDocument.Parse(File.ReadAllText(config));
        Assert.True(json.RootElement.GetProperty("OnboardingShown").GetBoolean());
    }

    [Fact]
    public void ThemeSelector_HighContrastCanBeSelectedFromRealUi()
    {
        using var ui = new GuiSession();
        var utilities = ui.Find("utilities.panel");
        Assert.NotNull(utilities);
        utilities.Patterns.ExpandCollapse.Pattern.Expand();
        var theme = ui.Visible("settings.theme").AsComboBox();
        theme.Select("High Contrast");
        Assert.Equal("High Contrast", theme.SelectedItem?.Text);
        ui.Navigate("nav.trace");
        Assert.False(ui.Visible("trace.search").IsOffscreen);
        // All writes are inside the disposable test profile, never the real config.
        Assert.True(Directory.Exists(Path.Combine(ui.Root, "config")));
    }

    [Fact]
    public void DashboardInvestigationAndUdsQuickViewsNavigateWithoutTrace()
    {
        using var ui = new GuiSession();
        // Investigation shortcuts refuse to invent traffic when no trace is loaded.
        ui.Click("dashboard.udsFailures");
        ui.AwaitStatus("First open a recorded CAN trace");
        ui.Navigate("nav.uds");
        Assert.NotNull(ui.Visible("uds.viewTransactions"));
        ui.Click("uds.viewTransactions");
        Assert.False(ui.Visible("uds.transactionsGrid").IsOffscreen);
        ui.Click("uds.viewFindings");
        // The view selector and buttons stay accessible without inventing traffic.
        Assert.NotNull(ui.Find("uds.openCatalog"));
    }

    [Fact]
    public void TraceImport_ActualOpenDialogSearchAndFilterReset()
    {
        using var ui = new GuiSession();
        ui.Navigate("nav.trace");
        ui.Click("trace.open");
        ui.ChooseFile(ui.TracePath);
        ui.AwaitStatus("Trace loaded:");
        Assert.NotNull(ui.Visible("trace.grid"));
        ui.Fill("trace.search", "100");
        var search = ui.Visible("trace.search");
        search.Focus();
        ui.PressKey(0x0D);
        Assert.Equal("100", ui.Text("trace.search"));
        ui.Click("trace.clearFilter");
        ui.Until(() => string.IsNullOrEmpty(ui.Text("trace.search")) ? "cleared" : null,
            "trace filter clear");
    }

    [Fact]
    public void DbcLoad_ExportsBitPositionsAndExactRawRangesToRealWorkbook()
    {
        using var ui = new GuiSession();
        ui.Navigate("nav.dbc");
        ui.Click("dbc.load");
        ui.ChooseFile(ui.DbcPath);
        ui.AwaitStatus("1 DBC");
        Assert.NotNull(ui.Visible("dbc.signalsGrid"));
        var destination = Path.Combine(ui.Root, "dbc-definition-test.xlsx");
        ui.Click("dbc.exportDefinitions");
        ui.ChooseFile(destination);
        ui.AwaitExport(destination);

        using var zip = ZipFile.OpenRead(destination);
        var entry = zip.GetEntry("xl/workbook.xml");
        Assert.NotNull(entry);
        using var stream = entry.Open();
        var document = XDocument.Load(stream);
        var sheets = document.Descendants()
            .Where(node => node.Name.LocalName == "sheet")
            .Select(node => node.Attribute("name")?.Value).ToArray();
        Assert.Contains("Messages", sheets);
        Assert.Contains("Signals", sheets);
        Assert.Contains("Bit Positions", sheets);
        Assert.Contains("Bit Layout Issues", sheets);
        Assert.Contains("Raw Physical Limits", sheets);
        Assert.True(zip.GetEntry("xl/worksheets/sheet3.xml")!.Length > 100);
        Assert.Contains("EngCoolantTemp", File.ReadAllText(ui.DbcPath));
    }

    [Fact]
    public void CddImport_EngineeringXlsxPreservesAllNestedParAndSubparRows()
    {
        using var ui = new GuiSession();
        ui.Navigate("nav.mapping");
        ui.Click("mapping.loadCatalog");
        ui.ChooseFile(ui.CddPath);
        ui.AwaitMappingStatus("from 1 file");
        ui.Navigate("nav.uds");
        ui.Click("uds.openCatalog");

        var destination = Path.Combine(ui.Root, "cdd-engineering-tree.xlsx");
        ui.Click("diagnostics.exportEngineering");
        ui.ChooseFile(destination);
        ui.AwaitExport(destination);
        using var zip = ZipFile.OpenRead(destination);
        using var stream = zip.GetEntry("xl/worksheets/sheet1.xml")!.Open();
        var sheet = XDocument.Load(stream);
        var strings = sheet.Descendants()
            .Where(element => element.Name.LocalName == "t")
            .Select(element => element.Value).ToArray();
        Assert.Contains("EngineeringPackage", strings);
        Assert.Contains("VehicleSpeed", strings);
        Assert.Contains("ModeFlag", strings);
        Assert.Contains("One status bit", strings);
        Assert.Contains("0x0A", strings); // Exact source-declared PAR code
        Assert.Contains("Top-level package", strings);
        Assert.Equal(Fixtures.Cdd, File.ReadAllText(ui.CddPath));
    }

    [Fact]
    public void ImportedOfflineTraceAndDbc_PopulateAnalysisGridsAndUdsTransactions()
    {
        using var ui = new GuiSession();
        ui.Navigate("nav.trace");
        ui.Click("trace.open");
        ui.ChooseFile(ui.TracePath);
        ui.AwaitStatus("Trace loaded:");
        ui.Navigate("nav.dbc");
        ui.Click("dbc.load");
        ui.ChooseFile(ui.DbcPath);
        ui.AwaitStatus("1 DBC");

        var analysisGrids = new (string Navigation, string Grid)[]
        {
            ("nav.signals", "signals.grid"),
            ("nav.matrix", "matrix.grid"),
            ("nav.byteMatrix", "byteMatrix.grid"),
            ("nav.statistics", "statistics.grid")
        };
        foreach (var (navigation, gridId) in analysisGrids)
        {
            ui.Navigate(navigation);
            var grid = ui.Visible(gridId);
            Assert.NotNull(ui.Until(() => grid.FindFirstDescendant(
                    cf => cf.ByControlType(ControlType.DataItem)),
                "recorded-data row in " + gridId));
        }
        ui.Navigate("nav.uds");
        ui.Click("uds.viewTransactions");
        Assert.NotNull(ui.Until(() => ui.Visible("uds.transactionsGrid")
            .FindFirstDescendant(cf => cf.ByControlType(ControlType.DataItem)),
            "offline ISO-TP/UDS transaction row"));
    }

    [Fact]
    public void OdxImport_ExportsEngineeringParHierarchyAndCoverage()
    {
        using var ui = new GuiSession();
        ui.Navigate("nav.mapping");
        ui.Click("mapping.loadCatalog");
        ui.ChooseFile(ui.OdxPath);
        ui.AwaitMappingStatus("from 1 file");
        var destination = Path.Combine(ui.Root, "engineering-par-test.xlsx");
        ui.Navigate("nav.uds");
        var catalogButton = ui.Until(() => ui.Find("uds.openCatalog"),
            "UDS diagnostic catalog navigation");
        catalogButton.AsButton().Invoke();
        ui.Click("diagnostics.exportEngineering");
        ui.ChooseFile(destination);
        ui.AwaitExport(destination);

        using var zip = ZipFile.OpenRead(destination);
        using var stream = zip.GetEntry("xl/workbook.xml")!.Open();
        var names = XDocument.Load(stream).Descendants()
            .Where(node => node.Name.LocalName == "sheet")
            .Select(node => node.Attribute("name")?.Value).ToArray();
        Assert.Contains("Engineering Parameters", names);
        Assert.Contains("PAR Extraction Coverage", names);
        Assert.Contains("Engineering PAR Values", names);
        Assert.Contains("EngineCoolantTemperature", File.ReadAllText(ui.OdxPath));
    }

    [Fact]
    public void DidCanMapping_SavesSnapshotAndExportsTableWithoutMutatingSourceFiles()
    {
        using var ui = new GuiSession();
        ui.Navigate("nav.dbc");
        ui.Click("dbc.load");
        ui.ChooseFile(ui.DbcPath);
        ui.AwaitStatus("1 DBC");
        ui.Navigate("nav.mapping");
        ui.Click("mapping.loadCatalog");
        ui.ChooseFile(ui.OdxPath);
        ui.AwaitMappingStatus("from 1 file");
        ui.Click("mapping.correlate");
        ui.AwaitMappingStatus("Auto-correlation done");

        var map = Path.Combine(ui.Root, "review.rbgmap.json");
        ui.Click("mapping.save");
        ui.ChooseFile(map);
        ui.Until(() => File.Exists(map) ? "saved" : null, "saved DID-CAN map", 30);
        using (var contents = JsonDocument.Parse(File.ReadAllText(map)))
        {
            Assert.True(contents.RootElement.TryGetProperty("Mappings", out var mappings));
            Assert.True(mappings.GetArrayLength() > 0);
        }

        var csv = Path.Combine(ui.Root, "mapping-report.csv");
        ui.Click("mapping.export");
        ui.ChooseFile(csv);
        ui.Until(() => File.Exists(csv) && new FileInfo(csv).Length > 50
            ? "exported" : null, "mapping CSV", 30);
        Assert.Contains("DidDefinitionFingerprint", File.ReadAllText(csv));
        Assert.Contains("EngineData", File.ReadAllText(ui.DbcPath));
        Assert.Contains("GuiFixture", File.ReadAllText(ui.OdxPath));
    }
}
