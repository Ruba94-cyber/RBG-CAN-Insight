using RbgCanInsight.Diagnostics;

namespace RbgCanInsight.Analysis;

/// <summary>
/// A particular 0x19 status/read/record/clear observation, not the asserted
/// physical onset of a fault. An observed status response may follow the true
/// condition by an unmeasured delay.
/// </summary>
public sealed record FaultEventContext(
    FaultMemoryEntry Fault,
    DtcStatusTransition Event,
    long WindowStartNanoseconds,
    long WindowEndNanoseconds,
    string EventMeaning,
    string StatusEvidence,
    IReadOnlyList<NearbySignalChange> NearbyChanges)
{
    public bool IsClearOperation => Event.Operation == "ClearDiagnosticInformation";
    public string Limitations =>
        "CAN signal transitions are time-near the recorded diagnostic event, not proven DTC triggers. " +
        "An ECU may have latched the fault before the tester read it. Values between CAN frames " +
        "are unknown; no cross-logger clock synchronization, root cause or snapshot equivalence is inferred.";
}

/// <summary>
/// Creates one explainable, source-safe fault investigation from a recorded
/// DTC event. Only genuine value transitions of already decoded CAN samples
/// are considered; the event's original frame membership is verified first.
/// </summary>
public static class FaultEventContextAnalyzer
{
    public static FaultEventContext Build(
        FaultMemoryEntry fault,
        DtcStatusTransition selectedEvent,
        IReadOnlyList<SignalTimeline> timelines,
        Func<int, int> sourceAt,
        long beforeNanoseconds = 5_000_000_000L,
        long afterNanoseconds = 5_000_000_000L,
        int maxResults = 30,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fault);
        ArgumentNullException.ThrowIfNull(selectedEvent);
        ArgumentNullException.ThrowIfNull(timelines);
        ArgumentNullException.ThrowIfNull(sourceAt);
        if (!fault.Timeline.Any(item => ReferenceEquals(item, selectedEvent)))
            throw new InvalidOperationException("The selected event is not in this exact DTC's recorded history.");
        if (fault.OriginalSourceIndex < 0)
            throw new InvalidOperationException("The fault's original physical logger is unverified.");
        if (selectedEvent.TimestampNanoseconds < 0)
            throw new ArgumentOutOfRangeException(nameof(selectedEvent));
        if (beforeNanoseconds < 0 || afterNanoseconds < 0 ||
            (beforeNanoseconds == 0 && afterNanoseconds == 0) ||
            beforeNanoseconds > 600_000_000_000L || afterNanoseconds > 600_000_000_000L)
            throw new ArgumentOutOfRangeException(nameof(beforeNanoseconds), "Choose a window up to ten minutes on either side.");
        if (maxResults is < 1 or > 1000)
            throw new ArgumentOutOfRangeException(nameof(maxResults));
        if (selectedEvent.FrameIndices.Count == 0 || !selectedEvent.FrameIndices.All(index =>
            index >= 0 && SourceIsVerified(sourceAt, index, fault.OriginalSourceIndex)))
            throw new InvalidOperationException(
                "The recorded DTC event has missing or cross-logger source frames. Context analysis was refused.");

        cancellationToken.ThrowIfCancellationRequested();
        var t = selectedEvent.TimestampNanoseconds;
        var start = Math.Max(0, t - beforeNanoseconds);
        var end = t > long.MaxValue - afterNanoseconds ? long.MaxValue : t + afterNanoseconds;

        // Ambiguous decoded source identities are excluded. Do not let a
        // coincident message name or a duplicate DBC key create a false curve.
        var candidates = timelines
            .Where(item => item.OriginalSourceIndex == fault.OriginalSourceIndex)
            .GroupBy(item => item.Key, StringComparer.Ordinal)
            .Where(group => group.Count() == 1)
            .Select(group => group.Single());

        var nearby = new List<NearbySignalChange>();
        var faultNameTokens = FaultSignalNameHints.FaultWords(fault);
        foreach (var timeline in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var samples = timeline.Samples;
            if (samples.Count < 2) continue;

            // Binary search for first observation not earlier than start.
            var lo = 0;
            var hi = samples.Count;
            while (lo < hi)
            {
                var mid = lo + (hi - lo) / 2;
                if (samples[mid].TimestampNanoseconds < start) lo = mid + 1;
                else hi = mid;
            }

            NearbySignalChange? best = null;
            for (var i = Math.Max(1, lo); i < samples.Count; i++)
            {
                if ((i & 2047) == 0) cancellationToken.ThrowIfCancellationRequested();
                var current = samples[i];
                if (current.TimestampNanoseconds > end) break;
                var previous = samples[i - 1];
                if (!double.IsFinite(previous.Value) || !double.IsFinite(current.Value) ||
                    current.Value == previous.Value) continue;
                if (sourceAt is not null &&
                    (!SourceIsVerified(sourceAt, previous.FrameIndex, fault.OriginalSourceIndex) ||
                     !SourceIsVerified(sourceAt, current.FrameIndex, fault.OriginalSourceIndex)))
                    continue;

                var result = new NearbySignalChange(timeline, previous, current,
                    current.TimestampNanoseconds - t);
                if (best is null || result.AbsoluteOffsetNanoseconds < best.AbsoluteOffsetNanoseconds ||
                    result.AbsoluteOffsetNanoseconds == best.AbsoluteOffsetNanoseconds &&
                    result.After.TimestampNanoseconds < best.After.TimestampNanoseconds)
                    best = result;
            }
            if (best is not null)
            {
                var hint = FaultSignalNameHints.Evaluate(faultNameTokens, timeline);
                nearby.Add(best with
                {
                    CatalogNameHintScore = hint.Score,
                    CatalogNameHint = hint.Explanation
                });
            }
        }

        // Show catalog-label matches first, THEN closest recorded transitions.
        // This is a discoverability ranking, never a causal confidence score.
        // Source-channel proximity is only a tie breaker.
        var ordered = nearby.OrderByDescending(item => item.CatalogNameHintScore)
            .ThenBy(item => item.AbsoluteOffsetNanoseconds)
            .ThenBy(item => fault.SourceChannel >= 0 && item.Timeline.Channel == fault.SourceChannel ? 0 : 1)
            .ThenBy(item => item.Timeline.Key, StringComparer.Ordinal)
            .Take(maxResults).ToArray();

        var eventMeaning = selectedEvent.Operation == "ClearDiagnosticInformation"
            ? "Acknowledged UDS 0x14 clear operation; not a verified DTC deactivation or 0x19 read-back."
            : "Recorded DTC diagnostic observation; its timestamp is not necessarily the physical fault onset.";
        return new FaultEventContext(fault, selectedEvent, start, end,
            eventMeaning, DtcStatusEvidence.Explain(fault.Timeline, selectedEvent), ordered);
    }

    private static bool SourceIsVerified(Func<int, int> sourceAt, int frameIndex, int expected)
    {
        if (frameIndex < 0) return false;
        try { return sourceAt(frameIndex) == expected; }
        catch (ArgumentOutOfRangeException) { return false; }
        catch (IndexOutOfRangeException) { return false; }
    }
}
