using Xunit;

namespace RbgCanInsight.GuiTests;

/// <summary>
/// Real Windows UI acceptance for notebook-like saved Trace recipes.
/// Nothing here assumes the notebook runs queries remotely or scopes UDS.
/// </summary>
[Trait("Category", "WindowsFlaUI")]
public sealed class GuiNotebookWorkspaceTests
{
    [Fact]
    public void NamedAnalysisTabsRenameDuplicateNotesAndSurviveSessionRoundTrip()
    {
        using var ui = new GuiSession();
        ui.ShowTraceSecondaryControls();
        ui.Navigate("nav.trace");
        ui.Click("trace.open");
        ui.ChooseFile(ui.TracePath);
        ui.AwaitStatus("Trace loaded:");

        Assert.False(ui.Visible("trace.notebookNew").IsOffscreen);
        ui.Click("trace.notebookNew");
        ui.AwaitStatus("Analysis tab created:");

        ui.Visible("trace.notebookEditor").Patterns.ExpandCollapse.Pattern.Expand();
        ui.Fill("trace.notebookTitle", "Cold-start investigation");
        ui.Click("trace.notebookRename");
        ui.AwaitStatus("Analysis tab renamed");
        ui.Fill("trace.notebookNotes", "Check repeated identifiers and UDS timing.");
        ui.Click("trace.notebookUpdate");
        ui.AwaitStatus("Current Trace query");

        ui.Click("trace.notebookDuplicate");
        ui.AwaitStatus("Analysis tab duplicated");
        var tabButtons = ui.Visible("trace.notebookTabs")
            .FindAllDescendants(cf => cf.ByAutomationId("trace.notebookOpen"));
        Assert.Equal(2, tabButtons.Length);
        Assert.Contains(tabButtons, button =>
            button.Name.Contains("Cold-start investigation", StringComparison.Ordinal));

        var session = Path.Combine(ui.Root, "saved-investigations.rbgsession");
        ui.Click("trace.notebookSaveSession");
        ui.ChooseFile(session);
        ui.AwaitStatus("Session saved:");
        var json = File.ReadAllText(session);
        Assert.Contains("TraceNotebooks", json, StringComparison.Ordinal);
        Assert.Contains("Cold-start investigation", json);
        Assert.Contains("Check repeated identifiers and UDS timing.", json);
        Assert.Contains("ActiveTraceNotebookId", json);

        ui.Visible("utilities.panel").Patterns.ExpandCollapse.Pattern.Expand();
        ui.Click("utilities.loadSession");
        ui.ChooseFile(session);
        ui.AwaitStatus("Session restored:");
        ui.Navigate("nav.trace");
        var restoredTabs = ui.Visible("trace.notebookTabs")
            .FindAllDescendants(cf => cf.ByAutomationId("trace.notebookOpen"));
        Assert.Equal(2, restoredTabs.Length);
        if (ui.Find("trace.notebookNotes") is not { IsOffscreen: false })
            ui.Visible("trace.notebookEditor").Patterns.ExpandCollapse.Pattern.Expand();
        Assert.Contains("Check repeated identifiers", ui.Text("trace.notebookNotes"));
        Assert.False(ui.Visible("trace.grid").IsOffscreen);
    }

    [Fact]
    public void SwitchingNotebookTabsPreservesTheOriginalCapture()
    {
        using var ui = new GuiSession();
        ui.ShowTraceSecondaryControls();
        ui.Navigate("nav.trace");
        ui.Click("trace.open");
        ui.ChooseFile(ui.TracePath);
        ui.AwaitStatus("Trace loaded:");
        ui.Click("trace.notebookNew");
        ui.AwaitStatus("Analysis tab created");
        // The tab actions live in the notebook editor expander, collapsed by default.
        ui.Visible("trace.notebookEditor").Patterns.ExpandCollapse.Pattern.Expand();
        ui.Click("trace.notebookDuplicate");
        ui.AwaitStatus("Analysis tab duplicated");
        ui.Visible("trace.notebookTabs").FindAllDescendants(
            cf => cf.ByAutomationId("trace.notebookOpen"))[0].Press();
        ui.AwaitStatus("Analysis tab 'Analysis 1' restored");
        Assert.False(ui.Visible("trace.grid").IsOffscreen);
        Assert.NotNull(ui.Visible("trace.search"));
    }

    [Fact]
    public void SourceSpecificNotebookRestoresExactLoggerAfterClearingFocusAndReloadingSession()
    {
        using var ui = new GuiSession();
        ui.ShowTraceSecondaryControls();
        var second = Path.Combine(ui.Root, "second-logger.asc");
        File.Copy(ui.TracePath, second);
        ui.Navigate("nav.trace");
        ui.Click("trace.open");
        ui.ChooseFiles(ui.TracePath, second);
        var mergeDialog = ui.WindowWithTitle("Merge mode");
        mergeDialog.FindFirstDescendant(cf => cf.ByAutomationId("merge.ok"))!
            .Patterns.Invoke.Pattern.Invoke();
        ui.AwaitStatus("Trace loaded:");
        ui.Visible("trace.workspaceFiles").Patterns.ExpandCollapse.Pattern.Expand();
        var focusButtons = ui.Visible("trace.workspaceEntries")
            .FindAllDescendants(cf => cf.ByAutomationId("trace.sourceFocus"));
        Assert.True(focusButtons.Length >= 3);
        focusButtons[^1].Press();
        ui.AwaitStatus("Trace rows focused on");
        Assert.Contains("second-logger.asc", ui.Visible("trace.sourceFocusStatus").Name);

        ui.Click("trace.notebookNew");
        ui.AwaitStatus("Analysis tab created:");
        ui.Click("trace.sourceFocusClear");
        ui.AwaitStatus("Trace rows now include all recordings");
        ui.Click("trace.notebookOpen");
        ui.AwaitStatus("Analysis tab 'Analysis 1' restored");
        Assert.Contains("second-logger.asc", ui.Visible("trace.sourceFocusStatus").Name);

        var session = Path.Combine(ui.Root, "source-notebook.rbgsession");
        ui.Click("trace.notebookSaveSession");
        ui.ChooseFile(session);
        ui.AwaitStatus("Session saved:");
        Assert.Contains("second-logger.asc", File.ReadAllText(session));
        ui.Click("trace.sourceFocusClear");
        ui.Visible("utilities.panel").Patterns.ExpandCollapse.Pattern.Expand();
        ui.Click("utilities.loadSession");
        ui.ChooseFile(session);
        ui.AwaitStatus("Session restored:");
        ui.Navigate("nav.trace");
        Assert.Contains("second-logger.asc", ui.Visible("trace.sourceFocusStatus").Name);
    }
}
