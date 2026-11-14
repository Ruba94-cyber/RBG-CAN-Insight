using System.Globalization;
using RbgCanInsight.Analysis;
using RbgCanInsight.App.Common;

namespace RbgCanInsight.App.ViewModels;

/// <summary>Read-only source-backed named state transition, not an asserted ECU switch time.</summary>
public sealed class SignalStateTransitionRowViewModel(SignalStateTransition model)
{
    public SignalStateTransition Model { get; } = model;
    public string Key => Model.SignalKey;
    public string Message => Model.Message;
    public string Signal => Model.Signal;
    public string Before => Model.PreviousDeclaredState;
    public string After => Model.CurrentDeclaredState;
    public string TimeSeconds => (Model.FirstCurrentObservationNanoseconds / 1_000_000_000d)
        .ToString("F6", CultureInfo.InvariantCulture);
    public string GapMilliseconds => (Model.TransitionTimeUncertaintyNanoseconds / 1_000_000d)
        .ToString("F3", CultureInfo.InvariantCulture);
    public string ObservedMilliseconds => (Model.PreviousStateObservedSpanNanoseconds / 1_000_000d)
        .ToString("F3", CultureInfo.InvariantCulture);
    public int BeforeFrame => Model.PreviousSourceFrameIndex + 1;
    public int AfterFrame => Model.CurrentSourceFrameIndex + 1;
    public string Evidence => $"State '{Before}' observed at frame {BeforeFrame}; '{After}' first observed at frame {AfterFrame}. Exact switch instant unknown.";
}

public sealed class SignalStateTransitionsViewModel : ObservableObject
{
    private IReadOnlyList<SignalStateTransitionRowViewModel> _allRows = [];
    private IReadOnlyList<SignalStateTransitionRowViewModel> _rows = [];
    private SignalStateTransitionRowViewModel? _selected;
    private string _search = "";
    private long _total, _omitted;
    private int _unsupported;
    private RelayCommand? _openBefore, _openAfter;
    public event EventHandler<int>? FrameRequested;
    public IReadOnlyList<SignalStateTransitionRowViewModel> Rows
    {
        get => _rows;
        private set => SetProperty(ref _rows, value);
    }
    public SignalStateTransitionRowViewModel? SelectedRow
    {
        get => _selected;
        set
        {
            if (!SetProperty(ref _selected, value)) return;
            OpenBeforeFrameCommand.RaiseCanExecuteChanged();
            OpenAfterFrameCommand.RaiseCanExecuteChanged();
        }
    }
    public string SearchText
    {
        get => _search;
        set
        {
            if (SetProperty(ref _search, value))
                ApplyFilter();
        }
    }
    public string Summary => _total == 0
        ? "No recorded named-state changes. Load a trace with DBC VAL_ descriptions."
        : $"{_total:N0} observed transitions · {Rows.Count:N0} visible" +
          (_omitted > 0 ? $" · {_omitted:N0} omitted from bounded results" : "") +
          (_unsupported > 0 ? $" · {_unsupported:N0} ambiguous/unsupported signals" : "");
    public string Interpretation => "A state change is observed between two recorded frames, not at a proven ECU switch instant. Unknown codes break continuity; the measured gap is an uncertainty window.";
    public RelayCommand OpenBeforeFrameCommand => _openBefore ??= new RelayCommand(
        () =>
        {
            if (SelectedRow is { Model.PreviousSourceFrameIndex: >= 0 } row)
                FrameRequested?.Invoke(this, row.Model.PreviousSourceFrameIndex);
        }, () => SelectedRow is { Model.PreviousSourceFrameIndex: >= 0 });
    public RelayCommand OpenAfterFrameCommand => _openAfter ??= new RelayCommand(
        () =>
        {
            if (SelectedRow is { Model.CurrentSourceFrameIndex: >= 0 } row)
                FrameRequested?.Invoke(this, row.Model.CurrentSourceFrameIndex);
        }, () => SelectedRow is { Model.CurrentSourceFrameIndex: >= 0 });
    public void SetReport(SignalStateTransitionReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        SelectedRow = null;
        _allRows = report.Transitions.Select(x => new SignalStateTransitionRowViewModel(x)).ToArray();
        _total = report.TotalTransitions;
        _omitted = report.OmittedTransitions;
        _unsupported = report.UnsupportedOrAmbiguousSignals;
        ApplyFilter();
    }
    private void ApplyFilter()
    {
        var terms = SearchText.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Rows = terms.Length == 0 ? _allRows : _allRows.Where(row => terms.All(term =>
            row.Key.Contains(term, StringComparison.OrdinalIgnoreCase) ||
            row.Message.Contains(term, StringComparison.OrdinalIgnoreCase) ||
            row.Signal.Contains(term, StringComparison.OrdinalIgnoreCase) ||
            row.Before.Contains(term, StringComparison.OrdinalIgnoreCase) ||
            row.After.Contains(term, StringComparison.OrdinalIgnoreCase))).ToArray();
        if (SelectedRow is not null && !Rows.Contains(SelectedRow))
            SelectedRow = null;
        OnPropertyChanged(nameof(Summary));
    }
}
