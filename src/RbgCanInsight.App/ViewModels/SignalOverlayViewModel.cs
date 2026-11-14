using System.Collections.ObjectModel;
using System.Globalization;
using RbgCanInsight.Analysis;
using RbgCanInsight.App.Common;

namespace RbgCanInsight.App.ViewModels;

/// <summary>Timelines and time origins of the loaded trace and the reference trace it was compared with.</summary>
public sealed record SignalOverlayContext(
    IReadOnlyList<SignalTimeline> Subject,
    IReadOnlyList<SignalTimeline> Reference,
    long SubjectOriginNanoseconds,
    long ReferenceOriginNanoseconds,
    string ReferenceFileName);

public sealed class SignalOverlayChoice(SignalTimeline subject, SignalTimeline reference)
{
    public SignalTimeline Subject { get; } = subject;
    public SignalTimeline Reference { get; } = reference;
    public string Display =>
        $"{Subject.Message.Name}.{Subject.Signal.Name}" + (Subject.Channel >= 0 ? $" · CH{Subject.Channel + 1}" : string.Empty) +
        (Subject.Signal.Unit.Length > 0 ? $" [{Subject.Signal.Unit}]" : string.Empty);
}

public sealed class SignalOverlayRowViewModel(SignalOverlayDifference difference)
{
    public SignalOverlayDifference Difference { get; } = difference;
    public string TimeText => Difference.RelativeSeconds.ToString("F6", CultureInfo.InvariantCulture);
    public string ReferenceText => Difference.ReferenceValue.ToString("G9", CultureInfo.InvariantCulture);
    public string LoadedText => Difference.SubjectValue.ToString("G9", CultureInfo.InvariantCulture);
    public string DeltaText => Difference.Delta.ToString("+0.######;-0.######;0", CultureInfo.InvariantCulture);
    public string FrameText => (Difference.SubjectFrameIndex + 1).ToString(CultureInfo.InvariantCulture);
}

/// <summary>Overlay of one decoded signal: loaded trace versus the reference trace, aligned by trace start.</summary>
public sealed class SignalOverlayViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly SignalOverlayContext _context;
    private SignalOverlayChoice? _selectedSignal;
    private SignalOverlayResult? _result;
    private SignalOverlayRowViewModel? _selectedRow;
    private string _toleranceText = "0";
    private string _summary = string.Empty;
    private RelayCommand? _openFrame;

    public SignalOverlayViewModel(MainViewModel main, SignalOverlayContext context)
    {
        _main = main;
        _context = context;
        // The reference is a separately imported file, so its timelines carry no
        // logger index of the loaded workspace; match on the recording-independent
        // identity and only when exactly one reference series exists (never guess).
        var reference = context.Reference
            .Where(timeline => timeline.Samples.Count > 0)
            .GroupBy(timeline => timeline.CrossRecordingKey, StringComparer.Ordinal)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single(), StringComparer.Ordinal);
        foreach (var subject in context.Subject.Where(item => item.Samples.Count > 0))
            if (reference.TryGetValue(subject.CrossRecordingKey, out var match))
                Signals.Add(new SignalOverlayChoice(subject, match));
        Header = $"Loaded trace vs reference {context.ReferenceFileName}";
        Summary = Signals.Count == 0
            ? "No decoded signal exists in both traces (load a DBC that decodes them, then compare again)."
            : $"{Signals.Count:N0} signal(s) decoded in both traces. Pick one to overlay.";
        if (Signals.Count > 0) SelectedSignal = Signals[0];
    }

    public string Header { get; }
    public ObservableCollection<SignalOverlayChoice> Signals { get; } = [];
    public ObservableCollection<SignalOverlayRowViewModel> Differences { get; } = [];

    public SignalOverlayChoice? SelectedSignal
    {
        get => _selectedSignal;
        set { if (SetProperty(ref _selectedSignal, value)) Recompute(); }
    }

    public string ToleranceText
    {
        get => _toleranceText;
        set { if (SetProperty(ref _toleranceText, value)) Recompute(); }
    }

    public SignalOverlayResult? Result { get => _result; private set => SetProperty(ref _result, value); }
    public string Summary { get => _summary; private set => SetProperty(ref _summary, value); }
    public string Alignment => Result?.Alignment ?? string.Empty;

    public SignalOverlayRowViewModel? SelectedRow
    {
        get => _selectedRow;
        set { if (SetProperty(ref _selectedRow, value)) _openFrame?.RaiseCanExecuteChanged(); }
    }

    public RelayCommand OpenFrameCommand => _openFrame ??= new RelayCommand(OpenSelectedFrame, () => SelectedRow is not null);

    public void OpenSelectedFrame()
    {
        if (SelectedRow is { } row) _main.OpenTraceFrame(row.Difference.SubjectFrameIndex);
    }

    private void Recompute()
    {
        Differences.Clear();
        SelectedRow = null;
        if (SelectedSignal is not { } choice)
        {
            Result = null;
            OnPropertyChanged(nameof(Alignment));
            return;
        }
        if (!double.TryParse(ToleranceText, NumberStyles.Float, CultureInfo.InvariantCulture, out var tolerance) ||
            tolerance < 0 || double.IsNaN(tolerance))
        {
            Summary = "Tolerance must be a non-negative number (physical units of the signal).";
            return;
        }
        var result = SignalOverlayComparer.Compare(choice.Reference.Samples, choice.Subject.Samples,
            new OverlayOrigins(_context.ReferenceOriginNanoseconds, _context.SubjectOriginNanoseconds), tolerance);
        Result = result;
        foreach (var difference in result.LargestDifferences.Where(item => item.Delta != 0).Take(200))
            Differences.Add(new SignalOverlayRowViewModel(difference));
        Summary = result.Verdict switch
        {
            SignalOverlayVerdict.NoOverlap =>
                "No overlap: no loaded-trace sample has a recent enough reference sample at the same relative time.",
            SignalOverlayVerdict.Identical =>
                $"Identical over {result.ComparedSamples:N0} compared samples ({result.Coverage:P0} of the loaded trace).",
            SignalOverlayVerdict.WithinTolerance =>
                $"Within tolerance: max |Δ| {result.MaxAbsDelta:G6} over {result.ComparedSamples:N0} samples ({result.Coverage:P0}).",
            _ => $"Different: max |Δ| {result.MaxAbsDelta:G6}, mean |Δ| {result.MeanAbsDelta:G6}, RMS {result.RootMeanSquareDelta:G6}, " +
                 $"mean Δ {result.MeanDelta:+0.######;-0.######;0} over {result.ComparedSamples:N0} samples ({result.Coverage:P0} compared)."
        };
        OnPropertyChanged(nameof(Alignment));
    }
}
