using Xunit;

namespace RbgCanInsight.GuiTests;

/// <summary>
/// User-centred, end-to-end checks of the independent four-section Data Studio.
/// Tests exercise real WPF controls, original trace files and existing notebook
/// services on an interactive Windows desktop. No live CAN device is used.
/// </summary>
[Trait("Category", "WindowsFlaUI")]
public sealed class GuiDataStudioTests
{

    [Fact]
    public void DataStudioDiagnosticWorkbenchProvidesDtcAndDidCanInvestigationOffline()
    {
        using var ui = new GuiSession();
        ui.Navigate("nav.dataStudio");
        ui.Click("studio.compare");
        Assert.NotNull(ui.Visible("studio.dtcGrid"));
        Assert.NotNull(ui.Visible("studio.dtcTimeline"));
        Assert.NotNull(ui.Visible("studio.dtcRecords"));
        Assert.NotNull(ui.Visible("studio.dtcRecordFields"));
        Assert.NotNull(ui.Visible("studio.dtcRecordTrace"));
        Assert.NotNull(ui.Find("studio.dtcCanGrid"));
        Assert.NotNull(ui.Find("studio.dtcCanInspect"));
        Assert.NotNull(ui.Find("studio.dtcCanPlot"));
        Assert.NotNull(ui.Find("studio.dtcCanSummary"));
        Assert.NotNull(ui.Find("studio.dtcStatusEvidence"));
        Assert.NotNull(ui.Visible("studio.dtcFieldTrace"));
        Assert.NotNull(ui.Visible("studio.dtcSearch"));
        Assert.NotNull(ui.Visible("studio.saveDiagnosticInvestigation"));
        Assert.NotNull(ui.Visible("studio.mappingGrid"));
        Assert.NotNull(ui.Visible("studio.mappingSearch"));
        Assert.Contains("No DTCs decoded", ui.Visible("studio.dtcSummary").Name);
        Assert.Contains("No DID", ui.Visible("studio.mappingSummary").Name);
    }

    [Fact]
    public void SingleOriginalDtcRecordingHasVerifiedLoggerOneIdentity()
    {
        using var ui = new GuiSession();
        File.WriteAllText(ui.TracePath, """
            date Tue Jul 07 10:00:00.000 2026
            base hex  timestamps absolute
            internal events logged
            0.000000 1 7E0 Rx d 8 03 19 02 FF 00 00 00 00
            0.010000 1 7E8 Rx d 8 07 59 02 FF 12 34 56 0D
            """);
        ui.Navigate("nav.dataStudio");
        ui.Click("studio.openRecords");
        ui.ChooseFile(ui.TracePath);
        ui.AwaitStatus("Trace loaded:");
        ui.Click("studio.compare");

        var grid = ui.Visible("studio.dtcGrid");
        ui.Until(() =>
        {
            var cells = grid.FindAllDescendants(
                cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.Text));
            return cells.Any(cell => cell.Name.Contains("Logger 1",
                StringComparison.Ordinal)) ? "one verified original logger" : null;
        }, "standalone DTC trace must not show an unattributed logger");
        Assert.Contains("DTC", ui.Visible("studio.dtcSummary").Name,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void IndependentDtcCapturesCanBeInspectedInDataStudioWithoutMergingClearStatus()
    {
        using var ui = new GuiSession();
        var other = Path.Combine(ui.Root, "read-only-dtc.asc");
        File.WriteAllText(ui.TracePath, """
            date Tue Jul 07 10:00:00.000 2026
            base hex  timestamps absolute
            internal events logged
            0.000000 1 7E0 Rx d 8 03 19 02 FF 00 00 00 00
            0.010000 1 7E8 Rx d 8 07 59 02 FF 12 34 56 0D
            0.020000 1 7E0 Rx d 8 04 14 FF FF FF 00 00 00
            0.030000 1 7E8 Rx d 8 01 54 00 00 00 00 00 00
            """);
        File.WriteAllText(other, """
            date Tue Jul 07 10:00:00.000 2026
            base hex  timestamps absolute
            internal events logged
            0.000000 1 7E0 Rx d 8 03 19 02 FF 00 00 00 00
            0.010000 1 7E8 Rx d 8 07 59 02 FF 12 34 56 0D
            """);
        ui.Navigate("nav.dataStudio");
        ui.Click("studio.openRecords");
        ui.ChooseFiles(ui.TracePath, other);
        var mode = ui.WindowWithTitle("Merge mode");
        mode.FindFirstDescendant(cf => cf.ByAutomationId("merge.ok"))!
            .Patterns.Invoke.Pattern.Invoke();
        ui.AwaitStatus("Trace loaded:");
        ui.Click("studio.compare");
        var grid = ui.Visible("studio.dtcGrid");
        ui.Until(() => grid.FindAllDescendants(
            cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.DataItem)).Length == 2
            ? "two source-specific fault histories" : null, "physical DTC isolation");
        var source = ui.Visible("studio.dtcLogger").AsComboBox();
        source.Select(1);
        ui.Until(() => grid.FindAllDescendants(
            cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.DataItem)).Length == 1
            ? "first recorder fault history" : null, "first DTC logger filter");
        source.Select(2);
        ui.Until(() => grid.FindAllDescendants(
            cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.DataItem)).Length == 1
            ? "second recorder fault history" : null, "second DTC logger filter");
        source.Select(0);
        ui.Until(() => grid.FindAllDescendants(
            cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.DataItem)).Length == 2
            ? "all original histories restored" : null, "all-source DTC view");
        ui.Click("studio.dtcOpenUds");
        Assert.NotNull(ui.Visible("uds.faultsGrid"));
    }

    [Fact]
    public void BeginnerCanOpenAFileSearchFramesAndSaveAnAnalysisWithoutVisitingAdvancedTrace()
    {
        using var ui = new GuiSession();
        ui.Navigate("nav.dataStudio");
        Assert.False(ui.Visible("studio.openRecords").IsOffscreen);
        ui.Click("studio.openRecords");
        ui.ChooseFile(ui.TracePath);
        ui.AwaitStatus("Trace loaded:");
        Assert.False(ui.Visible("studio.datasetSummary").IsOffscreen);

        ui.Click("studio.exploreRecords");
        Assert.False(ui.Visible("studio.resultsGrid").IsOffscreen);
        ui.Click("studio.quickChanges");
        ui.AwaitStatus("Applied 'Changed payloads'");
        Assert.False(ui.Visible("studio.resultsGrid").IsOffscreen);
        ui.Fill("studio.query", "uds");
        ui.Click("studio.runFilter");
        ui.AwaitStatus("Trace results filtered");
        Assert.False(ui.Visible("studio.resultsGrid").IsOffscreen);
        Assert.NotNull(ui.Find("studio.sourceEvidence"));

        ui.Click("studio.saveAnalysis");
        ui.AwaitStatus("Analysis tab created");
        Assert.False(ui.Visible("studio.analysisList").IsOffscreen);
        ui.Fill("studio.analysisTitle", "Recorded UDS investigation");
        ui.Click("studio.renameAnalysis");
        ui.AwaitStatus("Analysis tab renamed");
        ui.Fill("studio.analysisNotes", "Review source frames before assigning any ECU identity.");
        ui.Click("studio.updateAnalysis");
        ui.AwaitStatus("Current Trace query");
        ui.Visible("studio.analysisList")
            .FindFirstDescendant(cf => cf.ByAutomationId("studio.openAnalysis"))!
            .Press();
        ui.AwaitStatus("Analysis tab 'Recorded UDS investigation' restored");
        Assert.False(ui.Visible("studio.resultsGrid").IsOffscreen);
        Assert.Equal("uds", ui.Text("studio.query"));
        Assert.False(ui.Visible("studio.stepDescription").IsOffscreen);
    }

    [Fact]
    public void EmptyStudioHasAUsableFourStepGuideAndNoSilentSearch()
    {
        using var ui = new GuiSession();
        ui.Navigate("nav.dataStudio");
        ui.Click("studio.results");
        ui.Fill("studio.query", "id:7E0");
        // Calling the disabled Run action is not possible until data is loaded;
        // the separate section remains visible and guides the user to Datasets.
        Assert.False(ui.Visible("studio.runFilter").IsEnabled);
        ui.Click("studio.analyses");
        Assert.False(ui.Visible("studio.newAnalysis").IsEnabled);
        Assert.False(ui.Visible("studio.saveSession").IsEnabled);
        ui.Click("studio.compare");
        Assert.False(ui.Visible("studio.compareSources").IsEnabled);
        ui.Click("studio.datasets");
        Assert.False(ui.Visible("studio.openRecords").IsOffscreen);
    }

    [Fact]
    public void TwoIndependentSourcesCanBeFocusedAndComparedInsideDataStudio()
    {
        using var ui = new GuiSession();
        var other = Path.Combine(ui.Root, "second-log.asc");
        File.Copy(ui.TracePath, other);
        ui.Navigate("nav.dataStudio");
        ui.Click("studio.openRecords");
        ui.ChooseFiles(ui.TracePath, other);
        var mode = ui.WindowWithTitle("Merge mode");
        mode.FindFirstDescendant(cf => cf.ByAutomationId("merge.ok"))!
            .Patterns.Invoke.Pattern.Invoke();
        ui.AwaitStatus("Trace loaded:");

        var focus = ui.Visible("studio.sourceList")
            .FindAllDescendants(cf => cf.ByAutomationId("studio.focusSource"));
        Assert.True(focus.Length >= 3, "Combined plus two physical sources must be listed.");
        focus[^1].Press();
        ui.AwaitStatus("Trace rows focused on");
        Assert.False(ui.Visible("studio.resultsGrid").IsOffscreen);
        Assert.Contains("second-log.asc", ui.Status, StringComparison.OrdinalIgnoreCase);

        ui.Click("studio.compare");
        Assert.True(ui.Visible("studio.compareSources").IsEnabled);
        ui.Click("studio.compareSources");
        var comparison = ui.WindowWithTitle("Source comparison");
        Assert.NotNull(comparison.FindFirstDescendant(cf => cf.ByAutomationId("trace.coverage.summary")));
        comparison.Close();
    }

    [Fact]
    public void StudioExplainsWhenACombinedSourceMustBeReloadedBeforeFocusing()
    {
        using var ui = new GuiSession();
        var other = Path.Combine(ui.Root, "second-combined.asc");
        File.Copy(ui.TracePath, other);
        ui.Navigate("nav.dataStudio");
        ui.Click("studio.openRecords");
        ui.ChooseFiles(ui.TracePath, other);
        var merge = ui.WindowWithTitle("Merge mode");
        merge.FindFirstDescendant(cf => cf.ByAutomationId("merge.ok"))!
            .Patterns.Invoke.Pattern.Invoke();
        ui.AwaitStatus("Trace loaded:");

        // Deliberately switch from the combined capture to one independent
        // source, so the merged per-file frame index is no longer available.
        var viewButtons = ui.Visible("studio.sourceList")
            .FindAllDescendants(cf => cf.ByAutomationId("studio.viewSource"));
        Assert.True(viewButtons.Length >= 3);
        viewButtons[^1].Press();
        ui.Until(() => ui.Find("studio.datasetSummary")?.Name.Contains(
            "one-file view", StringComparison.OrdinalIgnoreCase) == true ? "ready" : null,
            "standalone file loaded");

        // Explore cannot pretend to be scoped to another physical logger
        // without reconstructing the merged dataset.
        var focusButtons = ui.Visible("studio.sourceList")
            .FindAllDescendants(cf => cf.ByAutomationId("studio.focusSource"));
        focusButtons[1].Press();
        ui.AwaitStatus("Combined recordings are not loaded");
        Assert.False(ui.Visible("studio.sourceList").IsOffscreen);

        // The All traces entry has a direct View action; no technical
        // navigation or file-dialog round-trip should be required.
        var reload = ui.Visible("studio.sourceList")
            .FindAllDescendants(cf => cf.ByAutomationId("studio.viewSource"));
        reload[0].Press();
        ui.Until(() => ui.Find("studio.datasetSummary")?.Name.Contains(
            "combined view", StringComparison.OrdinalIgnoreCase) == true ? "ready" : null,
            "combined dataset restored");
        ui.Visible("studio.sourceList")
            .FindAllDescendants(cf => cf.ByAutomationId("studio.focusSource"))[^1].Press();
        ui.AwaitStatus("Trace rows focused on");
        Assert.False(ui.Visible("studio.resultsGrid").IsOffscreen);
    }

    [Fact]
    public void SessionRestoresTheLastGuidedSectionWithoutChangingLegacyNavIndices()
    {
        using var ui = new GuiSession();
        ui.Navigate("nav.dataStudio");
        ui.Click("studio.openRecords");
        ui.ChooseFile(ui.TracePath);
        ui.AwaitStatus("Trace loaded:");
        ui.Click("studio.compare");
        Assert.False(ui.Visible("studio.compareSources").IsOffscreen);
        ui.Visible("utilities.panel").Patterns.ExpandCollapse.Pattern.Expand();

        var session = Path.Combine(ui.Root, "guided-compare.rbgsession");
        ui.Click("utilities.saveSession");
        ui.ChooseFile(session);
        ui.AwaitStatus("Session saved:");
        Assert.Contains("\"DataStudioSectionIndex\": 3", File.ReadAllText(session));

        ui.Click("studio.datasets");
        Assert.False(ui.Visible("studio.openRecords").IsOffscreen);
        ui.Click("utilities.loadSession");
        ui.ChooseFile(session);
        ui.AwaitStatus("Session restored:");
        Assert.False(ui.Visible("studio.compareSources").IsOffscreen);
        Assert.False(ui.Visible("studio.stepDescription").IsOffscreen);
    }

    [Fact]
    public void UdsServiceComparisonKeepsOverlappingLoggersIndependentAndDrillsToEvidence()
    {
        using var ui = new GuiSession();
        var other = Path.Combine(ui.Root, "overlapping-uds.asc");
        File.Copy(ui.TracePath, other);
        ui.Navigate("nav.dataStudio");
        ui.Click("studio.openRecords");
        ui.ChooseFiles(ui.TracePath, other);
        var merge = ui.WindowWithTitle("Merge mode");
        merge.FindFirstDescendant(cf => cf.ByAutomationId("merge.ok"))!
            .Patterns.Invoke.Pattern.Invoke();
        ui.AwaitStatus("Trace loaded:");

        ui.Click("studio.compare");
        ui.Click("studio.udsSourceBuild");
        ui.Until(() => ui.Find("studio.udsSourceStatus")?.Name.Contains(
                "2 source/service group(s)", StringComparison.OrdinalIgnoreCase) == true
                ? "ready" : null,
            "independent source-verified UDS comparison", 60);
        Assert.False(ui.Visible("studio.udsSourceGrid").IsOffscreen);
        var reportRows = ui.Visible("studio.udsSourceGrid")
            .FindAllDescendants(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.DataItem));
        Assert.True(reportRows.Length >= 2, "One summary per physical logger is required.");
        reportRows[0].Press();
        ui.Click("studio.udsSourceWitness");
        Assert.False(ui.Visible("trace.grid").IsOffscreen);
        Assert.NotNull(ui.Find("app.status"));
    }

    [Fact]
    public void ExpertUdsTransactionGridFiltersByPhysicalLoggerWithoutReimport()
    {
        using var ui = new GuiSession();
        var second = Path.Combine(ui.Root, "expert-logger.asc");
        File.Copy(ui.TracePath, second);
        ui.Navigate("nav.trace");
        ui.Click("trace.open");
        ui.ChooseFiles(ui.TracePath, second);
        var merge = ui.WindowWithTitle("Merge mode");
        merge.FindFirstDescendant(cf => cf.ByAutomationId("merge.ok"))!
            .Patterns.Invoke.Pattern.Invoke();
        ui.AwaitStatus("Trace loaded:");

        ui.Navigate("nav.uds");
        ui.Click("uds.viewTransactions");
        Assert.False(ui.Visible("uds.physicalSource").IsOffscreen);
        var before = ui.Visible("uds.transactionsGrid")
            .FindAllDescendants(cf => cf.ByControlType(
                FlaUI.Core.Definitions.ControlType.DataItem));
        Assert.True(before.Length >= 4, "Expect two observed requests/replies per logger.");

        ui.Visible("uds.physicalSource").AsComboBox().Select(2);
        ui.Until(() =>
        {
            var items = ui.Visible("uds.transactionsGrid").FindAllDescendants(
                cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.DataItem));
            return items.Length == 2 ? "scoped" : null;
        }, "two transactions from physical logger two", 35);
        Assert.False(ui.Visible("uds.transactionsGrid").IsOffscreen);

        ui.Visible("uds.physicalSource").AsComboBox().Select(0);
        ui.Until(() =>
        {
            var items = ui.Visible("uds.transactionsGrid").FindAllDescendants(
                cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.DataItem));
            return items.Length >= 4 ? "restored" : null;
        }, "combined transaction rows restored", 35);
    }
}
