using System.IO;
using System.Globalization;
using RbgCanInsight.Analysis;
using RbgCanInsight.App.Common;
using RbgCanInsight.Correlation;
using RbgCanInsight.Diagnostics;
using RbgCanInsight.Domain;

namespace RbgCanInsight.App.ViewModels;

/// <summary>One decoded CDD/ODX field with a verified, navigable original
/// CAN source frame. An absent source is visibly unverified, never guessed.</summary>
public sealed class UdsDecodedFieldRowViewModel(DecodedDiagnosticValue value)
{
    public DecodedDiagnosticValue Value { get; } = value;
    public string FieldName => Value.Path + " · " + Value.Name;
    public string ValueText => Value.NumericValue is null
        ? string.Equals(Value.Physical, Value.Raw, StringComparison.Ordinal)
            ? "Raw only: " + Value.Raw + " · physical conversion not verified"
            : "Decoded non-numeric value: " + Value.Physical + " · raw " + Value.Raw
        : Value.Physical +
          (string.IsNullOrWhiteSpace(Value.Unit) ? string.Empty : " " + Value.Unit) +
          " · raw " + Value.Raw;
    public string MeaningText => Value.Meaning;
    public bool HasVerifiedSource => Value.HasVerifiedSource;
    public IReadOnlyList<DiagnosticSourceByteSpan> SourceSpans =>
        Value.SourceSpans ?? [];
    public string SourceText => Value.HasVerifiedSource
        ? Value.SourceEvidenceText
        : "Original CAN byte source not verified; raw preview only.";
    public int FirstSourceFrameIndex =>
        Value.SourceSpans is { Count: > 0 } spans ? spans[0].FrameIndex : -1;
}

public sealed class UdsTransactionRowViewModel
{
    public UdsTransactionRowViewModel(
        DiagnosticTransaction transaction, Func<int, string?>? sourceNameAt = null,
        Func<int, string?>? sourceEvidenceAt = null)
    {
        Transaction = transaction;
        SourceName = transaction.FrameIndices.Count > 0
            ? sourceNameAt?.Invoke(transaction.FrameIndices[0]) ?? "Current recording"
            : "Source unknown";
        SourceEvidenceText = transaction.FrameIndices.Count > 0
            ? sourceEvidenceAt?.Invoke(transaction.FrameIndices[0]) ?? SourceName
            : "No original CAN source frame witness.";
    }

    public DiagnosticTransaction Transaction { get; }
    /// <summary>Physical logger name resolved from the original raw frame
    /// index, not guessed from channel, response ID or ECU label.</summary>
    public string SourceName { get; }
    public string SourceEvidenceText { get; }
    private IReadOnlyList<UdsDecodedFieldRowViewModel>? _decodedFields;
    public IReadOnlyList<UdsDecodedFieldRowViewModel> DecodedFields =>
        _decodedFields ??= Transaction.Decoded.Values
            .Select(value => new UdsDecodedFieldRowViewModel(value)).ToArray();
    public bool HasDecodedFields => Transaction.Decoded.Values.Count > 0;
    public string Sequence => Transaction.Sequence.ToString(CultureInfo.InvariantCulture);
    public string TimeText => (Transaction.StartTimestampNanoseconds / 1_000_000_000d).ToString("F6", CultureInfo.InvariantCulture);
    public string DurationText => Transaction.ResponseTimeMilliseconds is { } responseTime ? $"{responseTime:F2} ms" : "—";
    public string ChannelText => $"Ch {(Transaction.Request?.Channel ?? Transaction.Responses.FirstOrDefault()?.Channel ?? 0) + 1}";
    public string DirectionText => Transaction.Endpoint.Direction switch
    {
        DiagnosticDirection.TesterToEcu => "Tester → ECU",
        DiagnosticDirection.EcuToTester => "ECU → Tester",
        _ => "Unknown"
    };
    public string EndpointText => $"{Transaction.Endpoint.Tester} ↔ {Transaction.Endpoint.Ecu}";
    public string ServiceText => $"0x{Transaction.Decoded.ServiceId:X2} · {Transaction.Decoded.ServiceName}";
    public string ObjectText => Transaction.Decoded.ObjectText;
    public string RequestText => Transaction.Decoded.RequestSummary;
    public string ResponseText => Transaction.Decoded.ResponseSummary;
    public bool HasIncompleteDidLayout => Transaction.Decoded.ServiceId == 0x22 &&
        Transaction.Outcome == DiagnosticOutcome.Positive &&
        !Transaction.Decoded.DidPayloadFullyDecoded;
    public string IncompleteDidLayoutMessage =>
        "Partial DID layout: some requested identifiers or subparameters could not be " +
        "decoded unambiguously. Verified rows below remain inspectable; do not treat " +
        "this response as a complete CDD/ODX interpretation.";
    public string OutcomeText => Transaction.Outcome switch
    {
        DiagnosticOutcome.Positive when HasIncompleteDidLayout =>
            "Positive · partial DID layout",
        DiagnosticOutcome.Positive => "Positive",
        DiagnosticOutcome.Negative => $"Negative · {Transaction.Decoded.NegativeResponseText}",
        DiagnosticOutcome.NoResponse => "No response",
        DiagnosticOutcome.SuppressedResponse => "Response suppressed",
        DiagnosticOutcome.Malformed => "Malformed",
        DiagnosticOutcome.OrphanResponse when Transaction.Quality.StartsWith(
            "Ambiguous ·", StringComparison.Ordinal) &&
            Transaction.Responses.Any(response =>
                response.Payload.Length > 0 && response.Payload[0] == 0x7F) =>
                "Unassigned NRC · multiple possible requests",
        DiagnosticOutcome.OrphanResponse when Transaction.Quality.StartsWith(
            "Ambiguous ·", StringComparison.Ordinal) =>
                "Unassigned positive response · multiple possible requests",
        DiagnosticOutcome.OrphanResponse => "Orphan response",
        _ => Transaction.Outcome.ToString()
    };
    public string QualityText => Transaction.Quality;
    public string FrameCountText => Transaction.FrameIndices.Count.ToString(CultureInfo.InvariantCulture);
    public string FrameIndicesText => Transaction.FrameIndices.Count == 0
        ? "—"
        : string.Join(", ", Transaction.FrameIndices.Select(index => (index + 1).ToString("N0", CultureInfo.InvariantCulture)));
    public string RequestPayloadText => Hex(Transaction.Request?.Payload);
    public string ResponsePayloadText => string.Join(Environment.NewLine, Transaction.Responses.Select((response, index) =>
        $"{(index + 1).ToString(CultureInfo.InvariantCulture)}: {Hex(response.Payload)}"));
    public string AddressingText => $"{Transaction.Endpoint.Addressing} · {Transaction.Endpoint.Evidence}";
    public string DecodedValuesText
    {
        get
        {
            var lines = Transaction.Decoded.Values.Select(value =>
                $"{value.Path} · {value.Name}: {value.Physical}{(string.IsNullOrWhiteSpace(value.Unit) ? string.Empty : " " + value.Unit)}  [raw {value.Raw}]" +
                (string.IsNullOrWhiteSpace(value.Meaning) ? string.Empty : $" — {value.Meaning}"))
                .ToList();
            lines.AddRange(Transaction.Decoded.Dtcs.Select(dtc =>
                $"{dtc.CodeText}{(string.IsNullOrWhiteSpace(dtc.Name) ? string.Empty : " · " + dtc.Name)} · {dtc.StatusText}"));
            return lines.Count == 0 ? "No catalog fields decoded for this transaction." : string.Join(Environment.NewLine, lines);
        }
    }
    public int FirstFrameIndex => Transaction.FrameIndices.FirstOrDefault(-1);
    public bool IsProblem => HasIncompleteDidLayout ||
        Transaction.Outcome is DiagnosticOutcome.Negative or DiagnosticOutcome.NoResponse or
        DiagnosticOutcome.Malformed or DiagnosticOutcome.OrphanResponse;

    public bool Matches(string search)
    {
        if (string.IsNullOrWhiteSpace(search)) return true;
        var terms = search.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var haystack = string.Join(' ', TimeText, ChannelText, DirectionText, EndpointText, ServiceText,
            ObjectText, RequestText, ResponseText, OutcomeText, QualityText, RequestPayloadText,
            ResponsePayloadText, DecodedValuesText);
        return terms.All(term => haystack.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    private static string Hex(byte[]? bytes) => bytes is null || bytes.Length == 0
        ? "—"
        : string.Join(' ', bytes.Select(value => value.ToString("X2", CultureInfo.InvariantCulture)));
}

/// <summary>Transaction-centric UDS workspace. It intentionally lives beside the raw trace
/// rather than adding more columns to it.</summary>
public sealed class UdsAnalysisViewModel : ObservableObject
{
    // Identity of the immutable captured CAN frame snapshot used by
    // this decoded analysis. Frame indices and matching byte values
    // alone are NOT sufficient to navigate after a new trace loads.
    private IReadOnlyList<CanFrame>? _analysisSourceFrames;
    private IReadOnlyList<UdsTransactionRowViewModel> _allRows = [];
    private Func<int, int>? _physicalSourceAt;
    private int _selectedSourceIndex;
    private IReadOnlyList<string> _physicalSourceChoices = ["All recordings"];
    private IReadOnlyList<string> _physicalSourcePaths = [];
    private IReadOnlyList<UdsTransactionRowViewModel> _rows = [];
    private UdsTransactionRowViewModel? _selectedRow;
    private string _searchText = string.Empty;
    private int _filterIndex;
    private RelayCommand? _jumpToTraceCommand;
    private RelayCommand? _openDecodedFieldSourceCommand;
    private UdsDecodedFieldRowViewModel? _selectedDecodedField;
    private DiagnosticSourceByteSpan? _selectedDecodedSourceSpan;
    private RelayCommand? _clearDidSampleFocusCommand;
    private RelayCommand? _openDidSampleSourceFrameCommand;
    private DiagnosticDidSampleRow? _selectedDidSample;
    private int? _focusedDid;
    private int _selectedViewIndex;
    private FaultMemoryEntry? _selectedFault;
    private RoutineExecution? _selectedRoutine;
    private UdsCanCorrelationResult? _selectedCorrelation;
    private IReadOnlyList<UdsCanCorrelationResult> _visibleCorrelationRows = [];
    private string _correlationSearchText = string.Empty;
    private readonly DebounceTimer _correlationSearchDebounce = new(TimeSpan.FromMilliseconds(150));
    private bool _correlationSourcePairsOnly;
    private int _sourcePairedCorrelationCount;
    private RelayCommand? _clearCorrelationFiltersCommand;
    private int _selectedCorrelationPairIndex;
    private RelayCommand? _previousCorrelationPairCommand;
    private RelayCommand? _nextCorrelationPairCommand;
    private RelayCommand? _openCorrelationDidFrameCommand;
    private RelayCommand? _openCorrelationCanFrameCommand;
    private OfflineFindingRowViewModel? _selectedFinding;
    private DiagnosticSequenceRowViewModel? _selectedSequenceEvent;
    private RecordedEcuValueRowViewModel? _selectedEcuValue;

    public IReadOnlyList<string> ViewChoices { get; } =
        ["Overview", "Transactions", "DID samples", "Fault memory", "Routines", "UDS ↔ CAN", "Transport",
         "Findings", "Sequence", "ECU snapshot", "Catalog coverage", "Catalog definitions"];

    public int SelectedViewIndex
    {
        get => _selectedViewIndex;
        set => SetProperty(ref _selectedViewIndex, Math.Clamp(value, 0, ViewChoices.Count - 1));
    }

    public IReadOnlyList<DiagnosticDidSampleRow> DidSampleRows { get; private set; } = [];
    public IReadOnlyList<DiagnosticDidSampleRow> VisibleDidSampleRows { get; private set; } = [];
    public DiagnosticDidSampleRow? SelectedDidSample
    {
        get => _selectedDidSample;
        set => SetProperty(ref _selectedDidSample, value);
    }
    public bool HasDidSampleFocus => _focusedDid is not null;
    public string DidSampleScopeText => _focusedDid is int did
        ? $"Recorded DID 0x{did:X4} · ALL observed endpoint groups (ECU source not independently verified)."
        : "All recorded DID values. ECU labels reflect reconstructed diagnostic endpoint evidence, not OEM identity proof.";

    public RelayCommand ClearDidSampleFocusCommand =>
        _clearDidSampleFocusCommand ??= new RelayCommand(() =>
        {
            _focusedDid = null;
            RefreshDidSampleRows();
        });

    public RelayCommand OpenDidSampleSourceFrameCommand =>
        _openDidSampleSourceFrameCommand ??= new RelayCommand(() =>
        {
            // Use the last decoded, completed positive response in the
            // selected DID sample group. Do not jump to tester request or
            // invent a raw frame for a synthetic or missing sample.
            var sample = SelectedDidSample?.Samples.LastOrDefault();
            if (sample is { FrameIndex: >= 0 })
                TraceFrameRequested?.Invoke(this, sample.FrameIndex);
        });

    /// <summary>Focus all observed samples of one DID without guessing which
    /// ECU/variant owns a shared numeric identifier. Does not transmit UDS.</summary>
    public bool FocusDidSamples(int identifier)
    {
        if (!DidSampleRows.Any(row => row.Did == identifier))
            return false;
        _focusedDid = identifier;
        RefreshDidSampleRows();
        SelectedViewIndex = 2;
        return true;
    }

    private void RefreshDidSampleRows()
    {
        VisibleDidSampleRows = _focusedDid is int did
            ? DidSampleRows.Where(row => row.Did == did).ToArray()
            : DidSampleRows;
        // Refresh the grid's ItemsSource before assigning its selection,
        // otherwise WPF may clear SelectedDidSample when replacing a view.
        OnPropertyChanged(nameof(VisibleDidSampleRows));
        SelectedDidSample = VisibleDidSampleRows.FirstOrDefault();
        OnPropertyChanged(nameof(HasDidSampleFocus));
        OnPropertyChanged(nameof(DidSampleScopeText));
    }
    public IReadOnlyList<FaultMemoryEntry> FaultRows { get; private set; } = [];
    public IReadOnlyList<RoutineExecution> RoutineRows { get; private set; } = [];
    public IReadOnlyList<DiagnosticServiceStatistic> ServiceRows { get; private set; } = [];
    public IReadOnlyList<UdsCanCorrelationResult> CorrelationRows { get; private set; } = [];

    /// <summary>Filtered read-only candidate list. A source-only view never
    /// deletes insufficient/ambiguous evidence from the underlying analysis.</summary>
    public IReadOnlyList<UdsCanCorrelationResult> VisibleCorrelationRows
    {
        get => _visibleCorrelationRows;
        private set => SetProperty(ref _visibleCorrelationRows, value);
    }

    public string CorrelationSearchText
    {
        get => _correlationSearchText;
        set
        {
            if (SetProperty(ref _correlationSearchText, value ?? string.Empty))
            {
                // The reset button must become enabled immediately, even
                // while a large-catalog text filter is still debounced.
                OnPropertyChanged(nameof(HasActiveCorrelationFilters));
                _correlationSearchDebounce.Trigger(RefreshCorrelationRows);
            }
        }
    }

    public bool CorrelationSourcePairsOnly
    {
        get => _correlationSourcePairsOnly;
        set
        {
            if (SetProperty(ref _correlationSourcePairsOnly, value))
                RefreshCorrelationRows();
        }
    }

    public int SourcePairedCorrelationCount => _sourcePairedCorrelationCount;
    public bool HasCorrelationRows => CorrelationRows.Count > 0;
    public bool HasVisibleCorrelationRows => VisibleCorrelationRows.Count > 0;
    public bool HasSelectedCorrelation => SelectedCorrelation is not null;
    public bool CanPreviousCorrelationPair =>
        SelectedCorrelation is { Points.Count: > 0 } && _selectedCorrelationPairIndex > 0;
    public bool CanNextCorrelationPair =>
        SelectedCorrelation is { Points.Count: > 0 } selected &&
        _selectedCorrelationPairIndex < selected.Points.Count - 1;
    public bool HasActiveCorrelationFilters =>
        !string.IsNullOrWhiteSpace(CorrelationSearchText) || CorrelationSourcePairsOnly;
    public string CorrelationVisibleSummary =>
        $"{VisibleCorrelationRows.Count:N0} of {CorrelationRows.Count:N0} hypotheses · " +
        $"{_sourcePairedCorrelationCount:N0} with recorded pairs";

    public string CorrelationEmptyStateText => CorrelationRows.Count == 0
        ? "No UDS ↔ CAN candidate evidence yet. Load a trace with UDS traffic, " +
          "a DBC and compatible CDD/ODX descriptions, then run the offline analysis."
        : !string.IsNullOrWhiteSpace(CorrelationSearchText)
            ? "No candidate matches this search. Clear the search, check DID, ECU, " +
              "DBC and bus names, or view all recorded associations."
            : CorrelationSourcePairsOnly
                ? "No candidates have independently paired source frames. " +
                  "Show all candidates to inspect insufficient or ambiguous evidence."
                : "No candidate is visible. Reset the filters to see all results.";

    public RelayCommand ClearCorrelationFiltersCommand =>
        _clearCorrelationFiltersCommand ??= new RelayCommand(() =>
        {
            _correlationSearchDebounce.Cancel();
            _correlationSearchText = string.Empty;
            OnPropertyChanged(nameof(CorrelationSearchText));
            _correlationSourcePairsOnly = false;
            OnPropertyChanged(nameof(CorrelationSourcePairsOnly));
            RefreshCorrelationRows();
        });

    private void RefreshCorrelationRows()
    {
        var searchTokens = CorrelationCandidateSearch.Tokenize(CorrelationSearchText);
        var previous = SelectedCorrelation;
        VisibleCorrelationRows = CorrelationRows
            .Where(row => (!CorrelationSourcePairsOnly || row.HasSourcePairs) &&
                          CorrelationCandidateSearch.MatchesPrepared(row, searchTokens))
            .ToArray();
        SelectedCorrelation = previous is not null &&
            VisibleCorrelationRows.Contains(previous)
                ? previous : VisibleCorrelationRows.FirstOrDefault();
        OnPropertyChanged(nameof(CorrelationVisibleSummary));
        OnPropertyChanged(nameof(HasVisibleCorrelationRows));
        OnPropertyChanged(nameof(CorrelationEmptyStateText));
        OnPropertyChanged(nameof(HasActiveCorrelationFilters));
    }

    /// <summary>Cross-workspace links must reveal the requested candidate
    /// even when a previous UDS-only view filter hides it.</summary>
    public bool RevealCorrelation(UdsCanCorrelationResult correlation)
    {
        if (!CorrelationRows.Contains(correlation))
            return false;

        CorrelationSearchText = string.Empty;
        _correlationSearchDebounce.Cancel();
        CorrelationSourcePairsOnly = false;
        RefreshCorrelationRows();
        SelectedCorrelation = correlation;
        SelectedViewIndex = 5;
        return true;
    }

    public IReadOnlyList<string> TransportIssues { get; private set; } = [];
    public IReadOnlyList<OfflineFindingRowViewModel> FindingRows { get; private set; } = [];
    public IReadOnlyList<DiagnosticSequenceRowViewModel> SequenceRows { get; private set; } = [];
    public IReadOnlyList<RecordedEcuValueRowViewModel> EcuSnapshotRows { get; private set; } = [];
    public IReadOnlyList<CatalogCoverageRowViewModel> CatalogCoverageRows { get; private set; } = [];

    public OfflineFindingRowViewModel? SelectedFinding
    {
        get => _selectedFinding;
        set => SetProperty(ref _selectedFinding, value);
    }

    public DiagnosticSequenceRowViewModel? SelectedSequenceEvent
    {
        get => _selectedSequenceEvent;
        set => SetProperty(ref _selectedSequenceEvent, value);
    }

    public RecordedEcuValueRowViewModel? SelectedEcuValue
    {
        get => _selectedEcuValue;
        set => SetProperty(ref _selectedEcuValue, value);
    }

    public FaultMemoryEntry? SelectedFault
    {
        get => _selectedFault;
        set => SetProperty(ref _selectedFault, value);
    }

    public RoutineExecution? SelectedRoutine
    {
        get => _selectedRoutine;
        set => SetProperty(ref _selectedRoutine, value);
    }

    public UdsCanCorrelationResult? SelectedCorrelation
    {
        get => _selectedCorrelation;
        set
        {
            if (SetProperty(ref _selectedCorrelation, value))
            {
                _selectedCorrelationPairIndex = 0;
                OnPropertyChanged(nameof(SelectedCorrelationPairIndex));
                NotifyCorrelationPairChanged();
                OnPropertyChanged(nameof(HasSelectedCorrelation));
            }
        }
    }

    /// <summary>Zero-based selected evidence pair, shared by the chart and
    /// the raw-frame controls. Click a recorded point or use Previous/Next.</summary>
    public int SelectedCorrelationPairIndex
    {
        get => _selectedCorrelationPairIndex;
        set
        {
            var count = SelectedCorrelation?.Points.Count ?? 0;
            var clamped = count == 0 ? 0 : Math.Clamp(value, 0, count - 1);
            if (_selectedCorrelationPairIndex == clamped)
                return;
            _selectedCorrelationPairIndex = clamped;
            OnPropertyChanged();
            NotifyCorrelationPairChanged();
        }
    }

    /// <summary>Selected observed pair, not an interpolated value or an
    /// inferred frame. The first pair is shown by default, with explicit
    /// previous/next controls for investigating every source observation.</summary>
    public CorrelationPoint? SelectedCorrelationPoint =>
        SelectedCorrelation is { Points.Count: > 0 } correlation
            ? correlation.Points[Math.Clamp(_selectedCorrelationPairIndex, 0,
                correlation.Points.Count - 1)]
            : null;

    public string CorrelationPairSummary
    {
        get
        {
            if (SelectedCorrelation is not { Points.Count: > 0 } result ||
                SelectedCorrelationPoint is not { } point)
                return "No independently paired source frames for this candidate.";

            return $"Pair {_selectedCorrelationPairIndex + 1:N0} / {result.Points.Count:N0} · " +
                   $"DID {point.DidValue:G6} at {point.DidTimestampNanoseconds / 1e9:F6}s · " +
                   $"CAN {point.SignalValue:G6} at {point.SignalTimestampNanoseconds / 1e9:F6}s · " +
                   $"lag {(point.SignalTimestampNanoseconds / 1e6 - point.DidTimestampNanoseconds / 1e6):+0.###;-0.###;0} ms";
        }
    }

    public string CorrelationPairFramesText
    {
        get
        {
            if (SelectedCorrelationPoint is not { } point)
                return "No paired source frames available. A candidate score alone is not evidence.";

            var did = point.DidFrameIndex >= 0
                ? $"DID response terminal frame #{point.DidFrameIndex + 1:N0}"
                : "DID response source frame unavailable";
            var can = point.SignalFrameIndex >= 0
                ? $"CAN source frame #{point.SignalFrameIndex + 1:N0}"
                : "CAN source frame unavailable";
            var channel = SelectedCorrelation is { SignalChannel: >= 0 } selected
                ? $" / CAN {selected.SignalChannel + 1}"
                : " / physical CAN channel unverified";
            var responseFrames = point.DidResponseFrameIndices?
                .Where(index => index >= 0).Distinct().ToArray() ?? [];
            var segments = responseFrames.Length == 0
                ? ""
                : $" · ISO-TP response frames: " +
                  string.Join(", ", responseFrames.Take(12).Select(index =>
                      (index + 1).ToString("N0", CultureInfo.InvariantCulture))) +
                  (responseFrames.Length > 12
                      ? $" … ({responseFrames.Length:N0} total)"
                      : "");
            return did + segments + " · " + can + channel;
        }
    }

    public RelayCommand PreviousCorrelationPairCommand =>
        _previousCorrelationPairCommand ??= new RelayCommand(() => MoveCorrelationPair(-1));

    public RelayCommand NextCorrelationPairCommand =>
        _nextCorrelationPairCommand ??= new RelayCommand(() => MoveCorrelationPair(1));

    public RelayCommand OpenCorrelationDidFrameCommand =>
        _openCorrelationDidFrameCommand ??= new RelayCommand(() =>
        {
            if (SelectedCorrelationPoint is { DidFrameIndex: >= 0 } point)
                TraceFrameRequested?.Invoke(this, point.DidFrameIndex);
        });

    public RelayCommand OpenCorrelationCanFrameCommand =>
        _openCorrelationCanFrameCommand ??= new RelayCommand(() =>
        {
            if (SelectedCorrelationPoint is { SignalFrameIndex: >= 0 } point)
                TraceFrameRequested?.Invoke(this, point.SignalFrameIndex);
        });

    private void MoveCorrelationPair(int direction)
    {
        if (SelectedCorrelation is not { Points.Count: > 0 } correlation)
            return;
        SelectedCorrelationPairIndex = Math.Clamp(
            _selectedCorrelationPairIndex + direction, 0, correlation.Points.Count - 1);
    }

    private void NotifyCorrelationPairChanged()
    {
        OnPropertyChanged(nameof(SelectedCorrelationPoint));
        OnPropertyChanged(nameof(CorrelationPairSummary));
        OnPropertyChanged(nameof(CorrelationPairFramesText));
        OnPropertyChanged(nameof(CanPreviousCorrelationPair));
        OnPropertyChanged(nameof(CanNextCorrelationPair));
    }

    public string InsightSummary =>
        $"{_allRows.Count:N0} transactions · {DidSampleRows.Sum(row => row.SampleCount):N0} DID samples · " +
        $"{FaultRows.Count:N0} DTCs · {RoutineRows.Count:N0} routines · " +
        $"{CorrelationRows.Count(row => row.Verdict == CorrelationVerdict.StrongCandidate):N0} strong candidates · {FindingRows.Count:N0} findings";

    public string FindingSummary => FindingRows.Count == 0
        ? "No offline analysis findings were generated."
        : $"{FindingRows.Count:N0} findings · {FindingRows.Count(row => row.Severity == OfflineFindingSeverity.Critical):N0} critical · " +
          $"{FindingRows.Count(row => row.Severity == OfflineFindingSeverity.Warning):N0} warnings";

    public event EventHandler<int>? TraceFrameRequested;
    public event EventHandler<DiagnosticSourceByteSpan>? TraceSourceSpanRequested;

    public IReadOnlyList<UdsTransactionRowViewModel> Rows
    {
        get => _rows;
        private set => SetProperty(ref _rows, value);
    }

    public IReadOnlyList<UdsTransactionRowViewModel> AllRows => _allRows;

    /// <summary>Transaction-table filter only. Other diagnostic tables are
    /// intentionally NOT represented as scoped until their computations can
    /// be independently rebuilt for each logger.</summary>
    public IReadOnlyList<string> PhysicalSourceChoices => _physicalSourceChoices;
    public bool HasMultiplePhysicalSources => _physicalSourceChoices.Count > 2;
    public int SelectedPhysicalSourceIndex
    {
        get => _selectedSourceIndex;
        set
        {
            var normalized = Math.Clamp(value, 0, _physicalSourceChoices.Count - 1);
            if (SetProperty(ref _selectedSourceIndex, normalized))
            {
                OnPropertyChanged(nameof(PhysicalSourceScopeText));
                ApplyFilter();
            }
        }
    }
    public string PhysicalSourceScopeText => SelectedPhysicalSourceIndex == 0
        ? "Transactions from all original recording files."
        : $"Transactions from {_physicalSourceChoices[SelectedPhysicalSourceIndex]} only. Other diagnostic panels remain combined.";

    public UdsTransactionRowViewModel? SelectedRow
    {
        get => _selectedRow;
        set
        {
            if (SetProperty(ref _selectedRow, value))
                SelectedDecodedField = value?.DecodedFields.FirstOrDefault();
        }
    }

    public UdsDecodedFieldRowViewModel? SelectedDecodedField
    {
        get => _selectedDecodedField;
        set
        {
            if (SetProperty(ref _selectedDecodedField, value))
                SelectedDecodedSourceSpan = value?.SourceSpans.FirstOrDefault();
        }
    }

    /// <summary>The exact FF or CF carrying the selected bytes.
    /// Users can independently inspect every frame of a split field.</summary>
    public DiagnosticSourceByteSpan? SelectedDecodedSourceSpan
    {
        get => _selectedDecodedSourceSpan;
        set
        {
            if (SetProperty(ref _selectedDecodedSourceSpan, value))
                OnPropertyChanged(nameof(CanOpenDecodedFieldSource));
        }
    }

    public bool CanOpenDecodedFieldSource =>
        SelectedDecodedField?.HasVerifiedSource == true &&
        SelectedDecodedSourceSpan is { FrameIndex: >= 0 } selected &&
        SelectedDecodedField.SourceSpans.Contains(selected);

    public RelayCommand OpenDecodedFieldSourceCommand =>
        _openDecodedFieldSourceCommand ??= new RelayCommand(() =>
        {
            if (CanOpenDecodedFieldSource &&
                SelectedDecodedSourceSpan is { } source)
            {
                // Source-aware consumers can open the exact immutable
                // recorded byte range. Legacy frame-only navigation still
                // works for hosts that have not connected the new event.
                if (TraceSourceSpanRequested is not null)
                    TraceSourceSpanRequested.Invoke(this, source);
                else
                    TraceFrameRequested?.Invoke(this, source.FrameIndex);
            }
        });

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value)) ApplyFilter();
        }
    }

    public int FilterIndex
    {
        get => _filterIndex;
        set
        {
            if (SetProperty(ref _filterIndex, value)) ApplyFilter();
        }
    }

    public IReadOnlyList<string> FilterChoices { get; } = ["All transactions", "Problems", "DTC", "Routines", "Negative", "No response"];

    public string SummaryText
    {
        get
        {
            var positive = _allRows.Count(row => row.Transaction.Outcome == DiagnosticOutcome.Positive);
            var negative = _allRows.Count(row => row.Transaction.Outcome == DiagnosticOutcome.Negative);
            var missing = _allRows.Count(row => row.Transaction.Outcome == DiagnosticOutcome.NoResponse);
            return _allRows.Count == 0
                ? "No complete UDS transactions found in the current trace."
                : $"{_allRows.Count:N0} transactions · {positive:N0} positive · {negative:N0} negative · {missing:N0} without response";
        }
    }

    public string VisibleSummaryText => Rows.Count == _allRows.Count
        ? SummaryText
        : $"{Rows.Count:N0} of {_allRows.Count:N0} transactions visible";

    public RelayCommand JumpToTraceCommand => _jumpToTraceCommand ??= new RelayCommand(() =>
    {
        if (SelectedRow is { FirstFrameIndex: >= 0 } row)
        {
            TraceFrameRequested?.Invoke(this, row.FirstFrameIndex);
        }
    });

    /// <summary>Only allow original-frame navigation for the exact
    /// capture object from which the diagnostic values were decoded.
    /// Reference equality intentionally rejects newly loaded traces,
    /// even when they happen to share frame counts or CAN identifiers.</summary>
    public bool IsAnalysisForFrames(IReadOnlyList<CanFrame> frames) =>
        _analysisSourceFrames is not null &&
        ReferenceEquals(_analysisSourceFrames, frames);

    public void SetTransactions(IReadOnlyList<DiagnosticTransaction> transactions)
    {
        SetAnalysis(DiagnosticInsightAnalyzer.Analyze(transactions, []), [], OfflineAnalysisSnapshot.Empty);
    }

    public void SetAnalysis(
        DiagnosticInsightSnapshot insight,
        IReadOnlyList<UdsCanCorrelationResult> correlations,
        OfflineAnalysisSnapshot? offlineAnalysis = null,
        IReadOnlyList<CanFrame>? sourceFrames = null,
        IReadOnlyList<string>? physicalSourcePaths = null,
        Func<int, int>? physicalSourceAt = null,
        Func<int, string?>? sourceNameAt = null,
        Func<int, string?>? sourceEvidenceAt = null)
    {
        _analysisSourceFrames = sourceFrames;
        _physicalSourceAt = physicalSourceAt;
        // Preserve a selected physical logger when diagnostic catalogs are
        // rebuilt on the same recording. Different file sets cannot inherit
        // an old source number by coincidence.
        var previousSourcePath = _selectedSourceIndex > 0 &&
            _selectedSourceIndex <= _physicalSourcePaths.Count
            ? _physicalSourcePaths[_selectedSourceIndex - 1] : null;
        var sources = physicalSourcePaths ?? [];
        _physicalSourcePaths = sources.ToArray();
        var duplicateFileNames = sources.GroupBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        _physicalSourceChoices = ["All recordings", .. sources.Select(path =>
            duplicateFileNames.Contains(Path.GetFileName(path))
                ? $"{Path.GetFileName(path)} — {Path.GetFileName(Path.GetDirectoryName(path))}"
                : Path.GetFileName(path))];
        _selectedSourceIndex = previousSourcePath is null ? 0 :
            Array.FindIndex(_physicalSourcePaths.ToArray(), path =>
                string.Equals(path, previousSourcePath, StringComparison.OrdinalIgnoreCase)) + 1;
        OnPropertyChanged(nameof(PhysicalSourceChoices));
        OnPropertyChanged(nameof(HasMultiplePhysicalSources));
        OnPropertyChanged(nameof(SelectedPhysicalSourceIndex));
        OnPropertyChanged(nameof(PhysicalSourceScopeText));
        _allRows = insight.Transactions
            .Select(transaction => new UdsTransactionRowViewModel(
                transaction, sourceNameAt, sourceEvidenceAt))
            .ToArray();
        OnPropertyChanged(nameof(AllRows));
        _focusedDid = null; // A new trace/analysis must not retain an invisible stale filter.
        // A DID name and ECU label can be identical in independent
        // loggers. Never turn interleaved samples into one apparent trace.
        DidSampleRows = insight.DidSamples
            .GroupBy(sample => (sample.Did, sample.Field, sample.Name,
                sample.Unit, sample.Ecu, sample.OriginalSourceIndex, sample.Channel))
            .Select(group => new DiagnosticDidSampleRow(group.Key.Did, group.Key.Name,
                group.Key.Field, group.Key.Unit, group.Key.Ecu,
                group.OrderBy(sample => sample.TimestampNanoseconds).ToArray(),
                group.Key.OriginalSourceIndex, group.Key.Channel,
                group.Key.OriginalSourceIndex >= 0 &&
                group.Key.OriginalSourceIndex < _physicalSourcePaths.Count
                    ? _physicalSourcePaths[group.Key.OriginalSourceIndex] : null))
            .OrderBy(row => row.OriginalSourceIndex)
            .ThenBy(row => row.Channel)
            .ThenBy(row => row.Did).ThenBy(row => row.Field).ToArray();
        FaultRows = insight.Faults;
        RoutineRows = insight.Routines;
        ServiceRows = insight.Services;
        CorrelationRows = correlations;
        _sourcePairedCorrelationCount = correlations.Count(row => row.HasSourcePairs);
        OnPropertyChanged(nameof(SourcePairedCorrelationCount));
        OnPropertyChanged(nameof(HasCorrelationRows));
        CorrelationSearchText = string.Empty;
        _correlationSearchDebounce.Cancel();
        CorrelationSourcePairsOnly = false;
        RefreshCorrelationRows();
        TransportIssues = insight.TransportIssues;
        var offline = offlineAnalysis ?? OfflineAnalysisSnapshot.Empty;
        FindingRows = offline.Findings.Select(finding => new OfflineFindingRowViewModel(finding)).ToArray();
        SequenceRows = offline.Sequence.Select(item => new DiagnosticSequenceRowViewModel(item)).ToArray();
        EcuSnapshotRows = offline.EcuSnapshot.Select(item => new RecordedEcuValueRowViewModel(item)).ToArray();
        CatalogCoverageRows = offline.CatalogCoverage.Select(item => new CatalogCoverageRowViewModel(item)).ToArray();
        SelectedFault = FaultRows.FirstOrDefault();
        SelectedRoutine = RoutineRows.FirstOrDefault();
        SelectedCorrelation = VisibleCorrelationRows.FirstOrDefault();
        SelectedFinding = FindingRows.FirstOrDefault();
        SelectedSequenceEvent = SequenceRows.FirstOrDefault();
        SelectedEcuValue = EcuSnapshotRows.FirstOrDefault();
        ApplyFilter();
        OnPropertyChanged(nameof(DidSampleRows));
        RefreshDidSampleRows();
        OnPropertyChanged(nameof(FaultRows));
        OnPropertyChanged(nameof(RoutineRows));
        OnPropertyChanged(nameof(ServiceRows));
        OnPropertyChanged(nameof(CorrelationRows));
        OnPropertyChanged(nameof(TransportIssues));
        OnPropertyChanged(nameof(FindingRows));
        OnPropertyChanged(nameof(SequenceRows));
        OnPropertyChanged(nameof(EcuSnapshotRows));
        OnPropertyChanged(nameof(CatalogCoverageRows));
        OnPropertyChanged(nameof(FindingSummary));
        OnPropertyChanged(nameof(InsightSummary));
        OnPropertyChanged(nameof(SummaryText));
    }

    /// <summary>Navigate from an arbitrary raw CAN frame to its containing UDS
    /// transaction, even when the user previously hid that row via UDS filters.
    /// The operation never sends diagnostic traffic to a live ECU.</summary>
    public bool SelectTransactionContainingFrame(int frameIndex)
    {
        if (frameIndex < 0) return false;
        var match = _allRows.FirstOrDefault(row =>
            row.Transaction.FrameIndices.Contains(frameIndex));
        if (match is null) return false;
        SelectedViewIndex = 1; // Transactions
        SearchText = string.Empty;
        FilterIndex = 0;
        SelectedRow = Rows.FirstOrDefault(row =>
            row.Transaction.Sequence == match.Transaction.Sequence) ?? match;
        return true;
    }

    public void OpenEvidence(int frameIndex)
    {
        if (frameIndex >= 0) TraceFrameRequested?.Invoke(this, frameIndex);
    }

    private void ApplyFilter()
    {
        var selectedSequence = SelectedRow?.Transaction.Sequence;
        Rows = _allRows.Where(row =>
            row.Matches(SearchText) && FilterMatches(row) &&
            (_selectedSourceIndex == 0 ||
             row.FirstFrameIndex >= 0 &&
             (_physicalSourceAt?.Invoke(row.FirstFrameIndex) ?? 0) == _selectedSourceIndex - 1))
            .ToArray();
        SelectedRow = selectedSequence is { } sequence
            ? Rows.FirstOrDefault(row => row.Transaction.Sequence == sequence)
            : Rows.FirstOrDefault();
        OnPropertyChanged(nameof(VisibleSummaryText));
    }

    private bool FilterMatches(UdsTransactionRowViewModel row) => FilterIndex switch
    {
        1 => row.IsProblem,
        2 => row.Transaction.Decoded.ServiceId is 0x19 or 0x14,
        3 => row.Transaction.Decoded.ServiceId == 0x31,
        4 => row.Transaction.Outcome == DiagnosticOutcome.Negative,
        5 => row.Transaction.Outcome == DiagnosticOutcome.NoResponse,
        _ => true
    };
}

public sealed class OfflineFindingRowViewModel(OfflineAnalysisFinding finding)
{
    public OfflineAnalysisFinding Finding { get; } = finding;
    public OfflineFindingSeverity Severity => Finding.Severity;
    public string SeverityText => Finding.Severity.ToString();
    public string Category => Finding.Category.ToString();
    public string Title => Finding.Title;
    public string Details => Finding.Details;
    public string Ecu => Finding.Ecu;
    public string TimeText => Finding.TimestampNanoseconds is { } timestamp
        ? (timestamp / 1_000_000_000d).ToString("F6", CultureInfo.InvariantCulture)
        : "—";
    public string FramesText => Finding.FrameIndices.Count == 0
        ? "—"
        : string.Join(", ", Finding.FrameIndices.Select(index => (index + 1).ToString(CultureInfo.InvariantCulture)));
    public int FirstFrameIndex => Finding.FrameIndices.FirstOrDefault(-1);
}

public sealed class DiagnosticSequenceRowViewModel(DiagnosticSequenceEvent item)
{
    public DiagnosticSequenceEvent Item { get; } = item;
    public int Sequence => Item.Sequence;
    public string TimeText => (Item.TimestampNanoseconds / 1_000_000_000d).ToString("F6", CultureInfo.InvariantCulture);
    public string Category => Item.Category;
    public string Ecu => Item.Ecu;
    public string Service => Item.Service;
    public string Operation => Item.Operation;
    public string Outcome => Item.Outcome;
    public string State => Item.State;
    public int FirstFrameIndex => Item.FrameIndices.FirstOrDefault(-1);
}

public sealed class RecordedEcuValueRowViewModel(RecordedEcuValue value)
{
    public RecordedEcuValue Value { get; } = value;
    public string Ecu => Value.Ecu;
    public string DidText => $"0x{Value.Did:X4}";
    public string Parameter => Value.Parameter;
    public string PhysicalValue => string.IsNullOrWhiteSpace(Value.Unit) ? Value.Value : $"{Value.Value} {Value.Unit}";
    public string TimeText => (Value.TimestampNanoseconds / 1_000_000_000d).ToString("F6", CultureInfo.InvariantCulture);
    public string Quality => Value.Quality;
    public int FrameIndex => Value.FrameIndex;
}

public sealed class CatalogCoverageRowViewModel(CatalogCoverageRow row)
{
    public CatalogCoverageRow Row { get; } = row;
    public string Entity => Row.Entity;
    public int Defined => Row.Defined;
    public int Observed => Row.Observed;
    public int Resolved => Row.Resolved;
    public int Unknown => Row.Unknown;
    public string CoverageText => Row.Observed == 0 ? "No observations" : Row.Coverage.ToString("P0", CultureInfo.InvariantCulture);
    public string UnknownText => Row.Unknown == 0 ? "—" : string.Join(", ", Row.UnknownItems);
}

public sealed record DiagnosticDidSampleRow(
    int Did,
    string Name,
    string Field,
    string Unit,
    string Ecu,
    IReadOnlyList<DiagnosticDidSample> Samples,
    int OriginalSourceIndex = -1,
    int Channel = -1,
    string? OriginalSourcePath = null)
{
    public string RecordingText => OriginalSourceIndex < 0 ? "Current recording" :
        $"Logger {OriginalSourceIndex + 1}" +
        (string.IsNullOrWhiteSpace(OriginalSourcePath)
            ? "" : $" · {Path.GetFileName(OriginalSourcePath)}");
    public string ChannelText => Channel < 0 ? "CAN channel unknown" : $"CAN {Channel + 1}";
    public string DidText => $"0x{Did:X4}";
    public int SampleCount => Samples.Count;
    public string FirstTimeText => $"{Samples[0].TimestampNanoseconds / 1_000_000_000d:F6}";
    public string LastTimeText => $"{Samples[^1].TimestampNanoseconds / 1_000_000_000d:F6}";
    public string RangeText
    {
        get
        {
            var values = Samples.Where(sample => sample.NumericValue is not null)
                .Select(sample => sample.NumericValue!.Value).ToArray();
            return values.Length == 0
                ? Samples[^1].TextValue
                : $"{values.Min():G6} … {values.Max():G6}{(string.IsNullOrWhiteSpace(Unit) ? string.Empty : " " + Unit)}";
        }
    }
}
