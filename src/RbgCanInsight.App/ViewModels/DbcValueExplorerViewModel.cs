using RbgCanInsight.Analysis;
using RbgCanInsight.App.Common;
using RbgCanInsight.Dbc;
using RbgCanInsight.Domain;

namespace RbgCanInsight.App.ViewModels;

public sealed class DbcValueExplorerChoice(SignalTimeline source)
{
    public SignalTimeline Timeline { get; } = source;
    public string Display => $"CAN {Timeline.Channel + 1} · {Timeline.Samples.Count:N0} decoded observations";
}

/// <summary>
/// A source-backed, on-demand DBC raw-value analyzer. The draft DBC editor
/// never changes the decoder providing these observations; selecting another
/// signal or trace cancels and invalidates any previous frame links.
/// </summary>
public sealed class DbcValueExplorerViewModel : ObservableObject
{
    private IReadOnlyList<CanFrame> _frames = [];
    private IReadOnlyList<DbcValueExplorerChoice> _choices = [];
    private DbcValueExplorerChoice? _selectedChoice;
    private DbcObservedRawCode? _selectedRawCode;
    private IReadOnlyList<DbcObservedRawCode> _rawCodes = [];
    private DbcValueExplorerReport? _report;
    private string _summary = "Choose a DBC signal and one recorded CAN channel, then Analyze.";
    private string _declaredRange = "";
    private string _rawRange = "";
    private string _warning = "";
    private bool _isBusy;
    private int _generation;
    private CancellationTokenSource? _pending;
    private RelayCommand? _openFirst;
    private RelayCommand? _openLast;

    public DbcValueExplorerViewModel()
    {
        AnalyzeCommand = new AsyncCommand(AnalyzeAsync,
            () => SelectedChoice is not null && !IsBusy);
    }

    public event EventHandler<int>? FrameRequested;
    public AsyncCommand AnalyzeCommand { get; }

    public IReadOnlyList<DbcValueExplorerChoice> ChannelChoices
    {
        get => _choices;
        private set => SetProperty(ref _choices, value);
    }
    public DbcValueExplorerChoice? SelectedChoice
    {
        get => _selectedChoice;
        set
        {
            if (!SetProperty(ref _selectedChoice, value)) return;
            Invalidate();
            AnalyzeCommand.RaiseCanExecuteChanged();
        }
    }
    public DbcObservedRawCode? SelectedRawCode
    {
        get => _selectedRawCode;
        set
        {
            if (!SetProperty(ref _selectedRawCode, value)) return;
            OpenFirstFrameCommand.RaiseCanExecuteChanged();
            OpenLastFrameCommand.RaiseCanExecuteChanged();
        }
    }
    public IReadOnlyList<DbcObservedRawCode> RawCodes
    {
        get => _rawCodes;
        private set => SetProperty(ref _rawCodes, value);
    }
    public string Summary
    {
        get => _summary;
        private set => SetProperty(ref _summary, value);
    }
    public string RawDomain
    {
        get => _rawRange;
        private set => SetProperty(ref _rawRange, value);
    }
    public string PhysicalRange
    {
        get => _declaredRange;
        private set => SetProperty(ref _declaredRange, value);
    }
    public string Warning
    {
        get => _warning;
        private set => SetProperty(ref _warning, value);
    }
    public string Interpretation =>
        "DBC-declared meanings (VAL_) are not observed values. Unknown codes are not " +
        "automatically reserved or faults. Raw values are reconstructed exactly from source " +
        "bits (including 64-bit integers); floating codes are shown as IEEE-754 patterns. " +
        "Counts include only successfully decoded/active multiplex branches. The output " +
        "retains at most 512 distinct raw codes, with omissions stated explicitly.";
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value)) return;
            AnalyzeCommand.RaiseCanExecuteChanged();
        }
    }
    public DbcValueExplorerReport? LastReport => _report;
    public RelayCommand OpenFirstFrameCommand => _openFirst ??=
        new RelayCommand(() =>
        {
            if (SelectedRawCode is { FirstSourceFrameIndex: >= 0 } row)
                FrameRequested?.Invoke(this, row.FirstSourceFrameIndex);
        }, () => SelectedRawCode is { FirstSourceFrameIndex: >= 0 });
    public RelayCommand OpenLastFrameCommand => _openLast ??=
        new RelayCommand(() =>
        {
            if (SelectedRawCode is { LastSourceFrameIndex: >= 0 } row)
                FrameRequested?.Invoke(this, row.LastSourceFrameIndex);
        }, () => SelectedRawCode is { LastSourceFrameIndex: >= 0 });

    public void SetSource(DbcDatabase? database, DbcMessage? message,
        DbcSignal? signal, IReadOnlyList<SignalTimeline> timelines,
        IReadOnlyList<CanFrame> frames)
    {
        ArgumentNullException.ThrowIfNull(timelines);
        ArgumentNullException.ThrowIfNull(frames);
        CancelCurrent();
        _frames = frames;
        var previous = SelectedChoice?.Timeline.Key;
        // Never merge a homonymous signal from another DBC, CAN channel or
        // non-identical 11/29-bit arbitration identity.
        ChannelChoices = database is null || message is null || signal is null
            ? []
            : timelines.Where(item =>
                ReferenceEquals(item.Message, message) &&
                ReferenceEquals(item.Signal, signal) &&
                string.Equals(item.DatabaseName, database.Name, StringComparison.Ordinal))
                .OrderBy(item => item.Channel)
                .Select(item => new DbcValueExplorerChoice(item)).ToArray();
        SelectedChoice = ChannelChoices.FirstOrDefault(item =>
            item.Timeline.Key == previous);
        if (SelectedChoice is null && ChannelChoices.Count == 1)
            SelectedChoice = ChannelChoices[0];
        ClearResults(ChannelChoices.Count == 0
            ? "No decoded observations for the selected DBC signal. Check source frames, active bus and mux conditions."
            : ChannelChoices.Count == 1
                ? "One recorded CAN channel. Select Analyze to inspect exact raw code frequencies."
                : "Multiple CAN channels found. Select the physical bus before analyzing.");
    }

    public void CancelCurrent()
    {
        _generation++;
        _pending?.Cancel();
        _pending?.Dispose();
        _pending = null;
        IsBusy = false;
    }
    private void Invalidate()
    {
        CancelCurrent();
        ClearResults("Selected signal/channel changed. Previous recorded-code evidence was invalidated.");
    }
    private void ClearResults(string status)
    {
        _report = null;
        SelectedRawCode = null;
        RawCodes = [];
        Summary = status;
        RawDomain = "";
        PhysicalRange = "";
        Warning = "";
    }

    private async Task AnalyzeAsync()
    {
        var selected = SelectedChoice;
        if (selected is null) return;
        CancelCurrent();
        var generation = _generation;
        using var cts = new CancellationTokenSource();
        _pending = cts;
        IsBusy = true;
        ClearResults("Inspecting exact raw codes from previously decoded source frames…");
        // Freeze current physical source before leaving UI thread.
        var frames = _frames;
        try
        {
            var report = await Task.Run(() =>
                DbcValueExplorerAnalyzer.Analyze(selected.Timeline, frames,
                    cancellationToken: cts.Token), cts.Token);
            if (generation != _generation) return;
            _report = report;
            RawCodes = report.RawCodes;
            Summary = report.Summary;
            RawDomain = report.RawDomain;
            PhysicalRange = report.DeclaredPhysicalRange;
            Warning = report.Warning ?? "";
            SelectedRawCode = report.RawCodes.FirstOrDefault();
        }
        catch (OperationCanceledException)
        {
            if (generation == _generation)
                ClearResults("Raw-code analysis canceled.");
        }
        catch (Exception ex)
        {
            if (generation == _generation)
                ClearResults("Raw-code analysis failed: " + ex.Message);
        }
        finally
        {
            if (generation == _generation)
            {
                _pending = null;
                IsBusy = false;
            }
        }
    }
}
