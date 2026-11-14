using RbgCanInsight.Domain;

namespace RbgCanInsight.Analysis;

/// <summary>One reproducible, read-only aggregation cell. The saved recipe
/// contains no CAN payload and uses the same source selection as the owning
/// Trace view. Results must be recomputed from the original recording.</summary>
public enum TraceAggregateKind
{
    ByIdentifier = 0,
    BySource = 1,
    /// <summary>Observed one-second traffic windows per original logger and bus.
    /// These are frame counts, not calculated bus utilisation or clock alignment.</summary>
    BySecond = 2
}

/// <summary>Every row references an actual recorded frame index. A CAN ID
/// belongs to a physical source and channel: identical numeric IDs never
/// implicitly identify the same ECU or signal across loggers.</summary>
public sealed record TraceAggregateRow(
    int SourceIndex, int Channel, uint Identifier, bool IsExtended,
    long Frames, long ErrorFrames, long CanFdFrames,
    long FirstTimestampNanoseconds, long LastTimestampNanoseconds,
    int FirstFrameIndex, int LastFrameIndex,
    string SourceName,
    long? BucketSecond = null)
{
    public string ChannelText => Channel < 0 ? "All channels" : $"CAN {Channel + 1}";
    public string IdentifierText => Identifier == uint.MaxValue
        ? "All IDs" : Identifier.ToString(IsExtended ? "X8" : "X3") + (IsExtended ? "x" : "");
    public string TimeBucketText => BucketSecond is { } second ? $"{second} s" : "—";
    public string FrameCountText => Frames.ToString("N0");
    public string ErrorCountText => ErrorFrames.ToString("N0");
    public string CanFdCountText => CanFdFrames.ToString("N0");
    public string TimeRangeText =>
        $"{FirstTimestampNanoseconds / 1_000_000_000d:F6} – {LastTimestampNanoseconds / 1_000_000_000d:F6} s";
    public string SpanText =>
        $"{Math.Max(0, LastTimestampNanoseconds / 1e9 - FirstTimestampNanoseconds / 1e9):F3} s";
}

/// <summary>Bounded presentation, exact total row/key counts and raw trace
/// frame index witnesses, not statistical guesses or synthetic data.</summary>
public sealed record TraceAggregateResult(
    TraceAggregateKind Kind, long ExaminedFrames, int UniqueGroups,
    bool DisplayTruncated, IReadOnlyList<TraceAggregateRow> Rows)
{
    public string Description => $"{ExaminedFrames:N0} recorded frames · {UniqueGroups:N0} group(s)" +
        (DisplayTruncated ? $" · showing top {Rows.Count:N0}; not all groups" : "") +
        (Kind == TraceAggregateKind.BySecond
            ? " · 1 s source-clock windows per bus; not bus load or logger clock sync"
            : "");
}

/// <summary>
/// Single-pass, cancellation-aware offline aggregation over a virtual list
/// of frame indices. A source-aware key prevents joining identical CAN IDs
/// from independent loggers. Memory is O(distinct keys), not O(trace frames).
/// </summary>
public static class TraceAggregateCells
{
    public const int MaximumResultRows = 300;
    public const int MaximumGroupKeys = 200_000;

    private readonly record struct Key(
        int Source, int Channel, uint Identifier, bool Extended, long SecondBucket);

    private static long FloorSecond(long timestampNanoseconds)
    {
        const long nanosecondsPerSecond = 1_000_000_000;
        var whole = timestampNanoseconds / nanosecondsPerSecond;
        // Truncation towards zero is incorrect for negative timestamps.
        // Return the bucket number rather than multiplying back to ns:
        // long.MinValue's floored second start cannot fit in a long ns value.
        return timestampNanoseconds < 0 && timestampNanoseconds % nanosecondsPerSecond != 0
            ? whole - 1 : whole;
    }

    private sealed class Accumulator
    {
        public long Count;
        public long Errors;
        public long Flexible;
        public long FirstTime = long.MaxValue;
        public long LastTime = long.MinValue;
        public int FirstIndex = -1;
        public int LastIndex = -1;
        public void Add(CanFrame frame, int index)
        {
            Count++;
            if (frame.IsError) Errors++;
            if ((frame.Flags & CanFrameFlags.FlexibleDataRate) != 0) Flexible++;
            // Captures may be out of timestamp order. Retain original frame
            // witnesses for actual min/max timestamps, not scan-order bounds.
            if (frame.TimestampNanoseconds < FirstTime)
            {
                FirstTime = frame.TimestampNanoseconds;
                FirstIndex = index;
            }
            if (frame.TimestampNanoseconds >= LastTime)
            {
                LastTime = frame.TimestampNanoseconds;
                LastIndex = index;
            }
        }
    }

    public static TraceAggregateResult Calculate(
        IReadOnlyList<CanFrame> frames,
        int visibleCount,
        Func<int, int> visibleFrameIndexAt,
        TraceAggregateKind kind,
        Func<int, int>? sourceAt = null,
        IReadOnlyList<string>? sourcePaths = null,
        int maxRows = MaximumResultRows,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentNullException.ThrowIfNull(visibleFrameIndexAt);
        if (visibleCount < 0 || visibleCount > frames.Count)
            throw new ArgumentOutOfRangeException(nameof(visibleCount));
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        if (maxRows < 1 || maxRows > MaximumResultRows)
            throw new ArgumentOutOfRangeException(nameof(maxRows));
        if (sourcePaths is { Count: > 1 } && sourceAt is null)
            throw new ArgumentException(
                "Multi-logger aggregation requires an original-frame source index.",
                nameof(sourceAt));

        var groups = new Dictionary<Key, Accumulator>();
        for (var position = 0; position < visibleCount; position++)
        {
            if ((position & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
            var frameIndex = visibleFrameIndexAt(position);
            if ((uint)frameIndex >= (uint)frames.Count)
                throw new ArgumentOutOfRangeException(nameof(visibleFrameIndexAt),
                    "A visible row points outside the active capture.");
            var frame = frames[frameIndex];
            var source = sourceAt?.Invoke(frameIndex) ?? 0;
            if (source < 0 || (sourcePaths is not null && source >= sourcePaths.Count))
                throw new ArgumentOutOfRangeException(nameof(sourceAt),
                    "A row references an unknown physical recording.");
            var key = kind switch
            {
                TraceAggregateKind.BySource =>
                    new Key(source, -1, uint.MaxValue, false, long.MinValue),
                TraceAggregateKind.BySecond =>
                    new Key(source, frame.Channel, uint.MaxValue, false,
                        FloorSecond(frame.TimestampNanoseconds)),
                _ => new Key(source, frame.Channel, frame.Identifier, frame.IsExtended, long.MinValue)
            };
            if (!groups.TryGetValue(key, out var count))
            {
                if (groups.Count >= MaximumGroupKeys)
                    throw new InvalidOperationException(
                        $"Too many distinct CAN groups (limit {MaximumGroupKeys:N0}). " +
                        "Focus one source or apply a narrower filter, then retry.");
                groups.Add(key, count = new Accumulator());
            }
            count.Add(frame, frameIndex);
        }
        cancellationToken.ThrowIfCancellationRequested();
        var rows = groups
            .OrderByDescending(group => group.Value.Count)
            .ThenBy(group => group.Key.Source)
            .ThenBy(group => group.Key.Channel)
            .ThenBy(group => group.Key.Identifier)
            .ThenBy(group => group.Key.Extended)
            .ThenBy(group => group.Key.SecondBucket)
            .Take(maxRows)
            .Select(group => new TraceAggregateRow(
                group.Key.Source, group.Key.Channel, group.Key.Identifier, group.Key.Extended,
                group.Value.Count, group.Value.Errors, group.Value.Flexible,
                group.Value.FirstTime, group.Value.LastTime,
                group.Value.FirstIndex, group.Value.LastIndex,
                sourcePaths is { } paths ? paths[group.Key.Source] :
                    sourceAt is null ? "Current recording" : $"Recording {group.Key.Source + 1}",
                kind == TraceAggregateKind.BySecond ? group.Key.SecondBucket : null))
            .ToArray();
        return new TraceAggregateResult(kind, visibleCount, groups.Count,
            groups.Count > rows.Length, rows);
    }
}
