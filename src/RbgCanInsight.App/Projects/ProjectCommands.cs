using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using RbgCanInsight.Analysis.Projects;
using RbgCanInsight.App.Common;
using RbgCanInsight.App.ViewModels;

namespace RbgCanInsight.App.Projects;

/// <summary>The analysis project currently open in the app (annotations live here until saved).</summary>
public static class ProjectSession
{
    public static AnalysisProject Current { get; private set; } = new();
    public static string? CurrentPath { get; private set; }

    public static void Set(AnalysisProject project, string? path)
    {
        Current = project;
        CurrentPath = path;
    }
}

public static class ProjectCommands
{
    public const string FileFilter = "RBG CAN Insight project (*.rbgproj)|*.rbgproj";

    public static void SaveProject(Window owner, MainViewModel vm)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Save analysis project",
            Filter = FileFilter,
            DefaultExt = ".rbgproj",
            AddExtension = true,
            FileName = string.IsNullOrWhiteSpace(ProjectSession.Current.Name) ? "analysis" : ProjectSession.Current.Name,
        };
        if (ProjectSession.CurrentPath is { } existingPath && Directory.Exists(Path.GetDirectoryName(existingPath)))
            dialog.InitialDirectory = Path.GetDirectoryName(existingPath);
        if (dialog.ShowDialog(owner) != true) return;

        try
        {
            var capture = ProjectAssembler.Capture(
                dialog.FileName,
                ProjectSession.Current,
                vm.TraceWorkspacePaths,
                vm.DbcDatabases.Select(d => new ProjectDbcSource(
                    d.Path, d.ChannelIndex > 0 ? d.ChannelIndex - 1 : null, d.MatchByPgn)),
                vm.Mapping.CatalogPaths,
                CaptureMarkers(vm), vm.TraceWorkspaceMergeMode.ToString());
            ProjectStore.Save(dialog.FileName, capture.Project);
            ProjectSession.Set(capture.Project, dialog.FileName);

            var text = $"Project saved to {dialog.FileName}.\n\nIt references {capture.Project.TraceFiles.Count} trace, " +
                       $"{capture.Project.DbcFiles.Count} DBC and {capture.Project.CatalogFiles.Count} catalog file(s) with checksums, " +
                       $"{capture.Project.Markers.Count} marker(s) and {capture.Project.Annotations.Count} note(s). " +
                       "The files themselves are not copied or modified.";
            if (capture.SkippedFiles.Count > 0)
                text += "\n\nNot recorded because they no longer exist:\n" + string.Join("\n", capture.SkippedFiles);
            MessageBox.Show(owner, text, "Project saved", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(owner, "The project could not be saved: " + ex.Message, "Project not saved",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    public static async void OpenProject(Window owner, MainViewModel vm)
    {
        var dialog = new OpenFileDialog { Title = "Open analysis project", Filter = FileFilter, CheckFileExists = true };
        if (dialog.ShowDialog(owner) != true) return;
        await OpenProjectFileAsync(owner, vm, dialog.FileName);
    }

    /// <summary>Opens a project file by path (also usable for drag and drop or recent files).</summary>
    public static async Task OpenProjectFileAsync(Window owner, MainViewModel vm, string path)
    {
        ProjectLoadResult loaded;
        try
        {
            loaded = ProjectStore.Load(path);
        }
        catch (Exception ex) when (ex is ProjectFormatException or IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(owner, "This project could not be opened.\n\n" + ex.Message, "Project not opened",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        bool openChanged = false;
        if (!loaded.IsClean)
        {
            var choice = ProjectIssuesDialog.Show(owner, loaded);
            if (choice == ProjectIssuesDialog.Choice.Cancel) return;
            openChanged = choice == ProjectIssuesDialog.Choice.OpenAnyway;
        }

        var plan = ProjectAssembler.Plan(path, loaded, openChanged);
        try
        {
            if (plan.TracePaths.Count > 0)
            {
                var mode = Enum.TryParse<RbgCanInsight.TraceIO.TraceMergeMode>(loaded.Project.TraceMergeMode, true, out var parsed)
                    ? parsed : RbgCanInsight.TraceIO.TraceMergeMode.ByTime;
                await vm.LoadTraceSessionAsync(plan.TracePaths, mode);
            }
            if (plan.Dbcs.Count > 0)
            {
                await vm.LoadDbcAsync(plan.Dbcs.Select(d => d.Path).ToArray());
                foreach (var d in plan.Dbcs)
                {
                    var row = vm.DbcDatabases.FirstOrDefault(r =>
                        string.Equals(Path.GetFullPath(r.Path), Path.GetFullPath(d.Path), StringComparison.OrdinalIgnoreCase));
                    if (row is null) continue;
                    if (d.BusChannel is int bus && bus + 1 < row.ChannelChoices.Count) row.ChannelIndex = bus + 1;
                    row.MatchByPgn = d.J1939Pgn;
                }
            }
            if (plan.CatalogPaths.Count > 0) await vm.Mapping.LoadCatalogsAsync(plan.CatalogPaths);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            MessageBox.Show(owner, "A file of the project could not be loaded: " + ex.Message, "Project partly opened",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        var restored = vm.IsTraceLoaded
            ? vm.Trace.RestoreMarkers(loaded.Project.Markers.Select(m => (
                (long)Math.Round(m.TimestampSeconds * 1_000_000_000d),
                Enum.TryParse<ViewModels.MarkerCategory>(m.Category, true, out var c) ? c : default)))
            : 0;
        ProjectSession.Set(loaded.Project, path);
        vm.StatusText = $"Project opened: {Path.GetFileName(path)} ({loaded.Project.Annotations.Count} note(s), " +
                        $"{restored} of {loaded.Project.Markers.Count} saved marker(s) restored)";
    }

    private static List<ProjectMarker> CaptureMarkers(MainViewModel vm)
    {
        var frames = vm.Frames;
        var result = new List<ProjectMarker>();
        foreach (var (ts, category) in vm.Trace.MarkedFrames)
        {
            // Frame index = first frame at this timestamp (frames are time-ordered).
            int lo = 0, hi = frames.Count;
            while (lo < hi)
            {
                var mid = (lo + hi) / 2;
                if (frames[mid].TimestampNanoseconds < ts) lo = mid + 1; else hi = mid;
            }
            result.Add(new ProjectMarker
            {
                FrameIndex = lo,
                TimestampSeconds = ts / 1e9,
                Category = category.ToString(),
                Text = "",
            });
        }
        return result;
    }
}

/// <summary>Plain-language list of problems found while opening a project. 'Open anyway' exists only for changed files.</summary>
internal static class ProjectIssuesDialog
{
    public enum Choice { Cancel, Continue, OpenAnyway }

    public static Choice Show(Window owner, ProjectLoadResult loaded)
    {
        var changed = ProjectAssembler.HasChangedFiles(loaded);
        var result = Choice.Cancel;
        var window = new Window
        {
            Title = "Project files need attention",
            Width = 640, Height = 420, MinWidth = 480, MinHeight = 300,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = owner,
            ShowInTaskbar = false,
        };
        window.SetResourceReference(Window.BackgroundProperty, "BackgroundBrush");
        TitleBarTheme.Hook(window);

        var intro = changed
            ? "Some files have changed since this project was saved. Results you see may differ from the original analysis. " +
              "You can still open them, or cancel and restore the original files first."
            : "Some files could not be opened automatically. They are skipped; everything else is opened.";
        var list = new TextBox
        {
            Text = string.Join(Environment.NewLine + Environment.NewLine, loaded.Issues.Select(ProjectAssembler.Describe)),
            IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Margin = new Thickness(0, 8, 0, 8),
        };
        System.Windows.Automation.AutomationProperties.SetAutomationId(list, "project.issues.text");
        System.Windows.Automation.AutomationProperties.SetName(list, "Project file problems");

        Button Make(string content, string id, Choice choice, bool isDefault = false)
        {
            var b = new Button { Content = content, Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(14, 4, 14, 4), IsDefault = isDefault };
            System.Windows.Automation.AutomationProperties.SetAutomationId(b, id);
            System.Windows.Automation.AutomationProperties.SetName(b, content);
            b.Click += (_, _) => { result = choice; window.Close(); };
            return b;
        }

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        if (changed) buttons.Children.Add(Make("Open anyway", "project.issues.openAnyway", Choice.OpenAnyway));
        buttons.Children.Add(Make(changed ? "Open without changed files" : "Continue", "project.issues.continue", Choice.Continue, !changed));
        var cancel = Make("Cancel", "project.issues.cancel", Choice.Cancel);
        cancel.IsCancel = true;
        buttons.Children.Add(cancel);

        var panel = new DockPanel { Margin = new Thickness(14) };
        var text = new TextBlock { Text = intro, TextWrapping = TextWrapping.Wrap };
        DockPanel.SetDock(text, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        panel.Children.Add(text);
        panel.Children.Add(buttons);
        panel.Children.Add(list);
        window.Content = panel;
        window.ShowDialog();
        return result;
    }
}
