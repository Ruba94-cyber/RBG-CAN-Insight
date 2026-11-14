using FlaUI.Core.AutomationElements;
using Xunit;

namespace RbgCanInsight.GuiTests;

/// <summary>Trace tools, notes and project files. Hook buttons: tools.traceTools, tools.annotations (optional), project.save, project.open.</summary>
[Trait("Category", "WindowsFlaUI")]
public sealed class GuiProjectAndToolsTests
{
    private static GuiSession LoadedTrace()
    {
        var ui = new GuiSession();
        ui.Navigate("nav.trace");
        ui.Click("trace.open");
        ui.ChooseFile(ui.TracePath);
        ui.AwaitStatus("Trace loaded:");
        return ui;
    }

    private static AutomationElement In(Window w, string id) =>
        w.FindFirstDescendant(cf => cf.ByAutomationId(id)) ?? throw new InvalidOperationException("Missing control " + id);

    [Fact]
    public void MultipleTracesPersistInProjectAndAppearInSourceExplorer()
    {
        using var ui = new GuiSession();
        var second = Path.Combine(ui.Root, "fixture-part2.asc");
        File.Copy(ui.TracePath, second);
        ui.Navigate("nav.trace");
        ui.Click("trace.open");
        ui.ChooseFiles(ui.TracePath, second);
        var mode = ui.WindowWithTitle("Merge mode");
        In(mode, "merge.ok").Patterns.Invoke.Pattern.Invoke();
        ui.AwaitStatus("Trace loaded:");

        ui.Visible("trace.workspaceFiles").Patterns.ExpandCollapse.Pattern.Expand();
        var labels = ui.Visible("trace.workspaceEntries").FindAllDescendants()
            .Select(e => e.Name ?? "").ToArray();
        Assert.Contains(labels, name => name.Contains("fixture-part2.asc", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(labels, name => name.Contains("fixture.asc", StringComparison.OrdinalIgnoreCase));

        var project = Path.Combine(ui.Root, "multi-source.rbgproj");
        ui.Navigate("nav.dashboard");
        ui.Click("project.save");
        ui.ChooseFile(project);
        ui.AnswerMessageBox("Project saved", 2);
        var saved = File.ReadAllText(project);
        Assert.Contains("fixture.asc", saved);
        Assert.Contains("fixture-part2.asc", saved);
        Assert.Contains("traceMergeMode", saved);
    }

    [Fact]
    public void SourceFocusDoesNotReimportAndCanBeClearedFromTrace()
    {
        using var ui = new GuiSession();
        var second = Path.Combine(ui.Root, "focus-other-logger.asc");
        File.Copy(ui.TracePath, second);
        ui.Navigate("nav.trace");
        ui.Click("trace.open");
        ui.ChooseFiles(ui.TracePath, second);
        var dialog = ui.WindowWithTitle("Merge mode");
        In(dialog, "merge.ok").Patterns.Invoke.Pattern.Invoke();
        ui.AwaitStatus("Trace loaded:");

        ui.Visible("trace.workspaceFiles").Patterns.ExpandCollapse.Pattern.Expand();
        var buttons = ui.Visible("trace.workspaceEntries")
            .FindAllDescendants(cf => cf.ByAutomationId("trace.sourceFocus"));
        Assert.True(buttons.Length >= 3, "All rows and each source need a focus action");
        buttons[^1].Press();
        ui.AwaitStatus("Trace rows focused on");
        var status = ui.Visible("trace.sourceFocusStatus").Name;
        Assert.Contains("focus-other-logger.asc", status);
        Assert.False(ui.Visible("trace.grid").IsOffscreen);

        // Source focus is local to the Trace grid: switching workspaces cannot
        // trigger an implicit import or discard the active source focus.
        ui.Navigate("nav.uds");
        ui.Navigate("nav.trace");
        Assert.False(ui.Visible("trace.sourceFocusClear").IsOffscreen);
        ui.Click("trace.sourceFocusClear");
        ui.AwaitStatus("Trace rows now include all recordings");
        var cleared = ui.Find("trace.sourceFocusStatus");
        Assert.True(cleared is null || cleared.IsOffscreen);
        Assert.False(ui.Visible("trace.grid").IsOffscreen);
    }

    [Fact]
    public void SourceExplorerShowsOpenedTraceAndKeepsTheRawGridReachable()
    {
        using var ui = LoadedTrace();
        var explorer = ui.Visible("trace.workspaceFiles");
        explorer.Patterns.ExpandCollapse.Pattern.Expand();
        Assert.False(ui.Visible("trace.workspaceAdd").IsOffscreen);
        Assert.False(ui.Visible("trace.workspaceEntries").IsOffscreen);
        var sourceName = Path.GetFileName(ui.TracePath);
        var names = ui.Visible("trace.workspaceEntries")
            .FindAllDescendants()
            .Select(element => element.Name)
            .Where(name => !string.IsNullOrEmpty(name))
            .ToArray();
        Assert.Contains(names, name => name.Contains(sourceName, StringComparison.OrdinalIgnoreCase));
        Assert.False(ui.Visible("trace.search").IsOffscreen);
        Assert.NotNull(ui.Find("trace.grid"));
    }

    [Fact]
    public void TraceToolsCutPreviewShowsTheExpectedFrameCount()
    {
        using var ui = LoadedTrace();
        ui.Visible("trace.moreTools").Patterns.ExpandCollapse.Pattern.Expand();
        ui.Click("tools.traceTools");
        var window = ui.WindowWithTitle("Trace tools");
        // Fixture frames sit at 0.0 0.1 0.2 0.3 0.4 0.41 0.5 0.51 s; 0.1..0.3 inclusive keeps three.
        In(window, "traceTools.cut.from").AsTextBox().Text = "0.1";
        In(window, "traceTools.cut.to").AsTextBox().Text = "0.3";
        var preview = ui.Until(() =>
        {
            var text = In(window, "traceTools.preview").Name;
            return text.StartsWith("Result: 3 frames") ? text : null;
        }, "cut preview with 3 frames");
        Assert.Contains("0.1 s to 0.3 s", preview);
        In(window, "traceTools.op.split").Patterns.SelectionItem.Pattern.Select();
        In(window, "traceTools.split.gap").AsTextBox().Text = "0.45";
        // 0.2 -> 0.3 -> ... no gap reaches 0.45, so one segment holding all 8 frames.
        ui.Until(() => In(window, "traceTools.preview").Name.StartsWith("Result: 8 frames") ? "ok" : null, "split preview");
        window.Close();
    }

    [Fact]
    public void NotesCanBeAddedAndExportedToCsv()
    {
        using var ui = LoadedTrace();
        ui.Visible("trace.moreTools").Patterns.ExpandCollapse.Pattern.Expand();
        ui.Click("tools.annotations");
        var window = ui.WindowWithTitle("Notes");
        In(window, "annotations.title").AsTextBox().Text = "Cold start glitch";
        In(window, "annotations.note").AsTextBox().Text = "Checked against the DBC";
        In(window, "annotations.tags").AsTextBox().Text = "engine, review";
        In(window, "annotations.start").AsTextBox().Text = "0.1";
        In(window, "annotations.end").AsTextBox().Text = "0.3";
        In(window, "annotations.add").Patterns.Invoke.Pattern.Invoke();
        ui.Until(() => In(window, "annotations.summary").Name.StartsWith("1 note(s)") ? "ok" : null, "note added");

        var csv = Path.Combine(Path.GetTempPath(), "gui-notes-" + Guid.NewGuid().ToString("N") + ".csv");
        try
        {
            In(window, "annotations.exportCsv").Patterns.Invoke.Pattern.Invoke();
            ui.ChooseFile(csv);
            ui.Until(() => File.Exists(csv) ? "written" : null, "CSV file");
            var text = File.ReadAllText(csv);
            Assert.Contains("Cold start glitch", text);
            Assert.Contains("engine; review", text);
        }
        finally { try { File.Delete(csv); } catch { } }
        window.Close();
    }

    private static string SaveProjectNextToFixtures(GuiSession ui)
    {
        ui.Navigate("nav.dbc");
        ui.Click("dbc.load");
        ui.ChooseFile(ui.DbcPath);
        ui.AwaitStatus("1 DBC");
        ui.Navigate("nav.trace");
        ui.Click("trace.open");
        ui.ChooseFile(ui.TracePath);
        ui.AwaitStatus("Trace loaded:");
        ui.Navigate("nav.dashboard");
        // Saved inside the fixture folder so the files are stored as relative, verifiable references.
        var proj = Path.Combine(Path.GetDirectoryName(ui.TracePath)!, "round.rbgproj");
        ui.Click("project.save");
        ui.ChooseFile(proj);
        ui.AnswerMessageBox("Project saved", 2);
        Assert.True(File.Exists(proj));
        var json = File.ReadAllText(proj);
        Assert.Contains("fixture.asc", json);
        Assert.Contains("fixture.dbc", json);
        Assert.Contains("sha256", json, StringComparison.OrdinalIgnoreCase);
        return proj;
    }

    [Fact]
    public void ProjectSaveThenOpenRoundTripsWithoutWarnings()
    {
        using var ui = new GuiSession();
        var proj = SaveProjectNextToFixtures(ui);
        ui.Click("project.open");
        ui.ChooseFile(proj);
        ui.AwaitStatus("Project opened:");
    }

    [Fact]
    public void ChangedDbcIsReportedAndOnlyOpenedAfterOpenAnyway()
    {
        using var ui = new GuiSession();
        var proj = SaveProjectNextToFixtures(ui);
        File.AppendAllText(ui.DbcPath, "\nCM_ \"changed after the project was saved\";\n");
        ui.Click("project.open");
        ui.ChooseFile(proj);
        var dialog = ui.WindowWithTitle("Project files need attention");
        Assert.Contains("changed", In(dialog, "project.issues.text").AsTextBox().Text);
        In(dialog, "project.issues.openAnyway").Patterns.Invoke.Pattern.Invoke();
        ui.AwaitStatus("Project opened:");
    }
}
