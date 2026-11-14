using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using Xunit;

namespace RbgCanInsight.GuiTests;

/// <summary>
/// Screen-by-screen UI Automation acceptance tests. Every scenario launches an
/// independent real application with isolated synthetic data/preferences.
/// These are Windows desktop tests, not headless view-model tests.
/// </summary>
[Trait("Category", "WindowsFlaUI")]
public sealed class GuiSurfaceCoverageTests
{
    [Theory]
    [InlineData("nav.dashboard", "globalSearch.open")]
    [InlineData("nav.trace", "trace.search")]
    [InlineData("nav.dataStudio", "studio.datasets")]
    [InlineData("nav.uds", "uds.viewSelector")]
    [InlineData("nav.dbc", "dbc.search")]
    [InlineData("nav.signals", "signals.search")]
    [InlineData("nav.matrix", "matrix.search")]
    [InlineData("nav.byteMatrix", "byteMatrix.search")]
    [InlineData("nav.statistics", "statistics.search")]
    [InlineData("nav.mapping", "mapping.search")]
    [InlineData("nav.investigation", "investigation.signalsGrid")]
    public void Navigation_EachWorkspaceHasAnAccessiblePrimaryControl(
        string navigationId, string controlId)
    {
        using var ui = new GuiSession();
        ui.Navigate(navigationId);
        Assert.False(ui.Visible(controlId).IsOffscreen);
        Assert.NotNull(ui.Find("app.status"));
    }

    [Theory]
    [InlineData(0, "uds.servicesGrid")]
    [InlineData(1, "uds.transactionsGrid")]
    [InlineData(2, "uds.didSamplesGrid")]
    [InlineData(3, "uds.faultsGrid")]
    [InlineData(4, "uds.routinesGrid")]
    [InlineData(5, "uds.correlationGrid")]
    [InlineData(6, "uds.transportIssues")]
    [InlineData(7, "uds.findingsGrid")]
    [InlineData(8, "uds.sequenceGrid")]
    [InlineData(9, "uds.ecuSnapshotGrid")]
    [InlineData(10, "uds.coverageGrid")]
    [InlineData(11, "diagnostics.catalogGrid")]
    public void Uds_EachOfTheTwelveViewsRendersItsOwnAccessibleContent(
        int viewIndex, string expectedContentId)
    {
        using var ui = new GuiSession();
        ui.Navigate("nav.uds");
        ui.Visible("uds.viewSelector").AsComboBox().Select(viewIndex);
        Assert.False(ui.Visible(expectedContentId).IsOffscreen);
    }

    [Theory]
    [InlineData("Dark")]
    [InlineData("Light")]
    [InlineData("High Contrast")]
    public void Appearance_EachThemeCanBeSelectedWithoutLosingNavigation(string themeName)
    {
        using var ui = new GuiSession();
        var panel = ui.Visible("utilities.panel");
        panel.Patterns.ExpandCollapse.Pattern.Expand();
        var picker = ui.Visible("settings.theme").AsComboBox();
        picker.Select(themeName);
        Assert.Equal(themeName, picker.SelectedItem?.Text);
        ui.Navigate("nav.dbc");
        Assert.NotNull(ui.Visible("dbc.search"));
        ui.Navigate("nav.trace");
        Assert.NotNull(ui.Visible("trace.search"));
    }

    [Theory]
    [InlineData("nav.trace", "trace.search", "7E0")]
    [InlineData("nav.dbc", "dbc.search", "Engine")]
    [InlineData("nav.signals", "signals.search", "Coolant")]
    [InlineData("nav.matrix", "matrix.search", "ECM")]
    [InlineData("nav.byteMatrix", "byteMatrix.search", "100")]
    [InlineData("nav.statistics", "statistics.search", "Engine")]
    [InlineData("nav.mapping", "mapping.search", "F190")]
    public void WorkspaceSearch_CanEnterAndClearTextWithoutBreakingNavigation(
        string navigationId, string searchId, string query)
    {
        using var ui = new GuiSession();
        ui.Navigate(navigationId);
        ui.Fill(searchId, query);
        Assert.Equal(query, ui.Text(searchId));
        ui.Fill(searchId, "");
        ui.Until(() => ui.Text(searchId).Length == 0 ? "cleared" : null,
            "search cleared");
        ui.Navigate("nav.dashboard");
        Assert.NotNull(ui.Visible("globalSearch.open"));
    }

    [Theory]
    [InlineData("F405")]
    [InlineData("0xF190")]
    [InlineData("Engine")]
    public void DiagnosticCatalog_SearchBoxSupportsInputAndClear(string query)
    {
        using var ui = new GuiSession();
        ui.Navigate("nav.uds");
        ui.Visible("uds.viewSelector").AsComboBox().Select(11);
        ui.Fill("diagnostics.search", query);
        Assert.Equal(query, ui.Text("diagnostics.search"));
        ui.Fill("diagnostics.search", "");
        Assert.Equal("", ui.Text("diagnostics.search"));
        Assert.NotNull(ui.Visible("diagnostics.catalogGrid"));
    }

    [Theory]
    [InlineData("22")]
    [InlineData("F405")]
    [InlineData("ECM")]
    public void UdsTransactions_SearchBoxSupportsInputAndClear(string query)
    {
        using var ui = new GuiSession();
        ui.Navigate("nav.uds");
        ui.Click("uds.viewTransactions");
        ui.Fill("uds.search", query);
        Assert.Equal(query, ui.Text("uds.search"));
        ui.Fill("uds.search", "");
        Assert.Equal("", ui.Text("uds.search"));
        Assert.NotNull(ui.Visible("uds.transactionsGrid"));
    }

    [Fact]
    public void UdsQuickActions_NavigateToTransactionsFindingsAndCatalog()
    {
        using var ui = new GuiSession();
        ui.Navigate("nav.uds");
        ui.Click("uds.viewTransactions");
        Assert.False(ui.Visible("uds.transactionsGrid").IsOffscreen);
        ui.Click("uds.viewFindings");
        Assert.False(ui.Visible("uds.findingsGrid").IsOffscreen);
        ui.Click("uds.openCatalog");
        Assert.False(ui.Visible("diagnostics.catalogGrid").IsOffscreen);
    }

    [Fact]
    public void UdsViewSwitching_DoesNotLeaveWrongPaneVisible()
    {
        using var ui = new GuiSession();
        ui.Navigate("nav.uds");
        ui.Visible("uds.viewSelector").AsComboBox().Select(1);
        Assert.False(ui.Visible("uds.transactionsGrid").IsOffscreen);
        ui.Visible("uds.viewSelector").AsComboBox().Select(11);
        Assert.False(ui.Visible("diagnostics.catalogGrid").IsOffscreen);
        var prior = ui.Find("uds.transactionsGrid");
        Assert.True(prior is null || prior.IsOffscreen);
        ui.Visible("uds.viewSelector").AsComboBox().Select(1);
        Assert.False(ui.Visible("uds.transactionsGrid").IsOffscreen);
    }

    [Fact]
    public void CompactWindow_NavigationAndAllDiagnosticViewsStayReachable()
    {
        using var ui = new GuiSession();
        ui.ResizeTo(1280, 720);
        ui.Navigate("nav.uds");
        ui.Visible("uds.viewSelector").AsComboBox().Select(11);
        Assert.False(ui.Visible("diagnostics.exportEngineering").IsOffscreen);
        ui.Visible("uds.viewSelector").AsComboBox().Select(1);
        Assert.False(ui.Visible("uds.transactionsGrid").IsOffscreen);
        ui.Navigate("nav.mapping");
        Assert.NotNull(ui.Visible("mapping.search"));
        ui.Navigate("nav.trace");
        Assert.NotNull(ui.Visible("trace.search"));
    }

    [Fact]
    public void Trace_SecondaryAnalysisControlsStartCollapsedAndCanBeExpandedOnDemand()
    {
        using var ui = new GuiSession();
        ui.Navigate("nav.trace");

        Assert.False(ui.Visible("trace.search").IsOffscreen);
        Assert.False(ui.Visible("trace.grid").IsOffscreen);
        Assert.False(ui.Visible("trace.open").IsOffscreen);
        Assert.NotNull(ui.Visible("trace.navigationStatus"));
        Assert.NotNull(ui.Find("trace.replayTools"));
        Assert.Null(ui.Find("trace.head"));
        Assert.Null(ui.Find("trace.notebookNew"));
        Assert.Null(ui.Find("trace.goTime"));

        ui.Visible("trace.replayTools").Patterns.ExpandCollapse.Pattern.Expand();
        Assert.False(ui.Visible("trace.head").IsOffscreen);
        Assert.False(ui.Visible("trace.markerToggle").IsOffscreen);

        ui.Click("trace.secondaryToggle");
        Assert.False(ui.Visible("trace.notebookNew").IsOffscreen);
        Assert.False(ui.Visible("trace.goTime").IsOffscreen);
        Assert.False(ui.Visible("trace.search").IsOffscreen);
        Assert.False(ui.Visible("trace.grid").IsOffscreen);
    }

    [Fact]
    public void MinimumWindow_TraceSourceExplorerAndPrimaryControlsStayAccessible()
    {
        using var ui = new GuiSession();
        ui.ResizeTo(1024, 640);
        ui.Navigate("nav.trace");
        ui.Click("trace.open");
        ui.ChooseFile(ui.TracePath);
        ui.AwaitStatus("Trace loaded:");
        ui.Visible("trace.workspaceFiles").Patterns.ExpandCollapse.Pattern.Expand();
        Assert.False(ui.Visible("trace.workspaceAdd").IsOffscreen);
        Assert.False(ui.Visible("trace.search").IsOffscreen);
        Assert.NotNull(ui.Find("trace.grid"));
        ui.Visible("trace.workspaceFiles").Patterns.ExpandCollapse.Pattern.Collapse();
        ui.Navigate("nav.dbc");
        Assert.False(ui.Visible("dbc.search").IsOffscreen);
        ui.Navigate("nav.uds");
        Assert.False(ui.Visible("uds.viewSelector").IsOffscreen);
    }

    [Fact]
    public void SignalsPlotToolbarsStaySingleRowAndLeaveChartAndCatalogVisible()
    {
        using var ui = new GuiSession();
        ui.ResizeTo(1024, 700);
        ui.Navigate("nav.dbc");
        ui.Click("dbc.load");
        ui.ChooseFile(ui.DbcPath);
        ui.AwaitStatus("1 DBC");
        ui.Navigate("nav.trace");
        ui.Click("trace.open");
        ui.ChooseFile(ui.TracePath);
        ui.AwaitStatus("Trace loaded:");

        ui.Navigate("nav.signals");
        Assert.False(ui.Visible("signals.primaryToolbar").IsOffscreen);
        Assert.False(ui.Visible("signals.viewToolbar").IsOffscreen);
        Assert.False(ui.Visible("signals.plotSelected").IsOffscreen);
        Assert.False(ui.Visible("signals.search").IsOffscreen);
        Assert.False(ui.Visible("signals.chart").IsOffscreen);
        Assert.False(ui.Visible("signals.grid").IsOffscreen);
    }

    [Theory]
    [InlineData("studio.datasets", "studio.openRecords")]
    [InlineData("studio.analyses", "studio.newAnalysis")]
    [InlineData("studio.results", "studio.resultsGrid")]
    [InlineData("studio.compare", "studio.compareSources")]
    public void DataStudio_AllFourSectionsStayAccessibleAtMinimumWindow(
        string section, string expectedContent)
    {
        using var ui = new GuiSession();
        ui.ResizeTo(1024, 640);
        ui.Navigate("nav.dataStudio");
        ui.Click(section);
        Assert.NotNull(ui.Visible(expectedContent));
        Assert.False(ui.Visible("studio.stepDescription").IsOffscreen);
    }

    [Fact]
    public void Tour_CanBeReopenedAfterSkippingAndDismissedAgain()
    {
        using var ui = new GuiSession(runOnboarding: true);
        var tour = ui.WindowWithTitle("Quick tour");
        tour.FindFirstDescendant(cf => cf.ByAutomationId("onboarding.skip"))!
            .AsButton().Invoke();
        ui.Until(() => !ui.IsWindowOpen("Quick tour") ? "closed" : null,
            "first tour dismissed");
        ui.Visible("utilities.panel").Patterns.ExpandCollapse.Pattern.Expand();
        ui.Click("onboarding.open");
        var reopened = ui.WindowWithTitle("Quick tour");
        reopened.FindFirstDescendant(cf => cf.ByAutomationId("onboarding.skip"))!
            .AsButton().Invoke();
        ui.Until(() => !ui.IsWindowOpen("Quick tour") ? "closed" : null,
            "reopened tour dismissed");
    }

    [Fact]
    public void GlobalSearch_CanBeReopenedWithAnIsolatedQuery()
    {
        using var ui = new GuiSession();
        foreach (var query in new[] { "EngineData", "F190", "0x100" })
        {
            ui.Click("globalSearch.open");
            var window = ui.WindowWithTitle("Search");
            window.SetForeground();
            var box = ui.Until(() => window.FindFirstDescendant(
                cf => cf.ByAutomationId("globalSearch.query")), "global search input");
            box.AsTextBox().Text = query;
            Assert.Equal(query, box.AsTextBox().Text);
            ui.PressKey(0x1B);
            ui.Until(() => !ui.IsWindowOpen("Search") ? "closed" : null,
                "search dialog close", 12);
        }
    }
}
