using System.ComponentModel;
using System.Windows;
using RbgCanInsight.Analysis;
using RbgCanInsight.App.ViewModels;
using RbgCanInsight.App.Views;

namespace RbgCanInsight.App;

public partial class MainWindow
{
    private async void OnFindRelatedSignalsClick(object sender, RoutedEventArgs e)
    {
        var focus = _viewModel.Signals.SelectedSeries ??
            _viewModel.Signals.AllSeries.FirstOrDefault(series => series.IsPlotted);
        // First-time analysts can discover signals without knowing which
        // channel they need yet. Choosing the most-changing observed signal
        // is only an initial navigation seed, NOT a claim of importance.
        if (focus is null)
        {
            focus = _viewModel.Signals.AllSeries
                .Where(series => series.Timeline.OriginalSourceIndex >= 0 &&
                    series.SampleCount >= 12 && series.ChangeCount >= 3 &&
                    (_viewModel.Signals.SelectedLoggerIndex < 0 ||
                     series.Timeline.OriginalSourceIndex == _viewModel.Signals.SelectedLoggerIndex))
                .OrderByDescending(series => series.ChangeCount)
                .ThenBy(series => series.Timeline.Key, StringComparer.Ordinal)
                .FirstOrDefault();
            if (focus is not null) _viewModel.Signals.SelectedSeries = focus;
        }
        if (focus is null)
        {
            _viewModel.StatusText = "Select a decoded recorded signal in the left list, then choose Find related signals.";
            return;
        }
        if (focus.Timeline.OriginalSourceIndex < 0)
        {
            _viewModel.StatusText = "The selected signal has no verified original logger. " +
                "Load a physical CAN trace and its matching DBC first.";
            return;
        }

        var frames = _viewModel.Frames;
        var selectedSeries = _viewModel.Signals.AllSeries;
        if (frames.Count == 0 || !selectedSeries.Any(series => ReferenceEquals(series, focus)))
        {
            _viewModel.StatusText = "The selected signal is not in the current original CAN recording.";
            return;
        }
        if (focus.Samples.Count < 12)
        {
            _viewModel.StatusText = "This signal has fewer than 12 real CAN samples. " +
                "More recorded observations are needed to compare changes.";
            return;
        }

        var sourceIndex = _viewModel.CurrentTraceSourceIndex;
        // A missing source-map can only be replaced by a single-file
        // physical-source fallback. Never infer logger 0 for a merged
        // recording simply because one path happens to be populated.
        var singleKnown = !_viewModel.Signals.HasMultipleLoggers &&
            !string.IsNullOrWhiteSpace(_viewModel.CurrentTracePhysicalPath);
        Func<int, int> sourceAt = sourceIndex is not null
            ? sourceIndex.SourceAt
            : index => singleKnown && (uint)index < (uint)frames.Count ? 0 : -1;
        var scopeStart = Math.Max(0, focus.Samples[0].TimestampNanoseconds);
        var scopeEnd = Math.Max(scopeStart + 1, focus.Samples[^1].TimestampNanoseconds);
        var timelineSnapshot = selectedSeries.Select(series => series.Timeline).ToArray();

        using var cancellation = new CancellationTokenSource();
        void CancelOnClosed(object? s, EventArgs args) => cancellation.Cancel();
        void CancelOnNewSignals(object? s, PropertyChangedEventArgs args)
        {
            if (args.PropertyName == nameof(SignalsViewModel.AllSeries))
                cancellation.Cancel();
        }
        Closed += CancelOnClosed;
        _viewModel.Signals.PropertyChanged += CancelOnNewSignals;

        _viewModel.StatusText = "Finding CAN signals that move together or show a possible time shift " +
            $"in Logger {focus.Timeline.OriginalSourceIndex + 1}…";
        try
        {
            var report = await Task.Run(() => SignalRelationshipAnalyzer.Analyze(
                focus.Timeline, timelineSnapshot, sourceAt,
                scopeStart, scopeEnd, cancellationToken: cancellation.Token),
                cancellation.Token);

            bool EvidenceIsCurrent() =>
                !cancellation.IsCancellationRequested &&
                ReferenceEquals(_viewModel.Frames, frames) &&
                ReferenceEquals(_viewModel.Signals.AllSeries, selectedSeries) &&
                selectedSeries.Any(row => ReferenceEquals(row, focus));

            if (!EvidenceIsCurrent())
            {
                _viewModel.StatusText = "Signal relationship analysis canceled: recording or DBC source changed.";
                return;
            }

            var unique = selectedSeries
                .Where(item => item.Timeline.OriginalSourceIndex == focus.Timeline.OriginalSourceIndex)
                .GroupBy(item => item.Timeline.Key, StringComparer.Ordinal)
                .Where(group => group.Count() == 1)
                .ToDictionary(group => group.Key, group => group.Single(), StringComparer.Ordinal);

            void Add(PlotSeriesViewModel series)
            {
                if (!EvidenceIsCurrent() ||
                    !selectedSeries.Any(item => ReferenceEquals(item, series)) ||
                    series.Timeline.OriginalSourceIndex != focus.Timeline.OriginalSourceIndex)
                {
                    _viewModel.StatusText = "Source changed or signal logger mismatch; re-run the analysis.";
                    return;
                }
                if (_viewModel.Signals.HasMultipleLoggers)
                    _viewModel.Signals.SelectedLoggerIndex = focus.Timeline.OriginalSourceIndex;
                series.IsPlotted = true;
                _viewModel.StatusText = $"Added {series.MessageName}.{series.SignalName} to the main plot " +
                    "without replacing existing curves. Association is not influence or causality.";
            }

            void Open(int originalFrameIndex)
            {
                if (!EvidenceIsCurrent() || (uint)originalFrameIndex >= (uint)frames.Count ||
                    sourceAt(originalFrameIndex) != focus.Timeline.OriginalSourceIndex)
                {
                    _viewModel.StatusText = "Original frame source is no longer verified in this recording.";
                    return;
                }
                _viewModel.OpenTraceFrame(originalFrameIndex);
            }

            new SignalRelationshipsWindow(this, report, focus, unique,
                EvidenceIsCurrent, Add, Open).Show();
            _viewModel.StatusText = report.Summary;
        }
        catch (OperationCanceledException)
        {
            _viewModel.StatusText = "Relationship scan canceled because the trace or DBC changed.";
        }
        catch (Exception ex)
        {
            _viewModel.StatusText = "Signal relationship scan not available: " + ex.Message;
        }
        finally
        {
            _viewModel.Signals.PropertyChanged -= CancelOnNewSignals;
            Closed -= CancelOnClosed;
        }
    }

    private void OnOpenSimpleSignalViewClick(object sender, RoutedEventArgs e)
    {
        _viewModel.Investigation.ResetToSimple();
        _viewModel.SelectedNavIndex = MainViewModel.InvestigationNavIndex;
    }
}
