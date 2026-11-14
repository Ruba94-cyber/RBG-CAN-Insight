using RbgCanInsight.Domain;

namespace RbgCanInsight.Analysis;

/// <summary>
/// One specific timing gap between two consecutive frames of the same identifier, anomalously
/// large relative to that identifier's own average period. Unlike <see cref="MessageStatisticsRow"/>
/// (one row per identifier, summarizing its single worst gap over the whole trace), every
/// occurrence is captured here so incidents can be read back in chronological order — "what
/// happened, and when" across the whole trace, not "which IDs have issues".
/// </summary>
public sealed record TimingIncident(
    int Channel,
    uint Identifier,
    bool IsExtended,
    long TimestampNanoseconds,
    int FrameIndex,
    double GapMilliseconds,
    double AveragePeriodMilliseconds,
    double RatioVsAverage);

public sealed record MessageStatisticsRow(
    int Channel,
    uint Identifier,
    bool IsExtended,
    long FrameCount,
    long FirstTimestampNanoseconds,
    long LastTimestampNanoseconds,
    double AveragePeriodMilliseconds,
    double MinPeriodMilliseconds,
    double MaxPeriodMilliseconds,
    int MinLength,
    int MaxLength,
    double StdDevPeriodMilliseconds = 0,
    int BurstCount = 0);

/// <summary>One source-verified per-identifier statistical result. SourceIndex is
/// the zero-based original logger index, never the CAN channel.</summary>
public sealed record SourceMessageStatisticsRow(int SourceIndex, MessageStatisticsRow Row);

/// <summary>One timing incident with the immutable identity of its original
/// physical logger. The embedded frame index addresses the merged frame store.</summary>
public sealed record SourceTimingIncident(int SourceIndex, TimingIncident Incident);

/// <summary>Single-pass per-identifier statistics over a loaded trace.</summary>
public static class TraceStatistics
{
    /// <summary>A gap this much shorter than the identifier's own average period counts as a
    /// "burst" occurrence — symmetric to <see cref="FindTimingIncidents"/>'s default 2.5x-slower
    /// threshold, but for messages arriving unusually fast (retries, event-triggered supplementary
    /// sends) rather than unusually slow.</summary>
    private const double BurstRatioThreshold = 0.4;

    public static IReadOnlyList<MessageStatisticsRow> Compute(IReadOnlyList<CanFrame> frames)
    {
        var accumulators = new Dictionary<(int Channel, uint Identifier, bool IsExtended), Accumulator>();
        foreach (var frame in frames)
        {
            var key = (frame.Channel, frame.Identifier, frame.IsExtended);
            accumulators.TryGetValue(key, out var accumulator);
            accumulator.Add(frame);
            accumulators[key] = accumulator;
        }

        var rows = accumulators
            .OrderBy(pair => pair.Key.Channel)
            .ThenBy(pair => pair.Key.Identifier)
            .ThenBy(pair => pair.Key.IsExtended)
            .Select(pair => pair.Value.ToRow(pair.Key.Channel, pair.Key.Identifier, pair.Key.IsExtended))
            .ToArray();

        return AttachBurstCounts(frames, rows);
    }

    /// <summary>
    /// Calculate cycle statistics independently for each original logger.
    /// The source callback maps an index of the merged frame store to the
    /// original acquisition; timestamps are NEVER aligned across loggers.
    /// Both passes walk the full capture once and do not duplicate CAN frames.
    /// </summary>
    public static IReadOnlyList<SourceMessageStatisticsRow> ComputeBySource(
        IReadOnlyList<CanFrame> frames, Func<int, int> sourceAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentNullException.ThrowIfNull(sourceAt);
        var accumulators = new Dictionary<(int Source, int Channel, uint Id, bool Extended), Accumulator>();
        for (var index = 0; index < frames.Count; index++)
        {
            if ((index & 0xFFFF) == 0) cancellationToken.ThrowIfCancellationRequested();
            var source = sourceAt(index);
            if (source < 0) throw new ArgumentOutOfRangeException(nameof(sourceAt),
                "A CAN frame has no valid original recording index.");
            var frame = frames[index];
            var key = (source, frame.Channel, frame.Identifier, frame.IsExtended);
            accumulators.TryGetValue(key, out var acc);
            acc.Add(frame);
            accumulators[key] = acc;
        }

        var rows = accumulators.OrderBy(pair => pair.Key.Source)
            .ThenBy(pair => pair.Key.Channel)
            .ThenBy(pair => pair.Key.Id)
            .ThenBy(pair => pair.Key.Extended)
            .Select(pair => new SourceMessageStatisticsRow(pair.Key.Source,
                pair.Value.ToRow(pair.Key.Channel, pair.Key.Id, pair.Key.Extended)))
            .ToArray();

        var averages = new Dictionary<(int Source, int Channel, uint Id, bool Extended), double>();
        foreach (var item in rows)
        {
            var row = item.Row;
            if (row.FrameCount >= 3 && row.AveragePeriodMilliseconds > 0)
                averages[(item.SourceIndex, row.Channel, row.Identifier,
                    row.IsExtended)] = row.AveragePeriodMilliseconds;
        }
        if (averages.Count == 0) return rows;

        var lastSeen = new Dictionary<(int Source, int Channel, uint Id, bool Extended), long>();
        var bursts = new Dictionary<(int Source, int Channel, uint Id, bool Extended), int>();
        for (var index = 0; index < frames.Count; index++)
        {
            if ((index & 0xFFFF) == 0) cancellationToken.ThrowIfCancellationRequested();
            var frame = frames[index];
            var source = sourceAt(index);
            if (source < 0) throw new ArgumentOutOfRangeException(nameof(sourceAt));
            var key = (source, frame.Channel, frame.Identifier, frame.IsExtended);
            if (!averages.TryGetValue(key, out var averageMs)) continue;
            if (lastSeen.TryGetValue(key, out var last))
            {
                var gapMs = ((double)frame.TimestampNanoseconds - last) / 1_000_000d;
                if (gapMs >= 0 && gapMs <= averageMs * BurstRatioThreshold)
                    bursts[key] = bursts.GetValueOrDefault(key) + 1;
            }
            lastSeen[key] = frame.TimestampNanoseconds;
        }
        return rows.Select(item =>
        {
            var row = item.Row;
            var key = (item.SourceIndex, row.Channel, row.Identifier, row.IsExtended);
            return bursts.TryGetValue(key, out var count)
                ? item with { Row = row with { BurstCount = count } }
                : item;
        }).ToArray();
    }

    /// <summary>Find observed period gaps only between frames from the
    /// same original recording, bus, wire ID and frame format. The returned
    /// global frame index remains usable to open exact raw evidence.</summary>
    public static IReadOnlyList<SourceTimingIncident> FindTimingIncidentsBySource(
        IReadOnlyList<CanFrame> frames,
        IReadOnlyList<SourceMessageStatisticsRow> statistics,
        Func<int, int> sourceAt,
        double ratioThreshold = 2.5,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentNullException.ThrowIfNull(statistics);
        ArgumentNullException.ThrowIfNull(sourceAt);
        if (!double.IsFinite(ratioThreshold) || ratioThreshold <= 0)
            throw new ArgumentOutOfRangeException(nameof(ratioThreshold));

        var averages = new Dictionary<(int Source, int Channel, uint Id, bool Extended), double>();
        foreach (var sourceRow in statistics)
        {
            var row = sourceRow.Row;
            if (row.FrameCount >= 3 && row.AveragePeriodMilliseconds > 0)
                averages[(sourceRow.SourceIndex, row.Channel, row.Identifier,
                    row.IsExtended)] = row.AveragePeriodMilliseconds;
        }
        var previous = new Dictionary<(int Source, int Channel, uint Id, bool Extended), long>();
        var incidents = new List<SourceTimingIncident>();
        for (var index = 0; index < frames.Count; index++)
        {
            if ((index & 0xFFFF) == 0) cancellationToken.ThrowIfCancellationRequested();
            var frame = frames[index];
            var source = sourceAt(index);
            if (source < 0) throw new ArgumentOutOfRangeException(nameof(sourceAt));
            var key = (source, frame.Channel, frame.Identifier, frame.IsExtended);
            if (!averages.TryGetValue(key, out var averageMs)) continue;
            if (previous.TryGetValue(key, out var last))
            {
                var gapMs = ((double)frame.TimestampNanoseconds - last) / 1_000_000d;
                if (gapMs >= 0 && gapMs / averageMs >= ratioThreshold)
                    incidents.Add(new SourceTimingIncident(source,
                        new TimingIncident(frame.Channel, frame.Identifier,
                            frame.IsExtended, frame.TimestampNanoseconds,
                            index, gapMs, averageMs, gapMs / averageMs)));
            }
            previous[key] = frame.TimestampNanoseconds;
        }
        return incidents.OrderBy(item => item.Incident.TimestampNanoseconds)
            .ThenBy(item => item.SourceIndex)
            .ThenBy(item => item.Incident.FrameIndex).ToArray();
    }

    /// <summary>Second pass, mirroring <see cref="FindTimingIncidents"/>'s pattern: the per-ID
    /// average period is only known once every frame has been seen once, so counting how many
    /// individual gaps come in unusually fast needs its own walk over the trace.</summary>
    private static IReadOnlyList<MessageStatisticsRow> AttachBurstCounts(
        IReadOnlyList<CanFrame> frames, IReadOnlyList<MessageStatisticsRow> rows)
    {
        var averageByKey = new Dictionary<(int Channel, uint Identifier, bool IsExtended), double>();
        foreach (var row in rows)
        {
            if (row.FrameCount >= 3 && row.AveragePeriodMilliseconds > 0)
            {
                averageByKey[(row.Channel, row.Identifier, row.IsExtended)] = row.AveragePeriodMilliseconds;
            }
        }

        if (averageByKey.Count == 0)
        {
            return rows;
        }

        var burstCounts = new Dictionary<(int Channel, uint Identifier, bool IsExtended), int>();
        var lastTimestampByKey = new Dictionary<(int Channel, uint Identifier, bool IsExtended), long>();
        foreach (var frame in frames)
        {
            var key = (frame.Channel, frame.Identifier, frame.IsExtended);
            if (!averageByKey.TryGetValue(key, out var averageMs))
            {
                continue;
            }

            if (lastTimestampByKey.TryGetValue(key, out var lastTimestamp))
            {
                var gapMs = (frame.TimestampNanoseconds - lastTimestamp) / 1_000_000d;
                if (gapMs <= averageMs * BurstRatioThreshold)
                {
                    burstCounts[key] = burstCounts.GetValueOrDefault(key) + 1;
                }
            }

            lastTimestampByKey[key] = frame.TimestampNanoseconds;
        }

        if (burstCounts.Count == 0)
        {
            return rows;
        }

        return rows
            .Select(row => burstCounts.TryGetValue((row.Channel, row.Identifier, row.IsExtended), out var count)
                ? row with { BurstCount = count }
                : row)
            .ToArray();
    }

    /// <summary>
    /// Walks the trace once in chronological order and flags every individual gap between
    /// consecutive frames of the same identifier that is at least <paramref name="ratioThreshold"/>
    /// times that identifier's own average period (computed from <paramref name="statistics"/>).
    /// Returned in timestamp order so incidents across every ID interleave by when they actually
    /// happened, not grouped by which ID they belong to.
    /// </summary>
    public static IReadOnlyList<TimingIncident> FindTimingIncidents(
        IReadOnlyList<CanFrame> frames, IReadOnlyList<MessageStatisticsRow> statistics, double ratioThreshold = 2.5)
    {
        var averageByKey = new Dictionary<(int Channel, uint Identifier, bool IsExtended), double>();
        foreach (var row in statistics)
        {
            if (row.FrameCount >= 3 && row.AveragePeriodMilliseconds > 0)
            {
                averageByKey[(row.Channel, row.Identifier, row.IsExtended)] = row.AveragePeriodMilliseconds;
            }
        }

        var lastTimestampByKey = new Dictionary<(int Channel, uint Identifier, bool IsExtended), long>();
        var incidents = new List<TimingIncident>();
        for (var index = 0; index < frames.Count; index++)
        {
            var frame = frames[index];
            var key = (frame.Channel, frame.Identifier, frame.IsExtended);
            if (!averageByKey.TryGetValue(key, out var averageMs))
            {
                continue;
            }

            if (lastTimestampByKey.TryGetValue(key, out var lastTimestamp))
            {
                var gapMs = (frame.TimestampNanoseconds - lastTimestamp) / 1_000_000d;
                var ratio = gapMs / averageMs;
                if (ratio >= ratioThreshold)
                {
                    incidents.Add(new TimingIncident(
                        frame.Channel, frame.Identifier, frame.IsExtended,
                        frame.TimestampNanoseconds, index, gapMs, averageMs, ratio));
                }
            }

            lastTimestampByKey[key] = frame.TimestampNanoseconds;
        }

        return incidents.OrderBy(incident => incident.TimestampNanoseconds).ToArray();
    }

    private struct Accumulator
    {
        private long _count;
        private long _first;
        private long _last;
        private long _minPeriod;
        private long _maxPeriod;
        private long _periodSum;
        // Sum of squared periods in milliseconds (not nanoseconds — a period of just a few
        // seconds squared in nanoseconds risks overflowing a long) to compute the population
        // standard deviation from a single pass via Var = E[X^2] - E[X]^2.
        private double _periodSumSquaresMs;
        private int _minLength;
        private int _maxLength;

        public void Add(in CanFrame frame)
        {
            if (_count == 0)
            {
                _first = frame.TimestampNanoseconds;
                _minPeriod = long.MaxValue;
                _minLength = frame.DataLength;
                _maxLength = frame.DataLength;
            }
            else
            {
                var period = Math.Max(0, frame.TimestampNanoseconds - _last);
                _minPeriod = Math.Min(_minPeriod, period);
                _maxPeriod = Math.Max(_maxPeriod, period);
                _periodSum += period;
                var periodMs = period / 1_000_000d;
                _periodSumSquaresMs += periodMs * periodMs;
                _minLength = Math.Min(_minLength, frame.DataLength);
                _maxLength = Math.Max(_maxLength, frame.DataLength);
            }

            _last = frame.TimestampNanoseconds;
            _count++;
        }

        public readonly MessageStatisticsRow ToRow(int channel, uint identifier, bool isExtended)
        {
            var periodCount = _count - 1;
            var averageMs = periodCount > 0 ? _periodSum / (double)periodCount / 1_000_000d : 0;
            var variance = periodCount > 0
                ? Math.Max(0, _periodSumSquaresMs / periodCount - averageMs * averageMs)
                : 0;
            return new MessageStatisticsRow(
                channel,
                identifier,
                isExtended,
                _count,
                _first,
                _last,
                averageMs,
                _count > 1 ? _minPeriod / 1_000_000d : 0,
                _count > 1 ? _maxPeriod / 1_000_000d : 0,
                _minLength,
                _maxLength,
                Math.Sqrt(variance));
        }
    }
}
