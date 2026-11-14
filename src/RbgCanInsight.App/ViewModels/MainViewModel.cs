using System.Collections.ObjectModel;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using RbgCanInsight.Analysis;
using RbgCanInsight.App.Common;
using RbgCanInsight.Correlation;
using RbgCanInsight.Dbc;
using RbgCanInsight.DiagData;
using RbgCanInsight.Diagnostics;
using RbgCanInsight.Domain;
using RbgCanInsight.TraceIO;

namespace RbgCanInsight.App.ViewModels;

public sealed record RecentFile(string Path, string Kind)
{
    public string FileName => System.IO.Path.GetFileName(Path);
    public string Folder => System.IO.Path.GetDirectoryName(Path) ?? string.Empty;
}

/// <summary>One source in the trace file explorer. Null Path selects the combined view.</summary>
public sealed record TraceWorkspaceEntry(string? Path, string DisplayName, bool IsCurrent, bool CanRemove,
    string? Details = null)
{
    public string Description => Path is null ? "All source traces, aligned according to the session mode" :
        string.IsNullOrWhiteSpace(Details) ? Path : Path + Environment.NewLine + Details;
    public string ActionLabel => IsCurrent ? "Viewing" : "View";
    public string FocusLabel => Path is null ? "All rows" : "Focus";
    public bool CanView => !IsCurrent;
}
public sealed record TraceImportChannelRow(TraceChannelHealth Health)
{
    public string ChannelText => $"CAN {Health.Channel + 1}";
    public long Frames => Health.Frames;
    public long ErrorFrames => Health.ErrorFrames;
    public long RemoteFrames => Health.RemoteFrames;
    public long CanFdFrames => Health.CanFdFrames;
    public long OutOfOrderTimestamps => Health.OutOfOrderTimestamps;
}


public sealed record WorkspacePreset(
    int SelectedNavIndex,
    int ThemeIndex,
    int TraceViewModeIndex,
    double TracePlaybackSpeed,
    double SignalPlaybackSpeed,
    double SignalWindowSeconds,
    double ByteMatrixPlaybackSpeed,
    int MappingStatusFilterIndex,
    IReadOnlyList<string> WatchKeys);

/// <summary>
/// A full offline analysis session: which files were loaded (trace, DBC/ARXML, CDD/ODX, a
/// reviewed DID map) plus enough workspace state (filters, watch list, plotted signals) to pick
/// up exactly where the analyst left off — as opposed to <see cref="WorkspacePreset"/>, which only
/// remembers UI state and assumes the same files are already loaded.
/// </summary>
public sealed record SessionData(
    string? TracePath,
    IReadOnlyList<string> DbcPaths,
    IReadOnlyList<string> CatalogPaths,
    string? MappingPath,
    int SelectedNavIndex,
    int ThemeIndex,
    int TraceViewModeIndex,
    double TracePlaybackSpeed,
    double SignalPlaybackSpeed,
    double SignalWindowSeconds,
    double ByteMatrixPlaybackSpeed,
    int MappingStatusFilterIndex,
    IReadOnlyList<string> WatchKeys,
    IReadOnlyList<string> PlottedKeys,
    string TraceSearchText,
    string TraceRuleText,
    string SignalsSearchText,
    string MatrixSearchText,
    string ByteMatrixFilterText,
    string StatisticsSearchText,
    string MappingSearchText,
    string? TraceRelativePath = null,
    IReadOnlyList<string>? DbcRelativePaths = null,
    IReadOnlyList<string>? CatalogRelativePaths = null,
    string? MappingRelativePath = null,
    IReadOnlyList<string>? TracePaths = null,
    IReadOnlyList<string>? TraceRelativePaths = null,
    string? TraceMergeMode = null,
    IReadOnlyList<TraceNotebookPage>? TraceNotebooks = null,
    string? ActiveTraceNotebookId = null,
    int? DataStudioSectionIndex = null);

public sealed record UnmatchedIdentifierRow(
    int Channel,
    string Identifier,
    long FrameCount,
    string FirstTimeText,
    string LastTimeText,
    string Hint)
{
    /// <summary>Same "0x" notation as the coverage and E2E windows.</summary>
    public string IdentifierText => Identifier.EndsWith('x') ? "0x" + Identifier[..^1] + " (29-bit)" : "0x" + Identifier;
}

public sealed record CompareTraceRow(
    int Channel,
    string Identifier,
    long BaseFrames,
    long CompareFrames,
    long FrameDelta,
    string BaseRate,
    string CompareRate,
    string Status);

public sealed record CompareSignalRow(
    string Ecu,
    string Message,
    string Signal,
    string Unit,
    int BaseSamples,
    int CompareSamples,
    string BaseLast,
    string CompareLast,
    string DeltaLast,
    string BaseRange,
    string CompareRange,
    string Status,
    SignalChangeReport? BaseReport,
    SignalChangeReport? CompareReport);

public sealed class WatchSignalRow : ObservableObject
{
    private string _valueText = "-";
    private string _timeText = "-";

    public WatchSignalRow(PlotSeriesViewModel series)
    {
        Series = series;
    }

    public PlotSeriesViewModel Series { get; }
    public string Ecu => Series.Transmitter;
    public string Message => Series.MessageName;
    public string Signal => Series.SignalName;
    public string Unit => Series.Unit;
    public string Identifier => "0x" + Series.IdentifierHex;

    public string ValueText
    {
        get => _valueText;
        private set => SetProperty(ref _valueText, value);
    }

    public string TimeText
    {
        get => _timeText;
        private set => SetProperty(ref _timeText, value);
    }

    public void Update(long cursorTimestampNanoseconds)
    {
        if (Series.Samples.Count == 0)
        {
            ValueText = "-";
            TimeText = "-";
            return;
        }

        var timestamp = cursorTimestampNanoseconds >= 0
            ? cursorTimestampNanoseconds
            : Series.Samples[^1].TimestampNanoseconds;
        var sample = LatestSampleAtOrBefore(Series.Samples, timestamp);
        if (sample is null)
        {
            ValueText = "-";
            TimeText = "-";
            return;
        }
        var unit = string.IsNullOrWhiteSpace(Unit) ? string.Empty : " " + Unit;
        ValueText = sample.Value.Value.ToString("G6", System.Globalization.CultureInfo.InvariantCulture) + unit;
        TimeText = (sample.Value.TimestampNanoseconds / 1_000_000_000d)
            .ToString("F6", System.Globalization.CultureInfo.InvariantCulture) + " s";
    }

    private static SignalSample? LatestSampleAtOrBefore(IReadOnlyList<SignalSample> samples, long timestamp)
    {
        var low = 0;
        var high = samples.Count;
        while (low < high)
        {
            var mid = low + (high - low) / 2;
            if (samples[mid].TimestampNanoseconds <= timestamp) low = mid + 1;
            else high = mid;
        }
        // A future sample must not be displayed as if the ECU had already sent it.
        return low == 0 ? null : samples[low - 1];
    }
}

public sealed class StatisticsRowViewModel
{
    public StatisticsRowViewModel(MessageStatisticsRow row, string messageName, string ecu)
    {
        Row = row;
        MessageName = messageName;
        Ecu = ecu;
    }

    public MessageStatisticsRow Row { get; }
    public int RawChannel => Row.Channel;
    public uint RawIdentifier => Row.Identifier;
    public bool IsExtended => Row.IsExtended;
    public int Channel => Row.Channel + 1;
    public string Identifier =>
        Row.Identifier.ToString(Row.IsExtended ? "X8" : "X3", System.Globalization.CultureInfo.InvariantCulture) +
        (Row.IsExtended ? "x" : string.Empty);
    public string MessageName { get; }
    public string Ecu { get; }
    public long FrameCount => Row.FrameCount;
    public double AveragePeriodMs => Row.AveragePeriodMilliseconds;
    /// <summary>"-" when no period exists (a single frame), instead of a misleading 0.0.</summary>
    public string AveragePeriodText => Row.FrameCount < 2 || Row.AveragePeriodMilliseconds <= 0
        ? "-"
        : Row.AveragePeriodMilliseconds.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
    public double MinPeriodMs => Row.MinPeriodMilliseconds;
    public double MaxPeriodMs => Row.MaxPeriodMilliseconds;
    public string RateText => Row.AveragePeriodMilliseconds <= 0
        ? "-"
        : (1000d / Row.AveragePeriodMilliseconds).ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + " Hz";
    public string JitterText => Row.FrameCount < 2
        ? "-"
        : (Row.MaxPeriodMilliseconds - Row.MinPeriodMilliseconds).ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + " ms";
    public string StdDevText => Row.FrameCount < 2
        ? "-"
        : Row.StdDevPeriodMilliseconds.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + " ms";
    public int BurstCount => Row.BurstCount;
    public string BurstCountText => Row.BurstCount > 0
        ? Row.BurstCount.ToString(System.Globalization.CultureInfo.InvariantCulture)
        : "-";
    public string LengthText => Row.MinLength == Row.MaxLength
        ? Row.MinLength.ToString(System.Globalization.CultureInfo.InvariantCulture)
        : $"{Row.MinLength}-{Row.MaxLength}";
    public string FirstTimeText => (Row.FirstTimestampNanoseconds / 1_000_000_000d).ToString("F3", System.Globalization.CultureInfo.InvariantCulture);
    public string LastTimeText => (Row.LastTimestampNanoseconds / 1_000_000_000d).ToString("F3", System.Globalization.CultureInfo.InvariantCulture);

    public bool Matches(string search) =>
        string.IsNullOrWhiteSpace(search) ||
        Identifier.Contains(search, StringComparison.OrdinalIgnoreCase) ||
        MessageName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
        Ecu.Contains(search, StringComparison.OrdinalIgnoreCase) ||
        Channel.ToString(System.Globalization.CultureInfo.InvariantCulture) == search.Trim();
}

/// <summary>Estimated bus load for one channel, driven by a user-editable assumed bitrate (see
/// <see cref="BusLoadCalculator"/> for why this is never silently auto-detected).</summary>
public sealed class ChannelBusLoadRowViewModel : ObservableObject
{
    private readonly long _totalBits;
    private readonly long _spanNanoseconds;
    private double _bitrateKbps = 500;

    /// <param name="totalBits">Nominal unstuffed bit total of this channel's frames. Only the
    /// aggregate is kept, never a copy of the frames, so a disk-backed trace stays disk-backed.</param>
    public ChannelBusLoadRowViewModel(int channel, long frameCount, long totalBits, long spanNanoseconds,
        double? loggedMeanPercent = null)
    {
        LoggedLoadText = loggedMeanPercent is { } logged
            ? "· logged by recorder: " + logged.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + " % (mean)"
            : string.Empty;
        Channel = channel;
        FrameCount = frameCount;
        _totalBits = totalBits;
        _spanNanoseconds = spanNanoseconds;
    }

    public int Channel { get; }
    public string ChannelText => $"Ch {Channel + 1}";
    public long FrameCount { get; }

    public double BitrateKbps
    {
        get => _bitrateKbps;
        set
        {
            if (SetProperty(ref _bitrateKbps, Math.Max(1, value)))
            {
                OnPropertyChanged(nameof(LoadPercent));
                OnPropertyChanged(nameof(LoadPercentText));
            }
        }
    }

    /// <summary>Non-empty only when the source file carried logger-written Statistic: lines.</summary>
    public string LoggedLoadText { get; }

    public double LoadPercent => BusLoadCalculator.ComputeLoadPercent(_totalBits, _spanNanoseconds, _bitrateKbps * 1000);
    public string LoadPercentText => LoadPercent.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + " %";
}

/// <summary>
/// One chronological timing incident (see <see cref="TimingIncident"/>), dressed up with the
/// ECU/message names resolved from the loaded DBC for display. Trace health is read as a
/// timeline of individual events, not a per-signal roll-up — the same ID can appear on several
/// rows if it had more than one anomalous gap over the trace.
/// </summary>
public sealed class TraceHealthRowViewModel
{
    public TraceHealthRowViewModel(TimingIncident incident, string ecu, string messageName, string severity, int estimatedMissingFrames)
    {
        Incident = incident;
        Ecu = ecu;
        MessageName = messageName;
        Severity = severity;
        EstimatedMissingFrames = estimatedMissingFrames;
    }

    public TimingIncident Incident { get; }
    public int FrameIndex => Incident.FrameIndex;
    public int RawChannel => Incident.Channel;
    public uint RawIdentifier => Incident.Identifier;
    public bool IsExtended => Incident.IsExtended;
    public int Channel => Incident.Channel + 1;
    public string Identifier =>
        Incident.Identifier.ToString(Incident.IsExtended ? "X8" : "X3", System.Globalization.CultureInfo.InvariantCulture) +
        (Incident.IsExtended ? "x" : string.Empty);
    public string Ecu { get; }
    public string MessageName { get; }
    public long TimestampNanoseconds => Incident.TimestampNanoseconds;
    public string TimeText => (Incident.TimestampNanoseconds / 1_000_000_000d).ToString("F3", System.Globalization.CultureInfo.InvariantCulture);
    public double GapMs => Incident.GapMilliseconds;
    public double AveragePeriodMs => Incident.AveragePeriodMilliseconds;
    public double GapRatio => Incident.RatioVsAverage;
    public int EstimatedMissingFrames { get; }
    public string Severity { get; }
    public string Finding => EstimatedMissingFrames > 0
        ? $"Gap of {GapMs:F1} ms ({GapRatio:F1}x average period); about {EstimatedMissingFrames:N0} frame(s) may be missing."
        : $"Gap of {GapMs:F1} ms ({GapRatio:F1}x average period).";
    public string GapText => GapMs.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
    public string AveragePeriodText => AveragePeriodMs <= 0 ? "-" : AveragePeriodMs.ToString("F3", System.Globalization.CultureInfo.InvariantCulture);
    public string GapRatioText => GapRatio.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + "x";
}

public sealed class PresetLibraryRowViewModel(PresetLibraryEntry entry)
{
    public PresetLibraryEntry Entry { get; private set; } = entry;
    public string Path => Entry.Path;
    public string SavedAtText => Entry.SavedAt.LocalDateTime.ToString("g", System.Globalization.CultureInfo.CurrentCulture);

    public string Name
    {
        get => Entry.Name;
        set
        {
            if (string.IsNullOrWhiteSpace(value) || value == Entry.Name)
            {
                return;
            }

            PresetLibraryStore.Rename(Entry.Path, value);
            Entry = Entry with { Name = value };
        }
    }
}

public sealed record DbcMessageCatalogRow(
    string Database,
    string SourceFile,
    string Identifier,
    string Name,
    string Transmitter,
    int Length,
    int SignalCount,
    string CoverageText,
    string Comment,
    string NominalCycleText = "—");

public sealed record DbcSignalCatalogRow(
    string Database,
    string Ecu,
    string Message,
    string Identifier,
    string Signal,
    string Unit,
    string StartBit,
    int BitLength,
    string RangeText,
    string ScalingText,
    string Comment,
    string ByteOrderText = "",
    string EncodingText = "",
    string MultiplexText = "",
    int NamedValuesCount = 0,
    long ObservedSamples = 0,
    int ObservedChannels = 0)
{
    public string ObservedText => ObservedSamples == 0
        ? "Not observed"
        : $"{ObservedSamples:N0} {(ObservedSamples == 1 ? "sample" : "samples")} · {ObservedChannels} CAN";
}

public sealed class DbcDatabaseRowViewModel : ObservableObject
{
    private readonly Action? _optionsChanged;
    private bool _matchByPgn;
    private int _channelIndex;

    public DbcDatabaseRowViewModel(
        string name,
        string path,
        int messageCount,
        int signalCount,
        int matchedIdentifierCount,
        long matchedFrameCount,
        IReadOnlyList<string> warnings,
        IReadOnlyList<string> channelChoices,
        bool matchByPgn,
        int channelIndex,
        Action? optionsChanged)
    {
        Name = name;
        Path = path;
        MessageCount = messageCount;
        SignalCount = signalCount;
        MatchedIdentifierCount = matchedIdentifierCount;
        MatchedFrameCount = matchedFrameCount;
        Warnings = warnings;
        ChannelChoices = channelChoices;
        _matchByPgn = matchByPgn;
        _channelIndex = Math.Clamp(channelIndex, 0, Math.Max(0, channelChoices.Count - 1));
        _optionsChanged = optionsChanged;
    }

    public string Name { get; }
    public string Path { get; }
    public string FileName => System.IO.Path.GetFileName(Path);
    public int MessageCount { get; }
    public int SignalCount { get; }
    public int MatchedIdentifierCount { get; }
    public long MatchedFrameCount { get; }
    public IReadOnlyList<string> Warnings { get; }
    public bool HasWarnings => Warnings.Count > 0;
    public string WarningBadge => HasWarnings ? $"⚠ {Warnings.Count}" : string.Empty;
    public string WarningsText => Warnings.Count == 0
        ? "No parse warnings."
        : string.Join(Environment.NewLine, Warnings);

    public IReadOnlyList<string> ChannelChoices { get; }

    /// <summary>J1939 mode: match trace frames to this DBC by PGN instead of the full 29-bit ID.</summary>
    public bool MatchByPgn
    {
        get => _matchByPgn;
        set
        {
            if (SetProperty(ref _matchByPgn, value))
            {
                _optionsChanged?.Invoke();
            }
        }
    }

    /// <summary>0 = all channels; N = only channel N.</summary>
    public int ChannelIndex
    {
        get => _channelIndex;
        set
        {
            if (SetProperty(ref _channelIndex, value))
            {
                _optionsChanged?.Invoke();
            }
        }
    }

    public string CoverageText => MatchedFrameCount == 0
        ? "No trace match"
        : $"{MatchedIdentifierCount:N0} IDs · {MatchedFrameCount:N0} frames";
}

/// <summary>
/// Application shell and session: owns the loaded trace, DBC databases and analysis results,
/// wires the per-view models together and handles navigation.
/// </summary>
public sealed class MainViewModel : ObservableObject, IDisposable
{
    private sealed record DbcOptionsState(bool MatchByPgn, int ChannelIndex);

    private sealed record CoverageSnapshot(
        Dictionary<string, (int MatchedIds, long MatchedFrames)> ByDatabase,
        long DecodedFrames,
        int DecodedIdentifiers,
        HashSet<uint> SeenMessageIdentifiers,
        List<UnmatchedIdentifierRow> Unmatched);

    // Swap the fully imported List reference atomically instead of copying every
    // 80+ byte CanFrame into a second List and a third array. This keeps active
    // analysis/UI readers on their original immutable-in-practice snapshot.
    private IReadOnlyList<CanFrame> _frames = [];
    private IReadOnlyList<CanFrame> _framesSnapshot = [];
    // Owns the frame storage of the loaded trace (a memory-mapped spill file for very large ones).
    private CanFrameCollection? _loadedFrames;
    private readonly List<DbcDatabase> _databases = [];
    private readonly Dictionary<string, string> _databasePaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DbcOptionsState> _dbcOptions = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _autoResolvedDbcFixKeys = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, (int MatchedIds, long MatchedFrames)> _coverageByDb = new(StringComparer.OrdinalIgnoreCase);
    private List<UnmatchedIdentifierRow> _unmatchedRows = [];
    private int _lastAutoResolvedUnmatchedCount;
    private IReadOnlyList<SignalTimeline> _timelines = [];
    private IReadOnlyList<SignalChangeReport> _reports = [];
    private IReadOnlyList<MessageStatisticsRow> _statistics = [];
    private DbcCycleTimingResult _cycleTimingResult = DbcCycleTimingResult.Empty;
    private SignalStateTransitionReport _stateTransitionReport = new([], 0, 0, 0);
    private DbcNetworkMatrixReport _networkMatrixReport = new([], [], 0, 0, 0);
    private IReadOnlyList<DbcDecoder> _decoders = [];
    private long _decodedFrameCount;
    private long _decodedSampleCount;
    private int _decodedIdentifierCount;

    private int _selectedNavIndex;
    private bool _isBusy;
    private string _busyText = string.Empty;
    private double? _busyProgressPercent;
    private System.Threading.CancellationTokenSource? _analysisCancellation;
    private string _statusText = "Open a CAN trace to start.";
    private int _themeIndex;
    private string _traceName = string.Empty;
    private string _tracePath = string.Empty;
    private readonly List<string> _traceWorkspacePaths = new();
    private TraceMergeMode _traceWorkspaceMode = TraceMergeMode.ByTime;
    private TraceSessionSourceIndex? _traceSourceIndex;
    private IReadOnlyList<TraceSessionSourceSummary> _traceSourceSummaries = [];
    private readonly Dictionary<string, string> _traceSourceLabels = new(StringComparer.OrdinalIgnoreCase);
    private string? _activeTraceWorkspacePath;
    private bool _traceImportRunning;
    private AsyncCommand? _addTraceFilesCommand;
    private RelayCommand<TraceWorkspaceEntry>? _viewTraceSourceCommand;
    private RelayCommand<TraceWorkspaceEntry>? _focusTraceSourceCommand;
    private RelayCommand? _clearTraceSourceFocusCommand;
    private RelayCommand<TraceWorkspaceEntry>? _removeTraceSourceCommand;
    private AsyncCommand? _openOriginalTraceSourceCommand;
    private TraceImportHealthSnapshot? _traceImportHealth;
    private TraceSourceFingerprint? _traceSourceFingerprint;
    private string _dbcCatalogSearchText = string.Empty;
    private bool _dbcObservedOnly;
    private readonly DebounceTimer _dbcSearchDebounce = new();
    private string _statisticsSearchText = string.Empty;
    private DbcDatabaseRowViewModel? _selectedDbcDatabase;
    private DbcMessageCatalogRow? _selectedDbcMessage;
    private DbcSignalCatalogRow? _selectedDbcSignal;
    private RelayCommand? _plotSelectedDbcSignalCommand;
    private RelayCommand? _openSelectedDbcSignalFrameCommand;
    private RelayCommand? _mapSelectedDiagnosticDidCommand;
    private RelayCommand? _browseDiagnosticCatalogCommand;
    private RelayCommand? _browseSelectedMappingDidCommand;
    private RelayCommand? _showSelectedDiagnosticDidSamplesCommand;
    private StatisticsRowViewModel? _selectedStatisticsRow;
    private long _plotCursorNs = -1;
    private RelayCommand<object>? _navigateCommand;
    private RelayCommand<string>? _openStatisticsRowCommand;
    private RelayCommand<string>? _filterTraceByIdentifierCommand;

    private AsyncCommand? _openTraceCommand;
    private AsyncCommand? _loadDbcCommand;
    private AsyncCommand? _exportDecodedSignalsCommand;
    private AsyncCommand? _exportDbcDefinitionsCommand;
    private AsyncCommand? _compareDbcFilesCommand;
    private AsyncCommand? _exportDbcComparisonCommand;
    private RelayCommand? _inspectBaselineDbcDiffCommand;
    private RelayCommand? _inspectCurrentDbcDiffCommand;
    private DbcDefinitionDiffRow? _selectedDbcComparisonDiffRow;
    private AsyncCommand? _exportDiagnosticDefinitionsCommand;
    private AsyncCommand? _compareDiagnosticFilesCommand;
    private AsyncCommand? _exportDiagnosticComparisonCommand;
    private RelayCommand? _inspectBaselineDiagnosticDiffCommand;
    private RelayCommand? _inspectCurrentDiagnosticDiffCommand;
    private DiagnosticDefinitionDiffRow? _selectedDiagnosticDiffRow;
    private IReadOnlyList<DidCatalog>? _comparisonDiagnosticBefore;
    private IReadOnlyList<DidCatalog>? _comparisonDiagnosticAfter;
    private DiagnosticDefinitionDiffReport? _diagnosticDefinitionComparison;
    private string _comparisonDiagnosticBeforeFile = string.Empty;
    private string _comparisonDiagnosticAfterFile = string.Empty;
    private bool _diagnosticComparisonChangesOnly = true;
    private string _diagnosticComparisonSearchText = string.Empty;
    private string _selectedDiagnosticComparisonEcu = "All ECUs";
    private string _selectedDiagnosticComparisonScope = "All types";
    private DbcDatabase? _comparisonDbcBaseline;
    private DbcDatabase? _comparisonDbcCurrent;
    private DbcDefinitionDiffReport? _dbcDefinitionComparison;
    private bool _dbcComparisonChangesOnly = true;
    private string _comparisonDbcBaselineFile = string.Empty;
    private string _comparisonDbcCurrentFile = string.Empty;
    private AsyncCommand? _exportEngineeringParametersCommand;
    private AsyncCommand? _exportSelectedDiagnosticDidCommand;
    private AsyncCommand? _savePresetCommand;
    private AsyncCommand? _saveSessionCommand;
    private AsyncCommand? _loadSessionCommand;
    private AsyncCommand? _compareTraceCommand;
    private AsyncCommand? _exportReportCommand;

    /// <summary>
    /// Set by the view to a delegate that renders whichever analysis view is currently on screen
    /// to a PNG. The view model has no rendering capability of its own (no FrameworkElement
    /// reference), so it calls back into the view only when building an export report.
    /// </summary>
    public Func<byte[]?>? CaptureViewScreenshot { get; set; }

    private RelayCommand? _aboutCommand;
    private RelayCommand<DbcDatabaseRowViewModel>? _removeDbcCommand;
    private RelayCommand? _clearDbcCommand;
    private RelayCommand<RecentFile>? _openRecentCommand;
    private RelayCommand<SignalDetailRow>? _plotTraceSignalCommand;
    private RelayCommand<SignalDetailRow>? _quickPlotTraceSignalCommand;
    private RelayCommand<SignalDetailRow>? _watchTraceSignalCommand;
    private RelayCommand<WatchSignalRow>? _removeWatchSignalCommand;
    private RelayCommand? _plotSelectedTraceMessageCommand;
    private RelayCommand? _inspectSelectedTraceUdsCommand;
    private RelayCommand? _inspectSelectedTraceDbcCommand;
    private RelayCommand? _showUdsProblemsCommand;
    private RelayCommand<string>? _startInvestigationCommand;
    private RelayCommand? _plotSelectedByteMatrixRowCommand;
    private RelayCommand? _plotSelectedMappingCommand;

    public MainViewModel()
    {
        // Created once, on the thread that owns the view-model (the UI thread in the app).
        _statusExpiryTimer = new System.Windows.Threading.DispatcherTimer(
            System.Windows.Threading.DispatcherPriority.Background)
        {
            Interval = StatusLifetime
        };
        _statusExpiryTimer.Tick += OnStatusExpiryTick;
        Trace = new TraceViewModel();
        Notebook = new TraceNotebookWorkspace(this);
        DataStudio = new DataStudioViewModel(this);
        Trace.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TraceViewModel.SelectedRow))
                OnPropertyChanged(nameof(CanOpenOriginalSource));
        };
        Signals = new SignalsViewModel();
        // The Signals playback cursor is the canonical offline time while
        // that workspace is active. Keep plot crosshair and watch values in
        // sync with scrubbing/replay instead of showing a stale Trace selection.
        Signals.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SignalsViewModel.PlotWindowEndNanoseconds) &&
                SelectedNavIndex == 3 && Signals.PlotWindowEndNanoseconds >= 0)
                PlotCursorNanoseconds = Signals.PlotWindowEndNanoseconds;
        };
        Matrix = new MatrixViewModel();
        ByteMatrix = new ByteMatrixViewModel();
        Mapping = new MappingViewModel();
        Uds = new UdsAnalysisViewModel();
        CatalogExplorer = new DiagnosticCatalogExplorerViewModel();
        CycleTiming = new DbcCycleTimingViewModel();
        StateTransitions = new SignalStateTransitionsViewModel();
        NetworkMatrix = new DbcNetworkMatrixViewModel();
        Triggers = new SignalTriggerViewModel();
        Sequences = new SignalTriggerSequenceViewModel();
        Sequences.FrameRequested += (_, frameIndex) =>
        {
            SelectedNavIndex = 1;
            Trace.JumpToFrameIndex(frameIndex);
        };
        DbcValues.FrameRequested += (_, frameIndex) =>
        {
            SelectedNavIndex = 1;
            Trace.JumpToFrameIndex(frameIndex);
        };
        Triggers.FrameRequested += (_, frameIndex) =>
        {
            SelectedNavIndex = 1;
            Trace.JumpToFrameIndex(frameIndex);
        };
        NetworkMatrix.InspectDbcRequested += (_, selected) =>
        {
            // A DBC-definition row is not proof of the physical sender. Only
            // navigate to a uniquely loaded declaration with the same wire
            // identity and message name. Never guess an ECU from CAN ID alone.
            var matched = _databases.Where(db => db.Name == selected.Database)
                .SelectMany(db => db.AllMessageDefinitions)
                .Where(message => message.Identifier == selected.Identifier &&
                    message.IsExtended == selected.IsExtended &&
                    message.Name == selected.Message).ToArray();
            if (matched.Length != 1)
            {
                StatusText = "Cannot open an ambiguous or missing DBC network definition.";
                return;
            }
            DbcCatalogSearchText = string.Empty;
            SelectedDbcDatabase = DbcDatabases.SingleOrDefault(db =>
                db.Name == selected.Database);
            if (SelectedDbcDatabase is null)
            {
                StatusText = "DBC network source is no longer loaded.";
                return;
            }
            RebuildDbcMessageRows(); // bypass deferred catalog search refresh
            var idText = selected.Identifier.ToString(selected.IsExtended ? "X8" : "X3",
                System.Globalization.CultureInfo.InvariantCulture) +
                (selected.IsExtended ? "x" : string.Empty);
            var matches = DbcMessages.Where(message => message.Database == selected.Database &&
                message.Identifier == idText && message.Name == selected.Message).ToArray();
            if (matches.Length != 1)
            {
                StatusText = "DBC message selection is ambiguous; source data was not changed.";
                return;
            }
            SelectedDbcMessage = matches[0];
            SelectedNavIndex = 2;
            StatusText = $"Inspecting the declared network message {selected.Message}. " +
                "Observed CAN frames do not independently prove ECU identity.";
        };
        StateTransitions.FrameRequested += (_, index) => { SelectedNavIndex = 1; Trace.JumpToFrameIndex(index); };
        Investigation = new InvestigationViewModel(
            Trace, Uds, () => SelectedNavIndex == InvestigationNavIndex,
            new InvestigationLayoutStore(Path.Combine(AppStorage.ConfigDirectory, "investigation-layouts.json")));
        CycleTiming.FrameRequested += (_, frameIndex) =>
        {
            SelectedNavIndex = 1;
            Trace.JumpToFrameIndex(frameIndex);
        };

        Matrix.PlotRequested += (_, timeline) =>
        {
            // The matrix row already identifies a real logger. A plot
            // launched from it must be visible even when Signals last
            // displayed a different physical recording.
            if (timeline.OriginalSourceIndex >= 0 &&
                Signals.HasMultipleLoggers)
                Signals.SelectedLoggerIndex = timeline.OriginalSourceIndex;
            Signals.Plot(timeline);
            SelectedNavIndex = 3;
        };
        Matrix.FrameRequested += (_, frameIndex) =>
        {
            SelectedNavIndex = 1;
            Trace.JumpToFrameIndex(frameIndex);
        };

        // Feed CDD/ODX DID names into the trace grid's UDS interpretation, so a diagnostic
        // response frame reads "DID 0xF190 (VIN)" instead of just the raw identifier.
        Mapping.CatalogsChanged += async (_, _) =>
        {
            Trace.SetDidNameResolver(Mapping.ResolveDidName);
            CatalogExplorer.SetCatalogs(Mapping.Catalogs);
            await RefreshUdsAnalysisAsync();
        };
        Uds.TraceFrameRequested += (_, frameIndex) =>
        {
            if (IsDiagnosticSourceCurrent())
                OpenTraceFrame(frameIndex);
            else
                StatusText = "Diagnostic source belongs to a different trace. Refresh UDS analysis to navigate original CAN frames.";
        };
        Uds.TraceSourceSpanRequested += (_, source) =>
        {
            if (!IsDiagnosticSourceCurrent())
            {
                StatusText = "Diagnostic source belongs to a different trace. Refresh UDS analysis before opening the original bytes.";
                return;
            }
            SelectedNavIndex = 1;
            if (!Trace.JumpToSourceByteSpan(source))
                StatusText = "The verified DID source byte range is no longer available in the current Trace view.";
        };

        LoadRecentFiles();
    }

    /// <summary>Both decoded results and raw Trace rows must belong to
    /// the same object-identical capture; neither CAN IDs nor frame
    /// numbers establish provenance across a trace reload.</summary>
    private bool IsDiagnosticSourceCurrent() =>
        Uds.IsAnalysisForFrames(_framesSnapshot) &&
        Trace.IsSessionFrames(_framesSnapshot);

    /// <summary>Switch to the Trace view and select the original source frame (0-based index).</summary>
    public void OpenTraceFrame(int frameIndex)
    {
        SelectedNavIndex = 1;
        Trace.JumpToFrameIndex(frameIndex);
    }

    public TraceViewModel Trace { get; }
    public SignalsViewModel Signals { get; }
    public MatrixViewModel Matrix { get; }
    public ByteMatrixViewModel ByteMatrix { get; }
    public MappingViewModel Mapping { get; }
    public UdsAnalysisViewModel Uds { get; }
    public DiagnosticCatalogExplorerViewModel CatalogExplorer { get; }
    public DbcCycleTimingViewModel CycleTiming { get; }
    public SignalStateTransitionsViewModel StateTransitions { get; }
    public DbcNetworkMatrixViewModel NetworkMatrix { get; }
    public SignalTriggerViewModel Triggers { get; }
    public SignalTriggerSequenceViewModel Sequences { get; }

    /// <summary>Workspace index of the synchronized-pane Investigation view.</summary>
    public const int InvestigationNavIndex = 9;
    /// <summary>Dedicated guided workspace; previous navigation indices are
    /// preserved for saved sessions, presets and UI test compatibility.</summary>
    public const int DataStudioNavIndex = 10;

    public InvestigationViewModel Investigation { get; }

    public RelayCommand BrowseDiagnosticCatalogCommand =>
        _browseDiagnosticCatalogCommand ??= new RelayCommand(() =>
        {
            Uds.SelectedViewIndex = 11;
            SelectedNavIndex = 8;
        });

    /// <summary>Reverse link from an analyst-selected mapping row to the
    /// exact loaded DID definition. Do not infer a matching ECU by DID alone.</summary>
    public RelayCommand BrowseSelectedMappingDidCommand =>
        _browseSelectedMappingDidCommand ??= new RelayCommand(() =>
        {
            if (Mapping.SelectedRow is not { } selected)
            {
                StatusText = "Select a DID mapping row before opening its diagnostic description.";
                return;
            }
            var did = selected.Did;
            var focus = new DiagnosticDiffNavigationTarget(
                "DID", $"0x{did.Did:X4}", did.Ecu, did.SourceFile);
            if (!CatalogExplorer.TryFocus(focus))
            {
                StatusText = $"DID 0x{did.Did:X4} / ECU {did.Ecu} cannot be uniquely " +
                    "identified in the currently loaded descriptions. Review source variants.";
                return;
            }
            Uds.SelectedViewIndex = 11;
            SelectedNavIndex = 8;
            StatusText = $"Browsing source-backed DID 0x{did.Did:X4} / ECU " +
                $"{did.Ecu}. Original diagnostic fields and Engineering PARs remain unmodified.";
        });

    /// <summary>Go from a source-backed DID definition to RECORDED positive
    /// UDS samples, without treating matching DID numbers as proof of ECU
    /// or variant identity. All observed endpoint groups remain visible.</summary>
    public RelayCommand ShowSelectedDiagnosticDidSamplesCommand =>
        _showSelectedDiagnosticDidSamplesCommand ??= new RelayCommand(() =>
            ShowSelectedDiagnosticDidSamples());

    public bool ShowSelectedDiagnosticDidSamples()
    {
        if (CatalogExplorer.SelectedDefinition?.Did is not { } did)
        {
            StatusText = "Select a diagnostic DID definition to locate recorded samples.";
            return false;
        }
        if (!Uds.FocusDidSamples(did))
        {
            StatusText = $"No complete positive UDS response samples for DID 0x{did:X4} " +
                "were recovered from the loaded trace. This does not prove the ECU lacks the DID.";
            return false;
        }
        SelectedNavIndex = 8;
        StatusText = $"Showing recorded DID 0x{did:X4} across all observed " +
            "UDS endpoint groups. Verify endpoint and source frames before assigning an ECU.";
        return true;
    }

    public RelayCommand MapSelectedDiagnosticDidCommand =>
        _mapSelectedDiagnosticDidCommand ??= new RelayCommand(() =>
        {
            if (CatalogExplorer.SelectedDefinition is not { Did: { } did } selected)
            {
                StatusText = "Select a diagnostic DID definition before opening CAN correlation.";
                return;
            }
            if (!Mapping.TryFocusDid(did, selected.Ecu, selected.SourceFile))
            {
                StatusText = $"DID 0x{did:X4} / ECU {selected.Ecu} does not resolve " +
                    "to one unique source definition in the mapping workspace. " +
                    "Reload the exact diagnostic description or review competing variants.";
                return;
            }
            SelectedNavIndex = 7;
            StatusText = $"Focused DID 0x{did:X4} / ECU {selected.Ecu} from " +
                $"{Path.GetFileName(selected.SourceFile)}. Candidate confidence " +
                "does not verify CAN signal identity.";
        });

    public ObservableCollection<RecentFile> RecentFiles { get; } = [];
    public ObservableCollection<TraceWorkspaceEntry> TraceWorkspaceEntries { get; } = [];
    /// <summary>Notebook-like named Trace query tabs. Other analysis views
    /// continue using the combined source until explicitly scoped.</summary>
    public TraceNotebookWorkspace Notebook { get; }
    public DataStudioViewModel DataStudio { get; }
    public IReadOnlyList<string> TraceWorkspacePaths => _traceWorkspacePaths.ToArray();
    public TraceMergeMode TraceWorkspaceMergeMode => _traceWorkspaceMode;
    public bool HasTraceWorkspace => _traceWorkspacePaths.Count > 0;
    public bool HasMultipleTraceSources => _traceWorkspacePaths.Count > 1;
    public bool CanCompareTraceSources => _traceSourceIndex is { SourceCount: > 1 };
    public bool CanFocusTraceSource => _traceSourceIndex is { SourceCount: > 1 };
    public bool CanOpenOriginalSource => _traceSourceIndex is not null && Trace.SelectedRow is not null;
    public string TraceWorkspaceSummary => _traceWorkspacePaths.Count switch
    {
        0 => "Open files · no trace loaded",
        1 => "Open files · 1 trace",
        _ => $"Open files · {_traceWorkspacePaths.Count} traces · {_traceWorkspaceMode}" +
             (_activeTraceWorkspacePath is null ? " · combined view" : " · one-file view")
    };
    public ObservableCollection<string> DatabaseSummaries { get; } = [];
    public ObservableCollection<DbcDatabaseRowViewModel> DbcDatabases { get; } = [];

    /// <summary>Comparison of independent diagnostic description files.
    /// Results are read-only and never replace active UDS/DBC decoder inputs.</summary>
    public IReadOnlyList<DiagnosticDefinitionDiffRow> DiagnosticComparisonRows { get; private set; } = [];
    public IReadOnlyList<string> DiagnosticComparisonEcuChoices { get; private set; } = ["All ECUs"];
    public IReadOnlyList<string> DiagnosticComparisonScopeChoices { get; private set; } = ["All types"];

    public string DiagnosticComparisonSearchText
    {
        get => _diagnosticComparisonSearchText;
        set
        {
            if (SetProperty(ref _diagnosticComparisonSearchText, value ?? string.Empty))
                RefreshDiagnosticComparisonRows();
        }
    }

    public string SelectedDiagnosticComparisonEcu
    {
        get => _selectedDiagnosticComparisonEcu;
        set
        {
            if (SetProperty(ref _selectedDiagnosticComparisonEcu, value ?? "All ECUs"))
                RefreshDiagnosticComparisonRows();
        }
    }

    public string SelectedDiagnosticComparisonScope
    {
        get => _selectedDiagnosticComparisonScope;
        set
        {
            if (SetProperty(ref _selectedDiagnosticComparisonScope, value ?? "All types"))
                RefreshDiagnosticComparisonRows();
        }
    }
    public string DiagnosticComparisonVisibleSummary =>
        _diagnosticDefinitionComparison is null ? "No comparison loaded." :
        $"{DiagnosticComparisonRows.Count:N0} of {_diagnosticDefinitionComparison.Rows.Count:N0} definitions visible · " +
        "Export always includes every original row.";
    public DiagnosticDefinitionDiffRow? SelectedDiagnosticDiffRow
    {
        get => _selectedDiagnosticDiffRow;
        set
        {
            if (SetProperty(ref _selectedDiagnosticDiffRow, value))
                OnPropertyChanged(nameof(HasSelectedDiagnosticDiff));
        }
    }
    public bool HasSelectedDiagnosticDiff => SelectedDiagnosticDiffRow is not null;
    public bool HasDiagnosticComparison => _diagnosticDefinitionComparison is not null;
    public string DiagnosticComparisonSummary => _diagnosticDefinitionComparison?.Summary ??
        "Choose baseline and current diagnostic descriptions to compare.";
    public bool DiagnosticComparisonChangesOnly
    {
        get => _diagnosticComparisonChangesOnly;
        set
        {
            if (SetProperty(ref _diagnosticComparisonChangesOnly, value))
                RefreshDiagnosticComparisonRows();
        }
    }

    public IReadOnlyList<DbcDefinitionDiffRow> DbcComparisonRows { get; private set; } = [];
    public DbcDefinitionDiffRow? SelectedDbcComparisonDiffRow
    {
        get => _selectedDbcComparisonDiffRow;
        set
        {
            if (SetProperty(ref _selectedDbcComparisonDiffRow, value))
                OnPropertyChanged(nameof(HasSelectedDbcComparisonDiff));
        }
    }
    public bool HasSelectedDbcComparisonDiff => SelectedDbcComparisonDiffRow is not null;
    /// <summary>Default to actionable differences on large DBC catalogs;
    /// exports always include the full comparison regardless of this view filter.</summary>
    public bool DbcComparisonChangesOnly
    {
        get => _dbcComparisonChangesOnly;
        set
        {
            if (SetProperty(ref _dbcComparisonChangesOnly, value))
                RefreshDbcComparisonRows();
        }
    }
    public bool HasDbcComparison => _dbcDefinitionComparison is not null;
    public string DbcComparisonSummary => _dbcDefinitionComparison?.Summary ??
        "Choose baseline and current DBC files to compare their definitions.";

    public ObservableCollection<UnmatchedIdentifierRow> UnmatchedIdentifiers { get; } = [];
    public ObservableCollection<DbcMessageCatalogRow> DbcMessages { get; } = [];
    public ObservableCollection<DbcSignalCatalogRow> DbcSignals { get; } = [];
    public ObservableCollection<StatisticsRowViewModel> StatisticsRows { get; } = [];
    public ObservableCollection<TraceHealthRowViewModel> TraceHealthRows { get; } = [];
    public ObservableCollection<ChannelBusLoadRowViewModel> BusLoadRows { get; } = [];
    private readonly Dictionary<int, double> _channelBitrateKbps = [];
    public ObservableCollection<PresetLibraryRowViewModel> PresetLibraryEntries { get; } = [];
    public bool HasPresetLibraryEntries => PresetLibraryEntries.Count > 0;
    public ObservableCollection<WatchSignalRow> WatchSignals { get; } = [];
    public ObservableCollection<CompareTraceRow> CompareTraceRows { get; } = [];
    public ObservableCollection<CompareSignalRow> CompareSignalRows { get; } = [];
    public ObservableCollection<RegressionFinding> RegressionRows { get; } = [];
    private DiagnosticInsightSnapshot _diagnosticSnapshot = DiagnosticInsightSnapshot.Empty;
    private string _regressionSummary = "Compare a trace to list differences in identifiers, timing, signal status and diagnostics.";
    public string RegressionSummary
    {
        get => _regressionSummary;
        private set => SetProperty(ref _regressionSummary, value);
    }
    private CompareSignalRow? _selectedCompareSignalRow;
    public CompareSignalRow? SelectedCompareSignalRow
    {
        get => _selectedCompareSignalRow;
        set => SetProperty(ref _selectedCompareSignalRow, value);
    }

    private List<StatisticsRowViewModel> _allStatisticsRows = [];
    private string _compareSummary = "Load a second trace to compare frame counts and rates per ID.";

    public StatisticsRowViewModel? SelectedStatisticsRow
    {
        get => _selectedStatisticsRow;
        set => SetProperty(ref _selectedStatisticsRow, value);
    }

    /// <summary>Master-detail selection: the selected DBC scopes the message table.</summary>
    public DbcSignalInspectorViewModel DbcInspector { get; } = new();
    public DbcSignalEditorViewModel DbcEditor { get; } = new();
    public DbcValueExplorerViewModel DbcValues { get; } = new();

    /// <summary>Selecting a DBC signal is equivalent to opening its CANdb-style
    /// definition: physical bit map, byte order, scaling and symbolic raw meanings.</summary>
    public DbcSignalCatalogRow? SelectedDbcSignal
    {
        get => _selectedDbcSignal;
        set
        {
            if (SetProperty(ref _selectedDbcSignal, value))
                RefreshDbcInspector();
        }
    }

    public RelayCommand PlotSelectedDbcSignalCommand =>
        _plotSelectedDbcSignalCommand ??= new RelayCommand(() =>
        {
            if (_selectedDbcSignal is not { } selected) return;
            var idText = selected.Identifier.TrimEnd('x', 'X');
            if (!uint.TryParse(idText, System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out var wireId))
            {
                StatusText = "Selected DBC signal has an invalid CAN identifier.";
                return;
            }
            var series = Signals.EnsurePlotted(selected.Message, selected.Signal,
                channel: DbcInspector.SelectedPhysicalChannel, databaseName: selected.Database,
                identifier: wireId, isExtended: selected.Identifier.EndsWith("x", StringComparison.OrdinalIgnoreCase));
            if (series is null)
            {
                StatusText = "Select one source CAN channel in the DBC inspector if this signal appears on multiple buses, or load a trace/DBC first.";
                return;
            }
            SelectedNavIndex = 3;
        });

    private RelayCommand? _showRelatedDidsCommand;
    private RelayCommand? _showCorrelatedSignalInDbcCommand;
    private RelayCommand? _showDidSampleCorrelationsCommand;
    private RelayCommand? _showSelectedTraceFrameCorrelationsCommand;

    /// <summary>Raw trace frame → source-paired UDS/CAN evidence. A single
    /// recorded frame may participate in more than one correlation, so
    /// navigate to the shortlist and never assert a proven identity.</summary>
    public RelayCommand ShowSelectedTraceFrameCorrelationsCommand =>
        _showSelectedTraceFrameCorrelationsCommand ??= new RelayCommand(() =>
        {
            if (Trace.SelectedRow is not { FrameIndex: >= 0 } selected)
            {
                StatusText = "Select a recorded Trace frame before inspecting UDS↔CAN pairs.";
                return;
            }

            var frameIndex = selected.FrameIndex;
            var matches = Uds.CorrelationRows.Where(result =>
                result.HasSourcePairs && result.Points.Any(point =>
                    point.SignalFrameIndex == frameIndex ||
                    point.DidFrameIndex == frameIndex ||
                    point.DidResponseFrameIndices?.Contains(frameIndex) == true))
                .OrderByDescending(result => result.Confidence).ToArray();
            if (matches.Length == 0)
            {
                StatusText = $"Frame #{frameIndex + 1:N0} is not present in any " +
                    "source-paired UDS↔CAN hypothesis from this analysis. " +
                    "This does not imply the frame or its signal is invalid.";
                return;
            }

            var best = matches[0];
            if (!Uds.RevealCorrelation(best))
            {
                StatusText = "Source correlation is no longer in the active analysis. Refresh the loaded trace.";
                return;
            }
            var matchedPairIndex = CorrelationEvidenceNavigator.FindPairForFrame(
                best.Points, frameIndex);
            if (matchedPairIndex >= 0)
                Uds.SelectedCorrelationPairIndex = matchedPairIndex;
            Uds.SelectedViewIndex = 5;
            SelectedNavIndex = 8;
            StatusText = $"Frame #{frameIndex + 1:N0} appears in {matches.Length:N0} " +
                "recorded UDS↔CAN candidate(s). Inspect each DID/CAN source pair; " +
                "statistical association does not verify ECU signal identity.";
        });

    /// <summary>Recorded UDS DID field → correlated observed CAN sources.
    /// A DID number alone is not enough: preserve the decoded field and
    /// reconstructed endpoint label and require actual paired source frames.
    /// Show the shortlist, never claim the highest-scoring signal is proven.</summary>
    public RelayCommand ShowDidSampleCorrelationsCommand =>
        _showDidSampleCorrelationsCommand ??= new RelayCommand(() =>
        {
            if (Uds.SelectedDidSample is not { } sample)
            {
                StatusText = "Select a recorded DID field first.";
                return;
            }

            if (string.IsNullOrWhiteSpace(sample.Ecu) ||
                string.IsNullOrWhiteSpace(sample.Field))
            {
                StatusText = "This DID sample has no unambiguous endpoint/field context. " +
                    "Review its raw UDS response before comparing CAN signals.";
                return;
            }

            var matches = Uds.CorrelationRows.Where(result =>
                result.HasSourcePairs &&
                result.Candidate.Did.Did == sample.Did &&
                string.Equals(result.Candidate.Did.Ecu, sample.Ecu, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(result.ObservedField, sample.Field, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(result => result.Confidence).ToArray();
            if (matches.Length == 0)
            {
                StatusText = $"No source-paired CAN correlation for DID 0x{sample.Did:X4} / " +
                    $"{sample.Ecu} / {sample.Field}. This does not mean the signal is absent; " +
                    "inspect ISO-TP quality, loaded DBC and independent sample counts.";
                return;
            }

            var best = matches[0];
            if (!Uds.RevealCorrelation(best))
            {
                StatusText = "Selected DID correlation is no longer in the active analysis.";
                return;
            }
            // Focus the last exact response frame belonging to THIS selected
            // recorded DID field instead of arbitrarily opening pair #1.
            var frames = sample.Samples.Where(observation => observation.FrameIndex >= 0)
                .Select(observation => observation.FrameIndex).ToHashSet();
            var matchedPairIndex = CorrelationEvidenceNavigator.FindLastDidPair(
                best.Points, frames);
            if (matchedPairIndex >= 0)
                Uds.SelectedCorrelationPairIndex = matchedPairIndex;
            Uds.SelectedViewIndex = 5;
            SelectedNavIndex = 8;
            StatusText = $"{matches.Length:N0} source-paired CAN candidate(s) for " +
                $"DID 0x{sample.Did:X4} / {sample.Ecu} / {sample.Field}. " +
                "Review every paired frame before accepting an identity.";
        });

    private static string DbcIdentifierText(RbgCanInsight.Dbc.DbcMessage message) =>
        message.Identifier.ToString(message.IsExtended ? "X8" : "X3", System.Globalization.CultureInfo.InvariantCulture) +
        (message.IsExtended ? "x" : string.Empty);

    /// <summary>Only source-backed DID/CAN pairs from the *exact*
    /// DBC name, message ID, frame format and signal declaration are
    /// considered related. A selected physical CAN channel further scopes
    /// the results. A statistical fit alone is never a verified mapping.</summary>
    public IReadOnlyList<UdsCanCorrelationResult> RelatedDidCorrelations(DbcSignalCatalogRow signal)
    {
        var text = signal.Identifier.TrimEnd('x', 'X');
        if (!uint.TryParse(text, System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out var identifier))
            return [];

        var extended = signal.Identifier.EndsWith("x", StringComparison.OrdinalIgnoreCase);
        return Uds.CorrelationRows.Where(row =>
                CorrelationSourceIdentity.MatchesDefinition(
                    row, signal.Database, identifier, extended,
                    signal.Message, signal.Signal) &&
                CorrelationSourceIdentity.MatchesObservedChannel(
                    row, DbcInspector.SelectedPhysicalChannel))
            .OrderByDescending(row => row.Confidence).ToArray();
    }

    /// <summary>DBC signal → UDS correlations, preserving the physical CAN
    /// source. Never auto-select a winning bus among several matching buses.</summary>
    public RelayCommand ShowRelatedDidsCommand =>
        _showRelatedDidsCommand ??= new RelayCommand(() =>
        {
            if (_selectedDbcSignal is not { } selected)
            {
                StatusText = "Select a DBC signal first.";
                return;
            }

            var related = RelatedDidCorrelations(selected);
            if (related.Count == 0)
            {
                StatusText = $"No source-paired UDS↔CAN evidence for {selected.Database} / " +
                             $"{selected.Message}.{selected.Signal} on the selected CAN bus. " +
                             "This is not evidence that the ECU lacks a DID.";
                return;
            }

            var channels = related.Select(row => row.SignalChannel).Distinct().ToArray();
            if (channels.Length > 1 && DbcInspector.SelectedPhysicalChannel is null)
            {
                StatusText = $"Observed UDS↔CAN pairs span {channels.Length} CAN interfaces. " +
                    "Select the physical CAN channel in DBC → Recorded evidence first. " +
                    "No correlation source was guessed.";
                return;
            }

            if (!Uds.RevealCorrelation(related[0]))
            {
                StatusText = "Related DID correlation is no longer present in this trace analysis.";
                return;
            }
            SelectedNavIndex = 8;
            StatusText = $"{related.Count:N0} statistical DID candidate(s) for " +
                $"{selected.Database} / CAN {related[0].SignalChannel + 1} / " +
                $"{selected.Message}.{selected.Signal}: " +
                string.Join(", ", related.Take(8).Select(row => row.DidText)) +
                ". Source-frame pairs are inspectable; no automatic identity confirmation.";
        });

    /// <summary>UDS correlation → exact loaded DBC declaration. A DBC with
    /// the same visible signal name on another database is NOT interchangeable.
    /// A hidden catalog filter is cleared intentionally for navigation.</summary>
    public RelayCommand ShowCorrelatedSignalInDbcCommand =>
        _showCorrelatedSignalInDbcCommand ??= new RelayCommand(() =>
        {
            if (Uds.SelectedCorrelation is not { } correlation)
            {
                StatusText = "Select a UDS↔CAN correlation first.";
                return;
            }
            if (!correlation.HasSourcePairs)
            {
                StatusText = "This candidate has no paired, channel-identified CAN observations. " +
                    "Open its declared DBC source separately; a metadata match is not a recorded correlation.";
                return;
            }

            var sourceDatabases = _databases.Where(database =>
                string.Equals(database.Name, correlation.Candidate.Database,
                    StringComparison.OrdinalIgnoreCase)).ToArray();
            var displayDatabases = DbcDatabases.Where(database =>
                string.Equals(database.Name, correlation.Candidate.Database,
                    StringComparison.OrdinalIgnoreCase)).ToArray();
            if (sourceDatabases.Length != 1 || displayDatabases.Length != 1)
            {
                StatusText = $"Source DBC '{correlation.Candidate.Database}' is missing or ambiguous. " +
                    "Do not navigate by an identically named signal in another database.";
                return;
            }

            // The correlation was built using these actual immutable parsed
            // definitions. Refuse a stale source if DBC editing/reloading
            // has replaced them, even if names and numeric IDs still match.
            var sourceMatches = sourceDatabases[0].AllMessageDefinitions.Where(message =>
                ReferenceEquals(message, correlation.Candidate.Message) &&
                message.Signals.Count(signal => ReferenceEquals(signal,
                    correlation.Candidate.Signal)) == 1).ToArray();
            if (sourceMatches.Length != 1)
            {
                StatusText = "The correlation refers to a DBC snapshot that is no longer loaded. " +
                    "Rebuild the offline UDS↔CAN analysis before following this link.";
                return;
            }

            DbcCatalogSearchText = string.Empty;
            // Clearing a debounced search during an explicit source jump can
            // leave a scheduled callback that rebuilds the catalog AFTER
            // selecting the target. Cancel it before changing scope.
            _dbcSearchDebounce.Cancel();
            DbcObservedOnly = false;
            SelectedDbcDatabase = displayDatabases[0];
            SelectedDbcMessage = null;
            RebuildDbcMessageRows();

            var sourceMessage = sourceMatches[0];
            var selectedMessages = DbcMessages.Where(row =>
                string.Equals(row.Database, correlation.Candidate.Database, StringComparison.OrdinalIgnoreCase) &&
                row.Identifier == DbcIdentifierText(sourceMessage) &&
                row.Name == sourceMessage.Name).ToArray();
            if (selectedMessages.Length != 1)
            {
                StatusText = "Cannot uniquely display the source DBC message. " +
                    "Check competing definitions and the active catalog filters.";
                return;
            }

            SelectedDbcMessage = selectedMessages[0];
            var selectedSignals = DbcSignals.Where(row =>
                CorrelationSourceIdentity.MatchesDefinition(correlation,
                    row.Database, sourceMessage.Identifier, sourceMessage.IsExtended,
                    row.Message, row.Signal) &&
                row.Identifier == DbcIdentifierText(sourceMessage)).ToArray();
            if (selectedSignals.Length != 1)
            {
                StatusText = "Cannot uniquely locate this DBC signal in the source message.";
                return;
            }

            SelectedDbcSignal = selectedSignals[0];
            if (correlation.SignalChannel >= 0)
            {
                // Inspect the actual CAN frame used for the selected fit,
                // not the latest unrelated frame on an equally named bus.
                var preferred = Uds.SelectedCorrelationPoint;
                var paired = preferred is not null &&
                    preferred.SignalFrameIndex >= 0 &&
                    preferred.SignalFrameIndex < _framesSnapshot.Count &&
                    _framesSnapshot[preferred.SignalFrameIndex].Channel == correlation.SignalChannel
                        ? preferred
                        : correlation.Points.FirstOrDefault(point =>
                            point.SignalFrameIndex >= 0 &&
                            point.SignalFrameIndex < _framesSnapshot.Count &&
                            _framesSnapshot[point.SignalFrameIndex].Channel == correlation.SignalChannel);
                if (paired is not null)
                    DbcInspector.PinSourceFrame(paired.SignalFrameIndex);
                if (!DbcInspector.TrySelectObservedChannel(correlation.SignalChannel))
                {
                    StatusText = "DBC definition opened, but the original correlation CAN " +
                        "channel is no longer present in the decoded trace. Reload the source evidence.";
                    SelectedNavIndex = 2;
                    return;
                }
            }

            SelectedNavIndex = 2;
            StatusText = $"Opened the exact source DBC '{correlation.Candidate.Database}' / " +
                $"{correlation.SignalText}" +
                (correlation.SignalChannel >= 0 ? $" / CAN {correlation.SignalChannel + 1}" : "") +
                $" for DID {correlation.DidText}. Statistical agreement is not a confirmed mapping.";
        });
    public RelayCommand OpenSelectedDbcSignalFrameCommand =>
        _openSelectedDbcSignalFrameCommand ??= new RelayCommand(() =>
        {
            if (DbcInspector.SourceFrameIndex < 0)
            {
                StatusText = "Select a CAN channel with decoded samples in the DBC inspector first.";
                return;
            }
            SelectedNavIndex = 1;
            Trace.JumpToFrameIndex(DbcInspector.SourceFrameIndex);
        });

    private void RefreshDbcInspector()
    {
        var selected = _selectedDbcSignal;
        var database = _databases.FirstOrDefault(item => selected is not null &&
            string.Equals(item.Name, selected.Database, StringComparison.OrdinalIgnoreCase));
        var message = database?.AllMessageDefinitions.FirstOrDefault(item =>
            selected is not null &&
            string.Equals(item.Identifier.ToString(item.IsExtended ? "X8" : "X3",
                System.Globalization.CultureInfo.InvariantCulture) + (item.IsExtended ? "x" : string.Empty),
                selected.Identifier, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(item.Name, selected.Message, StringComparison.Ordinal));
        var signal = message?.Signals.FirstOrDefault(item =>
            string.Equals(item.Name, selected?.Signal, StringComparison.Ordinal));
        DbcInspector.Inspect(database, message, signal, _timelines, _decoders, _framesSnapshot);
        DbcValues.SetSource(database, message, signal, _timelines, _framesSnapshot);
        DbcEditor.Select(database, message, signal,
            database is not null && _databasePaths.TryGetValue(database.Name, out var sourcePath)
                ? sourcePath : null);
    }

    public DbcDatabaseRowViewModel? SelectedDbcDatabase
    {
        get => _selectedDbcDatabase;
        set
        {
            if (SetProperty(ref _selectedDbcDatabase, value))
            {
                _selectedDbcMessage = null;
                OnPropertyChanged(nameof(SelectedDbcMessage));
                RebuildDbcMessageRows();
                RebuildDbcSignalRows();
                OnPropertyChanged(nameof(DbcDetailScopeText));
            }
        }
    }

    /// <summary>Master-detail selection: the selected message scopes the signal table.</summary>
    public DbcMessageCatalogRow? SelectedDbcMessage
    {
        get => _selectedDbcMessage;
        set
        {
            if (SetProperty(ref _selectedDbcMessage, value))
            {
                RebuildDbcSignalRows();
                OnPropertyChanged(nameof(DbcDetailScopeText));
            }
        }
    }

    public string DbcDetailScopeText => _selectedDbcMessage is { } message
        ? $"Signals of {message.Name}"
        : _selectedDbcDatabase is { } database
            ? $"Signals of {database.FileName}"
            : "All signals";

    public string StatisticsSearchText
    {
        get => _statisticsSearchText;
        set
        {
            if (SetProperty(ref _statisticsSearchText, value))
            {
                RefreshStatisticsRows();
            }
        }
    }

    public string StatisticsSummary => _statistics.Count == 0
        ? "Load a trace to compute per-ID statistics."
        : $"{_statistics.Count:N0} bus/ID rows · period, rate and payload length per identifier";

    /// <summary>First frame timestamp, for positioning incidents on the trace health timeline.</summary>
    public long TraceHealthTimelineStartNanoseconds => _frames.Count == 0 ? 0 : _frames[0].TimestampNanoseconds;

    /// <summary>Last frame timestamp, for positioning incidents on the trace health timeline.</summary>
    public long TraceHealthTimelineEndNanoseconds => _frames.Count == 0 ? 0 : _frames[^1].TimestampNanoseconds;

    public string TraceHealthSummary
    {
        get
        {
            if (TraceHealthRows.Count == 0)
            {
                return "No timing incidents detected across the trace.";
            }

            var critical = TraceHealthRows.Count(row => row.Severity == "Crit");
            var high = TraceHealthRows.Count(row => row.Severity == "High");
            var missing = TraceHealthRows.Sum(row => row.EstimatedMissingFrames);
            var spanText = _frames.Count == 0
                ? string.Empty
                : $" across {(_frames[^1].TimestampNanoseconds - _frames[0].TimestampNanoseconds) / 1_000_000_000d:F1}s";
            var severityText = critical + high == 0 ? string.Empty : $" · {critical:N0} critical, {high:N0} high severity";
            return $"{TraceHealthRows.Count:N0} timing incident(s){spanText}{severityText} · ~{missing:N0} estimated missing frame(s)";
        }
    }

    public string CompareSummary
    {
        get => _compareSummary;
        private set => SetProperty(ref _compareSummary, value);
    }

    public RelayCommand<object> NavigateCommand => _navigateCommand ??= new RelayCommand<object>(parameter =>
    {
        if (parameter is int index)
        {
            SelectedNavIndex = index;
        }
        else if (parameter is string text && int.TryParse(text, out var parsed))
        {
            SelectedNavIndex = parsed;
        }
    });

    /// <summary>From a selected raw Trace frame, navigate to the exact matching
    /// DBC message and expose its bits, value tables and signals. A single
    /// unambiguous decoder must be active on this physical CAN interface.</summary>
    public RelayCommand InspectSelectedTraceDbcCommand =>
        _inspectSelectedTraceDbcCommand ??= new RelayCommand(() =>
        {
            if (Trace.SelectedRow is not { } selected)
            {
                StatusText = "Select a CAN trace frame first.";
                return;
            }
            var sources = _decoders.Select(decoder =>
            {
                var matches = decoder.TryGetMessage(selected.Frame, out var message);
                return (Decoder: decoder, Message: matches ? message : null);
            }).Where(result => result.Message is not null).ToArray();
            if (sources.Length != 1)
            {
                StatusText = sources.Length == 0
                    ? "No loaded DBC describes this frame on the current CAN channel. Load a matching DBC or check bus assignment."
                    : $"{sources.Length} DBC definitions match this frame. Set the correct CAN channel per DBC in DBC Manager before inspecting.";
                SelectedNavIndex = 2;
                return;
            }

            var source = sources[0];
            SelectedDbcDatabase = DbcDatabases.FirstOrDefault(row =>
                string.Equals(row.Name, source.Decoder.Database.Name, StringComparison.OrdinalIgnoreCase));
            var identifier = source.Message!.Identifier.ToString(source.Message.IsExtended ? "X8" : "X3",
                System.Globalization.CultureInfo.InvariantCulture) +
                (source.Message.IsExtended ? "x" : string.Empty);
            SelectedDbcMessage = DbcMessages.FirstOrDefault(row =>
                row.Database == source.Decoder.Database.Name &&
                row.Identifier == identifier && row.Name == source.Message.Name);
            SelectedDbcSignal = DbcSignals.FirstOrDefault();
            DbcInspector.PinSourceFrame(selected.FrameIndex);
            SelectedNavIndex = 2;
            StatusText = $"DBC: {source.Decoder.Database.Name} / {source.Message.Name} · CAN {selected.Frame.Channel + 1}. Inspect the recorded 0/1 bit values and choose any signal.";
        });

    /// <summary>Trace → UDS investigation with the matched ISO-TP request, response,
    /// quality, decoded fields and raw source-frame links already selected.</summary>
    public RelayCommand InspectSelectedTraceUdsCommand =>
        _inspectSelectedTraceUdsCommand ??= new RelayCommand(() =>
        {
            if (Trace.SelectedRow is not { } selected)
            {
                StatusText = "Select a frame in Trace first.";
                return;
            }
            if (!Uds.SelectTransactionContainingFrame(selected.FrameIndex))
            {
                StatusText = "No reconstructed UDS transaction includes this frame. Try the UDS quick filter, or inspect its decoded CAN signals.";
                return;
            }
            SelectedNavIndex = 8;
            StatusText = $"UDS transaction found for raw CAN frame {selected.FrameIndex + 1:N0}. Double-check the endpoint and quality evidence.";
        });

    /// <summary>Go straight to offline UDS problems rather than forcing analysts
    /// to navigate two drop-down filters by hand.</summary>
    public RelayCommand ShowUdsProblemsCommand =>
        _showUdsProblemsCommand ??= new RelayCommand(() =>
        {
            Uds.SelectedViewIndex = 1;
            Uds.SearchText = string.Empty;
            Uds.FilterIndex = 1; // Problems
            SelectedNavIndex = 8;
        });

    /// <summary>Dashboard tasks describe a user goal, not an internal workspace.
    /// Missing prerequisites are explained and the raw capture remains unchanged.</summary>
    public RelayCommand<string> StartInvestigationCommand =>
        _startInvestigationCommand ??= new RelayCommand<string>(goal =>
        {
            if (!IsTraceLoaded)
            {
                SelectedNavIndex = 0;
                StatusText = "First open a recorded CAN trace (BLF, ASC, TRC, CSV or MDF4).";
                return;
            }

            switch (goal?.ToLowerInvariant())
            {
                case "uds":
                    ShowUdsProblemsCommand.Execute(null);
                    break;
                case "changes":
                    Trace.QuickAnalysisCommand.Execute("changes");
                    SelectedNavIndex = 1;
                    break;
                case "unmatched":
                    if (_decoders.Count == 0)
                    {
                        SelectedNavIndex = 2;
                        StatusText = "Load a DBC/ARXML database first. Then select No DBC in the Trace focus bar.";
                        break;
                    }
                    Trace.QuickAnalysisCommand.Execute("unmatched");
                    SelectedNavIndex = 1;
                    break;
                case "signals":
                    if (_timelines.Count == 0)
                    {
                        SelectedNavIndex = 2;
                        StatusText = "Decode a trace with a DBC first; plot and watch controls will appear in Signals.";
                        break;
                    }
                    SelectedNavIndex = 3;
                    break;
                case "mapping":
                    SelectedNavIndex = 7;
                    break;
                default:
                    SelectedNavIndex = 1;
                    break;
            }
        });

    /// <summary>Cross-navigation used by context menus in Change Matrix, Byte Matrix, Statistics,
    /// Mapping and the DBC Manager: filter the Trace view by a hex CAN identifier and switch to it,
    /// without the user having to re-type or re-search for the ID by hand.</summary>
    public RelayCommand<string> FilterTraceByIdentifierCommand => _filterTraceByIdentifierCommand ??= new RelayCommand<string>(identifier =>
    {
        if (string.IsNullOrWhiteSpace(identifier))
        {
            return;
        }

        Trace.SearchText = identifier.TrimEnd('x', 'X');
        Trace.ApplyFilterCommand.Execute(null);
        SelectedNavIndex = 1;
    });

    /// <summary>
    /// Universal search (Ctrl+K) across every catalog the app already builds — decoded signals,
    /// DBC messages, trace identifiers and DIDs — so the user does not have to know in advance
    /// which of the eight workspaces holds what they are looking for.
    /// </summary>
    public IReadOnlyList<GlobalSearchResult> SearchGlobal(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        var term = query.Trim();
        var results = new List<GlobalSearchResult>();

        static bool Matches(string term, params string?[] fields) =>
            fields.Any(field => !string.IsNullOrEmpty(field) && field.Contains(term, StringComparison.OrdinalIgnoreCase));

        // Find ALL recorded series, including values that never change. The
        // Change Matrix is not a complete observed-signal catalog, and
        // identical signal names on distinct CAN channels must remain separate
        // hits with explicit physical source identity.
        // One linear scan with bounded result buckets, not OrderBy() of
        // every matching signal. A large DBC can expose tens of thousands
        // of series and this query runs on every typed character.
        // Rank exact name, prefix and other match; prefer already plotted
        // signals within each category. Preserve source catalog order
        // (including CAN channel) for ties.
        var buckets = Enumerable.Range(0, 6)
            .Select(_ => new List<PlotSeriesViewModel>(35)).ToArray();
        foreach (var series in Signals.AllSeries)
        {
            if (!Matches(term, series.SignalName, series.MessageName,
                    series.Transmitter, series.DatabaseName, series.ChannelText,
                    series.IdentifierHex, "0x" + series.IdentifierHex))
                continue;

            var exact = string.Equals(series.SignalName, term, StringComparison.OrdinalIgnoreCase);
            var prefix = !exact && series.SignalName.StartsWith(
                term, StringComparison.OrdinalIgnoreCase);
            var bucket = (exact ? 0 : prefix ? 2 : 4) + (series.IsPlotted ? 0 : 1);
            if (buckets[bucket].Count < 35)
                buckets[bucket].Add(series);
        }
        var observedMatches = buckets.SelectMany(items => items).Take(35);
        foreach (var series in observedMatches)
        {
            results.Add(new GlobalSearchResult(
                "Recorded signal",
                series.SignalName,
                $"{series.DatabaseName} · {series.ChannelText} · {series.MessageName} · " +
                $"0x{series.IdentifierHex} · {series.SampleCount:N0} sample(s)",
                () =>
                {
                    // A result is already tied to one immutable timeline.
                    // Do not guess a network/message identity by display name
                    // or alter the user's other plotted curves.
                    Signals.ChangingOnly = false;
                    Signals.PlottedOnly = false;
                    Signals.SearchText = series.SignalName;
                    Signals.SelectedSeries = series;
                    series.IsPlotted = true;
                    SelectedNavIndex = 3;
                    if (series.Samples.Count > 0)
                        Signals.SeekToTimestamp(series.Samples[^1].TimestampNanoseconds);
                    StatusText = $"Showing {series.DatabaseName}/{series.ChannelText} " +
                        $"{series.MessageName}.{series.SignalName} from recorded CAN evidence.";
                }));
        }

        // Matrix transitions are a separate navigation result, not another
        // anonymous copy of a recorded signal. The matrix is useful when the
        // analyst wants the change history rather than the time plot.
        foreach (var row in Matrix.Rows.Where(row => Matches(term,
            row.SignalName, row.MessageName, row.Ecu, row.IdentifierHex)).Take(12))
        {
            results.Add(new GlobalSearchResult(
                "Change history",
                row.SignalName,
                $"{row.Ecu} · {row.MessageName} · ID {row.IdentifierHex}",
                () =>
                {
                    Matrix.SelectedRow = row;
                    SelectedNavIndex = 4;
                }));
        }

        foreach (var row in DbcMessages.Where(row => Matches(term, row.Name, row.Identifier, row.Database)).Take(15))
        {
            results.Add(new GlobalSearchResult(
                "DBC Message",
                row.Name,
                $"{row.Database} · ID {row.Identifier}",
                () =>
                {
                    SelectedDbcMessage = row;
                    SelectedNavIndex = 2;
                }));
        }

        foreach (var row in StatisticsRows.Where(row => Matches(term, row.MessageName, row.Identifier, row.Ecu)).Take(15))
        {
            results.Add(new GlobalSearchResult(
                "Trace ID",
                string.IsNullOrEmpty(row.MessageName) ? $"ID {row.Identifier}" : row.MessageName,
                $"{row.Ecu} · ID {row.Identifier} · {row.FrameCount:N0} frame(s)",
                () =>
                {
                    SelectedStatisticsRow = row;
                    SelectedNavIndex = 6;
                }));
        }

        foreach (var row in Mapping.Rows.Where(row => Matches(term, row.Name, row.DidHex, row.Description, row.Ecu)).Take(15))
        {
            results.Add(new GlobalSearchResult(
                "DID",
                row.Name,
                $"{row.Ecu} · DID {row.DidHex}",
                () =>
                {
                    Mapping.SelectedRow = row;
                    SelectedNavIndex = 7;
                }));
        }

        return results;
    }

    public RelayCommand<string> OpenStatisticsRowCommand => _openStatisticsRowCommand ??= new RelayCommand<string>(target =>
    {
        if (SelectedStatisticsRow is null)
        {
            StatusText = "Select a statistics row first.";
            return;
        }

        var matchIndex = -1;
        if (string.Equals(target, "last", StringComparison.OrdinalIgnoreCase))
        {
            for (var index = _frames.Count - 1; index >= 0; index--)
            {
                if (MatchesStatisticsRow(_frames[index], SelectedStatisticsRow))
                {
                    matchIndex = index;
                    break;
                }
            }
        }
        else
        {
            for (var index = 0; index < _frames.Count; index++)
            {
                if (MatchesStatisticsRow(_frames[index], SelectedStatisticsRow))
                {
                    matchIndex = index;
                    break;
                }
            }
        }

        if (matchIndex < 0)
        {
            StatusText = "No frame found for the selected statistics row.";
            return;
        }

        SelectedNavIndex = 1;
        Trace.JumpToFrameIndex(matchIndex);
    });

    public void OpenTraceHealthRow(TraceHealthRowViewModel? row)
    {
        if (row is null)
        {
            StatusText = "Select a trace health row first.";
            return;
        }

        if (row.FrameIndex < 0 || row.FrameIndex >= _frames.Count)
        {
            StatusText = "No frame found for the selected timing incident.";
            return;
        }

        SelectedNavIndex = 1;
        Trace.JumpToFrameIndex(row.FrameIndex);
        StatusText = $"Opened timing incident for {row.Identifier} at t={row.TimeText}s.";
    }

    private static bool MatchesStatisticsRow(CanFrame frame, StatisticsRowViewModel row) =>
        frame.Channel == row.RawChannel &&
        frame.Identifier == row.RawIdentifier &&
        frame.IsExtended == row.IsExtended;

    public string AppTitle => "RBG CAN Insight";
    public string AppTagline => "Offline CAN trace analysis studio";
    public string DesignerCredit => "Designed by Paolo Rubagotti";
    public IReadOnlyList<string> ThemeChoices { get; } = ["Dark", "Light", "High Contrast"];

    public int ThemeIndex
    {
        get => _themeIndex;
        set => SetProperty(ref _themeIndex, Math.Clamp(value, 0, ThemeChoices.Count - 1));
    }

    public int SelectedNavIndex
    {
        get => _selectedNavIndex;
        set
        {
            if (SetProperty(ref _selectedNavIndex, value))
            {
                // Opening another page drops an old failure/prompt that no longer applies to it.
                ClearStaleStatus(TimeSpan.FromSeconds(2));
                if (value == 3 && Signals.PlotWindowEndNanoseconds >= 0)
                    PlotCursorNanoseconds = Signals.PlotWindowEndNanoseconds;
            }
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    public string BusyText
    {
        get => _busyText;
        private set => SetProperty(ref _busyText, value);
    }

    /// <summary>Determinate progress (0-100) for the busy indicator's current phase, or null while
    /// a phase has no meaningful percent (shown as an indeterminate bar instead).</summary>
    public double? BusyProgressPercent
    {
        get => _busyProgressPercent;
        private set => SetProperty(ref _busyProgressPercent, value);
    }

    public string StatusText
    {
        get => _statusText;
        set
        {
            if (!SetProperty(ref _statusText, value))
                return;
            _statusSetUtc = DateTime.UtcNow;
            RestartStatusExpiry();
        }
    }

    private DateTime _statusSetUtc = DateTime.UtcNow;
    private System.Windows.Threading.DispatcherTimer? _statusExpiryTimer;
    private bool _disposed;

    /// <summary>How long a one-off status message stays in the status bar before it is replaced by the neutral summary.</summary>
    public static readonly TimeSpan StatusLifetime = TimeSpan.FromSeconds(20);

    /// <summary>A neutral status for the current state: what is loaded, not the outcome of some earlier action.</summary>
    public string BaselineStatusText => _frames.Count == 0
        ? "Open a CAN trace to start."
        : $"{_frames.Count:N0} frames loaded · {DbcSummary}";

    /// <summary>Replaces a status message that no longer describes the current action (an old failure,
    /// "select two files..." and so on) with the neutral summary. Messages younger than
    /// <paramref name="minimumAge"/> are kept so the status written by the action that is still
    /// completing is never wiped.</summary>
    public void ClearStaleStatus(TimeSpan? minimumAge = null)
    {
        if (IsBusy || string.Equals(_statusText, BaselineStatusText, StringComparison.Ordinal))
            return;
        if (DateTime.UtcNow - _statusSetUtc < (minimumAge ?? TimeSpan.Zero))
            return;
        StatusText = BaselineStatusText;
    }

    private void RestartStatusExpiry()
    {
        var timer = _statusExpiryTimer;
        if (timer is null || _disposed)
            return;
        // StatusText can be set from background work; DispatcherTimer must only be touched on its own dispatcher.
        if (!timer.Dispatcher.CheckAccess())
        {
            timer.Dispatcher.BeginInvoke(new Action(RestartStatusExpiry));
            return;
        }

        timer.Stop();
        timer.Start();
    }

    private void OnStatusExpiryTick(object? sender, EventArgs e)
    {
        _statusExpiryTimer?.Stop();
        if (!_disposed)
            ClearStaleStatus();
    }

    /// <summary>Stops the status-expiry timer so it cannot tick after the window is closed.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_statusExpiryTimer is { } timer)
        {
            _statusExpiryTimer = null;
            timer.Tick -= OnStatusExpiryTick;
            if (timer.Dispatcher.CheckAccess())
                timer.Stop();
            else
                timer.Dispatcher.BeginInvoke(new Action(timer.Stop));
        }
    }

    public string TraceName
    {
        get => _traceName;
        private set => SetProperty(ref _traceName, value);
    }

    public string DbcCatalogSearchText
    {
        get => _dbcCatalogSearchText;
        set
        {
            if (SetProperty(ref _dbcCatalogSearchText, value))
            {
                // Debounced: a burst of keystrokes rebuilds the catalog tables once instead of
                // once per character — matters on a database with thousands of signals. Search
                // only refilters the catalog tables; the database list stays untouched.
                _dbcSearchDebounce.Trigger(() =>
                {
                    RebuildDbcMessageRows();
                    RebuildDbcSignalRows();
                });
            }
        }
    }

    /// <summary>Hide unobserved database definitions in the catalog without
    /// removing them from the loaded DBC or making inferred evidence claims.</summary>
    public bool DbcObservedOnly
    {
        get => _dbcObservedOnly;
        set
        {
            if (SetProperty(ref _dbcObservedOnly, value))
            {
                RebuildDbcMessageRows();
                RebuildDbcSignalRows();
            }
        }
    }

    public bool IsTraceLoaded => _frames.Count > 0;
    public int FrameCount => _frames.Count;
    public IReadOnlyList<CanFrame> Frames => _framesSnapshot;
    /// <summary>The original physical logging source for the loaded frame
    /// snapshot. In a combined session use CurrentTraceSourceIndex instead.</summary>
    public string CurrentTracePhysicalPath => _activeTraceWorkspacePath ?? _tracePath;
    public TraceSessionSourceIndex? CurrentTraceSourceIndex => _traceSourceIndex;
    /// <summary>Physical identity of a frame from the current immutable import.
    /// A validated single-file import is Logger 1 even without a merged-session
    /// source index. Invalid frame indices never become valid source evidence.</summary>
    public int CurrentFrameSourceAt(int frameIndex)
    {
        if ((uint)frameIndex >= (uint)_framesSnapshot.Count) return -1;
        return _traceSourceIndex is { } index
            ? index.SourceAt(frameIndex)
            : !string.IsNullOrWhiteSpace(CurrentTracePhysicalPath) ? 0 : -1;
    }

    private static Func<int, int>? BuildAnalysisSourceAt(
        IReadOnlyList<CanFrame> frames,
        TraceSessionSourceIndex? sourceIndex,
        string singleSourcePath)
    {
        if (sourceIndex is not null) return sourceIndex.SourceAt;
        if (frames.Count == 0 || string.IsNullOrWhiteSpace(singleSourcePath)) return null;
        var count = frames.Count;
        return frameIndex => (uint)frameIndex < (uint)count ? 0 : -1;
    }
    /// <summary>LIN / FlexRay / CAN XL records found in the loaded trace (empty for CAN-only files).</summary>
    public BusFrameCollection BusFrames { get; private set; } = new();
    public TraceImportHealthSnapshot? ImportHealth => _traceImportHealth;
    public string ImportHealthSummary => _traceImportHealth?.Summary ??
        "Open a CAN trace to inspect import completeness, rejected records and per-bus evidence.";
    public IReadOnlyList<TraceImportChannelRow> ImportChannelRows =>
        _traceImportHealth?.Channels.Select(channel => new TraceImportChannelRow(channel)).ToArray() ?? [];
    public IReadOnlyList<TraceImportIssue> ImportIssueRows =>
        _traceImportHealth?.IssueExamples ?? [];
    public IReadOnlyList<DbcDatabase> Databases => _databases;
    public IReadOnlyList<MessageStatisticsRow> Statistics => _statistics;
    public IReadOnlyList<SignalChangeReport> Reports => _reports;

    public string TraceSummary
    {
        get
        {
            if (!IsTraceLoaded)
            {
                return "No trace loaded";
            }

            var duration = (_frames[^1].TimestampNanoseconds - _frames[0].TimestampNanoseconds) / 1_000_000_000d;
            // ImportHealth is an immutable snapshot calculated during ingestion.
            // Do not re-enumerate 10M frames every time a status/summary binding
            // repaints on the WPF UI thread.
            var channels = _traceImportHealth?.Channels.Count ??
                _frames.Select(frame => frame.Channel).Distinct().Count();
            var ids = _statistics.Count;
            var errorFrames = _traceImportHealth is { } health
                ? health.Channels.Sum(row => row.ErrorFrames)
                : _frames.LongCount(frame => frame.IsError);
            var timingWarnings = _statistics.Count(HasTimingWarning);
            var anomalousSignals = _reports.Count(report => report.Activity == SignalActivity.Anomalous);
            var missingSignals = _reports.Count(report => report.Activity == SignalActivity.Missing);
            var decoded = _databases.Count == 0
                ? "no database"
                : $"{_decodedFrameCount * 100d / Math.Max(1, FrameCount):F1}% decoded";
            var warnings = new List<string>();
            if (errorFrames > 0)
            {
                warnings.Add($"{errorFrames:N0} error frame(s)");
            }

            if (timingWarnings > 0)
            {
                warnings.Add($"{timingWarnings:N0} timing gap(s)");
            }

            if (anomalousSignals > 0)
            {
                warnings.Add($"{anomalousSignals:N0} out-of-range signal(s)");
            }

            if (missingSignals > 0)
            {
                warnings.Add($"{missingSignals:N0} missing signal(s)");
            }

            if (_traceImportHealth?.RequiresReview == true)
                warnings.Add($"IMPORT REVIEW: {_traceImportHealth.RejectedRecords:N0} rejected records");

            var warningText = warnings.Count == 0 ? "no aggregate anomalies" : string.Join(" · ", warnings);
            return $"{FrameCount:N0} frames · {duration:F1} s · {channels} channel(s) · {ids:N0} IDs · {decoded} · {warningText}";
        }
    }

    private static bool HasTimingWarning(MessageStatisticsRow row) =>
        row.FrameCount >= 3 &&
        row.AveragePeriodMilliseconds > 0 &&
        row.MaxPeriodMilliseconds > row.AveragePeriodMilliseconds * 2.5;

    public string DbcSummary => _databases.Count == 0
        ? "No DBC loaded"
        : $"{_databases.Count} DBC · {_databases.Sum(db => db.MessageDefinitionCount):N0} messages · {_databases.Sum(db => db.AllSignals.Count()):N0} signals";

    public string DecodeCoverageSummary
    {
        get
        {
            if (_frames.Count == 0)
            {
                return "Load a trace to check DBC coverage.";
            }

            if (_databases.Count == 0)
            {
                return "Load one or more DBC files to interpret CAN frames.";
            }

            var framePercent = _frames.Count == 0 ? 0 : _decodedFrameCount * 100d / _frames.Count;
            var idCount = _frames.Select(frame => frame.Identifier).Distinct().Count();
            var idPercent = idCount == 0 ? 0 : _decodedIdentifierCount * 100d / idCount;
            var autoResolvedSuffix = _lastAutoResolvedUnmatchedCount > 0
                ? $" · auto-resolved {_lastAutoResolvedUnmatchedCount:N0} unmatched ID(s) — see DBC options"
                : string.Empty;
            return $"{_decodedFrameCount:N0}/{_frames.Count:N0} frames interpreted ({framePercent:F1}%) · " +
                   $"{_decodedIdentifierCount:N0}/{idCount:N0} IDs matched ({idPercent:F1}%) · " +
                   $"{_decodedSampleCount:N0} signal samples{autoResolvedSuffix}";
        }
    }

    /// <summary>Top signals by change count for the dashboard "top movers" card.</summary>
    public IReadOnlyList<MatrixRowViewModel> TopMovers =>
        _reports
            .Where(report => report.ChangeCount > 0)
            .OrderByDescending(report => report.ChangeCount)
            .Take(8)
            .Select(report => new MatrixRowViewModel(report))
            .ToArray();

    /// <summary>Trace-plot sync cursor (set from the trace grid selection).</summary>
    public long PlotCursorNanoseconds
    {
        get => _plotCursorNs;
        set
        {
            if (SetProperty(ref _plotCursorNs, value))
            {
                // Keep the chart time window aligned with Trace/Quick Plot
                // selection even while Signals & Plot is not the active tab.
                // The Signals setter rejects times outside observed data.
                Signals.TryFollowSharedCursor(value);
                RefreshWatchSignals();
            }
        }
    }

    /// <summary>Source files (trace(s), DBC(s), catalogs, mapping) currently loaded, for a configuration's file references.
    /// Reads and hashes files: call off the UI thread.</summary>
    public IReadOnlyList<NativeFileReference> CaptureConfigurationSources(string configurationDirectory)
    {
        var references = new List<NativeFileReference>();
        void Add(string kind, string? path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
            try { references.Add(NativeSourceReferenceChecker.Describe(kind, path, configurationDirectory)); }
            catch (IOException) { /* a file that cannot be read now simply has no reference */ }
        }
        var traces = _traceWorkspacePaths.Count > 0 ? _traceWorkspacePaths.ToArray() : [_tracePath];
        foreach (var path in traces.Distinct(StringComparer.OrdinalIgnoreCase)) Add(NativeSourceReferenceChecker.KindTrace, path);
        foreach (var path in _databasePaths.Values.Distinct(StringComparer.OrdinalIgnoreCase)) Add(NativeSourceReferenceChecker.KindDbc, path);
        foreach (var path in Mapping.CatalogPaths.Distinct(StringComparer.OrdinalIgnoreCase)) Add(NativeSourceReferenceChecker.KindCatalog, path);
        Add(NativeSourceReferenceChecker.KindMapping, Mapping.LastMapPath);
        return references;
    }

    /// <summary>Filters, frame markers, shared cursor and active view as they are now (UI-thread state, cheap).</summary>
    public NativeAnalysisExtras CaptureConfigurationState(IReadOnlyList<NativeFileReference> sources, out int markersOmitted)
    {
        var markers = Trace.MarkedFrames
            .Select(marker => new NativeMarker(marker.TimestampNanoseconds, marker.Category.ToString()))
            .ToArray();
        markersOmitted = Math.Max(0, markers.Length - 5000);
        return new NativeAnalysisExtras(
            Sources: sources,
            Filters: new NativeTraceFilters(
                string.IsNullOrEmpty(Trace.SearchText) ? null : Trace.SearchText,
                string.IsNullOrEmpty(Trace.RuleText) ? null : Trace.RuleText,
                string.IsNullOrEmpty(Signals.SearchText) ? null : Signals.SearchText),
            Markers: markers.Take(5000).ToArray(),
            Cursors: PlotCursorNanoseconds > 0 ? new NativeCursors(PlotCursorNanoseconds) : null,
            View: new NativeViewState(null, SelectedNavIndex, null));
    }

    /// <summary>Checks the saved source files against the files on disk / already open and returns one line per problem.
    /// Reads and hashes files: call off the UI thread. A modified or missing file is reported, never silently ignored.</summary>
    public IReadOnlyList<NativeFileCheck> CheckConfigurationSources(NativeAnalysisExtras extras, string configurationDirectory)
    {
        var open = new List<string>(_databasePaths.Values);
        open.AddRange(_traceWorkspacePaths);
        if (!string.IsNullOrWhiteSpace(_tracePath)) open.Add(_tracePath);
        open.AddRange(Mapping.CatalogPaths);
        return NativeSourceReferenceChecker.Check(extras.SourceList, [configurationDirectory], open);
    }

    /// <summary>Applies the saved filters, markers and shared cursor to the loaded recording. Filters are applied as saved;
    /// markers only where a frame with exactly that timestamp exists. Returns a short summary for the status line.</summary>
    public string ApplyConfigurationState(NativeAnalysisExtras extras)
    {
        var parts = new List<string>();
        if (extras.Filters is { } filters)
        {
            if (filters.TraceSearchText is not null) Trace.SearchText = filters.TraceSearchText;
            if (filters.TraceRuleText is not null) Trace.RuleText = filters.TraceRuleText;
            if (filters.SignalsSearchText is not null) Signals.SearchText = filters.SignalsSearchText;
            parts.Add("filters restored");
        }
        if (extras.MarkerList.Count > 0)
        {
            var wanted = extras.MarkerList
                .Select(marker => Enum.TryParse<MarkerCategory>(marker.Category, out var category)
                    ? (marker.TimestampNanoseconds, category)
                    : ((long, MarkerCategory)?)null)
                .Where(item => item is not null).Select(item => item!.Value).ToArray();
            var applied = Trace.RestoreMarkers(wanted);
            parts.Add($"{applied:N0} of {extras.MarkerList.Count:N0} marker(s) restored" +
                (applied < extras.MarkerList.Count ? " (the rest have no frame at that exact time in this recording)" : string.Empty));
        }
        if (extras.Cursors?.CursorANanoseconds is { } cursor && cursor > 0)
        {
            PlotCursorNanoseconds = cursor;
            parts.Add("cursor restored");
        }
        return string.Join("; ", parts);
    }

    public AsyncCommand OpenTraceCommand => _openTraceCommand ??= new AsyncCommand(async () =>
    {
        ClearStaleStatus();
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = TraceImporter.FileDialogFilter,
            Title = "Open CAN trace (select several files to open them as one session)",
            Multiselect = true
        };
        if (dialog.ShowDialog() == true)
        {
            if (dialog.FileNames.Length > 1)
            {
                if (TraceMergeModeDialog.Ask(System.Windows.Application.Current?.MainWindow, dialog.FileNames.Length) is { } mode)
                    await LoadTraceSessionAsync(dialog.FileNames, mode);
                return;
            }

            await LoadTraceAsync(dialog.FileName);
        }
    });


    /// <summary>Add more logs to the existing workspace without losing its current source list.
    /// The collection is reimported as one session; huge source logs may take time.</summary>
    public AsyncCommand AddTraceFilesCommand => _addTraceFilesCommand ??= new AsyncCommand(async () =>
    {
        if (_traceImportRunning) return;
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Add CAN traces to the current workspace",
            Filter = TraceImporter.FileDialogFilter,
            Multiselect = true
        };
        if (dialog.ShowDialog() != true) return;
        var paths = _traceWorkspacePaths.Concat(dialog.FileNames)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (paths.Length == _traceWorkspacePaths.Count)
        {
            StatusText = "All selected trace files are already in this workspace.";
            return;
        }
        var mode = _traceWorkspacePaths.Count > 1 ? _traceWorkspaceMode
            : paths.Length > 1
                ? TraceMergeModeDialog.Ask(System.Windows.Application.Current?.MainWindow, paths.Length)
                : TraceMergeMode.ByTime;
        if (mode is not { } chosen) return;
        await LoadTraceSessionAsync(paths, chosen);
    });

    /// <summary>Prevent stale pages referencing a removed physical logger
    /// from being silently overwritten by the currently displayed recording.</summary>
    public bool IsNotebookSourceAvailable(string sourcePath) =>
        _traceWorkspacePaths.Contains(sourcePath, StringComparer.OrdinalIgnoreCase);

    /// <summary>Snapshot the current Trace-grid recipe, never the CAN frame
    /// payloads. A source is identified by its exact imported path, not its
    /// display name or channel ID.</summary>
    public TraceNotebookPage? CaptureCurrentTraceNotebook(string title, string notes,
        string? id = null)
    {
        if (_frames.Count == 0 || _traceImportRunning)
        {
            StatusText = "Load and finish importing a trace before creating an analysis tab.";
            return null;
        }
        string? sourcePath = _activeTraceWorkspacePath;
        if (_traceSourceIndex is { } index &&
            Trace.FocusedSourceToken is var focus && focus >= 0 && focus < index.SourceCount)
            sourcePath = index.Paths[focus];
        return TraceNotebookPages.Normalize(new TraceNotebookPage(
            id ?? Guid.NewGuid().ToString("N"), title, Trace.AppliedQuery,
            Trace.ViewModeIndex, Trace.TimeDisplayModeIndex, sourcePath, notes));
    }

    /// <summary>Apply one saved Trace recipe. A stale or missing physical
    /// source is rejected, never silently replaced by similarly named logs.
    /// This is a Trace-only view, not an independent UDS/DBC dataset.</summary>
    public bool TryActivateTraceNotebook(TraceNotebookPage page)
    {
        if (_frames.Count == 0 || _traceImportRunning)
        {
            StatusText = "Load a complete trace before opening saved analysis tabs.";
            return false;
        }
        if (page.SourcePath is null)
        {
            if (_activeTraceWorkspacePath is not null && _traceWorkspacePaths.Count > 1)
            {
                StatusText = "Select All traces in the source explorer before opening a combined analysis tab.";
                return false;
            }
            Trace.ApplyNotebookView(page.Query, page.ViewModeIndex, page.TimeDisplayModeIndex);
        }
        else if (_traceSourceIndex is { } index)
        {
            var source = TraceNotebookPages.FindSource(page.SourcePath, index.Paths);
            if (source < 0)
            {
                StatusText = $"Analysis '{page.Title}' references a missing recording. Select a valid source; no other file was substituted.";
                return false;
            }
            Trace.ApplyNotebookView(page.Query, page.ViewModeIndex, page.TimeDisplayModeIndex,
                source, i => index.SourceAt(i) == source, Path.GetFileName(index.Paths[source]));
        }
        else if (string.Equals(page.SourcePath, _tracePath, StringComparison.OrdinalIgnoreCase))
        {
            // The exact physical recording is already open independently.
            Trace.ApplyNotebookView(page.Query, page.ViewModeIndex, page.TimeDisplayModeIndex);
        }
        else
        {
            StatusText = $"Analysis '{page.Title}' requires its original recording. Return to All traces to restore it.";
            return false;
        }
        // A notebook opened inside Data Studio should not force the user back
        // to the technical Trace view, which breaks the guided workflow.
        if (SelectedNavIndex != DataStudioNavIndex)
            SelectedNavIndex = 1;
        StatusText = $"Analysis tab '{page.Title}' restored (Trace only; other workspaces unchanged).";
        return true;
    }

    /// <summary>Focus the Trace grid on one recorded file without reloading any data.
    /// Other decoders, plots and investigative views still analyze the combined session.</summary>
    public RelayCommand<TraceWorkspaceEntry> FocusTraceSourceCommand =>
        _focusTraceSourceCommand ??= new RelayCommand<TraceWorkspaceEntry>(entry =>
        {
            if (_traceImportRunning || entry is null || _traceSourceIndex is not { SourceCount: > 1 } index)
                return;
            if (entry.Path is null)
            {
                Trace.ClearSourceScope();
                StatusText = "Trace rows now include all recordings.";
            }
            else
            {
                var source = -1;
                for (var i = 0; i < index.SourceCount; i++)
                    if (string.Equals(index.Paths[i], entry.Path, StringComparison.OrdinalIgnoreCase))
                    {
                        source = i;
                        break;
                    }
                if (source < 0) return;
                var selectedSource = source;
                Trace.FocusSource(selectedSource,
                    frameIndex => index.SourceAt(frameIndex) == selectedSource,
                    entry.DisplayName);
                StatusText = $"Trace rows focused on {entry.DisplayName}; combined analysis is unchanged.";
            }
            SelectedNavIndex = 1;
        });

    public RelayCommand ClearTraceSourceFocusCommand =>
        _clearTraceSourceFocusCommand ??= new RelayCommand(() =>
        {
            Trace.ClearSourceScope();
            StatusText = "Trace rows now include all recordings.";
        });

    public RelayCommand<TraceWorkspaceEntry> ViewTraceSourceCommand =>
        _viewTraceSourceCommand ??= new RelayCommand<TraceWorkspaceEntry>(async entry =>
        {
            if (entry is not null) await ActivateTraceWorkspaceEntryAsync(entry);
        });

    public RelayCommand<TraceWorkspaceEntry> RemoveTraceSourceCommand =>
        _removeTraceSourceCommand ??= new RelayCommand<TraceWorkspaceEntry>(async entry =>
        {
            if (entry?.Path is null || _traceWorkspacePaths.Count <= 1 || _traceImportRunning) return;
            var remaining = _traceWorkspacePaths.Where(p =>
                !string.Equals(p, entry.Path, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (remaining.Length > 0) await LoadTraceSessionAsync(remaining, _traceWorkspaceMode);
        });

    /// <summary>Switch between all sources and a single source without discarding the other
    /// source references, filters, DBC catalogs or annotations. Frames are reloaded on demand.</summary>
    public async Task ActivateTraceWorkspaceEntryAsync(TraceWorkspaceEntry entry)
    {
        if (_traceImportRunning || _traceWorkspacePaths.Count == 0 || entry.IsCurrent) return;
        if (entry.Path is { } selected)
        {
            if (!_traceWorkspacePaths.Contains(selected, StringComparer.OrdinalIgnoreCase)) return;
            await LoadTraceCoreAsync(selected, null, _traceWorkspaceMode, preserveWorkspace: true,
                activeSourcePath: selected);
        }
        else
        {
            var sources = _traceWorkspacePaths.ToArray();
            await LoadTraceCoreAsync(sources[0], sources.Length > 1 ? sources : null,
                _traceWorkspaceMode, preserveWorkspace: true);
        }
    }

    private string? TraceSourceNameAt(int frameIndex)
    {
        var path = _traceSourceIndex?.TryLocate(frameIndex, out var location) == true
            ? location.Path : _tracePath;
        return _traceSourceLabels.GetValueOrDefault(path) ?? Path.GetFileName(path);
    }

    /// <summary>Source frame positions are chronological after each source's
    /// stable timestamp sort, not raw file line numbers or BLF byte offsets.
    /// The merge may shift clocks or CAN channels; expose those transforms.</summary>
    private string? TraceSourceEvidenceAt(int frameIndex)
    {
        if (_traceSourceIndex?.TryLocate(frameIndex, out var source) == true)
            return source.Path + Environment.NewLine +
                   $"Original sorted frame #{source.SourceFrameIndex + 1:N0}" + Environment.NewLine +
                   $"Applied timestamp shift {source.TimeShiftNanoseconds:+0;-0;0} ns · " +
                   $"channel offset {source.ChannelOffset:+0;-0;0}" + Environment.NewLine +
                   "Frame position is chronological, not a physical file byte offset.";
        return string.IsNullOrWhiteSpace(_tracePath) ? null :
            _tracePath + Environment.NewLine +
            $"Sorted imported frame #{frameIndex + 1:N0}";
    }

    private void RefreshTraceWorkspaceEntries()
    {
        TraceWorkspaceEntries.Clear();
        _traceSourceLabels.Clear();
        if (_traceWorkspacePaths.Count > 1)
            TraceWorkspaceEntries.Add(new TraceWorkspaceEntry(null,
                $"All {_traceWorkspacePaths.Count} traces ({_traceWorkspaceMode})",
                _activeTraceWorkspacePath is null, false));
        var duplicateNames = _traceWorkspacePaths
            .GroupBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var path in _traceWorkspacePaths)
        {
            var name = Path.GetFileName(path);
            var displayName = duplicateNames.Contains(name)
                ? $"{name} — {Path.GetFileName(Path.GetDirectoryName(path))}" : name;
            _traceSourceLabels[path] = displayName;
            var isCurrent = (_traceWorkspacePaths.Count == 1 && _activeTraceWorkspacePath is null) ||
                string.Equals(path, _activeTraceWorkspacePath, StringComparison.OrdinalIgnoreCase);
            var summary = _traceSourceSummaries.FirstOrDefault(info =>
                string.Equals(info.Path, path, StringComparison.OrdinalIgnoreCase));
            var details = summary is null ? null :
                $"{summary.Frames:N0} frames · {summary.Channels} channels · {summary.Format}" +
                (summary.DurationSeconds is { } seconds ? $" · {seconds:N3} s" : "") +
                (summary.RequiresReview ? " · review import warnings" : "");
            TraceWorkspaceEntries.Add(new TraceWorkspaceEntry(path, displayName, isCurrent,
                _traceWorkspacePaths.Count > 1, details));
        }
        OnPropertyChanged(nameof(HasMultipleTraceSources));
        OnPropertyChanged(nameof(CanCompareTraceSources));
        OnPropertyChanged(nameof(CanFocusTraceSource));
        OnPropertyChanged(nameof(CanOpenOriginalSource));
        OnPropertyChanged(nameof(HasTraceWorkspace));
        OnPropertyChanged(nameof(TraceWorkspaceSummary));
        OnPropertyChanged(nameof(TraceWorkspacePaths));
        OnPropertyChanged(nameof(TraceWorkspaceMergeMode));
    }

    /// <summary>Navigate back to the exact sorted row of its original file.
    /// Re-checks frame bytes after reimport; a modified file cannot silently substitute evidence.</summary>
    public AsyncCommand OpenOriginalTraceSourceCommand => _openOriginalTraceSourceCommand ??=
        new AsyncCommand(async () =>
        {
            if (_traceImportRunning || _traceSourceIndex is null ||
                Trace.SelectedRow is not { } selected ||
                !_traceSourceIndex.TryLocate(selected.FrameIndex, out var original)) return;

            var combined = selected.Frame;
            var sourceTimestamp = checked(combined.TimestampNanoseconds - original.TimeShiftNanoseconds);
            var sourceChannel = checked(combined.Channel - original.ChannelOffset);
            await LoadTraceCoreAsync(original.Path, null, _traceWorkspaceMode,
                preserveWorkspace: true, activeSourcePath: original.Path);
            if (!string.Equals(_activeTraceWorkspacePath, original.Path, StringComparison.OrdinalIgnoreCase) ||
                _traceSourceIndex is not null)
            {
                StatusText = "Original source could not be reopened; previous snapshot was retained.";
                return;
            }
            if (_frames.Count <= original.SourceFrameIndex)
            {
                StatusText = "Original source changed: the expected frame no longer exists.";
                return;
            }
            var frame = _frames[original.SourceFrameIndex];
            bool matches = frame.TimestampNanoseconds == sourceTimestamp &&
                           frame.Channel == sourceChannel &&
                           frame.Identifier == combined.Identifier &&
                           frame.Flags == combined.Flags &&
                           frame.DataLength == combined.DataLength &&
                           frame.DeclaredDataLength == combined.DeclaredDataLength;
            if (matches)
            {
                for (var i = 0; i < frame.DataLength; i++)
                    if (frame.GetByte(i) != combined.GetByte(i)) { matches = false; break; }
            }
            if (!matches)
            {
                StatusText = "Original source differs from the merged snapshot. Frame navigation refused.";
                return;
            }
            Trace.SearchText = string.Empty;
            Trace.ClearFilterCommand.Execute(null);
            SelectedNavIndex = 1;
            Trace.JumpToFrameIndex(original.SourceFrameIndex);
            StatusText = $"Original frame {original.SourceFrameIndex + 1:N0} verified in {Path.GetFileName(original.Path)}.";
        });

    /// <summary>Build a cross-file coverage matrix from the actual merged snapshot.
    /// Imports cannot replace/dispose that snapshot while the analysis is running.</summary>
    public async Task<TraceSourceCoverageReport?> BuildSourceCoverageAsync()
    {
        if (_traceImportRunning || _traceSourceIndex is not { SourceCount: > 1 } index)
        {
            StatusText = "Open the combined view of two or more traces before comparing source coverage.";
            return null;
        }
        _traceImportRunning = true;
        IsBusy = true;
        BusyText = "Comparing CAN identifiers across sources…";
        try
        {
            var snapshot = _frames;
            var mode = _traceWorkspaceMode;
            var report = await Task.Run(() => TraceSourceCoverage.Analyze(snapshot, index, mode));
            StatusText = $"Source comparison: {report.TotalIdentifiers:N0} bus/ID identities across {index.SourceCount} traces.";
            return report;
        }
        catch (Exception ex)
        {
            StatusText = "Source comparison failed: " + ex.Message;
            return null;
        }
        finally
        {
            _traceImportRunning = false;
            IsBusy = false;
        }
    }

    public AsyncCommand LoadDbcCommand => _loadDbcCommand ??= new AsyncCommand(async () =>
    {
        ClearStaleStatus();
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "CAN databases (*.dbc;*.arxml)|*.dbc;*.arxml|DBC (*.dbc)|*.dbc|AUTOSAR ARXML (*.arxml)|*.arxml|All files (*.*)|*.*",
            Multiselect = true,
            Title = "Load CAN database"
        };
        if (dialog.ShowDialog() == true)
        {
            await LoadDbcAsync(dialog.FileNames);
        }
    });

    /// <summary>Export source DBC or CDD/ODX definitions independently of trace samples.
    /// The existing catalog search is applied as a case-insensitive AND filter;
    /// original input databases remain unchanged.</summary>
    public AsyncCommand ExportDbcDefinitionsCommand => _exportDbcDefinitionsCommand ??=
        new AsyncCommand(() => ExportCatalogDefinitionsAsync(dbc: true));

    public AsyncCommand CompareDbcFilesCommand => _compareDbcFilesCommand ??=
        new AsyncCommand(CompareDbcFilesDialogAsync);

    public AsyncCommand ExportDbcComparisonCommand => _exportDbcComparisonCommand ??=
        new AsyncCommand(ExportDbcComparisonAsync);

    public RelayCommand InspectBaselineDbcDiffCommand =>
        _inspectBaselineDbcDiffCommand ??= new RelayCommand(() =>
            InspectDbcDiffInLoadedDatabase(currentVersion: false));

    public RelayCommand InspectCurrentDbcDiffCommand =>
        _inspectCurrentDbcDiffCommand ??= new RelayCommand(() =>
            InspectDbcDiffInLoadedDatabase(currentVersion: true));

    /// <summary>Navigate from one version-diff row into a loaded DBC only when
    /// source path and understood parsed definitions match that exact version.
    /// Does not apply source changes, substitute a same-named database or infer
    /// an ambiguous 11/29-bit frame or duplicate signal identity.</summary>
    public bool InspectDbcDiffInLoadedDatabase(bool currentVersion)
    {
        var selected = SelectedDbcComparisonDiffRow;
        if (selected is null)
        {
            StatusText = "Select a DBC message or signal difference first.";
            return false;
        }
        if (selected.Change == DbcDefinitionChange.Ambiguous ||
            selected.Scope is not ("Signal" or "Message"))
        {
            StatusText = "Ambiguous or unsupported DBC difference: inspect source definitions manually.";
            return false;
        }
        var which = currentVersion ? "current" : "baseline";
        if ((currentVersion && selected.Change == DbcDefinitionChange.Removed) ||
            (!currentVersion && selected.Change == DbcDefinitionChange.Added))
        {
            StatusText = $"This CAN definition is absent from the {which} version.";
            return false;
        }
        var source = currentVersion ? _comparisonDbcCurrentFile : _comparisonDbcBaselineFile;
        var expected = currentVersion ? _comparisonDbcCurrent : _comparisonDbcBaseline;
        if (expected is null || string.IsNullOrWhiteSpace(source))
        {
            StatusText = "Compare two DBC files before navigating their definitions.";
            return false;
        }
        var matches = _databases.Where(database =>
            _databasePaths.TryGetValue(database.Name, out var path) &&
            string.Equals(Path.GetFullPath(path), Path.GetFullPath(source),
                StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
        if (matches.Length != 1)
        {
            StatusText = matches.Length == 0
                ? $"The exact {which} DBC file is not loaded in DBC Manager."
                : $"Several loaded DBCs use the {which} source path. Resolve duplicate databases first.";
            return false;
        }
        var loaded = matches[0];
        var resolution = DbcDiffNavigationResolver.Resolve(
            selected, currentVersion, expected, source,
            loaded, _databasePaths[loaded.Name]);
        if (resolution.Target is null)
        {
            StatusText = $"{which} DBC inspector: {resolution.Explanation}";
            return false;
        }
        // The pure resolver has already checked exact source, semantic
        // snapshot, frame format and signal multiplicity before UI navigation.
        var target = resolution.Target;
        var databaseRow = DbcDatabases.SingleOrDefault(row =>
            string.Equals(row.Name, loaded.Name, StringComparison.OrdinalIgnoreCase));
        if (databaseRow is null)
        {
            StatusText = "The validated DBC has no unique manager row; refresh the DBC Manager.";
            return false;
        }

        DbcCatalogSearchText = string.Empty;
        SelectedDbcDatabase = databaseRow;
        // The ordinary DBC search uses a debounce timer. Navigation is an
        // explicit action and must populate the target rows immediately even
        // when a previous search hid them or the same database stays selected.
        RebuildDbcMessageRows();
        RebuildDbcSignalRows();
        var frame = target.Identifier.ToString(target.IsExtended ? "X8" : "X3",
            System.Globalization.CultureInfo.InvariantCulture) +
            (target.IsExtended ? "x" : string.Empty);
        var messageRow = DbcMessages.SingleOrDefault(row =>
            row.Database == loaded.Name && row.Identifier == frame &&
            row.Name == target.MessageName);
        if (messageRow is null)
        {
            StatusText = "Unable to display the validated DBC message in the manager.";
            return false;
        }
        SelectedDbcMessage = messageRow;
        SelectedDbcSignal = selected.Scope == "Signal" ?
            DbcSignals.SingleOrDefault(row => row.Database == loaded.Name &&
                row.Identifier == frame && row.Message == target.MessageName &&
                row.Signal == target.SignalName) : null;
        SelectedNavIndex = 2;
        StatusText = $"Inspecting {which} {selected.Scope} 0x{selected.Identifier:X} " +
            $"({(selected.IsExtended ? "29-bit" : "11-bit")}) in " +
            $"{loaded.Name}. Exact source and parsed definitions verified; " +
            "recorded CAN evidence requires a trace and physical channel check.";
        return true;
    }

    /// <summary>Read-only DBC source comparison: does not replace any loaded
    /// DBC or mutate the active CAN decoder. Usable by the Windows GUI gate.</summary>
    public async Task<bool> CompareDbcFilesAsync(string baselinePath, string currentPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baselinePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentPath);
        try
        {
            var result = await Task.Run(() =>
            {
                var before = new DbcParser().ParseFile(baselinePath);
                var after = new DbcParser().ParseFile(currentPath);
                var differences = DbcDefinitionComparer.Compare(before, after);
                return (Before: before, After: after, Differences: differences);
            });

            _comparisonDbcBaseline = result.Before;
            _comparisonDbcCurrent = result.After;
            _comparisonDbcBaselineFile = baselinePath;
            _comparisonDbcCurrentFile = currentPath;
            _dbcDefinitionComparison = result.Differences;
            SelectedDbcComparisonDiffRow = null;
            RefreshDbcComparisonRows();
            OnPropertyChanged(nameof(HasDbcComparison));
            OnPropertyChanged(nameof(DbcComparisonSummary));
            StatusText = "Read-only DBC comparison ready. " + result.Differences.Summary +
                ". Export XLSX or CSV ZIP to inspect full before/after values.";
            return true;
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(ex.Message, "DBC comparison failed",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            return false;
        }
    }

    private void RefreshDbcComparisonRows()
    {
        DbcComparisonRows = _dbcDefinitionComparison is null ? [] :
            _dbcDefinitionComparison.Rows.Where(row =>
                !_dbcComparisonChangesOnly || row.Change != DbcDefinitionChange.Unchanged).ToArray();
        if (SelectedDbcComparisonDiffRow is { } selected && !DbcComparisonRows.Contains(selected))
            SelectedDbcComparisonDiffRow = null;
        OnPropertyChanged(nameof(DbcComparisonRows));
    }

    private async Task CompareDbcFilesDialogAsync()
    {
        var beforeDialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "DBC definitions (*.dbc)|*.dbc|All files (*.*)|*.*",
            Title = "Compare DBCs — choose BASELINE file"
        };
        if (beforeDialog.ShowDialog() != true) return;
        var afterDialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "DBC definitions (*.dbc)|*.dbc|All files (*.*)|*.*",
            Title = "Compare DBCs — choose CURRENT file"
        };
        if (afterDialog.ShowDialog() != true) return;
        await CompareDbcFilesAsync(beforeDialog.FileName, afterDialog.FileName);
    }

    private async Task ExportDbcComparisonAsync()
    {
        if (_comparisonDbcBaseline is null || _comparisonDbcCurrent is null ||
            _dbcDefinitionComparison is null) return;
        var save = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "Excel workbook (*.xlsx)|*.xlsx|CSV files in ZIP (*.zip)|*.zip",
            FilterIndex = 1,
            DefaultExt = ".xlsx",
            FileName = $"dbc-definition-diff-{DateTime.Now:yyyyMMdd-HHmmss}.xlsx",
            Title = "Export read-only DBC definition comparison"
        };
        if (save.ShowDialog() != true) return;
        var archive = save.FilterIndex == 2;
        var destination = Path.ChangeExtension(save.FileName, archive ? ".zip" : ".xlsx");
        var baseline = _comparisonDbcBaseline;
        var current = _comparisonDbcCurrent;
        var comparison = _dbcDefinitionComparison;
        var baselinePath = _comparisonDbcBaselineFile;
        var currentPath = _comparisonDbcCurrentFile;
        try
        {
            await Task.Run(() =>
            {
                var sheets = DbcDefinitionComparisonExport.BuildSheets(
                    baseline, current, comparison, baselinePath, currentPath);
                DbcDefinitionComparisonExport.Write(destination, sheets, csvZip: archive);
            });
            StatusText = $"DBC comparison exported: {Path.GetFileName(destination)}. " +
                comparison.Summary + ". Source DBC files unchanged.";
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(ex.Message, "DBC comparison export failed",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
    }

    public AsyncCommand ExportDiagnosticDefinitionsCommand => _exportDiagnosticDefinitionsCommand ??=
        new AsyncCommand(() => ExportCatalogDefinitionsAsync(dbc: false));

    private AsyncCommand? _exportDiagnosticConditionsCommand;

    /// <summary>Document declared pre-conditions (session, security, other), layouts and what was not
    /// extracted for every loaded CDD/ODX definition, as an Excel workbook or CSV archive.</summary>
    public AsyncCommand ExportDiagnosticConditionsCommand => _exportDiagnosticConditionsCommand ??=
        new AsyncCommand(async () =>
        {
            var catalogs = Mapping.Catalogs;
            if (catalogs.Count == 0)
            {
                StatusText = "Load a CDD, ODX or PDX diagnostic catalog first.";
                return;
            }
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "Excel workbook (*.xlsx)|*.xlsx|CSV archive (*.zip)|*.zip",
                FilterIndex = 1,
                DefaultExt = ".xlsx",
                AddExtension = true,
                FileName = "diagnostic-conditions-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"),
                Title = "Export diagnostic conditions and access requirements"
            };
            if (dialog.ShowDialog() != true) return;
            var csvArchive = dialog.FilterIndex == 2;
            var destination = Path.ChangeExtension(dialog.FileName, csvArchive ? ".zip" : ".xlsx");
            var temporary = destination + ".partial-" + Guid.NewGuid().ToString("N");
            try
            {
                var sheets = await Task.Run(() => DiagnosticConditionsExport.CreateSheets(catalogs));
                await Task.Run(() =>
                {
                    if (csvArchive) StructuredCsvArchiveExport.Write(temporary, sheets);
                    else StructuredWorkbookExport.Write(temporary, sheets);
                    File.Move(temporary, destination, overwrite: true);
                });
                StatusText = $"Diagnostic conditions exported: {Path.GetFileName(destination)} " +
                             $"({sheets[0].Rows.Count:N0} definitions, {sheets[1].Rows.Count:N0} declared conditions).";
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                StatusText = "Conditions export failed: " + exception.Message;
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch (IOException) { /* best effort */ }
            }
        });
    public AsyncCommand CompareDiagnosticFilesCommand => _compareDiagnosticFilesCommand ??=
        new AsyncCommand(CompareDiagnosticFilesDialogAsync);

    public AsyncCommand ExportDiagnosticComparisonCommand => _exportDiagnosticComparisonCommand ??=
        new AsyncCommand(ExportDiagnosticComparisonAsync);

    public RelayCommand InspectBaselineDiagnosticDiffCommand =>
        _inspectBaselineDiagnosticDiffCommand ??= new RelayCommand(() =>
            InspectDiagnosticDiffInLoadedCatalog(currentVersion: false));

    public RelayCommand InspectCurrentDiagnosticDiffCommand =>
        _inspectCurrentDiagnosticDiffCommand ??= new RelayCommand(() =>
            InspectDiagnosticDiffInLoadedCatalog(currentVersion: true));

    /// <summary>Open an exact existing source and ECU/identifier, never a same-number
    /// DID from another ECU, another file or an unrelated hierarchy path.</summary>
    public bool InspectDiagnosticDiffInLoadedCatalog(bool currentVersion)
    {
        var selected = SelectedDiagnosticDiffRow;
        if (selected is null)
        {
            StatusText = "Select a diagnostic definition difference first.";
            return false;
        }
        var which = currentVersion ? "current" : "baseline";
        var sourcePath = currentVersion
            ? _comparisonDiagnosticAfterFile : _comparisonDiagnosticBeforeFile;
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            StatusText = "Compare two diagnostic descriptions before opening a definition.";
            return false;
        }
        if ((currentVersion && selected.Change == DiagnosticDefinitionChange.Removed) ||
            (!currentVersion && selected.Change == DiagnosticDefinitionChange.Added))
        {
            StatusText = $"The selected definition is absent from the {which} source.";
            return false;
        }
        var resolution = DiagnosticDiffNavigationResolver.Resolve(
            selected, Mapping.Catalogs, sourcePath);
        if (resolution.Target is null)
        {
            StatusText = $"{which} inspector: {resolution.Explanation}";
            return false;
        }
        if (!CatalogExplorer.TryFocus(resolution.Target))
        {
            StatusText = $"{which} inspector: the matching root or nested parameter " +
                "cannot be selected uniquely in the loaded catalog.";
            return false;
        }
        Uds.SelectedViewIndex = 11;
        SelectedNavIndex = 8;
        StatusText = $"Inspecting {which} {selected.Scope} {selected.Identifier} in ECU " +
            $"{selected.Ecu}. Confirm source provenance; no live ECU value is implied.";
        return true;
    }

    /// <summary>Loads independent baseline/current CDD, ODX or PDX
    /// descriptions for read-only comparison; does not change loaded catalogs.</summary>
    public async Task<bool> CompareDiagnosticFilesAsync(string baselinePath, string currentPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baselinePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentPath);
        try
        {
            var loaded = await Task.Run(() =>
            {
                var before = DidCatalog.LoadAll(baselinePath);
                var after = DidCatalog.LoadAll(currentPath);
                var report = DiagnosticDefinitionComparer.Compare(before, after);
                return (Before: before, After: after, Report: report);
            });

            _comparisonDiagnosticBefore = loaded.Before;
            _comparisonDiagnosticAfter = loaded.After;
            _comparisonDiagnosticBeforeFile = baselinePath;
            _comparisonDiagnosticAfterFile = currentPath;
            _diagnosticDefinitionComparison = loaded.Report;
            SelectedDiagnosticDiffRow = null;
            DiagnosticComparisonEcuChoices = ["All ECUs",
                .. loaded.Report.Rows.Select(row => row.Ecu)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(ecu => ecu, StringComparer.OrdinalIgnoreCase)];
            DiagnosticComparisonScopeChoices = ["All types",
                .. loaded.Report.Rows.Select(row => row.Scope)
                    .Distinct(StringComparer.Ordinal).OrderBy(scope => scope, StringComparer.Ordinal)];
            OnPropertyChanged(nameof(DiagnosticComparisonEcuChoices));
            OnPropertyChanged(nameof(DiagnosticComparisonScopeChoices));
            // Reset filters on new source versions. Old ECU and PAR scopes
            // must not hide every row of an otherwise successful comparison.
            _selectedDiagnosticComparisonEcu = "All ECUs";
            _selectedDiagnosticComparisonScope = "All types";
            _diagnosticComparisonSearchText = string.Empty;
            OnPropertyChanged(nameof(SelectedDiagnosticComparisonEcu));
            OnPropertyChanged(nameof(SelectedDiagnosticComparisonScope));
            OnPropertyChanged(nameof(DiagnosticComparisonSearchText));
            RefreshDiagnosticComparisonRows();
            OnPropertyChanged(nameof(HasDiagnosticComparison));
            OnPropertyChanged(nameof(DiagnosticComparisonSummary));
            StatusText = "Read-only CDD/ODX comparison ready. " + loaded.Report.Summary +
                ". Export XLSX or CSV ZIP to inspect the full original definitions.";
            return true;
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception.Message, "Diagnostic definition comparison failed",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    private void RefreshDiagnosticComparisonRows()
    {
        var search = _diagnosticComparisonSearchText.Trim();
        var terms = search.Split(' ',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        DiagnosticComparisonRows = _diagnosticDefinitionComparison is null ? [] :
            _diagnosticDefinitionComparison.Rows.Where(row =>
                (!_diagnosticComparisonChangesOnly ||
                 row.Change != DiagnosticDefinitionChange.Unchanged) &&
                (_selectedDiagnosticComparisonEcu == "All ECUs" ||
                 string.Equals(row.Ecu, _selectedDiagnosticComparisonEcu,
                     StringComparison.OrdinalIgnoreCase)) &&
                (_selectedDiagnosticComparisonScope == "All types" ||
                 row.Scope == _selectedDiagnosticComparisonScope) &&
                terms.All(term =>
                    row.Identifier.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                    row.Ecu.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                    row.Scope.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                    row.Path.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                    row.ChangedFields.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                    row.BaselineName.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                    row.CurrentName.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                    row.BaselineDefinition.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                    row.CurrentDefinition.Contains(term, StringComparison.OrdinalIgnoreCase))).ToArray();
        if (SelectedDiagnosticDiffRow is { } selected &&
            !DiagnosticComparisonRows.Contains(selected))
            SelectedDiagnosticDiffRow = null;
        OnPropertyChanged(nameof(DiagnosticComparisonRows));
        OnPropertyChanged(nameof(DiagnosticComparisonVisibleSummary));
    }

    private async Task CompareDiagnosticFilesDialogAsync()
    {
        var beforeDialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = DidCatalog.FileDialogFilter,
            Title = "Compare diagnostic descriptions — choose BASELINE CDD/ODX/PDX"
        };
        if (beforeDialog.ShowDialog() != true) return;
        var afterDialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = DidCatalog.FileDialogFilter,
            Title = "Compare diagnostic descriptions — choose CURRENT CDD/ODX/PDX"
        };
        if (afterDialog.ShowDialog() != true) return;
        await CompareDiagnosticFilesAsync(beforeDialog.FileName, afterDialog.FileName);
    }

    private async Task ExportDiagnosticComparisonAsync()
    {
        if (_comparisonDiagnosticBefore is null || _comparisonDiagnosticAfter is null ||
            _diagnosticDefinitionComparison is null)
            return;
        var save = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "Excel workbook (*.xlsx)|*.xlsx|CSV files in ZIP (*.zip)|*.zip",
            FilterIndex = 1,
            DefaultExt = ".xlsx",
            FileName = $"diagnostic-definitions-diff-{DateTime.Now:yyyyMMdd-HHmmss}.xlsx",
            Title = "Export CDD/ODX/PDX definition changes"
        };
        if (save.ShowDialog() != true) return;
        var useZip = save.FilterIndex == 2;
        var target = Path.ChangeExtension(save.FileName, useZip ? ".zip" : ".xlsx");
        var before = _comparisonDiagnosticBefore;
        var after = _comparisonDiagnosticAfter;
        var beforeFile = _comparisonDiagnosticBeforeFile;
        var afterFile = _comparisonDiagnosticAfterFile;
        var report = _diagnosticDefinitionComparison;
        try
        {
            await Task.Run(() =>
            {
                var sheets = DiagnosticDefinitionComparisonExport.BuildSheets(
                    before, after, report, beforeFile, afterFile);
                DiagnosticDefinitionComparisonExport.Write(target, sheets, csvZip: useZip);
            });
            StatusText = "Diagnostic definition diff exported: " +
                Path.GetFileName(target) + ". " + report.Summary +
                ". Source catalogs unchanged.";
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception.Message, "Diagnostic comparison export failed",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    public AsyncCommand ExportSelectedDiagnosticDidCommand => _exportSelectedDiagnosticDidCommand ??=
        new AsyncCommand(() => ExportCatalogDefinitionsAsync(dbc: false, selectedDidOnly: true));

    public AsyncCommand ExportEngineeringParametersCommand => _exportEngineeringParametersCommand ??=
        new AsyncCommand(() => ExportCatalogDefinitionsAsync(dbc: false, engineeringOnly: true));

    private async Task ExportCatalogDefinitionsAsync(
        bool dbc, bool selectedDidOnly = false, bool engineeringOnly = false)
    {
        var databases = _databases.ToArray();
        var catalogs = Mapping.Catalogs;
        if (dbc ? databases.Length == 0 : catalogs.Count == 0)
        {
            StatusText = dbc ? "Load a DBC or ARXML database first." :
                "Load a CDD, ODX or PDX diagnostic catalog first.";
            return;
        }

        var selected = selectedDidOnly ? CatalogExplorer.SelectedDefinition : null;
        if (selectedDidOnly && (selected is null || selected.Kind != "DID" || selected.Did is null))
        {
            StatusText = "Select one diagnostic DID before exporting its definition.";
            return;
        }

        // The user's selection is captured before the file dialog opens; a
        // repeated numeric DID in a different ECU or source must never leak in.
        var didSelection = selectedDidOnly
            ? new DiagnosticDidSelection(selected!.SourceFile, selected.Ecu, selected.Did!.Value, selected.Name)
            : null;
        var filter = selectedDidOnly ? null : dbc ? DbcCatalogSearchText : CatalogExplorer.SearchText;
        var ecuFilter = selectedDidOnly || dbc ? null : CatalogExplorer.SelectedEcuForExport;
        var prefix = selectedDidOnly ? $"selected-did-{selected!.Did!.Value:X4}" :
            engineeringOnly ? "engineering-parameters" :
            dbc ? "dbc-definitions" : "diagnostic-definitions";
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "Excel workbook (*.xlsx)|*.xlsx|CSV archive (*.zip)|*.zip",
            FilterIndex = 1,
            DefaultExt = ".xlsx",
            AddExtension = true,
            FileName = prefix + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"),
            Title = selectedDidOnly ? "Export selected DID, engineering PARs and sub-PARs" :
                engineeringOnly ? "Export Engineering Parameters with DID and sub-PAR descriptions" :
                dbc ? "Export DBC definitions (current search)" :
                "Export CDD/ODX definitions (current search)"
        };
        if (dialog.ShowDialog() != true) return;

        // Both formats contain the same filtered source-backed definitions.
        // CSV ZIP is an Excel-friendly fallback for large or externally processed catalogs.
        var csvArchive = dialog.FilterIndex == 2;
        var destination = Path.ChangeExtension(dialog.FileName, csvArchive ? ".zip" : ".xlsx");
        var temporary = destination + ".partial-" + Guid.NewGuid().ToString("N");
        try
        {
            var sheets = await Task.Run(() =>
            {
                return dbc
                    ? CatalogDefinitionExport.CreateDbcSheets(databases, filter)
                    : CatalogDefinitionExport.CreateDiagnosticSheets(
                        catalogs, filter, didSelection, engineeringOnly, ecuFilter);
            });
            var count = sheets.Sum(sheet => sheet.Rows.Count);
            if (count == 0)
            {
                StatusText = "No catalog definitions match the current filter; no file was created.";
                return;
            }

            await Task.Run(() =>
            {
                if (csvArchive)
                    StructuredCsvArchiveExport.Write(temporary, sheets);
                else
                    StructuredWorkbookExport.Write(temporary, sheets);
            });
            File.Move(temporary, destination, overwrite: true);
            StatusText = csvArchive
                ? $"{count:N0} definition row(s) exported in {sheets.Count} CSV tables (ZIP): " +
                  Path.GetFileName(destination)
                : $"{count:N0} definition row(s) exported in {sheets.Count} Excel sheets: " +
                  Path.GetFileName(destination);
        }
        catch (Exception exception)
        {
            StatusText = "Catalog export failed: " + exception.Message;
            MessageBox.Show(exception.Message, "Catalog export failed",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { /* Preserve the primary error and do not touch source catalogs. */ }
        }
    }

    public AsyncCommand ExportDecodedSignalsCommand => _exportDecodedSignalsCommand ??= new AsyncCommand(async () =>
    {
        if (_timelines.Count == 0)
        {
            StatusText = "No decoded signals to export.";
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "CSV (*.csv)|*.csv|Excel workbook (*.xlsx)|*.xlsx",
            FileName = BuildExportBaseName("decoded-signals"),
            Title = "Export decoded signal samples"
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            await Task.Run(() =>
            {
                var rows = _timelines
                    .SelectMany(timeline => timeline.Samples.Select(sample => new
                    {
                        Transmitter = timeline.Message.Transmitter,
                        Message = timeline.Message.Name,
                        Signal = timeline.Signal.Name,
                        Unit = timeline.Signal.Unit,
                        Time = (sample.TimestampNanoseconds / 1_000_000_000d).ToString("F6", System.Globalization.CultureInfo.InvariantCulture),
                        sample.Value,
                        sample.FrameIndex
                    }))
                    .OrderBy(row => row.FrameIndex)
                    .ThenBy(row => row.Message, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(row => row.Signal, StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                var columns = new List<ExportColumn>
                {
                    new("Time [s]", rows.Select(row => row.Time).ToArray()),
                    new("Frame #", rows.Select(row => row.FrameIndex.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray()),
                    new("Transmitter", rows.Select(row => row.Transmitter).ToArray()),
                    new("Message", rows.Select(row => row.Message).ToArray()),
                    new("Signal", rows.Select(row => row.Signal).ToArray()),
                    new("Value", rows.Select(row => row.Value.ToString("G17", System.Globalization.CultureInfo.InvariantCulture)).ToArray()),
                    new("Unit", rows.Select(row => row.Unit).ToArray())
                };

                if (Path.GetExtension(dialog.FileName).Equals(".xlsx", StringComparison.OrdinalIgnoreCase))
                {
                    TabularExport.WriteXlsx(dialog.FileName, "Decoded signals", columns);
                }
                else
                {
                    TabularExport.WriteCsv(dialog.FileName, columns);
                }
            });

            StatusText = $"Decoded signals exported: {Path.GetFileName(dialog.FileName)}";
        }
        catch (Exception exception)
        {
            StatusText = "Decoded export failed: " + exception.Message;
            MessageBox.Show(exception.Message, "Export failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    });

    public AsyncCommand SavePresetCommand => _savePresetCommand ??= new AsyncCommand(async () =>
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "RBG CAN Insight preset (*.rbgpreset)|*.rbgpreset|JSON (*.json)|*.json",
            DefaultExt = ".rbgpreset",
            FileName = $"rbg-workspace-{DateTime.Now:yyyyMMdd-HHmmss}.rbgpreset",
            Title = "Save workspace preset"
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var preset = new WorkspacePreset(
            SelectedNavIndex,
            ThemeIndex,
            Trace.ViewModeIndex,
            Trace.PlaybackSpeed,
            Signals.PlaybackSpeed,
            Signals.PlotTimeWindowSeconds,
            ByteMatrix.PlaybackSpeed,
            Mapping.StatusFilterIndex,
            WatchSignals.Select(row => row.Series.Timeline.Key).ToArray());
        await File.WriteAllTextAsync(dialog.FileName, JsonSerializer.Serialize(preset, new JsonSerializerOptions { WriteIndented = true }));
        PresetLibraryStore.Remember(Path.GetFileNameWithoutExtension(dialog.FileName), dialog.FileName);
        RefreshPresetLibrary();
        StatusText = $"Preset saved: {Path.GetFileName(dialog.FileName)}";
    });

    /// <summary>Reloads the named preset library list from disk — called whenever the library
    /// window opens and after any save/load/remove so the list stays current.</summary>
    public void RefreshPresetLibrary()
    {
        PresetLibraryEntries.Clear();
        foreach (var entry in PresetLibraryStore.Load())
        {
            PresetLibraryEntries.Add(new PresetLibraryRowViewModel(entry));
        }

        OnPropertyChanged(nameof(HasPresetLibraryEntries));
    }

    public void RemovePresetLibraryEntry(string path)
    {
        PresetLibraryStore.Remove(path);
        RefreshPresetLibrary();
    }

    /// <summary>Loads a workspace preset by path directly — used by both the classic file dialog
    /// fallback and the named preset library.</summary>
    public async Task LoadPresetFileAsync(string path)
    {
        try
        {
            var preset = JsonSerializer.Deserialize<WorkspacePreset>(await File.ReadAllTextAsync(path));
            if (preset is null)
            {
                StatusText = "Preset file is empty.";
                return;
            }

            Trace.ViewModeIndex = preset.TraceViewModeIndex;
            ThemeIndex = preset.ThemeIndex;
            Trace.PlaybackSpeed = preset.TracePlaybackSpeed;
            Signals.PlaybackSpeed = preset.SignalPlaybackSpeed;
            Signals.PlotTimeWindowSeconds = preset.SignalWindowSeconds;
            ByteMatrix.PlaybackSpeed = preset.ByteMatrixPlaybackSpeed;
            Mapping.StatusFilterIndex = preset.MappingStatusFilterIndex;
            WatchSignals.Clear();
            foreach (var key in preset.WatchKeys)
            {
                var series = FindSeriesBySavedKey(key);
                if (series is null)
                {
                    continue;
                }

                var row = new WatchSignalRow(series);
                row.Update(PlotCursorNanoseconds);
                WatchSignals.Add(row);
            }

            SelectedNavIndex = preset.SelectedNavIndex;
            PresetLibraryStore.Remember(Path.GetFileNameWithoutExtension(path), path);
            RefreshPresetLibrary();
            StatusText = $"Preset loaded: {Path.GetFileName(path)}";
        }
        catch (Exception exception)
        {
            StatusText = "Preset not loaded: " + exception.Message;
            MessageBox.Show(exception.Message, "Preset not loaded", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// Unlike a workspace preset (UI state only), a session remembers which files were loaded —
    /// trace, DBC/ARXML, CDD/ODX and a reviewed DID map — plus filters, watch list and plotted
    /// signals, so reopening it re-loads everything instead of assuming the same files are
    /// already open.
    /// </summary>
    public AsyncCommand SaveSessionCommand => _saveSessionCommand ??= new AsyncCommand(async () =>
    {
        if (_frames.Count == 0 || _traceImportRunning)
        {
            StatusText = _traceImportRunning
                ? "Finish the current trace import before saving a session."
                : "Load a trace before saving a session.";
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "RBG CAN Insight session (*.rbgsession)|*.rbgsession|JSON (*.json)|*.json",
            DefaultExt = ".rbgsession",
            FileName = $"rbg-session-{DateTime.Now:yyyyMMdd-HHmmss}.rbgsession",
            Title = "Save analysis session"
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var sessionDir = Path.GetDirectoryName(Path.GetFullPath(dialog.FileName)) ?? string.Empty;
        string? MakeRelative(string? absolutePath) => string.IsNullOrWhiteSpace(absolutePath)
            ? null
            : Path.GetRelativePath(sessionDir, absolutePath);

        var dbcPaths = _databasePaths.Values.ToArray();
        var catalogPaths = Mapping.CatalogPaths.ToArray();
        var savedAnalysisTabs = Notebook.SnapshotForSession();

        var session = new SessionData(
            string.IsNullOrWhiteSpace(_tracePath) ? null : _tracePath,
            dbcPaths,
            catalogPaths,
            Mapping.LastMapPath,
            SelectedNavIndex,
            ThemeIndex,
            Trace.ViewModeIndex,
            Trace.PlaybackSpeed,
            Signals.PlaybackSpeed,
            Signals.PlotTimeWindowSeconds,
            ByteMatrix.PlaybackSpeed,
            Mapping.StatusFilterIndex,
            WatchSignals.Select(row => row.Series.Timeline.Key).ToArray(),
            Signals.AllSeries.Where(series => series.IsPlotted).Select(series => series.Timeline.Key).ToArray(),
            Trace.SearchText,
            Trace.RuleText,
            Signals.SearchText,
            Matrix.SearchText,
            ByteMatrix.FilterText,
            StatisticsSearchText,
            Mapping.SearchText,
            MakeRelative(_tracePath),
            dbcPaths.Select(path => Path.GetRelativePath(sessionDir, path)).ToArray(),
            catalogPaths.Select(path => Path.GetRelativePath(sessionDir, path)).ToArray(),
            MakeRelative(Mapping.LastMapPath),
            TraceWorkspacePaths,
            _traceWorkspacePaths.Select(MakeRelative).Select(p => p ?? string.Empty).ToArray(),
            _traceWorkspaceMode.ToString(),
            savedAnalysisTabs,
            Notebook.ActiveId,
            DataStudio.SectionIndex);

        try
        {
            await File.WriteAllTextAsync(dialog.FileName,
                JsonSerializer.Serialize(session, new JsonSerializerOptions { WriteIndented = true }));
            StatusText = $"Session saved: {Path.GetFileName(dialog.FileName)}";
        }
        catch (Exception exception)
        {
            StatusText = "Session save failed: " + exception.Message;
            MessageBox.Show(exception.Message, "Session save failed",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    });

    public AsyncCommand LoadSessionCommand => _loadSessionCommand ??= new AsyncCommand(async () =>
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "RBG CAN Insight session (*.rbgsession)|*.rbgsession|JSON (*.json)|*.json|All files (*.*)|*.*",
            Title = "Load analysis session"
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        SessionData? session;
        try
        {
            session = JsonSerializer.Deserialize<SessionData>(await File.ReadAllTextAsync(dialog.FileName));
        }
        catch (Exception exception)
        {
            StatusText = "Session not loaded: " + exception.Message;
            MessageBox.Show(exception.Message, "Session not loaded", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (session is null)
        {
            StatusText = "Session file is empty.";
            return;
        }

        IsBusy = true;
        BusyText = "Restoring session…";
        try
        {
            var sessionDir = Path.GetDirectoryName(Path.GetFullPath(dialog.FileName)) ?? string.Empty;
            var missingFiles = new List<string>();

            string? ResolvePath(string? absolutePath, string? relativePath)
            {
                if (!string.IsNullOrWhiteSpace(absolutePath) && File.Exists(absolutePath))
                {
                    return absolutePath;
                }

                if (!string.IsNullOrWhiteSpace(relativePath))
                {
                    var byRelative = Path.GetFullPath(Path.Combine(sessionDir, relativePath));
                    if (File.Exists(byRelative))
                    {
                        return byRelative;
                    }
                }

                if (!string.IsNullOrWhiteSpace(absolutePath))
                {
                    var byName = Path.Combine(sessionDir, Path.GetFileName(absolutePath));
                    if (File.Exists(byName))
                    {
                        return byName;
                    }
                }

                return null;
            }

            var savedTraces = session.TracePaths is { Count: > 0 }
                ? session.TracePaths
                : string.IsNullOrWhiteSpace(session.TracePath) ? Array.Empty<string>() : new[] { session.TracePath! };
            var tracePaths = new List<string>();
            var resolvedTracePaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < savedTraces.Count; i++)
            {
                var relative = session.TraceRelativePaths is { } rels && i < rels.Count
                    ? rels[i] : i == 0 ? session.TraceRelativePath : null;
                var resolved = ResolvePath(savedTraces[i], relative);
                if (resolved is not null)
                {
                    tracePaths.Add(resolved);
                    // The generic session loader may fall back to a file with
                    // the same basename. That is NOT enough to rebind a saved
                    // source-specific notebook. Only an existing exact path or
                    // the session's explicit relative path qualifies.
                    var exactAbsolute = File.Exists(savedTraces[i]) &&
                        string.Equals(Path.GetFullPath(savedTraces[i]), Path.GetFullPath(resolved),
                            StringComparison.OrdinalIgnoreCase);
                    var explicitRelative = !string.IsNullOrWhiteSpace(relative) &&
                        string.Equals(
                            Path.GetFullPath(Path.Combine(sessionDir, relative)), Path.GetFullPath(resolved),
                            StringComparison.OrdinalIgnoreCase);
                    if (exactAbsolute || explicitRelative)
                        resolvedTracePaths[savedTraces[i]] = resolved;
                }
                else missingFiles.Add(Path.GetFileName(savedTraces[i]));
            }
            if (tracePaths.Count > 0)
            {
                var mode = Enum.TryParse<TraceMergeMode>(session.TraceMergeMode, true, out var parsed)
                    ? parsed : TraceMergeMode.ByTime;
                await LoadTraceSessionAsync(tracePaths, mode);
            }

            var dbcRelativePaths = session.DbcRelativePaths;
            var dbcPaths = new List<string>();
            for (var i = 0; i < session.DbcPaths.Count; i++)
            {
                var relative = dbcRelativePaths is not null && i < dbcRelativePaths.Count ? dbcRelativePaths[i] : null;
                var resolved = ResolvePath(session.DbcPaths[i], relative);
                if (resolved is not null)
                {
                    dbcPaths.Add(resolved);
                }
                else
                {
                    missingFiles.Add(Path.GetFileName(session.DbcPaths[i]));
                }
            }

            if (dbcPaths.Count > 0)
            {
                await LoadDbcAsync(dbcPaths);
            }

            var catalogRelativePaths = session.CatalogRelativePaths;
            var catalogPaths = new List<string>();
            for (var i = 0; i < session.CatalogPaths.Count; i++)
            {
                var relative = catalogRelativePaths is not null && i < catalogRelativePaths.Count ? catalogRelativePaths[i] : null;
                var resolved = ResolvePath(session.CatalogPaths[i], relative);
                if (resolved is not null)
                {
                    catalogPaths.Add(resolved);
                }
                else
                {
                    missingFiles.Add(Path.GetFileName(session.CatalogPaths[i]));
                }
            }

            if (catalogPaths.Count > 0)
            {
                await Mapping.LoadCatalogsAsync(catalogPaths);
            }

            var mappingPath = ResolvePath(session.MappingPath, session.MappingRelativePath);
            if (mappingPath is not null)
            {
                await Mapping.LoadMapFileAsync(mappingPath);
            }
            else if (!string.IsNullOrWhiteSpace(session.MappingPath))
            {
                missingFiles.Add(Path.GetFileName(session.MappingPath));
            }

            ThemeIndex = session.ThemeIndex;
            Trace.ViewModeIndex = session.TraceViewModeIndex;
            Trace.PlaybackSpeed = session.TracePlaybackSpeed;
            Signals.PlaybackSpeed = session.SignalPlaybackSpeed;
            Signals.PlotTimeWindowSeconds = session.SignalWindowSeconds;
            ByteMatrix.PlaybackSpeed = session.ByteMatrixPlaybackSpeed;
            Mapping.StatusFilterIndex = session.MappingStatusFilterIndex;

            WatchSignals.Clear();
            foreach (var key in session.WatchKeys)
            {
                var series = FindSeriesBySavedKey(key);
                if (series is null)
                {
                    continue;
                }

                var row = new WatchSignalRow(series);
                row.Update(PlotCursorNanoseconds);
                WatchSignals.Add(row);
            }

            foreach (var key in session.PlottedKeys)
            {
                var series = FindSeriesBySavedKey(key);
                if (series is not null)
                {
                    series.IsPlotted = true;
                }
            }

            Trace.SearchText = session.TraceSearchText ?? string.Empty;
            Trace.ApplyFilterCommand.Execute(null);
            Trace.RuleText = session.TraceRuleText ?? string.Empty;
            // Restore named tabs only after all source paths were resolved and
            // the combined Trace snapshot was fully reconstructed.
            var notebookRestored = Notebook.RestoreFromSession(
                session.TraceNotebooks, session.ActiveTraceNotebookId, resolvedTracePaths);
            Signals.SearchText = session.SignalsSearchText ?? string.Empty;
            Matrix.SearchText = session.MatrixSearchText ?? string.Empty;
            ByteMatrix.FilterText = session.ByteMatrixFilterText ?? string.Empty;
            StatisticsSearchText = session.StatisticsSearchText ?? string.Empty;
            Mapping.SearchText = session.MappingSearchText ?? string.Empty;

            DataStudio.SectionIndex = Math.Clamp(session.DataStudioSectionIndex ?? 0,
                DataStudioViewModel.DataSection, DataStudioViewModel.CompareSection);
            SelectedNavIndex = session.SelectedNavIndex;
            StatusText = (missingFiles.Count == 0
                ? $"Session restored: {Path.GetFileName(dialog.FileName)}"
                : $"Session restored with {missingFiles.Count} file(s) not found: {string.Join(", ", missingFiles)}") +
                (notebookRestored ? "" :
                    " · Active analysis tab not restored: original source unavailable or incompatible view.");
        }
        catch (Exception exception)
        {
            StatusText = "Session restore failed: " + exception.Message;
            MessageBox.Show(exception.Message, "Session restore failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            IsBusy = false;
        }
    });

    /// <summary>Files the current workspace is built on (recordings, DBC, catalogs): what a saved Data Studio analysis must be
    /// able to verify later.</summary>
    public IReadOnlyList<(string Kind, string Path)> StudioSourcePaths()
    {
        var list = new List<(string, string)>();
        var traces = _traceWorkspacePaths.Count > 0
            ? _traceWorkspacePaths.ToArray()
            : string.IsNullOrWhiteSpace(_tracePath) ? [] : new[] { _tracePath! };
        list.AddRange(traces.Select(p => ("trace", p)));
        list.AddRange(_databasePaths.Values.Select(p => ("dbc", p)));
        list.AddRange(Mapping.CatalogPaths.Select(p => ("catalog", p)));
        return list;
    }

    /// <summary>Recipe of the current analysis (filters, tabs, section). Sources are described by the caller, off the UI thread.</summary>
    public StudioSavedAnalysis BuildStudioAnalysis(string name, IReadOnlyList<StudioSourceRef> sources) => new(
        StudioSavedAnalysisStore.CurrentSchemaVersion, Guid.NewGuid().ToString("N"), name, DateTime.UtcNow, sources,
        _traceWorkspaceMode.ToString(), Trace.SearchText, Trace.RuleText, Trace.ViewModeIndex, DataStudio.SectionIndex,
        Notebook.SnapshotForSession(), Notebook.ActiveId);

    /// <summary>
    /// Reopens a saved analysis. Missing sources abort it; changed sources abort unless <paramref name="confirmChanged"/>
    /// returns true. Never silently binds the analysis to different data. Returns a message for the user.
    /// </summary>
    public async Task<(bool Opened, string Message)> OpenStudioAnalysisAsync(
        StudioSavedAnalysis analysis, Func<string, bool> confirmChanged)
    {
        if (_traceImportRunning) return (false, "Finish the current trace import first.");
        var checks = await Task.Run(() => StudioSavedAnalysisStore.CheckSources(analysis));
        var problems = StudioSavedAnalysisStore.DescribeProblems(checks);
        if (checks.Any(c => c.State == StudioSourceState.Missing))
            return (false, $"“{analysis.Name}” was not opened. {problems}");
        if (problems.Length > 0 && !confirmChanged(problems))
            return (false, $"“{analysis.Name}” was not opened. {problems}");

        IsBusy = true;
        BusyText = "Opening analysis…";
        try
        {
            var tracePaths = analysis.Sources.Where(s => s.Kind == "trace").Select(s => s.Path).ToList();
            var dbcPaths = analysis.Sources.Where(s => s.Kind == "dbc").Select(s => s.Path).ToList();
            var catalogPaths = analysis.Sources.Where(s => s.Kind == "catalog").Select(s => s.Path).ToList();
            if (tracePaths.Count > 0)
            {
                var mode = Enum.TryParse<TraceMergeMode>(analysis.MergeMode, true, out var parsed) ? parsed : TraceMergeMode.ByTime;
                await LoadTraceSessionAsync(tracePaths, mode);
            }
            if (dbcPaths.Count > 0) await LoadDbcAsync(dbcPaths);
            if (catalogPaths.Count > 0) await Mapping.LoadCatalogsAsync(catalogPaths);

            Trace.ViewModeIndex = analysis.TraceViewModeIndex;
            Trace.SearchText = analysis.TraceSearchText ?? string.Empty;
            Trace.ApplyFilterCommand.Execute(null);
            Trace.RuleText = analysis.TraceRuleText ?? string.Empty;
            // Exact paths only: the saved sources were just verified to exist, so tabs rebind to the same files.
            var exact = tracePaths.ToDictionary(p => p, p => p, StringComparer.OrdinalIgnoreCase);
            var tabsRestored = Notebook.RestoreFromSession(analysis.Pages, analysis.ActiveTabId, exact);
            DataStudio.SectionIndex = Math.Clamp(analysis.SectionIndex, DataStudioViewModel.DataSection, DataStudioViewModel.CompareSection);
            SelectedNavIndex = DataStudioNavIndex;
            return (true, $"Opened analysis “{analysis.Name}”." + (tabsRestored ? "" : " Its active tab could not be restored."));
        }
        catch (Exception exception)
        {
            return (false, "Analysis not opened: " + exception.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public AsyncCommand CompareTraceCommand => _compareTraceCommand ??= new AsyncCommand(async () =>
    {
        if (_frames.Count == 0)
        {
            StatusText = "Load the base trace first.";
            return;
        }

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = TraceImporter.FileDialogFilter,
            Title = "Open comparison CAN trace"
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        await CompareWithAsync(dialog.FileName);
    });

    private SignalOverlayContext? _signalOverlayContext;

    /// <summary>True after a trace comparison that decoded signals in both recordings.</summary>
    public bool HasSignalOverlay => _signalOverlayContext is not null;
    public SignalOverlayContext? SignalOverlayContext => _signalOverlayContext;

    private static long FirstTimestamp(IReadOnlyList<CanFrame> frames)
    {
        if (frames.Count == 0) return 0;
        var first = frames[0].TimestampNanoseconds;
        for (var i = 1; i < frames.Count; i++) if (frames[i].TimestampNanoseconds < first) first = frames[i].TimestampNanoseconds;
        return first;
    }

    /// <summary>True when a previously compared trace is remembered as the stable reference.</summary>
    public bool HasReferenceTrace =>
        AppSettingsStore.LoadReferenceTracePath() is { Length: > 0 } reference && File.Exists(reference);

    /// <summary>Compares the loaded trace against the remembered reference trace without asking for a file.</summary>
    public AsyncCommand CompareWithReferenceCommand => _compareWithReferenceCommand ??= new AsyncCommand(async () =>
    {
        if (AppSettingsStore.LoadReferenceTracePath() is not { Length: > 0 } reference || !File.Exists(reference))
        {
            StatusText = "No reference trace is set. Use Compare trace… once to choose one.";
            return;
        }

        await CompareWithAsync(reference);
    });
    private AsyncCommand? _compareWithReferenceCommand;

    /// <summary>Compares the loaded (base) trace with another recording: per-identifier counts and
    /// rates, decoded signals, and a regression list of differences in timing, signal status and
    /// recorded diagnostics. The compared file is remembered as the stable reference.</summary>
    public async Task CompareWithAsync(string path, bool rememberAsReference = true)
    {
        if (_frames.Count == 0)
        {
            StatusText = "Load the base trace first.";
            return;
        }

        IsBusy = true;
        BusyText = "Comparing trace…";
        try
        {
            var compareFrames = new List<CanFrame>();
            await foreach (var frame in TraceImporter.ReadAsync(path))
            {
                compareFrames.Add(frame);
            }

            var compareStats = await Task.Run(() => TraceStatistics.Compute(compareFrames));
            BuildCompareRows(_statistics, compareStats);
            IReadOnlyList<SignalChangeReport>? compareReports = null;
            IReadOnlyList<SignalTimeline>? compareTimelinesBuilt = null;
            if (_decoders.Count > 0)
            {
                compareReports = await Task.Run(() =>
                {
                    var compareTimelines = SignalTimelineBuilder.Build(compareFrames, _decoders);
                    compareTimelinesBuilt = compareTimelines;
                    var dataFrames = compareFrames.Where(frame =>
                        !frame.IsError && !frame.Flags.HasFlag(CanFrameFlags.Remote));
                    var seenIdentifiers = dataFrames.Select(frame => frame.Identifier).ToHashSet();
                    var seenFrames = dataFrames
                        .Select(frame => (frame.Channel, frame.Identifier, frame.IsExtended)).ToHashSet();
                    return SignalChangeAnalyzer.Analyze(compareTimelines, _databases, seenIdentifiers,
                        seenFrameIdentities: seenFrames, activeDecoders: _decoders);
                });
                BuildSignalCompareRows(_reports, compareReports);
            }
            else
            {
                CompareSignalRows.Clear();
            }

            var catalogs = Mapping.Catalogs;
            var candidateDiagnostics = await Task.Run(() =>
                DiagnosticInsightAnalyzer.Analyze(UdsTransactionAnalyzer.Analyze(compareFrames, catalogs), catalogs));
            var findings = TraceRegressionAnalyzer.Compare(
                _statistics, compareStats,
                compareReports is null ? null : _reports, compareReports,
                _diagnosticSnapshot, candidateDiagnostics);
            RegressionRows.Clear();
            foreach (var finding in findings) RegressionRows.Add(finding);
            var warnings = findings.Count(item => item.Severity == RegressionSeverity.Warning);
            RegressionSummary = findings.Count == 0
                ? $"No differences found against {Path.GetFileName(path)} (identifiers, timing, signals, diagnostics)."
                : $"{findings.Count:N0} difference(s) against {Path.GetFileName(path)} · {warnings:N0} warning(s). " +
                  "Differences describe what changed, not why.";

            _signalOverlayContext = compareTimelinesBuilt is null ? null : new SignalOverlayContext(
                _timelines, compareTimelinesBuilt, FirstTimestamp(_framesSnapshot), FirstTimestamp(compareFrames), Path.GetFileName(path));
            OnPropertyChanged(nameof(HasSignalOverlay));
            if (rememberAsReference) AppSettingsStore.SaveReferenceTracePath(path);
            OnPropertyChanged(nameof(HasReferenceTrace));
            CompareSummary = $"Compared with {Path.GetFileName(path)} · {CompareTraceRows.Count:N0} ID rows" +
                             (_decoders.Count > 0 ? $" · {CompareSignalRows.Count:N0} decoded signal rows" : string.Empty);
            SelectedNavIndex = 6;
        }
        catch (Exception exception)
        {
            StatusText = "Compare failed: " + exception.Message;
            MessageBox.Show(exception.Message, "Compare failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public AsyncCommand ExportReportCommand => _exportReportCommand ??= new AsyncCommand(async () =>
    {
        if (_frames.Count == 0)
        {
            StatusText = "Load a trace before exporting an analysis report.";
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "Analysis report package (*.zip)|*.zip",
            DefaultExt = ".zip",
            FileName = BuildExportBaseName("analysis-report").Replace(".csv", ".zip"),
            Title = "Export analysis report"
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            // Rendering the view to a bitmap touches UI elements, so it must happen on this
            // (UI) thread before handing off to the background thread for zip/file I/O.
            var screenshot = CaptureViewScreenshot?.Invoke();
            // Freeze the optional trigger result and its exact rule on the UI
            // thread. An in-progress evaluation must not mix two different
            // snapshots in the exported source evidence.
            var triggerSnapshot = Triggers.LastReport;
            var triggerRuleSnapshot = Triggers.LastEvaluatedRule;
            var sequenceSnapshot = Sequences.LastReport;
            var rawValueSnapshot = DbcValues.LastReport;
            await Task.Run(() => WriteAnalysisReport(dialog.FileName, screenshot,
                triggerSnapshot, triggerRuleSnapshot, sequenceSnapshot, rawValueSnapshot));
            StatusText = $"Analysis report exported: {Path.GetFileName(dialog.FileName)}";
        }
        catch (Exception exception)
        {
            StatusText = "Report export failed: " + exception.Message;
            MessageBox.Show(exception.Message, "Report export failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    });

    public RelayCommand AboutCommand => _aboutCommand ??= new RelayCommand(() =>
        MessageBox.Show(
            $"{AppTitle}\n{AppTagline}\n\n" +
            "Offline workflow guide:\n\n" +
            "1. Load trace\n" +
            "Open ASC, BLF, TRC, CSV, LOG/TXT or MF4/MDF. The Trace view shows every frame, the full density overview, payload deltas, decoded signals and source ECU/TX when available.\n\n" +
            "2. Load CAN databases\n" +
            "Load DBC or ARXML. Use DBC Manager to check exactly which file is loaded, message/signal comments, bus assignment, J1939 PGN matching and unmatched IDs.\n\n" +
            "3. Navigate the trace\n" +
            "Use the search box for ID, message, channel or payload. Enter filters rows; F3/Shift+F3 jumps between matches. Mark rows manually or with rule sets such as id:18FEF100, payload:62F190 or VehicleSpeed > 20.\n\n" +
            "4. Plot and replay\n" +
            "Use Signals & Plot, Trace signal details, Change Matrix or Byte Matrix to plot signals. Play resumes from wherever the view currently is, restarting from the beginning only once it reaches the end; speed controls the offline replay rate.\n\n" +
            "5. Analyze changes\n" +
            "Change Matrix classifies every decoded signal as stable, variable, anomalous or missing. Statistics shows rate, period, jitter and compare between two traces, including decoded signal comparison when databases are loaded.\n\n" +
            "6. Map DIDs to CAN\n" +
            "Load CDD/ODX in DID-CAN Mapping, run auto-correlate, then review candidates. ISO-TP UDS 0x62 responses found in the trace are used as extra evidence, but low-confidence matches remain To verify or Unmatched.\n\n" +
            "7. Export and layouts\n" +
            "Export visible traces, decoded signals, matrix, mapping and full analysis reports. Use Columns, Export layout and Import layout to keep table setup consistent between portable folders.\n\n" +
            $"Version {System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)}\n" +
            $"{DesignerCredit}\n© 2026 Paolo Rubagotti. All rights reserved.",
            $"About {AppTitle}",
            MessageBoxButton.OK,
            MessageBoxImage.Information));

    public RelayCommand<DbcDatabaseRowViewModel> RemoveDbcCommand => _removeDbcCommand ??= new RelayCommand<DbcDatabaseRowViewModel>(
        async row =>
        {
            if (row is null)
            {
                return;
            }

            if (DbcEditor.HasStagedChangesFor(row.Name) &&
                MessageBox.Show("This DBC has staged edits. Remove it and discard the working copy?",
                    "Discard DBC changes", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;
            DbcEditor.ForgetDatabase(row.Name);
            _databases.RemoveAll(database => string.Equals(database.Name, row.Name, StringComparison.OrdinalIgnoreCase));
            _databasePaths.Remove(row.Name);
            _dbcOptions.Remove(row.Name);
            await RebuildAnalysisAsync();
            StatusText = $"{row.FileName} removed. {DbcSummary}";
        });

    public RelayCommand ClearDbcCommand => _clearDbcCommand ??= new RelayCommand(async () =>
    {
        if (DbcEditor.HasStagedChanges &&
            MessageBox.Show("Discard all staged DBC edits and unload the databases?",
                "Discard DBC changes", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        DbcEditor.ForgetAll();
        _databases.Clear();
        _databasePaths.Clear();
        _dbcOptions.Clear();
        await RebuildAnalysisAsync();
        StatusText = "All DBC databases removed.";
    });

    public RelayCommand<RecentFile> OpenRecentCommand => _openRecentCommand ??= new RelayCommand<RecentFile>(
        async recent =>
        {
            if (recent is null)
            {
                return;
            }

            if (!File.Exists(recent.Path))
            {
                StatusText = "File not found: " + recent.Path;
                RecentFiles.Remove(recent);
                SaveRecentFiles();
                return;
            }

            if (recent.Kind == "dbc")
            {
                await LoadDbcAsync([recent.Path]);
            }
            else
            {
                await LoadTraceAsync(recent.Path);
            }
        });

    public RelayCommand<SignalDetailRow> PlotTraceSignalCommand => _plotTraceSignalCommand ??= new RelayCommand<SignalDetailRow>(
        signal =>
        {
            if (signal is null)
            {
                return;
            }

            var series = Signals.Plot(signal.Message, signal.Signal,
                channel: signal.SourceChannel >= 0 ? signal.SourceChannel : null,
                databaseName: string.IsNullOrWhiteSpace(signal.DatabaseName) ? null : signal.DatabaseName,
                identifier: signal.DefinitionIdentifier,
                isExtended: signal.SourceIsExtended,
                sourceIdentifier: signal.FrameIdentifier);
            if (series?.IsPlotted == true)
            {
                if (Trace.SelectedRow is { } source)
                    Signals.SeekToTimestamp(source.TimestampNanoseconds);
                SelectedNavIndex = 3;
            }
        });

    /// <summary>Trace decoded-signal ＋: idempotent, exact-source add to the
    /// existing chart, without leaving the current page or clearing curves.
    /// Keep the main chart positioned on the actual selected CAN evidence.</summary>
    public RelayCommand<SignalDetailRow> QuickPlotTraceSignalCommand => _quickPlotTraceSignalCommand ??= new RelayCommand<SignalDetailRow>(
        signal =>
        {
            if (signal is null) return;

            var series = Signals.EnsurePlotted(signal.Message, signal.Signal,
                channel: signal.SourceChannel >= 0 ? signal.SourceChannel : null,
                databaseName: string.IsNullOrWhiteSpace(signal.DatabaseName) ? null : signal.DatabaseName,
                identifier: signal.DefinitionIdentifier,
                isExtended: signal.SourceIsExtended,
                sourceIdentifier: signal.FrameIdentifier);
            if (series is null)
            {
                StatusText = "Cannot resolve the exact CAN/DBC/logger source for this signal. " +
                    Signals.StatusText;
                return;
            }
            if (Signals.HasMultipleLoggers && Signals.SelectedLoggerIndex >= 0 &&
                Signals.SelectedLoggerIndex != series.Timeline.OriginalSourceIndex &&
                series.Timeline.OriginalSourceIndex >= 0)
                Signals.SelectedLoggerIndex = series.Timeline.OriginalSourceIndex;

            if (signal.SourceTimestampNanoseconds >= 0)
            {
                Signals.SeekToTimestamp(signal.SourceTimestampNanoseconds);
                PlotCursorNanoseconds = signal.SourceTimestampNanoseconds;
            }
            StatusText = $"Added {series.MessageName}.{series.SignalName} from " +
                $"{series.RecordingText} / {series.ChannelText} to main plot; " +
                "existing curves kept. Open Signals & Plot or Signals + trend to inspect it.";
        });

    public RelayCommand<SignalDetailRow> WatchTraceSignalCommand => _watchTraceSignalCommand ??= new RelayCommand<SignalDetailRow>(
        signal =>
        {
            if (signal is null)
            {
                return;
            }

            var series = Signals.EnsurePlotted(signal.Message, signal.Signal,
                channel: signal.SourceChannel >= 0 ? signal.SourceChannel : null,
                databaseName: string.IsNullOrWhiteSpace(signal.DatabaseName) ? null : signal.DatabaseName,
                identifier: signal.DefinitionIdentifier,
                isExtended: signal.SourceIsExtended,
                sourceIdentifier: signal.FrameIdentifier);
            if (series is null)
            {
                StatusText = "Signal is not available in the decoded timelines.";
                return;
            }

            if (WatchSignals.Any(row => row.Series.Timeline.Key == series.Timeline.Key))
            {
                StatusText = $"{series.MessageName}.{series.SignalName} is already in the watch list.";
                return;
            }

            var row = new WatchSignalRow(series);
            row.Update(PlotCursorNanoseconds);
            WatchSignals.Add(row);
            StatusText = $"Watching {series.MessageName}.{series.SignalName}.";
        });

    public RelayCommand<WatchSignalRow> RemoveWatchSignalCommand => _removeWatchSignalCommand ??= new RelayCommand<WatchSignalRow>(
        row =>
        {
            if (row is not null)
            {
                WatchSignals.Remove(row);
            }
        });

    private RelayCommand? _openSelectedPlotSourceCommand;
    public RelayCommand OpenSelectedPlotSourceCommand => _openSelectedPlotSourceCommand ??= new RelayCommand(() =>
    {
        var series = Signals.SelectedSeries;
        if (series is null)
        {
            StatusText = "Select a recorded signal in Signals & Plot first.";
            return;
        }

        var end = Signals.PlotWindowEndNanoseconds;
        var time = end >= 0 ? end :
            series.Samples.Count > 0 ? series.Samples[^1].TimestampNanoseconds : -1;
        var observed = series.Timeline.SampleAt(time);
        if (observed is not { } sample)
        {
            StatusText = "This signal has no recorded source sample at or before the plot cursor.";
            return;
        }

        OpenTraceFrame(sample.FrameIndex);
        StatusText = $"Source frame {sample.FrameIndex + 1:N0} of {series.ChannelText} / " +
            $"{series.MessageName}.{series.SignalName}, at {sample.TimestampNanoseconds / 1e9:F6}s.";
    });

    public RelayCommand PlotSelectedTraceMessageCommand => _plotSelectedTraceMessageCommand ??= new RelayCommand(() =>
        PlotSelectedTraceMessage(navigateToPlot: true));

    /// <summary>Adds the selected trace row's decoded signals to the main chart,
    /// optionally leaving the user on the Trace page.</summary>
    public void PlotSelectedTraceMessage(bool navigateToPlot)
    {
        if (Trace.SelectedRow is null)
        {
            StatusText = "Select a decoded trace row first.";
            return;
        }

        var plotted = 0;
        foreach (var signal in Trace.SelectedRowSignals)
        {
            if (Signals.EnsurePlotted(signal.Message, signal.Signal,
                    channel: signal.SourceChannel >= 0 ? signal.SourceChannel : null,
                    databaseName: string.IsNullOrWhiteSpace(signal.DatabaseName) ? null : signal.DatabaseName,
                    identifier: signal.DefinitionIdentifier,
                    isExtended: signal.SourceIsExtended,
                    sourceIdentifier: signal.FrameIdentifier) is not null)
            {
                plotted++;
            }
        }

        if (plotted > 0)
        {
            Signals.SeekToTimestamp(Trace.SelectedRow.TimestampNanoseconds);
            if (navigateToPlot)
                SelectedNavIndex = 3;
            StatusText = navigateToPlot
                ? $"Plotted {plotted} signal(s) from {Trace.SelectedRow.MessageName}."
                : $"Added {plotted} signal(s) from {Trace.SelectedRow.MessageName} to the chart — open Signals & Plot to see it.";
        }
        else
        {
            StatusText = "Selected trace row has no decoded signals to plot.";
        }
    }

    public RelayCommand PlotSelectedByteMatrixRowCommand => _plotSelectedByteMatrixRowCommand ??= new RelayCommand(() =>
    {
        if (ByteMatrix.SelectedRow is null)
        {
            StatusText = "Select a byte matrix row first.";
            return;
        }

        var plotted = 0;
        foreach (var signal in ByteMatrix.SelectedRowSignals)
        {
            if (Signals.EnsurePlotted(signal.Message, signal.Signal,
                    channel: signal.SourceChannel >= 0 ? signal.SourceChannel : null,
                    databaseName: string.IsNullOrWhiteSpace(signal.DatabaseName) ? null : signal.DatabaseName,
                    identifier: signal.DefinitionIdentifier,
                    isExtended: signal.SourceIsExtended,
                    sourceIdentifier: signal.FrameIdentifier) is not null)
            {
                plotted++;
            }
        }

        if (plotted > 0)
        {
            SelectedNavIndex = 3;
            StatusText = $"Plotted {plotted} signal(s) from ID {ByteMatrix.SelectedRow.Identifier}.";
        }
        else
        {
            StatusText = "Selected byte matrix row has no decoded signals to plot.";
        }
    });

    public RelayCommand PlotSelectedMappingCommand => _plotSelectedMappingCommand ??= new RelayCommand(() =>
    {
        var candidate = Mapping.SelectedRow?.ActiveCandidate;
        if (candidate is null)
        {
            StatusText = "Select a DID row with a CAN candidate first.";
            return;
        }

        var series = Signals.EnsurePlotted(candidate.Message.Name, candidate.Signal.Name,
            databaseName: candidate.Database,
            identifier: candidate.Message.Identifier, isExtended: candidate.Message.IsExtended);
        if (series is null)
        {
            StatusText = "Candidate signal is not present in the decoded trace. Check DBC coverage or load a trace.";
            return;
        }

        SelectedNavIndex = 3;
        StatusText = $"Plotted mapping candidate {candidate.Message.Transmitter}.{candidate.Message.Name}.{candidate.Signal.Name}.";
    });

    /// <summary>Start small for ordinary engineering captures instead of
    /// eagerly reserving roughly a million CAN structs before reading one
    /// line. Use a bounded file-size estimate for large recordings; BLF/MDF
    /// compression ratios vary, so this is only a memory-allocation hint.
    /// No record is ever dropped to meet the estimate.</summary>
    private static int EstimateInitialFrameCapacity(string path)
    {
        var fileLength = new FileInfo(path).Length;
        var bytesPerFrame = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".log" or ".txt" => 38L,
            ".blf" => 48L,
            ".mf4" or ".mdf" => 64L,
            _ => 52L // ASC/CSV/TRC metadata and ordinary text line overhead
        };
        return (int)Math.Clamp(fileLength / bytesPerFrame, 4_096L, 1 << 20);
    }

    public Task LoadTraceAsync(string path) => LoadTraceCoreAsync(path, null, TraceMergeMode.ByTime);

    /// <summary>Opens several logs as one offline session, combined by <paramref name="mode"/>.
    /// A single path behaves exactly like <see cref="LoadTraceAsync"/>.</summary>
    public Task LoadTraceSessionAsync(IReadOnlyList<string> paths, TraceMergeMode mode)
    {
        if (paths.Count == 0) throw new ArgumentException("At least one trace is required.", nameof(paths));
        var distinct = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return LoadTraceCoreAsync(distinct[0], distinct.Length > 1 ? distinct : null, mode);
    }

    private async Task LoadTraceCoreAsync(string path, IReadOnlyList<string>? sessionPaths,
        TraceMergeMode sessionMode, bool preserveWorkspace = false, string? activeSourcePath = null)
    {
        if (_traceImportRunning) return;
        _traceImportRunning = true;
        // A background aggregate may be enumerating a memory-mapped frame
        // snapshot. Cancel AND await its completion before this import can
        // replace/dispose that snapshot, never race a large trace reload.
        await DataStudio.CancelAndInvalidateAggregatesAsync();
        IsBusy = true;
        BusyText = sessionPaths is null
            ? $"Importing {Path.GetFileName(path)}…"
            : $"Importing {sessionPaths.Count} traces ({sessionMode})…";
        var importDiagnostics = new TraceImportHealth();
        var sourceCommitted = false;
        CanFrameCollection? imported = null;
        TraceSessionImportResult? multiImport = null;
        var previousFrames = _loadedFrames;
        try
        {
            var originalSourceStamp = TraceSourceFingerprint.ReadStamp(path);
            AdaptiveCanFrameCollector.CleanUpStaleSpillFiles();
            // Ordinary traces stay in a plain in-memory list. Past the spill threshold the capture
            // is streamed to a memory-mapped file instead, so multi-million-frame recordings do
            // not need to fit in RAM. No frame is dropped either way.
            using var collector = new AdaptiveCanFrameCollector(EstimateInitialFrameCapacity(path));
            await Task.Run(async () =>
            {
                if (sessionPaths is not null)
                {
                    multiImport = await TraceSession.ImportIntoAsync(sessionPaths,
                        sessionMode, importDiagnostics, frame => collector.Add(frame));
                    return;
                }

                await foreach (var frame in TraceImporter.ReadAsync(path,
                                   diagnostics: importDiagnostics))
                {
                    collector.Add(frame);
                }
            });
            var importedHealth = importDiagnostics.Snapshot();

            if (collector.Count == 0)
            {
                if (importDiagnostics.BusFrames.Total > 0)
                    BusFrames = importDiagnostics.BusFrames;
                var empty = LoadFailureReport.NoUsableFrames(
                    Path.GetFileName(path), importedHealth.Summary, previousFrames is not null);
                StatusText = empty.StatusLine;
                MessageBox.Show(empty.Message, empty.Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Most BLF/ASC captures already arrive chronologically. Sorting
            // millions of correctly ordered frames wastes O(N log N) CPU.
            // ImportHealth observes the ORIGINAL file order, before any sort.
            // Only disorder needs a sort, and equal timestamp ties preserve
            // the recorder's source sequence for ISO-TP correctness.
            imported = await Task.Run(() =>
                collector.Complete(sortByTime: sessionPaths is null && importedHealth.OutOfOrderTimestamps > 0));
            // Hash the source while it still has the same metadata as at the
            // beginning of import. A moved/modified trace gets no trusted ID.
            // A multi-file session has no single source file to fingerprint.
            var sourceFingerprint = sessionPaths is not null ? null : await Task.Run(() =>
                TraceSourceFingerprint.TryCapture(path, originalSourceStamp));
            var frames = imported.Frames;
            _frames = frames;
            _framesSnapshot = frames;
            _loadedFrames = imported;
            _traceImportHealth = importedHealth;
            BusFrames = importDiagnostics.BusFrames;
            _traceSourceFingerprint = sourceFingerprint;
            _traceSourceIndex = multiImport?.SourceIndex;
            if (!preserveWorkspace || multiImport is not null)
                _traceSourceSummaries = multiImport?.Sources ?? [];
            sourceCommitted = true;
            OnPropertyChanged(nameof(ImportHealth));
            OnPropertyChanged(nameof(ImportHealthSummary));
            OnPropertyChanged(nameof(ImportChannelRows));
            OnPropertyChanged(nameof(ImportIssueRows));
            OnPropertyChanged(nameof(Frames));
            TraceName = sessionPaths is null
                ? Path.GetFileName(path)
                : $"{Path.GetFileName(path)} + {sessionPaths.Count - 1} more ({sessionMode})";
            _tracePath = path;
            if (!preserveWorkspace)
            {
                var newSources = sessionPaths ?? new[] { path };
                if (_traceWorkspacePaths.Count > 0 &&
                    !_traceWorkspacePaths.Any(existing =>
                        newSources.Contains(existing, StringComparer.OrdinalIgnoreCase)))
                    Notebook.ClearForNewDataset();
                _traceWorkspacePaths.Clear();
                _traceWorkspacePaths.AddRange(newSources);
                _traceWorkspaceMode = sessionMode;
            }
            _activeTraceWorkspacePath = preserveWorkspace ? activeSourcePath : null;
            RefreshTraceWorkspaceEntries();
            if (!preserveWorkspace)
                foreach (var sourcePath in sessionPaths ?? new[] { path })
                    AddRecent(sourcePath, "trace");
            await RebuildAnalysisAsync();
            StatusText = $"Trace loaded: {TraceSummary} · {ImportHealthSummary}";
            if (SelectedNavIndex == 0)
            {
                SelectedNavIndex = 1;
            }
        }
        catch (Exception exception)
        {
            var attempt = importDiagnostics.Snapshot();
            if (sourceCommitted)
            {
                StatusText = $"Trace data imported, but subsequent analysis failed: {exception.Message}. " +
                             "The import snapshot remains attached to the loaded frames.";
                MessageBox.Show(exception.Message, "Trace analysis failed",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            else
            {
                imported?.Dispose(); // completed but never attached: release its spill file
                var failure = LoadFailureReport.Trace(
                    Path.GetFileName(path), exception, attempt.Frames, previousFrames is not null);
                StatusText = failure.StatusLine;
                MessageBox.Show(failure.Message, failure.Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        finally
        {
            // Every view model has switched to the new frames (or the new trace was rejected);
            // only now is it safe to release the previous trace's spill file.
            if (sourceCommitted && !ReferenceEquals(previousFrames, _loadedFrames))
            {
                previousFrames?.Dispose();
            }

            _traceImportRunning = false;
            IsBusy = false;
        }
    }

    /// <summary>Releases the loaded trace's frame storage (deleting any spill file). Called on exit.</summary>
    public void ReleaseTraceStorage()
    {
        var frames = _loadedFrames;
        _loadedFrames = null;
        _frames = [];
        _framesSnapshot = [];
        BusFrames = new BusFrameCollection();
        _traceSourceFingerprint = null;
        _traceSourceIndex = null;
        _traceSourceSummaries = [];
        _traceWorkspacePaths.Clear();
        _activeTraceWorkspacePath = null;
        RefreshTraceWorkspaceEntries();
        Triggers.SetTimelines([]);
        Sequences.SetSource([], []);
        frames?.Dispose();
    }

    public async Task LoadDbcAsync(IReadOnlyList<string> paths)
    {
        IsBusy = true;
        BusyText = "Parsing CAN database…";
        try
        {
            foreach (var path in paths)
            {
                try
                {
                    var database = await Task.Run(() =>
                        Path.GetExtension(path).Equals(".arxml", StringComparison.OrdinalIgnoreCase)
                            ? new ArxmlParser().ParseFile(path)
                            : new DbcParser().ParseFile(path));

                    // A file that yields no messages is not a usable database: refuse it loudly instead of
                    // adding an empty entry that looks like a successful load.
                    if (DbcLoadChecks.HasNoMessages(database))
                    {
                        var reason = database.ParseWarnings.Count > 0
                            ? Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine, database.ParseWarnings.Take(3))
                            : string.Empty;
                        MessageBox.Show(
                            $"{Path.GetFileName(path)} contains no CAN messages, so it was not added. The file is not a valid DBC or ARXML database, or it is empty." + reason,
                            "CAN database not loaded",
                            MessageBoxButton.OK,
                            MessageBoxImage.Warning);
                        StatusText = $"{Path.GetFileName(path)} was not added: no CAN messages could be parsed.";
                        continue;
                    }

                    // Same file reloaded → replace. Different file with the same name → keep
                    // both under a disambiguated name instead of silently swallowing one.
                    if (_databasePaths.TryGetValue(database.Name, out var existingPath) &&
                        !string.Equals(existingPath, path, StringComparison.OrdinalIgnoreCase))
                    {
                        var unique = database.Name;
                        var suffix = 2;
                        while (_databasePaths.ContainsKey(unique))
                        {
                            unique = $"{database.Name} ({suffix++})";
                        }

                        database = database with { Name = unique };
                    }
                    else
                    {
                        if (DbcEditor.HasStagedChangesFor(database.Name) &&
                            MessageBox.Show("Reloading this DBC discards its staged edits. Continue?",
                                "DBC reload", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                            continue;
                        DbcEditor.ForgetDatabase(database.Name);
                        _databases.RemoveAll(existing => existing.Name == database.Name);
                    }

                    var duplicateIds = DbcLoadChecks.FindDuplicateMessageIds(
                        database, _databases, other => DbcsCanShareChannel(database.Name, other.Name));
                    if (duplicateIds.Count > 0)
                        database = database with { Warnings = database.ParseWarnings.Concat(duplicateIds).ToArray() };

                    _databases.Add(database);
                    _databasePaths[database.Name] = path;
                    AddRecent(path, "dbc");
                }
                catch (Exception exception)
                {
                    var failure = LoadFailureReport.Database(Path.GetFileName(path), exception);
                    StatusText = failure.StatusLine;
                    MessageBox.Show(failure.Message, failure.Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }

            await RebuildAnalysisAsync();
            StatusText = $"{DbcSummary} · {DecodeCoverageSummary}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Per-database matching options as chosen in the DBC Manager.</summary>
    private DbcDecoderOptions OptionsFor(DbcDatabase database)
    {
        var state = _dbcOptions.GetValueOrDefault(database.Name, new DbcOptionsState(false, 0));
        return new DbcDecoderOptions(
            state.MatchByPgn,
            state.ChannelIndex > 0 ? state.ChannelIndex - 1 : null);
    }

    private DbcDecoder[] BuildDecoders() =>
        _databases.Select(database => new DbcDecoder(database, OptionsFor(database))).ToArray();

    /// <summary>Channel index 0 means "All channels"; two databases can only decode the same frames when either
    /// applies to every channel or both are pinned to the same one.</summary>
    private bool DbcsCanShareChannel(string first, string second)
    {
        var a = _dbcOptions.GetValueOrDefault(first, new DbcOptionsState(false, 0)).ChannelIndex;
        var b = _dbcOptions.GetValueOrDefault(second, new DbcOptionsState(false, 0)).ChannelIndex;
        return a == 0 || b == 0 || a == b;
    }

    private IReadOnlyList<string> BuildChannelChoices()
    {
        var choices = new List<string> { "All channels" };
        choices.AddRange(_frames
            .Select(frame => frame.Channel)
            .Distinct()
            .OrderBy(channel => channel)
            .Select(channel => $"Ch {channel + 1} only"));
        if (choices.Count == 1)
        {
            choices.Add("Ch 1 only");
        }

        return choices;
    }

    private async Task RebuildAnalysisAsync()
    {
        var frames = _frames;
        // Capture the exact source mapping belonging to this frame snapshot
        // before dispatch. A newer import must not change the function used
        // by an older background UDS run.
        var sourceIndex = _traceSourceIndex;
        var singleSourcePath = CurrentTracePhysicalPath;
        var physicalSourceAt = BuildAnalysisSourceAt(frames, sourceIndex, singleSourcePath);
        IReadOnlyList<string> physicalSourcePaths = sourceIndex?.Paths ??
            (string.IsNullOrWhiteSpace(singleSourcePath) ? [] : [singleSourcePath]);
        var databases = _databases.ToArray();
        var diagnosticCatalogs = Mapping.Catalogs;

        _analysisCancellation?.Cancel();
        _analysisCancellation?.Dispose();
        var cancellation = new System.Threading.CancellationTokenSource();
        _analysisCancellation = cancellation;
        var token = cancellation.Token;

        BusyText = "Decoding signals…";
        BusyProgressPercent = null;
        IsBusy = true;
        try
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            // IProgress<T> captures this thread's SynchronizationContext at construction time, so
            // reports below marshal back to the UI thread automatically.
            var udsProgress = new Progress<UdsAnalysisProgress>(report => ReportAnalysisProgress(report, stopwatch));

            var (timelines, reports, statistics, coverage, decoders, autoResolved, udsInsight, udsCorrelations, offlineAnalysis, cycleAnalysis, stateTransitions, networkMatrix) = await Task.Run(() =>
            {
                IReadOnlyList<MessageStatisticsRow> builtStatistics =
                    frames.Count > 0 ? TraceStatistics.Compute(frames) : [];

                var initialDecoders = BuildDecoders();
                var resolved = frames.Count > 0
                    ? AutoResolveUnmatchedIdentifiers(builtStatistics, databases, initialDecoders)
                    : 0;
                var finalDecoders = resolved > 0 ? BuildDecoders() : initialDecoders;

                var builtCoverage = ComputeCoverage(builtStatistics, databases, finalDecoders);
                // Link DBC-declared GenMsgCycleTime to independent actual raw
                // frames; no inference from other buses, PGN sources or IDs.
                var builtCycleAnalysis = DbcCycleTimingAnalyzer.Analyze(
                    frames, builtStatistics, finalDecoders,
                    cancellationToken: token);

                IReadOnlyList<SignalTimeline> builtTimelines = [];
                IReadOnlyList<SignalChangeReport> builtReports = [];
                if (frames.Count > 0 && databases.Length > 0)
                {
                    builtTimelines = SignalTimelineBuilder.Build(frames, finalDecoders, token,
                        physicalSourceAt, physicalSourcePaths);
                    // One frame walk creates both the legacy bus/ID index and
                    // its original-file-scoped counterpart. Missing-signal
                    // classification must never infer Logger 2's behavior
                    // from a CAN identifier observed only in Logger 1.
                    var seenFrames = new HashSet<(int Channel, uint Identifier, bool Extended)>();
                    HashSet<(int Source, int Channel, uint Identifier, bool Extended)>? seenByLogger =
                        physicalSourceAt is null ? null : [];
                    for (var i = 0; i < frames.Count; i++)
                    {
                        if ((i & 0xFFFF) == 0) token.ThrowIfCancellationRequested();
                        var frame = frames[i];
                        if (frame.IsError || frame.Flags.HasFlag(CanFrameFlags.Remote))
                            continue;
                        seenFrames.Add((frame.Channel, frame.Identifier, frame.IsExtended));
                        if (seenByLogger is not null)
                            seenByLogger.Add((physicalSourceAt!(i), frame.Channel,
                                frame.Identifier, frame.IsExtended));
                    }
                    builtReports = SignalChangeAnalyzer.Analyze(
                        builtTimelines, databases, builtCoverage.SeenMessageIdentifiers, token,
                        seenFrameIdentities: seenFrames, activeDecoders: finalDecoders,
                        seenSourceFrameIdentities: seenByLogger,
                        sourcePaths: physicalSourcePaths);
                }

                var builtUdsTransactions = frames.Count > 0
                    ? UdsTransactionAnalyzer.Analyze(
                        frames, diagnosticCatalogs, udsProgress, token,
                        physicalSourceAt)
                    : [];
                var builtUdsInsight = DiagnosticInsightAnalyzer.Analyze(builtUdsTransactions, diagnosticCatalogs, frames,
                        sourceAt: physicalSourceAt);
                var builtUdsCorrelations = BuildUdsCanCorrelations(
                    builtUdsInsight, diagnosticCatalogs, databases, builtTimelines,
                    physicalSourceAt);
                var builtOfflineAnalysis = OfflineAnalysisAnalyzer.Analyze(
                    builtReports,
                    TraceStatistics.FindTimingIncidents(frames, builtStatistics),
                    builtUdsInsight,
                    diagnosticCatalogs,
                    builtCycleAnalysis);

                var builtStateTransitions = SignalStateTransitionAnalyzer.Analyze(builtTimelines, cancellationToken: token);
                var builtNetworkMatrix = DbcNetworkMatrixAnalyzer.Analyze(finalDecoders, builtStatistics);
                return (builtTimelines, builtReports, builtStatistics, builtCoverage, finalDecoders, resolved,
                    builtUdsInsight, builtUdsCorrelations, builtOfflineAnalysis, builtCycleAnalysis,
                    builtStateTransitions, builtNetworkMatrix);
            }, token);

            // A newly loaded trace can replace the capture while a costly
            // offline analysis is still computing. Do not publish stale
            // source-frame indices, charts or DID evidence after that.
            token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(frames, _framesSnapshot))
                return;

            _decoders = decoders;
            _timelines = timelines;
            _reports = reports;
            _statistics = statistics;
            _cycleTimingResult = cycleAnalysis;
            _stateTransitionReport = stateTransitions;
            _networkMatrixReport = networkMatrix;
            _coverageByDb = coverage.ByDatabase;
            _unmatchedRows = coverage.Unmatched;
            _decodedFrameCount = coverage.DecodedFrames;
            _decodedIdentifierCount = coverage.DecodedIdentifiers;
            _decodedSampleCount = _timelines.Sum(timeline => (long)timeline.Samples.Count);
            _lastAutoResolvedUnmatchedCount = autoResolved;

            Func<int, int>? sourceAt = physicalSourceAt;
            Trace.SetSession(frames, decoders, TraceSourceNameAt, sourceAt,
                TraceSourceEvidenceAt);
            OnPropertyChanged(nameof(CanOpenOriginalSource));
            ByteMatrix.SetSession(frames, decoders);
            Signals.SetTimelines(_timelines, physicalSourcePaths: physicalSourcePaths);
            RefreshWatchSignals(removeMissing: true);
            Matrix.SetDataState(_frames.Count > 0, _databases.Count > 0);
            Matrix.SetReports(_reports, physicalSourcePaths: physicalSourcePaths);
            // Grouped (not ToDictionary) on an identifier-based key: homonymous message/signal
            // names across databases or channels must never crash the analysis pipeline.
            Mapping.SetContext(
                databases,
                frames,
                _reports.Count == 0
                    ? null
                    : _reports
                        .GroupBy(report => DidSignalCorrelator.TraceReportKey(
                            report.Timeline.DatabaseName, report.Timeline.Message, report.Timeline.Signal))
                        // One DBC signal received on multiple CAN channels has no
                        // unique physical trace evidence without an explicit bus assignment.
                        .Where(group => group.Count() == 1)
                        .ToDictionary(group => group.Key, group => group.Single()),
                sourceAt: physicalSourceAt,
                multipleTraceSources: physicalSourcePaths.Count > 1 ||
                    (physicalSourceAt is null && HasMultipleTraceSources));
            Uds.SetAnalysis(udsInsight, udsCorrelations, offlineAnalysis,
                sourceFrames: frames,
                physicalSourcePaths: physicalSourcePaths,
                physicalSourceAt: physicalSourceAt,
                sourceNameAt: TraceSourceNameAt,
                sourceEvidenceAt: TraceSourceEvidenceAt);
            _diagnosticSnapshot = udsInsight;
            DataStudio.RefreshDiagnosticWorkbench();
            CycleTiming.SetResult(cycleAnalysis);
            StateTransitions.SetReport(stateTransitions);
            NetworkMatrix.SetReport(networkMatrix);
            Triggers.SetTimelines(timelines);
            Sequences.SetSource(timelines, frames);

            OnPropertyChanged(nameof(IsTraceLoaded));
            OnPropertyChanged(nameof(FrameCount));
            OnPropertyChanged(nameof(TraceSummary));
            OnPropertyChanged(nameof(DbcSummary));
            OnPropertyChanged(nameof(DecodeCoverageSummary));
            OnPropertyChanged(nameof(TopMovers));
            OnPropertyChanged(nameof(Statistics));
            OnPropertyChanged(nameof(StatisticsSummary));
            RebuildDbcManagerRows();
            RebuildStatisticsRows();
            RebuildBusLoadRows();
            OnPropertyChanged(nameof(TraceHealthSummary));
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer trace/database load; the newer request's result wins.
        }
        catch (Exception exception)
        {
            StatusText = "Analysis failed: " + exception.Message;
            FileLog.Error("RebuildAnalysis", exception);
        }
        finally
        {
            IsBusy = false;
            BusyProgressPercent = null;
        }
    }

    /// <summary>Shared by every UDS-analysis entry point so a newer request's progress/cancellation
    /// always wins over a stale one, whether triggered by a trace/database reload or by loading a
    /// diagnostic catalog while a trace is already loaded.</summary>
    private void ReportAnalysisProgress(UdsAnalysisProgress report, System.Diagnostics.Stopwatch stopwatch)
    {
        var percent = report.Total <= 0 ? 0 : Math.Clamp(report.Completed * 100d / report.Total, 0, 100);
        BusyProgressPercent = percent;
        var elapsed = stopwatch.Elapsed;
        if (report.Completed <= 0)
        {
            BusyText = $"{report.Phase}… 0/{report.Total:N0}";
            return;
        }

        var estimatedTotalSeconds = elapsed.TotalSeconds * report.Total / report.Completed;
        var remainingSeconds = Math.Max(0, estimatedTotalSeconds - elapsed.TotalSeconds);
        BusyText = report.Completed >= report.Total
            ? $"{report.Phase}… {report.Completed:N0}/{report.Total:N0} · {elapsed.TotalSeconds:F1}s elapsed"
            : $"{report.Phase}… {report.Completed:N0}/{report.Total:N0} · {elapsed.TotalSeconds:F1}s elapsed · ~{remainingSeconds:F1}s remaining";
    }

    private async Task RefreshUdsAnalysisAsync()
    {
        var frames = _framesSnapshot;
        var sourceIndex = _traceSourceIndex;
        var singleSourcePath = CurrentTracePhysicalPath;
        var physicalSourceAt = BuildAnalysisSourceAt(frames, sourceIndex, singleSourcePath);
        IReadOnlyList<string> physicalSourcePaths = sourceIndex?.Paths ??
            (string.IsNullOrWhiteSpace(singleSourcePath) ? [] : [singleSourcePath]);
        var catalogs = Mapping.Catalogs;

        _analysisCancellation?.Cancel();
        _analysisCancellation?.Dispose();
        var cancellation = new System.Threading.CancellationTokenSource();
        _analysisCancellation = cancellation;
        var token = cancellation.Token;

        if (frames.Count == 0)
        {
            Uds.SetAnalysis(DiagnosticInsightSnapshot.Empty, [], OfflineAnalysisSnapshot.Empty);
            DataStudio.RefreshDiagnosticWorkbench();
            return;
        }

        BusyText = "Refreshing UDS analysis…";
        BusyProgressPercent = null;
        IsBusy = true;
        try
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var progress = new Progress<UdsAnalysisProgress>(report => ReportAnalysisProgress(report, stopwatch));
            var analysis = await Task.Run(() =>
            {
                var transactions = UdsTransactionAnalyzer.Analyze(
                    frames, catalogs, progress, token, physicalSourceAt);
                var insight = DiagnosticInsightAnalyzer.Analyze(transactions, catalogs, frames,
                    sourceAt: physicalSourceAt);
                var offline = OfflineAnalysisAnalyzer.Analyze(
                    _reports, TraceStatistics.FindTimingIncidents(frames, _statistics), insight, catalogs,
                    _cycleTimingResult);
                return (insight, BuildUdsCanCorrelations(insight, catalogs, _databases,
                    _timelines, physicalSourceAt), offline);
            }, token);
            token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(frames, _framesSnapshot) ||
                !ReferenceEquals(_analysisCancellation, cancellation))
                return;
            Uds.SetAnalysis(analysis.Item1, analysis.Item2, analysis.offline,
                sourceFrames: frames,
                physicalSourcePaths: physicalSourcePaths,
                physicalSourceAt: physicalSourceAt,
                sourceNameAt: TraceSourceNameAt,
                sourceEvidenceAt: TraceSourceEvidenceAt);
            _diagnosticSnapshot = analysis.Item1;
            DataStudio.RefreshDiagnosticWorkbench();
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer request.
        }
        finally
        {
            // An older canceled refresh must not hide the newer refresh's
            // progress indicator when both continuations return on the UI.
            if (ReferenceEquals(_analysisCancellation, cancellation))
            {
                IsBusy = false;
                BusyProgressPercent = null;
            }
        }
    }

    private static IReadOnlyList<UdsCanCorrelationResult> BuildUdsCanCorrelations(
        DiagnosticInsightSnapshot insight,
        IReadOnlyList<DidCatalog> catalogs,
        IReadOnlyList<DbcDatabase> databases,
        IReadOnlyList<SignalTimeline> timelines,
        Func<int, int>? sourceAt = null)
    {
        if (insight.DidSamples.Count == 0 || databases.Count == 0 || timelines.Count == 0) return [];
        var definitions = catalogs.SelectMany(catalog => catalog.Parameters)
            .GroupBy(did => did.Did).ToDictionary(group => group.Key, group => group.ToArray());
        var results = new List<UdsCanCorrelationResult>();
        // A DID is not one scalar observation: OEM records may contain
        // multiple independently decoded fields, even within the same ECU.
        // Each candidate fit must keep its DID, ECU, physical channel and
        // named source-field path distinct.
        foreach (var group in insight.DidSamples.Where(sample =>
                         sample.NumericValue is { } value && double.IsFinite(value))
                     .GroupBy(sample => (sample.Did, sample.Ecu, sample.Channel, sample.Field,
                         Logger: sourceAt is null || sample.FrameIndex < 0
                             ? -1 : sourceAt(sample.FrameIndex))))
        {
            if (!definitions.TryGetValue(group.Key.Did, out var possibilities)) continue;
            var exact = possibilities.Where(did =>
                string.Equals(did.Ecu, group.Key.Ecu, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            // When the ECU is unknown, a DID repeated across multiple catalogs
            // must not silently inherit the first definition in enumeration order.
            var definition = exact.Length == 1 ? exact[0] :
                exact.Length == 0 && possibilities.Length == 1 ? possibilities[0] : null;
            if (definition is null) continue;
            var scoped = DidSignalCorrelator.ScopeToRecordedField(
                definition, group.Key.Field);
            if (scoped is null)
            {
                // A decoded field path does not map uniquely to this OEM
                // definition. Never recycle another field's unit/scale/name
                // to rank CAN candidates for an unrelated response value.
                continue;
            }
            var candidates = DidSignalCorrelator.Correlate(
                scoped, databases, options: new CorrelationOptions(8, 0.10, false));
            results.AddRange(UdsCanTimeCorrelation.Correlate(
                group.ToArray(), candidates, timelines, sourceAt: sourceAt));
        }

        return results.OrderByDescending(result => result.Confidence).ToArray();
    }

    /// <summary>
    /// Single background pass over the per-ID statistics computing, per database and overall,
    /// which trace identifiers are matched (channel- and PGN-aware) plus the unmatched rows
    /// with an actionable hint about why they did not match.
    /// </summary>
    private static CoverageSnapshot ComputeCoverage(
        IReadOnlyList<MessageStatisticsRow> statistics,
        IReadOnlyList<DbcDatabase> databases,
        IReadOnlyList<DbcDecoder> decoders)
    {
        var byDatabase = new Dictionary<string, (int MatchedIds, long MatchedFrames)>(StringComparer.OrdinalIgnoreCase);
        foreach (var database in databases)
        {
            byDatabase[database.Name] = (0, 0);
        }

        var decodedFrames = 0L;
        var matchedIdentifiers = new HashSet<uint>();
        var seenMessageIdentifiers = new HashSet<uint>();
        var unmatched = new List<(MessageStatisticsRow Row, string Hint)>();

        var pgnsByDatabase = databases.ToDictionary(
            database => database.Name,
            database => database.AllMessageDefinitions
                .Where(message => message.IsExtended)
                .Select(message => J1939.ExtractPgn(message.Identifier))
                .ToHashSet());

        foreach (var row in statistics)
        {
            var matchedAny = false;
            foreach (var decoder in decoders)
            {
                if (decoder.TryGetMessage(row.Channel, row.Identifier, row.IsExtended, out var message))
                {
                    matchedAny = true;
                    seenMessageIdentifiers.Add(message.Identifier);
                    var (ids, frameCount) = byDatabase[decoder.Database.Name];
                    byDatabase[decoder.Database.Name] = (ids + 1, frameCount + row.FrameCount);
                }
            }

            if (matchedAny)
            {
                decodedFrames += row.FrameCount;
                matchedIdentifiers.Add(row.Identifier);
            }
            else
            {
                unmatched.Add((row, BuildUnmatchedHint(row, databases, pgnsByDatabase)));
            }
        }

        var unmatchedRows = unmatched
            .OrderByDescending(item => item.Row.FrameCount)
            .ThenBy(item => item.Row.Channel)
            .ThenBy(item => item.Row.Identifier)
            .Take(200)
            .Select(item => new UnmatchedIdentifierRow(
                item.Row.Channel + 1,
                item.Row.Identifier.ToString(item.Row.IsExtended ? "X8" : "X3", System.Globalization.CultureInfo.InvariantCulture) +
                (item.Row.IsExtended ? "x" : string.Empty),
                item.Row.FrameCount,
                (item.Row.FirstTimestampNanoseconds / 1_000_000_000d).ToString("F3", System.Globalization.CultureInfo.InvariantCulture),
                (item.Row.LastTimestampNanoseconds / 1_000_000_000d).ToString("F3", System.Globalization.CultureInfo.InvariantCulture),
                item.Hint))
            .ToList();

        return new CoverageSnapshot(
            byDatabase, decodedFrames, matchedIdentifiers.Count, seenMessageIdentifiers, unmatchedRows);
    }

    /// <summary>
    /// Applies unambiguous fixes for unmatched trace identifiers automatically: widening a
    /// DBC's channel restriction, or enabling J1939 PGN matching, whenever exactly one loaded
    /// database explains the identifier. Each (database, fix) pair is only ever auto-applied
    /// once per session, so a user who deliberately reverts the option afterwards is not
    /// fought by the next re-analysis.
    /// </summary>
    private int AutoResolveUnmatchedIdentifiers(
        IReadOnlyList<MessageStatisticsRow> statistics,
        IReadOnlyList<DbcDatabase> databases,
        IReadOnlyList<DbcDecoder> decoders)
    {
        if (databases.Count == 0 || statistics.Count == 0)
        {
            return 0;
        }

        var pgnsByDatabase = databases.ToDictionary(
            database => database.Name,
            database => database.AllMessageDefinitions
                .Where(message => message.IsExtended)
                .Select(message => J1939.ExtractPgn(message.Identifier))
                .ToHashSet());

        var applied = 0;
        foreach (var row in statistics)
        {
            var matchedAny = decoders.Any(decoder => decoder.TryGetMessage(row.Channel, row.Identifier, row.IsExtended, out _));
            if (matchedAny)
            {
                continue;
            }

            var channelCandidates = databases.Where(database => database.TryGetMessage(row.Identifier, row.IsExtended, out _)).ToArray();
            if (channelCandidates.Length == 1)
            {
                applied += ApplyAutoFix(channelCandidates[0].Name, "channel", state => state with { ChannelIndex = 0 });
                continue;
            }

            if (!row.IsExtended)
            {
                continue;
            }

            var pgn = J1939.ExtractPgn(row.Identifier);
            var pgnCandidates = databases
                .Where(database => pgnsByDatabase.TryGetValue(database.Name, out var pgns) && pgns.Contains(pgn))
                .ToArray();
            if (pgnCandidates.Length == 1)
            {
                applied += ApplyAutoFix(pgnCandidates[0].Name, "pgn", state => state with { MatchByPgn = true });
            }
        }

        return applied;
    }

    /// <summary>Applies one auto-fix to a database's stored options, once per session at most.</summary>
    private int ApplyAutoFix(string databaseName, string fixKind, Func<DbcOptionsState, DbcOptionsState> apply)
    {
        if (!_autoResolvedDbcFixKeys.Add($"{databaseName}|{fixKind}"))
        {
            return 0;
        }

        var current = _dbcOptions.GetValueOrDefault(databaseName, new DbcOptionsState(false, 0));
        var updated = apply(current);
        if (updated == current)
        {
            return 0;
        }

        _dbcOptions[databaseName] = updated;
        return 1;
    }

    private static string BuildUnmatchedHint(
        MessageStatisticsRow row,
        IReadOnlyList<DbcDatabase> databases,
        IReadOnlyDictionary<string, HashSet<uint>> pgnsByDatabase)
    {
        var channelCandidates = databases.Where(database => database.TryGetMessage(row.Identifier, row.IsExtended, out _)).ToArray();
        if (channelCandidates.Length == 1)
        {
            return $"Defined in {channelCandidates[0].Name} but excluded by its channel assignment " +
                   "— switching it to \"All channels\" resolves this automatically the first time it is seen";
        }

        if (channelCandidates.Length > 1)
        {
            return "Defined (but channel-excluded) in " + string.Join(", ", channelCandidates.Select(database => database.Name)) +
                   " — ambiguous, pick the right one manually in DBC Manager options";
        }

        if (row.IsExtended)
        {
            var pgn = J1939.ExtractPgn(row.Identifier);
            var pgnCandidates = databases
                .Where(database => pgnsByDatabase.TryGetValue(database.Name, out var pgns) && pgns.Contains(pgn))
                .ToArray();
            if (pgnCandidates.Length == 1)
            {
                return $"Same J1939 PGN as a message in {pgnCandidates[0].Name} " +
                       "— enabling \"Match by PGN\" resolves this automatically the first time it is seen";
            }

            if (pgnCandidates.Length > 1)
            {
                return "Same J1939 PGN as messages in " + string.Join(", ", pgnCandidates.Select(database => database.Name)) +
                       " — ambiguous, enable \"Match by PGN\" on the correct one manually";
            }
        }

        if (databases.Count > 0 && UdsFrameInterpreter.LooksLikeDiagnosticId(row.Identifier, row.IsExtended))
            return "Diagnostic (ISO-TP / UDS) traffic - normally has no signal definition in a DBC";
        return databases.Count == 0 ? "No DBC loaded" : "No candidate in the loaded DBCs";
    }

    private void RebuildDbcManagerRows()
    {
        var selectedDatabaseName = _selectedDbcDatabase?.Name;
        DatabaseSummaries.Clear();
        DbcDatabases.Clear();
        UnmatchedIdentifiers.Clear();

        var channelChoices = BuildChannelChoices();
        DbcDatabaseRowViewModel? toSelect = null;
        foreach (var database in _databases.OrderBy(database => database.Name, StringComparer.OrdinalIgnoreCase))
        {
            var signalCount = database.AllSignals.Count();
            var (matchedIds, matchedFrames) = _coverageByDb.GetValueOrDefault(database.Name, (0, 0L));
            var options = _dbcOptions.GetValueOrDefault(database.Name, new DbcOptionsState(false, 0));
            var path = _databasePaths.GetValueOrDefault(database.Name, database.Name + ".dbc");
            var name = database.Name;
            var row = new DbcDatabaseRowViewModel(
                name,
                path,
                database.MessageDefinitionCount,
                signalCount,
                matchedIds,
                matchedFrames,
                database.ParseWarnings,
                channelChoices,
                options.MatchByPgn,
                options.ChannelIndex,
                optionsChanged: null);
            DbcDatabases.Add(row);
            DatabaseSummaries.Add(
                $"{database.Name} — {database.MessageDefinitionCount:N0} messages, {signalCount:N0} signals");
            if (string.Equals(name, selectedDatabaseName, StringComparison.OrdinalIgnoreCase))
            {
                toSelect = row;
            }
        }

        // Option callbacks are wired after construction so restoring saved values cannot
        // re-trigger a full analysis rebuild.
        foreach (var row in DbcDatabases)
        {
            WireDbcRowOptions(row);
        }

        foreach (var row in _unmatchedRows)
        {
            UnmatchedIdentifiers.Add(row);
        }

        _selectedDbcDatabase = toSelect;
        OnPropertyChanged(nameof(SelectedDbcDatabase));
        RebuildDbcMessageRows();
        RebuildDbcSignalRows();
        OnPropertyChanged(nameof(DbcDetailScopeText));
    }

    private void WireDbcRowOptions(DbcDatabaseRowViewModel row)
    {
        row.PropertyChanged += async (_, args) =>
        {
            if (args.PropertyName is not (nameof(DbcDatabaseRowViewModel.MatchByPgn) or nameof(DbcDatabaseRowViewModel.ChannelIndex)))
            {
                return;
            }

            _dbcOptions[row.Name] = new DbcOptionsState(row.MatchByPgn, row.ChannelIndex);
            await RebuildAnalysisAsync();
        };
    }

    private void RebuildDbcMessageRows()
    {
        var selectedMessageKey = _selectedDbcMessage is { } selected ? (selected.Database, selected.Identifier) : default;
        DbcMessages.Clear();

        // Match coverage using the active source DBC decoder profile. A raw ID
        // count is misleading when the same numeric ID appears on several CAN
        // buses, in 11/29-bit formats, or via J1939 PGN source addresses.
        var frameCounts = new Dictionary<(string Database, uint Id, bool Extended), long>();
        foreach (var observed in _statistics)
        {
            DbcDecoder? onlyDecoder = null;
            DbcMessage? onlyMessage = null;
            var claimants = 0;
            foreach (var decoder in _decoders)
            {
                if (!decoder.TryGetMessage(observed.Channel, observed.Identifier,
                        observed.IsExtended, out var matched))
                    continue;
                claimants++;
                onlyDecoder = decoder;
                onlyMessage = matched;
            }
            // Do not call these "covered" in the DBC manager if another
            // active profile claims the same channel/11-/29-bit CAN identity.
            if (claimants != 1 || onlyDecoder is null || onlyMessage is null)
                continue;
            var key = (onlyDecoder.Database.Name, onlyMessage.Identifier, onlyMessage.IsExtended);
            frameCounts[key] = frameCounts.GetValueOrDefault(key) + observed.FrameCount;
        }

        DbcMessageCatalogRow? toSelect = null;
        foreach (var database in ScopedDatabases())
        {
            foreach (var message in database.AllMessageDefinitions.OrderBy(message => message.Identifier))
            {
                var identifier = message.Identifier.ToString(message.IsExtended ? "X8" : "X3", System.Globalization.CultureInfo.InvariantCulture) +
                                 (message.IsExtended ? "x" : string.Empty);
                var coverage = frameCounts.TryGetValue(
                    (database.Name, message.Identifier, message.IsExtended), out var count)
                    ? $"{count:N0} frames"
                    : "No trace match";
                if (_dbcObservedOnly && !frameCounts.ContainsKey(
                        (database.Name, message.Identifier, message.IsExtended)))
                    continue;
                if (!MatchesDbcSearch(
                        database.Name,
                        _databasePaths.GetValueOrDefault(database.Name, database.Name + ".dbc"),
                        identifier,
                        message.Name,
                        message.Transmitter,
                        coverage,
                        message.Comment))
                {
                    continue;
                }

                var row = new DbcMessageCatalogRow(
                    database.Name,
                    _databasePaths.GetValueOrDefault(database.Name, database.Name + ".dbc"),
                    identifier,
                    message.Name,
                    message.Transmitter,
                    message.Length,
                    message.Signals.Count,
                    coverage,
                    message.Comment,
                    message.NominalCycleTimeMilliseconds is { } ms ? ms.ToString(System.Globalization.CultureInfo.InvariantCulture) : "—");
                DbcMessages.Add(row);
                if ((row.Database, row.Identifier) == selectedMessageKey)
                {
                    toSelect = row;
                }
            }
        }

        _selectedDbcMessage = toSelect;
        OnPropertyChanged(nameof(SelectedDbcMessage));
    }

    private void RebuildDbcSignalRows()
    {
        var previouslySelected = _selectedDbcSignal is { } item
            ? (item.Database, item.Identifier, item.Message, item.Signal) : default;
        DbcSignals.Clear();
        // Exact database / 11-29-bit CAN identity / signal. An observed sample
        // never makes a DBC definition on a different physical bus proven.
        var observed = _timelines
            .GroupBy(t => (t.DatabaseName, t.Message.Identifier, t.Message.IsExtended, t.Signal.Name))
            .ToDictionary(group => group.Key, group => (
                Samples: group.Sum(t => (long)t.Samples.Count),
                Channels: group.Select(t => t.Channel).Distinct().Count()));
        foreach (var database in ScopedDatabases())
        {
            if (_selectedDbcMessage is { } selectedMessage &&
                !string.Equals(database.Name, selectedMessage.Database, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var message in database.AllMessageDefinitions.OrderBy(message => message.Identifier))
            {
                var identifier = message.Identifier.ToString(message.IsExtended ? "X8" : "X3", System.Globalization.CultureInfo.InvariantCulture) +
                                 (message.IsExtended ? "x" : string.Empty);
                if (_selectedDbcMessage is { } scoped && identifier != scoped.Identifier)
                {
                    continue;
                }

                foreach (var signal in message.Signals)
                {
                    observed.TryGetValue(
                        (database.Name, message.Identifier, message.IsExtended, signal.Name),
                        out var evidence);
                    if (_dbcObservedOnly && evidence.Samples == 0)
                        continue;
                    var range = $"{signal.Minimum:G6} .. {signal.Maximum:G6}";
                    var scaling = signal.PhysicalConversionUnsupported
                        ? "RAW ONLY (conversion unsupported)"
                        : signal.UsesRangeDependentScale
                            ? $"Segmented source scaling ({signal.ScaleSegments!.Count} intervals)"
                            : $"x{signal.Factor:G6} + {signal.Offset:G6}";
                    if (!MatchesDbcSearch(
                            database.Name,
                            identifier,
                            message.Name,
                            signal.Name,
                            signal.Unit,
                            range,
                            scaling,
                            signal.Comment,
                            signal.Receiver,
                            signal.ByteOrder.ToString(),
                            signal.IsSigned ? "signed" : "unsigned",
                            signal.IsMultiplexor ? "multiplexor" :
                                signal.MultiplexValue is { } value ? $"mux {value}" : "always",
                            signal.ValueDescriptions is { } values
                                ? string.Join(" ", values.Select(pair => $"{pair.Key} {pair.Value}"))
                                : string.Empty))
                    {
                        continue;
                    }

                    DbcSignals.Add(new DbcSignalCatalogRow(
                        database.Name,
                        message.Transmitter,
                        message.Name,
                        identifier,
                        signal.Name,
                        signal.Unit,
                        signal.StartBit.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        signal.BitLength,
                        range,
                        scaling,
                        signal.Comment,
                        signal.ByteOrder == DbcByteOrder.Intel ? "Intel (@1)" : "Motorola (@0)",
                        signal.ValueType != DbcSignalValueType.Integer ? signal.ValueType.ToString() :
                            signal.IsSigned ? "Signed" : "Unsigned",
                        signal.IsMultiplexor ? "MUX" :
                            signal.MultiplexValue is { } selector ? $"m{selector}" : "Always",
                        signal.ValueDescriptions?.Count ?? 0,
                        evidence.Samples, evidence.Channels));
                }
            }
        }

        _selectedDbcSignal = DbcSignals.FirstOrDefault(row =>
            (row.Database, row.Identifier, row.Message, row.Signal) == previouslySelected)
            ?? DbcSignals.FirstOrDefault();
        OnPropertyChanged(nameof(SelectedDbcSignal));
        RefreshDbcInspector();
    }

    private IEnumerable<DbcDatabase> ScopedDatabases()
    {
        var databases = _databases.OrderBy(database => database.Name, StringComparer.OrdinalIgnoreCase);
        return _selectedDbcDatabase is { } selected
            ? databases.Where(database => string.Equals(database.Name, selected.Name, StringComparison.OrdinalIgnoreCase))
            : databases;
    }

    private void RebuildStatisticsRows()
    {
        _allStatisticsRows = _statistics
            .Select(row =>
            {
                var (messageName, ecu) = ResolveStatisticsMessage(row);
                return new StatisticsRowViewModel(row, messageName, ecu);
            })
            .ToList();
        RebuildTraceHealthRows();
        RefreshStatisticsRows();
    }

    /// <summary>One row per channel present in the trace, each with its own editable assumed
    /// bitrate (default 500 kbit/s) driving an estimated bus-load percent. The bitrate is never
    /// auto-detected from the file: BLF/MF4 do not reliably carry it, and guessing wrong would
    /// produce a bus-load number that looks precise but is not.</summary>
    private void RebuildBusLoadRows()
    {
        BusLoadRows.Clear();
        if (_frames.Count == 0)
        {
            return;
        }

        var span = Math.Max(1, _frames[^1].TimestampNanoseconds - _frames[0].TimestampNanoseconds);
        // One streaming pass accumulating only counts and bit totals per channel.
        var perChannel = new SortedDictionary<int, (long Frames, long Bits)>();
        foreach (var frame in _frames)
        {
            perChannel.TryGetValue(frame.Channel, out var totals);
            perChannel[frame.Channel] = (totals.Frames + 1, totals.Bits + BusLoadCalculator.EstimateFrameBits(frame));
        }

        foreach (var (channel, totals) in perChannel)
        {
            var row = new ChannelBusLoadRowViewModel(channel, totals.Frames, totals.Bits, span,
                LoggedBusMetadata.LoggedMeanLoad(_traceImportHealth?.LoggedBusStatistics, channel));
            if (_channelBitrateKbps.TryGetValue(channel, out var savedBitrate))
            {
                row.BitrateKbps = savedBitrate;
            }

            row.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ChannelBusLoadRowViewModel.BitrateKbps))
                {
                    _channelBitrateKbps[row.Channel] = row.BitrateKbps;
                }
            };
            BusLoadRows.Add(row);
        }
    }

    private void RebuildTraceHealthRows()
    {
        TraceHealthRows.Clear();
        var incidents = TraceStatistics.FindTimingIncidents(_frames, _statistics);
        foreach (var incident in incidents)
        {
            var (messageName, ecu) = ResolveStatisticsMessage(incident.Channel, incident.Identifier, incident.IsExtended);
            var estimatedMissing = Math.Max(0, (int)Math.Round(incident.RatioVsAverage) - 1);
            var severity = incident.RatioVsAverage switch
            {
                >= 10 => "Crit",
                >= 5 => "High",
                _ => "Warn"
            };
            TraceHealthRows.Add(new TraceHealthRowViewModel(incident, ecu, messageName, severity, estimatedMissing));
        }

        OnPropertyChanged(nameof(TraceHealthSummary));
    }

    private (string MessageName, string Ecu) ResolveStatisticsMessage(MessageStatisticsRow row) =>
        ResolveStatisticsMessage(row.Channel, row.Identifier, row.IsExtended);

    private (string MessageName, string Ecu) ResolveStatisticsMessage(int channel, uint identifier, bool isExtended)
    {
        foreach (var decoder in _decoders)
        {
            if (decoder.TryGetMessage(channel, identifier, isExtended, out var message))
            {
                return (message.Name, message.Transmitter);
            }
        }

        return (string.Empty, string.Empty);
    }

    private void RefreshStatisticsRows()
    {
        StatisticsRows.Clear();
        foreach (var row in _allStatisticsRows.Where(row => row.Matches(_statisticsSearchText)))
        {
            StatisticsRows.Add(row);
        }
    }

    /// <summary>Restore current full-identity presets, and old name-only presets only
    /// when the name resolves to exactly one DBC signal on one CAN interface.</summary>
    private PlotSeriesViewModel? FindSeriesBySavedKey(string key)
    {
        var exact = Signals.AllSeries.FirstOrDefault(series => series.Timeline.Key == key);
        if (exact is not null) return exact;
        var possibleLegacy = Signals.AllSeries.Where(series => series.Timeline.LegacyKey == key)
            .Take(2).ToArray();
        return possibleLegacy.Length == 1 ? possibleLegacy[0] : null;
    }

    private void RefreshWatchSignals(bool removeMissing = false)
    {
        if (removeMissing)
        {
            var validKeys = Signals.AllSeries.Select(series => series.Timeline.Key).ToHashSet();
            foreach (var stale in WatchSignals.Where(row => !validKeys.Contains(row.Series.Timeline.Key)).ToArray())
            {
                WatchSignals.Remove(stale);
            }
        }

        foreach (var row in WatchSignals)
        {
            row.Update(PlotCursorNanoseconds);
        }
    }

    private void BuildCompareRows(
        IReadOnlyList<MessageStatisticsRow> baseStats,
        IReadOnlyList<MessageStatisticsRow> compareStats)
    {
        CompareTraceRows.Clear();
        var byKey = baseStats.ToDictionary(row => (row.Channel, row.Identifier, row.IsExtended));
        var compareByKey = compareStats.ToDictionary(row => (row.Channel, row.Identifier, row.IsExtended));
        foreach (var key in byKey.Keys.Union(compareByKey.Keys).OrderBy(key => key.Channel).ThenBy(key => key.Identifier))
        {
            byKey.TryGetValue(key, out var left);
            compareByKey.TryGetValue(key, out var right);
            var identifier = key.Identifier.ToString(key.IsExtended ? "X8" : "X3", System.Globalization.CultureInfo.InvariantCulture) +
                             (key.IsExtended ? "x" : string.Empty);
            var leftCount = left?.FrameCount ?? 0;
            var rightCount = right?.FrameCount ?? 0;
            var status = (leftCount, rightCount) switch
            {
                (0, > 0) => "added",
                (> 0, 0) => "missing",
                _ when Math.Abs(rightCount - leftCount) > Math.Max(5, leftCount * 0.1) => "changed",
                _ => "similar"
            };
            CompareTraceRows.Add(new CompareTraceRow(
                key.Channel + 1,
                identifier,
                leftCount,
                rightCount,
                rightCount - leftCount,
                left is null ? "-" : FormatRate(left),
                right is null ? "-" : FormatRate(right),
                status));
        }
    }

    private void BuildSignalCompareRows(
        IReadOnlyList<SignalChangeReport> baseReports,
        IReadOnlyList<SignalChangeReport> compareReports)
    {
        CompareSignalRows.Clear();
        var leftByKey = baseReports
            .GroupBy(SignalCompareKey)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(report => report.SampleCount).First());
        var rightByKey = compareReports
            .GroupBy(SignalCompareKey)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(report => report.SampleCount).First());
        foreach (var key in leftByKey.Keys.Union(rightByKey.Keys).OrderBy(key => key.Message, StringComparer.OrdinalIgnoreCase).ThenBy(key => key.Signal, StringComparer.OrdinalIgnoreCase))
        {
            leftByKey.TryGetValue(key, out var left);
            rightByKey.TryGetValue(key, out var right);
            var leftLast = left is null || left.SampleCount == 0 ? (double?)null : left.LastValue;
            var rightLast = right is null || right.SampleCount == 0 ? (double?)null : right.LastValue;
            var status = (left?.SampleCount ?? 0, right?.SampleCount ?? 0) switch
            {
                (0, > 0) => "added",
                (> 0, 0) => "missing",
                _ when leftLast is not null && rightLast is not null && Math.Abs(leftLast.Value - rightLast.Value) > 1e-9 => "value changed",
                _ when Math.Abs((left?.ChangeCount ?? 0) - (right?.ChangeCount ?? 0)) > Math.Max(3, (left?.ChangeCount ?? 0) * 0.1) => "activity changed",
                _ => "similar"
            };
            var report = left ?? right!;
            CompareSignalRows.Add(new CompareSignalRow(
                $"{report.Timeline.Message.Transmitter} · CAN {(report.Timeline.Channel >= 0 ? report.Timeline.Channel + 1 : 0)}",
                report.MessageName,
                report.SignalName,
                report.Unit,
                left?.SampleCount ?? 0,
                right?.SampleCount ?? 0,
                FormatSignalValue(leftLast, report.Unit),
                FormatSignalValue(rightLast, report.Unit),
                leftLast is not null && rightLast is not null ? FormatSignalValue(rightLast.Value - leftLast.Value, report.Unit) : "-",
                FormatRange(left),
                FormatRange(right),
                status,
                left,
                right));
        }
    }

    private static (string Database, int Channel, uint Identifier, bool Extended, string Message, string Signal)
        SignalCompareKey(SignalChangeReport report) =>
        (report.Timeline.DatabaseName, report.Timeline.Channel, report.Identifier,
            report.Timeline.Message.IsExtended, report.MessageName, report.SignalName);

    private static string FormatRange(SignalChangeReport? report) =>
        report is null || report.SampleCount == 0
            ? "-"
            : $"{report.MinValue:G6}..{report.MaxValue:G6}";

    private static string FormatSignalValue(double? value, string unit) =>
        value is null
            ? "-"
            : value.Value.ToString("G6", System.Globalization.CultureInfo.InvariantCulture) +
              (string.IsNullOrWhiteSpace(unit) ? string.Empty : " " + unit);

    private static string FormatRate(MessageStatisticsRow row) =>
        row.FrameCount < 2 || row.AveragePeriodMilliseconds <= 0
            ? "-"
            : (1000d / row.AveragePeriodMilliseconds).ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + " Hz";

    private void WriteAnalysisReport(string path, byte[]? screenshot,
        SignalTriggerReport? evaluatedTrigger, SignalTriggerRule? triggerRule,
        SignalTriggerSequenceReport? sequenceReport, DbcValueExplorerReport? rawValueReport)
    {
        // The in-memory trace was decoded from the original captured source.
        // An export-time hash alone could belong to a replaced file.
        var traceSourceSha256 = _traceSourceFingerprint?.VerifyUnchangedFile() == true
            ? _traceSourceFingerprint.Sha256
            : null;
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        WriteReportEntry(archive, "summary.md", BuildReportSummary());
        WriteReportEntry(archive, "report.html", BuildReportHtml(hasScreenshot: screenshot is { Length: > 0 }));
        // Source-file health evidence is captured at import time, before
        // chronological sorting and before any DBC interpretation.
        WriteReportEntry(archive, "trace-import-health.json",
            JsonSerializer.Serialize(_traceImportHealth, new JsonSerializerOptions { WriteIndented = true }));
        WriteReportEntry(archive, "trace-import-channels.csv", BuildCsv([
            new("Channel", ImportChannelRows.Select(row => row.ChannelText).ToArray()),
            new("AcceptedFrames", ImportChannelRows.Select(row => row.Frames.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray()),
            new("CanFdFrames", ImportChannelRows.Select(row => row.CanFdFrames.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray()),
            new("RemoteFrames", ImportChannelRows.Select(row => row.RemoteFrames.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray()),
            new("ErrorFrames", ImportChannelRows.Select(row => row.ErrorFrames.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray()),
            new("OutOfOrderTimestamps", ImportChannelRows.Select(row => row.OutOfOrderTimestamps.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray())
        ]));
        WriteReportEntry(archive, "trace-import-issues.csv", BuildCsv([
            new("Severity", ImportIssueRows.Select(row => row.Severity.ToString()).ToArray()),
            new("Code", ImportIssueRows.Select(row => row.Code).ToArray()),
            new("Explanation", ImportIssueRows.Select(row => row.Explanation).ToArray()),
            new("PhysicalLine", ImportIssueRows.Select(row => row.LineNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "").ToArray()),
            new("TopLevelByteOffset", ImportIssueRows.Select(row => row.ByteOffset?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "").ToArray())
        ]));
        WriteReportEntry(archive, "statistics.csv", BuildCsv([
            new("Channel", _allStatisticsRows.Select(row => row.Channel.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray()),
            new("ID", _allStatisticsRows.Select(row => row.Identifier).ToArray()),
            new("ECU", _allStatisticsRows.Select(row => row.Ecu).ToArray()),
            new("Message", _allStatisticsRows.Select(row => row.MessageName).ToArray()),
            new("Frames", _allStatisticsRows.Select(row => row.FrameCount.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray()),
            new("Rate", _allStatisticsRows.Select(row => row.RateText).ToArray()),
            new("AvgPeriodMs", _allStatisticsRows.Select(row => row.AveragePeriodMs.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)).ToArray()),
            new("StdDevPeriodMs", _allStatisticsRows.Select(row => row.Row.StdDevPeriodMilliseconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)).ToArray()),
            new("BurstCount", _allStatisticsRows.Select(row => row.BurstCount.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray())
        ]));
        WriteReportEntry(archive, "dbc-cycle-timing.csv", BuildCsv([
            new("Database", CycleTiming.Rows.Select(row => row.Database).ToArray()),
            new("CANChannel", CycleTiming.Rows.Select(row => row.Channel.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray()),
            new("CANIdentifier", CycleTiming.Rows.Select(row => row.Identifier).ToArray()),
            new("Message", CycleTiming.Rows.Select(row => row.Message).ToArray()),
            new("Transmitter", CycleTiming.Rows.Select(row => row.Transmitter).ToArray()),
            new("NominalPeriodMs", CycleTiming.Rows.Select(row => row.NominalMs.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray()),
            new("ObservedFrames", CycleTiming.Rows.Select(row => row.Frames.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray()),
            new("ObservedMeanMs", CycleTiming.Rows.Select(row => row.MeanMs).ToArray()),
            new("Deviation", CycleTiming.Rows.Select(row => row.DeviationText).ToArray()),
            new("ObservedMinMaxMs", CycleTiming.Rows.Select(row => row.MinMaxMs).ToArray()),
            new("StandardDeviationMs", CycleTiming.Rows.Select(row => row.StdDevMs).ToArray()),
            new("EarlyGapCount", CycleTiming.Rows.Select(row => row.EarlyGaps.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray()),
            new("LateGapCount", CycleTiming.Rows.Select(row => row.LateGaps.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray()),
            new("Assessment", CycleTiming.Rows.Select(row => row.Status).ToArray()),
            new("EvidenceLimitations", CycleTiming.Rows.Select(row => row.Detail).ToArray())
        ]));
        WriteReportEntry(archive, "dbc-cycle-gap-evidence.csv", BuildCsv([
            new("Database", CycleTiming.Incidents.Select(row => row.Database).ToArray()),
            new("CANChannel", CycleTiming.Incidents.Select(row => row.Channel.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray()),
            new("CANIdentifier", CycleTiming.Incidents.Select(row => row.Identifier).ToArray()),
            new("Message", CycleTiming.Incidents.Select(row => row.Message).ToArray()),
            new("TimestampSeconds", CycleTiming.Incidents.Select(row => row.TimestampSeconds.ToString("F6", System.Globalization.CultureInfo.InvariantCulture)).ToArray()),
            new("SourceFrameNumber", CycleTiming.Incidents.Select(row => row.FrameNumber.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray()),
            new("GapMs", CycleTiming.Incidents.Select(row => row.GapMs.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)).ToArray()),
            new("NominalPeriodMs", CycleTiming.Incidents.Select(row => row.NominalMs.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray()),
            new("GapRatio", CycleTiming.Incidents.Select(row => row.Ratio).ToArray()),
            new("Interpretation", CycleTiming.Incidents.Select(row => row.Note).ToArray())
        ]));
        WriteReportEntry(archive, "trace-health.csv", BuildCsv([
            new("TimeS", TraceHealthRows.Select(row => row.TimeText).ToArray()),
            new("Severity", TraceHealthRows.Select(row => row.Severity).ToArray()),
            new("Channel", TraceHealthRows.Select(row => row.Channel.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray()),
            new("ID", TraceHealthRows.Select(row => row.Identifier).ToArray()),
            new("ECU", TraceHealthRows.Select(row => row.Ecu).ToArray()),
            new("Message", TraceHealthRows.Select(row => row.MessageName).ToArray()),
            new("GapMs", TraceHealthRows.Select(row => row.GapText).ToArray()),
            new("AveragePeriodMs", TraceHealthRows.Select(row => row.AveragePeriodText).ToArray()),
            new("GapRatio", TraceHealthRows.Select(row => row.GapRatioText).ToArray()),
            new("EstimatedMissingFrames", TraceHealthRows.Select(row => row.EstimatedMissingFrames.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray()),
            new("Finding", TraceHealthRows.Select(row => row.Finding).ToArray())
        ]));
        WriteReportEntry(archive, "analysis-manifest.json", BuildReportManifest(traceSourceSha256));

        // UDS source evidence is keyed by the immutable original source-order
        // sequence, NOT a filter/replay row. Explicitly report any row limit.
        const int MaxExportedEvidenceFrames = 25000;
        var triggerSourceFrames = evaluatedTrigger?.Events
            .SelectMany(row => new[] { row.PreviousFrameIndex, row.CurrentFrameIndex })
            ?? Enumerable.Empty<int>();
        var sequenceSourceFrames = sequenceReport?.Events.SelectMany(row => new[]
        {
            row.First.PreviousFrameIndex, row.First.CurrentFrameIndex,
            row.Second.PreviousFrameIndex, row.Second.CurrentFrameIndex
        }) ?? Enumerable.Empty<int>();
        var rawValueSourceFrames = rawValueReport?.RawCodes.SelectMany(row =>
            new[] { row.FirstSourceFrameIndex, row.LastSourceFrameIndex })
            ?? Enumerable.Empty<int>();
        var evidenceIndices = Uds.AllRows
            .SelectMany(row => row.Transaction.FrameIndices)
            .Concat(triggerSourceFrames)
            .Concat(sequenceSourceFrames)
            .Concat(rawValueSourceFrames)
            .Where(index => index >= 0 && index < _frames.Count)
            .Distinct().OrderBy(index => index).ToArray();
        var evidence = evidenceIndices.Take(MaxExportedEvidenceFrames)
            .Select(index => new { Index = index, Frame = _frames[index] }).ToArray();
        WriteReportEntry(archive, "frame-evidence.csv", BuildCsv([
            new("SourceFrameIndex0", evidence.Select(row =>
                row.Index.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray()),
            new("OriginalSourceSequence", evidence.Select(row =>
                row.Frame.Sequence.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray()),
            new("CANChannel", evidence.Select(row =>
                row.Frame.Channel.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray()),
            new("CANIdentifierHex", evidence.Select(row =>
                $"0x{row.Frame.Identifier:X8}").ToArray()),
            new("ArbitrationFormat", evidence.Select(row =>
                row.Frame.IsExtended ? "Extended29" : "Standard11").ToArray()),
            new("TimestampNanoseconds", evidence.Select(row =>
                row.Frame.TimestampNanoseconds.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray()),
            new("StableEvidenceId", evidence.Select(row =>
                traceSourceSha256 is not null && row.Frame.Sequence > 0
                    ? FrameEvidenceIdentity.FromFrame(traceSourceSha256, row.Frame).Token
                    : string.Empty).ToArray())
        ]));
        WriteReportEntry(archive, "frame-evidence-coverage.json",
            JsonSerializer.Serialize(new
            {
                schema = FrameEvidenceIdentity.Version,
                sourceSha256 = traceSourceSha256,
                sourceFrameReferences = evidenceIndices.Length,
                rowsExported = evidence.Length,
                rowsOmitted = evidenceIndices.Length - evidence.Length,
                includesUdsTriggerSequenceAndRawCodeSourceFrames = true,
                stableIdsAvailable = traceSourceSha256 is not null &&
                    evidence.All(row => row.Frame.Sequence > 0),
                warning = traceSourceSha256 is null
                    ? "Source unavailable or changed since import; stable frame IDs omitted."
                    : evidenceIndices.Length > evidence.Length
                        ? "Some UDS source frames omitted by the bounded export."
                        : (string?)null
            }, new JsonSerializerOptions { WriteIndented = true }));
        // Source-declared named-state transitions, not inferred ECU switch
        // instants. Export the observation uncertainty and source frames.
        var transitions = _stateTransitionReport;
        WriteReportEntry(archive, "signal-state-transitions.csv", BuildCsv([
            new("SignalKey", transitions.Transitions.Select(row => row.SignalKey).ToArray()),
            new("Message", transitions.Transitions.Select(row => row.Message).ToArray()),
            new("Signal", transitions.Transitions.Select(row => row.Signal).ToArray()),
            new("PreviousNamedState", transitions.Transitions.Select(row => row.PreviousDeclaredState).ToArray()),
            new("CurrentNamedState", transitions.Transitions.Select(row => row.CurrentDeclaredState).ToArray()),
            new("LastPreviousObservationNs", transitions.Transitions.Select(row =>
                row.LastPreviousObservationNanoseconds.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray()),
            new("FirstCurrentObservationNs", transitions.Transitions.Select(row =>
                row.FirstCurrentObservationNanoseconds.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray()),
            new("PreviousFrameIndex0", transitions.Transitions.Select(row =>
                row.PreviousSourceFrameIndex.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray()),
            new("CurrentFrameIndex0", transitions.Transitions.Select(row =>
                row.CurrentSourceFrameIndex.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray()),
            new("PreviousStateObservedSpanNs", transitions.Transitions.Select(row =>
                row.PreviousStateObservedSpanNanoseconds.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray()),
            new("TransitionTimeUncertaintyNs", transitions.Transitions.Select(row =>
                row.TransitionTimeUncertaintyNanoseconds.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray())
        ]));
        WriteReportEntry(archive, "signal-state-transition-coverage.json",
            JsonSerializer.Serialize(new
            {
                totalTransitions = transitions.TotalTransitions,
                rowsExported = transitions.Transitions.Count,
                rowsOmitted = transitions.OmittedTransitions,
                unsupportedOrAmbiguousSignals = transitions.UnsupportedOrAmbiguousSignals,
                interpretation = "Transition is first observed at the new-state frame. " +
                    "Dwell lower bound comes from repeated previous-state samples. " +
                    "Actual ECU switching instant within the observation gap is unknown."
            }, new JsonSerializerOptions { WriteIndented = true }));
        // Declared network roles and uniquely observed physical CAN matches.
        var network = _networkMatrixReport;
        WriteReportEntry(archive, "dbc-network-matrix.csv", BuildCsv([
            new("DBC", network.Rows.Select(row => row.Database).ToArray()),
            new("CANIdentifier", network.Rows.Select(row => row.IdentifierText).ToArray()),
            new("CANChannel", network.Rows.Select(row => row.ChannelText).ToArray()),
            new("Message", network.Rows.Select(row => row.Message).ToArray()),
            new("DeclaredTX", network.Rows.Select(row => row.DeclaredTransmitter).ToArray()),
            new("DeclaredRX", network.Rows.Select(row => row.DeclaredReceivers).ToArray()),
            new("Dlc", network.Rows.Select(row =>
                row.DeclaredDlc.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray()),
            new("Signals", network.Rows.Select(row =>
                row.SignalCount.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray()),
            new("ObservedFrames", network.Rows.Select(row =>
                row.ObservedFrames.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray()),
            new("ObservedAveragePeriodMs", network.Rows.Select(row => row.PeriodText).ToArray()),
            new("PossibleJ1939Pgn", network.Rows.Select(row => row.PgnText).ToArray()),
            new("AttributionEvidence", network.Rows.Select(row => row.Evidence).ToArray())
        ]));
        WriteReportEntry(archive, "dbc-network-matrix-issues.csv", BuildCsv([
            new("CANChannel", network.Issues.Select(row =>
                (row.Channel + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray()),
            new("CANIdentifier", network.Issues.Select(row => row.IdentifierText).ToArray()),
            new("RecordedFrames", network.Issues.Select(row =>
                row.RecordedFrames.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray()),
            new("IssueCode", network.Issues.Select(row => row.Code).ToArray()),
            new("Explanation", network.Issues.Select(row => row.Explanation).ToArray())
        ]));
        WriteReportEntry(archive, "dbc-network-matrix-coverage.json",
            JsonSerializer.Serialize(new
            {
                definedMessages = network.DefinedMessages,
                uniquelyMappedFrames = network.UniquelyMappedFrames,
                unassignedOrAmbiguousFrames = network.UnattributedFrames,
                totalIdentityIssues = network.TotalIssueCount,
                detailedIssuesExported = network.Issues.Count,
                detailedIssuesOmitted = network.OmittedIssues,
                note = "Declared DBC transmitters and receivers are not independently proven " +
                    "physical ECU identities. PGN matching remains a possible interpretation."
            }, new JsonSerializerOptions { WriteIndented = true }));
        var triggerEvents = evaluatedTrigger?.Events ?? [];
        WriteReportEntry(archive, "signal-trigger-evidence.csv", BuildCsv([
            new("SignalKey", triggerEvents.Select(row => row.SignalKey).ToArray()),
            new("Mode", triggerEvents.Select(_ => triggerRule?.Mode.ToString() ?? "").ToArray()),
            new("PreviousSourceFrameIndex0", triggerEvents.Select(row =>
                row.PreviousFrameIndex.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray()),
            new("CurrentSourceFrameIndex0", triggerEvents.Select(row =>
                row.CurrentFrameIndex.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray()),
            new("LastPreviousObservationNs", triggerEvents.Select(row =>
                row.PreviousTimestampNanoseconds.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray()),
            new("FirstCurrentObservationNs", triggerEvents.Select(row =>
                row.CurrentTimestampNanoseconds.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray()),
            new("PreviousPhysical", triggerEvents.Select(row =>
                row.PreviousPhysicalValue.ToString("G17", System.Globalization.CultureInfo.InvariantCulture)).ToArray()),
            new("CurrentPhysical", triggerEvents.Select(row =>
                row.CurrentPhysicalValue.ToString("G17", System.Globalization.CultureInfo.InvariantCulture)).ToArray()),
            new("PreviousDeclaredState", triggerEvents.Select(row => row.PreviousNamedState ?? "").ToArray()),
            new("CurrentDeclaredState", triggerEvents.Select(row => row.CurrentNamedState ?? "").ToArray()),
            new("TransitionTimeUncertaintyNs", triggerEvents.Select(row =>
                row.UncertaintyNanoseconds.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray())
        ]));
        WriteReportEntry(archive, "signal-trigger-coverage.json",
            JsonSerializer.Serialize(new
            {
                evaluated = evaluatedTrigger is not null,
                signalKey = evaluatedTrigger?.SignalKey,
                mode = triggerRule?.Mode.ToString(),
                threshold = triggerRule?.Threshold,
                changeTolerance = triggerRule?.ChangeTolerance,
                namedState = triggerRule?.NamedState,
                minimumEventSeparationNs = triggerRule?.MinimumEventSeparationNanoseconds,
                matchedEvents = evaluatedTrigger?.MatchedEvents ?? 0,
                retainedEvents = triggerEvents.Count,
                suppressedBySeparation = evaluatedTrigger?.SuppressedBySeparation ?? 0,
                omittedEvents = evaluatedTrigger?.OmittedEvents ?? 0,
                warning = evaluatedTrigger?.Warning,
                interpretation = "Offline observations only. Source frame pairs bound an unknown actual ECU event instant."
            }, new JsonSerializerOptions { WriteIndented = true }));

        // Export only the explicitly evaluated, bounded pair list. Source
        // context is represented as timestamp windows; the full CAN payload
        // is never cloned into ZIP files without an analyst choosing a trace
        // export. Every pair retains both predecessor/current source frames.
        var pairs = sequenceReport?.Events ?? [];
        WriteReportEntry(archive, "signal-sequence-evidence.csv", BuildCsv([
            new("FirstSignalKey", pairs.Select(row => row.First.SignalKey).ToArray()),
            new("SecondSignalKey", pairs.Select(row => row.Second.SignalKey).ToArray()),
            new("FirstPriorFrameIndex0", pairs.Select(row =>
                row.First.PreviousFrameIndex.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray()),
            new("FirstMatchFrameIndex0", pairs.Select(row =>
                row.First.CurrentFrameIndex.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray()),
            new("SecondPriorFrameIndex0", pairs.Select(row =>
                row.Second.PreviousFrameIndex.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray()),
            new("SecondMatchFrameIndex0", pairs.Select(row =>
                row.Second.CurrentFrameIndex.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray()),
            new("FirstObservedNs", pairs.Select(row =>
                row.FirstObservationNanoseconds.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray()),
            new("SecondObservedNs", pairs.Select(row =>
                row.SecondObservationNanoseconds.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray()),
            new("ObservedDelayNs", pairs.Select(row =>
                row.ObservedDelayNanoseconds.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray()),
            new("ContextStartNs", pairs.Select(row =>
                row.ContextStartNanoseconds.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray()),
            new("ContextEndNs", pairs.Select(row =>
                row.ContextEndNanoseconds.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray())
        ]));
        WriteReportEntry(archive, "signal-sequence-coverage.json",
            JsonSerializer.Serialize(new
            {
                evaluated = sequenceReport is not null,
                firstSignalKey = sequenceReport?.Rule.First.SignalKey,
                secondSignalKey = sequenceReport?.Rule.Second.SignalKey,
                firstMode = sequenceReport?.Rule.First.Mode.ToString(),
                secondMode = sequenceReport?.Rule.Second.Mode.ToString(),
                sequenceMode = sequenceReport?.Rule.Mode.ToString(),
                minObservedDelayNs = sequenceReport?.Rule.MinObservedDelayNanoseconds,
                maxObservedDelayNs = sequenceReport?.Rule.MaxObservedDelayNanoseconds,
                preContextNs = sequenceReport?.Rule.PreContextNanoseconds,
                postContextNs = sequenceReport?.Rule.PostContextNanoseconds,
                firstInputMatched = sequenceReport?.FirstInputMatches ?? 0,
                secondInputMatched = sequenceReport?.SecondInputMatches ?? 0,
                firstInputOmitted = sequenceReport?.FirstInputOmitted ?? 0,
                secondInputOmitted = sequenceReport?.SecondInputOmitted ?? 0,
                retainedInputPairs = sequenceReport?.MatchedPairs ?? 0,
                detailedPairsExported = pairs.Count,
                detailedPairsOmitted = sequenceReport?.OmittedPairs ?? 0,
                completeEvidence = sequenceReport?.IsComplete == true,
                firstInputWarning = sequenceReport?.FirstWarning,
                secondInputWarning = sequenceReport?.SecondWarning,
                interpretation = "Two measured CAN trigger observations, not a proven " +
                    "instantaneous ECU transition. Context is an inclusive timestamp " +
                    "window; full frame payloads are not duplicated in this package."
            }, new JsonSerializerOptions { WriteIndented = true }));
        // Export only the user-requested, bounded histogram and its source
        // indices. Never suggest that an untracked distinct code was absent.
        var rawCodeRows = rawValueReport?.RawCodes ?? [];
        WriteReportEntry(archive, "dbc-observed-raw-codes.csv", BuildCsv([
            new("SignalKey", rawCodeRows.Select(_ => rawValueReport?.TimelineKey ?? "").ToArray()),
            new("ExactRaw", rawCodeRows.Select(row => row.ExactRaw).ToArray()),
            new("RawHex", rawCodeRows.Select(row => row.HexRaw).ToArray()),
            new("DeclaredMeaning", rawCodeRows.Select(row => row.DeclaredMeaning ?? "").ToArray()),
            new("ObservedCount", rawCodeRows.Select(row =>
                row.ObservedCount.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray()),
            new("FirstSourceFrameIndex0", rawCodeRows.Select(row =>
                row.FirstSourceFrameIndex.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray()),
            new("LastSourceFrameIndex0", rawCodeRows.Select(row =>
                row.LastSourceFrameIndex.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray()),
            new("FirstObservedNanoseconds", rawCodeRows.Select(row =>
                row.FirstTimestampNanoseconds.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray()),
            new("LastObservedNanoseconds", rawCodeRows.Select(row =>
                row.LastTimestampNanoseconds.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray())
        ]));
        WriteReportEntry(archive, "dbc-observed-raw-coverage.json",
            JsonSerializer.Serialize(new
            {
                evaluated = rawValueReport is not null,
                signalKey = rawValueReport?.TimelineKey,
                exactRawDomain = rawValueReport?.RawDomain,
                dbcDeclaredPhysicalRange = rawValueReport?.DeclaredPhysicalRange,
                observedSamples = rawValueReport?.ObservedSamples ?? 0,
                namedCodesDeclared = rawValueReport?.DeclaredNamedCodes ?? 0,
                observedUnknownRawCodes = rawValueReport?.ObservedUnknownRawCodes ?? 0,
                observedOutsideDeclaredPhysicalRange =
                    rawValueReport?.ObservedOutsideDeclaredPhysicalRange ?? 0,
                observedNotExactlyRepresentableInDouble =
                    rawValueReport?.ObservedWithoutExactDoubleRawRepresentation ?? 0,
                distinctRawCodesTracked = rawCodeRows.Count,
                observationsWithUntrackedDistinctRawCode =
                    rawValueReport?.UntrackedSamplesDueToDistinctLimit ?? 0,
                observationsWithoutUsableSourceFrame =
                    rawValueReport?.UnavailableOriginalFrames ?? 0,
                isFloatingPoint = rawValueReport?.IsFloatingPoint ?? false,
                warning = rawValueReport?.Warning,
                note = "Raw histogram is source-frame based; DBC VAL_ names are declarations, " +
                    "not facts inferred from vehicle behavior. Untracked distinct raw codes " +
                    "are not silently treated as absent."
            }, new JsonSerializerOptions { WriteIndented = true }));
        WriteReportEntry(archive, "top-movers.csv", BuildCsv([
            new("ECU", TopMovers.Select(row => row.Ecu).ToArray()),
            new("Message", TopMovers.Select(row => row.MessageName).ToArray()),
            new("Signal", TopMovers.Select(row => row.SignalName).ToArray()),
            new("Changes", TopMovers.Select(row => row.ChangeCount.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray()),
            new("Amplitude", TopMovers.Select(row => row.AmplitudeText).ToArray())
        ]));
        WriteReportEntry(archive, "dbc-coverage.csv", BuildCsv([
            new("DBC", DbcDatabases.Select(row => row.FileName).ToArray()),
            new("Path", DbcDatabases.Select(row => row.Path).ToArray()),
            new("Messages", DbcDatabases.Select(row => row.MessageCount.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray()),
            new("Signals", DbcDatabases.Select(row => row.SignalCount.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray()),
            new("Coverage", DbcDatabases.Select(row => row.CoverageText).ToArray())
        ]));
        WriteReportEntry(archive, "unmatched-ids.csv", BuildCsv([
            new("Channel", UnmatchedIdentifiers.Select(row => row.Channel.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray()),
            new("ID", UnmatchedIdentifiers.Select(row => row.Identifier).ToArray()),
            new("Frames", UnmatchedIdentifiers.Select(row => row.FrameCount.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray()),
            new("Hint", UnmatchedIdentifiers.Select(row => row.Hint).ToArray())
        ]));
        WriteReportEntry(archive, "watch-list.csv", BuildCsv([
            new("ECU", WatchSignals.Select(row => row.Ecu).ToArray()),
            new("Message", WatchSignals.Select(row => row.Message).ToArray()),
            new("Signal", WatchSignals.Select(row => row.Signal).ToArray()),
            new("ValueAtCursor", WatchSignals.Select(row => row.ValueText).ToArray()),
            new("SampleTime", WatchSignals.Select(row => row.TimeText).ToArray())
        ]));
        WriteReportEntry(archive, "mapping.csv", BuildCsv([
            new("DID", Mapping.Rows.Select(row => row.DidHex).ToArray()),
            new("Parameter", Mapping.Rows.Select(row => row.Name).ToArray()),
            new("ECU", Mapping.Rows.Select(row => row.Ecu).ToArray()),
            new("Candidate", Mapping.Rows.Select(row => row.CandidateText).ToArray()),
            new("AssociationIndicator", Mapping.Rows.Select(row => row.ConfidenceText).ToArray()),
            new("CandidateMatchScore", Mapping.Rows.Select(row => row.CandidateScoreText).ToArray()),
            new("ReviewWarning", Mapping.Rows.Select(row => row.ReviewWarning).ToArray()),
            new("DidDefinitionFingerprint", Mapping.Rows.Select(row =>
                MappingDefinitionFingerprint.ForDid(row.Did)).ToArray()),
            new("CanSignalFingerprint", Mapping.Rows.Select(row => row.ActiveCandidate is { } candidate
                ? MappingDefinitionFingerprint.ForSignal(candidate.Database,
                    candidate.Message, candidate.Signal)
                : string.Empty).ToArray()),
            new("Status", Mapping.Rows.Select(row => row.StatusText).ToArray()),
            new("Reason", Mapping.Rows.Select(row => row.ReasonText).ToArray())
        ]));
        WriteReportEntry(archive, "diagnostic-analysis.json", BuildDiagnosticReportJson());
        WriteReportEntry(archive, "offline-findings.csv", BuildCsv([
            new("Severity", Uds.FindingRows.Select(row => row.SeverityText).ToArray()),
            new("Category", Uds.FindingRows.Select(row => row.Category).ToArray()),
            new("TimeS", Uds.FindingRows.Select(row => row.TimeText).ToArray()),
            new("ECU", Uds.FindingRows.Select(row => row.Ecu).ToArray()),
            new("Finding", Uds.FindingRows.Select(row => row.Title).ToArray()),
            new("Evidence", Uds.FindingRows.Select(row => row.Details).ToArray()),
            new("Frames", Uds.FindingRows.Select(row => row.FramesText).ToArray())
        ]));
        WriteReportEntry(archive, "diagnostic-sequence.csv", BuildCsv([
            new("Sequence", Uds.SequenceRows.Select(row => row.Sequence.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray()),
            new("TimeS", Uds.SequenceRows.Select(row => row.TimeText).ToArray()),
            new("Phase", Uds.SequenceRows.Select(row => row.Category).ToArray()),
            new("ECU", Uds.SequenceRows.Select(row => row.Ecu).ToArray()),
            new("Service", Uds.SequenceRows.Select(row => row.Service).ToArray()),
            new("Operation", Uds.SequenceRows.Select(row => row.Operation).ToArray()),
            new("Outcome", Uds.SequenceRows.Select(row => row.Outcome).ToArray()),
            new("State", Uds.SequenceRows.Select(row => row.State).ToArray())
        ]));
        WriteReportEntry(archive, "ecu-snapshot.csv", BuildCsv([
            new("ECU", Uds.EcuSnapshotRows.Select(row => row.Ecu).ToArray()),
            new("DID", Uds.EcuSnapshotRows.Select(row => row.DidText).ToArray()),
            new("Parameter", Uds.EcuSnapshotRows.Select(row => row.Parameter).ToArray()),
            new("RecordedValue", Uds.EcuSnapshotRows.Select(row => row.PhysicalValue).ToArray()),
            new("TimeS", Uds.EcuSnapshotRows.Select(row => row.TimeText).ToArray()),
            new("Quality", Uds.EcuSnapshotRows.Select(row => row.Quality).ToArray())
        ]));
        WriteReportEntry(archive, "diagnostic-catalog-coverage.csv", BuildCsv([
            new("Entity", Uds.CatalogCoverageRows.Select(row => row.Entity).ToArray()),
            new("Defined", Uds.CatalogCoverageRows.Select(row => row.Defined.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray()),
            new("Observed", Uds.CatalogCoverageRows.Select(row => row.Observed.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray()),
            new("Resolved", Uds.CatalogCoverageRows.Select(row => row.Resolved.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray()),
            new("Unknown", Uds.CatalogCoverageRows.Select(row => row.Unknown.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray()),
            new("Coverage", Uds.CatalogCoverageRows.Select(row => row.CoverageText).ToArray()),
            new("ObservedButUnresolved", Uds.CatalogCoverageRows.Select(row => row.UnknownText).ToArray())
        ]));
        WriteReportEntry(archive, "uds-transactions.csv", BuildCsv([
            new("Sequence", Uds.AllRows.Select(row => row.Sequence).ToArray()),
            new("TimeS", Uds.AllRows.Select(row => row.TimeText).ToArray()),
            new("Channel", Uds.AllRows.Select(row => row.ChannelText).ToArray()),
            new("Direction", Uds.AllRows.Select(row => row.DirectionText).ToArray()),
            new("Endpoint", Uds.AllRows.Select(row => row.EndpointText).ToArray()),
            new("Service", Uds.AllRows.Select(row => row.ServiceText).ToArray()),
            new("Object", Uds.AllRows.Select(row => row.ObjectText).ToArray()),
            new("Request", Uds.AllRows.Select(row => row.RequestText).ToArray()),
            new("Outcome", Uds.AllRows.Select(row => row.OutcomeText).ToArray()),
            new("Response", Uds.AllRows.Select(row => row.ResponseText).ToArray()),
            new("ResponseTime", Uds.AllRows.Select(row => row.DurationText).ToArray()),
            new("Frames", Uds.AllRows.Select(row => row.FrameIndicesText).ToArray()),
            new("Quality", Uds.AllRows.Select(row => row.QualityText).ToArray())
        ]));
        var specTrace = DiagnosticSpecTraceChecker.Check(
            Uds.AllRows.Select(row => row.Transaction).ToArray(), Mapping.Catalogs);
        WriteReportEntry(archive, "spec-vs-trace.csv", BuildCsv([
            new("Kind", specTrace.Findings.Select(finding => finding.Kind.ToString()).ToArray()),
            new("Identifier", specTrace.Findings.Select(finding => finding.Identifier).ToArray()),
            new("Description", specTrace.Findings.Select(finding => finding.Description).ToArray()),
            new("Occurrences", specTrace.Findings.Select(finding => finding.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray()),
            new("SourceFrames", specTrace.Findings.Select(finding => string.Join(' ', finding.FrameIndices.Select(frame => frame + 1))).ToArray()),
            new("Caveat", specTrace.Findings.Select(finding => finding.Caveat).ToArray()),
            new("Limitations", specTrace.Findings.Select(_ => specTrace.Limitations).ToArray())
        ]));
        var diagnosticSamples = Uds.DidSampleRows.SelectMany(row => row.Samples).ToArray();
        WriteReportEntry(archive, "did-samples.csv", BuildCsv([
            new("DID", diagnosticSamples.Select(sample => $"0x{sample.Did:X4}").ToArray()),
            new("Name", diagnosticSamples.Select(sample => sample.Name).ToArray()),
            new("Field", diagnosticSamples.Select(sample => sample.Field).ToArray()),
            new("TimeS", diagnosticSamples.Select(sample => (sample.TimestampNanoseconds / 1_000_000_000d).ToString("F6", System.Globalization.CultureInfo.InvariantCulture)).ToArray()),
            new("Physical", diagnosticSamples.Select(sample => sample.TextValue).ToArray()),
            new("Unit", diagnosticSamples.Select(sample => sample.Unit).ToArray()),
            new("ECU", diagnosticSamples.Select(sample => sample.Ecu).ToArray()),
            new("Frame", diagnosticSamples.Select(sample => (sample.FrameIndex + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray())
        ]));
        WriteReportEntry(archive, "fault-memory.csv", BuildCsv([
            new("DTC", Uds.FaultRows.Select(row => row.CodeText).ToArray()),
            new("Name", Uds.FaultRows.Select(row => row.Name).ToArray()),
            new("ECU", Uds.FaultRows.Select(row => row.Ecu).ToArray()),
            new("Status", Uds.FaultRows.Select(row => row.CurrentStatusText).ToArray()),
            new("Occurrences", Uds.FaultRows.Select(row => row.Occurrences.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray()),
            new("Cleared", Uds.FaultRows.Select(row => row.WasCleared.ToString()).ToArray()),
            new("Observed", Uds.FaultRows.Select(row => row.DurationText).ToArray())
        ]));
        WriteReportEntry(archive, "routine-analysis.csv", BuildCsv([
            new("Sequence", Uds.RoutineRows.Select(row => row.Sequence.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray()),
            new("Routine", Uds.RoutineRows.Select(row => row.IdentifierText).ToArray()),
            new("Name", Uds.RoutineRows.Select(row => row.Name).ToArray()),
            new("ECU", Uds.RoutineRows.Select(row => row.Ecu).ToArray()),
            new("State", Uds.RoutineRows.Select(row => row.State.ToString()).ToArray()),
            new("DurationMs", Uds.RoutineRows.Select(row => row.DurationMilliseconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)).ToArray()),
            new("RecordedInput", Uds.RoutineRows.Select(row => row.InputSummary).ToArray()),
            new("RecordedResult", Uds.RoutineRows.Select(row => row.ResultSummary).ToArray()),
            new("ResponsePending", Uds.RoutineRows.Select(row => row.ResponsePendingCount.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray())
        ]));
        WriteReportEntry(archive, "uds-can-correlation.csv", BuildCsv([
            new("DID", Uds.CorrelationRows.Select(row => row.DidText).ToArray()),
            new("Parameter", Uds.CorrelationRows.Select(row => row.DidName).ToArray()),
            new("Signal", Uds.CorrelationRows.Select(row => row.SignalText).ToArray()),
            new("Verdict", Uds.CorrelationRows.Select(row => row.Verdict.ToString()).ToArray()),
            new("AssociationConfidence", Uds.CorrelationRows.Select(row => row.AssociationConfidence.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)).ToArray()),
            new("StatisticalFitScore", Uds.CorrelationRows.Select(row => row.StatisticalFitScore.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)).ToArray()),
            new("Pairs", Uds.CorrelationRows.Select(row => row.PairCount.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray()),
            new("Coverage", Uds.CorrelationRows.Select(row => row.Coverage.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)).ToArray()),
            new("LagMs", Uds.CorrelationRows.Select(row => row.LagMilliseconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)).ToArray()),
            new("RSquared", Uds.CorrelationRows.Select(row => row.RSquared.ToString("F5", System.Globalization.CultureInfo.InvariantCulture)).ToArray()),
            new("MAE", Uds.CorrelationRows.Select(row => row.MeanAbsoluteError.ToString("G9", System.Globalization.CultureInfo.InvariantCulture)).ToArray()),
            new("Explanation", Uds.CorrelationRows.Select(row => row.Explanation).ToArray())
        ]));
        WriteReportEntry(archive, "diagnostic-report.pdf", BuildDiagnosticReportPdf());
        WriteReportEntry(archive, "rule-bookmarks.csv", BuildCsv([
            new("Name", Trace.RuleBookmarkPresets.Select(row => row.Name).ToArray()),
            new("Rules", Trace.RuleBookmarkPresets.Select(row => row.Rules.ReplaceLineEndings("; ")).ToArray())
        ]));
        WriteReportEntry(archive, "markers.csv", BuildCsv([
            new("TimeS", Trace.MarkedFrames.Select(marker => (marker.TimestampNanoseconds / 1_000_000_000d).ToString("F6", System.Globalization.CultureInfo.InvariantCulture)).ToArray()),
            new("Category", Trace.MarkedFrames.Select(marker => marker.Category.ToString()).ToArray())
        ]));

        if (screenshot is { Length: > 0 })
        {
            WriteReportEntry(archive, "view-screenshot.png", screenshot);
        }

        if (CompareTraceRows.Count > 0)
        {
            WriteReportEntry(archive, "compare-trace.csv", BuildCsv([
                new("Channel", CompareTraceRows.Select(row => row.Channel.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray()),
                new("ID", CompareTraceRows.Select(row => row.Identifier).ToArray()),
                new("BaseFrames", CompareTraceRows.Select(row => row.BaseFrames.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray()),
                new("CompareFrames", CompareTraceRows.Select(row => row.CompareFrames.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray()),
                new("Delta", CompareTraceRows.Select(row => row.FrameDelta.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray()),
                new("Status", CompareTraceRows.Select(row => row.Status).ToArray())
            ]));
        }

        if (CompareSignalRows.Count > 0)
        {
            WriteReportEntry(archive, "compare-signals.csv", BuildCsv([
                new("ECU", CompareSignalRows.Select(row => row.Ecu).ToArray()),
                new("Message", CompareSignalRows.Select(row => row.Message).ToArray()),
                new("Signal", CompareSignalRows.Select(row => row.Signal).ToArray()),
                new("Unit", CompareSignalRows.Select(row => row.Unit).ToArray()),
                new("BaseSamples", CompareSignalRows.Select(row => row.BaseSamples.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray()),
                new("CompareSamples", CompareSignalRows.Select(row => row.CompareSamples.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray()),
                new("BaseLast", CompareSignalRows.Select(row => row.BaseLast).ToArray()),
                new("CompareLast", CompareSignalRows.Select(row => row.CompareLast).ToArray()),
                new("DeltaLast", CompareSignalRows.Select(row => row.DeltaLast).ToArray()),
                new("BaseRange", CompareSignalRows.Select(row => row.BaseRange).ToArray()),
                new("CompareRange", CompareSignalRows.Select(row => row.CompareRange).ToArray()),
                new("Status", CompareSignalRows.Select(row => row.Status).ToArray())
            ]));
        }
    }

    private string BuildReportSummary()
    {
        var builder = new StringBuilder();
        builder.AppendLine("# RBG CAN Insight Analysis Report");
        builder.AppendLine();
        builder.AppendLine($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"Trace: {TraceName}");
        builder.AppendLine($"Trace summary: {TraceSummary}");
        builder.AppendLine($"Import integrity: {ImportHealthSummary}");
        builder.AppendLine($"DBC summary: {DbcSummary}");
        builder.AppendLine($"Coverage: {DecodeCoverageSummary}");
        builder.AppendLine($"Statistics: {StatisticsSummary}");
        builder.AppendLine($"Trace health: {TraceHealthSummary}");
        builder.AppendLine($"DBC nominal cycles: {CycleTiming.Summary}");
        builder.AppendLine("DBC cycle thresholds use a ±20% screening heuristic; interval deviations do not prove lost CAN frames.");
        builder.AppendLine($"Mapping: {Mapping.CatalogSummary}");
        builder.AppendLine($"Watch signals: {WatchSignals.Count:N0}");
        builder.AppendLine($"Saved rule sets: {Trace.RuleBookmarkPresets.Count:N0}");
        builder.AppendLine($"Trace markers: {Trace.MarkedFrames.Count:N0}");
        if (CompareTraceRows.Count > 0)
        {
            builder.AppendLine($"Compare: {CompareSummary}");
        }

        builder.AppendLine();
        builder.AppendLine("Included files: report.html, analysis-manifest.json, trace-import-health.json, trace-import-channels.csv, trace-import-issues.csv, statistics.csv, trace-health.csv, dbc-cycle-timing.csv, dbc-cycle-gap-evidence.csv, top-movers.csv, dbc-coverage.csv, unmatched-ids.csv, watch-list.csv, mapping.csv, diagnostic-analysis.json, offline-findings.csv, diagnostic-sequence.csv, ecu-snapshot.csv, diagnostic-catalog-coverage.csv, uds-transactions.csv, spec-vs-trace.csv, did-samples.csv, fault-memory.csv, routine-analysis.csv, uds-can-correlation.csv, diagnostic-report.pdf, rule-bookmarks.csv, markers.csv" +
                           (CompareTraceRows.Count > 0 ? ", compare-trace.csv" : string.Empty) +
                           (CompareSignalRows.Count > 0 ? ", compare-signals.csv" : string.Empty));
        return builder.ToString();
    }

    private string BuildDiagnosticReportJson()
    {
        var model = new
        {
            schema = "rbg-can-insight-diagnostic-analysis/2.0",
            generatedAt = DateTimeOffset.Now,
            readOnly = true,
            summary = Uds.InsightSummary,
            services = Uds.ServiceRows,
            transactions = Uds.AllRows.Select(row => new
            {
                sequence = row.Transaction.Sequence,
                timestampNanoseconds = row.Transaction.StartTimestampNanoseconds,
                endpoint = row.EndpointText,
                serviceId = row.Transaction.Decoded.ServiceId,
                service = row.Transaction.Decoded.ServiceName,
                request = row.RequestText,
                response = row.ResponseText,
                outcome = row.OutcomeText,
                responseTimeMilliseconds = row.Transaction.ResponseTimeMilliseconds,
                frames = row.Transaction.FrameIndices.Select(index => index + 1),
                quality = row.QualityText
            }),
            didSamples = Uds.DidSampleRows.SelectMany(row => row.Samples),
            faults = Uds.FaultRows,
            routines = Uds.RoutineRows,
            findings = Uds.FindingRows.Select(row => new
            {
                severity = row.SeverityText,
                category = row.Category,
                time = row.TimeText,
                row.Ecu,
                title = row.Title,
                evidence = row.Details,
                frames = row.FramesText
            }),
            sequence = Uds.SequenceRows.Select(row => new
            {
                row.Sequence,
                time = row.TimeText,
                phase = row.Category,
                row.Ecu,
                row.Service,
                row.Operation,
                row.Outcome,
                row.State
            }),
            ecuSnapshot = Uds.EcuSnapshotRows.Select(row => new
            {
                row.Ecu,
                did = row.DidText,
                row.Parameter,
                value = row.PhysicalValue,
                time = row.TimeText,
                row.Quality
            }),
            catalogCoverage = Uds.CatalogCoverageRows.Select(row => new
            {
                row.Entity,
                row.Defined,
                row.Observed,
                row.Resolved,
                row.Unknown,
                coverage = row.CoverageText,
                unresolved = row.UnknownText
            }),
            correlations = Uds.CorrelationRows.Select(row => new
            {
                did = row.DidText,
                parameter = row.DidName,
                signal = row.SignalText,
                verdict = row.Verdict.ToString(),
                associationConfidence = row.AssociationConfidence,
                statisticalFitScore = row.StatisticalFitScore,
                row.MetadataConfidence,
                row.ValueConfidence,
                row.PairCount,
                row.Coverage,
                row.LagMilliseconds,
                row.Pearson,
                row.RSquared,
                row.Slope,
                row.Intercept,
                row.MeanAbsoluteError,
                row.RootMeanSquaredError,
                row.Explanation,
                row.Points
            }),
            transportIssues = Uds.TransportIssues
        };
        return JsonSerializer.Serialize(model, new JsonSerializerOptions { WriteIndented = true });
    }

    private byte[] BuildDiagnosticReportPdf()
    {
        var lines = new List<string>
        {
            $"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}",
            "Mode: offline read-only analysis of recorded traffic",
            $"Trace: {TraceName}",
            Uds.InsightSummary,
            string.Empty,
            "SERVICE ACTIVITY"
        };
        lines.AddRange(Uds.ServiceRows.Select(row =>
            $"0x{row.ServiceId:X2} {row.ServiceName}: requests {row.Requests}, positive {row.Positive}, negative {row.Negative}, missing {row.Missing}"));
        lines.Add(string.Empty);
        lines.Add("OFFLINE FINDINGS");
        lines.AddRange(Uds.FindingRows.Select(row =>
            $"[{row.SeverityText}] {row.Category} at {row.TimeText}s: {row.Title}; {row.Details}"));
        lines.Add(string.Empty);
        lines.Add("RECORDED ECU SNAPSHOT");
        lines.AddRange(Uds.EcuSnapshotRows.Select(row =>
            $"{row.Ecu} {row.DidText} {row.Parameter}: {row.PhysicalValue} at {row.TimeText}s"));
        lines.Add(string.Empty);
        lines.Add("CDD / ODX COVERAGE");
        lines.AddRange(Uds.CatalogCoverageRows.Select(row =>
            $"{row.Entity}: {row.Resolved}/{row.Observed} observed resolved ({row.CoverageText}); unknown {row.UnknownText}"));
        lines.Add(string.Empty);
        lines.Add("FAULT MEMORY");
        lines.AddRange(Uds.FaultRows.Select(row =>
            $"{row.CodeText} {row.Name}: {row.CurrentStatusText}; reads {row.Occurrences}; cleared {row.WasCleared}"));
        lines.Add(string.Empty);
        lines.Add("RECORDED ROUTINES");
        lines.AddRange(Uds.RoutineRows.Select(row =>
            $"{row.IdentifierText} {row.Name}: {row.State}; {row.DurationMilliseconds:F2} ms; result {row.ResultSummary}"));
        lines.Add(string.Empty);
        lines.Add("UDS TO CAN CORRELATION");
        lines.AddRange(Uds.CorrelationRows.Select(row =>
            $"{row.DidText} {row.DidName} -> {row.SignalText}: {row.Verdict}, association {row.AssociationConfidence:P0}, statistical fit {row.StatisticalFitScore:P0}; {row.MetricsText}"));
        if (Uds.TransportIssues.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add("TRANSPORT FINDINGS");
            lines.AddRange(Uds.TransportIssues);
        }
        return SimplePdfWriter.Create("RBG CAN Insight - Diagnostic Analysis Report", lines);
    }

    private string BuildReportManifest(string? traceSourceSha256)
    {
        var assembly = typeof(MainViewModel).Assembly;
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                      ?? assembly.GetName().Version?.ToString()
                      ?? "unknown";
        var databases = _databasePaths
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => new
            {
                name = pair.Key,
                path = pair.Value,
                sha256 = TryHashFile(pair.Value)
            })
            .ToArray();
        var manifest = new
        {
            product = "RBG CAN Insight",
            version,
            generatedAt = DateTimeOffset.Now,
            trace = new
            {
                name = TraceName,
                path = _tracePath,
                sha256 = traceSourceSha256,
                frameCount = FrameCount,
                summary = TraceSummary,
                health = TraceHealthSummary,
                importHealth = _traceImportHealth is null ? null : new
                {
                    _traceImportHealth.Format,
                    _traceImportHealth.Completed,
                    _traceImportHealth.Frames,
                    _traceImportHealth.RejectedRecords,
                    _traceImportHealth.UnsupportedObjectRecords,
                    _traceImportHealth.OutOfOrderTimestamps,
                    _traceImportHealth.RequiresReview
                }
            },
            databases,
            coverage = DecodeCoverageSummary,
            statisticsRows = _statistics.Count,
            dbcNominalCycle = new
            {
                tolerancePercent = 20d,
                profiles = CycleTiming.Rows.Count,
                lateIntervals = CycleTiming.Rows.Sum(row => (long)row.LateGaps),
                reportedLateGapRows = CycleTiming.Incidents.Count,
                interpretation = CycleTiming.Interpretation
            },
            signalReports = _reports.Count,
            watchSignals = WatchSignals.Count,
            compareTraceRows = CompareTraceRows.Count,
            compareSignalRows = CompareSignalRows.Count,
            filters = new
            {
                traceSearch = Trace.SearchText,
                statisticsSearch = StatisticsSearchText,
                matrixSearch = Matrix.SearchText,
                mappingSearch = Mapping.SearchText
            }
        };

        return JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
    }

    private static string? TryHashFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        try
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream));
        }
        catch
        {
            return null;
        }
    }

    private string BuildReportHtml(bool hasScreenshot)
    {
        var builder = new StringBuilder();
        builder.AppendLine("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><title>RBG CAN Insight Report</title>");
        builder.AppendLine("<style>body{font-family:Segoe UI,Arial,sans-serif;margin:24px;color:#17202a;background:#f6f8fb}h1{margin:0 0 4px}section{background:#fff;border:1px solid #d8e0ea;border-radius:8px;padding:16px;margin:14px 0}.muted{color:#5d6d7e}.bar{height:12px;background:#2ea8ff;border-radius:3px}.row{display:grid;grid-template-columns:280px 1fr 90px;gap:10px;align-items:center;margin:7px 0}table{border-collapse:collapse;width:100%;font-size:13px}th,td{border-bottom:1px solid #e3e8ef;padding:6px;text-align:left}th{color:#5d6d7e}</style></head><body>");
        builder.AppendLine("<h1>RBG CAN Insight Analysis Report</h1>");
        builder.AppendLine($"<div class=\"muted\">Generated {Html(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture))}</div>");
        builder.AppendLine("<section><h2>Session</h2><table>");
        AppendHtmlRow(builder, "Trace", TraceName);
        AppendHtmlRow(builder, "Trace summary", TraceSummary);
        AppendHtmlRow(builder, "Import integrity", ImportHealthSummary);
        AppendHtmlRow(builder, "Trace health", TraceHealthSummary);
        AppendHtmlRow(builder, "DBC nominal cycles", CycleTiming.Summary);
        AppendHtmlRow(builder, "Databases", DbcSummary);
        AppendHtmlRow(builder, "Coverage", DecodeCoverageSummary);
        AppendHtmlRow(builder, "Mapping", Mapping.CatalogSummary);
        AppendHtmlRow(builder, "Saved rule sets", Trace.RuleBookmarkPresets.Count.ToString("N0", System.Globalization.CultureInfo.InvariantCulture));
        AppendHtmlRow(builder, "Trace markers", Trace.MarkedFrames.Count.ToString("N0", System.Globalization.CultureInfo.InvariantCulture));
        builder.AppendLine("</table></section>");

        if (CycleTiming.Rows.Count > 0)
        {
            builder.AppendLine("<section><h2>DBC Nominal Cycle vs Recorded CAN Traffic</h2>");
            builder.AppendLine($"<p class=\"muted\">{Html(CycleTiming.Interpretation)}</p>");
            builder.AppendLine("<table><tr><th>DBC</th><th>Channel</th><th>ID</th><th>Message</th><th>Nominal ms</th><th>Observed mean ms</th><th>Deviation</th><th>Late</th><th>Assessment</th></tr>");
            foreach (var row in CycleTiming.Rows.Take(150))
            {
                builder.AppendLine($"<tr><td>{Html(row.Database)}</td><td>{row.Channel}</td><td>{Html(row.Identifier)}</td>" +
                    $"<td>{Html(row.Message)}</td><td>{row.NominalMs}</td><td>{Html(row.MeanMs)}</td>" +
                    $"<td>{Html(row.DeviationText)}</td><td>{row.LateGaps}</td><td>{Html(row.Status)}</td></tr>");
            }
            builder.AppendLine("</table><p class=\"muted\">Individual late gaps, their physical CAN source identifiers and original frame numbers are in dbc-cycle-gap-evidence.csv. Raw frame loss cannot be concluded from timing alone.</p></section>");
        }

        builder.AppendLine("<section><h2>Offline UDS Analysis</h2><table>");
        AppendHtmlRow(builder, "Transactions", Uds.AllRows.Count.ToString("N0", System.Globalization.CultureInfo.InvariantCulture));
        AppendHtmlRow(builder, "Physical DID samples", Uds.DidSampleRows.Sum(row => row.SampleCount).ToString("N0", System.Globalization.CultureInfo.InvariantCulture));
        AppendHtmlRow(builder, "DTCs reconstructed", Uds.FaultRows.Count.ToString("N0", System.Globalization.CultureInfo.InvariantCulture));
        AppendHtmlRow(builder, "Routine executions", Uds.RoutineRows.Count.ToString("N0", System.Globalization.CultureInfo.InvariantCulture));
        AppendHtmlRow(builder, "Offline findings", Uds.FindingRows.Count.ToString("N0", System.Globalization.CultureInfo.InvariantCulture));
        AppendHtmlRow(builder, "Recorded ECU identification values", Uds.EcuSnapshotRows.Count.ToString("N0", System.Globalization.CultureInfo.InvariantCulture));
        AppendHtmlRow(builder, "Strong UDS-CAN candidates (unreviewed)", Uds.CorrelationRows.Count(row => row.Verdict == CorrelationVerdict.StrongCandidate).ToString("N0", System.Globalization.CultureInfo.InvariantCulture));
        builder.AppendLine("</table><p class=\"muted\">Read-only analysis of recorded traffic. No diagnostic request or routine can be transmitted.</p></section>");

        if (Uds.FindingRows.Count > 0)
        {
            builder.AppendLine("<section><h2>Offline Findings</h2><table><tr><th>Severity</th><th>Category</th><th>Time</th><th>Finding</th><th>Evidence</th></tr>");
            foreach (var finding in Uds.FindingRows.Take(200))
                builder.AppendLine($"<tr><td>{finding.SeverityText}</td><td>{Html(finding.Category)}</td><td>{Html(finding.TimeText)} s</td><td>{Html(finding.Title)}</td><td>{Html(finding.Details)}</td></tr>");
            builder.AppendLine("</table></section>");
        }

        if (Uds.EcuSnapshotRows.Count > 0)
        {
            builder.AppendLine("<section><h2>Recorded ECU Snapshot</h2><table><tr><th>ECU</th><th>DID</th><th>Parameter</th><th>Recorded value</th><th>Time</th></tr>");
            foreach (var value in Uds.EcuSnapshotRows.Take(200))
                builder.AppendLine($"<tr><td>{Html(value.Ecu)}</td><td>{Html(value.DidText)}</td><td>{Html(value.Parameter)}</td><td>{Html(value.PhysicalValue)}</td><td>{Html(value.TimeText)} s</td></tr>");
            builder.AppendLine("</table></section>");
        }

        if (Uds.CatalogCoverageRows.Count > 0)
        {
            builder.AppendLine("<section><h2>CDD / ODX Coverage</h2><table><tr><th>Entity</th><th>Defined</th><th>Observed</th><th>Resolved</th><th>Coverage</th><th>Unresolved</th></tr>");
            foreach (var row in Uds.CatalogCoverageRows)
                builder.AppendLine($"<tr><td>{Html(row.Entity)}</td><td>{row.Defined}</td><td>{row.Observed}</td><td>{row.Resolved}</td><td>{Html(row.CoverageText)}</td><td>{Html(row.UnknownText)}</td></tr>");
            builder.AppendLine("</table></section>");
        }

        if (Uds.FaultRows.Count > 0)
        {
            builder.AppendLine("<section><h2>Fault Memory</h2><table><tr><th>DTC</th><th>Description</th><th>Status</th><th>Reads</th><th>Cleared</th></tr>");
            foreach (var fault in Uds.FaultRows.Take(100)) builder.AppendLine($"<tr><td>{Html(fault.CodeText)}</td><td>{Html(fault.Name)}</td><td>{Html(fault.CurrentStatusText)}</td><td>{fault.Occurrences}</td><td>{fault.WasCleared}</td></tr>");
            builder.AppendLine("</table></section>");
        }

        if (Uds.RoutineRows.Count > 0)
        {
            builder.AppendLine("<section><h2>Recorded Routines</h2><table><tr><th>Routine</th><th>Name</th><th>State</th><th>Duration</th><th>Result</th></tr>");
            foreach (var routine in Uds.RoutineRows.Take(100)) builder.AppendLine($"<tr><td>{Html(routine.IdentifierText)}</td><td>{Html(routine.Name)}</td><td>{routine.State}</td><td>{routine.DurationMilliseconds:F2} ms</td><td>{Html(routine.ResultSummary)}</td></tr>");
            builder.AppendLine("</table></section>");
        }

        if (Uds.CorrelationRows.Count > 0)
        {
            builder.AppendLine("<section><h2>UDS to CAN Correlation</h2><table><tr><th>DID</th><th>CAN signal</th><th>Verdict</th><th>Association (unreviewed)</th><th>Statistical fit</th><th>Evidence</th></tr>");
            foreach (var correlation in Uds.CorrelationRows.Take(100)) builder.AppendLine($"<tr><td>{Html(correlation.DidText)} {Html(correlation.DidName)}</td><td>{Html(correlation.SignalText)}</td><td>{correlation.Verdict}</td><td>{correlation.AssociationConfidence:P0}</td><td>{correlation.StatisticalFitScore:P0}</td><td>{Html(correlation.MetricsText)}</td></tr>");
            builder.AppendLine("</table></section>");
        }

        if (hasScreenshot)
        {
            builder.AppendLine("<section><h2>View Screenshot</h2><img src=\"view-screenshot.png\" style=\"max-width:100%;border:1px solid #d8e0ea;border-radius:6px\" alt=\"Screenshot of the analysis view at export time\"></section>");
        }

        builder.AppendLine("<section><h2>Top Changing Signals</h2>");
        var movers = TopMovers.ToArray();
        var maxChanges = Math.Max(1, movers.Select(row => row.ChangeCount).DefaultIfEmpty().Max());
        foreach (var row in movers)
        {
            var width = Math.Clamp(row.ChangeCount * 100d / maxChanges, 2, 100);
            builder.AppendLine("<div class=\"row\">" +
                               $"<div>{Html(row.MessageName)}.{Html(row.SignalName)}</div>" +
                               $"<div><div class=\"bar\" style=\"width:{width:F1}%\"></div></div>" +
                               $"<div>{row.ChangeCount:N0}</div></div>");
        }

        if (movers.Length == 0)
        {
            builder.AppendLine("<div class=\"muted\">No decoded signal changes available.</div>");
        }

        builder.AppendLine("</section>");

        if (CompareSignalRows.Count > 0)
        {
            builder.AppendLine("<section><h2>Decoded Signal Compare</h2><table><tr><th>Signal</th><th>Base last</th><th>Compare last</th><th>Delta</th><th>Status</th></tr>");
            foreach (var row in CompareSignalRows.Take(60))
            {
                builder.AppendLine("<tr>" +
                                   $"<td>{Html(row.Message)}.{Html(row.Signal)}</td>" +
                                   $"<td>{Html(row.BaseLast)}</td>" +
                                   $"<td>{Html(row.CompareLast)}</td>" +
                                   $"<td>{Html(row.DeltaLast)}</td>" +
                                   $"<td>{Html(row.Status)}</td></tr>");
            }

            builder.AppendLine("</table></section>");
        }

        builder.AppendLine("</body></html>");
        return builder.ToString();
    }

    private static void AppendHtmlRow(StringBuilder builder, string key, string value) =>
        builder.AppendLine($"<tr><th>{Html(key)}</th><td>{Html(value)}</td></tr>");

    private static string Html(string value) => System.Net.WebUtility.HtmlEncode(value);

    private static string BuildCsv(IReadOnlyList<ExportColumn> columns)
    {
        using var writer = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
        writer.WriteLine(string.Join(',', columns.Select(column => EscapeCsv(column.Header))));
        var rows = columns.Count == 0 ? 0 : columns.Max(column => column.Values.Count);
        for (var row = 0; row < rows; row++)
        {
            writer.WriteLine(string.Join(',', columns.Select(column => EscapeCsv(row < column.Values.Count ? column.Values[row] : string.Empty))));
        }

        return writer.ToString();
    }

    private static string EscapeCsv(string value) =>
        value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r')
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;

    private static void WriteReportEntry(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(content);
    }

    private static void WriteReportEntry(ZipArchive archive, string name, byte[] content)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        stream.Write(content, 0, content.Length);
    }

    private bool MatchesDbcSearch(params string[] values)
    {
        var search = DbcCatalogSearchText.Trim();
        return search.Length == 0 ||
               values.Any(value => value.Contains(search, StringComparison.OrdinalIgnoreCase));
    }

    // ----- Recent files (persisted in the portable config folder) -----

    private static string RecentFilePath => Path.Combine(AppStorage.ConfigDirectory, "recent.json");

    private void AddRecent(string path, string kind)
    {
        var existing = RecentFiles.FirstOrDefault(item =>
            string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            RecentFiles.Remove(existing);
        }

        RecentFiles.Insert(0, new RecentFile(path, kind));
        while (RecentFiles.Count > 10)
        {
            RecentFiles.RemoveAt(RecentFiles.Count - 1);
        }

        SaveRecentFiles();
    }

    private string BuildExportBaseName(string suffix)
    {
        var trace = _traceWorkspacePaths.Count > 1 && _activeTraceWorkspacePath is null
            ? $"{Path.GetFileNameWithoutExtension(_traceWorkspacePaths[0])}-multi-{_traceWorkspacePaths.Count}"
            : string.IsNullOrWhiteSpace(_tracePath)
                ? "trace"
                : Path.GetFileNameWithoutExtension(_tracePath);
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            trace = trace.Replace(invalid, '_');
        }

        return $"{trace}-{suffix}.csv";
    }

    private void SaveRecentFiles()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(RecentFilePath)!);
            File.WriteAllText(RecentFilePath, JsonSerializer.Serialize(RecentFiles.ToArray()));
        }
        catch
        {
            // Recent-files persistence is best-effort.
        }
    }

    private void LoadRecentFiles()
    {
        try
        {
            if (!File.Exists(RecentFilePath))
            {
                return;
            }

            var items = JsonSerializer.Deserialize<RecentFile[]>(File.ReadAllText(RecentFilePath)) ?? [];
            foreach (var item in items.Take(10))
            {
                RecentFiles.Add(item);
            }
        }
        catch
        {
            // Ignore corrupt recent list.
        }
    }

    /// <summary>Drag &amp; drop entry point: routes each dropped file to the right loader.</summary>
    public async Task HandleDroppedFilesAsync(IReadOnlyList<string> paths)
    {
        var dbcFiles = paths
            .Where(path =>
                path.EndsWith(".dbc", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith(".arxml", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (dbcFiles.Length > 0)
        {
            await LoadDbcAsync(dbcFiles);
        }

        var traces = paths.Where(TraceImporter.IsSupported)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (traces.Length > 1)
        {
            if (TraceMergeModeDialog.Ask(System.Windows.Application.Current?.MainWindow, traces.Length) is { } mode)
                await LoadTraceSessionAsync(traces, mode);
        }
        else if (traces.Length == 1)
        {
            await LoadTraceAsync(traces[0]);
        }
    }
}
