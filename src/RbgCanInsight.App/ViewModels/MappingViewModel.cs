using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using RbgCanInsight.Analysis;
using RbgCanInsight.App.Common;
using RbgCanInsight.Correlation;
using RbgCanInsight.Dbc;
using RbgCanInsight.DiagData;
using RbgCanInsight.Domain;
using RbgCanInsight.TraceIO;

namespace RbgCanInsight.App.ViewModels;

/// <summary>One DID row of the mapping table with its ranked signal candidates.</summary>
public sealed class MappingRowViewModel : ObservableObject
{
    public static readonly object NoCandidate = new();

    private object? _selectedCandidate;
    private MappingStatus _status = MappingStatus.Unmatched;
    private string _notes = string.Empty;
    private string _reviewWarning = string.Empty;
    private RelayCommand? _confirmCommand;
    private RelayCommand? _rejectCommand;

    public MappingRowViewModel(DidParameter did)
    {
        Did = did;
        Candidates = [];
        _selectedCandidate = NoCandidate;
    }

    public DidParameter Did { get; }
    public string DidHex => Did.DidHex;
    public string Name => Did.Name;
    public string Description => string.IsNullOrWhiteSpace(Did.Description) ? Did.LongName : Did.Description;
    public string Unit => Did.Unit;
    public string Ecu => Did.Ecu;
    public string RangeText => Did is { Minimum: { } min, Maximum: { } max }
        ? $"{min.ToString("G5", CultureInfo.InvariantCulture)} … {max.ToString("G5", CultureInfo.InvariantCulture)}"
        : string.Empty;

    private DidTraceSummary? _traceSummary;
    private string _traceEvidenceNotice = "Not verified";
    private bool _candidateEvidenceStale;

    public string TraceEvidenceNotice
    {
        get => _traceEvidenceNotice;
        set
        {
            if (SetProperty(ref _traceEvidenceNotice, value))
            {
                OnPropertyChanged(nameof(TraceEvidenceText));
                OnPropertyChanged(nameof(TraceEvidenceToolTip));
            }
        }
    }

    /// <summary>Values of this DID actually observed in the open trace (decoded offline from
    /// ISO-TP ReadDataByIdentifier responses) — the strongest review evidence there is: it shows
    /// whether the tester really read this DID, how often, and in what range.</summary>
    public DidTraceSummary? TraceSummary
    {
        get => _traceSummary;
        set
        {
            if (SetProperty(ref _traceSummary, value))
            {
                OnPropertyChanged(nameof(TraceEvidenceText));
                OnPropertyChanged(nameof(TraceEvidenceToolTip));
                OnPropertyChanged(nameof(HasTraceEvidence));
            }
        }
    }

    public bool HasTraceEvidence => _traceSummary is not null;
    public bool IsCandidateEvidenceStale => _candidateEvidenceStale;

    public string TraceEvidenceText => _traceSummary is not { } summary
        ? TraceEvidenceNotice
        : summary.SampleCount == 1
            ? $"1× · {summary.LastValue.ToString("G5", CultureInfo.InvariantCulture)} · ECU unverified"
            : $"{summary.SampleCount}× · {summary.MinValue.ToString("G5", CultureInfo.InvariantCulture)}…{summary.MaxValue.ToString("G5", CultureInfo.InvariantCulture)} · last {summary.LastValue.ToString("G5", CultureInfo.InvariantCulture)} · ECU unverified";

    public string TraceEvidenceToolTip => _traceSummary is null
        ? $"{TraceEvidenceNotice}. An unavailable preview does not prove the DID was absent. Open one physical recording independently and inspect its UDS transactions."
        : $"{TraceEvidenceText}. Best-effort DID values from offline ISO-TP responses; ECU identity is not verified by this preview. Inspect full UDS transaction provenance.";

    public IReadOnlyList<CorrelationCandidate> Candidates { get; private set; }

    /// <summary>Combo items: the ranked candidates plus the "no mapping" sentinel.</summary>
    public IReadOnlyList<object> CandidateChoices { get; private set; } = [NoCandidate];

    public object? SelectedCandidate
    {
        get => _selectedCandidate;
        set
        {
            if (SetProperty(ref _selectedCandidate, value))
            {
                // Manually picking a different candidate resets the review state.
                if (Status is MappingStatus.Confirmed or MappingStatus.Rejected)
                {
                    Status = MappingStatus.ToVerify;
                }
                if (!_candidateEvidenceStale) ReviewWarning = string.Empty;
                NotifyCandidateDetails();
            }
        }
    }

    public CorrelationCandidate? ActiveCandidate => _selectedCandidate as CorrelationCandidate;

    public string CandidateText => ActiveCandidate is { } candidate
        ? $"{candidate.Message.Name}.{candidate.Signal.Name}"
        : "—";

    public string MessageText => ActiveCandidate is { } candidate
        ? $"0x{candidate.Message.Identifier:X3} {candidate.Message.Name}"
        : string.Empty;

    /// <summary>Raw candidate ranking score, preserved in legacy .rbgmap.json.
    /// This is not an identity probability.</summary>
    public double CandidateScore => !_candidateEvidenceStale && ActiveCandidate?.Confidence is double raw && double.IsFinite(raw)
        ? Math.Clamp(raw, 0d, 1d) : 0d;
    public string CandidateScoreText => ActiveCandidate is null ? string.Empty : _candidateEvidenceStale ? "Not verified" : $"{CandidateScore:P0}";
    /// <summary>Conservative displayed association. Only explicit analyst review
    /// of this candidate allows a value above 50%.</summary>
    public double Confidence => _candidateEvidenceStale ? 0d : CorrelationEvidencePolicy.AssociationIndicator(
        CandidateScore, Status == MappingStatus.Confirmed);
    public string ConfidenceText => ActiveCandidate is null ? string.Empty : _candidateEvidenceStale ? "Not verified" : $"{Confidence:P0}";
    public string ReasonText => ActiveCandidate is null ? string.Empty : _candidateEvidenceStale
        ? "Earlier trace evidence is stale; run Auto-correlate for the current recording. The analyst's review is retained."
        : ActiveCandidate.ReasonText;

    public MappingStatus Status
    {
        get => _status;
        set
        {
            if (SetProperty(ref _status, value))
            {
                OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(Confidence));
                OnPropertyChanged(nameof(ConfidenceText));
            }
        }
    }

    public string StatusText => Status switch
    {
        MappingStatus.Suggested => "Suggested",
        MappingStatus.Confirmed => "Confirmed",
        MappingStatus.ToVerify => "To verify",
        MappingStatus.Rejected => "Rejected",
        _ => "Unmatched"
    };

    public string Notes
    {
        get => _notes;
        set => SetProperty(ref _notes, value);
    }

    /// <summary>Reason that a restored review cannot safely be reused.</summary>
    public string ReviewWarning
    {
        get => _reviewWarning;
        private set => SetProperty(ref _reviewWarning, value);
    }

    public RelayCommand ConfirmCommand => _confirmCommand ??= new RelayCommand(() =>
    {
        if (ActiveCandidate is not null)
        {
            if (!_candidateEvidenceStale) ReviewWarning = string.Empty;
            Status = MappingStatus.Confirmed;
        }
    });

    public RelayCommand RejectCommand => _rejectCommand ??= new RelayCommand(() =>
    {
        if (!_candidateEvidenceStale) ReviewWarning = string.Empty;
        Status = MappingStatus.Rejected;
    });

    /// <summary>True when the active candidate came from a user-taught alias rather than the
    /// automatic scorer (see <see cref="DidAliasStore"/>).</summary>
    public bool IsLearnedAlias => ActiveCandidate is { } candidate && candidate.Reasons.Contains(LearnedAliasReason);

    internal const string LearnedAliasReason = "Learned alias: you taught this DID → signal mapping";

    public void SetCandidates(IReadOnlyList<CorrelationCandidate> candidates)
    {
        var reviewedCandidate = Status is MappingStatus.Confirmed or MappingStatus.Rejected
            ? ActiveCandidate : null;
        var reviewedStatus = Status;
        if (_candidateEvidenceStale)
            ReviewWarning = ReviewWarning.Replace(
                "Trace context changed; correlation evidence requires a fresh analysis. Review is retained.",
                string.Empty, StringComparison.Ordinal).Trim();
        _candidateEvidenceStale = false;
        OnPropertyChanged(nameof(IsCandidateEvidenceStale));
        Candidates = candidates;
        CandidateChoices = [.. candidates.Cast<object>(), NoCandidate];
        // Reuse a prior analyst review only if the entire understood signal
        // definition matches and precisely one current candidate has that
        // identity. A duplicate DBC profile is never resolved by list order.
        var equivalent = reviewedCandidate is null
            ? Array.Empty<CorrelationCandidate>()
            : candidates.Where(candidate =>
            string.Equals(candidate.Database, reviewedCandidate.Database, StringComparison.OrdinalIgnoreCase) &&
            candidate.Message.Identifier == reviewedCandidate.Message.Identifier &&
            candidate.Message.IsExtended == reviewedCandidate.Message.IsExtended &&
            string.Equals(candidate.Message.Name, reviewedCandidate.Message.Name, StringComparison.Ordinal) &&
            string.Equals(candidate.Signal.Name, reviewedCandidate.Signal.Name, StringComparison.Ordinal) &&
            MappingDefinitionFingerprint.ForSignal(candidate.Database, candidate.Message, candidate.Signal) ==
            MappingDefinitionFingerprint.ForSignal(reviewedCandidate.Database,
                reviewedCandidate.Message, reviewedCandidate.Signal)).Take(2).ToArray();
        var retained = equivalent.Length == 1 ? equivalent[0] : null;
        _selectedCandidate = retained ?? (object)(candidates.Count > 0 ? candidates[0] : NoCandidate);
        if (reviewedCandidate is not null && retained is null)
            ReviewWarning = "Previously reviewed CAN signal definition no longer matches this session; recheck required.";
        Status = retained is not null
            ? reviewedStatus
            : reviewedCandidate is not null
                ? MappingStatus.ToVerify
                : candidates.Count > 0 ? candidates[0].SuggestedStatus : MappingStatus.Unmatched;
        OnPropertyChanged(nameof(Candidates));
        OnPropertyChanged(nameof(CandidateChoices));
        OnPropertyChanged(nameof(SelectedCandidate));
        NotifyCandidateDetails();
    }

    public void MarkTraceEvidenceStale()
    {
        if (ActiveCandidate is null) return;
        _candidateEvidenceStale = true;
        OnPropertyChanged(nameof(IsCandidateEvidenceStale));
        const string staleWarning = "Trace context changed; correlation evidence requires a fresh analysis. Review is retained.";
        if (string.IsNullOrEmpty(ReviewWarning)) ReviewWarning = staleWarning;
        else if (!ReviewWarning.Contains(staleWarning, StringComparison.Ordinal)) ReviewWarning += " " + staleWarning;
        NotifyCandidateDetails();
    }

    public void ApplySaved(DidSignalMapping saved, IReadOnlyList<DbcDatabase> databases)
    {
        Notes = saved.Notes;
        ReviewWarning = string.Empty;
        if (string.IsNullOrWhiteSpace(saved.Signal))
        {
            _selectedCandidate = NoCandidate;
            Status = saved.Status == MappingStatus.Rejected
                ? MappingStatus.Rejected : MappingStatus.Unmatched;
            OnPropertyChanged(nameof(SelectedCandidate));
            NotifyCandidateDetails();
            return;
        }

        // Enumerate ALL loaded DBC profiles. A name-only legacy mapping must
        // not be silently rebound to the first of several matching CAN signals.
        var matches = databases.Where(db =>
                string.Equals(db.Name, saved.Database, StringComparison.OrdinalIgnoreCase))
            .SelectMany(db => db.AllMessageDefinitions
                .Where(message =>
                    string.Equals(message.Name, saved.Message, StringComparison.Ordinal) &&
                    (saved.MessageIdentifier is null || message.Identifier == saved.MessageIdentifier.Value) &&
                    (saved.IsExtended is null || message.IsExtended == saved.IsExtended.Value))
                .SelectMany(message => message.Signals
                    .Where(signal => string.Equals(signal.Name, saved.Signal, StringComparison.Ordinal))
                    .Select(signal => (Database: db, Message: message, Signal: signal))))
            .Take(2).ToArray();

        CorrelationCandidate? candidate = null;
        var restoredFromFile = false;
        if (matches.Length == 1)
        {
            var match = matches[0];
            candidate = Candidates.FirstOrDefault(item =>
                string.Equals(item.Database, match.Database.Name, StringComparison.OrdinalIgnoreCase) &&
                ReferenceEquals(item.Message, match.Message) &&
                ReferenceEquals(item.Signal, match.Signal));
            if (candidate is null)
            {
                restoredFromFile = true;
                candidate = new CorrelationCandidate(
                    Did, match.Message, match.Signal, match.Database.Name, saved.Confidence,
                    [string.IsNullOrWhiteSpace(saved.Reason)
                        ? "restored from saved map" : saved.Reason]);
                CandidateChoices = [.. Candidates.Cast<object>(), candidate, NoCandidate];
                OnPropertyChanged(nameof(CandidateChoices));
            }
            var assessment = MappingReviewGuard.Assess(saved, Did,
                match.Database.Name, match.Message, match.Signal);
            if (!assessment.MayRetainReview)
                ReviewWarning = assessment.Reason;
        }
        else
        {
            ReviewWarning = matches.Length == 0
                ? "Saved CAN signal not found in loaded DBCs; manual review required."
                : "Multiple CAN signals match the saved mapping; no identity was guessed.";
        }

        _selectedCandidate = (object?)candidate ?? NoCandidate;
        Status = candidate is null ? MappingStatus.ToVerify :
            (saved.Status is MappingStatus.Confirmed or MappingStatus.Rejected) &&
            ReviewWarning.Length > 0 ? MappingStatus.ToVerify : saved.Status;
        OnPropertyChanged(nameof(SelectedCandidate));
        NotifyCandidateDetails();
        if (candidate is not null && (restoredFromFile || _candidateEvidenceStale))
            MarkTraceEvidenceStale();
    }

    private void NotifyCandidateDetails()
    {
        OnPropertyChanged(nameof(ActiveCandidate));
        OnPropertyChanged(nameof(CandidateText));
        OnPropertyChanged(nameof(MessageText));
        OnPropertyChanged(nameof(Confidence));
        OnPropertyChanged(nameof(ConfidenceText));
        OnPropertyChanged(nameof(CandidateScore));
        OnPropertyChanged(nameof(CandidateScoreText));
        OnPropertyChanged(nameof(ReasonText));
        OnPropertyChanged(nameof(IsLearnedAlias));
    }
}

/// <summary>
/// DID ↔ CAN Mapping workspace: loads CDD/ODX catalogs, runs the automatic correlation against
/// the loaded DBCs (using trace evidence when a trace is open), and lets the user review,
/// override, save, reload and export the resulting map.
/// </summary>
public sealed class MappingViewModel : ObservableObject
{
    private IReadOnlyList<DbcDatabase> _databases = [];
    private IReadOnlyList<CanFrame> _frames = [];
    private Func<int, int>? _sourceAt;
    private bool _multipleTraceSources;
    private long _evidenceGeneration;
    private IReadOnlyDictionary<string, SignalChangeReport>? _traceReports;
    private readonly List<DidCatalog> _catalogs = [];
    private string _searchText = string.Empty;
    private readonly DebounceTimer _searchDebounce = new();
    private int _statusFilterIndex;
    private string _statusMessage = "Load CDD/ODX or a DTC table for interpretation; load DID definitions and DBCs to correlate signals.";
    private bool _isCorrelating;
    private double _correlationProgressPercent;
    private string _correlationTimeText = string.Empty;
    private MappingRowViewModel? _selectedRow;
    private MappingRowViewModel? _focusedDiagnosticRow;
    private AsyncCommand? _loadCatalogCommand;
    private AsyncCommand? _correlateCommand;
    private AsyncCommand? _saveMapCommand;
    private AsyncCommand? _loadMapCommand;
    private AsyncCommand? _compareMapsCommand;
    private AsyncCommand? _exportComparisonCommand;
    private AsyncCommand? _exportCommand;
    private MappingDocument? _comparisonBaseline;
    private MappingDocument? _comparisonCurrent;
    private MappingComparisonReport? _comparison;
    private string _comparisonBaselineFile = string.Empty;
    private string _comparisonCurrentFile = string.Empty;
    private RelayCommand? _confirmCommand;
    private RelayCommand? _rejectCommand;
    private RelayCommand? _saveAliasCommand;
    private RelayCommand? _forgetAliasCommand;

    private readonly List<MappingRowViewModel> _allRows = [];

    public ObservableCollection<MappingRowViewModel> Rows { get; } = [];

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                // Editing the text box leaves the exact DID focus mode and
                // restores ordinary all-catalog searching immediately.
                _focusedDiagnosticRow = null;
                _searchDebounce.Trigger(RebuildRows);
            }
        }
    }

    /// <summary>0=All, 1=Suggested, 2=Confirmed, 3=To verify, 4=Rejected, 5=Unmatched.</summary>
    public int StatusFilterIndex
    {
        get => _statusFilterIndex;
        set
        {
            if (SetProperty(ref _statusFilterIndex, value))
            {
                _focusedDiagnosticRow = null;
                RebuildRows();
            }
        }
    }

    public MappingRowViewModel? SelectedRow
    {
        get => _selectedRow;
        set => SetProperty(ref _selectedRow, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    /// <summary>Previous/current saved association differences. Read-only:
    /// the Mapping grid and source files are never changed by this preview.</summary>
    public IReadOnlyList<MappingDiffRow> ComparisonRows { get; private set; } = [];
    public bool HasComparison => _comparison is not null;
    public string ComparisonSummary => _comparison?.Summary ?? "Choose two saved maps to compare.";

    /// <summary>True while Auto-correlate is running — drives the progress bar instead of a static "Correlating…" label.</summary>
    public bool IsCorrelating
    {
        get => _isCorrelating;
        private set => SetProperty(ref _isCorrelating, value);
    }

    /// <summary>0-100 progress of the current correlation run.</summary>
    public double CorrelationProgressPercent
    {
        get => _correlationProgressPercent;
        private set => SetProperty(ref _correlationProgressPercent, value);
    }

    /// <summary>"N/total DIDs · elapsed · ~remaining" while correlating.</summary>
    public string CorrelationTimeText
    {
        get => _correlationTimeText;
        private set => SetProperty(ref _correlationTimeText, value);
    }

    public string CatalogSummary => _catalogs.Count == 0
        ? "no diagnostic files loaded"
        : string.Join(", ", _catalogs.Select(catalog =>
            $"{Path.GetFileName(catalog.SourcePath)} · {catalog.Name} · ECU {(string.IsNullOrWhiteSpace(catalog.Ecu) ? "not specified" : catalog.Ecu)} ({catalog.Parameters.Count} DIDs, {catalog.DtcDefinitions.Count} DTCs)"));

    public AsyncCommand LoadCatalogCommand => _loadCatalogCommand ??= new AsyncCommand(LoadCatalogAsync);
    public AsyncCommand CorrelateCommand => _correlateCommand ??= new AsyncCommand(CorrelateAsync);
    public AsyncCommand SaveMapCommand => _saveMapCommand ??= new AsyncCommand(SaveMapAsync);
    public AsyncCommand LoadMapCommand => _loadMapCommand ??= new AsyncCommand(LoadMapAsync);
    public AsyncCommand CompareMapsCommand => _compareMapsCommand ??= new AsyncCommand(CompareMapsAsync);
    public AsyncCommand ExportComparisonCommand => _exportComparisonCommand ??= new AsyncCommand(ExportComparisonAsync);
    public AsyncCommand ExportCommand => _exportCommand ??= new AsyncCommand(ExportAsync);

    /// <summary>Confirm/reject the selected row — used by the grid's context menu, which (unlike
    /// the per-row inline buttons) is bound to this view model rather than the row.</summary>
    public RelayCommand ConfirmCommand => _confirmCommand ??= new RelayCommand(() => SelectedRow?.ConfirmCommand.Execute(null));
    public RelayCommand RejectCommand => _rejectCommand ??= new RelayCommand(() => SelectedRow?.RejectCommand.Execute(null));

    /// <summary>Remembers the selected row's active candidate as a standing alias for its DID
    /// parameter name, so future catalogs with the same parameter name surface it immediately.</summary>
    public RelayCommand SaveAliasCommand => _saveAliasCommand ??= new RelayCommand(() =>
    {
        if (SelectedRow?.ActiveCandidate is not { } candidate)
        {
            return;
        }

        DidAliasStore.Save(SelectedRow.Did, candidate.Database, candidate.Message, candidate.Signal);
        StatusMessage = $"Remembered: \"{SelectedRow.Name}\" → {candidate.Message.Name}.{candidate.Signal.Name}";
    });

    public RelayCommand ForgetAliasCommand => _forgetAliasCommand ??= new RelayCommand(() =>
    {
        if (SelectedRow is null)
        {
            return;
        }

        DidAliasStore.Remove(SelectedRow.Did);
        StatusMessage = $"Forgot learned alias for \"{SelectedRow.Name}\".";
    });

    public int CatalogCount => _catalogs.Count;
    public int DidCount => _allRows.Count;

    /// <summary>Source paths of every currently loaded CDD/ODX file, in load order — used to
    /// rebuild a full analysis session without the user re-picking files by hand.</summary>
    public IReadOnlyList<string> CatalogPaths => _catalogs.Select(catalog => catalog.SourcePath)
        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    /// <summary>Immutable snapshot used by the UDS transaction decoder. The source catalogs
    /// retain DID, DTC, routine and service metadata instead of exposing only display names.</summary>
    public IReadOnlyList<DidCatalog> Catalogs => _catalogs.ToArray();

    /// <summary>Path of the last saved or loaded <c>.rbgmap.json</c>, if any, so a session can
    /// remember which reviewed mapping belongs with this trace/catalog combination.</summary>
    public string? LastMapPath { get; private set; }

    public void SetContext(
        IReadOnlyList<DbcDatabase> databases,
        IReadOnlyList<CanFrame> frames,
        IReadOnlyDictionary<string, SignalChangeReport>? traceReports,
        Func<int, int>? sourceAt = null,
        bool multipleTraceSources = false)
    {
        if (ReferenceEquals(_databases, databases) && ReferenceEquals(_frames, frames) &&
            ReferenceEquals(_traceReports, traceReports) && ReferenceEquals(_sourceAt, sourceAt) &&
            _multipleTraceSources == multipleTraceSources)
            return;
        _evidenceGeneration++;
        _databases = databases;
        _frames = frames;
        _traceReports = traceReports;
        _sourceAt = sourceAt;
        _multipleTraceSources = multipleTraceSources;
        foreach (var row in _allRows)
        {
            row.MarkTraceEvidenceStale();
            row.TraceSummary = null;
            row.TraceEvidenceNotice = EvidenceNotice();
        }
        // A new/changed trace invalidates the observed-DID evidence; refresh it in the background.
        _ = RefreshTraceEvidenceAsync();
    }

    private async Task LoadCatalogAsync()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = DidCatalog.FileDialogFilter,
            Multiselect = true,
            Title = "Load diagnostic CDD/ODX/PDX or DTC table (CSV/TSV/XLSX)"
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        await LoadCatalogsAsync(dialog.FileNames);
    }

    /// <summary>Loads one or more CDD/ODX files by path directly, without a file dialog — used
    /// both by drag-drop and by session restore.</summary>
    public async Task LoadCatalogsAsync(IReadOnlyList<string> paths)
    {
        foreach (var path in paths)
        {
            try
            {
                // One file can describe several ECU variants (ODX layer inheritance, PDX packages):
                // replace everything previously loaded from it, then add every variant.
                var loaded = await Task.Run(() => DidCatalog.LoadAll(path));
                var sourcePath = Path.GetFullPath(path);
                _catalogs.RemoveAll(existing => existing.SourcePath == sourcePath);
                _catalogs.AddRange(loaded);
            }
            catch (Exception exception)
            {
                var failure = LoadFailureReport.DiagnosticFile(Path.GetFileName(path), exception);
                StatusMessage = failure.StatusLine;
                MessageBox.Show(failure.Message, failure.Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        RebuildAllRows();
        var observed = await RefreshTraceEvidenceAsync();
        StatusMessage = $"{DidCount:N0} DIDs and {_catalogs.Sum(catalog => catalog.DtcDefinitions.Count):N0} DTC definitions from {CatalogPaths.Count} file(s)." +
                        (_multipleTraceSources ? " DID trace evidence is unavailable with multiple recordings; open one independently." :
                            observed > 0 ? $" {observed:N0} DID(s) observed in the open trace (see \"In trace\")." : string.Empty) +
                        (DidCount > 0 ? " Run Auto-correlate to propose signals." :
                            " DTC names and descriptions are available in the diagnostic workspaces after trace analysis.");
        OnPropertyChanged(nameof(CatalogSummary));
        OnPropertyChanged(nameof(CatalogCount));
        OnPropertyChanged(nameof(DidCount));
    }

    /// <summary>Decodes ISO-TP ReadDataByIdentifier traffic in the open trace and attaches the
    /// observed values to each DID row, so trace evidence shows as soon as a CDD/ODX is loaded —
    /// no need to run the full correlation first. Returns how many DIDs were observed.</summary>
    public async Task<int> RefreshTraceEvidenceAsync()
    {
        if (_allRows.Count == 0 || _frames.Count == 0)
        {
            return 0;
        }

        var generation = _evidenceGeneration;
        var rows = _allRows.ToArray();
        var definitions = rows.Select(row => row.Did).ToArray();
        var frames = _frames;
        var sourceAt = _sourceAt;
        var multipleSources = _multipleTraceSources;
        var summaries = multipleSources
            ? new Dictionary<int, DidTraceSummary>()
            : await Task.Run(() => IsoTpDidExtractor.Summarize(frames, definitions, sourceAt));
        if (generation != _evidenceGeneration || !ReferenceEquals(frames, _frames)) return 0;
        foreach (var row in rows)
        {
            row.TraceSummary = summaries.TryGetValue(row.Did.Did, out var summary) ? summary : null;
            row.TraceEvidenceNotice = EvidenceNotice();
        }

        return summaries.Count;
    }

    /// <summary>Raised whenever the set of loaded DID definitions changes, so other workspaces
    /// (e.g. the Trace grid's UDS interpretation) can refresh their DID name lookups.</summary>
    public event EventHandler? CatalogsChanged;

    private Dictionary<int, string> _didNameByNumber = [];

    /// <summary>Display name of a DID from the loaded catalogs, or null when unknown.</summary>
    public string? ResolveDidName(int did) =>
        _didNameByNumber.TryGetValue(did, out var name) ? name : null;

    /// <summary>Focus exactly the DID, ECU and original source selected in
    /// Catalog Explorer, rather than showing other variants with the same
    /// numeric DID. Repeated definitions are refused rather than guessed.</summary>
    public bool TryFocusDid(int identifier, string ecu, string sourceFile)
    {
        if (string.IsNullOrWhiteSpace(ecu) || string.IsNullOrWhiteSpace(sourceFile))
            return false;
        var matches = _allRows.Where(row =>
            row.Did.Did == identifier &&
            string.Equals(row.Did.Ecu, ecu, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(row.Did.SourceFile, sourceFile,
                StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
        if (matches.Length != 1) return false;

        _focusedDiagnosticRow = matches[0];
        if (_statusFilterIndex != 0)
        {
            _statusFilterIndex = 0;
            OnPropertyChanged(nameof(StatusFilterIndex));
        }
        // SetProperty refreshes the search field without starting a deferred
        // rebuild that would immediately clear the selected DataGrid row.
        SetProperty(ref _searchText, $"0x{identifier:X4}", nameof(SearchText));
        RebuildRows();
        SelectedRow = matches[0];
        return true;
    }

    private void RebuildAllRows()
    {
        _evidenceGeneration++;
        _focusedDiagnosticRow = null;
        _allRows.Clear();
        foreach (var catalog in _catalogs)
        {
            foreach (var did in catalog.Parameters)
            {
                _allRows.Add(new MappingRowViewModel(did) { TraceEvidenceNotice = EvidenceNotice() });
            }
        }

        // A DID number can mean different things in different ECU variants. Only
        // show a single global DID name if all loaded catalogs agree on that name.
        _didNameByNumber = _catalogs.SelectMany(catalog => catalog.Parameters)
            .GroupBy(did => did.Did)
            .Where(group => group.Select(did => did.DisplayName)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1)
            .ToDictionary(group => group.Key, group => group.First().DisplayName);
        RebuildRows();
        CatalogsChanged?.Invoke(this, EventArgs.Empty);
    }

    private string EvidenceNotice() => _multipleTraceSources
        ? "Unavailable: multiple recordings"
        : "Not verified (not proof of absence)";

    private async Task CorrelateAsync()
    {
        if (_allRows.Count == 0)
        {
            StatusMessage = "Load a CDD/ODX file first.";
            return;
        }

        if (_databases.Count == 0)
        {
            StatusMessage = "Load at least one DBC first — candidates come from DBC signals.";
            return;
        }

        var databases = _databases;
        // A report from one logger must not boost a candidate against a DID
        // from another logger in a combined workspace. Until a physical source
        // is explicitly selected, use metadata-only ranking for multifile data.
        var reports = _multipleTraceSources ? null : _traceReports;
        var frames = _frames;
        var sourceAt = _sourceAt;
        var multipleSources = _multipleTraceSources;
        var generation = _evidenceGeneration;
        bool IsCurrent() => generation == _evidenceGeneration &&
            ReferenceEquals(frames, _frames) && ReferenceEquals(databases, _databases);
        var rows = _allRows.ToArray();
        var didDefinitions = rows.Select(row => row.Did).ToArray();
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        IsCorrelating = true;
        CorrelationProgressPercent = 0;
        CorrelationTimeText = "starting…";
        StatusMessage = $"Correlating {rows.Length:N0} DID(s) against {databases.Sum(db => db.AllSignals.Count()):N0} DBC signals…";
        try
        {
            var didTraceSummaries = multipleSources
                ? new Dictionary<int, DidTraceSummary>()
                : await Task.Run(() => IsoTpDidExtractor.Summarize(frames, didDefinitions, sourceAt));
            if (!IsCurrent()) return;

            // IProgress<T> captures this thread's SynchronizationContext at construction time,
            // so reports below marshal back to the UI thread automatically.
            var progress = new Progress<int>(done =>
            {
                if (!IsCurrent()) return;
                var total = Math.Max(1, rows.Length);
                CorrelationProgressPercent = Math.Clamp(done * 100d / total, 0, 100);
                var elapsed = stopwatch.Elapsed;
                if (done <= 0)
                {
                    CorrelationTimeText = $"0/{rows.Length:N0} DIDs";
                    return;
                }

                var estimatedTotalSeconds = elapsed.TotalSeconds * rows.Length / done;
                var remainingSeconds = Math.Max(0, estimatedTotalSeconds - elapsed.TotalSeconds);
                CorrelationTimeText = done >= rows.Length
                    ? $"{done:N0}/{rows.Length:N0} DIDs · {elapsed.TotalSeconds:F1}s elapsed"
                    : $"{done:N0}/{rows.Length:N0} DIDs · {elapsed.TotalSeconds:F1}s elapsed · ~{remainingSeconds:F1}s remaining";
            });

            var results = await Task.Run(() =>
            {
                var array = new IReadOnlyList<CorrelationCandidate>[rows.Length];
                for (var index = 0; index < rows.Length; index++)
                {
                    array[index] = DidSignalCorrelator.Correlate(rows[index].Did, databases, reports, didTraceSummaries);
                    // Reporting every DID (not batched) keeps the bar smooth without noticeably
                    // slowing correlation — each DID scores every signal, so it is not free, but
                    // the progress callback itself is cheap.
                    ((IProgress<int>)progress).Report(index + 1);
                }

                return array;
            });

            if (!IsCurrent()) return;

            for (var index = 0; index < rows.Length; index++)
            {
                rows[index].SetCandidates(ApplyLearnedAlias(rows[index].Did, results[index], databases));
                rows[index].TraceSummary = didTraceSummaries.TryGetValue(rows[index].Did.Did, out var summary)
                    ? summary
                    : null;
            }

            var matched = rows.Count(row => row.Status == MappingStatus.Suggested);
            var toVerify = rows.Count(row => row.Status == MappingStatus.ToVerify);
            var unmatched = rows.Count(row => row.Status == MappingStatus.Unmatched);
            StatusMessage = $"Auto-correlation done in {stopwatch.Elapsed.TotalSeconds:F1}s: " +
                            $"{matched:N0} suggested, {toVerify:N0} to verify, {unmatched:N0} unmatched." +
                            (multipleSources ? " DID trace evidence unavailable for multiple recordings; analyze one recording independently." :
                                didTraceSummaries.Count == 0 ? " No verified DID trace evidence (not proof of absence)." : $" ISO-TP evidence: {didTraceSummaries.Count:N0} DID(s) observed.") +
                            (reports is null && !multipleSources && frames.Count == 0
                                ? " (Load a trace to add on-bus evidence.)" : string.Empty);
            RebuildRows();
        }
        finally
        {
            IsCorrelating = false;
        }
    }

    /// <summary>
    /// If the analyst previously taught an alias for this exact DID parameter name, and a signal
    /// with that remembered name still exists in one of the currently loaded databases, promotes
    /// it to the top of the candidate list — ahead of whatever the automatic scorer produced —
    /// instead of making the analyst re-find and re-confirm it every time.
    /// </summary>
    private static IReadOnlyList<CorrelationCandidate> ApplyLearnedAlias(
        DidParameter did, IReadOnlyList<CorrelationCandidate> candidates, IReadOnlyList<DbcDatabase> databases)
    {
        var alias = DidAliasStore.Find(did);
        if (alias is null) return candidates;

        var matches = databases
            .Where(database => string.IsNullOrWhiteSpace(alias.Database) ||
                string.Equals(database.Name, alias.Database, StringComparison.OrdinalIgnoreCase))
            .SelectMany(database => database.AllMessageDefinitions
                .Where(message => message.Name == alias.Message &&
                    (alias.MessageIdentifier is null || message.Identifier == alias.MessageIdentifier.Value) &&
                    (alias.IsExtended is null || message.IsExtended == alias.IsExtended.Value))
                .SelectMany(message => message.Signals
                    .Where(signal => signal.Name == alias.Signal)
                    .Select(signal => (Database: database, Message: message, Signal: signal))))
            .ToArray();
        // A name-only legacy alias may point to several different DBC messages:
        // do not choose the first one and call it a learned association.
        if (matches.Length != 1) return candidates;
        var match = matches[0];
        var legacy = alias.Did is null;
        var confidence = legacy ? 0.50 : 0.99;
        var reasons = legacy
            ? new[] { MappingRowViewModel.LearnedAliasReason, "Legacy name-only alias: verify ECU, DID and database" }
            : new[] { MappingRowViewModel.LearnedAliasReason };
        var aliasCandidate = new CorrelationCandidate(
            did, match.Message, match.Signal, match.Database.Name, confidence, reasons);
        var rest = candidates.Where(candidate =>
            !(candidate.Database == match.Database.Name &&
              candidate.Message.Identifier == match.Message.Identifier &&
              candidate.Message.IsExtended == match.Message.IsExtended &&
              candidate.Signal.Name == match.Signal.Name));
        return [aliasCandidate, .. rest];
    }

    private void RebuildRows()
    {
        if (_focusedDiagnosticRow is not null &&
            !_allRows.Contains(_focusedDiagnosticRow))
            _focusedDiagnosticRow = null;
        var query = _allRows.AsEnumerable();
        if (_focusedDiagnosticRow is { } focused)
            query = query.Where(row => ReferenceEquals(row, focused));
        if (StatusFilterIndex > 0)
        {
            var status = StatusFilterIndex switch
            {
                1 => MappingStatus.Suggested,
                2 => MappingStatus.Confirmed,
                3 => MappingStatus.ToVerify,
                4 => MappingStatus.Rejected,
                _ => MappingStatus.Unmatched
            };
            query = query.Where(row => row.Status == status);
        }

        var search = SearchText.Trim();
        if (search.Length > 0)
        {
            query = query.Where(row =>
                row.DidHex.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                row.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                row.Description.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                row.CandidateText.Contains(search, StringComparison.OrdinalIgnoreCase));
        }

        Rows.Clear();
        foreach (var row in query.OrderBy(row => row.Did.Did))
        {
            Rows.Add(row);
        }
    }

    private MappingDocument BuildDocument() =>
        new(
            "RBG CAN Insight DID map",
            DateTimeOffset.Now,
            _allRows.Select(row => new DidSignalMapping(
                row.Did.Did,
                row.Did.Name,
                row.Did.Ecu,
                row.ActiveCandidate?.Database ?? string.Empty,
                row.ActiveCandidate?.Message.Name ?? string.Empty,
                row.ActiveCandidate?.Signal.Name ?? string.Empty,
                row.CandidateScore,
                row.ReasonText,
                row.Status,
                row.Notes,
                row.ActiveCandidate?.Message.Identifier,
                row.ActiveCandidate?.Message.IsExtended,
                MappingDefinitionFingerprint.ForDid(row.Did),
                row.ActiveCandidate is { } candidate
                    ? MappingDefinitionFingerprint.ForSignal(candidate.Database,
                        candidate.Message, candidate.Signal)
                    : null)).ToArray());

    private async Task SaveMapAsync()
    {
        if (_allRows.Count == 0)
        {
            return;
        }
        if (_allRows.Any(row => row.IsCandidateEvidenceStale))
        {
            StatusMessage = "Trace context changed. Run Auto-correlate before saving; existing analyst reviews are retained and the old map is unchanged.";
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = MappingDocument.FileDialogFilter,
            DefaultExt = MappingDocument.FileExtension,
            FileName = $"did-can-map-{DateTime.Now:yyyyMMdd-HHmmss}{MappingDocument.FileExtension}",
            Title = "Save DID ↔ CAN map"
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var document = BuildDocument();
        await Task.Run(() => document.Save(dialog.FileName));
        LastMapPath = dialog.FileName;
        StatusMessage = $"Map saved: {Path.GetFileName(dialog.FileName)}";
    }

    private async Task LoadMapAsync()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = MappingDocument.FileDialogFilter,
            Title = "Load DID ↔ CAN map"
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        await LoadMapFileAsync(dialog.FileName);
    }

    /// <summary>Loads a previously saved <c>.rbgmap.json</c> by path directly, without a file
    /// dialog — used by session restore. Requires the matching catalogs to already be loaded.</summary>
    public async Task LoadMapFileAsync(string path)
    {
        try
        {
            var document = await Task.Run(() => MappingDocument.Load(path));
            var byDid = _allRows.GroupBy(row => row.Did.Did)
                .ToDictionary(group => group.Key, group => group.ToArray());
            var candidates = new List<(MappingRowViewModel Row, DidSignalMapping Saved)>();
            var skippedAmbiguous = 0;
            foreach (var saved in document.Mappings)
            {
                if (!byDid.TryGetValue(saved.Did, out var possible)) continue;
                var exact = possible.Where(row =>
                    string.Equals(row.Did.Ecu, saved.Ecu, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(row.Did.Name, saved.DidName, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                if (exact.Length != 1 && !string.IsNullOrWhiteSpace(saved.DidDefinitionFingerprint))
                    exact = possible.Where(row =>
                        string.Equals(row.Did.Ecu, saved.Ecu, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(MappingDefinitionFingerprint.ForDid(row.Did),
                            saved.DidDefinitionFingerprint, StringComparison.Ordinal)).ToArray();
                if (exact.Length != 1)
                {
                    // A renamed/legacy DID is provisionally selectable only when
                    // its ECU+number resolves to precisely one source definition.
                    exact = possible.Where(row =>
                        string.Equals(row.Did.Ecu, saved.Ecu, StringComparison.OrdinalIgnoreCase))
                        .ToArray();
                }
                if (exact.Length != 1)
                {
                    skippedAmbiguous++;
                    continue;
                }
                candidates.Add((exact[0], saved));
            }
            var applied = 0;
            var requiresReview = 0;
            var duplicateTargetCount = 0;
            foreach (var matchGroup in candidates.GroupBy(match => match.Row))
            {
                if (matchGroup.Count() != 1)
                {
                    // Two saved entries targeting the same current DID cannot
                    // be applied in source-file order: refuse both instead.
                    duplicateTargetCount += matchGroup.Count();
                    continue;
                }
                var (row, saved) = matchGroup.Single();
                row.ApplySaved(saved, _databases);
                if (!string.IsNullOrEmpty(row.ReviewWarning)) requiresReview++;
                applied++;
            }

            LastMapPath = path;
            StatusMessage = $"Map loaded: {applied:N0} of {document.Mappings.Count:N0} entries applied." +
                            (requiresReview > 0 ? $" {requiresReview:N0} need re-review due to changed or unverified definitions." : string.Empty) +
                            (skippedAmbiguous > 0 ? $" {skippedAmbiguous:N0} ambiguous ECU/DID entries skipped." : string.Empty) +
                            (duplicateTargetCount > 0 ? $" {duplicateTargetCount:N0} conflicting saved entries skipped." : string.Empty);
            RebuildRows();
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception.Message, "Map not loaded", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>Compare two saved .rbgmap.json snapshots without requiring a trace,
    /// loading their catalogs or modifying either source document.</summary>
    private async Task CompareMapsAsync()
    {
        var baselineDialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = MappingDocument.FileDialogFilter,
            Title = "Compare maps — choose BASELINE .rbgmap.json"
        };
        if (baselineDialog.ShowDialog() != true) return;
        var baselinePath = baselineDialog.FileName;

        var currentDialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = MappingDocument.FileDialogFilter,
            Title = "Compare maps — choose CURRENT .rbgmap.json"
        };
        if (currentDialog.ShowDialog() != true) return;
        var currentPath = currentDialog.FileName;
        await CompareMapFilesAsync(baselinePath, currentPath);
    }

    /// <summary>Comparison entry point for local Windows GUI verification and
    /// guided automation; it does not modify either document or loaded catalogs.</summary>
    public async Task<bool> CompareMapFilesAsync(string baselinePath, string currentPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baselinePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentPath);
        try
        {
            var loaded = await Task.Run(() =>
            {
                var baseline = MappingDocument.Load(baselinePath);
                var current = MappingDocument.Load(currentPath);
                return (Baseline: baseline, Current: current,
                    Report: MappingDocumentComparer.Compare(baseline, current));
            });

            _comparisonBaseline = loaded.Baseline;
            _comparisonCurrent = loaded.Current;
            _comparisonBaselineFile = baselinePath;
            _comparisonCurrentFile = currentPath;
            _comparison = loaded.Report;
            ComparisonRows = loaded.Report.Rows;
            OnPropertyChanged(nameof(ComparisonRows));
            OnPropertyChanged(nameof(HasComparison));
            OnPropertyChanged(nameof(ComparisonSummary));
            StatusMessage = "Read-only map comparison ready. " +
                            loaded.Report.Summary + ". Use Export comparison… for XLSX or CSV ZIP.";
            return true;
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception.Message, "Map comparison failed",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    private async Task ExportComparisonAsync()
    {
        if (_comparison is null || _comparisonBaseline is null || _comparisonCurrent is null)
            return;

        var save = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "Excel workbook (*.xlsx)|*.xlsx|CSV files in ZIP (*.zip)|*.zip",
            FilterIndex = 1,
            DefaultExt = ".xlsx",
            FileName = $"did-can-map-diff-{DateTime.Now:yyyyMMdd-HHmmss}.xlsx",
            Title = "Export DID-CAN map comparison (read-only)"
        };
        if (save.ShowDialog() != true) return;

        var writeZip = save.FilterIndex == 2;
        var destination = Path.ChangeExtension(save.FileName, writeZip ? ".zip" : ".xlsx");
        var before = _comparisonBaseline;
        var after = _comparisonCurrent;
        var comparison = _comparison;
        var baselinePath = _comparisonBaselineFile;
        var currentPath = _comparisonCurrentFile;
        try
        {
            await Task.Run(() =>
            {
                var sheets = MappingComparisonExport.BuildSheets(
                    before, after, comparison, baselinePath, currentPath);
                MappingComparisonExport.Write(destination, sheets, csvZip: writeZip);
            });
            StatusMessage = $"Comparison exported: {Path.GetFileName(destination)}. " +
                            comparison.Summary + ". Source maps unchanged.";
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception.Message, "Map comparison export failed",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task ExportAsync()
    {
        if (_allRows.Count == 0)
        {
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "CSV (*.csv)|*.csv|Excel workbook (*.xlsx)|*.xlsx",
            DefaultExt = ".csv",
            FileName = $"did-can-map-{DateTime.Now:yyyyMMdd-HHmmss}.csv",
            Title = "Export DID ↔ CAN map"
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var rows = _allRows.OrderBy(row => row.Did.Did).ToArray();
        var columns = new List<ExportColumn>
        {
            new("DID", rows.Select(row => row.DidHex).ToArray()),
            new("Parameter", rows.Select(row => row.Name).ToArray()),
            new("Description", rows.Select(row => row.Description).ToArray()),
            new("Unit", rows.Select(row => row.Unit).ToArray()),
            new("Range", rows.Select(row => row.RangeText).ToArray()),
            new("ECU", rows.Select(row => row.Ecu).ToArray()),
            new("CAN message", rows.Select(row => row.ActiveCandidate?.Message.Name ?? string.Empty).ToArray()),
            new("CAN signal", rows.Select(row => row.ActiveCandidate?.Signal.Name ?? string.Empty).ToArray()),
            new("AssociationIndicator", rows.Select(row => row.ConfidenceText).ToArray()),
            new("CandidateMatchScore", rows.Select(row => row.CandidateScoreText).ToArray()),
            new("ReviewWarning", rows.Select(row => row.ReviewWarning).ToArray()),
            new("DidDefinitionFingerprint", rows.Select(row =>
                MappingDefinitionFingerprint.ForDid(row.Did)).ToArray()),
            new("CanSignalFingerprint", rows.Select(row => row.ActiveCandidate is { } candidate
                ? MappingDefinitionFingerprint.ForSignal(candidate.Database,
                    candidate.Message, candidate.Signal)
                : string.Empty).ToArray()),
            new("Reason", rows.Select(row => row.ReasonText).ToArray()),
            new("Status", rows.Select(row => row.StatusText).ToArray()),
            new("Notes", rows.Select(row => row.Notes).ToArray())
        };

        try
        {
            await Task.Run(() =>
            {
                if (dialog.FileName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
                {
                    TabularExport.WriteXlsx(dialog.FileName, "DID-CAN map", columns);
                }
                else
                {
                    TabularExport.WriteCsv(dialog.FileName, columns);
                }
            });
            StatusMessage = $"Exported: {Path.GetFileName(dialog.FileName)}";
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception.Message, "Export failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
