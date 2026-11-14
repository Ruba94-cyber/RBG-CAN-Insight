using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using RbgCanInsight.Analysis.Projects;
using RbgCanInsight.App.Common;
using RbgCanInsight.App.Projects;
using RbgCanInsight.App.ViewModels;

namespace RbgCanInsight.App;

/// <summary>Analyst notes kept in the current project (<see cref="ProjectSession.Current"/>).</summary>
public partial class AnnotationsWindow : Window
{
    private readonly MainViewModel _viewModel;
    private bool _loading;

    private sealed record Row(ProjectAnnotation Note)
    {
        public string Display
        {
            get
            {
                var range = Note.StartSeconds.HasValue || Note.EndSeconds.HasValue
                    ? $"{Fmt(Note.StartSeconds)}-{Fmt(Note.EndSeconds)} s"
                    : $"frames {Note.StartFrame}-{Note.EndFrame}";
                var tags = Note.Tags.Count > 0 ? "  [" + string.Join(", ", Note.Tags) + "]" : "";
                return $"{Note.Title}  ({range}){tags}";
            }
        }
    }

    public static void Open(Window owner, MainViewModel vm)
    {
        var window = new AnnotationsWindow(vm) { Owner = owner };
        window.Show();
    }

    private AnnotationsWindow(MainViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        TitleBarTheme.Hook(this);
        Refresh(null);
        UseSelection(silent: true);
    }

    private static AnalysisProject Project => ProjectSession.Current;

    private void Refresh(ProjectAnnotation? select)
    {
        _loading = true;
        NoteList.ItemsSource = Project.Annotations.Select(a => new Row(a)).ToList();
        if (select is not null)
            NoteList.SelectedItem = ((IEnumerable<Row>)NoteList.ItemsSource).FirstOrDefault(r => ReferenceEquals(r.Note, select));
        _loading = false;
        SummaryText.Text = $"{Project.Annotations.Count} note(s) in project \"{(string.IsNullOrWhiteSpace(Project.Name) ? "unsaved project" : Project.Name)}\". " +
                           "Save the project to keep them.";
    }

    private ProjectAnnotation? Selected => (NoteList.SelectedItem as Row)?.Note;

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || Selected is not { } a) return;
        TitleBox.Text = a.Title;
        NoteBox.Text = a.Note ?? "";
        TagsBox.Text = string.Join(", ", a.Tags);
        if (a.StartSeconds.HasValue || a.EndSeconds.HasValue)
        {
            TimeRadio.IsChecked = true;
            StartBox.Text = Fmt(a.StartSeconds);
            EndBox.Text = Fmt(a.EndSeconds);
        }
        else
        {
            FrameRadio.IsChecked = true;
            StartBox.Text = a.StartFrame?.ToString(CultureInfo.InvariantCulture) ?? "";
            EndBox.Text = a.EndFrame?.ToString(CultureInfo.InvariantCulture) ?? "";
        }
        ErrorText.Text = "";
    }

    private void OnUseSelectionClick(object sender, RoutedEventArgs e) => UseSelection(silent: false);

    private void UseSelection(bool silent)
    {
        if (_viewModel.Trace.SelectedRow is not { } row)
        {
            if (!silent) ErrorText.Text = "Select a frame on the Trace page first.";
            return;
        }
        if (TimeRadio.IsChecked == true)
        {
            StartBox.Text = EndBox.Text = Fmt(row.TimestampNanoseconds / 1e9);
        }
        else
        {
            StartBox.Text = EndBox.Text = row.FrameIndex.ToString(CultureInfo.InvariantCulture);
        }
        ErrorText.Text = "";
    }

    private bool TryRead(out ProjectAnnotation note)
    {
        note = new ProjectAnnotation();
        if (string.IsNullOrWhiteSpace(TitleBox.Text)) { ErrorText.Text = "Enter a title."; return false; }
        note.Title = TitleBox.Text.Trim();
        note.Note = string.IsNullOrWhiteSpace(NoteBox.Text) ? null : NoteBox.Text;
        note.Tags = ProjectAssembler.ParseTags(TagsBox.Text);
        var hasStart = TraceEditPlanParse(StartBox.Text, out var s);
        var hasEnd = TraceEditPlanParse(EndBox.Text, out var en);
        if (!hasStart || !hasEnd) { ErrorText.Text = "Enter a numeric start and end for the range."; return false; }
        if (en < s) { ErrorText.Text = "The end must not be before the start."; return false; }
        if (TimeRadio.IsChecked == true) { note.StartSeconds = s; note.EndSeconds = en; }
        else
        {
            if (s < 0 || s != Math.Floor(s) || en != Math.Floor(en)) { ErrorText.Text = "Frame indexes must be whole numbers from 0."; return false; }
            note.StartFrame = (long)s; note.EndFrame = (long)en;
        }
        ErrorText.Text = "";
        return true;
    }

    private static bool TraceEditPlanParse(string text, out double v) =>
        RbgCanInsight.TraceIO.TraceEditPlan.TryParseNumber(text, out v);

    private void OnAddClick(object sender, RoutedEventArgs e)
    {
        if (!TryRead(out var note)) return;
        note.Id = ProjectAssembler.NewAnnotationId(Project);
        note.Author = Environment.UserName;
        Project.Annotations.Add(note);
        Refresh(note);
    }

    private void OnUpdateClick(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } target) { ErrorText.Text = "Select a note to update."; return; }
        if (!TryRead(out var edited)) return;
        target.Title = edited.Title; target.Note = edited.Note; target.Tags = edited.Tags;
        target.StartSeconds = edited.StartSeconds; target.EndSeconds = edited.EndSeconds;
        target.StartFrame = edited.StartFrame; target.EndFrame = edited.EndFrame;
        Refresh(target);
    }

    private void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } target) { ErrorText.Text = "Select a note to delete."; return; }
        Project.Annotations.Remove(target);
        Refresh(null);
        TitleBox.Text = NoteBox.Text = TagsBox.Text = "";
    }

    private void OnExportCsvClick(object sender, RoutedEventArgs e) =>
        Export("CSV files (*.csv)|*.csv", ".csv", "notes.csv", ProjectStore.ExportAnnotationsCsv(Project));

    private void OnExportMarkdownClick(object sender, RoutedEventArgs e) =>
        Export("Markdown files (*.md)|*.md", ".md", "notes.md", ProjectStore.ExportAnnotationsMarkdown(Project));

    private void Export(string filter, string ext, string name, string content)
    {
        var dialog = new SaveFileDialog { Title = "Export notes", Filter = filter, DefaultExt = ext, AddExtension = true, FileName = name };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            File.WriteAllText(dialog.FileName, content, new UTF8Encoding(false));
            ErrorText.Text = "";
            SummaryText.Text = $"Exported {Project.Annotations.Count} note(s) to {dialog.FileName}.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ErrorText.Text = "Export failed: " + ex.Message;
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private static string Fmt(double? v) => v?.ToString("0.######", CultureInfo.InvariantCulture) ?? "";
}
