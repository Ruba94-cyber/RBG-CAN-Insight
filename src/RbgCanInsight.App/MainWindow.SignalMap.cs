using System.ComponentModel;
using System.Windows;
using RbgCanInsight.Analysis;
using RbgCanInsight.App.ViewModels;
using RbgCanInsight.App.Views;

namespace RbgCanInsight.App;

public partial class MainWindow
{
    /// <summary>
    /// No DTC and no preselected signal are required: the original CAN
    /// recording itself supplies a transparent bounded discovery pool.
    /// A map always contains ONE physical logger, never a synthetic timeline
    /// assembled from files with unverified clock alignment.
    /// </summary>
    private async void OnDiscoverSignalGroupsClick(object sender, RoutedEventArgs e)
    {
        var frames = _viewModel.Frames;
        var series = _viewModel.Signals.AllSeries;
        if (frames.Count == 0 || series.Count == 0)
        {
            _viewModel.StatusText = "Load a CAN trace and its matching DBC to discover signal groups.";
            return;
        }

        var originalLogger = _viewModel.Signals.SelectedLoggerIndex;
        if (originalLogger < 0)
        {
            // Explicit scope is preferred. Otherwise use the logger of the
            // selected signal; a first visit uses the most active real logger.
            originalLogger = _viewModel.Signals.SelectedSeries?.Timeline.OriginalSourceIndex ?? -1;
            if (originalLogger < 0)
                originalLogger = series
                    .Where(item => item.Timeline.OriginalSourceIndex >= 0)
                    .GroupBy(item => item.Timeline.OriginalSourceIndex)
                    .OrderByDescending(group => group.Count(item =>
                        item.Samples.Count >= 12 && item.ChangeCount >= 3))
                    .ThenBy(group => group.Key)
                    .Select(group => group.Key)
                    .FirstOrDefault(-1);
        }
        if (originalLogger < 0)
        {
            _viewModel.StatusText = "No uniquely identified physical CAN logger with decoded signals is available.";
            return;
        }

        var selectedLogger = originalLogger;
        var sourceIndex = _viewModel.CurrentTraceSourceIndex;
        // A missing source-map can only be replaced by a single-file
        // physical-source fallback. Never infer logger 0 for a merged
        // recording simply because one path happens to be populated.
        var singleKnown = !_viewModel.Signals.HasMultipleLoggers &&
            !string.IsNullOrWhiteSpace(_viewModel.CurrentTracePhysicalPath);
        Func<int, int> sourceAt = sourceIndex is not null
            ? sourceIndex.SourceAt
            : frameIndex => singleKnown && (uint)frameIndex < (uint)frames.Count ? 0 : -1;
        var sourceSeries = series.Where(item =>
            item.Timeline.OriginalSourceIndex == selectedLogger).ToArray();
        var operatorInterest = sourceSeries
            .Where(item => item.IsPlotted ||
                ReferenceEquals(item, _viewModel.Signals.SelectedSeries))
            .Select(item => item.Timeline.Key)
            .ToHashSet(StringComparer.Ordinal);
        if (sourceSeries.Length < 2)
        {
            _viewModel.StatusText = $"Logger {selectedLogger + 1}: fewer than two DBC-decoded original CAN signals.";
            return;
        }

        using var cancellation = new CancellationTokenSource();
        void CancelOnClosed(object? obj, EventArgs args) => cancellation.Cancel();
        void CancelOnCatalogChange(object? obj, PropertyChangedEventArgs args)
        {
            if (args.PropertyName == nameof(SignalsViewModel.AllSeries))
                cancellation.Cancel();
        }
        Closed += CancelOnClosed;
        _viewModel.Signals.PropertyChanged += CancelOnCatalogChange;

        _viewModel.StatusText = $"Discovering recorded relationships in Logger {selectedLogger + 1}… " +
            "Unverified CAN loggers are not combined.";
        try
        {
            var timelines = sourceSeries.Select(item => item.Timeline).ToArray();
            var map = await Task.Run(() => SignalRelationshipMapAnalyzer.Analyze(
                timelines, sourceAt, selectedLogger,
                cancellationToken: cancellation.Token,
                focusSignalKeys: operatorInterest), cancellation.Token);

            bool Current() =>
                !cancellation.IsCancellationRequested &&
                ReferenceEquals(frames, _viewModel.Frames) &&
                ReferenceEquals(series, _viewModel.Signals.AllSeries) &&
                _viewModel.Signals.AllSeries.Count == series.Count;
            if (!Current())
            {
                _viewModel.StatusText = "Signal map canceled: the physical recording or decoded catalog changed.";
                return;
            }

            var exact = sourceSeries
                .GroupBy(item => item.Timeline.Key, StringComparer.Ordinal)
                .Where(group => group.Count() == 1)
                .ToDictionary(group => group.Key, group => group.Single(),
                    StringComparer.Ordinal);

            void Add(PlotSeriesViewModel item)
            {
                if (!Current() || !sourceSeries.Any(recorded => ReferenceEquals(recorded, item)) ||
                    item.Timeline.OriginalSourceIndex != selectedLogger)
                {
                    _viewModel.StatusText = "Cannot plot a stale or cross-logger relationship candidate.";
                    return;
                }
                if (_viewModel.Signals.HasMultipleLoggers)
                    _viewModel.Signals.SelectedLoggerIndex = selectedLogger;
                item.IsPlotted = true;
                _viewModel.StatusText = $"Added {item.MessageName}.{item.SignalName} from Logger " +
                    $"{selectedLogger + 1} to the main chart, retaining existing curves.";
            }

            void Open(int index)
            {
                if (!Current() || (uint)index >= (uint)frames.Count ||
                    sourceAt(index) != selectedLogger)
                {
                    _viewModel.StatusText = "The original CAN frame is no longer source-verified.";
                    return;
                }
                _viewModel.OpenTraceFrame(index);
            }

            new SignalRelationshipMapWindow(this, map, exact, Current, Add, Open, sourceAt).Show();
            _viewModel.StatusText = map.Summary;
        }
        catch (OperationCanceledException)
        {
            _viewModel.StatusText = "CAN relationship map scan canceled: the source data changed.";
        }
        catch (Exception exception)
        {
            _viewModel.StatusText = "CAN relationship map unavailable: " + exception.Message;
        }
        finally
        {
            _viewModel.Signals.PropertyChanged -= CancelOnCatalogChange;
            Closed -= CancelOnClosed;
        }
    }
}
