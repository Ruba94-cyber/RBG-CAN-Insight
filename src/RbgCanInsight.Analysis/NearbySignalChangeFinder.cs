namespace RbgCanInsight.Analysis;

/// <summary>A recorded physical value transition near an operator-chosen instant.
/// The time offset is descriptive only: temporal proximity never proves cause,
/// ECU ownership or equivalence with a diagnostic DTC/DID.</summary>
public sealed record NearbySignalChange(
    SignalTimeline Timeline,
    SignalSample Before,
    SignalSample After,
    long OffsetNanoseconds)
{
    /// <summary>Transparent text overlap only, never inferred causal evidence.</summary>
    public int CatalogNameHintScore { get; init; }
    public string CatalogNameHint { get; init; } =
        "Temporal proximity only; no fault association verified";

    public long AbsoluteOffsetNanoseconds =>
        OffsetNanoseconds == long.MinValue ? long.MaxValue : Math.Abs(OffsetNanoseconds);
}

/// <summary>Source-scoped offline discovery of which decoded signals actually
/// changed around a selected time. Reads original timeline samples, not plotted
/// or visually interpolated points. No cross-logger clock alignment is assumed.</summary>
public static class NearbySignalChangeFinder
{
    public static IReadOnlyList<NearbySignalChange> Find(
        IReadOnlyList<SignalTimeline> timelines,
        SignalTimeline focus,
        long eventTimestampNanoseconds,
        long halfWindowNanoseconds = 5_000_000_000L,
        int maximumResults = 20,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(timelines);
        ArgumentNullException.ThrowIfNull(focus);
        if (eventTimestampNanoseconds < 0)
            throw new ArgumentOutOfRangeException(nameof(eventTimestampNanoseconds));
        if (halfWindowNanoseconds is <= 0 or > 600_000_000_000L)
            throw new ArgumentOutOfRangeException(nameof(halfWindowNanoseconds));
        if (maximumResults is < 1 or > 1000)
            throw new ArgumentOutOfRangeException(nameof(maximumResults));

        var start = Math.Max(0, eventTimestampNanoseconds - halfWindowNanoseconds);
        var end = eventTimestampNanoseconds > long.MaxValue - halfWindowNanoseconds
            ? long.MaxValue : eventTimestampNanoseconds + halfWindowNanoseconds;

        var matches = new List<NearbySignalChange>();
        foreach (var candidate in timelines)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Identical DBC names/IDs can occur on unrelated physical loggers.
            // Do not compare their timestamps until a separate alignment exists.
            if (candidate.Key == focus.Key ||
                candidate.OriginalSourceIndex != focus.OriginalSourceIndex)
                continue;
            if (focus.OriginalSourcePath is { Length: > 0 } source &&
                candidate.OriginalSourcePath is { Length: > 0 } other &&
                !string.Equals(source, other, StringComparison.OrdinalIgnoreCase))
                continue;

            var samples = candidate.Samples;
            if (samples.Count < 2) continue;
            var lo = 0;
            var hi = samples.Count;
            while (lo < hi)
            {
                var middle = lo + (hi - lo) / 2;
                if (samples[middle].TimestampNanoseconds < start)
                    lo = middle + 1;
                else
                    hi = middle;
            }

            NearbySignalChange? best = null;
            for (var i = Math.Max(1, lo); i < samples.Count; i++)
            {
                if ((i & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
                var current = samples[i];
                if (current.TimestampNanoseconds > end) break;
                var previous = samples[i - 1];
                if (!double.IsFinite(current.Value) || !double.IsFinite(previous.Value) ||
                    current.Value == previous.Value)
                    continue;
                var offset = current.TimestampNanoseconds - eventTimestampNanoseconds;
                var hit = new NearbySignalChange(candidate, previous, current, offset);
                if (best is null || hit.AbsoluteOffsetNanoseconds < best.AbsoluteOffsetNanoseconds ||
                    (hit.AbsoluteOffsetNanoseconds == best.AbsoluteOffsetNanoseconds &&
                     hit.After.TimestampNanoseconds < best.After.TimestampNanoseconds))
                    best = hit;
            }
            if (best is not null) matches.Add(best);
        }

        return matches
            .OrderBy(hit => hit.AbsoluteOffsetNanoseconds)
            .ThenBy(hit => hit.Timeline.Channel == focus.Channel ? 0 : 1)
            .ThenBy(hit => hit.Timeline.Key, StringComparer.Ordinal)
            .Take(maximumResults)
            .ToArray();
    }
}
