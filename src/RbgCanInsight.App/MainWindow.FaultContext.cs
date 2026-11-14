using System.ComponentModel;
using System.Windows;
using RbgCanInsight.Analysis;
using RbgCanInsight.Correlation;
using RbgCanInsight.App.ViewModels;
using RbgCanInsight.App.Views;

namespace RbgCanInsight.App;

public partial class MainWindow
{
    /// <summary>From UDS fault memory, navigate to the matching original DTC
    /// lifecycle in Data Studio. Do not choose an unverified fault event by
    /// matching its code alone across physical ECUs/loggers.</summary>
    private void OnOpenDtcConditionsFromUdsClick(object sender, RoutedEventArgs e)
    {
        var fault = _viewModel.Uds.SelectedFault;
        if (fault is null)
        {
            _viewModel.StatusText = "Select a recorded DTC from the UDS fault memory first.";
            return;
        }
        var studio = _viewModel.DataStudio;
        studio.DiagnosticSourceIndex = 0;
        studio.DtcSearchText = string.Empty;
        studio.DtcStateFilterIndex = 0;
        var exact = studio.DtcRows.FirstOrDefault(row => ReferenceEquals(row.Fault, fault));
        if (exact is null)
        {
            _viewModel.StatusText = "Selected DTC is no longer in the current original-source Data Studio analysis.";
            return;
        }
        studio.SelectedDtc = exact;
        studio.SectionIndex = DataStudioViewModel.CompareSection;
        _viewModel.SelectedNavIndex = MainViewModel.DataStudioNavIndex;
        _viewModel.StatusText = "Select the recorded lifecycle event, then choose Investigate fault conditions. " +
            "A 0x19 read time is not the proven physical onset.";
    }

    private void OnOpenDtcConditionsFromDashboardClick(object sender, RoutedEventArgs e)
    {
        _viewModel.DataStudio.SectionIndex = DataStudioViewModel.CompareSection;
        _viewModel.SelectedNavIndex = MainViewModel.DataStudioNavIndex;
        _viewModel.StatusText = "Choose a recorded DTC and exact event in Data Studio, then Investigate fault conditions.";
    }

    /// <summary>
    /// Enter a source-verified fault analysis from the exact DTC event selected
    /// in Data Studio. A new capture/DBC reload cancels the scan; no mismatched
    /// historical frame can silently become evidence for the new recording.
    /// </summary>
    public async void OpenFaultContextFromDataStudio(long halfWindowNanoseconds = 5_000_000_000L)
    {
        var chosen = _viewModel.DataStudio.SelectedDtc;
        var selectedEvent = _viewModel.DataStudio.SelectedDtcTransition;
        if (chosen?.Fault is not { } fault || selectedEvent is null)
        {
            _viewModel.StatusText = "Select a DTC history and its recorded status/snapshot event in Data Studio.";
            return;
        }

        var frames = _viewModel.Frames;
        var selectedSeries = _viewModel.Signals.AllSeries;
        // Data Studio already applies DtcSnapshotCanLinker to this exact
        // selected event. Capture its vetted navigation leads, never borrow
        // another DTC event's latest snapshot or generic DID-name matches.
        var snapshotLinks = _viewModel.DataStudio.DtcCanCandidates.ToArray();
        if (frames.Count == 0 ||
            !_viewModel.Uds.IsAnalysisForFrames(frames) ||
            !_viewModel.Uds.FaultRows.Any(row => ReferenceEquals(row, fault)))
        {
            _viewModel.StatusText = "DTC evidence is stale. Refresh the loaded trace before investigating.";
            return;
        }

        // Capture the source-index object and immutable frame collection once;
        // never consult a later merged logger map during a background scan.
        var sourceIndex = _viewModel.CurrentTraceSourceIndex;
        var singleKnown = !string.IsNullOrWhiteSpace(_viewModel.CurrentTracePhysicalPath);
        Func<int, int> sourceAt = sourceIndex is not null
            ? sourceIndex.SourceAt
            : index => singleKnown && (uint)index < (uint)frames.Count ? 0 : -1;

        using var cancellation = new CancellationTokenSource();
        void CancelOnClosed(object? sender, EventArgs args) => cancellation.Cancel();
        void CancelOnNewCatalog(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(SignalsViewModel.AllSeries))
                cancellation.Cancel();
        }
        Closed += CancelOnClosed;
        _viewModel.Signals.PropertyChanged += CancelOnNewCatalog;

        _viewModel.StatusText = $"Inspecting recorded conditions for {fault.CodeText} at " +
            $"{selectedEvent.TimestampNanoseconds / 1e9:F6}s (same original physical logger)…";

        try
        {
            var context = await Task.Run(() => FaultEventContextAnalyzer.Build(
                fault, selectedEvent, selectedSeries.Select(series => series.Timeline).ToArray(),
                sourceAt,
                beforeNanoseconds: halfWindowNanoseconds,
                afterNanoseconds: halfWindowNanoseconds,
                cancellationToken: cancellation.Token), cancellation.Token);

            bool EvidenceIsCurrent() =>
                ReferenceEquals(_viewModel.Frames, frames) &&
                ReferenceEquals(_viewModel.Signals.AllSeries, selectedSeries) &&
                _viewModel.Uds.IsAnalysisForFrames(frames) &&
                _viewModel.Uds.FaultRows.Any(row => ReferenceEquals(row, fault)) &&
                ReferenceEquals(_viewModel.DataStudio.SelectedDtc?.Fault, fault) &&
                ReferenceEquals(_viewModel.DataStudio.SelectedDtcTransition, selectedEvent) &&
                fault.Timeline.Any(item => ReferenceEquals(item, selectedEvent));

            if (cancellation.IsCancellationRequested || !EvidenceIsCurrent())
            {
                _viewModel.StatusText = "Fault context analysis was canceled because the source evidence changed.";
                return;
            }

            var exactSeries = selectedSeries
                .GroupBy(series => series.Timeline.Key, StringComparer.Ordinal)
                .Where(group => group.Count() == 1)
                .ToDictionary(group => group.Key, group => group.Single(), StringComparer.Ordinal);
            var rows = context.NearbyChanges
                .Where(change => exactSeries.TryGetValue(change.Timeline.Key, out var series) &&
                    ReferenceEquals(series.Timeline, change.Timeline))
                .Select(change => new NearbySignalCandidateRow(exactSeries[change.Timeline.Key], change))
                .ToArray();

            void AddToMainPlot(PlotSeriesViewModel signal)
            {
                if (!EvidenceIsCurrent() ||
                    !selectedSeries.Any(current => ReferenceEquals(current, signal)))
                {
                    _viewModel.StatusText = "Original signal source changed; reopen the fault analysis.";
                    return;
                }
                if (_viewModel.Signals.HasMultipleLoggers)
                    _viewModel.Signals.SelectedLoggerIndex = fault.OriginalSourceIndex;
                signal.IsPlotted = true;
                _viewModel.Signals.SeekToTimestamp(selectedEvent.TimestampNanoseconds);
                _viewModel.PlotCursorNanoseconds = selectedEvent.TimestampNanoseconds;
                _viewModel.StatusText = $"Added {signal.MessageName}.{signal.SignalName} to the existing plot, " +
                    "without replacing other curves. Time proximity is not a DTC cause.";
            }

            void OpenOriginal(int frameIndex)
            {
                if (!EvidenceIsCurrent() ||
                    (uint)frameIndex >= (uint)frames.Count ||
                    sourceAt(frameIndex) != fault.OriginalSourceIndex)
                {
                    _viewModel.StatusText = "This original CAN frame is no longer verified in the current physical recording.";
                    return;
                }
                _viewModel.OpenTraceFrame(frameIndex);
            }

            void InspectDidCanLead(DataStudioSnapshotCanRow link)
            {
                if (!EvidenceIsCurrent() || !snapshotLinks.Contains(link) ||
                    link.Link.Correlation.OriginalSourceIndex != fault.OriginalSourceIndex)
                {
                    _viewModel.StatusText = "The selected DTC snapshot-to-CAN lead is no longer verified for this event.";
                    return;
                }
                _viewModel.DataStudio.SelectedDtcCanCandidate = link;
                _viewModel.DataStudio.OpenSelectedDtcCanCandidateCommand.Execute(null);
            }

            void AddDidCanLead(DataStudioSnapshotCanRow link)
            {
                if (!EvidenceIsCurrent() || !snapshotLinks.Contains(link) ||
                    link.Link.Correlation.OriginalSourceIndex != fault.OriginalSourceIndex)
                {
                    _viewModel.StatusText = "This DTC snapshot-to-CAN lead is no longer source-valid.";
                    return;
                }
                var exact = selectedSeries.Where(series =>
                    CorrelationSourceIdentity.MatchesObservedTimeline(
                        link.Link.Correlation, series.Timeline)).Take(2).ToArray();
                if (exact.Length != 1)
                {
                    _viewModel.StatusText = "Cannot identify one exact recorded CAN curve for this DID lead. " +
                        "Recheck physical logger, bus, DBC and J1939 source address.";
                    return;
                }
                AddToMainPlot(exact[0]);
                _viewModel.StatusText = "Added the source-matched CAN curve from separately paired DID/CAN " +
                    "evidence. Snapshot-to-signal identity and fault causality remain unverified.";
            }

            new FaultContextWindow(this, context, rows, snapshotLinks,
                EvidenceIsCurrent, AddToMainPlot, OpenOriginal,
                InspectDidCanLead, AddDidCanLead).Show();
            _viewModel.StatusText = $"{rows.Length} observed signal change(s) near selected DTC event; " +
                "inspect original frames and catalog records before inferring a cause.";
        }
        catch (OperationCanceledException)
        {
            _viewModel.StatusText = "Fault context scan canceled due to a trace or DBC reload.";
        }
        catch (Exception ex)
        {
            _viewModel.StatusText = "Fault context could not be verified: " + ex.Message;
        }
        finally
        {
            _viewModel.Signals.PropertyChanged -= CancelOnNewCatalog;
            Closed -= CancelOnClosed;
        }
    }
}
