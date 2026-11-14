using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using RbgCanInsight.Analysis;
using RbgCanInsight.App.Common;

namespace RbgCanInsight.App.ViewModels;

public sealed class SignalTriggerChoice(SignalTimeline timeline)
{
    public SignalTimeline Timeline { get; } = timeline;
    public string Key => Timeline.Key;
    public string Label => $"{Timeline.DatabaseName} · CAN {Timeline.Channel + 1} · " +
        $"{Timeline.Message.Name}.{Timeline.Signal.Name} ({Timeline.Signal.Unit}) · " +
        $"{Timeline.Samples.Count:N0} samples";
}

/// <summary>One measured pair of samples satisfying a user-authored offline
/// rule. Source frame indices refer to the currently loaded trace only.</summary>
public sealed class SignalTriggerEventRow(SignalTriggerEvent item)
{
    public SignalTriggerEvent Model { get; } = item;
    public int BeforeFrame => Model.PreviousFrameIndex + 1;
    public int AfterFrame => Model.CurrentFrameIndex + 1;
    public string TimeSeconds =>
        (Model.CurrentTimestampNanoseconds / 1_000_000_000d).ToString("F6", CultureInfo.InvariantCulture);
    public string BeforeValue => Model.PreviousPhysicalValue.ToString("G10", CultureInfo.InvariantCulture);
    public string AfterValue => Model.CurrentPhysicalValue.ToString("G10", CultureInfo.InvariantCulture);
    public string BeforeState => Model.PreviousNamedState ?? "—";
    public string AfterState => Model.CurrentNamedState ?? "—";
    public string ObservationGapMs =>
        (Model.UncertaintyNanoseconds / 1_000_000d).ToString("F3", CultureInfo.InvariantCulture);
}

/// <summary>
/// Workbench for one user-configured, source-backed signal trigger.
/// Evaluation is opt-in and off the WPF UI thread. Superseded analyses are
/// canceled; stale completions cannot overwrite results from a new trace.
/// </summary>
public sealed class SignalTriggerViewModel : ObservableObject
{
    private const int MaxVisibleSignalChoices = 1000;
    private IReadOnlyList<SignalTriggerChoice> _allSignals = [];
    private IReadOnlyList<SignalTriggerChoice> _visibleSignals = [];
    private IReadOnlyList<SignalTriggerEventRow> _events = [];
    private SignalTriggerChoice? _selectedSignal;
    private SignalTriggerEventRow? _selectedEvent;
    private string _signalSearchText = "";
    private int _modeIndex;
    private string _thresholdText = "0";
    private string _toleranceText = "0";
    private string _namedStateText = "";
    private string _separationMsText = "0";
    private bool _isBusy;
    private string _statusText = "Select a decoded signal, choose a rule and Evaluate. No trigger runs automatically.";
    private long _totalSignals;
    private int _generation;
    private CancellationTokenSource? _evaluationCancellation;
    private RelayCommand? _openBefore;
    private RelayCommand? _openAfter;
    private readonly SignalTriggerPresetStore _presetsStore = new(
        Path.Combine(AppStorage.ConfigDirectory, "signal-trigger-presets.json"));
    private IReadOnlyList<SignalTriggerPreset> _presets = [];
    private SignalTriggerPreset? _selectedPreset;
    private string _presetNameText = "";
    private RelayCommand? _savePresetCommand;
    private RelayCommand? _loadPresetCommand;
    private RelayCommand? _deletePresetCommand;

    public event EventHandler<int>? FrameRequested;

    public IReadOnlyList<SignalTriggerPreset> Presets
    {
        get => _presets;
        private set => SetProperty(ref _presets, value);
    }
    public SignalTriggerPreset? SelectedPreset
    {
        get => _selectedPreset;
        set
        {
            if (!SetProperty(ref _selectedPreset, value)) return;
            LoadPresetCommand.RaiseCanExecuteChanged();
            DeletePresetCommand.RaiseCanExecuteChanged();
        }
    }
    public string PresetNameText
    {
        get => _presetNameText;
        set
        {
            if (!SetProperty(ref _presetNameText, value)) return;
            SavePresetCommand.RaiseCanExecuteChanged();
        }
    }
    public RelayCommand SavePresetCommand => _savePresetCommand ??=
        new RelayCommand(SavePreset, () =>
            SelectedSignal is not null && !string.IsNullOrWhiteSpace(PresetNameText));
    public RelayCommand LoadPresetCommand => _loadPresetCommand ??=
        new RelayCommand(LoadPreset, () => SelectedPreset is not null);
    public RelayCommand DeletePresetCommand => _deletePresetCommand ??=
        new RelayCommand(DeletePreset, () => SelectedPreset is not null);

    public IReadOnlyList<string> ModeChoices { get; } =
        ["Crosses above", "Crosses below", "Changes by more than",
         "Enters named DBC state", "Leaves named DBC state"];

    public IReadOnlyList<SignalTriggerChoice> SignalChoices
    {
        get => _visibleSignals;
        private set => SetProperty(ref _visibleSignals, value);
    }
    public SignalTriggerChoice? SelectedSignal
    {
        get => _selectedSignal;
        set
        {
            if (SetProperty(ref _selectedSignal, value))
            {
                OnPropertyChanged(nameof(SelectedSignalIdentity));
                EvaluateCommand.RaiseCanExecuteChanged();
                SavePresetCommand.RaiseCanExecuteChanged();
                InvalidatePreviouslyEvaluatedRule();
            }
        }
    }
    public string SelectedSignalIdentity => SelectedSignal?.Key ??
        "Select an exact physical CAN channel and loaded database signal.";
    public string SignalSearchText
    {
        get => _signalSearchText;
        set
        {
            if (SetProperty(ref _signalSearchText, value)) RefreshSignalChoices();
        }
    }
    public string SignalSelectionSummary =>
        $"{_totalSignals:N0} decoded signal timelines · up to {MaxVisibleSignalChoices:N0} visible matches. " +
        "Type a specific ID, ECU, signal or database to narrow the list.";
    public int ModeIndex
    {
        get => _modeIndex;
        set
        {
            if (SetProperty(ref _modeIndex, Math.Clamp(value, 0, ModeChoices.Count - 1)))
                InvalidatePreviouslyEvaluatedRule();
        }
    }
    public string ThresholdText
    {
        get => _thresholdText;
        set
        {
            if (SetProperty(ref _thresholdText, value)) InvalidatePreviouslyEvaluatedRule();
        }
    }
    public string ToleranceText
    {
        get => _toleranceText;
        set
        {
            if (SetProperty(ref _toleranceText, value)) InvalidatePreviouslyEvaluatedRule();
        }
    }
    public string NamedStateText
    {
        get => _namedStateText;
        set
        {
            if (SetProperty(ref _namedStateText, value)) InvalidatePreviouslyEvaluatedRule();
        }
    }
    public string SeparationMsText
    {
        get => _separationMsText;
        set
        {
            if (SetProperty(ref _separationMsText, value)) InvalidatePreviouslyEvaluatedRule();
        }
    }
    public IReadOnlyList<SignalTriggerEventRow> Events
    {
        get => _events;
        private set => SetProperty(ref _events, value);
    }
    public SignalTriggerEventRow? SelectedEvent
    {
        get => _selectedEvent;
        set
        {
            if (!SetProperty(ref _selectedEvent, value)) return;
            OpenBeforeFrameCommand.RaiseCanExecuteChanged();
            OpenAfterFrameCommand.RaiseCanExecuteChanged();
        }
    }
    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value)) return;
            EvaluateCommand.RaiseCanExecuteChanged();
        }
    }
    public string Interpretation =>
        "Offline-only. Thresholds compare decoded physical samples; named states come solely " +
        "from source DBC raw-value descriptions. Every result links two observed frames. " +
        "The ECU's actual switch instant is unknown within their time gap.";

    public SignalTriggerReport? LastReport { get; private set; }
    public SignalTriggerRule? LastEvaluatedRule { get; private set; }
    public AsyncCommand EvaluateCommand { get; }
    public RelayCommand OpenBeforeFrameCommand => _openBefore ??=
        new RelayCommand(() =>
        {
            if (SelectedEvent is { Model.PreviousFrameIndex: >= 0 } row)
                FrameRequested?.Invoke(this, row.Model.PreviousFrameIndex);
        }, () => SelectedEvent is { Model.PreviousFrameIndex: >= 0 });
    public RelayCommand OpenAfterFrameCommand => _openAfter ??=
        new RelayCommand(() =>
        {
            if (SelectedEvent is { Model.CurrentFrameIndex: >= 0 } row)
                FrameRequested?.Invoke(this, row.Model.CurrentFrameIndex);
        }, () => SelectedEvent is { Model.CurrentFrameIndex: >= 0 });

    public SignalTriggerViewModel()
    {
        EvaluateCommand = new AsyncCommand(EvaluateAsync,
            () => SelectedSignal is not null && !IsBusy);
        try
        {
            Presets = _presetsStore.ReadAll();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                   JsonException or InvalidDataException or NotSupportedException or
                                   ArgumentException)
        {
            StatusText = "Saved trigger rules could not be loaded: " + ex.Message +
                ". The existing configuration was not overwritten.";
        }
    }

    private void SavePreset()
    {
        if (SelectedSignal is not { } choice) return;
        try
        {
            var rule = ReadRule(choice);
            Presets = _presetsStore.Save(PresetNameText, rule);
            SelectedPreset = Presets.First(item =>
                item.Name.Equals(PresetNameText.Trim(), StringComparison.OrdinalIgnoreCase));
            StatusText = $"Saved local trigger rule '{SelectedPreset.Name}'. No source files were changed.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                   JsonException or InvalidDataException or NotSupportedException or
                                   ArgumentException or InvalidOperationException or FormatException or
                                   OverflowException)
        {
            StatusText = "Could not save trigger rule: " + ex.Message;
        }
    }

    private void LoadPreset()
    {
        if (SelectedPreset is not { } selected) return;
        var choice = _allSignals.SingleOrDefault(item =>
            string.Equals(item.Key, selected.Rule.SignalKey, StringComparison.Ordinal));
        if (choice is null)
        {
            StatusText = "This preset refers to an exact CAN channel/database/signal not present " +
                "in the loaded trace. Load the matching sources before applying it.";
            return;
        }

        // Exact source-scoped identity only: never fall back to a homonymous
        // signal from another database or bus.
        SignalSearchText = choice.Key;
        SelectedSignal = choice;
        ModeIndex = (int)selected.Rule.Mode;
        ThresholdText = selected.Rule.Threshold.ToString("R", CultureInfo.InvariantCulture);
        ToleranceText = selected.Rule.ChangeTolerance.ToString("R", CultureInfo.InvariantCulture);
        NamedStateText = selected.Rule.NamedState ?? "";
        SeparationMsText = (selected.Rule.MinimumEventSeparationNanoseconds / 1_000_000d)
            .ToString("R", CultureInfo.InvariantCulture);
        PresetNameText = selected.Name;
        ClearResults("Loaded rule '" + selected.Name +
            "'. Evaluate it against the currently loaded trace to obtain fresh evidence.");
    }

    private void DeletePreset()
    {
        if (SelectedPreset is not { } selected) return;
        if (MessageBox.Show("Delete saved trigger rule '" + selected.Name +
                "'? This only changes local rule configuration, never the trace or DBC.",
                "Delete saved trigger rule", MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        try
        {
            if (_presetsStore.Remove(selected.Name))
            {
                Presets = _presetsStore.ReadAll();
                SelectedPreset = null;
                StatusText = "Removed saved trigger rule. Original trace and DBC are unchanged.";
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                   JsonException or InvalidDataException or NotSupportedException or
                                   ArgumentException)
        {
            StatusText = "Could not delete trigger rule: " + ex.Message;
        }
    }

    public void SetTimelines(IReadOnlyList<SignalTimeline> timelines)
    {
        ArgumentNullException.ThrowIfNull(timelines);
        CancelCurrent();
        // A reload invalidates all frame-index evidence, even if the human
        // readable message/signal name happens to be identical.
        var previouslySelectedKey = SelectedSignal?.Key;
        _allSignals = timelines.Where(series => series.Samples.Count > 1)
            .Select(series => new SignalTriggerChoice(series))
            .OrderBy(item => item.Label, StringComparer.OrdinalIgnoreCase).ToArray();
        _totalSignals = _allSignals.Count;
        RefreshSignalChoices();
        SelectedSignal = _allSignals.SingleOrDefault(item =>
            string.Equals(item.Key, previouslySelectedKey, StringComparison.Ordinal));
        ClearResults("Trace/database analysis updated. Evaluate a rule again for current source frames.");
        OnPropertyChanged(nameof(SignalSelectionSummary));
    }

    private void RefreshSignalChoices()
    {
        var text = SignalSearchText.Trim();
        var terms = text.Split(' ',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        SignalChoices = _allSignals.Where(item => terms.All(term =>
                item.Label.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                item.Key.Contains(term, StringComparison.OrdinalIgnoreCase)))
            .Take(MaxVisibleSignalChoices).ToArray();
        if (SelectedSignal is not null && !SignalChoices.Contains(SelectedSignal))
            SelectedSignal = null;
        OnPropertyChanged(nameof(SignalSelectionSummary));
    }

    public void CancelCurrent()
    {
        _generation++;
        _evaluationCancellation?.Cancel();
        _evaluationCancellation?.Dispose();
        _evaluationCancellation = null;
        IsBusy = false;
    }

    private void InvalidatePreviouslyEvaluatedRule()
    {
        if (LastReport is null && !IsBusy && Events.Count == 0) return;
        CancelCurrent();
        ClearResults("Rule or signal changed. Evaluate again; previous source-frame evidence is no longer applicable.");
    }

    private void ClearResults(string status)
    {
        LastReport = null;
        LastEvaluatedRule = null;
        SelectedEvent = null;
        Events = [];
        StatusText = status;
    }

    private static double ParseNumeric(string text, string name)
    {
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) &&
            !double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value))
            throw new FormatException($"{name} requires a valid number.");
        if (!double.IsFinite(value)) throw new FormatException($"{name} must be finite.");
        return value;
    }

    private SignalTriggerRule ReadRule(SignalTriggerChoice signal)
    {
        var threshold = ParseNumeric(ThresholdText, "Threshold");
        var tolerance = ParseNumeric(ToleranceText, "Change tolerance");
        var milliseconds = ParseNumeric(SeparationMsText, "Minimum event separation");
        if (tolerance < 0 || milliseconds < 0 || milliseconds > long.MaxValue / 1_000_000d)
            throw new FormatException("Tolerance and minimum event separation must be nonnegative and representable.");
        return new SignalTriggerRule(
            signal.Key, (SignalTriggerMode)ModeIndex, threshold, NamedStateText.Trim(),
            tolerance, checked((long)Math.Round(milliseconds * 1_000_000d)));
    }

    private async Task EvaluateAsync()
    {
        var signal = SelectedSignal;
        if (signal is null) return;
        SignalTriggerRule rule;
        try { rule = ReadRule(signal); }
        catch (Exception ex) when (ex is FormatException or OverflowException)
        {
            ClearResults("Cannot evaluate trigger: " + ex.Message);
            return;
        }

        CancelCurrent();
        var generation = _generation;
        using var cts = new CancellationTokenSource();
        _evaluationCancellation = cts;
        IsBusy = true;
        ClearResults("Evaluating recorded signal samples…");
        try
        {
            var report = await Task.Run(() =>
                SignalTriggerAnalyzer.Evaluate(signal.Timeline, rule,
                    cancellationToken: cts.Token), cts.Token);
            if (generation != _generation) return;
            LastReport = report;
            LastEvaluatedRule = rule;
            Events = report.Events.Select(item => new SignalTriggerEventRow(item)).ToArray();
            SelectedEvent = Events.FirstOrDefault();
            StatusText = report.Warning is null ? report.Summary :
                report.Summary + " · " + report.Warning;
        }
        catch (OperationCanceledException)
        {
            if (generation == _generation)
                ClearResults("Trigger evaluation canceled.");
        }
        catch (Exception exception)
        {
            if (generation == _generation)
                ClearResults("Trigger evaluation failed: " + exception.Message);
        }
        finally
        {
            if (generation == _generation)
            {
                _evaluationCancellation = null;
                IsBusy = false;
            }
        }
    }
}
