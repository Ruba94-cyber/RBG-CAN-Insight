using System.Collections.ObjectModel;
using System.ComponentModel;
using RbgCanInsight.Analysis;
using RbgCanInsight.App.Common;

namespace RbgCanInsight.App.ViewModels;

public sealed record PaneChoice(InvestigationPane Pane, string Label);
public sealed record ArrangementChoice(InvestigationArrangement Arrangement, string Label);

/// <summary>One pane slot of the Investigation grid: what it shows and where it sits.</summary>
public sealed class InvestigationSlotViewModel : ObservableObject
{
    private InvestigationPane _kind;
    private bool _isVisible;
    private int _row, _column, _rowSpan = 1, _columnSpan = 1;

    public InvestigationSlotViewModel(int index) => Index = index;

    public int Index { get; }
    public string Name => $"Pane {Index + 1}";
    public InvestigationPane Kind { get => _kind; set => SetProperty(ref _kind, value); }
    public bool IsVisible { get => _isVisible; set => SetProperty(ref _isVisible, value); }
    public int Row { get => _row; set => SetProperty(ref _row, value); }
    public int Column { get => _column; set => SetProperty(ref _column, value); }
    public int RowSpan { get => _rowSpan; set => SetProperty(ref _rowSpan, value); }
    public int ColumnSpan { get => _columnSpan; set => SetProperty(ref _columnSpan, value); }
}

/// <summary>
/// Synchronized, saveable pane layouts: Trace, decoded signals, UDS transactions and findings side
/// by side on one shared cursor. Selecting a frame, transaction or finding in any pane moves the
/// others to the nearest item of their own kind; only the original source frames are referenced,
/// never copies. Synchronization only runs while this workspace is the active one, so other
/// workspaces keep their independent selections.
/// </summary>
public sealed class InvestigationViewModel : ObservableObject
{
    private readonly TraceViewModel _trace;
    private readonly UdsAnalysisViewModel _uds;
    private readonly Func<bool> _isActive;
    private readonly InvestigationLayoutStore _store;
    private bool _syncing;
    private InvestigationArrangement _arrangement = InvestigationArrangement.SideBySide;
    private InvestigationLayout? _selectedLayout;
    private string _saveName = string.Empty;
    private string _status = "Select a frame, transaction or finding: every pane follows the same cursor.";
    private bool _applyingLayout;
    private bool _advancedVisible;
    private RelayCommand? _saveCommand, _deleteCommand;

    public InvestigationViewModel(
        TraceViewModel trace, UdsAnalysisViewModel uds, Func<bool> isActive, InvestigationLayoutStore store)
    {
        _trace = trace;
        _uds = uds;
        _isActive = isActive;
        _store = store;
        PaneChoices = new[]
        {
            new PaneChoice(InvestigationPane.Trace, "Trace frames"),
            new PaneChoice(InvestigationPane.DecodedSignals, "Decoded signals at cursor"),
            new PaneChoice(InvestigationPane.ObservedSignals, "Observed signal explorer"),
            new PaneChoice(InvestigationPane.SignalPlot, "Main signal trend chart"),
            new PaneChoice(InvestigationPane.UdsTransactions, "UDS transactions"),
            new PaneChoice(InvestigationPane.Findings, "Offline findings"),
            new PaneChoice(InvestigationPane.Empty, "Empty")
        };
        ArrangementChoices = new[]
        {
            new ArrangementChoice(InvestigationArrangement.Single, "One pane"),
            new ArrangementChoice(InvestigationArrangement.SideBySide, "Two panes side by side"),
            new ArrangementChoice(InvestigationArrangement.Stacked, "Two panes stacked"),
            new ArrangementChoice(InvestigationArrangement.Quad, "Four panes")
        };
        for (var i = 0; i < InvestigationLayouts.SlotCount; i++)
        {
            var slot = new InvestigationSlotViewModel(i);
            slot.PropertyChanged += OnSlotChanged;
            Slots.Add(slot);
        }

        ReloadLayouts();
        // First visit: a minimal source-backed signal list beside the chart.
        // Existing saved layout names and old built-in indices remain unchanged.
        SelectedLayout = Layouts.First(layout => layout.Name == "Signals + trend");

        _trace.PropertyChanged += OnTraceChanged;
        _uds.PropertyChanged += OnUdsChanged;
    }

    /// <summary>Advanced pane arrangement and customization are hidden by
    /// default on a 13-inch screen; the actual recorded signal explorer and
    /// chart remain present. This is a UI preference, not a trace filter.</summary>
    public bool AdvancedVisible
    {
        get => _advancedVisible;
        set => SetProperty(ref _advancedVisible, value);
    }

    /// <summary>Always restore the simple two-pane view, including after a
    /// user manually edited pane slots while the same saved layout remained
    /// selected. Merely setting SelectedLayout to the same object would
    /// otherwise be a no-op and leave the view unexpectedly complex.</summary>
    public void ResetToSimple()
    {
        var layout = Layouts.First(item => item.Name == "Signals + trend");
        ApplyLayout(layout);
        _selectedLayout = layout;
        OnPropertyChanged(nameof(SelectedLayout));
        AdvancedVisible = false;
        Status = "Simple CAN signals and source-backed trend restored.";
    }

    public IReadOnlyList<PaneChoice> PaneChoices { get; }
    public IReadOnlyList<ArrangementChoice> ArrangementChoices { get; }
    public ObservableCollection<InvestigationSlotViewModel> Slots { get; } = [];
    public ObservableCollection<InvestigationLayout> Layouts { get; } = [];

    public TraceViewModel Trace => _trace;
    public UdsAnalysisViewModel Uds => _uds;

    public InvestigationArrangement Arrangement
    {
        get => _arrangement;
        set
        {
            if (SetProperty(ref _arrangement, value))
            {
                ArrangeSlots();
                OnPropertyChanged(nameof(ShowVerticalSplitter));
                OnPropertyChanged(nameof(ShowHorizontalSplitter));
                OnPropertyChanged(nameof(ArrangementChoice));
            }
        }
    }

    /// <summary>ComboBox binding target (the choice object for <see cref="Arrangement"/>).</summary>
    public ArrangementChoice ArrangementChoice
    {
        get => ArrangementChoices.First(choice => choice.Arrangement == _arrangement);
        set { if (value is not null) Arrangement = value.Arrangement; }
    }

    public bool ShowVerticalSplitter => _arrangement is InvestigationArrangement.SideBySide or InvestigationArrangement.Quad;
    public bool ShowHorizontalSplitter => _arrangement is InvestigationArrangement.Stacked or InvestigationArrangement.Quad;

    public InvestigationLayout? SelectedLayout
    {
        get => _selectedLayout;
        set
        {
            if (SetProperty(ref _selectedLayout, value) && value is not null)
            {
                ApplyLayout(value);
                SaveName = InvestigationLayouts.IsBuiltIn(value.Name) ? string.Empty : value.Name;
                Status = $"Layout \"{value.Name}\" applied.";
            }
        }
    }

    public string SaveName { get => _saveName; set => SetProperty(ref _saveName, value); }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }

    public RelayCommand SaveCommand => _saveCommand ??= new RelayCommand(() =>
    {
        var name = SaveName.Trim();
        if (name.Length == 0) { Status = "Enter a name to save this layout."; return; }
        if (InvestigationLayouts.IsBuiltIn(name)) { Status = $"\"{name}\" is a built-in layout; choose another name."; return; }
        _store.Save(CurrentLayout(name));
        ReloadLayouts();
        _selectedLayout = Layouts.FirstOrDefault(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        OnPropertyChanged(nameof(SelectedLayout));
        Status = $"Layout \"{name}\" saved.";
    });

    public RelayCommand DeleteCommand => _deleteCommand ??= new RelayCommand(() =>
    {
        if (SelectedLayout is not { } layout || InvestigationLayouts.IsBuiltIn(layout.Name))
        {
            Status = "Built-in layouts cannot be deleted.";
            return;
        }

        _store.Delete(layout.Name);
        ReloadLayouts();
        _selectedLayout = Layouts.First();
        OnPropertyChanged(nameof(SelectedLayout));
        Status = $"Layout \"{layout.Name}\" deleted.";
    });

    public InvestigationLayout CurrentLayout(string name) =>
        new(name, _arrangement, Slots.Select(slot => slot.Kind).ToArray());

    private void ReloadLayouts()
    {
        Layouts.Clear();
        foreach (var layout in InvestigationLayouts.BuiltIn) Layouts.Add(layout);
        foreach (var layout in _store.Load()) Layouts.Add(layout);
    }

    private void ApplyLayout(InvestigationLayout layout)
    {
        var normalized = InvestigationLayouts.Normalize(layout);
        _applyingLayout = true;
        try
        {
            for (var i = 0; i < Slots.Count; i++) Slots[i].Kind = normalized.Panes[i];
            Arrangement = normalized.Arrangement;
        }
        finally
        {
            _applyingLayout = false;
        }

        ArrangeSlots();
    }

    private void OnSlotChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_applyingLayout && e.PropertyName == nameof(InvestigationSlotViewModel.Kind))
        {
            Status = "Pane content changed. Save the layout to keep it.";
        }
    }

    private void ArrangeSlots()
    {
        void Place(int index, int row, int column, int rowSpan, int columnSpan, bool visible)
        {
            var slot = Slots[index];
            slot.Row = row;
            slot.Column = column;
            slot.RowSpan = rowSpan;
            slot.ColumnSpan = columnSpan;
            slot.IsVisible = visible;
        }

        switch (_arrangement)
        {
            case InvestigationArrangement.Single:
                Place(0, 0, 0, 3, 3, true);
                Place(1, 0, 0, 1, 1, false); Place(2, 0, 0, 1, 1, false); Place(3, 0, 0, 1, 1, false);
                break;
            case InvestigationArrangement.SideBySide:
                Place(0, 0, 0, 3, 1, true);
                Place(1, 0, 2, 3, 1, true);
                Place(2, 0, 0, 1, 1, false); Place(3, 0, 0, 1, 1, false);
                break;
            case InvestigationArrangement.Stacked:
                Place(0, 0, 0, 1, 3, true);
                Place(1, 2, 0, 1, 3, true);
                Place(2, 0, 0, 1, 1, false); Place(3, 0, 0, 1, 1, false);
                break;
            default:
                Place(0, 0, 0, 1, 1, true);
                Place(1, 0, 2, 1, 1, true);
                Place(2, 2, 0, 1, 1, true);
                Place(3, 2, 2, 1, 1, true);
                break;
        }
    }

    // ----- shared cursor ---------------------------------------------------------------------

    private static int FirstFrame(UdsTransactionRowViewModel row) =>
        row.Transaction.FrameIndices.Count == 0 ? int.MaxValue : row.Transaction.FrameIndices.Min();

    /// <summary>Moves every non-trace pane to the item nearest the given source frame.</summary>
    public void SyncToFrame(int frameIndex)
    {
        if (frameIndex < 0) return;
        _syncing = true;
        try
        {
            var rows = _uds.Rows;
            var transaction = CursorSync.NearestItem(rows, row => FirstFrame(row), frameIndex);
            if (transaction >= 0 && FirstFrame(rows[transaction]) != int.MaxValue)
                _uds.SelectedRow = rows[transaction];

            var findings = _uds.FindingRows.Where(row => row.FirstFrameIndex >= 0).ToList();
            var finding = CursorSync.NearestItem(findings, row => row.FirstFrameIndex, frameIndex);
            if (finding >= 0) _uds.SelectedFinding = findings[finding];
        }
        finally
        {
            _syncing = false;
        }
    }

    private void OnTraceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_syncing || !_isActive() || e.PropertyName != nameof(TraceViewModel.SelectedRow)) return;
        if (_trace.SelectedRow is { } row) SyncToFrame(row.FrameIndex);
    }

    private void OnUdsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_syncing || !_isActive()) return;
        int frame;
        if (e.PropertyName == nameof(UdsAnalysisViewModel.SelectedRow) && _uds.SelectedRow is { } transaction &&
            FirstFrame(transaction) != int.MaxValue)
            frame = FirstFrame(transaction);
        else if (e.PropertyName == nameof(UdsAnalysisViewModel.SelectedFinding) && _uds.SelectedFinding is { FirstFrameIndex: >= 0 } finding)
            frame = finding.FirstFrameIndex;
        else
            return;

        _syncing = true;
        try
        {
            _trace.JumpToFrameIndex(frame);
        }
        finally
        {
            _syncing = false;
        }

        // The trace moved; bring the pane that was not the source along too.
        SyncToFrame(frame);
    }
}
