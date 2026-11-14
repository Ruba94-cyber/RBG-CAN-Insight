using System.Collections.ObjectModel;
using System.Globalization;
using RbgCanInsight.Analysis;
using RbgCanInsight.App.Common;

namespace RbgCanInsight.App.ViewModels;

public sealed class DbcCycleTimingRowViewModel(DbcCycleTimingRow row)
{
    public DbcCycleTimingRow Model { get; } = row;
    public string Database => Model.Database;
    public string Message => Model.Message;
    public string Transmitter => Model.Transmitter;
    public string Identifier => Model.IdentifierText;
    public int Channel => Model.Channel + 1;
    public int NominalMs => Model.NominalCycleMs;
    public long Frames => Model.RecordedFrameCount;
    public string MeanMs => Model.RecordedFrameCount < 2 ? "—"
        : Model.AveragePeriodMs.ToString("F2", CultureInfo.InvariantCulture);
    public string MinMaxMs => Model.RecordedFrameCount < 2 ? "—"
        : $"{Model.MinPeriodMs:F2} – {Model.MaxPeriodMs:F2}";
    public string StdDevMs => Model.RecordedFrameCount < 3 ? "—"
        : Model.StandardDeviationMs.ToString("F3", CultureInfo.InvariantCulture);
    public string DeviationText => Model.RecordedFrameCount < 2 ? "—"
        : Model.AverageDeviationPercent.ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture) + " %";
    public int LateGaps => Model.LateGapCount;
    public int EarlyGaps => Model.EarlyGapCount;
    public string Status => Model.Status;
    public string Detail =>
        "Compared with DBC GenMsgCycleTime; not a validated OEM deadline. " +
        $"Worst gap {Model.MaxPeriodMs:F2} ms; {Model.PotentialMissingFrames} possible scheduled slots absent " +
        "(unverified hypothesis). " +
        (Model.NonMonotonicTimestampCount > 0
            ? $"{Model.NonMonotonicTimestampCount} out-of-order timestamp(s) excluded." : "");
}

public sealed class DbcCycleIncidentViewModel(DbcCycleTimingIncident incident)
{
    public DbcCycleTimingIncident Model { get; } = incident;
    public string Database => Model.Database;
    public string Message => Model.Message;
    public string Identifier => Model.IdentifierText;
    public int Channel => Model.Channel + 1;
    public double TimestampSeconds => Model.TimestampNanoseconds / 1_000_000_000d;
    public double GapMs => Model.ObservedGapMilliseconds;
    public int NominalMs => Model.NominalCycleMilliseconds;
    public string Ratio => Model.GapRatio.ToString("F2", CultureInfo.InvariantCulture) + "×";
    public int FrameNumber => Model.FrameIndex + 1;
    public string Note => Model.Note;
}

/// <summary>Self-contained view model for source-backed nominal DBC timing.
/// Consumers see hypotheses and the actual frame, not a certainty of lost packets.</summary>
public sealed class DbcCycleTimingViewModel : ObservableObject
{
    private DbcCycleIncidentViewModel? _selectedIncident;
    private RelayCommand? _openIncidentCommand;
    private string _summary = "Load a trace and a DBC declaring GenMsgCycleTime to analyze recorded cycles.";

    public event EventHandler<int>? FrameRequested;

    public ObservableCollection<DbcCycleTimingRowViewModel> Rows { get; } = [];
    public ObservableCollection<DbcCycleIncidentViewModel> Incidents { get; } = [];

    public string Summary
    {
        get => _summary;
        private set => SetProperty(ref _summary, value);
    }

    public string Interpretation =>
        "Nominal timing is taken from the DBC, not inferred from the CAN bus. " +
        "The default ±20% tolerance is a screening heuristic. Early intervals may " +
        "be event-triggered, long gaps may be pauses, and missing frames are not proven.";

    public DbcCycleIncidentViewModel? SelectedIncident
    {
        get => _selectedIncident;
        set
        {
            if (SetProperty(ref _selectedIncident, value))
                OpenIncidentCommand.RaiseCanExecuteChanged();
        }
    }

    public RelayCommand OpenIncidentCommand => _openIncidentCommand ??=
        new RelayCommand(() =>
        {
            if (SelectedIncident is { } selected)
                FrameRequested?.Invoke(this, selected.Model.FrameIndex);
        }, () => SelectedIncident is not null);

    public void SetResult(DbcCycleTimingResult result)
    {
        SelectedIncident = null;
        Rows.Clear();
        Incidents.Clear();
        foreach (var row in result.Rows)
            Rows.Add(new DbcCycleTimingRowViewModel(row));
        foreach (var incident in result.Incidents)
            Incidents.Add(new DbcCycleIncidentViewModel(incident));
        Summary = result.Summary;
    }
}
