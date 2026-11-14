using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using RbgCanInsight.Analysis;
using RbgCanInsight.App.Common;
using RbgCanInsight.TraceIO;

namespace RbgCanInsight.App.ViewModels;

public sealed class MatrixRowViewModel(SignalChangeReport report)
{
    public SignalChangeReport Report { get; } = report;
    public string Ecu => Report.Timeline.Message.Transmitter;
    public string MessageName => Report.MessageName;
    public string SignalName => Report.SignalName;
    public string IdentifierHex => "0x" + Report.Identifier.ToString(Report.Timeline.Message.IsExtended ? "X8" : "X3");
    public string Unit => Report.Unit;
    public string RecordingText => Report.Timeline.OriginalSourceIndex < 0
        ? "Current recording"
        : $"Logger {Report.Timeline.OriginalSourceIndex + 1}" +
          (Report.Timeline.OriginalSourcePath is { Length: > 0 } path
              ? $" · {System.IO.Path.GetFileName(path)}" : "");
    public string OriginalSourcePath => Report.Timeline.OriginalSourcePath ?? string.Empty;
    public string ChannelText => Report.Timeline.Channel < 0
        ? "CAN ?" : $"CAN {Report.Timeline.Channel + 1}";
    public int SampleCount => Report.SampleCount;
    public int ChangeCount => Report.ChangeCount;
    public string FirstValueText => Format(Report.FirstValue);
    public string LastValueText => Format(Report.LastValue);
    public string MinMaxText => Report.SampleCount == 0 ? "-" : $"{Format(Report.MinValue)} … {Format(Report.MaxValue)}";
    public string AmplitudeText => Format(Report.Amplitude);
    public string LargestDeltaText => Format(Report.LargestDelta);
    public string FirstChangeText => Report.ChangeCount == 0 ? "-" : Seconds(Report.FirstChangeNanoseconds);
    public string LastChangeText => Report.ChangeCount == 0 ? "-" : Seconds(Report.LastChangeNanoseconds);
    public string ActivityText => Report.Activity.ToString();
    public SignalActivity Activity => Report.Activity;

    private static string Format(double value) => value.ToString("G5", CultureInfo.InvariantCulture);
    private static string Seconds(long ns) => (ns / 1_000_000_000d).ToString("F3", CultureInfo.InvariantCulture) + " s";
}

public sealed class ChangeRowViewModel(SignalChange change)
{
    public SignalChange Change { get; } = change;
    public string TimeText => (Change.TimestampNanoseconds / 1_000_000_000d).ToString("F6", CultureInfo.InvariantCulture);
    public string FromText => Change.FromValue.ToString("G6", CultureInfo.InvariantCulture);
    public string ToText => Change.ToValue.ToString("G6", CultureInfo.InvariantCulture);
    public string DeltaText => Change.Delta.ToString("+0.#####;-0.#####;0", CultureInfo.InvariantCulture);
    public int FrameIndex => Change.FrameIndex;
}

/// <summary>
/// Signal Change Matrix: which signals change, when, from which value to which value — with
/// activity classification, filtering, sorting, per-change drill-down and CSV/XLSX export.
/// </summary>
public sealed record MatrixLoggerChoice(int SourceIndex, string Name);

public sealed class MatrixViewModel : ObservableObject
{
    private IReadOnlyList<SignalChangeReport> _reports = [];
    private IReadOnlyList<string>? _physicalSourcePaths;
    private IReadOnlyList<MatrixLoggerChoice> _loggerChoices =
        [new MatrixLoggerChoice(-1, "All recordings")];
    private int _selectedLoggerIndex = -1;
    private string _searchText = string.Empty;
    private int _activityFilterIndex;
    private int _sortIndex;
    private double _minDelta;
    private MatrixRowViewModel? _selectedRow;
    private IReadOnlyList<ChangeRowViewModel> _selectedChanges = [];
    private RelayCommand? _refreshCommand;
    private RelayCommand? _plotSelectedCommand;
    private AsyncCommand? _exportCommand;

    /// <summary>Raised when the user asks to plot the selected signal.</summary>
    public event EventHandler<SignalTimeline>? PlotRequested;

    /// <summary>Raised when the user opens the source frame of a change.</summary>
    public event EventHandler<int>? FrameRequested;

    public ObservableCollection<MatrixRowViewModel> Rows { get; } = [];

    public IReadOnlyList<MatrixLoggerChoice> LoggerChoices => _loggerChoices;
    public bool HasMultipleLoggers => _loggerChoices.Count > 2;
    public int SelectedLoggerIndex
    {
        get => _selectedLoggerIndex;
        set
        {
            if (value != -1 && !_loggerChoices.Any(choice => choice.SourceIndex == value))
                return;
            if (!SetProperty(ref _selectedLoggerIndex, value)) return;
            SelectedRow = null;
            Rebuild();
            OnPropertyChanged(nameof(SummaryText));
            OnPropertyChanged(nameof(EmptyMatrixText));
        }
    }

    private IEnumerable<SignalChangeReport> ScopedReports =>
        _selectedLoggerIndex < 0 ? _reports :
        _reports.Where(report => report.Timeline.OriginalSourceIndex == _selectedLoggerIndex);

    private bool _hasTrace;
    private bool _hasDatabase;

    /// <summary>Tells the matrix what is loaded so the empty-state text can name the missing input
    /// instead of blaming a filter that was never set.</summary>
    public void SetDataState(bool hasTrace, bool hasDatabase)
    {
        if (_hasTrace == hasTrace && _hasDatabase == hasDatabase) return;
        _hasTrace = hasTrace;
        _hasDatabase = hasDatabase;
        OnPropertyChanged(nameof(EmptyMatrixText));
    }

    public string EmptyMatrixText =>
        !_hasTrace && !_hasDatabase ? "Load a trace and a DBC to see changing signals."
        : !_hasTrace ? "Load a trace to see changing signals."
        : !_hasDatabase ? "Load a DBC that matches the trace to see changing signals."
        : _selectedLoggerIndex >= 0
            ? $"No matching DBC signal evidence for Logger {_selectedLoggerIndex + 1}. The recording is still loaded; inspect its CAN IDs, buses and matching DBC in Trace."
            : "No signal rows match this matrix filter. Clear the search or load a matching DBC.";

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                Rebuild();
            }
        }
    }

    /// <summary>0=All, 1=Changed only, 2=Stable, 3=Anomalous, 4=Missing.</summary>
    public int ActivityFilterIndex
    {
        get => _activityFilterIndex;
        set
        {
            if (SetProperty(ref _activityFilterIndex, value))
            {
                Rebuild();
            }
        }
    }

    /// <summary>0=Changes desc, 1=Amplitude desc, 2=Largest step desc, 3=Name, 4=Last change.</summary>
    public int SortIndex
    {
        get => _sortIndex;
        set
        {
            if (SetProperty(ref _sortIndex, value))
            {
                Rebuild();
            }
        }
    }

    /// <summary>Minimum |largest step| to show; 0 disables the filter.</summary>
    public double MinDelta
    {
        get => _minDelta;
        set
        {
            if (SetProperty(ref _minDelta, value))
            {
                Rebuild();
            }
        }
    }

    public MatrixRowViewModel? SelectedRow
    {
        get => _selectedRow;
        set
        {
            if (SetProperty(ref _selectedRow, value))
            {
                SelectedChanges = value is null
                    ? []
                    : value.Report.Changes.Take(20_000).Select(change => new ChangeRowViewModel(change)).ToArray();
                PlotSelectedCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public IReadOnlyList<ChangeRowViewModel> SelectedChanges
    {
        get => _selectedChanges;
        private set => SetProperty(ref _selectedChanges, value);
    }

    public string SummaryText
    {
        get
        {
            if (_reports.Count == 0)
            {
                return "Load a trace and a DBC to populate the matrix.";
            }

            var scoped = ScopedReports.ToArray();
            var variable = scoped.Count(report => report.Activity == SignalActivity.Variable);
            var anomalous = scoped.Count(report => report.Activity == SignalActivity.Anomalous);
            var stable = scoped.Count(report => report.Activity == SignalActivity.Stable);
            var missing = scoped.Count(report => report.Activity == SignalActivity.Missing);
            return $"{scoped.Length:N0}/{_reports.Count:N0} signal rows · {variable:N0} changing · {stable:N0} stable · {anomalous:N0} anomalous · {missing:N0} missing";
        }
    }

    public RelayCommand RefreshCommand => _refreshCommand ??= new RelayCommand(Rebuild);

    public RelayCommand PlotSelectedCommand => _plotSelectedCommand ??= new RelayCommand(
        () =>
        {
            if (SelectedRow is { } row && row.Report.SampleCount > 0)
            {
                PlotRequested?.Invoke(this, row.Report.Timeline);
            }
        },
        () => SelectedRow is not null);

    public AsyncCommand ExportCommand => _exportCommand ??= new AsyncCommand(ExportAsync);

    public void OpenChangeFrame(ChangeRowViewModel change) =>
        FrameRequested?.Invoke(this, change.FrameIndex);

    public void SetReports(IReadOnlyList<SignalChangeReport> reports,
        IReadOnlyList<string>? physicalSourcePaths = null)
    {
        _reports = reports;
        _physicalSourcePaths = physicalSourcePaths?.ToArray();
        // A new dataset resets the physical-file index: never carry a
        // previous session's Logger 2 into an unrelated import.
        _selectedLoggerIndex = -1;
        var available = _physicalSourcePaths is { Count: > 1 } paths
            ? Enumerable.Range(0, paths.Count)
                .Select(index => new MatrixLoggerChoice(index,
                    $"Logger {index + 1} · {System.IO.Path.GetFileName(paths[index])}"))
            : reports.Where(report => report.Timeline.OriginalSourceIndex >= 0)
                .GroupBy(report => report.Timeline.OriginalSourceIndex)
                .OrderBy(group => group.Key)
                .Select(group => new MatrixLoggerChoice(group.Key,
                    $"Logger {group.Key + 1} · " +
                    System.IO.Path.GetFileName(group.First().Timeline.OriginalSourcePath ?? "")));
        _loggerChoices = [new MatrixLoggerChoice(-1, "All recordings"), .. available];
        OnPropertyChanged(nameof(LoggerChoices));
        OnPropertyChanged(nameof(SelectedLoggerIndex));
        OnPropertyChanged(nameof(HasMultipleLoggers));
        SelectedRow = null;
        Rebuild();
        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(EmptyMatrixText));
    }

    private void Rebuild()
    {
        var query = ScopedReports;
        if (ActivityFilterIndex == 1)
        {
            query = query.Where(report => report.ChangeCount > 0);
        }
        else if (ActivityFilterIndex == 2)
        {
            query = query.Where(report => report.Activity == SignalActivity.Stable);
        }
        else if (ActivityFilterIndex == 3)
        {
            query = query.Where(report => report.Activity == SignalActivity.Anomalous);
        }
        else if (ActivityFilterIndex == 4)
        {
            query = query.Where(report => report.Activity == SignalActivity.Missing);
        }

        if (MinDelta > 0)
        {
            query = query.Where(report => report.LargestDelta >= MinDelta);
        }

        var search = SearchText.Trim();
        if (search.Length > 0)
        {
            query = query.Where(report =>
                report.SignalName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                report.Timeline.Message.Transmitter.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                report.Timeline.Signal.Comment.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                report.MessageName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                report.Unit.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                ("0x" + report.Identifier.ToString("X")).Contains(search, StringComparison.OrdinalIgnoreCase) ||
                (report.Timeline.OriginalSourcePath?.Contains(search,
                    StringComparison.OrdinalIgnoreCase) ?? false));
        }

        query = SortIndex switch
        {
            1 => query.OrderByDescending(report => report.Amplitude),
            2 => query.OrderByDescending(report => report.LargestDelta),
            3 => query.OrderBy(report => report.MessageName, StringComparer.OrdinalIgnoreCase)
                      .ThenBy(report => report.SignalName, StringComparer.OrdinalIgnoreCase),
            4 => query.OrderByDescending(report => report.LastChangeNanoseconds),
            _ => query.OrderByDescending(report => report.ChangeCount)
        };

        // Replacing a filtered dataset invalidates the previous selected
        // row's change evidence; do not leave stale source-frame links.
        SelectedRow = null;
        Rows.Clear();
        foreach (var report in query)
            Rows.Add(new MatrixRowViewModel(report));
        OnPropertyChanged(nameof(EmptyMatrixText));
    }

    private async Task ExportAsync()
    {
        if (Rows.Count == 0)
        {
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "CSV (*.csv)|*.csv|Excel workbook (*.xlsx)|*.xlsx",
            DefaultExt = ".csv",
            FileName = $"signal-change-matrix-{DateTime.Now:yyyyMMdd-HHmmss}.csv",
            Title = "Export change matrix"
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var rows = Rows.ToArray();
        var columns = new List<ExportColumn>
        {
            new("ECU", rows.Select(row => row.Ecu).ToArray()),
            new("Message", rows.Select(row => row.MessageName).ToArray()),
            new("Signal", rows.Select(row => row.SignalName).ToArray()),
            new("ID", rows.Select(row => row.IdentifierHex).ToArray()),
            new("Unit", rows.Select(row => row.Unit).ToArray()),
            new("Samples", rows.Select(row => row.SampleCount.ToString(CultureInfo.InvariantCulture)).ToArray()),
            new("Changes", rows.Select(row => row.ChangeCount.ToString(CultureInfo.InvariantCulture)).ToArray()),
            new("First", rows.Select(row => row.FirstValueText).ToArray()),
            new("Last", rows.Select(row => row.LastValueText).ToArray()),
            new("Min…Max", rows.Select(row => row.MinMaxText).ToArray()),
            new("Amplitude", rows.Select(row => row.AmplitudeText).ToArray()),
            new("LargestStep", rows.Select(row => row.LargestDeltaText).ToArray()),
            new("FirstChange", rows.Select(row => row.FirstChangeText).ToArray()),
            new("LastChange", rows.Select(row => row.LastChangeText).ToArray()),
            new("Activity", rows.Select(row => row.ActivityText).ToArray()),
            // Keep legacy export columns stable; append provenance.
            new("Recording", rows.Select(row => row.RecordingText).ToArray()),
            new("OriginalSourcePath", rows.Select(row => row.OriginalSourcePath).ToArray()),
            new("CANChannel", rows.Select(row => row.ChannelText).ToArray())
        };

        try
        {
            await Task.Run(() =>
            {
                if (dialog.FileName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
                {
                    TabularExport.WriteXlsx(dialog.FileName, "Change matrix", columns);
                }
                else
                {
                    TabularExport.WriteCsv(dialog.FileName, columns);
                }
            });
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception.Message, "Export failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
