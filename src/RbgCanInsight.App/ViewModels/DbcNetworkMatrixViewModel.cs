using RbgCanInsight.Analysis;
using RbgCanInsight.App.Common;

namespace RbgCanInsight.App.ViewModels;

/// <summary>Read-only declared DBC network roles versus recorded CAN identities.</summary>
public sealed class DbcNetworkMatrixViewModel : ObservableObject
{
    private IReadOnlyList<DbcNetworkMatrixRow> _allRows = [];
    private IReadOnlyList<DbcNetworkMatrixRow> _rows = [];
    private IReadOnlyList<DbcNetworkMatrixIssue> _issues = [];
    private DbcNetworkMatrixRow? _selectedRow;
    private string _search = "";
    private string _summary = "Load a DBC and optionally a trace to inspect declared/observed network roles.";
    private RelayCommand? _inspectCommand;

    public event EventHandler<DbcNetworkMatrixRow>? InspectDbcRequested;

    public IReadOnlyList<DbcNetworkMatrixRow> Rows
    {
        get => _rows;
        private set => SetProperty(ref _rows, value);
    }
    public IReadOnlyList<DbcNetworkMatrixIssue> Issues
    {
        get => _issues;
        private set => SetProperty(ref _issues, value);
    }
    public DbcNetworkMatrixRow? SelectedRow
    {
        get => _selectedRow;
        set
        {
            if (SetProperty(ref _selectedRow, value))
                InspectInDbcCommand.RaiseCanExecuteChanged();
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
    public string Summary
    {
        get => _summary;
        private set => SetProperty(ref _summary, value);
    }

    public string Interpretation =>
        "TX/RX are declared in DBC/ARXML, never inferred from CAN frames. A single " +
        "configured decoder can uniquely match a frame without proving the physical ECU. " +
        "Unmatched and multiply claimed frames remain separate, with explicit warnings; " +
        "29-bit PGN is only a possible J1939 interpretation.";

    public RelayCommand InspectInDbcCommand => _inspectCommand ??=
        new RelayCommand(() =>
        {
            if (SelectedRow is { } row)
                InspectDbcRequested?.Invoke(this, row);
        }, () => SelectedRow is not null);

    public void SetReport(DbcNetworkMatrixReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        SelectedRow = null;
        _allRows = report.Rows;
        Issues = report.Issues;
        Summary = report.Summary;
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var terms = SearchText.Split(' ',
            StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        Rows = terms.Length == 0 ? _allRows : _allRows.Where(row => terms.All(term =>
            row.Database.Contains(term, StringComparison.OrdinalIgnoreCase) ||
            row.Message.Contains(term, StringComparison.OrdinalIgnoreCase) ||
            row.DeclaredTransmitter.Contains(term, StringComparison.OrdinalIgnoreCase) ||
            row.DeclaredReceivers.Contains(term, StringComparison.OrdinalIgnoreCase) ||
            row.IdentifierText.Contains(term, StringComparison.OrdinalIgnoreCase) ||
            row.ChannelText.Contains(term, StringComparison.OrdinalIgnoreCase) ||
            row.Evidence.Contains(term, StringComparison.OrdinalIgnoreCase))).ToArray();
        if (SelectedRow is not null && !Rows.Contains(SelectedRow))
            SelectedRow = null;
    }
}
