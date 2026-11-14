using System.Windows.Threading;
using RbgCanInsight.Analysis;
using RbgCanInsight.App.Common;

namespace RbgCanInsight.App.ViewModels;

/// <summary>A physical recording choice; -1 shows all independent loggers.</summary>
public sealed record SignalLoggerChoice(int SourceIndex, string Name);

/// <summary>Signal explorer + plot: search the decoded signals, tick to plot, compare, measure.</summary>
public sealed class SignalsViewModel : ObservableObject
{
    private List<PlotSeriesViewModel> _allSeries = [];
    private IReadOnlyList<PlotSeriesViewModel> _chartSeries = [];
    private IReadOnlyList<string>? _physicalSourcePaths;
    private IReadOnlyList<SignalLoggerChoice> _loggerChoices =
        [new SignalLoggerChoice(-1, "All recordings")];
    private int _selectedLoggerIndex = -1;
    private string _searchText = string.Empty;
    private readonly DebounceTimer _searchDebounce = new();
    private readonly DispatcherTimer _playTimer;
    private readonly System.Diagnostics.Stopwatch _playbackClock = new();
    private readonly double[] _playbackSpeedChoices = [0.1, 0.25, 0.5, 1, 2, 5, 10, 20, 50, 100];
    private long _timelineStartNs = -1;
    private long _timelineEndNs = -1;
    private long _plotWindowEndNs = -1;
    private double _plotTimeWindowSeconds = 30;
    private double _playbackSpeed = 1;
    private double _timelinePercent = 100;
    private bool _isPlaying;
    private bool _isCumulativeReveal;
    private int _yAxisModeIndex;
    private bool _changingOnly;
    private bool _plottedOnly;
    private bool _showInlineLegend;
    private bool _linearInterpolation;
    private bool _batchPlotSelection;
    private bool _filteredRebuildScheduled;
    private PlotSeriesViewModel? _selectedSeries;
    private SignalChangeIndex? _selectedChangeIndex;
    private Task<SignalChangeIndex>? _selectedChangeIndexTask;
    private bool _changeNavigationInProgress;
    private RelayCommand? _previousChangeCommand;
    private RelayCommand? _nextChangeCommand;
    private RelayCommand? _clearPlotsCommand;
    private RelayCommand? _resetSignalFiltersCommand;
    private RelayCommand? _plotFilteredCommand;
    private RelayCommand? _zoomInCommand;
    private RelayCommand? _zoomOutCommand;
    private RelayCommand? _fitAllCommand;
    private RelayCommand<string>? _lastSecondsCommand;
    private RelayCommand? _headCommand;
    private RelayCommand? _previousCommand;
    private RelayCommand? _nextCommand;
    private RelayCommand? _tailCommand;
    private RelayCommand? _togglePlaybackCommand;

    public ResettableObservableCollection<PlotSeriesViewModel> FilteredSeries { get; } = [];

    /// <summary>The actual selected recorded channel/DBC signal, not a name-only lookup.</summary>
    public PlotSeriesViewModel? SelectedSeries
    {
        get => _selectedSeries;
        set
        {
            if (SetProperty(ref _selectedSeries, value))
            {
                _selectedChangeIndex = null;
                _selectedChangeIndexTask = null;
                OnPropertyChanged(nameof(SelectedSeriesText));
                OnPropertyChanged(nameof(SelectedSignalCursorReadout));
            }
        }
    }

    public bool ShowInlineLegend
    {
        get => _showInlineLegend;
        set => SetProperty(ref _showInlineLegend, value);
    }

    /// <summary>Optional straight-line display interpolation. Default
    /// sample-and-hold reflects only the frames actually observed in CAN.</summary>
    public bool LinearInterpolation
    {
        get => _linearInterpolation;
        set => SetProperty(ref _linearInterpolation, value);
    }

    public string SelectedSeriesText => SelectedSeries is { } selected
        ? $"{selected.RecordingText} · {selected.ChannelText} · {selected.MessageName}.{selected.SignalName}"
        : "Select a recorded signal to navigate its changes.";

    /// <summary>Read original, non-decimated CAN observations at the shared cursor.
    /// A held value is never described as a fresh observation or a causal fault.
    /// Binary search avoids scanning million-sample timelines on mouse moves.</summary>
    public string SelectedSignalCursorReadout
    {
        get
        {
            if (SelectedSeries is not { } series || _plotWindowEndNs < 0)
                return "Select a signal and a trace time to inspect recorded value changes.";

            var samples = series.Samples;
            if (samples.Count == 0 || _plotWindowEndNs < samples[0].TimestampNanoseconds)
                return "No decoded observation yet at this trace time.";

            var lo = 0;
            var hi = samples.Count - 1;
            while (lo < hi)
            {
                var mid = lo + (hi - lo + 1) / 2;
                if (samples[mid].TimestampNanoseconds <= _plotWindowEndNs)
                    lo = mid;
                else
                    hi = mid - 1;
            }

            var sample = samples[lo];
            var ageSeconds = Math.Max(0, _plotWindowEndNs - sample.TimestampNanoseconds) / 1e9;
            var unit = string.IsNullOrWhiteSpace(series.Unit) ? "" : " " + series.Unit;
            var current = $"t={_plotWindowEndNs / 1e9:F6}s · {series.SignalName}: {sample.Value:G9}{unit} " +
                $"(observed t={sample.TimestampNanoseconds / 1e9:F6}s, trace frame #{sample.FrameIndex + 1})";
            if (lo == 0)
                return current + " · first recorded observation" +
                    (ageSeconds > 0.0000005 ? $" · held {ageSeconds:F3}s" : "");

            var before = samples[lo - 1];
            var delta = sample.Value - before.Value;
            var comparison = delta == 0
                ? "unchanged from preceding recorded sample"
                : $"changed from {before.Value:G9}{unit} (Δ {delta:+0.#########;-0.#########;0}{unit})";
            return current + $" · {comparison} at {sample.TimestampNanoseconds / 1e9:F6}s" +
                (ageSeconds > 0.0000005 ? $" · held {ageSeconds:F3}s" : "");
        }
    }

    public SignalsViewModel()
    {
        _playTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        _playTimer.Tick += (_, _) => AdvancePlayback();
        _playbackSpeed = PlaybackSpeedStore.Current;
        PlaybackSpeedStore.Changed += (_, _) =>
        {
            if (Math.Abs(_playbackSpeed - PlaybackSpeedStore.Current) > 1e-9)
            {
                _playbackSpeed = PlaybackSpeedStore.Current;
                OnPropertyChanged(nameof(PlaybackSpeed));
            }
        };
    }

    /// <summary>All decoded timelines, including other original loggers.
    /// Exact-source navigation works independently of the current chart.</summary>
    public IReadOnlyList<PlotSeriesViewModel> AllSeries => _allSeries;

    /// <summary>Main plot uses only this source-scoped view; curve selections
    /// from other loggers stay checked and return when the picker changes.</summary>
    public IReadOnlyList<PlotSeriesViewModel> ChartSeries => _chartSeries;
    public IReadOnlyList<SignalLoggerChoice> LoggerChoices => _loggerChoices;
    public bool HasMultipleLoggers => _loggerChoices.Count > 2;

    public int SelectedLoggerIndex
    {
        get => _selectedLoggerIndex;
        set
        {
            if (value != -1 && !_loggerChoices.Any(choice => choice.SourceIndex == value))
                return;
            if (!SetProperty(ref _selectedLoggerIndex, value)) return;
            RefreshChartSeries();
            SetTimelineBounds(_chartSeries);
            RebuildFiltered();
            OnPropertyChanged(nameof(SummaryText));
            StatusText = value < 0
                ? "Showing all recordings. Independent logger clocks are not aligned."
                : $"Showing Logger {value + 1} only in this catalog and main plot; other plot selections are preserved.";
        }
    }

    private void RefreshChartSeries()
    {
        // Inexpensive references, not duplicate decoded samples or frames.
        _chartSeries = _selectedLoggerIndex < 0
            ? _allSeries.ToArray()
            : _allSeries.Where(series =>
                series.Timeline.OriginalSourceIndex == _selectedLoggerIndex).ToArray();
        OnPropertyChanged(nameof(ChartSeries));
    }

    private void RefreshLoggerChoices(bool keepCurrent)
    {
        var previouslySelected = _selectedLoggerIndex;
        // Keep every imported physical file in the picker, including
        // recordings that have no matching DBC signals.
        var available = _physicalSourcePaths is { Count: > 1 } paths
            ? Enumerable.Range(0, paths.Count)
                .Select(index => new SignalLoggerChoice(index,
                    $"Logger {index + 1} · {System.IO.Path.GetFileName(paths[index])}"))
            : _allSeries
                .Where(series => series.Timeline.OriginalSourceIndex >= 0)
                .GroupBy(series => series.Timeline.OriginalSourceIndex)
                .OrderBy(group => group.Key)
                .Select(group => new SignalLoggerChoice(group.Key,
                    $"Logger {group.Key + 1} · " +
                    (group.First().Timeline.OriginalSourcePath is { Length: > 0 } path
                        ? System.IO.Path.GetFileName(path) : "recording")));
        _loggerChoices = [new SignalLoggerChoice(-1, "All recordings"), .. available];

        // A different imported dataset must never inherit a stale Logger 2
        // selection simply because it happens to have two sources as well.
        _selectedLoggerIndex = keepCurrent &&
            _loggerChoices.Any(choice => choice.SourceIndex == previouslySelected)
            ? previouslySelected : -1;
        OnPropertyChanged(nameof(LoggerChoices));
        OnPropertyChanged(nameof(SelectedLoggerIndex));
        OnPropertyChanged(nameof(HasMultipleLoggers));
        RefreshChartSeries();
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                // Debounced: a burst of keystrokes runs one rebuild instead of one per character,
                // which matters once a database decodes into thousands of signals.
                _searchDebounce.Trigger(RebuildFiltered);
            }
        }
    }

    public string SummaryText => _allSeries.Count == 0
        ? "Load a trace and a DBC to explore signals."
        : $"{_chartSeries.Count:N0} in view / {_allSeries.Count:N0} observed · " +
          $"{_chartSeries.Count(series => series.HasObservedChanges):N0} changing · " +
          $"{_chartSeries.Count(series => series.IsPlotted):N0} plotted here";

    /// <summary>Make active search/filter scope visible without asking users
    /// to infer it from an unexpectedly empty grid.</summary>
    public string VisibleSeriesSummary => _allSeries.Count == 0 ? "No decoded signals loaded"
        : $"{FilteredSeries.Count:N0} / {_allSeries.Count:N0} recorded signals shown";

    public string EmptySignalsText => _allSeries.Count == 0
        ? "No decoded signals yet. Load a CAN Trace and a matching DBC database to begin."
        : _selectedLoggerIndex >= 0 && _chartSeries.Count == 0
            ? $"Logger {_selectedLoggerIndex + 1} has no DBC-decoded signals. The original file remains loaded. Check CAN IDs, bus assignment and DBC matching."
            : "No recorded signals match these filters. Clear filters to show all observed signals.";

    public RelayCommand ResetSignalFiltersCommand => _resetSignalFiltersCommand ??= new RelayCommand(() =>
    {
        // This is a visibility reset, NOT Clear plot: keep the operator's
        // selected curves, watch list, and trace position intact.
        _changingOnly = false;
        _plottedOnly = false;
        _searchText = string.Empty;
        _selectedLoggerIndex = -1;
        OnPropertyChanged(nameof(SelectedLoggerIndex));
        RefreshChartSeries();
        SetTimelineBounds(_chartSeries);
        OnPropertyChanged(nameof(ChangingOnly));
        OnPropertyChanged(nameof(PlottedOnly));
        OnPropertyChanged(nameof(SearchText));
        RebuildFiltered();
        OnPropertyChanged(nameof(SummaryText));
        StatusText = "Search and logger scope cleared; existing plot selections are retained.";
    });

    /// <summary>Only signals with at least one physical value transition in this recorded trace.</summary>
    public bool ChangingOnly
    {
        get => _changingOnly;
        set
        {
            if (SetProperty(ref _changingOnly, value)) RebuildFiltered();
        }
    }

    /// <summary>Focus the catalog on the signals currently selected for the main plot.</summary>
    public bool PlottedOnly
    {
        get => _plottedOnly;
        set
        {
            if (SetProperty(ref _plottedOnly, value)) RebuildFiltered();
        }
    }


    public double PlotTimeWindowSeconds
    {
        get => _plotTimeWindowSeconds;
        set
        {
            if (SetProperty(ref _plotTimeWindowSeconds, Math.Clamp(value, 0.05, 86_400)))
            {
                OnPropertyChanged(nameof(OfflinePlotNavigationText));
            }
        }
    }

    public long PlotWindowEndNanoseconds
    {
        get => _plotWindowEndNs;
        private set
        {
            if (SetProperty(ref _plotWindowEndNs, value))
            {
                _timelinePercent = ComputeTimelinePercent(value);
                OnPropertyChanged(nameof(TimelinePercent));
                OnPropertyChanged(nameof(OfflinePlotNavigationText));
                OnPropertyChanged(nameof(SelectedSignalCursorReadout));
            }
        }
    }

    /// <summary>Align the Signals replay window to an actual trace-time
    /// cursor from Trace, Quick Plot or a linked inspector. Do not clamp
    /// timestamps outside the range of recorded decoded evidence: otherwise
    /// a frame after the last signal could be mislabeled as its own sample.
    /// </summary>
    public bool TryFollowSharedCursor(long timestampNanoseconds)
    {
        if (_timelineStartNs < 0 ||
            timestampNanoseconds < _timelineStartNs ||
            timestampNanoseconds > _timelineEndNs)
            return false;

        PlotWindowEndNanoseconds = timestampNanoseconds;
        return true;
    }

    public double TimelinePercent
    {
        get => _timelinePercent;
        set
        {
            var clamped = Math.Clamp(value, 0, 100);
            if (SetProperty(ref _timelinePercent, clamped))
            {
                PlotWindowEndNanoseconds = PercentToTimestamp(clamped);
            }
        }
    }

    public string PlaybackButtonText => _isPlaying ? "Stop" : "Play";
    public IReadOnlyList<double> PlaybackSpeedChoices => _playbackSpeedChoices;

    public bool IsCumulativeReveal
    {
        get => _isCumulativeReveal;
        set => SetProperty(ref _isCumulativeReveal, value);
    }

    public IReadOnlyList<string> YAxisModeChoices { get; } = ["Auto", "Shared axis", "Per-signal"];

    /// <summary>0 = Auto (per-signal only when plotted units are mixed), 1 = always shared,
    /// 2 = always per-signal — drives SignalPlotControl.YAxisModeIndex.</summary>
    public int YAxisModeIndex
    {
        get => _yAxisModeIndex;
        set => SetProperty(ref _yAxisModeIndex, Math.Clamp(value, 0, YAxisModeChoices.Count - 1));
    }

    public double PlaybackSpeed
    {
        get => _playbackSpeed;
        set
        {
            var clamped = Math.Clamp(value, 0.1, 100);
            if (SetProperty(ref _playbackSpeed, clamped))
            {
                PlaybackSpeedStore.Current = clamped;
            }
        }
    }

    /// <summary>Full time span of all actually decoded CAN signals,
    /// even when the currently plotted ECU is silent for part of it.</summary>
    public long TimelineStartNanoseconds => _timelineStartNs;
    public long TimelineEndNanoseconds => _timelineEndNs;

    public string OfflinePlotNavigationText
    {
        get
        {
            if (_timelineStartNs < 0 || _timelineEndNs <= _timelineStartNs)
            {
                return "No offline signal timeline loaded.";
            }

            var end = PlotWindowEndNanoseconds < 0 ? _timelineEndNs : PlotWindowEndNanoseconds;
            var start = Math.Max(_timelineStartNs, end - (long)(PlotTimeWindowSeconds * 1_000_000_000d));
            return $"{start / 1_000_000_000d:F3}s .. {end / 1_000_000_000d:F3}s";
        }
    }

    public RelayCommand ClearPlotsCommand => _clearPlotsCommand ??= new RelayCommand(() =>
    {
        // Avoid N complete ObservableCollection resets while clearing thousands of
        // selections; each checkbox can otherwise raise an expensive UI rebuild.
        _batchPlotSelection = true;
        try
        {
            foreach (var series in _allSeries)
                series.IsPlotted = false;
        }
        finally
        {
            _batchPlotSelection = false;
        }

        RebuildFiltered();
        OnPropertyChanged(nameof(SummaryText));
        StatusText = "Plot selection cleared. Recorded signals remain available.";
    });

    private string _statusText = "Select signals to compare them on the plot.";

    public RelayCommand PlotFilteredCommand => _plotFilteredCommand ??= new RelayCommand(() =>
    {
        // Snapshot first: selecting a series notifies its PropertyChanged handler,
        // which ordinarily reorders/clears FilteredSeries. Iterating that changing
        // ObservableCollection caused InvalidOperationException mid-command.
        // The visible catalog intentionally sorts already-plotted curves
        // first. Selecting its first 24 would therefore do nothing after
        // the first bulk action. Prioritize NEW matching curves so repeated
        // clicks make forward progress while keeping existing selections.
        var candidates = FilteredSeries.Where(series => !series.IsPlotted).ToArray();
        var targets = candidates.Take(24).ToArray();
        var remainingBefore = candidates.Length;
        _batchPlotSelection = true;
        try
        {
            foreach (var series in targets)
                series.IsPlotted = true;
        }
        finally
        {
            _batchPlotSelection = false;
        }

        var plotted = targets.Length;
        RebuildFiltered();
        if (plotted > 0)
            FocusOnLatestPlottedSample();
        OnPropertyChanged(nameof(SummaryText));
        StatusText = plotted == 0
            ? FilteredSeries.Count == 0
                ? "No matching recorded signals. Clear filters or load a matching DBC."
                : "All visible matching signals are already plotted."
            : remainingBefore > plotted
                ? $"Added {plotted:N0} new curves; {remainingBefore - plotted:N0} matching signals remain. Click again or narrow the search."
                : $"Added {plotted:N0} new curves to the plot; existing curves were preserved.";
    });

    public RelayCommand ZoomInCommand => _zoomInCommand ??= new RelayCommand(() =>
        PlotTimeWindowSeconds = Math.Max(0.05, PlotTimeWindowSeconds / 2));

    public RelayCommand ZoomOutCommand => _zoomOutCommand ??= new RelayCommand(() =>
        PlotTimeWindowSeconds = Math.Min(86_400, PlotTimeWindowSeconds * 2));

    public RelayCommand FitAllCommand => _fitAllCommand ??= new RelayCommand(() =>
    {
        if (_timelineEndNs > _timelineStartNs)
        {
            PlotTimeWindowSeconds = Math.Max(0.05, (_timelineEndNs - _timelineStartNs) / 1_000_000_000d);
            PlotWindowEndNanoseconds = _timelineEndNs;
        }
    });

    public RelayCommand<string> LastSecondsCommand => _lastSecondsCommand ??= new RelayCommand<string>(seconds =>
    {
        if (double.TryParse(seconds, System.Globalization.CultureInfo.InvariantCulture, out var value) && value > 0)
        {
            PlotTimeWindowSeconds = value;
            PlotWindowEndNanoseconds = _timelineEndNs;
        }
    });

    /// <summary>Focus the replay on a real trace timestamp supplied by a
    /// source-frame click. Do not infer or extrapolate a signal measurement.</summary>
    public void SeekToTimestamp(long timestampNanoseconds)
    {
        if (_timelineStartNs < 0 || _timelineEndNs < _timelineStartNs)
            return;
        PlotWindowEndNanoseconds = Math.Clamp(timestampNanoseconds, _timelineStartNs, _timelineEndNs);
    }

    public RelayCommand PreviousChangeCommand => _previousChangeCommand ??=
        new RelayCommand(() => _ = NavigateChangeAsync(backward: true));
    public RelayCommand NextChangeCommand => _nextChangeCommand ??=
        new RelayCommand(() => _ = NavigateChangeAsync(backward: false));

    private async Task NavigateChangeAsync(bool backward)
    {
        if (_changeNavigationInProgress)
            return;

        if (SelectedSeries is not { } series)
        {
            StatusText = "Select a recorded signal before navigating changes.";
            return;
        }

        _changeNavigationInProgress = true;
        try
        {
            // Small traces stay synchronous and instant. On multi-million-frame
            // recordings, index building can be costly; move it off WPF's UI
            // thread and reuse the same task for the selected exact series.
            if (_selectedChangeIndex is null)
            {
                if (series.Samples.Count <= 50_000)
                    _selectedChangeIndex = new SignalChangeIndex(series.Samples);
                else
                {
                    StatusText = $"Indexing recorded changes for {series.SignalName}…";
                    var indexTask = _selectedChangeIndexTask ??=
                        Task.Run(() => new SignalChangeIndex(series.Samples));
                    var index = await indexTask;
                    if (!ReferenceEquals(SelectedSeries, series))
                        return;
                    _selectedChangeIndex = index;
                }
            }

            var cursor = PlotWindowEndNanoseconds < 0 ? _timelineEndNs : PlotWindowEndNanoseconds;
            var found = backward ? _selectedChangeIndex.Previous(cursor) : _selectedChangeIndex.Next(cursor);
            if (found is not { } sample)
            {
                StatusText = backward ? "No earlier observed change for this signal."
                    : "No later observed change for this signal.";
                return;
            }

            // Focus the plot on the recorded transition, keeping the raw Trace
            // source jump as a separate explicit action for the analyst.
            PlotWindowEndNanoseconds = sample.TimestampNanoseconds;
            StatusText = $"Observed {series.SignalName} transition at {sample.TimestampNanoseconds / 1e9:F6}s · source frame {sample.FrameIndex + 1:N0}.";
        }
        catch (Exception ex)
        {
            StatusText = "Unable to index recorded transitions: " + ex.Message;
        }
        finally
        {
            _changeNavigationInProgress = false;
        }
    }

    public RelayCommand HeadCommand => _headCommand ??= new RelayCommand(() => PlotWindowEndNanoseconds = WindowEndFromStart());
    public RelayCommand PreviousCommand => _previousCommand ??= new RelayCommand(() => MoveWindow(-1));
    public RelayCommand NextCommand => _nextCommand ??= new RelayCommand(() => MoveWindow(1));
    public RelayCommand TailCommand => _tailCommand ??= new RelayCommand(() => PlotWindowEndNanoseconds = _timelineEndNs);

    public RelayCommand TogglePlaybackCommand => _togglePlaybackCommand ??= new RelayCommand(() =>
    {
        _isPlaying = !_isPlaying;
        if (_isPlaying)
        {
            // Resume from wherever the window currently is; only rewind to the start
            // when playback had already reached (or never left) the end of the trace.
            if (_plotWindowEndNs < 0 || _plotWindowEndNs >= _timelineEndNs)
            {
                PlotWindowEndNanoseconds = WindowEndFromStart();
            }

            _playbackClock.Restart();
            _playTimer.Start();
        }
        else
        {
            _playTimer.Stop();
            _playbackClock.Reset();
        }

        OnPropertyChanged(nameof(PlaybackButtonText));
    });

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public void ReportStatus(string message) => StatusText = message;

    private readonly List<SignalTimeline> _derivedTimelines = [];

    /// <summary>Adds a derived (computed, not recorded) signal to the list and the main chart. Derived signals are
    /// kept while the recorded signals are rebuilt for the same trace and dropped when a new trace is loaded.</summary>
    public void AddDerivedSignal(SignalTimeline derived)
    {
        _derivedTimelines.RemoveAll(existing => existing.Key == derived.Key);
        _derivedTimelines.Add(derived);
        var recorded = _allSeries.Select(series => series.Timeline)
            .Where(timeline => _derivedTimelines.All(item => item.Key != timeline.Key)).ToList();
        SetTimelines(recorded, keepDerived: true);
        if (_allSeries.FirstOrDefault(series => series.Timeline.Key == derived.Key) is { } added)
        {
            added.IsPlotted = true;
            SelectedSeries = added;
            StatusText = $"Added derived signal \"{derived.Signal.Name}\" to the chart (computed from recorded samples, not recorded).";
        }
    }

    public void SetTimelines(IReadOnlyList<SignalTimeline> timelines, bool keepDerived = false,
        IReadOnlyList<string>? physicalSourcePaths = null)
    {
        if (!keepDerived)
        {
            _derivedTimelines.Clear();
            _physicalSourcePaths = physicalSourcePaths?.ToArray();
        }
        else
            timelines = timelines.Concat(_derivedTimelines.Where(derived => timelines.All(t => t.Key != derived.Key))).ToList();

        _playTimer.Stop();
        _playbackClock.Reset();
        _isPlaying = false;
        OnPropertyChanged(nameof(PlaybackButtonText));
        var previouslySelectedKey = SelectedSeries?.Timeline.Key;
        var plotted = _allSeries
            .Where(series => series.IsPlotted)
            .Select(series => series.Timeline.Key)
            .ToHashSet();

        _allSeries = timelines.Select(timeline => new PlotSeriesViewModel(timeline)).ToList();
        RefreshLoggerChoices(keepCurrent: keepDerived);
        SetTimelineBounds(_chartSeries);
        foreach (var series in _allSeries)
        {
            if (plotted.Contains(series.Timeline.Key))
            {
                series.IsPlotted = true;
            }

            series.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(PlotSeriesViewModel.IsPlotted))
                {
                    if (_batchPlotSelection)
                        return;
                    // A newly chosen single signal may only occur near the beginning of
                    // a long trace; focusing on the file tail would render an empty plot.
                    if (series.IsPlotted &&
                        (_selectedLoggerIndex < 0 ||
                         series.Timeline.OriginalSourceIndex == _selectedLoggerIndex) &&
                        _chartSeries.Count(item => item.IsPlotted) == 1 &&
                        series.Samples.Count > 0)
                        PlotWindowEndNanoseconds = series.Samples[^1].TimestampNanoseconds;
                    OnPropertyChanged(nameof(SummaryText));
                    // Do not clear a bound DataGrid synchronously inside a
                    // CheckBox binding/PropertyChanged dispatch. Rebuild once
                    // after WPF finishes the current input transaction.
                    RequestRebuildFiltered();
                }
            };
        }

        SelectedSeries = _allSeries.FirstOrDefault(series => series.Timeline.Key == previouslySelectedKey);
        RebuildFiltered();
        OnPropertyChanged(nameof(AllSeries));
        OnPropertyChanged(nameof(SummaryText));
    }

    /// <summary>Plots the given timeline (from the matrix or a trace row) and returns its series.</summary>
    public PlotSeriesViewModel? Plot(SignalTimeline timeline)
    {
        var series = _allSeries.FirstOrDefault(item => item.Timeline.Key == timeline.Key);
        if (series is not null)
        {
            series.IsPlotted = true;
            OnPropertyChanged(nameof(SummaryText));
        }

        return series;
    }

    /// <summary>Resolve a trace signal by name only when it is unambiguous among
    /// loaded databases and CAN channels. The Signals workspace allows explicit
    /// selection when several physical series have the same name.</summary>
    private PlotSeriesViewModel? FindUniqueSeries(
        string messageName, string signalName, int? channel, string? databaseName,
        uint? identifier = null, bool? isExtended = null,
        uint? sourceIdentifier = null)
    {
        var matches = _allSeries.Where(item =>
            string.Equals(item.MessageName, messageName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(item.SignalName, signalName, StringComparison.OrdinalIgnoreCase) &&
            (channel is null || item.Timeline.Channel == channel.Value) &&
            (databaseName is null || string.Equals(item.Timeline.DatabaseName,
                databaseName, StringComparison.OrdinalIgnoreCase)) &&
            (identifier is null ||
                item.Timeline.Message.Identifier == identifier.Value ||
                (sourceIdentifier is null &&
                    item.Timeline.SourceIdentifier == identifier.Value)) &&
            (sourceIdentifier is null ||
                item.Timeline.SourceIdentifier == sourceIdentifier.Value) &&
            (isExtended is null || item.Timeline.SourceIsExtended == isExtended.Value))
            .Take(2).ToArray();
        if (matches.Length != 1)
        {
            StatusText = matches.Length == 0
                ? "Signal is not available in the decoded timelines."
                : "Multiple loggers, CAN channels, DBCs or J1939 source addresses contain this signal. Choose its exact recorded source in Signals (try logger:1).";
            return null;
        }
        return matches[0];
    }

    public PlotSeriesViewModel? Plot(
        string messageName, string signalName, int? channel = null, string? databaseName = null,
        uint? identifier = null, bool? isExtended = null,
        uint? sourceIdentifier = null)
    {
        var series = FindUniqueSeries(messageName, signalName, channel, databaseName,
            identifier, isExtended, sourceIdentifier);
        if (series is not null)
        {
            TogglePlot(series);
            OnPropertyChanged(nameof(SummaryText));
        }

        return series;
    }

    /// <summary>Resolve an observed signal for a standalone Quick Plot without
    /// modifying the persistent multi-signal chart's selection.</summary>
    public PlotSeriesViewModel? FindObservedSignal(
        string messageName, string signalName, int? channel = null, string? databaseName = null,
        uint? identifier = null, bool? isExtended = null,
        uint? sourceIdentifier = null) =>
        FindUniqueSeries(messageName, signalName, channel, databaseName,
            identifier, isExtended, sourceIdentifier);

    public PlotSeriesViewModel? EnsurePlotted(
        string messageName, string signalName, int? channel = null, string? databaseName = null,
        uint? identifier = null, bool? isExtended = null,
        uint? sourceIdentifier = null)
    {
        var series = FindUniqueSeries(messageName, signalName, channel, databaseName,
            identifier, isExtended, sourceIdentifier);
        if (series is not null)
        {
            series.IsPlotted = true;
            StatusText = $"Plotted {series.Timeline.DatabaseName} / CAN {series.Timeline.Channel + 1}: {series.MessageName}.{series.SignalName}.";
            OnPropertyChanged(nameof(SummaryText));
        }

        return series;
    }

    public void TogglePlot(PlotSeriesViewModel series)
    {
        series.IsPlotted = !series.IsPlotted;
        StatusText = series.IsPlotted
            ? $"Plotted {series.MessageName}.{series.SignalName}."
            : $"Removed {series.MessageName}.{series.SignalName} from plot.";
        OnPropertyChanged(nameof(SummaryText));
    }

    /// <summary>Focus the view on actual evidence, not the end of an unrelated
    /// channel or CAN message that kept transmitting after plotted signals stopped.</summary>
    private void FocusOnLatestPlottedSample()
    {
        var ends = _chartSeries.Where(item => item.IsPlotted && item.Samples.Count > 0)
            .Select(item => item.Samples[^1].TimestampNanoseconds);
        var latest = ends.DefaultIfEmpty(-1).Max();
        if (latest >= 0)
            PlotWindowEndNanoseconds = latest;
    }

    private void SetTimelineBounds(IReadOnlyList<PlotSeriesViewModel> series)
    {
        var starts = series.Where(item => item.Samples.Count > 0).Select(item => item.Samples[0].TimestampNanoseconds).ToArray();
        var ends = series.Where(item => item.Samples.Count > 0).Select(item => item.Samples[^1].TimestampNanoseconds).ToArray();
        if (starts.Length == 0 || ends.Length == 0)
        {
            _timelineStartNs = -1;
            _timelineEndNs = -1;
            OnPropertyChanged(nameof(TimelineStartNanoseconds));
            OnPropertyChanged(nameof(TimelineEndNanoseconds));
            PlotWindowEndNanoseconds = -1;
            return;
        }

        _timelineStartNs = starts.Min();
        _timelineEndNs = ends.Max();
        OnPropertyChanged(nameof(TimelineStartNanoseconds));
        OnPropertyChanged(nameof(TimelineEndNanoseconds));
        PlotWindowEndNanoseconds = _timelineEndNs;
        OnPropertyChanged(nameof(OfflinePlotNavigationText));
    }

    private void MoveWindow(int direction)
    {
        if (_timelineStartNs < 0)
        {
            return;
        }

        var current = PlotWindowEndNanoseconds < 0 ? _timelineEndNs : PlotWindowEndNanoseconds;
        var step = Math.Max(1_000_000L, (long)(PlotTimeWindowSeconds * 1_000_000_000d * 0.8));
        PlotWindowEndNanoseconds = Math.Clamp(current + direction * step, _timelineStartNs, _timelineEndNs);
    }

    private void AdvancePlayback()
    {
        if (!_isPlaying || _timelineStartNs < 0)
        {
            return;
        }

        var current = PlotWindowEndNanoseconds < 0 ? _timelineStartNs : PlotWindowEndNanoseconds;
        var elapsed = _playbackClock.Elapsed;
        _playbackClock.Restart();
        var next = OfflineReplayClock.Advance(current, _timelineEndNs, elapsed, PlaybackSpeed);
        if (next >= _timelineEndNs)
        {
            PlotWindowEndNanoseconds = _timelineEndNs;
            _isPlaying = false;
            _playTimer.Stop();
            _playbackClock.Reset();
            OnPropertyChanged(nameof(PlaybackButtonText));
            return;
        }

        PlotWindowEndNanoseconds = next;
    }

    private long WindowEndFromStart()
    {
        if (_timelineStartNs < 0)
        {
            return -1;
        }

        var window = (long)(PlotTimeWindowSeconds * 1_000_000_000d);
        if (_timelineEndNs > _timelineStartNs && window >= _timelineEndNs - _timelineStartNs)
        {
            window = Math.Max(1_000_000L, (_timelineEndNs - _timelineStartNs) / 20);
            _plotTimeWindowSeconds = window / 1_000_000_000d;
            OnPropertyChanged(nameof(PlotTimeWindowSeconds));
        }

        return Math.Min(_timelineEndNs, _timelineStartNs + window);
    }

    private double ComputeTimelinePercent(long timestamp)
    {
        if (_timelineStartNs < 0 || _timelineEndNs <= _timelineStartNs || timestamp < 0)
        {
            return 100;
        }

        return Math.Clamp((timestamp - _timelineStartNs) * 100d / (_timelineEndNs - _timelineStartNs), 0, 100);
    }

    private long PercentToTimestamp(double percent)
    {
        if (_timelineStartNs < 0 || _timelineEndNs <= _timelineStartNs)
        {
            return -1;
        }

        return _timelineStartNs + (long)((_timelineEndNs - _timelineStartNs) * percent / 100d);
    }

    private void RequestRebuildFiltered()
    {
        if (_filteredRebuildScheduled)
            return;
        _filteredRebuildScheduled = true;
        System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(new Action(() =>
        {
            _filteredRebuildScheduled = false;
            RebuildFiltered();
        }), DispatcherPriority.Background);
    }

    private static bool MatchesSeriesTerm(PlotSeriesViewModel series, string term)
    {
        // These typed filters must match the actual extended CAN frame
        // identity only; a DBC declaration can represent several J1939
        // source addresses when PGN matching is enabled.
        if (RecordedCanSourceQuery.IsSourceFilter(term))
            return RecordedCanSourceQuery.Matches(
                series.Timeline.SourceIdentifier,
                series.Timeline.SourceIsExtended, term);

        // Explicit logger selection keeps an easy search UX while refusing
        // ambiguous source-name matches and never guessing a shared ECU.
        if (term.StartsWith("logger:", StringComparison.OrdinalIgnoreCase))
        {
            var ordinal = term.AsSpan("logger:".Length);
            return int.TryParse(ordinal,
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var number) &&
                number > 0 && series.Timeline.OriginalSourceIndex == number - 1;
        }

        static bool Has(string text, string part) =>
            text.Contains(part, StringComparison.OrdinalIgnoreCase);

        return Has(series.SignalName, term) ||
               Has(series.MessageName, term) ||
               Has(series.MessageName + "." + series.SignalName, term) ||
               Has(series.Transmitter, term) ||
               Has(series.DatabaseName, term) ||
               Has(series.RecordingText, term) ||
               Has(series.RecordingPath, term) ||
               Has(series.ChannelText, term) ||
               (series.Timeline.Channel >= 0 &&
                string.Equals("ch" + (series.Timeline.Channel + 1),
                    term, StringComparison.OrdinalIgnoreCase)) ||
               Has(series.IdentifierHex, term) ||
               Has("0x" + series.IdentifierHex, term) ||
               Has(series.Unit, term);
    }

    private void RebuildFiltered()
    {
        var desiredSelection = SelectedSeries;
        // All space-separated terms must match the SAME physical series.
        // This makes "ch2 Coolant", "0x100 Mode" and "ECM EngineLoad"
        // useful without introducing a query language into the simple UI.
        var terms = SearchText.Split(' ',
            StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var visible = new List<PlotSeriesViewModel>();
        foreach (var series in _chartSeries.OrderByDescending(series => series.IsPlotted))
        {
            if ((!ChangingOnly || series.HasObservedChanges) &&
                (!PlottedOnly || series.IsPlotted) &&
                terms.All(term => MatchesSeriesTerm(series, term)))
            {
                visible.Add(series);
            }
        }

        // One WPF Reset rather than thousands of per-row notifications;
        // preserve the exact selected series when it remains visible.
        FilteredSeries.ReplaceAll(visible);
        SelectedSeries = desiredSelection is not null && visible.Contains(desiredSelection)
            ? desiredSelection
            : visible.FirstOrDefault();
        OnPropertyChanged(nameof(VisibleSeriesSummary));
        OnPropertyChanged(nameof(EmptySignalsText));
    }
}
