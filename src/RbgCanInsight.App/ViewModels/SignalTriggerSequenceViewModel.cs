using System.IO;
using System.Globalization;
using RbgCanInsight.Analysis;
using RbgCanInsight.App.Common;
using RbgCanInsight.Domain;

namespace RbgCanInsight.App.ViewModels;

public sealed class SignalTriggerSequenceEventRow(SignalTriggerSequenceEvent item)
{
    public SignalTriggerSequenceEvent Model { get; } = item;
    public int AFrame => Model.FirstSourceFrameIndex + 1;
    public int BFrame => Model.SecondSourceFrameIndex + 1;
    public string ATimeSeconds => (Model.FirstObservationNanoseconds / 1_000_000_000d)
        .ToString("F6", CultureInfo.InvariantCulture);
    public string BTimeSeconds => (Model.SecondObservationNanoseconds / 1_000_000_000d)
        .ToString("F6", CultureInfo.InvariantCulture);
    public string ObservedDelayMs => (Model.ObservedDelayNanoseconds / 1_000_000d)
        .ToString("F3", CultureInfo.InvariantCulture);
    public string ContextStartSeconds => (Model.ContextStartNanoseconds / 1_000_000_000d)
        .ToString("F6", CultureInfo.InvariantCulture);
    public string ContextEndSeconds => (Model.ContextEndNanoseconds / 1_000_000_000d)
        .ToString("F6", CultureInfo.InvariantCulture);
}

public sealed class TriggerContextFrameRow(int index, CanFrame frame)
{
    public int OriginalFrameNumber => index + 1;
    public string TimestampSeconds =>
        (frame.TimestampNanoseconds / 1_000_000_000d).ToString("F6", CultureInfo.InvariantCulture);
    public string Channel => $"CAN {frame.Channel + 1}";
    public string Identifier => $"0x{frame.Identifier:X8} ({(frame.IsExtended ? "29" : "11")}-bit)";
    public string Data => Convert.ToHexString(frame.GetPayload());
    public int FrameIndex => index;
}

/// <summary>
/// Opt-in offline composition of two source-scoped trigger rules with strict
/// source time ordering, one-to-one matched evidence and bounded context
/// windows. No ECU control, synthetic event timing or background polling.
/// </summary>
public sealed class SignalTriggerSequenceViewModel : ObservableObject
{
    private const int MaxVisibleChoices = 800;
    private IReadOnlyList<SignalTimeline> _timelines = [];
    private IReadOnlyList<CanFrame> _frames = [];
    private IReadOnlyList<SignalTriggerChoice> _allChoices = [];
    private IReadOnlyList<SignalTriggerChoice> _firstChoices = [];
    private IReadOnlyList<SignalTriggerChoice> _secondChoices = [];
    private SignalTriggerChoice? _first, _second;
    private string _firstSearch = "", _secondSearch = "";
    private int _firstModeIndex, _secondModeIndex, _sequenceModeIndex;
    private string _firstThresholdText = "0", _secondThresholdText = "0";
    private string _firstStateText = "", _secondStateText = "";
    private string _firstToleranceText = "0", _secondToleranceText = "0";
    private string _minDelayMsText = "0", _maxDelayMsText = "500";
    private string _preContextMsText = "1000", _postContextMsText = "1000";
    private IReadOnlyList<SignalTriggerSequenceEventRow> _results = [];
    private SignalTriggerSequenceEventRow? _selectedEvent;
    private IReadOnlyList<TriggerContextFrameRow> _contextFrames = [];
    private TriggerContextFrameRow? _selectedContext;
    private string _statusText = "Select two precisely scoped signals, define A and B, then Evaluate sequence.";
    private string _contextStatus = "";
    private bool _busy;
    private int _generation;
    private CancellationTokenSource? _cts;
    private RelayCommand? _openA, _openB, _openContext;

    public SignalTriggerSequenceViewModel()
    {
        EvaluateCommand = new AsyncCommand(EvaluateAsync, () => CanEvaluate && !IsBusy);
    }

    public event EventHandler<int>? FrameRequested;
    public AsyncCommand EvaluateCommand { get; }
    public IReadOnlyList<string> TriggerModes { get; } =
        ["Crosses above", "Crosses below", "Changes by more than",
         "Enters named DBC state", "Leaves named DBC state"];
    public IReadOnlyList<string> SequenceModes { get; } =
        ["A then B (strict observed time order)", "A and B within temporal tolerance"];

    public IReadOnlyList<SignalTriggerChoice> FirstChoices
    {
        get => _firstChoices;
        private set => SetProperty(ref _firstChoices, value);
    }
    public IReadOnlyList<SignalTriggerChoice> SecondChoices
    {
        get => _secondChoices;
        private set => SetProperty(ref _secondChoices, value);
    }
    public SignalTriggerChoice? FirstSignal
    {
        get => _first;
        set
        {
            if (!SetProperty(ref _first, value)) return;
            ChangedRule();
            EvaluateCommand.RaiseCanExecuteChanged();
        }
    }
    public SignalTriggerChoice? SecondSignal
    {
        get => _second;
        set
        {
            if (!SetProperty(ref _second, value)) return;
            ChangedRule();
            EvaluateCommand.RaiseCanExecuteChanged();
        }
    }
    public string FirstSearchText
    {
        get => _firstSearch;
        set
        {
            if (SetProperty(ref _firstSearch, value)) RefreshChoices();
        }
    }
    public string SecondSearchText
    {
        get => _secondSearch;
        set
        {
            if (SetProperty(ref _secondSearch, value)) RefreshChoices();
        }
    }
    public int FirstModeIndex
    {
        get => _firstModeIndex;
        set { if (SetProperty(ref _firstModeIndex, value)) ChangedRule(); }
    }
    public int SecondModeIndex
    {
        get => _secondModeIndex;
        set { if (SetProperty(ref _secondModeIndex, value)) ChangedRule(); }
    }
    public int SequenceModeIndex
    {
        get => _sequenceModeIndex;
        set { if (SetProperty(ref _sequenceModeIndex, value)) ChangedRule(); }
    }
    public string FirstThresholdText
    {
        get => _firstThresholdText;
        set { if (SetProperty(ref _firstThresholdText, value)) ChangedRule(); }
    }
    public string SecondThresholdText
    {
        get => _secondThresholdText;
        set { if (SetProperty(ref _secondThresholdText, value)) ChangedRule(); }
    }
    public string FirstStateText
    {
        get => _firstStateText;
        set { if (SetProperty(ref _firstStateText, value)) ChangedRule(); }
    }
    public string SecondStateText
    {
        get => _secondStateText;
        set { if (SetProperty(ref _secondStateText, value)) ChangedRule(); }
    }
    public string FirstToleranceText
    {
        get => _firstToleranceText;
        set { if (SetProperty(ref _firstToleranceText, value)) ChangedRule(); }
    }
    public string SecondToleranceText
    {
        get => _secondToleranceText;
        set { if (SetProperty(ref _secondToleranceText, value)) ChangedRule(); }
    }
    public string MinDelayMsText
    {
        get => _minDelayMsText;
        set { if (SetProperty(ref _minDelayMsText, value)) ChangedRule(); }
    }
    public string MaxDelayMsText
    {
        get => _maxDelayMsText;
        set { if (SetProperty(ref _maxDelayMsText, value)) ChangedRule(); }
    }
    public string PreContextMsText
    {
        get => _preContextMsText;
        set { if (SetProperty(ref _preContextMsText, value)) ChangedRule(); }
    }
    public string PostContextMsText
    {
        get => _postContextMsText;
        set { if (SetProperty(ref _postContextMsText, value)) ChangedRule(); }
    }
    public bool CanEvaluate => FirstSignal is not null && SecondSignal is not null;
    public bool IsBusy
    {
        get => _busy;
        private set
        {
            if (!SetProperty(ref _busy, value)) return;
            EvaluateCommand.RaiseCanExecuteChanged();
        }
    }
    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }
    public string ContextStatus
    {
        get => _contextStatus;
        private set => SetProperty(ref _contextStatus, value);
    }
    public IReadOnlyList<SignalTriggerSequenceEventRow> Results
    {
        get => _results;
        private set => SetProperty(ref _results, value);
    }
    public IReadOnlyList<TriggerContextFrameRow> ContextFrames
    {
        get => _contextFrames;
        private set => SetProperty(ref _contextFrames, value);
    }
    public SignalTriggerSequenceEventRow? SelectedEvent
    {
        get => _selectedEvent;
        set
        {
            if (!SetProperty(ref _selectedEvent, value)) return;
            OpenAFrameCommand.RaiseCanExecuteChanged();
            OpenBFrameCommand.RaiseCanExecuteChanged();
            RefreshContext();
        }
    }
    public TriggerContextFrameRow? SelectedContext
    {
        get => _selectedContext;
        set
        {
            if (!SetProperty(ref _selectedContext, value)) return;
            OpenContextFrameCommand.RaiseCanExecuteChanged();
        }
    }
    public RelayCommand OpenAFrameCommand => _openA ??=
        new RelayCommand(() =>
        {
            if (SelectedEvent is { Model.FirstSourceFrameIndex: >= 0 } item)
                FrameRequested?.Invoke(this, item.Model.FirstSourceFrameIndex);
        }, () => SelectedEvent is { Model.FirstSourceFrameIndex: >= 0 });
    public RelayCommand OpenBFrameCommand => _openB ??=
        new RelayCommand(() =>
        {
            if (SelectedEvent is { Model.SecondSourceFrameIndex: >= 0 } item)
                FrameRequested?.Invoke(this, item.Model.SecondSourceFrameIndex);
        }, () => SelectedEvent is { Model.SecondSourceFrameIndex: >= 0 });
    public RelayCommand OpenContextFrameCommand => _openContext ??=
        new RelayCommand(() =>
        {
            if (SelectedContext is { } item)
                FrameRequested?.Invoke(this, item.FrameIndex);
        }, () => SelectedContext is not null);

    public SignalTriggerSequenceReport? LastReport { get; private set; }

    public void SetSource(IReadOnlyList<SignalTimeline> timelines, IReadOnlyList<CanFrame> frames)
    {
        ArgumentNullException.ThrowIfNull(timelines);
        ArgumentNullException.ThrowIfNull(frames);
        Cancel();
        _timelines = timelines;
        _frames = frames;
        var oldA = FirstSignal?.Key;
        var oldB = SecondSignal?.Key;
        _allChoices = timelines.Where(item => item.Samples.Count >= 2)
            .Select(item => new SignalTriggerChoice(item))
            .OrderBy(item => item.Label, StringComparer.OrdinalIgnoreCase).ToArray();
        FirstSignal = null;
        SecondSignal = null;
        RefreshChoices();
        FirstSignal = _allChoices.SingleOrDefault(item => item.Key == oldA);
        SecondSignal = _allChoices.SingleOrDefault(item => item.Key == oldB);
        ClearResults("Source analysis changed. Re-evaluate sequence events against the current trace.");
    }

    private void RefreshChoices()
    {
        static IReadOnlyList<SignalTriggerChoice> Find(
            IReadOnlyList<SignalTriggerChoice> source, string query)
        {
            var terms = query.Trim().Split(' ',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return source.Where(item => terms.All(term =>
                item.Label.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                item.Key.Contains(term, StringComparison.OrdinalIgnoreCase)))
                .Take(MaxVisibleChoices).ToArray();
        }
        FirstChoices = Find(_allChoices, FirstSearchText);
        SecondChoices = Find(_allChoices, SecondSearchText);
        if (FirstSignal is not null && !FirstChoices.Contains(FirstSignal))
            FirstSignal = null;
        if (SecondSignal is not null && !SecondChoices.Contains(SecondSignal))
            SecondSignal = null;
    }

    public void Cancel()
    {
        _generation++;
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        IsBusy = false;
    }

    private void ChangedRule()
    {
        if (!IsBusy && LastReport is null && Results.Count == 0) return;
        Cancel();
        ClearResults("Sequence rule changed. Previous event evidence was invalidated; evaluate again.");
    }

    private void ClearResults(string status)
    {
        LastReport = null;
        SelectedEvent = null;
        SelectedContext = null;
        Results = [];
        ContextFrames = [];
        ContextStatus = "";
        StatusText = status;
    }

    private void RefreshContext()
    {
        SelectedContext = null;
        if (SelectedEvent is not { } current || _frames.Count == 0)
        {
            ContextFrames = [];
            ContextStatus = "";
            return;
        }
        try
        {
            var scope = TraceEventWindowIndex.Locate(_frames,
                current.Model.ContextStartNanoseconds,
                current.Model.ContextEndNanoseconds, maxFrames: 256);
            ContextFrames = scope.FrameIndices.Select(index =>
                new TriggerContextFrameRow(index, _frames[index])).ToArray();
            ContextStatus = $"{scope.TotalFramesInWindow:N0} source frames in context · " +
                $"{scope.OmittedFrameIndices:N0} not listed by limit" +
                (scope.Warning is null ? "" : " · " + scope.Warning);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException)
        {
            ContextFrames = [];
            ContextStatus = "Cannot resolve source context: " + ex.Message;
        }
    }

    private static double ReadNumeric(string text, string field)
    {
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) &&
            !double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value))
            throw new FormatException($"{field} must be numeric.");
        if (!double.IsFinite(value)) throw new FormatException($"{field} must be finite.");
        return value;
    }

    private static long ReadNonnegativeNanoseconds(string text, string field)
    {
        var ms = ReadNumeric(text, field);
        if (ms < 0 || ms > 86_400_000)
            throw new FormatException($"{field} must be between 0 and 86,400,000 ms.");
        return checked((long)Math.Round(ms * 1_000_000d));
    }

    private SignalTriggerRule BuildRule(SignalTriggerChoice choice,
        int modeIndex, string threshold, string tolerance, string state)
    {
        return new SignalTriggerRule(choice.Key, (SignalTriggerMode)modeIndex,
            ReadNumeric(threshold, "Trigger threshold"), state.Trim(),
            ReadNumeric(tolerance, "Trigger tolerance"));
    }

    private SignalTriggerSequenceRule BuildSequenceRule()
    {
        if (FirstSignal is not { } first || SecondSignal is not { } second)
            throw new ArgumentException("Select both source-scoped signals first.");
        var firstRule = BuildRule(first, FirstModeIndex, FirstThresholdText,
            FirstToleranceText, FirstStateText);
        var secondRule = BuildRule(second, SecondModeIndex, SecondThresholdText,
            SecondToleranceText, SecondStateText);
        var mode = (SignalTriggerSequenceMode)SequenceModeIndex;
        var minimum = mode == SignalTriggerSequenceMode.BothWithin ? 0 :
            ReadNonnegativeNanoseconds(MinDelayMsText, "Minimum delay");
        var rule = new SignalTriggerSequenceRule(firstRule, secondRule, mode,
            minimum, ReadNonnegativeNanoseconds(MaxDelayMsText, "Maximum delay"),
            ReadNonnegativeNanoseconds(PreContextMsText, "Pre-event context"),
            ReadNonnegativeNanoseconds(PostContextMsText, "Post-event context"));
        SignalTriggerSequenceAnalyzer.Validate(rule, maxPairs: 10_000);
        return rule;
    }

    private async Task EvaluateAsync()
    {
        SignalTriggerSequenceRule rule;
        try { rule = BuildSequenceRule(); }
        catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException)
        {
            ClearResults("Invalid sequence rule: " + ex.Message);
            return;
        }

        Cancel();
        var generation = _generation;
        using var cts = new CancellationTokenSource();
        _cts = cts;
        IsBusy = true;
        ClearResults("Evaluating source-backed A/B event sequence…");
        try
        {
            var report = await Task.Run(() =>
                SignalTriggerSequenceAnalyzer.Evaluate(_timelines, rule,
                    maxPairs: 10_000, cancellationToken: cts.Token), cts.Token);
            if (generation != _generation) return;
            LastReport = report;
            Results = report.Events.Select(item =>
                new SignalTriggerSequenceEventRow(item)).ToArray();
            StatusText = report.Summary +
                (report.FirstWarning is null ? "" : " · A: " + report.FirstWarning) +
                (report.SecondWarning is null ? "" : " · B: " + report.SecondWarning);
            SelectedEvent = Results.FirstOrDefault();
        }
        catch (OperationCanceledException)
        {
            if (generation == _generation)
                ClearResults("Sequence evaluation canceled.");
        }
        catch (Exception ex)
        {
            if (generation == _generation)
                ClearResults("Sequence evaluation failed: " + ex.Message);
        }
        finally
        {
            if (generation == _generation)
            {
                _cts = null;
                IsBusy = false;
            }
        }
    }
}
