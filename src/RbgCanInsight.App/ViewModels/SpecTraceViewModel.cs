using System.Collections.ObjectModel;
using System.Globalization;
using RbgCanInsight.App.Common;
using RbgCanInsight.Diagnostics;

namespace RbgCanInsight.App.ViewModels;

public sealed class SpecTraceRowViewModel(SpecTraceFinding finding)
{
    public SpecTraceFinding Finding { get; } = finding;
    public string KindText => Finding.Kind switch
    {
        SpecTraceFindingKind.ObservedDidNotDeclared => "DID used, not in catalog",
        SpecTraceFindingKind.DeclaredDidNotObserved => "DID in catalog, not observed",
        SpecTraceFindingKind.ObservedRoutineNotDeclared => "Routine used, not in catalog",
        SpecTraceFindingKind.DeclaredRoutineNotObserved => "Routine in catalog, not observed",
        SpecTraceFindingKind.ObservedDtcNotDeclared => "DTC reported, not in catalog",
        SpecTraceFindingKind.NegativeResponse => "Negative response",
        SpecTraceFindingKind.NoResponse => "No response",
        SpecTraceFindingKind.RoutinePrerequisiteNotMet => "Routine prerequisite not met",
        _ => "Routine prerequisite unverifiable"
    };
    public string Identifier => Finding.Identifier;
    public string Description => Finding.Description;
    public string CountText => Finding.Count == 0 ? "—" : Finding.Count.ToString(CultureInfo.InvariantCulture);
    public string FramesText => Finding.FrameIndices.Count == 0 ? "—" :
        string.Join(' ', Finding.FrameIndices.Take(6).Select(frame => (frame + 1).ToString(CultureInfo.InvariantCulture))) +
        (Finding.FrameIndices.Count > 6 ? " …" : string.Empty);
    public string Caveat => Finding.Caveat;
    public int? FirstFrameIndex => Finding.FrameIndices.Count == 0 ? null : Finding.FrameIndices[0];
}

/// <summary>Declared-vs-observed diagnostic check: catalogs (CDD/ODX) against the recorded UDS
/// traffic. Reports gaps only; absence in a finite capture or in a partial catalog is not a defect.</summary>
public sealed class SpecTraceViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private string _summary = string.Empty;
    private SpecTraceRowViewModel? _selected;
    private RelayCommand? _openFrame;
    private RelayCommand? _refresh;

    public SpecTraceViewModel(MainViewModel main)
    {
        _main = main;
        Refresh();
    }

    public ObservableCollection<SpecTraceRowViewModel> Rows { get; } = [];
    public string Summary { get => _summary; private set => SetProperty(ref _summary, value); }
    public string Limitations { get; private set; } = DiagnosticSpecTraceChecker.StandardLimitations;
    public bool HasData { get; private set; }

    public SpecTraceRowViewModel? SelectedRow
    {
        get => _selected;
        set { if (SetProperty(ref _selected, value)) _openFrame?.RaiseCanExecuteChanged(); }
    }

    public RelayCommand RefreshCommand => _refresh ??= new RelayCommand(Refresh);

    public RelayCommand OpenFrameCommand => _openFrame ??= new RelayCommand(OpenSelectedFrame,
        () => SelectedRow?.FirstFrameIndex is not null);

    public void OpenSelectedFrame()
    {
        if (SelectedRow?.FirstFrameIndex is int frame) _main.OpenTraceFrame(frame);
    }

    public void Refresh()
    {
        Rows.Clear();
        var transactions = _main.Uds.AllRows.Select(row => row.Transaction).ToArray();
        var catalogs = _main.Mapping.Catalogs;
        HasData = transactions.Length > 0 || catalogs.Count > 0;
        if (!HasData)
        {
            Summary = "Nothing to compare yet: load a CDD/ODX catalog and a trace with diagnostic traffic.";
        }
        else
        {
            var report = DiagnosticSpecTraceChecker.Check(transactions, catalogs);
            foreach (var finding in report.Findings) Rows.Add(new SpecTraceRowViewModel(finding));
            Limitations = report.Limitations;
            Summary = $"{report.TransactionsAnalyzed:N0} diagnostic transactions · " +
                      $"{report.DeclaredDidCount:N0} DIDs declared / {report.ObservedDidCount:N0} observed · " +
                      $"{report.DeclaredRoutineCount:N0} routines declared / {report.ObservedRoutineCount:N0} observed · " +
                      $"{report.Findings.Count:N0} finding(s)";
        }
        SelectedRow = null;
        OnPropertyChanged(nameof(HasData));
        OnPropertyChanged(nameof(Limitations));
    }
}
