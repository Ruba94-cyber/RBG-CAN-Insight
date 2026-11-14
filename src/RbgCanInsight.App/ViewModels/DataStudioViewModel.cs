using System.IO;
using RbgCanInsight.Analysis;
using RbgCanInsight.Correlation;
using RbgCanInsight.Diagnostics;
using RbgCanInsight.App.Common;

namespace RbgCanInsight.App.ViewModels;

/// <summary>
/// Guided offline data-workspace navigation. Unlike the frame-oriented Trace,
/// Data Studio has four separate, beginner-friendly sections: recordings,
/// saved analyses, read-only results and comparisons. It reuses the existing
/// verified import/Trace/notebook services; no CAN frames are copied.
/// </summary>
public sealed class DataStudioViewModel : ObservableObject
{
    private readonly MainViewModel _host;
    private int _sectionIndex;
    private string _queryDraft = string.Empty;
    private RelayCommand<string>? _sectionCommand;
    private RelayCommand? _runQueryCommand;
    private RelayCommand? _showAllCommand;
    private RelayCommand<string>? _quickQueryCommand;
    private RelayCommand<string>? _quickLensCommand;
    private RelayCommand<TraceWorkspaceEntry>? _focusSourceCommand;
    private RelayCommand<TraceNotebookTabRow>? _openTabCommand;
    private RelayCommand? _createTabCommand;
    private RelayCommand? _saveDiagnosticInvestigationCommand;
    private RelayCommand? _openSelectedFrameCommand;
    private RelayCommand? _openTraceCommand;
    private RelayCommand? _openSignalsCommand;
    private RelayCommand? _openDiagnosticsCommand;
    private RelayCommand? _openStatisticsCommand;
    private AsyncCommand? _runAggregateCommand;
    private AsyncCommand? _exportAggregateCommand;
    private RelayCommand? _cancelAggregateCommand;
    private RelayCommand? _saveAggregateCellCommand;
    private RelayCommand<TraceNotebookSavedCellRow>? _removeSavedCellCommand;
    private RelayCommand? _inspectAggregateFrameCommand;
    private RelayCommand? _fullTimelineCommand;
    private IReadOnlyList<RbgCanInsight.Domain.CanFrame>? _aggregatedFrames;
    private VirtualRowList? _aggregatedRows;
    private Task<TraceAggregateResult>? _activeAggregateTask;
    private CancellationTokenSource? _activeAggregateCancellation;
    private TraceAggregateResult? _aggregateResult;
    private TraceAggregateRow? _selectedAggregateRow;
    private string _aggregateStatus = "Choose CAN IDs, original recordings or one-second traffic bursts, then Analyze visible rows.";
    private bool _isAggregating;
    private int _aggregateKindIndex;
    private bool _aggregatePanelExpanded;
    private AsyncCommand? _buildUdsSourceSummaryCommand;
    private RelayCommand? _cancelUdsSourceSummaryCommand;
    private RelayCommand? _inspectUdsSourceWitnessCommand;
    private CancellationTokenSource? _udsSummaryCancellation;
    private Task<UdsSourceSummaryReport>? _udsSummaryTask;
    private UdsSourceSummaryReport? _udsSourceReport;
    private UdsSourceSummaryRow? _selectedUdsSourceRow;
    private IReadOnlyList<RbgCanInsight.Domain.CanFrame>? _udsSummaryFrames;
    private string _udsSourceStatus = "Select Build UDS summary to count diagnostic services for each original logger.";
    private bool _isSummarizingUds;

    // A source-backed diagnostic notebook-style workbench. Rows point at
    // existing analysis objects; no raw CAN frames or transport PDUs are copied.
    private IReadOnlyList<UdsTransactionRowViewModel>? _diagnosticSourceRows;
    private IReadOnlyList<RbgCanInsight.Domain.CanFrame>? _diagnosticSourceFrames;
    private IReadOnlyList<DataStudioFaultRow> _allDtcRows = [];
    private IReadOnlyList<DataStudioMappingRow> _allMappingRows = [];
    private IReadOnlyList<string> _diagnosticSourceChoices = ["All recordings"];
    private int _diagnosticSourceIndex;
    private string _dtcSearchText = string.Empty;
    private int _dtcStateFilterIndex;
    private string _mappingSearchText = string.Empty;
    private bool _pairedMappingsOnly;
    private DataStudioFaultRow? _selectedDtc;
    private DtcStatusTransition? _selectedDtcTransition;
    private DecodedDtcRecord? _selectedDtcRecord;
    private DecodedDtcRecordValue? _selectedDtcRecordField;
    private DataStudioMappingRow? _selectedMapping;
    private DataStudioSnapshotCanRow? _selectedDtcCanCandidate;
    private RelayCommand? _openSelectedDtcCanCandidateCommand;
    private RelayCommand? _plotSelectedDtcCanCandidateCommand;
    private RelayCommand? _openDtcFrameCommand;
    private RelayCommand? _openDtcRecordSourceCommand;
    private RelayCommand? _openDtcFieldSourceCommand;
    private RelayCommand? _openDtcInUdsCommand;
    private RelayCommand? _openMappingDidFrameCommand;
    private RelayCommand? _openMappingCanFrameCommand;
    private RelayCommand? _openMappingInUdsCommand;
    public IReadOnlyList<DataStudioFaultRow> DtcRows { get; private set; } = [];
    public IReadOnlyList<DataStudioMappingRow> MappingRows { get; private set; } = [];
    public IReadOnlyList<DataStudioSnapshotCanRow> DtcCanCandidates { get; private set; } = [];
    public DataStudioSnapshotCanRow? SelectedDtcCanCandidate
    {
        get => _selectedDtcCanCandidate;
        set
        {
            if (SetProperty(ref _selectedDtcCanCandidate, value))
            {
                OnPropertyChanged(nameof(SelectedDtcCanExplanation));
                OpenSelectedDtcCanCandidateCommand.RaiseCanExecuteChanged();
                PlotSelectedDtcCanCandidateCommand.RaiseCanExecuteChanged();
            }
        }
    }
    public string DtcCanSummary => SelectedDtc is null
        ? "Select a fault history, then a recorded snapshot event."
        : SelectedDtcTransition is null
            ? "Select a recorded 0x19 snapshot event to see CDD/ODX DID-to-DBC leads."
            : SelectedDtc.OriginalSourceIndex < 0
                ? "Original logger not verified. No cross-domain links inferred."
                : DtcCanCandidates.Count == 0
                    ? "No matching source-verified, numerically decoded DID/DBC candidates for this fault event and ECU. This does not prove no CAN signal exists; inspect CDD/ODX, DBC and the original trace."
                    : $"{DtcCanCandidates.Count:N0} possible DID↔CAN link(s) from this snapshot event only. Shared DID, same logger and separate value pairs are not proof of signal identity.";
    public string SelectedDtcCanExplanation =>
        SelectedDtcCanCandidate?.Explanation ??
        "Select a candidate to inspect its original snapshot bytes, CDD/ODX definition and CAN/DBC fit.";
    /// <summary>Plot only the exact CAN timeline actually used by this
    /// candidate. Never pick a similarly named DBC signal from another
    /// logger, CAN channel or J1939 source address.</summary>
    public RelayCommand PlotSelectedDtcCanCandidateCommand =>
        _plotSelectedDtcCanCandidateCommand ??= new RelayCommand(() =>
        {
            if (!DiagnosticEvidenceIsCurrent ||
                SelectedDtcCanCandidate is not { } selected ||
                !DtcCanCandidates.Contains(selected)) return;
            var result = selected.Link.Correlation;
            var matches = _host.Signals.AllSeries.Where(series =>
                    CorrelationSourceIdentity.MatchesObservedTimeline(result, series.Timeline))
                .Take(2).ToArray();
            if (matches.Length != 1)
            {
                _host.StatusText = "Cannot open a unique source-verified CAN curve for this DTC snapshot candidate. Check active DBC, original logger and J1939 source address.";
                return;
            }
            if (_host.Signals.HasMultipleLoggers)
                _host.Signals.SelectedLoggerIndex = result.OriginalSourceIndex;
            _host.Signals.Plot(matches[0].Timeline);
            // Focus a real CAN signal sample used by the time-series
            // correlation, not the snapshot timestamp (which was not paired).
            _host.Signals.SeekToTimestamp(result.Points[0].SignalTimestampNanoseconds);
            _host.SelectedNavIndex = 3;
            _host.StatusText = "Source-exact CAN curve opened from a DTC snapshot DID candidate. This is an investigation lead, not verified snapshot-to-signal identity.";
        }, () => SelectedDtcCanCandidate is not null);

    public RelayCommand OpenSelectedDtcCanCandidateCommand =>
        _openSelectedDtcCanCandidateCommand ??= new RelayCommand(() =>
        {
            if (!DiagnosticEvidenceIsCurrent ||
                SelectedDtcCanCandidate is not { } candidate ||
                !DtcCanCandidates.Contains(candidate)) return;
            if (!_host.Uds.RevealCorrelation(candidate.Link.Correlation)) return;
            _host.Uds.SelectedViewIndex = 5; // UDS ↔ CAN evidence
            _host.SelectedNavIndex = 8;
        }, () => SelectedDtcCanCandidate is not null);

    public IReadOnlyList<string> DiagnosticSourceChoices => _diagnosticSourceChoices;
    public bool HasMultipleDiagnosticSources => _diagnosticSourceChoices.Count > 2;
    public int DiagnosticSourceIndex
    {
        get => _diagnosticSourceIndex;
        set
        {
            if (!SetProperty(ref _diagnosticSourceIndex,
                Math.Clamp(value, 0, _diagnosticSourceChoices.Count - 1))) return;
            RefreshDiagnosticFilters();
        }
    }

    public string DtcSearchText
    {
        get => _dtcSearchText;
        set
        {
            if (SetProperty(ref _dtcSearchText, value ?? string.Empty))
                RefreshDiagnosticFilters();
        }
    }

    public int DtcStateFilterIndex
    {
        get => _dtcStateFilterIndex;
        set
        {
            if (SetProperty(ref _dtcStateFilterIndex, Math.Clamp(value, 0, 3)))
                RefreshDiagnosticFilters();
        }
    }
    public string MappingSearchText
    {
        get => _mappingSearchText;
        set
        {
            if (SetProperty(ref _mappingSearchText, value ?? string.Empty))
                RefreshDiagnosticFilters();
        }
    }
    public bool PairedMappingsOnly
    {
        get => _pairedMappingsOnly;
        set
        {
            if (SetProperty(ref _pairedMappingsOnly, value))
                RefreshDiagnosticFilters();
        }
    }

    public DataStudioFaultRow? SelectedDtc
    {
        get => _selectedDtc;
        set
        {
            if (!SetProperty(ref _selectedDtc, value)) return;
            SelectedDtcTransition = value?.Fault.Timeline.LastOrDefault();
            OnPropertyChanged(nameof(SelectedDtcTimeline));
            OnPropertyChanged(nameof(SelectedDtcStatusExplanation));
            OnPropertyChanged(nameof(SelectedDtcRecords));
            OnPropertyChanged(nameof(DtcRecordEventExplanation));
            RefreshDtcCanCandidates();
        }
    }
    public IReadOnlyList<DtcStatusTransition> SelectedDtcTimeline =>
        SelectedDtc?.Fault.Timeline ?? [];
    public DtcStatusTransition? SelectedDtcTransition
    {
        get => _selectedDtcTransition;
        set
        {
            if (!SetProperty(ref _selectedDtcTransition, value)) return;
            OnPropertyChanged(nameof(SelectedDtcRecords));
            OnPropertyChanged(nameof(DtcRecordEventExplanation));
            OnPropertyChanged(nameof(SelectedDtcStatusExplanation));
            SelectedDtcRecord = SelectedDtcRecords.FirstOrDefault();
            RefreshDtcCanCandidates();
        }
    }
    public string SelectedDtcStatusExplanation =>
        DtcStatusEvidence.Explain(SelectedDtcTimeline, SelectedDtcTransition);

    /// <summary>Show only the records which were actually delivered in
    /// the selected 0x19 event, not the latest records across later reads.</summary>
    public IReadOnlyList<DecodedDtcRecord> SelectedDtcRecords =>
        SelectedDtcTransition?.Records ?? [];
    public string DtcRecordEventExplanation => SelectedDtcTransition is null
        ? "Select a DTC event above to inspect its recorded snapshot/extended data."
        : SelectedDtcRecords.Count == 0
            ? "This event has no snapshot/extended-data record. Choose a 0x19 record-read event above; other events' records are not substituted."
            : $"{SelectedDtcRecords.Count} record(s) from this event at " +
              $"{SelectedDtcTransition.TimestampNanoseconds / 1_000_000_000d:F6}s.";
    public DecodedDtcRecord? SelectedDtcRecord
    {
        get => _selectedDtcRecord;
        set
        {
            if (SetProperty(ref _selectedDtcRecord, value))
            {
                OnPropertyChanged(nameof(SelectedDtcRecordValues));
                OnPropertyChanged(nameof(DtcRecordExplanation));
                SelectedDtcRecordField = value?.Values.FirstOrDefault();
            }
        }
    }
    public IReadOnlyList<DecodedDtcRecordValue> SelectedDtcRecordValues =>
        SelectedDtcRecord?.Values ?? [];
    public DecodedDtcRecordValue? SelectedDtcRecordField
    {
        get => _selectedDtcRecordField;
        set
        {
            if (SetProperty(ref _selectedDtcRecordField, value))
                OnPropertyChanged(nameof(DtcRecordFieldEvidence));
        }
    }
    public string DtcRecordFieldEvidence => SelectedDtcRecordField?.SourceEvidenceText ??
        "Select a CDD/ODX field to inspect its original CAN source bytes.";
    public string DtcRecordExplanation => SelectedDtcRecord is not { } record
        ? "No recorded DTC snapshot/extended-data payload. Use ReadDTCInformation 0x19 responses and an applicable CDD/ODX for decoded fields."
        : $"{record.KindText} 0x{record.RecordNumber:X2} · " +
          (record.IsFullyDecoded
              ? "All stored record fields decoded against the available catalog."
              : "Partial or raw record; do not infer missing fields. " + record.Note) +
          $" · Original bytes: {record.RawHex}" +
          $" · Original CAN evidence: {record.SourceEvidenceText}";

    public DataStudioMappingRow? SelectedMapping
    {
        get => _selectedMapping;
        set => SetProperty(ref _selectedMapping, value);
    }
    public string DtcSummary => _allDtcRows.Count == 0
        ? "No DTCs decoded. Load recorded UDS 0x19 responses and CDD/ODX or ECU-scoped DTC tables for interpretation."
        : $"{DtcRows.Count:N0} visible / {_allDtcRows.Count:N0} original-source DTC histories · " +
          $"{_allDtcRows.Count(row => row.HasCatalogDefinition):N0} with CDD/ODX definitions";
    public string MappingSummary => _allMappingRows.Count == 0
        ? "No DID ↔ CAN candidates. Load a compatible CDD/ODX, DBC and recorded diagnostic traffic."
        : $"{MappingRows.Count:N0} visible / {_allMappingRows.Count:N0} DID ↔ CAN hypotheses · " +
          $"{_allMappingRows.Count(row => row.PairCount > 0):N0} with recorded frame pairs. Not proof of identity.";

    /// <summary>Refresh after an immutable UDS analysis snapshot is published.
    /// Avoid reassembling or redecoding CAN frames from the UI thread.</summary>
    public void RefreshDiagnosticWorkbench()
    {
        _diagnosticSourceRows = _host.Uds.AllRows;
        _diagnosticSourceFrames = _host.Frames;
        var paths = _host.CurrentTraceSourceIndex?.Paths ??
            (_host.IsTraceLoaded ? new[] { _host.CurrentTracePhysicalPath } : []);
        _diagnosticSourceChoices = ["All recordings",
            .. paths.Select((path, index) =>
                $"Logger {index + 1} · {System.IO.Path.GetFileName(path)}")];
        _diagnosticSourceIndex = 0;
        _allDtcRows = _host.Uds.FaultRows
            .Select(row => new DataStudioFaultRow(row, paths)).ToArray();
        _allMappingRows = _host.Uds.CorrelationRows
            .Select(row => new DataStudioMappingRow(row, paths)).ToArray();

        SelectedDtc = null;
        SelectedMapping = null;
        OnPropertyChanged(nameof(DiagnosticSourceChoices));
        OnPropertyChanged(nameof(HasMultipleDiagnosticSources));
        OnPropertyChanged(nameof(DiagnosticSourceIndex));
        RefreshDiagnosticFilters();
        RefreshDtcCanCandidates();
    }

    private void RefreshDiagnosticFilters()
    {
        var scope = _diagnosticSourceIndex - 1;
        var text = _dtcSearchText.Trim();
        IEnumerable<DataStudioFaultRow> dtcs = _allDtcRows;
        if (scope >= 0)
            dtcs = dtcs.Where(row => row.OriginalSourceIndex == scope);
        if (text.Length > 0)
            dtcs = dtcs.Where(row => row.Matches(text));
        dtcs = _dtcStateFilterIndex switch
        {
            1 => dtcs.Where(row => !row.CurrentlyCleared),
            2 => dtcs.Where(row => row.CurrentlyCleared),
            3 => dtcs.Where(row => row.RecordCount > 0),
            _ => dtcs
        };
        DtcRows = dtcs.ToArray();
        if (SelectedDtc is not null && !DtcRows.Contains(SelectedDtc))
            SelectedDtc = null;
        if (SelectedDtc is null)
            SelectedDtc = DtcRows.FirstOrDefault();
        OnPropertyChanged(nameof(DtcRows));
        OnPropertyChanged(nameof(DtcSummary));

        var search = _mappingSearchText.Trim();
        IEnumerable<DataStudioMappingRow> mappings = _allMappingRows;
        if (scope >= 0)
            mappings = mappings.Where(row => row.OriginalSourceIndex == scope);
        if (_pairedMappingsOnly)
            mappings = mappings.Where(row => row.Result.HasSourcePairs);
        if (search.Length > 0)
            mappings = mappings.Where(row => row.Matches(search));
        MappingRows = mappings.ToArray();
        if (SelectedMapping is not null && !MappingRows.Contains(SelectedMapping))
            SelectedMapping = null;
        if (SelectedMapping is null)
            SelectedMapping = MappingRows.FirstOrDefault();
        OnPropertyChanged(nameof(MappingRows));
        OnPropertyChanged(nameof(MappingSummary));
        RefreshDtcCanCandidates();
    }

    /// <summary>Recompute only from the chosen fault lifecycle event, not
    /// from latest-record rollups or a previously selected mapping filter.</summary>
    private void RefreshDtcCanCandidates()
    {
        var old = SelectedDtcCanCandidate;
        // An import or catalog refresh can replace the frame store while
        // the previously selected DTC is still visible. Do not present
        // stale source indices as current CAN/DBC investigation leads.
        var links = DiagnosticEvidenceIsCurrent
            ? DtcSnapshotCanLinker.Find(
                SelectedDtc?.OriginalSourceIndex ?? -1, SelectedDtcRecords,
                _allMappingRows.Select(row => row.Result).ToArray(),
                sourceAt: _host.CurrentFrameSourceAt,
                observedEcu: SelectedDtc?.Ecu)
            : [];
        DtcCanCandidates = links.Select(link => new DataStudioSnapshotCanRow(link)).ToArray();
        SelectedDtcCanCandidate = DtcCanCandidates.FirstOrDefault(row =>
            ReferenceEquals(row.Link.Correlation, old?.Link.Correlation) &&
            ReferenceEquals(row.Link.SnapshotField, old?.Link.SnapshotField));
        OnPropertyChanged(nameof(DtcCanCandidates));
        OnPropertyChanged(nameof(DtcCanSummary));
        OnPropertyChanged(nameof(SelectedDtcCanExplanation));
    }

    private bool DiagnosticEvidenceIsCurrent =>
        _diagnosticSourceFrames is not null &&
        ReferenceEquals(_host.Frames, _diagnosticSourceFrames) &&
        ReferenceEquals(_host.Uds.AllRows, _diagnosticSourceRows) &&
        _host.Uds.IsAnalysisForFrames(_diagnosticSourceFrames);

    public RelayCommand OpenDtcFrameCommand => _openDtcFrameCommand ??=
        new RelayCommand(() =>
        {
            if (!DiagnosticEvidenceIsCurrent || SelectedDtc is null ||
                SelectedDtcTransition is not { FrameIndices.Count: > 0 } transition)
                return;
            var index = transition.FrameIndices[^1];
            if ((uint)index < (uint)_host.Frames.Count)
                _host.OpenTraceFrame(index);
        });
    public RelayCommand OpenDtcRecordSourceCommand => _openDtcRecordSourceCommand ??=
        new RelayCommand(() =>
        {
            if (!DiagnosticEvidenceIsCurrent || SelectedDtc is null ||
                SelectedDtcRecord is not { HasVerifiedSource: true } record ||
                record.SourceSpans is not { Count: > 0 } spans) return;
            OpenVerifiedDtcEvidence(spans[0].FrameIndex);
        });

    public RelayCommand OpenDtcFieldSourceCommand => _openDtcFieldSourceCommand ??=
        new RelayCommand(() =>
        {
            if (!DiagnosticEvidenceIsCurrent || SelectedDtc is null ||
                SelectedDtcRecordField is not { HasVerifiedSource: true } field ||
                field.SourceSpans is not { Count: > 0 } spans) return;
            OpenVerifiedDtcEvidence(spans[0].FrameIndex);
        });

    private void OpenVerifiedDtcEvidence(int frameIndex)
    {
        if ((uint)frameIndex >= (uint)_host.Frames.Count || SelectedDtc is null)
            return;
        var sourceIndex = _host.CurrentTraceSourceIndex;
        if (sourceIndex is not null && SelectedDtc.OriginalSourceIndex >= 0 &&
            sourceIndex.SourceAt(frameIndex) != SelectedDtc.OriginalSourceIndex)
            return;
        _host.OpenTraceFrame(frameIndex);
    }

    public RelayCommand OpenDtcInUdsCommand => _openDtcInUdsCommand ??=
        new RelayCommand(() =>
        {
            if (!DiagnosticEvidenceIsCurrent || SelectedDtc is null) return;
            _host.Uds.SelectedFault = SelectedDtc.Fault;
            _host.Uds.SelectedViewIndex = 3; // Fault memory
            _host.SelectedNavIndex = 8; // UDS Analysis
        });
    public RelayCommand OpenMappingDidFrameCommand => _openMappingDidFrameCommand ??=
        new RelayCommand(() =>
        {
            if (!DiagnosticEvidenceIsCurrent || SelectedMapping?.Result.Points.FirstOrDefault()
                    is not { DidFrameIndex: >= 0 } point) return;
            if ((uint)point.DidFrameIndex < (uint)_host.Frames.Count)
                _host.OpenTraceFrame(point.DidFrameIndex);
        });
    public RelayCommand OpenMappingCanFrameCommand => _openMappingCanFrameCommand ??=
        new RelayCommand(() =>
        {
            if (!DiagnosticEvidenceIsCurrent || SelectedMapping?.Result.Points.FirstOrDefault()
                    is not { SignalFrameIndex: >= 0 } point) return;
            if ((uint)point.SignalFrameIndex < (uint)_host.Frames.Count)
                _host.OpenTraceFrame(point.SignalFrameIndex);
        });
    public RelayCommand OpenMappingInUdsCommand => _openMappingInUdsCommand ??=
        new RelayCommand(() =>
        {
            if (!DiagnosticEvidenceIsCurrent || SelectedMapping is null) return;
            _host.Uds.SelectedCorrelation = SelectedMapping.Result;
            _host.Uds.SelectedViewIndex = 5; // UDS ↔ CAN
            _host.SelectedNavIndex = 8;
        });

    public const int DataSection = 0;
    public const int NotebookSection = 1;
    public const int ResultsSection = 2;
    public const int CompareSection = 3;

    // ----- Saved analyses (library): named, reusable, verifiable against the original files -----
    private readonly StudioSavedAnalysisStore _savedStore =
        new(Path.Combine(AppStorage.ConfigDirectory, "studio-analyses"));
    private StudioSavedAnalysisRow? _selectedSaved;
    private string _savedName = string.Empty;
    private string _savedStatus = "Name the current analysis and press Save analysis to reopen it later.";
    private bool _isSavedBusy;
    private AsyncCommand? _saveAnalysisCommand;
    private AsyncCommand? _openSavedCommand;
    private RelayCommand? _deleteSavedCommand;
    private RelayCommand? _renameSavedCommand;

    public System.Collections.ObjectModel.ObservableCollection<StudioSavedAnalysisRow> SavedAnalyses { get; } = [];

    public StudioSavedAnalysisRow? SelectedSavedAnalysis
    {
        get => _selectedSaved;
        set
        {
            if (SetProperty(ref _selectedSaved, value))
            {
                if (value is not null) SavedName = value.Analysis.Name;
                _openSavedCommand?.RaiseCanExecuteChanged();
                _deleteSavedCommand?.RaiseCanExecuteChanged();
                _renameSavedCommand?.RaiseCanExecuteChanged();
            }
        }
    }

    public string SavedName
    {
        get => _savedName;
        set => SetProperty(ref _savedName, value);
    }

    public string SavedStatus
    {
        get => _savedStatus;
        private set => SetProperty(ref _savedStatus, value);
    }

    public bool IsSavedBusy
    {
        get => _isSavedBusy;
        private set => SetProperty(ref _isSavedBusy, value);
    }

    private void RefreshSaved(string? selectId = null)
    {
        var analyses = _savedStore.List(out var problems);
        SavedAnalyses.Clear();
        foreach (var analysis in analyses)
            SavedAnalyses.Add(new StudioSavedAnalysisRow(analysis));
        if (problems.Count > 0)
            SavedStatus = $"{problems.Count} saved file(s) could not be read and were left untouched: {problems[0]}";
        SelectedSavedAnalysis = SavedAnalyses.FirstOrDefault(r => r.Analysis.Id == selectId);
    }

    /// <summary>Saves the current recordings, DBC/catalog files (with SHA-256), Trace filters, analysis tabs and section.
    /// No CAN frames or results are stored.</summary>
    public AsyncCommand SaveAnalysisCommand => _saveAnalysisCommand ??= new AsyncCommand(async () =>
    {
        if (!_host.IsTraceLoaded)
        {
            SectionIndex = DataSection;
            SavedStatus = "Open a recording first: an analysis is saved together with the files it was built on.";
            return;
        }
        if (IsSavedBusy) return;
        IsSavedBusy = true;
        try
        {
            var name = string.IsNullOrWhiteSpace(SavedName) ? $"Analysis {DateTime.Now:yyyy-MM-dd HH:mm}" : SavedName;
            var paths = _host.StudioSourcePaths();
            SavedStatus = "Fingerprinting the source files…";
            var sources = await Task.Run(() => paths
                .Select(p => StudioSavedAnalysisStore.DescribeSource(p.Kind, p.Path)).ToList());
            var saved = _savedStore.Save(_host.BuildStudioAnalysis(name, sources));
            RefreshSaved(saved.Id);
            SavedStatus = $"Saved “{saved.Name}” with {sources.Count} source file(s).";
        }
        catch (StudioSavedAnalysisException ex) { SavedStatus = ex.Message; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SavedStatus = "The analysis was not saved: " + ex.Message;
        }
        finally { IsSavedBusy = false; }
    });

    /// <summary>Reopens the selected analysis after checking its files. Missing files abort; changed files need an explicit yes.</summary>
    public AsyncCommand OpenSavedCommand => _openSavedCommand ??= new AsyncCommand(async () =>
    {
        if (SelectedSavedAnalysis is not { } row || IsSavedBusy) return;
        IsSavedBusy = true;
        try
        {
            SavedStatus = "Checking the original files…";
            var (_, message) = await _host.OpenStudioAnalysisAsync(row.Analysis, problems =>
                System.Windows.MessageBox.Show(problems + "\n\nThese files differ from the ones used when the analysis was saved. Open it anyway?",
                    "Saved analysis", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning)
                == System.Windows.MessageBoxResult.Yes);
            SavedStatus = message;
        }
        finally { IsSavedBusy = false; }
    }, () => SelectedSavedAnalysis is not null);

    public RelayCommand DeleteSavedCommand => _deleteSavedCommand ??= new RelayCommand(() =>
    {
        if (SelectedSavedAnalysis is not { } row) return;
        try
        {
            _savedStore.Delete(row.Analysis.Id);
            RefreshSaved();
            SavedStatus = $"Deleted “{row.Analysis.Name}”. Original recordings are untouched.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SavedStatus = "Not deleted: " + ex.Message;
        }
    }, () => SelectedSavedAnalysis is not null);

    public RelayCommand RenameSavedCommand => _renameSavedCommand ??= new RelayCommand(() =>
    {
        if (SelectedSavedAnalysis is not { } row) return;
        try
        {
            var renamed = _savedStore.Rename(row.Analysis.Id, SavedName);
            RefreshSaved(renamed.Id);
            SavedStatus = $"Renamed to “{renamed.Name}”.";
        }
        catch (StudioSavedAnalysisException ex) { SavedStatus = ex.Message; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { SavedStatus = "Not renamed: " + ex.Message; }
    }, () => SelectedSavedAnalysis is not null);

    public DataStudioViewModel(MainViewModel host)
    {
        _host = host;
        RefreshSaved();
    }

    public int SectionIndex
    {
        get => _sectionIndex;
        set
        {
            if (!SetProperty(ref _sectionIndex, Math.Clamp(value, DataSection, CompareSection)))
                return;
            if (_sectionIndex == ResultsSection) QueryDraft = _host.Trace.SearchText;
            OnPropertyChanged(nameof(StepDescription));
        }
    }

    /// <summary>All results are read-only and derived from the actual visible
    /// Trace row indices. A cell never copies CAN payloads or invents ECU IDs.</summary>
    public TraceAggregateResult? AggregateResult
    {
        get => _aggregateResult;
        private set
        {
            if (SetProperty(ref _aggregateResult, value))
            {
                OnPropertyChanged(nameof(AggregateRows));
                OnPropertyChanged(nameof(HasAggregateResult));
            }
        }
    }
    public IReadOnlyList<TraceAggregateRow> AggregateRows => AggregateResult?.Rows ?? [];
    public bool HasAggregateResult => AggregateResult is not null;
    public string AggregateStatus
    {
        get => _aggregateStatus;
        private set => SetProperty(ref _aggregateStatus, value);
    }
    public bool IsAggregating
    {
        get => _isAggregating;
        private set => SetProperty(ref _isAggregating, value);
    }
    public bool AggregatePanelExpanded
    {
        get => _aggregatePanelExpanded;
        set => SetProperty(ref _aggregatePanelExpanded, value);
    }
    public int AggregateKindIndex
    {
        get => _aggregateKindIndex;
        set => SetProperty(ref _aggregateKindIndex, Math.Clamp(value, 0, 2));
    }
    public TraceAggregateRow? SelectedAggregateRow
    {
        get => _selectedAggregateRow;
        set => SetProperty(ref _selectedAggregateRow, value);
    }

    public UdsSourceSummaryReport? UdsSourceReport
    {
        get => _udsSourceReport;
        private set
        {
            if (SetProperty(ref _udsSourceReport, value))
                OnPropertyChanged(nameof(UdsSourceRows));
        }
    }
    public IReadOnlyList<UdsSourceSummaryRow> UdsSourceRows => UdsSourceReport?.Rows ?? [];
    public string UdsSourceStatus
    {
        get => _udsSourceStatus;
        private set => SetProperty(ref _udsSourceStatus, value);
    }
    public bool IsSummarizingUds
    {
        get => _isSummarizingUds;
        private set => SetProperty(ref _isSummarizingUds, value);
    }
    public UdsSourceSummaryRow? SelectedUdsSourceRow
    {
        get => _selectedUdsSourceRow;
        set => SetProperty(ref _selectedUdsSourceRow, value);
    }

    public string StepDescription => SectionIndex switch
    {
        DataSection => "1 / 4 · Choose recordings and confirm where the data came from.",
        NotebookSection => "2 / 4 · Keep different questions in named, reusable analysis tabs.",
        ResultsSection => "3 / 4 · Filter recorded frames and inspect the exact original evidence.",
        _ => "4 / 4 · Compare loggers and examine decoded results in specialist views."
    };

    public string QueryDraft
    {
        get => _queryDraft;
        set => SetProperty(ref _queryDraft, value);
    }

    public RelayCommand<string> SelectSectionCommand => _sectionCommand ??=
        new RelayCommand<string>(value =>
        {
            if (int.TryParse(value, out var index)) SectionIndex = index;
        });

    public RelayCommand RunQueryCommand => _runQueryCommand ??= new RelayCommand(() =>
    {
        if (!_host.IsTraceLoaded)
        {
            SectionIndex = DataSection;
            _host.StatusText = "First open an ASC, BLF, TRC or MF4 recording in Datasets.";
            return;
        }
        _host.Trace.ApplyQuery(QueryDraft);
        SectionIndex = ResultsSection;
        _host.StatusText = string.IsNullOrWhiteSpace(QueryDraft)
            ? "Showing all recorded frames (subject to the selected physical source)."
            : "Trace results filtered. Select a row to inspect source evidence.";
    });

    public RelayCommand ShowAllCommand => _showAllCommand ??= new RelayCommand(() =>
    {
        if (!_host.IsTraceLoaded) return;
        _host.Trace.ClearSourceScope();
        _host.Trace.ViewModeIndex = 0;
        _host.Trace.ApplyQuery("");
        QueryDraft = "";
        SectionIndex = ResultsSection;
        _host.StatusText = "Showing every imported frame. Original recordings remain unchanged.";
    });

    public RelayCommand<string> QuickQueryCommand => _quickQueryCommand ??=
        new RelayCommand<string>(query =>
        {
            if (!_host.IsTraceLoaded)
            {
                SectionIndex = DataSection;
                _host.StatusText = "Open a recording before running an analysis.";
                return;
            }
            QueryDraft = query ?? "";
            RunQueryCommand.Execute(null);
        });

    /// <summary>Plain-language shortcuts to the existing source-aware Trace
    /// lenses, without making a beginner learn filter syntax.</summary>
    public RelayCommand<string> QuickLensCommand => _quickLensCommand ??=
        new RelayCommand<string>(lens =>
        {
            if (!_host.IsTraceLoaded)
            {
                SectionIndex = DataSection;
                _host.StatusText = "Open a recording before running an analysis.";
                return;
            }
            _host.Trace.QuickAnalysisCommand.Execute(lens);
            QueryDraft = _host.Trace.SearchText;
            SectionIndex = ResultsSection;
            _host.StatusText = $"Applied '{_host.Trace.QuickLensLabel}' to the current recorded dataset.";
        });

    public RelayCommand<TraceWorkspaceEntry> FocusSourceCommand => _focusSourceCommand ??=
        new RelayCommand<TraceWorkspaceEntry>(entry =>
        {
            if (entry is null || !_host.IsTraceLoaded) return;
            // The explorer can also show source references when the analyst
            // opened one file independently. The merged index is then absent:
            // never claim to have focused a different physical logger.
            if (_host.HasMultipleTraceSources && !_host.CanFocusTraceSource)
            {
                SectionIndex = DataSection;
                _host.StatusText = "Combined recordings are not loaded. Choose View for the All traces row, then Explore.";
                return;
            }
            if (_host.CanFocusTraceSource)
                _host.FocusTraceSourceCommand.Execute(entry);
            else
                _host.StatusText = "Exploring the current recording. No additional source filter is needed.";
            _host.SelectedNavIndex = MainViewModel.DataStudioNavIndex;
            SectionIndex = ResultsSection;
        });

    private IReadOnlyList<string> DiagnosticOriginalPaths =>
        _host.CurrentTraceSourceIndex?.Paths ??
        (_host.IsTraceLoaded ? [_host.CurrentTracePhysicalPath] : []);

    /// <summary>Save the exact Data Studio DTC and DID/DBC filter recipe
    /// into the active analysis tab. Never copy raw frames, decoded fault
    /// rows or guessed ECU identities into the serialized notebook.</summary>
    public RelayCommand SaveDiagnosticInvestigationCommand =>
        _saveDiagnosticInvestigationCommand ??= new RelayCommand(() =>
        {
            if (!_host.IsTraceLoaded)
            {
                _host.StatusText = "Load original recordings before saving a diagnostic investigation.";
                return;
            }
            var paths = DiagnosticOriginalPaths;
            var selected = _diagnosticSourceIndex - 1;
            if (selected >= paths.Count)
            {
                _host.StatusText = "Original logger is unavailable. The diagnostic investigation was not saved.";
                return;
            }
            if (!_host.Notebook.HasActive)
                _host.Notebook.NewCommand.Execute(null);
            if (!_host.Notebook.HasActive)
                return;
            var recipe = new TraceNotebookDiagnosticRecipe(
                _dtcSearchText, _dtcStateFilterIndex, _mappingSearchText,
                _pairedMappingsOnly, selected < 0 ? null : paths[selected]);
            if (_host.Notebook.SaveDiagnosticRecipe(recipe))
            {
                _host.StatusText = "DTC and DID↔DBC investigation saved to the active analysis tab. Save session to persist it.";
            }
        });

    public RelayCommand<TraceNotebookTabRow> OpenTabCommand => _openTabCommand ??=
        new RelayCommand<TraceNotebookTabRow>(tab =>
        {
            if (tab is null || !_host.IsTraceLoaded) return;
            // A saved diagnostic review is tied to the exact physical
            // path, not a basename or logger ordinal from a different
            // import. Fail before applying any trace view if unavailable.
            var recipe = tab.Page.DiagnosticRecipe;
            var physicalPaths = DiagnosticOriginalPaths;
            var loggerIndex = recipe?.OriginalSourcePath is { } path
                ? TraceNotebookPages.FindSource(path, physicalPaths)
                : -1;
            if (recipe?.OriginalSourcePath is not null && loggerIndex < 0)
            {
                _host.StatusText = "Saved diagnostic logger was not imported. Reopen its exact recording before restoring this investigation.";
                SectionIndex = NotebookSection;
                return;
            }
            // Never infer success from the prior active tab: even reopening
            // the selected tab may fail if its physical source was removed.
            if (!_host.Notebook.TryOpen(tab))
            {
                _host.SelectedNavIndex = MainViewModel.DataStudioNavIndex;
                SectionIndex = NotebookSection;
                return;
            }
            _host.SelectedNavIndex = MainViewModel.DataStudioNavIndex;
            QueryDraft = _host.Trace.SearchText;
            if (recipe is not null)
            {
                // Exact reproducible diagnostic parameters (no raw frames).
                // The active tab's original source recipe remains independent
                // of the diagnostic source picker and is not overridden.
                _diagnosticSourceIndex = loggerIndex + 1;
                _dtcSearchText = recipe.DtcSearchText;
                _dtcStateFilterIndex = recipe.DtcStateFilterIndex;
                _mappingSearchText = recipe.MappingSearchText;
                _pairedMappingsOnly = recipe.PairedMappingsOnly;
                OnPropertyChanged(nameof(DiagnosticSourceIndex));
                OnPropertyChanged(nameof(DtcSearchText));
                OnPropertyChanged(nameof(DtcStateFilterIndex));
                OnPropertyChanged(nameof(MappingSearchText));
                OnPropertyChanged(nameof(PairedMappingsOnly));
                RefreshDiagnosticFilters();
                SectionIndex = CompareSection;
                _host.StatusText = "Saved diagnostic notebook filters restored against the imported original evidence.";
            }
            else
                SectionIndex = ResultsSection;
        });

    public RelayCommand CreateTabCommand => _createTabCommand ??= new RelayCommand(() =>
    {
        _host.Notebook.NewCommand.Execute(null);
        SectionIndex = NotebookSection;
    });

    public RelayCommand OpenSelectedFrameCommand => _openSelectedFrameCommand ??=
        new RelayCommand(() =>
        {
            if (_host.Trace.SelectedRow is not { } row)
            {
                _host.StatusText = "Select a result row first; its original CAN frame will open in Trace.";
                return;
            }
            _host.OpenTraceFrame(row.FrameIndex);
        });

    /// <summary>Compare observed UDS services across original physical
    /// recordings, never by guessed ECU or shared CAN IDs. The upstream
    /// ISO-TP and request-response engines now partition by source file.</summary>
    public AsyncCommand BuildUdsSourceSummaryCommand => _buildUdsSourceSummaryCommand ??=
        new AsyncCommand(BuildUdsSourceSummaryAsync);

    public RelayCommand CancelUdsSourceSummaryCommand => _cancelUdsSourceSummaryCommand ??=
        new RelayCommand(() => _udsSummaryCancellation?.Cancel());

    private async Task BuildUdsSourceSummaryAsync()
    {
        if (!_host.IsTraceLoaded)
        {
            SectionIndex = DataSection;
            UdsSourceStatus = "Open one or more recorded CAN files before comparing UDS services.";
            return;
        }
        if (IsSummarizingUds)
        {
            UdsSourceStatus = "The UDS summary is already running. Cancel it before starting another.";
            return;
        }

        using var cancellation = new CancellationTokenSource();
        _udsSummaryCancellation = cancellation;
        var token = cancellation.Token;
        IsSummarizingUds = true;
        UdsSourceReport = null;
        _udsSummaryFrames = null;
        SelectedUdsSourceRow = null;
        UdsSourceStatus = "Verifying physical CAN frame evidence for each UDS service…";
        try
        {
            var frames = _host.Frames;
            var rows = _host.Uds.AllRows;
            var index = _host.CurrentTraceSourceIndex;
            var paths = index?.Paths ?? new[] { _host.CurrentTracePhysicalPath };
            var lookup = index is null ? null : new Func<int, int>(index.SourceAt);
            _udsSummaryTask = Task.Run(() =>
                UdsSourceSummary.Calculate(rows.Select(row => row.Transaction).ToArray(),
                    frames.Count, paths, lookup, cancellationToken: token), token);
            var report = await _udsSummaryTask;
            token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(frames, _host.Frames) ||
                !ReferenceEquals(rows, _host.Uds.AllRows) ||
                !ReferenceEquals(index, _host.CurrentTraceSourceIndex))
            {
                UdsSourceStatus = "The diagnostic analysis changed. Rebuild this source summary.";
                return;
            }
            _udsSummaryFrames = frames;
            UdsSourceReport = report;
            UdsSourceStatus = report.Description +
                " · physical source and original frame indices verified.";
        }
        catch (OperationCanceledException)
        {
            UdsSourceStatus = "UDS summary cancelled. No partial source claims displayed.";
        }
        catch (Exception exception)
        {
            UdsSourceStatus = "UDS source summary rejected: " + exception.Message;
        }
        finally
        {
            if (ReferenceEquals(_udsSummaryCancellation, cancellation))
            {
                _udsSummaryCancellation = null;
                _udsSummaryTask = null;
            }
            IsSummarizingUds = false;
        }
    }

    public RelayCommand InspectUdsSourceWitnessCommand =>
        _inspectUdsSourceWitnessCommand ??= new RelayCommand(() =>
        {
            if (SelectedUdsSourceRow is not { } row ||
                !ReferenceEquals(_udsSummaryFrames, _host.Frames))
            {
                UdsSourceStatus = "Select a valid summary row, or rebuild the UDS source summary.";
                return;
            }
            _host.OpenTraceFrame(row.WitnessFrameIndex);
        });

    /// <summary>Group only the visible rows. Their virtual index maps to
    /// original recorded positions without instantiating row view models.</summary>
    public AsyncCommand RunAggregateCommand => _runAggregateCommand ??=
        new AsyncCommand(() => RunAggregationAsync((TraceAggregateKind)AggregateKindIndex));

    public RelayCommand CancelAggregateCommand => _cancelAggregateCommand ??=
        new RelayCommand(() => _activeAggregateCancellation?.Cancel());

    public AsyncCommand ExportAggregateCommand => _exportAggregateCommand ??=
        new AsyncCommand(async () =>
        {
            var result = AggregateResult;
            if (result is null)
            {
                AggregateStatus = "Run an analysis before exporting its table.";
                return;
            }
            if (!ReferenceEquals(_aggregatedFrames, _host.Frames) ||
                !ReferenceEquals(_aggregatedRows, _host.Trace.Rows) ||
                !_host.Trace.HasCommittedFilterRows)
            {
                AggregateStatus = "The visible dataset changed. Run the analysis again before exporting.";
                return;
            }
            var save = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Export source-backed CAN summary",
                Filter = "CSV table (*.csv)|*.csv",
                DefaultExt = ".csv",
                AddExtension = true,
                FileName = result.Kind switch
                {
                    TraceAggregateKind.BySource => "recordings-summary.csv",
                    TraceAggregateKind.BySecond => "traffic-bursts-by-second.csv",
                    _ => "can-id-summary.csv"
                }
            };
            if (save.ShowDialog() != true) return;
            // The native picker is modal, but a background source reload may
            // still complete while it is open. Recheck the exact snapshot.
            if (!ReferenceEquals(result, AggregateResult) ||
                !ReferenceEquals(_aggregatedFrames, _host.Frames) ||
                !ReferenceEquals(_aggregatedRows, _host.Trace.Rows) ||
                !_host.Trace.HasCommittedFilterRows)
            {
                AggregateStatus = "Recording or filter changed while choosing a destination. Export cancelled.";
                return;
            }
            try
            {
                var text = TraceAggregateCsvExport.Create(result);
                await File.WriteAllTextAsync(save.FileName, text,
                    System.Text.Encoding.UTF8);
                AggregateStatus = $"Exported {result.Rows.Count:N0} observed group(s) to {Path.GetFileName(save.FileName)}" +
                    (result.DisplayTruncated ? " (top groups only; truncated status included)." : ".");
                _host.StatusText = AggregateStatus;
            }
            catch (Exception exception)
            {
                AggregateStatus = "Summary export failed: " + exception.Message;
                _host.StatusText = AggregateStatus;
            }
        });

    public RelayCommand FullTimelineCommand => _fullTimelineCommand ??=
        new RelayCommand(() =>
        {
            if (!_host.IsTraceLoaded) return;
            _host.Trace.TailCommand.Execute(null);
            AggregateResult = null;
            _aggregatedFrames = null;
            _aggregatedRows = null;
            AggregateStatus = "Full timeline selected. Run the analysis again to save a reproducible cell.";
        });

    public RelayCommand SaveAggregateCellCommand => _saveAggregateCellCommand ??=
        new RelayCommand(() =>
        {
            if (AggregateResult is null || IsAggregating)
            {
                AggregateStatus = "Run an analysis first; only reproducible results can be saved.";
                return;
            }
            if (!ReferenceEquals(_aggregatedFrames, _host.Frames) ||
                !ReferenceEquals(_aggregatedRows, _host.Trace.Rows) ||
                !_host.Trace.HasCommittedFilterRows)
            {
                AggregateStatus = "The recorded dataset or filter changed. Run the summary again before saving.";
                return;
            }
            if (_host.Trace.VisibleThroughFrameIndex != _host.FrameCount - 1)
            {
                AggregateStatus = "Saved cells require the full recording. Choose Full timeline and analyze again.";
                return;
            }
            if (_host.Notebook.SaveAggregateCell(AggregateResult.Kind))
            {
                AggregateStatus = "Analysis cell saved with its tab. Save session to retain the recipe.";
                _host.StatusText = AggregateStatus;
                SectionIndex = NotebookSection;
            }
            else
                AggregateStatus = _host.StatusText;
        });

    public RelayCommand<TraceNotebookSavedCellRow> RemoveSavedCellCommand =>
        _removeSavedCellCommand ??= new RelayCommand<TraceNotebookSavedCellRow>(cell =>
        {
            if (_host.Notebook.RemoveAggregateCell(cell))
                _host.StatusText = "Saved aggregate cell removed. Original recordings are unchanged.";
        });

    public RelayCommand<TraceNotebookSavedCellRow> OpenSavedCellCommand =>
        _openSavedCellCommand ??= new RelayCommand<TraceNotebookSavedCellRow>(item =>
        {
            if (item is null) return;
            _ = RunSavedCellAsync(item);
        });
    private RelayCommand<TraceNotebookSavedCellRow>? _openSavedCellCommand;

    private async Task RunSavedCellAsync(TraceNotebookSavedCellRow row)
    {
        if (IsAggregating)
        {
            AggregateStatus = "An analysis is already running. Cancel it before reopening another cell.";
            return;
        }
        try
        {
            if (!_host.Notebook.TryOpenCellOwner(row))
            {
                SectionIndex = NotebookSection;
                return;
            }
            _host.SelectedNavIndex = MainViewModel.DataStudioNavIndex;
            QueryDraft = _host.Trace.SearchText;
            SectionIndex = ResultsSection;
            AggregateKindIndex = (int)row.Cell.Kind;
            // Saved analysis cells always run against the full imported
            // capture, not an arbitrary unpersisted replay prefix.
            _host.Trace.TailCommand.Execute(null);
            await RunAggregationAsync(row.Cell.Kind);
        }
        catch (Exception exception)
        {
            AggregateStatus = "Saved analysis cell cannot be reopened: " + exception.Message;
            _host.StatusText = AggregateStatus;
        }
    }

    private async Task RunAggregationAsync(TraceAggregateKind kind)
    {
        if (!_host.IsTraceLoaded)
        {
            SectionIndex = DataSection;
            AggregateStatus = "First open a recording in Datasets.";
            return;
        }
        if (IsAggregating)
        {
            AggregateStatus = "Another analysis is running. Cancel it before starting a new one.";
            return;
        }

        using var cancellation = new CancellationTokenSource();
        _activeAggregateCancellation = cancellation;
        var token = cancellation.Token;
        AggregatePanelExpanded = true;
        IsAggregating = true;
        AggregateStatus = "Preparing filtered rows…";
        AggregateResult = null;
        _aggregatedFrames = null;
        _aggregatedRows = null;
        SelectedAggregateRow = null;
        try
        {
            // The Trace matcher may run on a worker thread for multi-million
            // frame logs. Wait for the committed virtual-row snapshot before
            // aggregating, not a previous query's stale visible frame list.
            while (_host.Trace.IsFiltering)
            {
                token.ThrowIfCancellationRequested();
                await Task.Delay(35, token);
            }
            token.ThrowIfCancellationRequested();
            if (!_host.Trace.HasCommittedFilterRows)
            {
                AggregateStatus = "The Trace filter was cancelled or failed. Reapply it, then retry analysis.";
                return;
            }
            var frames = _host.Frames;
            var rows = _host.Trace.Rows;
            var index = _host.CurrentTraceSourceIndex;
            var paths = index?.Paths ??
                new[] { _host.CurrentTracePhysicalPath };
            var lookup = index is null ? null : new Func<int, int>(index.SourceAt);
            AggregateStatus = $"Analyzing {rows.Count:N0} visible frames…";
            // Keep a Task reference so imports can cancel and await its
            // completion before disposing any spill-backed source snapshot.
            var task = Task.Run(() => TraceAggregateCells.Calculate(
                frames, rows.Count, rows.FrameIndexAt, kind, lookup, paths,
                cancellationToken: token), token);
            _activeAggregateTask = task;
            var result = await task;
            token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(frames, _host.Frames) ||
                !ReferenceEquals(rows, _host.Trace.Rows) ||
                !_host.Trace.HasCommittedFilterRows)
            {
                AggregateStatus = "Source or filter changed during analysis. Run again for an accurate result.";
                return;
            }
            _aggregatedFrames = frames;
            _aggregatedRows = rows;
            AggregateResult = result;
            AggregateStatus = result.Description +
                " · source and timestamps come from the recorded frames.";
        }
        catch (OperationCanceledException)
        {
            AggregateStatus = "Analysis cancelled; no partial result was presented.";
        }
        catch (Exception exception)
        {
            AggregateStatus = "Analysis failed: " + exception.Message;
        }
        finally
        {
            if (ReferenceEquals(_activeAggregateCancellation, cancellation))
            {
                _activeAggregateCancellation = null;
                _activeAggregateTask = null;
            }
            IsAggregating = false;
        }
    }

    /// <summary>Stop background scans before an import can close the previous
    /// memory-mapped CAN frame storage. Also clear stale UI computations.</summary>
    public async Task CancelAndInvalidateAggregatesAsync()
    {
        _activeAggregateCancellation?.Cancel();
        _udsSummaryCancellation?.Cancel();
        if (_udsSummaryTask is { } udsTask)
        {
            try { await udsTask; }
            catch (OperationCanceledException) { }
            catch (Exception) { /* The action reports invalid evidence. */ }
        }
        if (_activeAggregateTask is { } task)
        {
            try { await task; }
            catch (OperationCanceledException) { }
            catch (Exception) { /* The action reports its own failure. */ }
        }
        AggregateResult = null;
        _aggregatedFrames = null;
        _aggregatedRows = null;
        SelectedAggregateRow = null;
        AggregateStatus = "Recording changed. Run an analysis on the current dataset.";
        UdsSourceReport = null;
        _udsSummaryFrames = null;
        SelectedUdsSourceRow = null;
        UdsSourceStatus = "Recording changed. Rebuild the source-specific UDS summary.";
        // Never leave links to a disposed memory-mapped trace after import.
        _diagnosticSourceFrames = null;
        _diagnosticSourceRows = null;
        _allDtcRows = [];
        _allMappingRows = [];
        _diagnosticSourceChoices = ["All recordings"];
        _diagnosticSourceIndex = 0;
        SelectedDtc = null;
        SelectedMapping = null;
        OnPropertyChanged(nameof(DiagnosticSourceChoices));
        OnPropertyChanged(nameof(HasMultipleDiagnosticSources));
        OnPropertyChanged(nameof(DiagnosticSourceIndex));
        RefreshDiagnosticFilters();
    }

    public RelayCommand InspectAggregateFrameCommand =>
        _inspectAggregateFrameCommand ??= new RelayCommand(() =>
        {
            if (SelectedAggregateRow is not { } row)
            {
                AggregateStatus = "Select a summary row to open its first observed original frame.";
                return;
            }
            _host.OpenTraceFrame(row.FirstFrameIndex);
        });

    public RelayCommand OpenTraceCommand => _openTraceCommand ??=
        new RelayCommand(() => _host.SelectedNavIndex = 1);
    public RelayCommand OpenSignalsCommand => _openSignalsCommand ??=
        new RelayCommand(() => _host.SelectedNavIndex = 3);
    public RelayCommand OpenDiagnosticsCommand => _openDiagnosticsCommand ??=
        new RelayCommand(() => _host.SelectedNavIndex = 8);
    public RelayCommand OpenStatisticsCommand => _openStatisticsCommand ??=
        new RelayCommand(() => _host.SelectedNavIndex = 6);
}

/// <summary>A saved analysis as shown in the Analyses section.</summary>
public sealed record StudioSavedAnalysisRow(RbgCanInsight.Analysis.StudioSavedAnalysis Analysis)
{
    public string Title => Analysis.Name;
    public string Details =>
        $"{Analysis.SavedUtc.ToLocalTime():yyyy-MM-dd HH:mm} · {Analysis.Sources.Count(s => s.Kind == "trace")} recording(s) · " +
        $"{Analysis.Pages.Count} tab(s)" + (string.IsNullOrWhiteSpace(Analysis.TraceSearchText) ? "" : $" · filter: {Analysis.TraceSearchText}");
}
