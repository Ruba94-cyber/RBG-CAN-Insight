using RbgCanInsight.Domain;

namespace RbgCanInsight.TraceIO;

/// <summary>How several logs are combined into one offline session (modelled on Vector's merge modes).</summary>
public enum TraceMergeMode
{
    /// <summary>Interleave all frames by timestamp; channel numbers are kept (files share channels).</summary>
    ByTime,
    /// <summary>Append the files one after another. A file whose first timestamp is not after the previous
    /// file's last frame is shifted so it starts exactly there; otherwise original timestamps are kept.</summary>
    Sequential,
    /// <summary>Interleave by timestamp, but give every file its own channel range (offset = number of
    /// channels used by the preceding files) so equal channel numbers of different files stay apart.</summary>
    SeparateChannels
}

/// <param name="ChannelOffsets">Channel offset added to each source.</param>
/// <param name="TimeShiftsNs">Time shift applied to each source.</param>
public sealed record TraceMergeResult(
    List<CanFrame> Frames, IReadOnlyList<int> ChannelOffsets, IReadOnlyList<long> TimeShiftsNs);

/// <summary>
/// Pure, non-mutating operations over CAN frame sequences. Every function returns new frames
/// (payload, flags, declared length and FD flags preserved) and renumbers
/// <see cref="CanFrame.Sequence"/> from 0 so the output is monotonic. Inputs are never modified.
/// </summary>
public static class TraceTools
{
    /// <summary>
    /// K-way merge of already time-sorted lists. Stable: frames with equal timestamps are ordered
    /// by source index, then by position within the source. When <paramref name="channelOffsets"/>
    /// is given, source i has channelOffsets[i] added to every frame channel so channels of
    /// different sources do not collide.
    /// </summary>
    /// <exception cref="ArgumentException">A source is not sorted by timestamp, or the offsets length differs.</exception>
    public static List<CanFrame> Merge(
        IReadOnlyList<IReadOnlyList<CanFrame>> sources,
        IReadOnlyList<int>? channelOffsets = null)
    {
        ArgumentNullException.ThrowIfNull(sources);
        if (channelOffsets is not null && channelOffsets.Count != sources.Count)
            throw new ArgumentException("channelOffsets must have one entry per source.", nameof(channelOffsets));
        if (channelOffsets is not null && channelOffsets.Any(o => o < 0))
            throw new ArgumentOutOfRangeException(nameof(channelOffsets), "Channel offsets must be >= 0.");

        for (var s = 0; s < sources.Count; s++)
            for (var i = 1; i < sources[s].Count; i++)
                if (sources[s][i].TimestampNanoseconds < sources[s][i - 1].TimestampNanoseconds)
                    throw new ArgumentException($"Source {s} is not sorted by timestamp (frame {i}).", nameof(sources));

        var total = 0;
        foreach (var source in sources) total = checked(total + source.Count);
        var result = new List<CanFrame>(total);
        var heads = new int[sources.Count];
        // O(log sourceCount) per frame, rather than scanning every source for every frame.
        // The source index is the secondary priority: ties retain source order, and
        // each individual source advances in its original stable timestamp order.
        var queue = new PriorityQueue<int, (long Timestamp, int Source)>();
        for (var source = 0; source < sources.Count; source++)
            if (sources[source].Count > 0)
                queue.Enqueue(source, (sources[source][0].TimestampNanoseconds, source));
        while (queue.TryDequeue(out var source, out _))
        {
            var frame = sources[source][heads[source]++];
            var offset = channelOffsets?[source] ?? 0;
            result.Add(Copy(frame, checked(frame.Channel + offset), frame.TimestampNanoseconds, result.Count));
            if (heads[source] < sources[source].Count)
                queue.Enqueue(source, (sources[source][heads[source]].TimestampNanoseconds, source));
        }
        return result;
    }

    /// <summary>Combines several time-sorted sources according to <paramref name="mode"/>. Reuses
    /// <see cref="Merge"/> and <see cref="ShiftTime"/>; sources are never modified.</summary>
    public static TraceMergeResult MergeSession(IReadOnlyList<IReadOnlyList<CanFrame>> sources, TraceMergeMode mode)
    {
        ArgumentNullException.ThrowIfNull(sources);
        var offsets = new int[sources.Count];
        var shifts = new long[sources.Count];
        IReadOnlyList<IReadOnlyList<CanFrame>> effective = sources;
        if (mode == TraceMergeMode.SeparateChannels)
        {
            var next = 0;
            for (var i = 0; i < sources.Count; i++)
            {
                offsets[i] = next;
                if (sources[i].Count > 0) next += sources[i].Max(f => f.Channel) + 1;
            }
        }
        else if (mode == TraceMergeMode.Sequential)
        {
            var shifted = new List<IReadOnlyList<CanFrame>>(sources.Count);
            long previousEnd = 0;
            var any = false;
            for (var i = 0; i < sources.Count; i++)
            {
                IReadOnlyList<CanFrame> list = sources[i];
                if (list.Count == 0) { shifted.Add(list); continue; }
                var start = list[0].TimestampNanoseconds;
                if (any && start < previousEnd)
                {
                    shifts[i] = previousEnd - start;
                    list = ShiftTime(list, shifts[i]);
                }
                shifted.Add(list);
                previousEnd = list[^1].TimestampNanoseconds;
                any = true;
            }
            effective = shifted;
        }

        return new TraceMergeResult(Merge(effective, offsets), offsets, shifts);
    }

    /// <summary>Adds <paramref name="nanoseconds"/> (may be negative) to every timestamp.
    /// Throws if any resulting timestamp would be below zero or overflow.</summary>
    public static List<CanFrame> ShiftTime(IEnumerable<CanFrame> frames, long nanoseconds)
    {
        ArgumentNullException.ThrowIfNull(frames);
        var result = new List<CanFrame>();
        foreach (var f in frames)
        {
            long shifted;
            try { shifted = checked(f.TimestampNanoseconds + nanoseconds); }
            catch (OverflowException) { throw new ArgumentOutOfRangeException(nameof(nanoseconds), "Time shift overflows the timestamp range."); }
            if (shifted < 0)
                throw new ArgumentOutOfRangeException(nameof(nanoseconds),
                    $"Shifting by {nanoseconds} ns would produce a negative timestamp (frame at {f.TimestampNanoseconds} ns).");
            result.Add(Copy(f, f.Channel, shifted, result.Count));
        }
        return result;
    }

    /// <summary>Keeps frames with <c>fromNs &lt;= timestamp &lt; toNs</c> (from inclusive, to exclusive).
    /// Timestamps are not rebased.</summary>
    public static List<CanFrame> Cut(IEnumerable<CanFrame> frames, long fromNs, long toNs)
    {
        ArgumentNullException.ThrowIfNull(frames);
        if (toNs < fromNs) throw new ArgumentException("toNs must not be less than fromNs.", nameof(toNs));
        var result = new List<CanFrame>();
        foreach (var f in frames)
            if (f.TimestampNanoseconds >= fromNs && f.TimestampNanoseconds < toNs)
                result.Add(Copy(f, f.Channel, f.TimestampNanoseconds, result.Count));
        return result;
    }

    /// <summary>Splits a time-ordered sequence into segments wherever the gap between two
    /// consecutive frames is &gt;= <paramref name="minGapNs"/>. Each segment is renumbered from 0.</summary>
    public static List<List<CanFrame>> SplitAtGaps(IEnumerable<CanFrame> frames, long minGapNs)
    {
        ArgumentNullException.ThrowIfNull(frames);
        if (minGapNs <= 0) throw new ArgumentOutOfRangeException(nameof(minGapNs), "minGapNs must be positive.");
        var segments = new List<List<CanFrame>>();
        List<CanFrame>? current = null;
        long previous = 0;
        foreach (var f in frames)
        {
            if (current is null || f.TimestampNanoseconds - previous >= minGapNs)
            {
                current = [];
                segments.Add(current);
            }
            current.Add(Copy(f, f.Channel, f.TimestampNanoseconds, current.Count));
            previous = f.TimestampNanoseconds;
        }
        return segments;
    }

    /// <summary>Maps channels through <paramref name="map"/>; channels absent from the map are unchanged.
    /// Two source channels may be mapped to the same target (explicit merge of channels).</summary>
    public static List<CanFrame> RemapChannels(IEnumerable<CanFrame> frames, IReadOnlyDictionary<int, int> map)
    {
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentNullException.ThrowIfNull(map);
        foreach (var target in map.Values)
            if (target < 0) throw new ArgumentOutOfRangeException(nameof(map), "Target channels must be >= 0.");
        var result = new List<CanFrame>();
        foreach (var f in frames)
            result.Add(Copy(f, map.TryGetValue(f.Channel, out var t) ? t : f.Channel, f.TimestampNanoseconds, result.Count));
        return result;
    }

    /// <summary>Keeps frames for which <paramref name="predicate"/> is true.</summary>
    public static List<CanFrame> SelectIds(IEnumerable<CanFrame> frames, Func<CanFrame, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentNullException.ThrowIfNull(predicate);
        var result = new List<CanFrame>();
        foreach (var f in frames)
            if (predicate(f))
                result.Add(Copy(f, f.Channel, f.TimestampNanoseconds, result.Count));
        return result;
    }

    private static CanFrame Copy(in CanFrame f, int channel, long timestamp, long sequence)
    {
        Span<byte> payload = stackalloc byte[64];
        f.CopyPayloadTo(payload);
        return new CanFrame(channel, f.Identifier, payload[..f.DataLength], timestamp, f.Flags, sequence, f.DeclaredDataLength);
    }
}
