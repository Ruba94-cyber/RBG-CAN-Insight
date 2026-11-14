using System.IO.Compression;
using System.Text.Json;
using FlaUI.Core.Definitions;
using Xunit;

namespace RbgCanInsight.GuiTests;

/// <summary>Real desktop file pickers and utility actions, using disposable
/// .rbgsession/.rbgpreset/.rbglayout files and synthetic traffic only.</summary>
[Trait("Category", "WindowsFlaUI")]
public sealed class GuiPersistenceAndUtilityTests
{
    [Fact]
    public void Session_SaveContainsSyntheticTraceAndCanBeReloadedFromUi()
    {
        using var ui = new GuiSession();
        LoadTrace(ui);
        OpenUtilities(ui);
        var destination = Path.Combine(ui.Root, "offline-session.rbgsession");
        ui.Click("utilities.saveSession");
        ui.ChooseFile(destination);
        ui.Until(() => File.Exists(destination) && new FileInfo(destination).Length > 100
            ? "session saved" : null, "session save");

        using (var json = JsonDocument.Parse(File.ReadAllText(destination)))
        {
            var model = json.RootElement;
            Assert.Equal(ui.TracePath, model.GetProperty("TracePath").GetString());
            Assert.Equal(1, model.GetProperty("SelectedNavIndex").GetInt32());
        }
        ui.Click("utilities.loadSession");
        ui.ChooseFile(destination);
        ui.Until(() => ui.Status.Contains("session", StringComparison.OrdinalIgnoreCase)
            || ui.Status.Contains("loaded", StringComparison.OrdinalIgnoreCase)
            ? "session reopened" : null, "session reload", 35);
        Assert.Equal(Fixtures.Trace, File.ReadAllText(ui.TracePath));
    }

    [Fact]
    public void WorkspacePreset_SavesOnlyViewAndThemePreferences()
    {
        using var ui = new GuiSession();
        ui.Navigate("nav.dbc");
        OpenUtilities(ui);
        ui.Visible("settings.theme").AsComboBox().Select("Light");
        var target = Path.Combine(ui.Root, "light-view.rbgpreset");
        ui.Click("utilities.savePreset");
        ui.ChooseFile(target);
        // The development build may hold the file briefly after it appears.
        // Wait for a complete, readable JSON payload instead of racing on File.Exists.
        ui.Until(() =>
        {
            try
            {
                if (!File.Exists(target)) return null;
                using var check = JsonDocument.Parse(File.ReadAllText(target));
                var root = check.RootElement;
                return root.TryGetProperty("ThemeIndex", out var theme) &&
                       root.TryGetProperty("SelectedNavIndex", out var view) &&
                       theme.GetInt32() == 1 && view.GetInt32() == 2
                    ? "complete preset saved and readable" : null;
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
            catch (JsonException) { return null; }
        }, "workspace preset fully written");
        using var json = JsonDocument.Parse(File.ReadAllText(target));
        Assert.Equal(1, json.RootElement.GetProperty("ThemeIndex").GetInt32());
        Assert.Equal(2, json.RootElement.GetProperty("SelectedNavIndex").GetInt32());
        Assert.False(json.RootElement.TryGetProperty("TracePath", out _));
    }

    [Fact]
    public void UiLayout_ExportImportRestoresNavigationWithoutExternalData()
    {
        using var ui = new GuiSession();
        ui.Navigate("nav.matrix");
        OpenUtilities(ui);
        var target = Path.Combine(ui.Root, "matrix-layout.rbglayout");
        ui.Click("utilities.exportLayout");
        ui.ChooseFile(target);
        ui.Until(() => File.Exists(target) ? "layout saved" : null, "UI layout export");
        using (var json = JsonDocument.Parse(File.ReadAllText(target)))
        {
            Assert.Equal(4, json.RootElement.GetProperty("SelectedNavIndex").GetInt32());
            Assert.True(json.RootElement.TryGetProperty("Grids", out _));
        }
        ui.Navigate("nav.trace");
        OpenUtilities(ui);
        ui.Click("utilities.importLayout");
        ui.ChooseFile(target);
        Assert.NotNull(ui.Visible("matrix.grid"));
    }

    [Fact]
    public void AnalysisReport_ExportsReadableZipWithoutTouchingInputCapture()
    {
        using var ui = new GuiSession();
        LoadTrace(ui);
        OpenUtilities(ui);
        var zipPath = Path.Combine(ui.Root, "analysis.zip");
        ui.Click("utilities.exportReport");
        ui.ChooseFile(zipPath);
        ui.AwaitStatus("Analysis report exported:");
        ui.AwaitExport(zipPath);
        using var zip = ZipFile.OpenRead(zipPath);
        Assert.NotEmpty(zip.Entries);
        Assert.NotNull(zip.GetEntry("frame-evidence.csv"));
        Assert.NotNull(zip.GetEntry("frame-evidence-coverage.json"));
        Assert.NotNull(zip.GetEntry("signal-state-transitions.csv"));
        Assert.NotNull(zip.GetEntry("signal-state-transition-coverage.json"));
        using (var entry = new StreamReader(zip.GetEntry("frame-evidence-coverage.json")!.Open()))
        using (var json = JsonDocument.Parse(entry.ReadToEnd()))
        {
            Assert.Equal("RBGFRAME1", json.RootElement.GetProperty("schema").GetString());
            Assert.True(json.RootElement.GetProperty("stableIdsAvailable").GetBoolean());
            Assert.Equal(0, json.RootElement.GetProperty("rowsOmitted").GetInt32());
        }
        Assert.All(zip.Entries, entry => Assert.DoesNotContain("..", entry.FullName));
        Assert.Equal(Fixtures.Trace, File.ReadAllText(ui.TracePath));
    }

    [Fact]
    public void ReportAfterSourceFileMutation_RefusesToCertifyOldFrameIds()
    {
        using var ui = new GuiSession();
        LoadTrace(ui);

        // The application has already decoded the original trace. The file
        // at the same location is now different; a fresh export-time digest
        // cannot stand in for the original capture hash.
        var changed = Fixtures.Trace.Replace(
            "0.100000 1 100 Rx d 8 37 28 00",
            "0.100000 1 100 Rx d 8 37 28 FF",
            StringComparison.Ordinal);
        Assert.NotEqual(Fixtures.Trace, changed);
        File.WriteAllText(ui.TracePath, changed);

        OpenUtilities(ui);
        var path = Path.Combine(ui.Root, "source-changed.zip");
        ui.Click("utilities.exportReport");
        ui.ChooseFile(path);
        ui.AwaitStatus("Analysis report exported:");
        ui.AwaitExport(path);
        using var zip = ZipFile.OpenRead(path);
        using var entry = new StreamReader(zip.GetEntry("frame-evidence-coverage.json")!.Open());
        using var coverage = JsonDocument.Parse(entry.ReadToEnd());
        Assert.False(coverage.RootElement.GetProperty("stableIdsAvailable").GetBoolean());
        Assert.Equal(JsonValueKind.Null, coverage.RootElement.GetProperty("sourceSha256").ValueKind);
        Assert.Contains("changed", coverage.RootElement.GetProperty("warning").GetString(),
            StringComparison.OrdinalIgnoreCase);
        // An existing decoded UI capture must remain usable, not replaced by
        // newly changed file bytes without an explicit reimport.
        ui.Navigate("nav.trace");
        Assert.NotNull(ui.Visible("trace.grid"));
    }

    [Fact]
    public void ColumnsDialog_ShowAllThenCloseKeepsTraceGridUsable()
    {
        using var ui = new GuiSession();
        ui.Navigate("nav.trace");
        OpenUtilities(ui);
        ui.Click("utilities.columns");
        var dialog = ui.WindowWithTitle("Columns");
        var options = dialog.FindAllDescendants(cf => cf.ByControlType(ControlType.CheckBox));
        Assert.NotEmpty(options);
        dialog.FindFirstDescendant(cf => cf.ByName("Show all"))!.AsButton().Invoke();
        dialog.FindFirstDescendant(cf => cf.ByName("Close"))!.AsButton().Invoke();
        ui.Until(() => !ui.IsWindowOpen("Columns") ? "closed" : null,
            "column selector dismissed");
        Assert.NotNull(ui.Visible("trace.grid"));
    }

    [Fact]
    public void ResetWindowPosition_LeavesPrimaryWorkspaceAccessible()
    {
        using var ui = new GuiSession();
        ui.ResizeTo(1280, 720);
        OpenUtilities(ui);
        ui.Click("utilities.resetWindow");
        ui.Navigate("nav.dbc");
        Assert.NotNull(ui.Visible("dbc.messagesGrid"));
    }

    [Fact]
    public void AboutDialog_CanOpenAndCloseWithoutInterruptingOfflineWorkspace()
    {
        using var ui = new GuiSession();
        OpenUtilities(ui);
        ui.Click("utilities.about");
        var dialog = ui.WindowWithTitle("About RBG CAN Insight");
        Assert.Contains("About RBG CAN Insight", dialog.Title);
        dialog.Close();
        ui.Until(() => !ui.IsWindowOpen("About RBG CAN Insight")
            ? "closed" : null, "about dialog close");
        ui.Navigate("nav.trace");
        Assert.NotNull(ui.Visible("trace.grid"));
    }

    private static void LoadTrace(GuiSession ui)
    {
        ui.Navigate("nav.trace");
        ui.Click("trace.open");
        ui.ChooseFile(ui.TracePath);
        ui.AwaitStatus("Trace loaded:");
    }

    private static void OpenUtilities(GuiSession ui)
    {
        var panel = ui.Visible("utilities.panel");
        panel.Patterns.ExpandCollapse.Pattern.Expand();
        Assert.NotNull(ui.Visible("utilities.saveSession"));
    }
}
