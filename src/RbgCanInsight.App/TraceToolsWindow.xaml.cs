using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
using RbgCanInsight.App.Common;
using RbgCanInsight.App.ViewModels;
using RbgCanInsight.Domain;
using RbgCanInsight.TraceIO;

namespace RbgCanInsight.App;

/// <summary>Cut, shift, split and remap the loaded trace and save the result to a NEW file.</summary>
public partial class TraceToolsWindow : Window
{
    private readonly IReadOnlyList<CanFrame> _frames;
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private TraceEditOutcome? _outcome;
    private int _version;

    /// <param name="fromSeconds">Optional prefill for Cut (for example from the A/B markers).</param>
    public static void Open(Window owner, MainViewModel vm, double? fromSeconds = null, double? toSeconds = null)
    {
        if (!vm.IsTraceLoaded)
        {
            MessageBox.Show(owner, "Open a trace first.", "Trace tools", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        new TraceToolsWindow(vm, fromSeconds, toSeconds) { Owner = owner }.Show();
    }

    private TraceToolsWindow(MainViewModel vm, double? fromSeconds, double? toSeconds)
    {
        _frames = vm.Frames;
        InitializeComponent();
        TitleBarTheme.Hook(this);
        var first = _frames[0].TimestampNanoseconds / 1e9;
        var last = _frames[^1].TimestampNanoseconds / 1e9;
        SourceText.Text = $"Source: {_frames.Count:N0} frames, {Fmt(first)} s to {Fmt(last)} s.";
        FromBox.Text = Fmt(fromSeconds ?? first);
        ToBox.Text = Fmt(toSeconds ?? last);
        _debounce.Tick += (_, _) => { _debounce.Stop(); _ = RecomputeAsync(); };
        Loaded += (_, _) => _ = RecomputeAsync();
    }

    private void OnInputChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        _debounce.Stop();
        _debounce.Start();
    }

    private TraceEditRequest? ReadRequest(out string? error)
    {
        error = null;
        double a, b;
        if (CutRadio.IsChecked == true)
        {
            if (!TraceEditPlan.TryParseNumber(FromBox.Text, out a) || !TraceEditPlan.TryParseNumber(ToBox.Text, out b))
            { error = "Enter 'From' and 'To' as numbers of seconds."; return null; }
            return new TraceEditRequest(TraceEditKind.Cut, FromSeconds: a, ToSeconds: b);
        }
        if (ShiftRadio.IsChecked == true)
        {
            if (!TraceEditPlan.TryParseNumber(ShiftBox.Text, out a)) { error = "Enter the shift as a number of seconds."; return null; }
            return new TraceEditRequest(TraceEditKind.Shift, ShiftSeconds: a);
        }
        if (SplitRadio.IsChecked == true)
        {
            if (!TraceEditPlan.TryParseNumber(GapBox.Text, out a)) { error = "Enter the gap as a number of seconds."; return null; }
            return new TraceEditRequest(TraceEditKind.SplitAtGaps, GapSeconds: a);
        }
        if (!int.TryParse(ChFromBox.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var f) ||
            !int.TryParse(ChToBox.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var t))
        { error = "Enter channel numbers (1 or higher)."; return null; }
        return new TraceEditRequest(TraceEditKind.RemapChannel, FromChannel: f, ToChannel: t);
    }

    private async Task RecomputeAsync()
    {
        var version = ++_version;
        var request = ReadRequest(out var error);
        if (request is null) { Show(version, null, error); return; }
        try
        {
            var frames = _frames;
            var outcome = await Task.Run(() => TraceEditPlan.Apply(frames, request));
            Show(version, outcome, null);
        }
        catch (ArgumentException ex)
        {
            Show(version, null, ex.Message);
        }
    }

    private void Show(int version, TraceEditOutcome? outcome, string? error)
    {
        if (version != _version) return;
        _outcome = outcome;
        ResultText.Text = "";
        SaveButton.IsEnabled = outcome is { FrameCount: > 0 };
        if (outcome is null) { PreviewText.Text = error ?? ""; return; }
        if (outcome.FrameCount == 0) { PreviewText.Text = "Result: 0 frames. Nothing to save."; return; }
        var text = $"Result: {outcome.FrameCount:N0} frames, {Fmt(outcome.FirstSeconds!.Value)} s to {Fmt(outcome.LastSeconds!.Value)} s";
        if (outcome.Segments.Count > 1) text += $", in {outcome.Segments.Count} files";
        PreviewText.Text = text + ".";
    }

    private async void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (_outcome is not { FrameCount: > 0 } outcome) return;
        var dialog = new SaveFileDialog
        {
            Title = "Save edited trace as a new file",
            Filter = "Vector ASC (*.asc)|*.asc|CSV (*.csv)|*.csv|PEAK TRC (*.trc)|*.trc|Vector BLF (*.blf)|*.blf|MDF4 (*.mf4)|*.mf4",
            DefaultExt = ".asc",
            AddExtension = true,
            FileName = "edited-trace",
            OverwritePrompt = true,
        };
        if (dialog.ShowDialog(this) != true) return;

        SaveButton.IsEnabled = false;
        try
        {
            var report = new System.Text.StringBuilder();
            for (var i = 0; i < outcome.Segments.Count; i++)
            {
                var path = TraceEditPlan.SegmentPath(dialog.FileName, i, outcome.Segments.Count);
                if (outcome.Segments.Count > 1 && File.Exists(path) &&
                    MessageBox.Show(this, $"{path} already exists. Overwrite it?", "Overwrite file",
                        MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                    continue;
                var note = await WriteAsync(path, outcome.Segments[i]);
                report.AppendLine($"Saved {outcome.Segments[i].Count:N0} frames to {path}.{note}");
            }
            ResultText.Text = report.ToString().TrimEnd() + "\nThe original trace file was not modified.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            ResultText.Text = "Save failed: " + ex.Message;
        }
        finally
        {
            SaveButton.IsEnabled = true;
        }
    }

    private static async Task<string> WriteAsync(string path, IReadOnlyList<CanFrame> frames)
    {
        switch (Path.GetExtension(path).ToLowerInvariant())
        {
            case ".csv": await TraceExporter.WriteCsvAsync(path, frames); break;
            case ".trc": await TraceExporter.WriteTrcAsync(path, frames); break;
            case ".blf": await TraceExporter.WriteBlfAsync(path, frames); break;
            case ".mf4":
                var r = await Mdf4Writer.WriteAsync(path, frames);
                return r.FramesSkipped == 0 ? "" :
                    $" {r.FramesSkipped:N0} frame(s) were skipped because MDF4 cannot hold them (remote {r.RemoteFramesSkipped}, " +
                    $"error {r.ErrorFramesSkipped}, invalid length {r.InvalidLengthFramesSkipped}, channel out of range {r.ChannelOutOfRangeFramesSkipped}).";
            default: await TraceExporter.WriteAscAsync(path, frames); break;
        }
        return "";
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private static string Fmt(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);
}
