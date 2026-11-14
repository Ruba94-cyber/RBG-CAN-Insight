using System.Collections;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using RbgCanInsight.Analysis;
using RbgCanInsight.App.Common;
using RbgCanInsight.Correlation;
using RbgCanInsight.Dbc;
using RbgCanInsight.Diagnostics;
using RbgCanInsight.Domain;
using RbgCanInsight.TraceIO;

namespace RbgCanInsight.App.ViewModels;

public sealed record SignalDetailRow(
    string Ecu,
    string Message,
    string Signal,
    string Physical,
    string Raw,
    string Unit,
    string Meaning,
    string PreviousPhysical,
    string Delta,
    bool IsChanged,
    uint FrameIdentifier,
    string DatabaseName = "",
    int SourceChannel = -1,
    bool? SourceIsExtended = null,
    uint? DefinitionIdentifier = null,
    long SourceTimestampNanoseconds = -1)
{
    public string MessageSignal => $"{Message}.{Signal}";
}

public sealed record RuleBookmarkPreset(string Name, string Rules);

/// <summary>Groups a trace marker by what kind of finding it represents, so the Trace grid and
/// exported reports can tell a manual note apart from a timing, diagnostic or range finding.</summary>
public enum MarkerCategory
{
    Manual,
    Timing,
    Diagnostic,
    Range
}

/// <summary>One editable row of the bookmark-rule table editor (enable toggle + rule text).</summary>
public sealed class RuleLineViewModel : ObservableObject
{
    private bool _isEnabled;
    private string _text;

    public RuleLineViewModel(string text, bool isEnabled)
    {
        _text = text;
        _isEnabled = isEnabled;
    }

    /// <summary>Raised after a property changes, so the owner can re-serialize the rule set.</summary>
    public event Action? Changed;

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (SetProperty(ref _isEnabled, value))
            {
                Changed?.Invoke();
            }
        }
    }

    public string Text
    {
        get => _text;
        set
        {
            if (SetProperty(ref _text, value))
            {
                Changed?.Invoke();
            }
        }
    }
}

/// <summary>Trace explorer: virtualised frame grid, quick filter, find, and row decode panel.</summary>
public sealed class TraceViewModel : ObservableObject
{
    private IReadOnlyList<CanFrame> _frames = [];
    private IReadOnlyList<DbcDecoder> _decoders = [];
    private Func<int, string?>? _sourceFile;
    private Func<int, string?>? _sourceEvidence;
    private Func<int, bool>? _sourceScope;
    private int _sourceScopeToken = -1;
    private string _sourceScopeLabel = string.Empty;
    private int[] _previousSameKeyIndex = [];
    private int[] _sourcePreviousSameKeyIndex = [];
    private BitArray? _changedFrameSetCache;
    private BitArray? _sourceChangedFrameSetCache;
    private TraceFrameSelectionIndex _frameSelectionIndex = new([]);
    private int[]? _cachedFilteredIndices;
    private string? _cachedFilterQuery;
    private bool _cachedChangedOnly;
    private int _filterContextVersion;
    private int _cachedFilterContextVersion = -1;
    private VirtualRowList _rows = new([], []);
    private TraceRowViewModel? _selectedRow;
    private string _searchText = string.Empty;
    private string _ruleText = string.Empty;
    private string _ruleSetName = string.Empty;
    private RuleBookmarkPreset? _selectedRuleBookmark;
    private string _appliedFilter = string.Empty;
    private string _findStatus = string.Empty;
    private string _selectedSourceByteEvidenceText = string.Empty;
    private int _selectedSourceFrameIndex = -1;
    private int _selectedSourceByteOffset;
    private int _selectedSourceByteCount;
    private readonly DispatcherTimer _playTimer;
    private readonly double[] _playbackSpeedChoices = [0.1, 0.25, 0.5, 1, 2, 5, 10, 20, 50, 100];
    private int _traceEndIndex = -1;
    private double _timelinePercent = 100;
    private double _playbackSpeed = 1;
    private int _viewModeIndex;
    private int _timeDisplayModeIndex;
    private bool _isPlaying;
    private readonly Dictionary<int, MarkerCategory> _markedFrameIndices = [];
    private MarkerCategory _selectedMarkerCategory;
    private readonly HashSet<int> _expandedFrameIndices = [];
    private IReadOnlyList<SignalDetailRow> _selectedRowSignals = [];
    private RelayCommand? _applyFilterCommand;
    private RelayCommand? _clearFilterCommand;
    private RelayCommand? _findNextCommand;
    private RelayCommand? _findPreviousCommand;
    private AsyncCommand? _exportVisibleCommand;
    private RelayCommand? _headCommand;
    private RelayCommand? _previousCommand;
    private RelayCommand? _nextCommand;
    private RelayCommand? _tailCommand;
    private RelayCommand? _togglePlaybackCommand;
    private RelayCommand? _toggleMarkerCommand;
    private RelayCommand? _previousMarkerCommand;
    private RelayCommand? _nextMarkerCommand;
    private RelayCommand? _clearMarkersCommand;
    private RelayCommand? _filterBySelectedIdCommand;
    private RelayCommand? _applyRuleBookmarksCommand;
    private RelayCommand? _saveRuleBookmarkPresetCommand;
    private RelayCommand? _deleteRuleBookmarkPresetCommand;
    private RelayCommand? _enableAllRulesCommand;
    private RelayCommand? _disableAllRulesCommand;
    private RelayCommand? _addRuleLineCommand;
    private RelayCommand<RuleLineViewModel>? _removeRuleLineCommand;
    private RelayCommand? _reloadRuleBookmarkPresetCommand;
    private bool _syncingRuleLines;
    private RelayCommand<string>? _quickAnalysisCommand;
    private RelayCommand<string>? _goToTimeCommand;
    private string _quickLensLabel = "All frames";
    private int _measurementA = -1;
    private int _measurementB = -1;
    private RelayCommand<string>? _setMeasurementCommand;

    public TraceViewModel()
    {
        _playTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(140) };
        _playTimer.Tick += (_, _) => AdvancePlayback();
        _rowColourRules = ToColourRules(new SavedQueryStore().Load());
        LoadRuleBookmarkPresets();
        RebuildRuleLinesFromText();
        _timeDisplayModeIndex = AppSettingsStore.LoadTraceTimeDisplayModeIndex();
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

    /// <summary>Bookmark rules as an editable table (one row per rule) instead of raw text.</summary>
    public ObservableCollection<RuleLineViewModel> RuleLines { get; } = [];

    public RelayCommand AddRuleLineCommand => _addRuleLineCommand ??= new RelayCommand(() =>
    {
        var line = new RuleLineViewModel(string.Empty, true);
        line.Changed += OnRuleLineChanged;
        RuleLines.Add(line);
    });

    public RelayCommand<RuleLineViewModel> RemoveRuleLineCommand => _removeRuleLineCommand ??= new RelayCommand<RuleLineViewModel>(line =>
    {
        if (line is null)
        {
            return;
        }

        line.Changed -= OnRuleLineChanged;
        RuleLines.Remove(line);
        if (RuleLines.Count == 0)
        {
            var empty = new RuleLineViewModel(string.Empty, true);
            empty.Changed += OnRuleLineChanged;
            RuleLines.Add(empty);
        }

        SyncRuleTextFromLines();
    });

    private void OnRuleLineChanged() => SyncRuleTextFromLines();

    private void SyncRuleTextFromLines()
    {
        _syncingRuleLines = true;
        try
        {
            var parts = RuleLines
                .Where(line => !string.IsNullOrWhiteSpace(line.Text))
                .Select(line => $"{(line.IsEnabled ? "[x]" : "[ ]")} {line.Text.Trim()}");
            RuleText = string.Join(Environment.NewLine, parts);
        }
        finally
        {
            _syncingRuleLines = false;
        }
    }

    private void RebuildRuleLinesFromText()
    {
        var built = new List<RuleLineViewModel>();
        foreach (var rawLine in SplitRuleLines(_ruleText))
        {
            var trimmed = rawLine.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            var enabled = !(trimmed.StartsWith("[ ]", StringComparison.Ordinal) ||
                             trimmed.StartsWith("[off]", StringComparison.OrdinalIgnoreCase) ||
                             trimmed.StartsWith("disabled:", StringComparison.OrdinalIgnoreCase));
            built.Add(new RuleLineViewModel(StripRuleToggle(trimmed), enabled));
        }

        if (built.Count == 0)
        {
            built.Add(new RuleLineViewModel(string.Empty, true));
        }

        foreach (var old in RuleLines)
        {
            old.Changed -= OnRuleLineChanged;
        }

        RuleLines.Clear();
        foreach (var line in built)
        {
            line.Changed += OnRuleLineChanged;
            RuleLines.Add(line);
        }
    }

    /// <summary>Raised when a row was selected programmatically and the grid should scroll to it.</summary>
    public event EventHandler<TraceRowViewModel>? BringRowIntoViewRequested;

    public VirtualRowList Rows
    {
        get => _rows;
        private set => SetProperty(ref _rows, value);
    }

    /// <summary>Timestamps of every marked frame, in trace time — bound by the signal plot so
    /// bookmarks show up as vertical lines there too, not just as a column in the Trace grid.
    /// Always a fresh array so a plain property-changed notification is enough for WPF to see it.</summary>
    public IReadOnlyList<long> MarkedTimestamps => _markedFrameIndices.Keys
        .OrderBy(index => index)
        .Select(index => _frames[index].TimestampNanoseconds)
        .ToArray();

    /// <summary>Every marker, ordered by trace time, for report export.</summary>
    public IReadOnlyList<(long TimestampNanoseconds, MarkerCategory Category)> MarkedFrames => _markedFrameIndices
        .OrderBy(pair => pair.Key)
        .Select(pair => (_frames[pair.Key].TimestampNanoseconds, pair.Value))
        .ToArray();

    public static IReadOnlyList<MarkerCategory> MarkerCategoryChoices { get; } = Enum.GetValues<MarkerCategory>();

    /// <summary>Category applied to the next row marked via <see cref="ToggleMarkerCommand"/> or the
    /// bookmark-rule engine — lets an analyst tag findings as timing, diagnostic or range issues
    /// instead of only a plain manual note.</summary>
    public MarkerCategory SelectedMarkerCategory
    {
        get => _selectedMarkerCategory;
        set => SetProperty(ref _selectedMarkerCategory, value);
    }

    private MarkerCategory? MarkerCategoryAt(int frameIndex) =>
        _markedFrameIndices.TryGetValue(frameIndex, out var category) ? category : null;

    private void NotifyMarkersChanged() => OnPropertyChanged(nameof(MarkedTimestamps));

    public TraceRowViewModel? SelectedRow
    {
        get => _selectedRow;
        set
        {
            if (SetProperty(ref _selectedRow, value))
            {
                // Mouse/keyboard selection is also navigation. A highlight
                // must never keep naming bytes from another raw frame.
                if (_selectedSourceFrameIndex >= 0 &&
                    value?.FrameIndex != _selectedSourceFrameIndex)
                    ClearSelectedSourceByteEvidence();
                UpdateSelectedRowSignals();
            }
        }
    }

    public IReadOnlyList<SignalDetailRow> SelectedRowSignals
    {
        get => _selectedRowSignals;
        private set => SetProperty(ref _selectedRowSignals, value);
    }

    /// <summary>
    /// Single search box: Enter applies it as a row filter, F3/▲▼ navigate matches without
    /// hiding rows. Replaces the previous separate Filter and Find inputs.
    /// </summary>
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
                OnPropertyChanged(nameof(RowCountText));
        }
    }

    public string FindStatus
    {
        get => _findStatus;
        private set => SetProperty(ref _findStatus, value);
    }

    /// <summary>The recorded byte range selected from a decoded UDS field,
    /// not a hypothetical signal/parameter position in the CAN frame.</summary>
    public string SelectedSourceByteEvidenceText
    {
        get => _selectedSourceByteEvidenceText;
        private set
        {
            if (SetProperty(ref _selectedSourceByteEvidenceText, value))
                OnPropertyChanged(nameof(HasSelectedSourceByteEvidence));
        }
    }

    public bool HasSelectedSourceByteEvidence =>
        !string.IsNullOrEmpty(SelectedSourceByteEvidenceText);

    /// <summary>The currently selected one-click analyst view, distinct from Find.</summary>
    public string QuickLensLabel
    {
        get => _quickLensLabel;
        private set => SetProperty(ref _quickLensLabel, value);
    }

    /// <summary>A/B time measurement between any two selected raw CAN frames.
    /// Independent of regular investigation bookmarks and the active view filter.</summary>
    public string MeasurementSummaryText
    {
        get
        {
            if (_measurementA < 0 && _measurementB < 0)
                return "Select a frame and set A, then select another and set B.";
            if (_measurementA < 0 || _measurementB < 0)
                return $"A/B measurement · {(_measurementA < 0 ? "Set A" : "Set B")} to compute Δt";
            var a = _frames[_measurementA];
            var b = _frames[_measurementB];
            var deltaMs = (b.TimestampNanoseconds / 1_000_000d) -
                          (a.TimestampNanoseconds / 1_000_000d);
            return $"Δt {deltaMs:+0.000000;-0.000000;0.000000} ms  ·  " +
                   $"A #{_measurementA + 1:N0} CAN{a.Channel + 1} → " +
                   $"B #{_measurementB + 1:N0} CAN{b.Channel + 1}  ·  " +
                   $"Δ frames {_measurementB - _measurementA:+0;-0;0}";
        }
    }

    public RelayCommand<string> SetMeasurementCommand =>
        _setMeasurementCommand ??= new RelayCommand<string>(which =>
        {
            if (string.Equals(which, "clear", StringComparison.OrdinalIgnoreCase))
            {
                _measurementA = -1;
                _measurementB = -1;
            }
            else if (SelectedRow is not { } row)
            {
                FindStatus = "Select a trace frame before setting measurement A or B.";
                return;
            }
            else if (string.Equals(which, "a", StringComparison.OrdinalIgnoreCase))
                _measurementA = row.FrameIndex;
            else if (string.Equals(which, "b", StringComparison.OrdinalIgnoreCase))
                _measurementB = row.FrameIndex;
            else return;

            OnPropertyChanged(nameof(MeasurementSummaryText));
        });

    public string RuleText
    {
        get => _ruleText;
        set
        {
            if (SetProperty(ref _ruleText, value) && !_syncingRuleLines)
            {
                RebuildRuleLinesFromText();
            }
        }
    }

    public string RuleSetName
    {
        get => _ruleSetName;
        set => SetProperty(ref _ruleSetName, value);
    }

    public ObservableCollection<RuleBookmarkPreset> RuleBookmarkPresets { get; } = [];

    public RuleBookmarkPreset? SelectedRuleBookmark
    {
        get => _selectedRuleBookmark;
        set
        {
            // Re-serialize the rule text even when the selection reference does not change
            // (SetProperty would then report "no change"): re-picking the same saved set after
            // editing or clearing the rule rows must still reload it, not silently no-op.
            SetProperty(ref _selectedRuleBookmark, value);
            if (value is not null)
            {
                RuleSetName = value.Name;
                RuleText = value.Rules;
            }
        }
    }

    /// <summary>0 = All frames, 1 = Latest per ID, 2 = Changes only.</summary>
    public int ViewModeIndex
    {
        get => _viewModeIndex;
        set
        {
            if (SetProperty(ref _viewModeIndex, value))
            {
                ApplyFilter();
            }
        }
    }

    private bool OverwriteRows => _viewModeIndex == 1;
    private bool ShowChangedOnly => _viewModeIndex == 2;

    public IReadOnlyList<string> TimeDisplayModeChoices { get; } =
        ["Absolute", "Relative to trace start", "Δ previous frame", "Δ previous same ID"];

    /// <summary>0 = Absolute, 1 = Relative to trace start, 2 = Delta from previous frame,
    /// 3 = Delta from previous frame of the same identifier.</summary>
    public int TimeDisplayModeIndex
    {
        get => _timeDisplayModeIndex;
        set
        {
            var clamped = Math.Clamp(value, 0, TimeDisplayModeChoices.Count - 1);
            if (SetProperty(ref _timeDisplayModeIndex, clamped))
            {
                AppSettingsStore.SaveTraceTimeDisplayModeIndex(clamped);
                ApplyFilter();
            }
        }
    }

    internal TraceTimeDisplayMode TimeDisplayMode => (TraceTimeDisplayMode)_timeDisplayModeIndex;

    internal long TraceStartTimestampNanoseconds => _frames.Count > 0 ? _frames[0].TimestampNanoseconds : 0;

    public bool IsFilterActive => _appliedFilter.Length > 0;

    /// <summary>True only when the currently shown virtual rows were built
    /// for the exact active filter/source/view state. A cancelled or failed
    /// background job keeps the previous grid and must not be called a new
    /// source-backed computation.</summary>
    public bool HasCommittedFilterRows =>
        !_isFiltering &&
        (_frames.Count == 0 ||
         OverwriteRows ||
         (_appliedFilter.Length == 0 && !ShowChangedOnly && _sourceScope is null) ||
         (_cachedFilteredIndices is not null &&
          _cachedFilterQuery == _appliedFilter &&
          _cachedChangedOnly == ShowChangedOnly &&
          _cachedFilterContextVersion == _filterContextVersion));

    public double TimelinePercent
    {
        get => _timelinePercent;
        set
        {
            var clamped = Math.Clamp(value, 0, 100);
            if (SetProperty(ref _timelinePercent, clamped))
            {
                MoveToPercent(clamped);
            }
        }
    }

    public string PlaybackButtonText => _isPlaying ? "Stop" : "Play";
    public IReadOnlyList<double> PlaybackSpeedChoices => _playbackSpeedChoices;

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

    /// <summary>The last original merged frame included in the current
    /// replay/visible-result window. At the tail this is Count - 1; saved
    /// calculation recipes intentionally use the full capture.</summary>
    public int VisibleThroughFrameIndex => _traceEndIndex;

    public string NavigationText
    {
        get
        {
            if (_frames.Count == 0)
            {
                return "No trace loaded.";
            }

            var index = Math.Clamp(_traceEndIndex, 0, _frames.Count - 1);
            var timestamp = _frames[index].TimestampNanoseconds / 1_000_000_000d;
            return $"frame {index + 1:N0} / {_frames.Count:N0} · t={timestamp:F3}s";
        }
    }

    public string RowCountText => TraceFilterFeedback.Format(
        Rows.Count, _frames.Count, _appliedFilter.Length > 0,
        !string.Equals(SearchText.Trim(), _appliedFilter, StringComparison.Ordinal));

    /// <summary>Filter text currently applied to the Trace (empty when showing every frame).</summary>
    public string AppliedQuery => _appliedFilter;

    /// <summary>Applies filter text built elsewhere (e.g. the query builder) exactly as if it had been
    /// typed into the search box and confirmed with Enter.</summary>
    public void ApplyQuery(string query)
    {
        SearchText = query;
        ApplyFilterCommand.Execute(null);
    }

    public RelayCommand ApplyFilterCommand => _applyFilterCommand ??= new RelayCommand(() =>
    {
        _appliedFilter = SearchText.Trim();
        QuickLensLabel = _appliedFilter.Length == 0 ? "All frames" : "Custom query";
        ApplyFilter();
        OnPropertyChanged(nameof(IsFilterActive));
    });

    /// <summary>CANalyzer-style one-click offline analysis lenses. They use exactly the
    /// same parser as the main search box: quick actions never hide extra data outside
    /// the current view, never alter the captured frames, and are fully reversible.</summary>
    public RelayCommand<string> QuickAnalysisCommand =>
        _quickAnalysisCommand ??= new RelayCommand<string>(ApplyQuickAnalysis);

    private void ApplyQuickAnalysis(string? lens)
    {
        if (_frames.Count == 0)
        {
            FindStatus = "Open a CAN trace to use quick analysis.";
            return;
        }

        var (query, mode, label) = lens?.ToLowerInvariant() switch
        {
            "uds" => ("uds", 0, "UDS / ISO-TP"),
            "nrc" => ("uds:neg", 0, "UDS negative responses"),
            "errors" => ("error", 0, "CAN error frames"),
            "fd" => ("fd", 0, "CAN FD"),
            "changes" => ("", 2, "Changed payloads"),
            "unmatched" => ("unmatched", 0, "Without matching DBC"),
            "remote" => ("remote", 0, "Remote frames"),
            _ => ("", 0, "All frames")
        };
        if (query == "unmatched" && _decoders.Count == 0)
        {
            FindStatus = "Load a DBC/ARXML database before searching for unmatched IDs.";
            return;
        }
        SearchText = query;
        _appliedFilter = query;
        _viewModeIndex = mode;
        QuickLensLabel = label;
        FindStatus = string.Empty;
        OnPropertyChanged(nameof(ViewModeIndex));
        OnPropertyChanged(nameof(IsFilterActive));
        ApplyFilter();
    }

    /// <summary>Jump by trace timestamp in seconds, or by elapsed seconds when the
    /// time display uses 'Relative to trace start'. No scan through the trace is needed.</summary>
    public RelayCommand<string> GoToTimeCommand =>
        _goToTimeCommand ??= new RelayCommand<string>(GoToTime);

    private void GoToTime(string? input)
    {
        if (_frames.Count == 0)
        {
            FindStatus = "Open a trace before jumping to a timestamp.";
            return;
        }
        var text = input?.Trim().TrimEnd('s', 'S').Trim();
        if (string.IsNullOrWhiteSpace(text) ||
            !(double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) ||
              double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out seconds)) ||
            !double.IsFinite(seconds))
        {
            FindStatus = "Enter a valid time in seconds, e.g. 12.345";
            return;
        }
        var target = seconds * 1_000_000_000d;
        if (_timeDisplayModeIndex == 1)
            target += _frames[0].TimestampNanoseconds;
        if (target < long.MinValue || target > long.MaxValue)
        {
            FindStatus = "The requested time is outside the supported range.";
            return;
        }
        JumpToTimestamp((long)Math.Round(target));
        FindStatus = SelectedRow is { } row
            ? $"at {row.TimestampNanoseconds / 1_000_000_000d:F6}s · frame {row.FrameIndex + 1:N0}"
            : "No frame at the requested time.";
        if (!IsFilterActive && _viewModeIndex == 0)
            QuickLensLabel = "All frames";
    }
    public RelayCommand ClearFilterCommand => _clearFilterCommand ??= new RelayCommand(() =>
    {
        SearchText = string.Empty;
        _appliedFilter = string.Empty;
        QuickLensLabel = "All frames";
        FindStatus = string.Empty;
        ApplyFilter();
        OnPropertyChanged(nameof(IsFilterActive));
    });
    public RelayCommand FindNextCommand => _findNextCommand ??= new RelayCommand(() => Find(1));
    public RelayCommand FindPreviousCommand => _findPreviousCommand ??= new RelayCommand(() => Find(-1));

    // Offline replay keys (Vector-style): F7 step, F8 animate, F9 run, Esc break, Ctrl+F9 run to next match.
    private RelayCommand? _stepForwardCommand, _stepBackCommand, _animateCommand, _runCommand, _pauseCommand;
    private AsyncCommand? _runToNextMatchCommand, _runToPreviousMatchCommand;
    private CancellationTokenSource? _breakSearch;
    private bool _animate;

    private bool _breakSearchRunning;
    public bool IsPlaying => _isPlaying;
    /// <summary>True while timed replay or a run-to-match search is active (Esc has something to break).</summary>
    public bool IsReplayActive => _isPlaying || _breakSearchRunning;
    public RelayCommand StepForwardCommand => _stepForwardCommand ??= new RelayCommand(() => StepFrames(1));
    public RelayCommand StepBackCommand => _stepBackCommand ??= new RelayCommand(() => StepFrames(-1));
    public RelayCommand AnimateCommand => _animateCommand ??= new RelayCommand(() => StartReplay(animate: true));
    public RelayCommand RunCommand => _runCommand ??= new RelayCommand(() => StartReplay(animate: false));
    public RelayCommand PauseCommand => _pauseCommand ??= new RelayCommand(PauseReplay);
    /// <summary>Replays forward until a frame matches the break condition (the search box text, same grammar
    /// as the filter), then stops on it. Esc cancels.</summary>
    public AsyncCommand RunToNextMatchCommand => _runToNextMatchCommand ??= new AsyncCommand(() => RunToMatchAsync(1));
    public AsyncCommand RunToPreviousMatchCommand => _runToPreviousMatchCommand ??= new AsyncCommand(() => RunToMatchAsync(-1));
    public AsyncCommand ExportVisibleCommand => _exportVisibleCommand ??= new AsyncCommand(ExportVisibleAsync);
    public RelayCommand HeadCommand => _headCommand ??= new RelayCommand(() => MoveToFrame(0));
    public RelayCommand PreviousCommand => _previousCommand ??= new RelayCommand(() => MoveByWindow(-1));
    public RelayCommand NextCommand => _nextCommand ??= new RelayCommand(() => MoveByWindow(1));
    public RelayCommand TailCommand => _tailCommand ??= new RelayCommand(() => MoveToFrame(_frames.Count - 1));
    public RelayCommand TogglePlaybackCommand => _togglePlaybackCommand ??= new RelayCommand(TogglePlayback);
    public RelayCommand ToggleMarkerCommand => _toggleMarkerCommand ??= new RelayCommand(ToggleMarker);
    public RelayCommand PreviousMarkerCommand => _previousMarkerCommand ??= new RelayCommand(() => MoveToMarker(-1));
    public RelayCommand NextMarkerCommand => _nextMarkerCommand ??= new RelayCommand(() => MoveToMarker(1));
    public RelayCommand ClearMarkersCommand => _clearMarkersCommand ??= new RelayCommand(() =>
    {
        _markedFrameIndices.Clear();
        ApplyFilter();
        NotifyMarkersChanged();
    });
    public RelayCommand ApplyRuleBookmarksCommand => _applyRuleBookmarksCommand ??= new RelayCommand(ApplyRuleBookmarks);
    public RelayCommand SaveRuleBookmarkPresetCommand => _saveRuleBookmarkPresetCommand ??= new RelayCommand(SaveCurrentRuleBookmarkPreset);
    public RelayCommand DeleteRuleBookmarkPresetCommand => _deleteRuleBookmarkPresetCommand ??= new RelayCommand(DeleteSelectedRuleBookmarkPreset);

    /// <summary>
    /// Re-applies the selected saved set even when it was already selected (WPF's ComboBox does
    /// not raise a selection-changed notification for re-picking the same item, so editing the
    /// rows then trying to "reload" by re-choosing the same set from the dropdown would otherwise
    /// silently do nothing).
    /// </summary>
    public RelayCommand ReloadRuleBookmarkPresetCommand => _reloadRuleBookmarkPresetCommand ??= new RelayCommand(() =>
    {
        if (SelectedRuleBookmark is not { } preset)
        {
            FindStatus = "select a rule set first";
            return;
        }

        RuleSetName = preset.Name;
        RuleText = preset.Rules;
        FindStatus = $"rule set reloaded: {preset.Name}";
    });
    public RelayCommand EnableAllRulesCommand => _enableAllRulesCommand ??= new RelayCommand(() => SetAllRuleLinesEnabled(true));
    public RelayCommand DisableAllRulesCommand => _disableAllRulesCommand ??= new RelayCommand(() => SetAllRuleLinesEnabled(false));
    public RelayCommand FilterBySelectedIdCommand => _filterBySelectedIdCommand ??= new RelayCommand(() =>
    {
        if (SelectedRow is null)
        {
            return;
        }

        SearchText = SelectedRow.IdentifierHex.TrimEnd('x');
        _appliedFilter = SearchText;
        QuickLensLabel = "Selected CAN identifier";
        ApplyFilter();
        OnPropertyChanged(nameof(IsFilterActive));
    });

    /// <summary>Looks up a DID name in the loaded CDD/ODX catalogs for the UDS column; set by the
    /// shell whenever diagnostic data is (re)loaded so trace rows can label diagnostic frames.</summary>
    private Func<int, string?>? _didNameResolver;

    /// <summary>Bus/ID pairs learned to carry diagnostics: standard ranges, plus any identifier
    /// where at least one frame is unambiguous UDS (or reads a DID from the loaded catalog).
    /// Continuation/flow-control frames on these IDs then decode too, whatever the ID range.</summary>
    private HashSet<(int Channel, uint Id, bool Extended)> _diagnosticIds = [];

    public void SetDidNameResolver(Func<int, string?>? resolver)
    {
        _didNameResolver = resolver;
        RebuildDiagnosticIds();
        // DID labels/UDS discovery can affect existing text filters. Their
        // cached results must be invalidated, not reused across catalog reloads.
        _filterContextVersion++;
        // Rebuild so already-materialised (cached) rows pick up the new names.
        ApplyFilter();
    }

    private void RebuildDiagnosticIds()
    {
        var set = new HashSet<(int Channel, uint Id, bool Extended)>();
        Span<byte> payloadBuffer = stackalloc byte[64];
        foreach (var frame in _frames)
        {
            var key = (frame.Channel, frame.Identifier, frame.IsExtended);
            if (set.Contains(key))
            {
                continue;
            }

            if (frame.IsError || frame.Flags.HasFlag(CanFrameFlags.Remote))
                continue;
            if (UdsFrameInterpreter.LooksLikeDiagnosticId(frame.Identifier, frame.IsExtended))
            {
                set.Add(key);
                continue;
            }

            frame.CopyPayloadTo(payloadBuffer[..frame.DataLength]);
            if (UdsFrameInterpreter.LooksLikeUdsFrame(payloadBuffer[..frame.DataLength], _didNameResolver))
            {
                set.Add(key);
            }
        }

        _diagnosticIds = set;
    }

    private bool IsDiagnosticId(int channel, uint id, bool extended) =>
        _diagnosticIds.Contains((channel, id, extended));

    /// <summary>Verify that source-frame navigation belongs to this
    /// exact loaded trace snapshot. The source can be a memory-mapped
    /// capture and copying it just to compare would be prohibitive.</summary>
    public bool IsSessionFrames(IReadOnlyList<CanFrame> frames) =>
        ReferenceEquals(_frames, frames);

    /// <summary>Non-destructive dataset focus. Only Trace rows are scoped; other
    /// investigations keep the entire merged capture. No file is reimported.</summary>
    public bool HasSourceScope => _sourceScope is not null;
    /// <summary>Physical source number in the active merged frame index;
    /// -1 denotes all sources, not a missing or inferred source.</summary>
    public int FocusedSourceToken => _sourceScopeToken;
    public string SourceScopeLabel => _sourceScope is null ? string.Empty :
        "Trace rows focused on " + _sourceScopeLabel + " (other analysis views remain combined)";

    public void FocusSource(int sourceToken, Func<int, bool> frameIsFromSource, string displayName)
    {
        if (sourceToken < 0) throw new ArgumentOutOfRangeException(nameof(sourceToken));
        ArgumentNullException.ThrowIfNull(frameIsFromSource);
        if (_frames.Count == 0) return;
        _filterCancellation?.Cancel();
        _filterGeneration++;
        IsFiltering = false;
        FilterProgressText = string.Empty;
        _sourceScope = frameIsFromSource;
        _sourceScopeToken = sourceToken;
        _sourceScopeLabel = displayName;
        _cachedFilteredIndices = null;
        _filterContextVersion++;
        SelectedRow = null;
        OnPropertyChanged(nameof(HasSourceScope));
        OnPropertyChanged(nameof(SourceScopeLabel));
        ApplyFilter();
    }

    public void ClearSourceScope()
    {
        if (_sourceScope is null) return;
        _filterCancellation?.Cancel();
        _filterGeneration++;
        IsFiltering = false;
        FilterProgressText = string.Empty;
        _sourceScope = null;
        _sourceScopeToken = -1;
        _sourceScopeLabel = string.Empty;
        _cachedFilteredIndices = null;
        _filterContextVersion++;
        SelectedRow = null;
        OnPropertyChanged(nameof(HasSourceScope));
        OnPropertyChanged(nameof(SourceScopeLabel));
        ApplyFilter();
    }

    /// <summary>Apply a saved notebook tab as one filter operation, including its
    /// physical source. Avoid running three expensive full-trace filters while
    /// restoring query, view mode and clock display separately.</summary>
    public void ApplyNotebookView(string query, int viewModeIndex, int timeModeIndex,
        int sourceToken = -1, Func<int, bool>? sourcePredicate = null,
        string sourceLabel = "")
    {
        if (sourceToken >= 0 && sourcePredicate is null)
            throw new ArgumentException("A physical source token needs a predicate.", nameof(sourcePredicate));
        _filterCancellation?.Cancel();
        _filterGeneration++;
        IsFiltering = false;
        FilterProgressText = string.Empty;
        _sourceScope = sourceToken >= 0 ? sourcePredicate : null;
        _sourceScopeToken = sourceToken >= 0 ? sourceToken : -1;
        _sourceScopeLabel = _sourceScope is null ? string.Empty : sourceLabel;
        SearchText = query ?? string.Empty;
        _appliedFilter = SearchText.Trim();
        _viewModeIndex = Math.Clamp(viewModeIndex, 0, 2);
        var clampedTime = Math.Clamp(timeModeIndex, 0, TimeDisplayModeChoices.Count - 1);
        if (_timeDisplayModeIndex != clampedTime)
        {
            _timeDisplayModeIndex = clampedTime;
            AppSettingsStore.SaveTraceTimeDisplayModeIndex(clampedTime);
        }
        _cachedFilteredIndices = null;
        _filterContextVersion++;
        QuickLensLabel = _appliedFilter.Length == 0 ? "All frames" : "Saved analysis tab";
        SelectedRow = null;
        OnPropertyChanged(nameof(HasSourceScope));
        OnPropertyChanged(nameof(SourceScopeLabel));
        OnPropertyChanged(nameof(ViewModeIndex));
        OnPropertyChanged(nameof(TimeDisplayModeIndex));
        OnPropertyChanged(nameof(IsFilterActive));
        ApplyFilter();
    }

    public void SetSession(IReadOnlyList<CanFrame> frames, IReadOnlyList<DbcDecoder> decoders,
        Func<int, string?>? sourceFile = null, Func<int, int>? sourceAt = null,
        Func<int, string?>? sourceEvidence = null)
    {
        _sourceFile = sourceFile;
        _sourceEvidence = sourceEvidence;
        if (ReferenceEquals(_frames, frames))
        {
            // DBC/ARXML options or definitions changed, not the recording.
            // Do not rescan millions of frames for previous-same-ID indices,
            // recreate the changed-bit mask, or discard investigation markers.
            var selectedFrameIndex = SelectedRow?.FrameIndex ?? -1;
            _decoders = decoders;
            _filterContextVersion++; // name/unmatched filters depend on active DBCs
            ApplyFilter();
            if (selectedFrameIndex >= 0)
            {
                var position = Rows.PositionOfFrameIndex(selectedFrameIndex);
                SelectedRow = position >= 0 ? Rows[position] : null;
            }
            return;
        }

        // A filter still being built belongs to the previous recording: cancel and discard it.
        _filterCancellation?.Cancel();
        _filterGeneration++;
        IsFiltering = false;
        FilterProgressText = string.Empty;
        SelectedSourceByteEvidenceText = string.Empty;
        _selectedSourceFrameIndex = -1;
        _selectedSourceByteCount = 0;
        _frames = frames;
        _decoders = decoders;
        // A scope references the old combined row-to-file index. It cannot be
        // carried into another import, even when the paths look identical.
        _sourceScope = null;
        _sourceScopeToken = -1;
        _sourceScopeLabel = string.Empty;
        OnPropertyChanged(nameof(HasSourceScope));
        OnPropertyChanged(nameof(SourceScopeLabel));
        _frameSelectionIndex = new TraceFrameSelectionIndex(frames);
        _cachedFilteredIndices = null;
        _cachedFilterQuery = null;
        _filterContextVersion++;
        RebuildDiagnosticIds();
        (_previousSameKeyIndex, _sourcePreviousSameKeyIndex) =
            TraceFrameSelectionIndex.BuildPreviousIndices(frames, sourceAt);
        _changedFrameSetCache = null;
        _sourceChangedFrameSetCache = null;
        _traceEndIndex = frames.Count - 1;
        _timelinePercent = 100;
        _markedFrameIndices.Clear();
        _expandedFrameIndices.Clear();
        _measurementA = -1;
        _measurementB = -1;
        OnPropertyChanged(nameof(MeasurementSummaryText));
        SelectedRow = null;
        ApplyFilter();
        OnPropertyChanged(nameof(TimelinePercent));
        OnPropertyChanged(nameof(NavigationText));
        NotifyMarkersChanged();
    }

    /// <summary>Selects and scrolls to the given frame index (used by matrix/plot cross-navigation).</summary>
    private void ClearSelectedSourceByteEvidence()
    {
        if (_selectedSourceFrameIndex >= 0)
        {
            var oldPosition = Rows.PositionOfFrameIndex(_selectedSourceFrameIndex);
            if (oldPosition >= 0)
                Rows[oldPosition].ClearOriginalSourceBytes();
            _selectedSourceFrameIndex = -1;
            _selectedSourceByteCount = 0;
        }
        SelectedSourceByteEvidenceText = string.Empty;
    }

    public void JumpToFrameIndex(int frameIndex)
    {
        // Any ordinary jump clears the previous DID-specific overlay.
        ClearSelectedSourceByteEvidence();
        if (frameIndex < 0 || frameIndex >= _frames.Count)
        {
            FindStatus = "The requested trace frame is no longer available.";
            return;
        }

        if (Rows.PositionOfFrameIndex(frameIndex) < 0)
        {
            // A current lens or replay cursor can hide the source. Show the
            // raw frame instead of landing on an unrelated visible row.
            SearchText = string.Empty;
            _appliedFilter = string.Empty;
            _viewModeIndex = 0;
            QuickLensLabel = "All frames";
            OnPropertyChanged(nameof(ViewModeIndex));
            OnPropertyChanged(nameof(IsFilterActive));
            // A link from UDS/DBC/plot may target a different physical file.
            // Reveal its original frame rather than landing on the nearest
            // unrelated frame in the currently focused source.
            if (_sourceScope is not null)
            {
                _filterCancellation?.Cancel();
                _filterGeneration++;
                _sourceScope = null;
                _sourceScopeToken = -1;
                _sourceScopeLabel = string.Empty;
                _cachedFilteredIndices = null;
                _filterContextVersion++;
                OnPropertyChanged(nameof(HasSourceScope));
                OnPropertyChanged(nameof(SourceScopeLabel));
            }
        }

        // Navigation must move both the selection and the timeline cursor.
        // Otherwise a Go To Time / UDS source link selects an old frame while
        // the scrubber and status bar still falsely say "end of trace".
        MoveToFrame(frameIndex);
        var position = Rows.PositionOfFrameIndex(frameIndex);
        if (position >= 0 && position < Rows.Count)
        {
            var row = Rows[position];
            SelectedRow = row;
            BringRowIntoViewRequested?.Invoke(this, row);
        }
    }

    /// <summary>Reveal the exact original byte interval represented by
    /// a source-proven CDD/ODX subparameter. Never infer missing bytes or
    /// silently select a different frame if the trace has been replaced.</summary>
    public bool JumpToSourceByteSpan(DiagnosticSourceByteSpan source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.FrameIndex < 0 || source.FrameIndex >= _frames.Count ||
            source.CanPayloadByteOffset < 0 || source.ByteCount <= 0 ||
            (long)source.CanPayloadByteOffset + source.ByteCount >
                _frames[source.FrameIndex].DataLength)
        {
            FindStatus = "The original DID source byte range is unavailable in this recording.";
            SelectedSourceByteEvidenceText = string.Empty;
            return false;
        }

        JumpToFrameIndex(source.FrameIndex);
        if (SelectedRow is not { } row ||
            row.FrameIndex != source.FrameIndex)
        {
            FindStatus = "The original DID source frame could not be selected.";
            return false;
        }
        _selectedSourceFrameIndex = source.FrameIndex;
        _selectedSourceByteOffset = source.CanPayloadByteOffset;
        _selectedSourceByteCount = source.ByteCount;
        if (!row.HighlightOriginalSourceBytes(source.CanPayloadByteOffset, source.ByteCount))
        {
            _selectedSourceFrameIndex = -1;
            _selectedSourceByteCount = 0;
            FindStatus = "The original DID source bytes could not be highlighted.";
            return false;
        }

        var last = source.CanPayloadByteOffset + source.ByteCount - 1;
        SelectedSourceByteEvidenceText = last == source.CanPayloadByteOffset
            ? $"Verified DID source · frame #{source.FrameIndex + 1:N0} · byte B{last}"
            : $"Verified DID source · frame #{source.FrameIndex + 1:N0} · bytes B{source.CanPayloadByteOffset}..B{last}";
        BringRowIntoViewRequested?.Invoke(this, row);
        return true;
    }

    public void JumpToTimestamp(long timestampNanoseconds)
    {
        if (_frames.Count == 0)
        {
            return;
        }

        var low = 0;
        var high = _frames.Count - 1;
        while (low < high)
        {
            var mid = (low + high) / 2;
            if (_frames[mid].TimestampNanoseconds < timestampNanoseconds)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }

        JumpToFrameIndex(low);
    }

    private static Func<int, bool>? CombineEligibility(
        Func<int, bool>? source, BitArray? changed)
    {
        if (source is null) return changed is null ? null : changed.Get;
        return changed is null ? source : index => source(index) && changed.Get(index);
    }

    private void ApplyFilter()
    {
        var filter = _appliedFilter;
        var end = _frames.Count == 0 ? -1 : Math.Clamp(_traceEndIndex, 0, _frames.Count - 1);

        if (filter.Length == 0 && !OverwriteRows && !ShowChangedOnly && _sourceScope is null)
        {
            // Unfiltered replay keeps zero index arrays, exactly as before.
            Rows = NewRows(null, end + 1);
        }
        else if (OverwriteRows)
        {
            // Incremental per-physical-ID latest index: forward playback
            // considers only new frames, not all preceding frames per tick.
            var indices = _frameSelectionIndex.LatestIndicesThrough(end, _sourceScope, _sourceScopeToken);
            if (filter.Length > 0)
            {
                var matcher = BuildMatcher(filter);
                indices = indices.Where(index => matcher(_frames[index])).ToArray();
            }
            Rows = NewRows(indices);
        }
        else
        {
            // One full-trace matching array is shared across replay ticks.
            // Rebuilding on every cursor movement used O(total frames) per
            // tick and allocated a new int[] for every replay update.
            if (_cachedFilteredIndices is null ||
                _cachedFilterQuery != filter ||
                _cachedChangedOnly != ShowChangedOnly ||
                _cachedFilterContextVersion != _filterContextVersion)
            {
                if (_frames.Count >= BackgroundFilterThreshold)
                {
                    StartBackgroundFilter(filter);
                    return;
                }

                var matches = filter.Length == 0 ? null : BuildMatcher(filter);
                var eligible = ShowChangedOnly ? ChangedFrameSet : null;
                _cachedFilteredIndices = _frameSelectionIndex.BuildMatchingIndex(
                    matches, CombineEligibility(_sourceScope, eligible));
                _cachedFilterQuery = filter;
                _cachedChangedOnly = ShowChangedOnly;
                _cachedFilterContextVersion = _filterContextVersion;
            }

            var visible = TraceFrameSelectionIndex.VisiblePrefixLength(
                _cachedFilteredIndices, end);
            Rows = NewRows(_cachedFilteredIndices, visible);
        }

        OnPropertyChanged(nameof(RowCountText));
        OnPropertyChanged(nameof(NavigationText));
    }

    /// <summary>Traces with at least this many frames build a first-time filter result on a
    /// worker thread with progress and cancellation; smaller traces stay synchronous so their
    /// behaviour is unchanged. Adjustable so tests can exercise the background path.</summary>
    public int BackgroundFilterThreshold { get; set; } = 250_000;

    private CancellationTokenSource? _filterCancellation;
    private int _filterGeneration;
    private bool _isFiltering;
    private string _filterProgressText = string.Empty;

    /// <summary>Completes when the most recently started filter build has finished,
    /// been cancelled or been superseded. Already completed when no build is running.</summary>
    public Task FilterCompletion { get; private set; } = Task.CompletedTask;

    public bool IsFiltering
    {
        get => _isFiltering;
        private set => SetProperty(ref _isFiltering, value);
    }

    public string FilterProgressText
    {
        get => _filterProgressText;
        private set => SetProperty(ref _filterProgressText, value);
    }

    public RelayCommand CancelFilterCommand => _cancelFilterCommand ??= new RelayCommand(() =>
        _filterCancellation?.Cancel());
    private RelayCommand? _cancelFilterCommand;

    /// <summary>Builds the full-trace filter result off the UI thread. The previous rows stay
    /// on screen (never a partial or fabricated result) until the new, complete result
    /// is ready. A newer request or a new trace supersedes and cancels this one.</summary>
    private void StartBackgroundFilter(string filter)
    {
        _filterCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _filterCancellation = cancellation;
        var generation = ++_filterGeneration;
        var token = cancellation.Token;

        // Matchers capture the decoder list and resolvers by reference; build them here, on the
        // thread that owns that state, then hand only immutable data to the worker.
        var matcher = filter.Length == 0 ? null : BuildMatcher(filter);
        var changedOnly = ShowChangedOnly;
        var sourceScope = _sourceScope;
        var changedCache = sourceScope is null ? _changedFrameSetCache : _sourceChangedFrameSetCache;
        var contextVersion = _filterContextVersion;
        var frames = _frames;
        var previousSameKey = sourceScope is null ? _previousSameKeyIndex : _sourcePreviousSameKeyIndex;
        var selectionIndex = _frameSelectionIndex;
        var context = SynchronizationContext.Current;

        IsFiltering = true;
        FilterProgressText = $"Filtering {frames.Count:N0} frames…";

        void Report(double fraction)
        {
            var text = $"Filtering {frames.Count:N0} frames… {fraction:P0}";
            if (context is null) FilterProgressText = text;
            else context.Post(_ =>
            {
                if (generation == _filterGeneration) FilterProgressText = text;
            }, null);
        }

        FilterCompletion = Task.Run(() =>
        {
            BitArray? eligible = null;
            if (changedOnly)
            {
                eligible = changedCache ?? ComputeChangedFrameSet(frames, previousSameKey, token);
            }

            var indices = selectionIndex.BuildMatchingIndex(
                matcher, CombineEligibility(sourceScope, eligible), token, Report);
            return (indices, eligible);
        }, token).ContinueWith(task =>
        {
            void Complete()
            {
                if (generation != _filterGeneration) return; // superseded by a newer request
                IsFiltering = false;
                FilterProgressText = string.Empty;
                if (task.IsCanceled)
                {
                    // User cancelled: keep the rows already shown and say so explicitly.
                    FindStatus = "Filter cancelled — the previous view is unchanged.";
                    return;
                }
                if (task.IsFaulted)
                {
                    FindStatus = "Filter failed: " + (task.Exception?.GetBaseException().Message ?? "unknown error");
                    return;
                }

                var (indices, eligible) = task.Result;
                if (eligible is not null && ReferenceEquals(_frames, frames))
                {
                    if (sourceScope is null) _changedFrameSetCache ??= eligible;
                    else _sourceChangedFrameSetCache ??= eligible;
                }
                _cachedFilteredIndices = indices;
                _cachedFilterQuery = filter;
                _cachedChangedOnly = changedOnly;
                _cachedFilterContextVersion = contextVersion;
                var end = _frames.Count == 0 ? -1 : Math.Clamp(_traceEndIndex, 0, _frames.Count - 1);
                Rows = NewRows(indices, TraceFrameSelectionIndex.VisiblePrefixLength(indices, end));
                OnPropertyChanged(nameof(RowCountText));
                OnPropertyChanged(nameof(NavigationText));
            }

            if (context is null) Complete();
            else context.Post(_ => Complete(), null);
        }, TaskScheduler.Default);
    }

    private (int Offset, int Count)? SourceByteSelectionForFrame(int frameIndex) =>
        frameIndex == _selectedSourceFrameIndex && _selectedSourceByteCount > 0
            ? (_selectedSourceByteOffset, _selectedSourceByteCount)
            : null;

    private VirtualRowList NewRows(int[]? indices, int? visibleCount = null) =>
        new(_frames, _decoders,
            _sourceScope is null ? _previousSameKeyIndex : _sourcePreviousSameKeyIndex, indices,
            MarkerCategoryAt,
            _expandedFrameIndices.Contains,
            SetRowExpanded,
            visibleCount,
            _didNameResolver,
            IsDiagnosticId,
            TimeDisplayMode,
            TraceStartTimestampNanoseconds,
            SourceByteSelectionForFrame,
            RowColourAt,
            _sourceFile,
            _sourceEvidence);

    private IReadOnlyList<RowColourRule> _rowColourRules = [];
    private Func<CanFrame, string?>? _rowColourFunc;
    private (int Context, int Rules) _rowColourFuncVersion = (-1, -1);
    private int _rowColourRulesVersion;

    /// <summary>Saved queries that have a colour paint matching rows (first match wins). Rows are
    /// re-created so the new tints show immediately.</summary>
    public void SetRowColourRules(IEnumerable<SavedQuery> savedQueries)
    {
        _rowColourRules = ToColourRules(savedQueries);
        _rowColourRulesVersion++;
        _rowColourFunc = null;
        if (_frames.Count > 0) ApplyFilter();
    }

    private static RowColourRule[] ToColourRules(IEnumerable<SavedQuery> savedQueries) => savedQueries
        .Where(item => RowColourRules.IsValidColour(item.Colour) && !string.IsNullOrWhiteSpace(item.Query))
        .Select(item => new RowColourRule(item.Query, item.Colour!))
        .ToArray();

    private string? RowColourAt(int frameIndex)
    {
        if (_rowColourRules.Count == 0 || frameIndex < 0 || frameIndex >= _frames.Count) return null;
        var version = (_filterContextVersion, _rowColourRulesVersion);
        if (_rowColourFunc is null || _rowColourFuncVersion != version)
        {
            _rowColourFunc = RowColourRules.Compile(_rowColourRules, TraceStartTimestampNanoseconds, BuildTermPredicate);
            _rowColourFuncVersion = version;
        }

        return _rowColourFunc(_frames[frameIndex]);
    }

    private void SetRowExpanded(int frameIndex, bool isExpanded)
    {
        if (isExpanded)
        {
            _expandedFrameIndices.Add(frameIndex);
        }
        else
        {
            _expandedFrameIndices.Remove(frameIndex);
        }
    }

    /// <summary>Re-applies markers saved in a project. Frames are matched by exact timestamp; a marker whose
    /// timestamp no longer exists in the loaded trace is skipped, and the number applied is returned.</summary>
    /// <summary>Exact tab-local marker identity: no timestamp-only ambiguity when
    /// multiple CAN frames share the same timestamp.</summary>
    public readonly record struct ExactMarker(int FrameIndex, CanFrame Frame, MarkerCategory Category);

    public IReadOnlyList<ExactMarker> CaptureExactMarkers() =>
        _markedFrameIndices.OrderBy(kv => kv.Key)
            .Select(kv => new ExactMarker(kv.Key, _frames[kv.Key], kv.Value)).ToArray();

    /// <summary>Restore only positions whose entire original frame identity still matches.
    /// Files that changed silently never acquire a marker on an unrelated frame.</summary>
    public int RestoreExactMarkers(IEnumerable<ExactMarker> markers)
    {
        var applied = 0;
        foreach (var marker in markers)
        {
            if ((uint)marker.FrameIndex >= (uint)_frames.Count ||
                !_frames[marker.FrameIndex].Equals(marker.Frame)) continue;
            _markedFrameIndices[marker.FrameIndex] = marker.Category;
            applied++;
        }
        if (applied > 0)
        {
            ApplyFilter();
            NotifyMarkersChanged();
        }
        return applied;
    }

    public int RestoreMarkers(IEnumerable<(long TimestampNanoseconds, MarkerCategory Category)> markers)
    {
        var applied = 0;
        foreach (var (timestamp, category) in markers)
        {
            int lo = 0, hi = _frames.Count;
            while (lo < hi)
            {
                var mid = (lo + hi) / 2;
                if (_frames[mid].TimestampNanoseconds < timestamp) lo = mid + 1; else hi = mid;
            }

            if (lo >= _frames.Count || _frames[lo].TimestampNanoseconds != timestamp)
                continue;
            _markedFrameIndices[lo] = category;
            applied++;
        }

        if (applied > 0)
        {
            ApplyFilter();
            NotifyMarkersChanged();
        }

        return applied;
    }

    /// <summary>Compares the marked frames (M / context menu) plus the selected row, in trace order, up to
    /// <see cref="FrameDifference.MaxFrames"/>. Returns null (with a status message) when fewer than two.</summary>
    public FrameDifferenceResult? BuildFrameDifference()
    {
        var indices = _markedFrameIndices.Keys.ToList();
        if (SelectedRow is { } selected && !indices.Contains(selected.FrameIndex)) indices.Add(selected.FrameIndex);
        indices.Sort();
        if (indices.Count < 2)
        {
            FindStatus = "mark at least two frames (M) or mark one and select another, then compare";
            return null;
        }

        if (indices.Count > FrameDifference.MaxFrames)
        {
            FindStatus = $"comparing the first {FrameDifference.MaxFrames} of {indices.Count} marked frames";
        }

        var decoders = _decoders;
        IReadOnlyList<SignalValue> Decode(CanFrame frame)
        {
            foreach (var decoder in decoders)
                if (decoder.TryGetMessage(frame, out _)) return decoder.Decode(frame);
            return [];
        }

        return FrameDifference.Compute(
            indices.Select(index => (index, _frames[index])).ToArray(), Decode, TraceStartTimestampNanoseconds);
    }

    private void ToggleMarker()
    {
        if (SelectedRow is not { } row)
        {
            return;
        }

        if (!_markedFrameIndices.Remove(row.FrameIndex))
        {
            _markedFrameIndices[row.FrameIndex] = SelectedMarkerCategory;
        }

        ApplyFilter();
        NotifyMarkersChanged();
        JumpToFrameIndex(row.FrameIndex);
    }

    private void ApplyRuleBookmarks()
    {
        if (_frames.Count == 0)
        {
            FindStatus = "load a trace first";
            return;
        }

        var rule = RuleText.Trim();
        if (rule.Length == 0)
        {
            FindStatus = "enter a rule";
            return;
        }

        var predicates = new List<Func<CanFrame, bool>>();
        foreach (var item in SplitEnabledRules(rule))
        {
            if (!TryBuildRule(item, out var predicate, out var error))
            {
                FindStatus = error;
                return;
            }

            predicates.Add(predicate);
        }

        if (predicates.Count == 0)
        {
            FindStatus = "enter a rule";
            return;
        }

        var added = 0;
        for (var index = 0; index < _frames.Count; index++)
        {
            if (predicates.Any(predicate => predicate(_frames[index])) && _markedFrameIndices.TryAdd(index, SelectedMarkerCategory))
            {
                added++;
            }
        }

        ApplyFilter();
        if (added > 0)
        {
            NotifyMarkersChanged();
        }
        FindStatus = added == 0 ? "rule set matched no new frames" : $"rule set marked {added:N0} frame(s)";
        if (added > 0)
        {
            MoveToMarker(1);
        }
    }

    private void SaveCurrentRuleBookmarkPreset()
    {
        var rules = RuleText.Trim();
        if (rules.Length == 0)
        {
            FindStatus = "enter rules before saving";
            return;
        }

        var name = string.IsNullOrWhiteSpace(RuleSetName)
            ? rules.Split('\n', ';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "Rule set"
            : RuleSetName.Trim();
        var existing = RuleBookmarkPresets.FirstOrDefault(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            var index = RuleBookmarkPresets.IndexOf(existing);
            RuleBookmarkPresets[index] = new RuleBookmarkPreset(name, rules);
            SelectedRuleBookmark = RuleBookmarkPresets[index];
        }
        else
        {
            var preset = new RuleBookmarkPreset(name, rules);
            RuleBookmarkPresets.Add(preset);
            SelectedRuleBookmark = preset;
        }

        SaveRuleBookmarkPresets();
        FindStatus = $"rule set saved: {name}";
    }

    private void DeleteSelectedRuleBookmarkPreset()
    {
        if (SelectedRuleBookmark is null)
        {
            FindStatus = "select a rule set first";
            return;
        }

        var name = SelectedRuleBookmark.Name;
        RuleBookmarkPresets.Remove(SelectedRuleBookmark);
        SelectedRuleBookmark = null;
        RuleSetName = string.Empty;
        SaveRuleBookmarkPresets();
        FindStatus = $"rule set deleted: {name}";
    }

    private void SetAllRuleLinesEnabled(bool isEnabled)
    {
        if (RuleLines.All(line => string.IsNullOrWhiteSpace(line.Text)))
        {
            FindStatus = "enter rules first";
            return;
        }

        foreach (var line in RuleLines)
        {
            line.IsEnabled = isEnabled;
        }

        FindStatus = isEnabled ? "all rules enabled" : "all rules disabled";
    }

    private static IEnumerable<string> SplitEnabledRules(string rules)
    {
        foreach (var line in SplitRuleLines(rules))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("#", StringComparison.Ordinal))
            {
                continue;
            }

            if (trimmed.StartsWith("[ ]", StringComparison.Ordinal) ||
                trimmed.StartsWith("[off]", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("disabled:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            yield return StripRuleToggle(trimmed);
        }
    }

    private static IEnumerable<string> SplitRuleLines(string rules) =>
        rules.Split(['\n', '\r', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string StripRuleToggle(string rule)
    {
        var trimmed = rule.Trim();
        foreach (var prefix in new[] { "[x]", "[on]", "enabled:" })
        {
            if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return trimmed[prefix.Length..].Trim();
            }
        }

        if (trimmed.StartsWith("[ ]", StringComparison.Ordinal))
        {
            return trimmed[3..].Trim();
        }

        if (trimmed.StartsWith("[off]", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed[5..].Trim();
        }

        if (trimmed.StartsWith("disabled:", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed[9..].Trim();
        }

        return trimmed;
    }

    private static string RuleBookmarkPresetPath => Path.Combine(AppStorage.ConfigDirectory, "rule-bookmarks.json");

    private void LoadRuleBookmarkPresets()
    {
        try
        {
            if (!File.Exists(RuleBookmarkPresetPath))
            {
                return;
            }

            var presets = JsonSerializer.Deserialize<RuleBookmarkPreset[]>(File.ReadAllText(RuleBookmarkPresetPath));
            if (presets is null)
            {
                return;
            }

            foreach (var preset in presets.Where(item => !string.IsNullOrWhiteSpace(item.Name) && !string.IsNullOrWhiteSpace(item.Rules)))
            {
                RuleBookmarkPresets.Add(preset);
            }
        }
        catch
        {
            // Rule presets are convenience-only; malformed local config must not block trace analysis.
        }
    }

    private void SaveRuleBookmarkPresets()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(RuleBookmarkPresetPath)!);
            File.WriteAllText(
                RuleBookmarkPresetPath,
                JsonSerializer.Serialize(RuleBookmarkPresets.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase), new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Best effort.
        }
    }

    private bool TryBuildRule(
        string rule,
        out Func<CanFrame, bool> predicate,
        out string error)
    {
        predicate = _ => false;
        error = string.Empty;

        if (rule.StartsWith("id:", StringComparison.OrdinalIgnoreCase))
        {
            var text = rule[3..].Trim().TrimStart('0', 'x', 'X');
            if (!uint.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var id))
            {
                error = "invalid id rule";
                return false;
            }

            predicate = frame => frame.Identifier == id;
            return true;
        }

        if (rule.StartsWith("payload:", StringComparison.OrdinalIgnoreCase))
        {
            var hex = rule[8..].Replace(" ", string.Empty);
            if (hex.Length == 0)
            {
                error = "invalid payload rule";
                return false;
            }

            predicate = frame => PayloadHexContains(frame, hex);
            return true;
        }

        var operators = new[] { ">=", "<=", "==", "!=", ">", "<" };
        var op = operators.FirstOrDefault(rule.Contains);
        if (op is null)
        {
            error = "rule examples: id:18FEF100, payload:62F190, VehicleSpeed > 20";
            return false;
        }

        var parts = rule.Split(op, 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || parts[0].Length == 0 ||
            !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var threshold))
        {
            error = "invalid signal rule";
            return false;
        }

        var signalTerm = parts[0];
        predicate = frame =>
        {
            foreach (var decoder in _decoders)
            {
                foreach (var value in decoder.Decode(frame))
                {
                    if (!SignalMatches(value, signalTerm))
                    {
                        continue;
                    }

                    if (Compare(value.PhysicalValue, threshold, op))
                    {
                        return true;
                    }
                }
            }

            return false;
        };
        return true;
    }

    private static bool SignalMatches(SignalValue value, string term) =>
        value.Signal.Equals(term, StringComparison.OrdinalIgnoreCase) ||
        $"{value.Message}.{value.Signal}".Equals(term, StringComparison.OrdinalIgnoreCase);

    private static bool Compare(double value, double threshold, string op) =>
        op switch
        {
            ">=" => value >= threshold,
            "<=" => value <= threshold,
            "==" => Math.Abs(value - threshold) <= 1e-9,
            "!=" => Math.Abs(value - threshold) > 1e-9,
            ">" => value > threshold,
            "<" => value < threshold,
            _ => false
        };

    private void MoveToMarker(int direction)
    {
        if (_markedFrameIndices.Count == 0)
        {
            FindStatus = "no markers";
            return;
        }

        var current = SelectedRow?.FrameIndex ?? _traceEndIndex;
        var ordered = _markedFrameIndices.Keys.OrderBy(index => index).ToArray();
        var target = direction > 0
            ? ordered.FirstOrDefault(index => index > current, ordered[0])
            : ordered.LastOrDefault(index => index < current, ordered[^1]);
        JumpToFrameIndex(target);
        FindStatus = $"marker {Array.IndexOf(ordered, target) + 1:N0} / {ordered.Length:N0}";
    }

    /// <summary>Bit-set rather than HashSet of every changed frame: for a
    /// million-frame trace it needs ~125 KiB instead of millions of HashSet
    /// entries, and querying a source frame's status is constant time.</summary>
    private BitArray ChangedFrameSet
    {
        get
        {
            if (_sourceScope is not null)
                return _sourceChangedFrameSetCache ??=
                    ComputeChangedFrameSet(_frames, _sourcePreviousSameKeyIndex, CancellationToken.None);
            return _changedFrameSetCache ??=
                ComputeChangedFrameSet(_frames, _previousSameKeyIndex, CancellationToken.None);
        }
    }

    private static BitArray ComputeChangedFrameSet(
        IReadOnlyList<CanFrame> frames, int[] previousSameKeyIndex, CancellationToken token)
    {
        var changed = new BitArray(frames.Count);
        for (var index = 0; index < frames.Count; index++)
        {
            if ((index & 0x3FFF) == 0) token.ThrowIfCancellationRequested();
            var previousIndex = previousSameKeyIndex[index];
            if (previousIndex >= 0 && !PayloadEquals(frames[previousIndex], frames[index]))
                changed[index] = true;
        }

        return changed;
    }

    /// <summary>
    /// Filter syntax: space-separated terms are ANDed. A term matches the ID (hex), the channel
    /// ("ch1"), the message name, or the payload ("payload:AABB" / bare hex pairs). OR / "|" separates
    /// alternatives; id:100..1FF, d0&0F=05, dlc>=8 and t:1..2 are ID, byte, length and time terms. A leading "-"
    /// turns any term into a stop filter that excludes matches instead of requiring them
    /// ("-payload:AA" keeps every frame whose payload does NOT contain AA; "-ch2" drops channel 2
    /// entirely), so one box can both narrow down and carve out noise at the same time.
    /// </summary>
    private Func<CanFrame, bool> BuildMatcher(string filter)
    {
        // OR groups, "a|b" alternatives, "-" negation and the advanced ID/byte/mask/time terms are
        // compiled by the pure FrameQuery grammar; everything else goes to BuildTermPredicate.
        var origin = _frames.Count > 0 ? _frames[0].TimestampNanoseconds : 0;
        return FrameQuery.Compile(filter, origin, BuildTermPredicate);
    }

    private Func<CanFrame, bool> BuildTermPredicate(string term)
    {
        if (term.StartsWith("ch", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(term[2..], out var channel))
        {
            return frame => frame.Channel == channel - 1;
        }

        // Typed bus/transport facets are evaluated without rendering hex payloads or
        // running any DBC decoder, so these quick filters stay fast on large captures.
        if (term.Equals("error", StringComparison.OrdinalIgnoreCase) ||
            term.Equals("errors", StringComparison.OrdinalIgnoreCase))
            return frame => frame.IsError;
        if (term.Equals("fd", StringComparison.OrdinalIgnoreCase))
            return frame => frame.Flags.HasFlag(CanFrameFlags.FlexibleDataRate) || frame.DataLength > 8;
        if (term.Equals("remote", StringComparison.OrdinalIgnoreCase) ||
            term.Equals("rtr", StringComparison.OrdinalIgnoreCase))
            return frame => frame.Flags.HasFlag(CanFrameFlags.Remote);
        if (term.Equals("extended", StringComparison.OrdinalIgnoreCase))
            return frame => frame.IsExtended;
        if (term.Equals("standard", StringComparison.OrdinalIgnoreCase))
            return frame => !frame.IsExtended;

        if (term.Equals("unmatched", StringComparison.OrdinalIgnoreCase))
        {
            var decoders = _decoders;
            var matchedByBusId = new Dictionary<(int Channel, uint Id, bool Extended), bool>();
            return frame =>
            {
                if (decoders.Count == 0) return false;
                var key = (frame.Channel, frame.Identifier, frame.IsExtended);
                if (!matchedByBusId.TryGetValue(key, out var matched))
                {
                    matched = decoders.Any(decoder => decoder.TryGetMessage(frame, out _));
                    matchedByBusId[key] = matched;
                }
                return !matched;
            };
        }

        if (term.StartsWith("payload:", StringComparison.OrdinalIgnoreCase))
        {
            var hex = term[8..].Replace(" ", string.Empty);
            return frame => PayloadHexContains(frame, hex);
        }

        // "uds" keeps only frames with a UDS/ISO-TP reading; "uds:vin" (or uds:22f190,
        // uds:neg…) additionally requires that text in the decoded description.
        if (term.Equals("uds", StringComparison.OrdinalIgnoreCase) ||
            term.StartsWith("uds:", StringComparison.OrdinalIgnoreCase))
        {
            var needle = term.Length > 4 ? term[4..] : string.Empty;
            var resolver = _didNameResolver;
            return frame =>
            {
                Span<byte> payloadBuffer = stackalloc byte[64];
                frame.CopyPayloadTo(payloadBuffer[..frame.DataLength]);
                var text = UdsFrameInterpreter.Describe(
                    payloadBuffer[..frame.DataLength], frame.Identifier, frame.IsExtended, resolver,
                    assumeDiagnostic: IsDiagnosticId(frame.Channel, frame.Identifier, frame.IsExtended));
                return text is not null &&
                       (needle.Length == 0 || text.Contains(needle, StringComparison.OrdinalIgnoreCase));
            };
        }

        if (uint.TryParse(term.TrimStart('0', 'x', 'X'), NumberStyles.HexNumber,
                CultureInfo.InvariantCulture, out var id) ||
            uint.TryParse(term, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out id))
        {
            var captured = id;
            var byName = MessageNameMatcher(term);
            return frame => frame.Identifier == captured || byName(frame);
        }

        return MessageNameMatcher(term);
    }

    private static readonly char[] HexDigits = "0123456789ABCDEF".ToCharArray();

    /// <summary>True when this frame's payload, formatted as an unseparated uppercase hex string,
    /// contains <paramref name="hexNeedle"/> as a substring — the same nibble-level substring
    /// semantics `Convert.ToHexString(frame.GetPayload()).Contains(...)` had, but without
    /// allocating a byte[] and a string per frame. That allocation was the dominant cost of a
    /// payload filter/rule over a multi-million-frame trace.</summary>
    private static bool PayloadHexContains(in CanFrame frame, string hexNeedle)
    {
        Span<byte> payload = stackalloc byte[64];
        frame.CopyPayloadTo(payload[..frame.DataLength]);
        Span<char> hex = stackalloc char[128];
        for (var i = 0; i < frame.DataLength; i++)
        {
            var value = payload[i];
            hex[i * 2] = HexDigits[value >> 4];
            hex[i * 2 + 1] = HexDigits[value & 0xF];
        }

        return ((ReadOnlySpan<char>)hex[..(frame.DataLength * 2)]).Contains(hexNeedle, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Payload equality without allocating a byte[] for either frame.</summary>
    private static bool PayloadEquals(in CanFrame a, in CanFrame b)
    {
        if (a.DataLength != b.DataLength)
        {
            return false;
        }

        Span<byte> bufferA = stackalloc byte[64];
        Span<byte> bufferB = stackalloc byte[64];
        a.CopyPayloadTo(bufferA[..a.DataLength]);
        b.CopyPayloadTo(bufferB[..b.DataLength]);
        return bufferA[..a.DataLength].SequenceEqual(bufferB[..b.DataLength]);
    }

    private Func<CanFrame, bool> MessageNameMatcher(string term)
    {
        // Resolve matching identifiers once so per-frame checks stay cheap. The decoder lookup
        // (not the raw dictionary) keeps J1939 PGN-matched and channel-assigned frames correct.
        var decoders = _decoders;
        var verdictByKey = new Dictionary<(int Channel, uint Id, bool Extended), bool>();
        return frame =>
        {
            var key = (frame.Channel, frame.Identifier, frame.IsExtended);
            if (verdictByKey.TryGetValue(key, out var verdict))
            {
                return verdict;
            }

            verdict = false;
            foreach (var decoder in decoders)
            {
                if (decoder.TryGetMessage(frame, out var message) &&
                    message.Name.Contains(term, StringComparison.OrdinalIgnoreCase))
                {
                    verdict = true;
                    break;
                }
            }

            return verdictByKey[key] = verdict;
        };
    }

    private void Find(int direction)
    {
        var text = SearchText.Trim();
        if (text.Length == 0 || Rows.Count == 0)
        {
            FindStatus = string.Empty;
            return;
        }

        var start = SelectedRow is { } selected
            ? Rows.PositionOfFrameIndex(selected.FrameIndex)
            : direction > 0 ? -1 : Rows.Count;

        for (var step = 1; step <= Rows.Count; step++)
        {
            var position = ((start + direction * step) % Rows.Count + Rows.Count) % Rows.Count;
            var row = Rows[position];
            if (Matches(row, text))
            {
                SelectedRow = row;
                BringRowIntoViewRequested?.Invoke(this, row);
                FindStatus = $"row {position + 1:N0} / {Rows.Count:N0}";
                return;
            }
        }

        FindStatus = "no match";
    }

    private static bool Matches(TraceRowViewModel row, string text) =>
        row.IdentifierHex.Contains(text, StringComparison.OrdinalIgnoreCase) ||
        row.MessageName.Contains(text, StringComparison.OrdinalIgnoreCase) ||
        row.Ecu.Contains(text, StringComparison.OrdinalIgnoreCase) ||
        row.MessageComment.Contains(text, StringComparison.OrdinalIgnoreCase) ||
        row.PayloadHex.Contains(text, StringComparison.OrdinalIgnoreCase) ||
        row.InterpretationText.Contains(text, StringComparison.OrdinalIgnoreCase);

    private void UpdateSelectedRowSignals()
    {
        if (SelectedRow is not { } row)
        {
            SelectedRowSignals = [];
            return;
        }

        SelectedRowSignals = row.DecodeDetailedSignals();
    }

    private async Task ExportVisibleAsync()
    {
        if (Rows.Count == 0)
        {
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "Vector ASCII (*.asc)|*.asc|CSV (*.csv)|*.csv|PCAN trace (*.trc)|*.trc|Vector binary log (*.blf)|*.blf|ASAM MDF4 bus log (*.mf4)|*.mf4",
            DefaultExt = ".asc",
            FileName = $"insight-export-{DateTime.Now:yyyyMMdd-HHmmss}.asc",
            Title = "Export visible frames"
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            var frames = EnumerateVisibleFrames();
            if (dialog.FileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
            {
                await TraceExporter.WriteCsvAsync(dialog.FileName, frames);
            }
            else if (dialog.FileName.EndsWith(".trc", StringComparison.OrdinalIgnoreCase))
            {
                await TraceExporter.WriteTrcAsync(dialog.FileName, frames);
            }
            else if (dialog.FileName.EndsWith(".blf", StringComparison.OrdinalIgnoreCase))
            {
                await TraceExporter.WriteBlfAsync(dialog.FileName, frames);
            }
            else if (dialog.FileName.EndsWith(".mf4", StringComparison.OrdinalIgnoreCase))
            {
                var result = await Mdf4Writer.WriteAsync(dialog.FileName, frames);
                if (result.FramesSkipped > 0)
                    MessageBox.Show(
                        $"{result.FramesWritten:N0} frames written. {result.FramesSkipped:N0} frame(s) cannot be represented in an MDF4 CAN_DataFrame log and were skipped: " +
                        $"{result.RemoteFramesSkipped:N0} remote, {result.ErrorFramesSkipped:N0} error, {result.InvalidLengthFramesSkipped:N0} invalid length, {result.ChannelOutOfRangeFramesSkipped:N0} channel out of range.",
                        "MDF4 export", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                await TraceExporter.WriteAscAsync(dialog.FileName, frames);
            }

            FindStatus = $"exported {Rows.Count:N0} frames";
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception.Message, "Export failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private IEnumerable<CanFrame> EnumerateVisibleFrames()
    {
        for (var position = 0; position < Rows.Count; position++)
        {
            yield return _frames[Rows.FrameIndexAt(position)];
        }
    }

    private void TogglePlayback()
    {
        if (_frames.Count == 0)
        {
            return;
        }

        if (_isPlaying) PauseReplay();
        else StartReplay(animate: false);
    }

    private void StartReplay(bool animate)
    {
        if (_frames.Count == 0)
        {
            return;
        }

        _breakSearch?.Cancel();
        _playTimer.Stop();
        _animate = animate;
        _playTimer.Interval = TimeSpan.FromMilliseconds(animate ? ReplayNavigator.AnimateIntervalMilliseconds : 140);
        _isPlaying = true;
        if (_traceEndIndex >= _frames.Count - 1)
        {
            MoveToFrame(0);
        }

        _playTimer.Start();
        OnPropertyChanged(nameof(PlaybackButtonText));
        OnPropertyChanged(nameof(IsPlaying));
    }

    /// <summary>Esc / break: stops timed replay and cancels a running "run to match" search.</summary>
    private void PauseReplay()
    {
        _breakSearch?.Cancel();
        _breakSearchRunning = false;
        if (!_isPlaying)
        {
            return;
        }

        _isPlaying = false;
        _playTimer.Stop();
        OnPropertyChanged(nameof(PlaybackButtonText));
        OnPropertyChanged(nameof(IsPlaying));
    }

    private void StepFrames(int delta)
    {
        if (_frames.Count == 0)
        {
            return;
        }

        PauseReplay();
        MoveToFrame(ReplayNavigator.Step(_traceEndIndex, delta, _frames.Count));
    }

    private async Task RunToMatchAsync(int direction)
    {
        if (_frames.Count == 0)
        {
            return;
        }

        var text = SearchText.Trim();
        if (text.Length == 0) text = _appliedFilter.Trim();
        if (text.Length == 0)
        {
            FindStatus = "type a break condition in the search box first (e.g. d0=5A, id:100..1FF OR ch2)";
            return;
        }

        PauseReplay();
        _breakSearch?.Cancel();
        var cts = _breakSearch = new CancellationTokenSource();
        var frames = _frames;
        var from = _traceEndIndex;
        var condition = BuildMatcher(text);
        FindStatus = "running to the next match... (Esc stops)";
        _breakSearchRunning = true;
        try
        {
            var index = await Task.Run(() => ReplayNavigator.FindMatch(frames, from, direction, condition, cts.Token));
            if (cts.IsCancellationRequested || !ReferenceEquals(frames, _frames))
            {
                return;
            }

            if (index < 0)
            {
                FindStatus = direction > 0 ? "no further match - reached the end" : "no earlier match - reached the start";
                return;
            }

            MoveToFrame(index);
            FindStatus = $"break at frame {index + 1:N0} / {frames.Count:N0} · t={frames[index].TimestampNanoseconds / 1e9:F3}s";
        }
        catch (OperationCanceledException)
        {
            FindStatus = "stopped";
        }
        finally
        {
            if (ReferenceEquals(_breakSearch, cts)) _breakSearchRunning = false;
        }
    }

    private void AdvancePlayback()
    {
        if (!_isPlaying || _frames.Count == 0)
        {
            return;
        }

        var step = _animate ? 1 : Math.Max(1, (int)Math.Round(_frames.Count / 250d * PlaybackSpeed));
        var next = Math.Min(_frames.Count - 1, _traceEndIndex + step);
        MoveToFrame(next);
        if (next >= _frames.Count - 1)
        {
            _isPlaying = false;
            _playTimer.Stop();
            OnPropertyChanged(nameof(PlaybackButtonText));
            OnPropertyChanged(nameof(IsPlaying));
        }
    }

    private void MoveByWindow(int direction)
    {
        if (_frames.Count == 0)
        {
            return;
        }

        var step = Math.Max(1, _frames.Count / 20);
        MoveToFrame(Math.Clamp(_traceEndIndex + direction * step, 0, _frames.Count - 1));
    }

    private void MoveToPercent(double percent)
    {
        if (_frames.Count == 0)
        {
            return;
        }

        var target = (int)Math.Round((_frames.Count - 1) * percent / 100d);
        var dispatcher = Application.Current?.Dispatcher;
        if (_frames.Count < BackgroundFilterThreshold || dispatcher is null)
        {
            MoveToFrame(target, updatePercent: false);
            return;
        }

        // Dragging the slider on a very large trace raises many updates per second and each one
        // rebuilds the grid. Keep only the newest target and apply it when the UI is idle, so the
        // slider stays responsive and the final position is always rendered exactly.
        _pendingScrubFrame = target;
        if (_scrubApplyScheduled)
        {
            return;
        }

        _scrubApplyScheduled = true;
        dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _scrubApplyScheduled = false;
            if (_pendingScrubFrame is { } pending)
            {
                _pendingScrubFrame = null;
                MoveToFrame(pending, updatePercent: false);
            }
        });
    }

    private int? _pendingScrubFrame;
    private bool _scrubApplyScheduled;

    private void MoveToFrame(int frameIndex, bool updatePercent = true)
    {
        if (_frames.Count == 0)
        {
            return;
        }

        _traceEndIndex = Math.Clamp(frameIndex, 0, _frames.Count - 1);
        if (_selectedSourceFrameIndex >= 0 &&
            _traceEndIndex != _selectedSourceFrameIndex)
            ClearSelectedSourceByteEvidence();
        if (updatePercent)
        {
            _timelinePercent = _frames.Count <= 1 ? 100 : _traceEndIndex * 100d / (_frames.Count - 1);
            OnPropertyChanged(nameof(TimelinePercent));
        }

        ApplyFilter();
        if (Rows.Count > 0)
        {
            var visiblePosition = Rows.PositionOfFrameIndex(_traceEndIndex);
            var row = Rows[visiblePosition >= 0 ? visiblePosition : Rows.Count - 1];
            SelectedRow = row;
            BringRowIntoViewRequested?.Invoke(this, row);
        }

        OnPropertyChanged(nameof(NavigationText));
    }
}
