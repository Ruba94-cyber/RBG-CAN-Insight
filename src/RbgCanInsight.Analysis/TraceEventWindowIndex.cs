using RbgCanInsight.Domain;

namespace RbgCanInsight.Analysis;

/// <summary>
/// A bounded set of original zero-based source-frame indices within a
/// source-recorded timestamp window. The requested range and the number
/// omitted must remain visible if UI/export caps truncate the context.
/// </summary>
public sealed record TraceEventWindow(
    long RequestedStartNanoseconds,
    long RequestedEndNanoseconds,
    IReadOnlyList<int> FrameIndices,
    long TotalFramesInWindow,
    long OmittedFrameIndices,
    string? Warning);

/// <summary>
/// O(log n + k) extraction for time-sorted import snapshots. Does not copy
/// CAN payloads or index millions of frames. Never expands a trigger context
/// beyond the explicit limit without disclosing truncation.
/// </summary>
public static class TraceEventWindowIndex
{
    public const int MaxReturnedIndices = 1024;

    public static TraceEventWindow Locate(
        IReadOnlyList<CanFrame> chronologicalFrames, long startNanoseconds,
        long endNanoseconds, int maxFrames = 512)
    {
        ArgumentNullException.ThrowIfNull(chronologicalFrames);
        if (endNanoseconds < startNanoseconds)
            throw new ArgumentException("Context end precedes context start.");
        if (maxFrames is < 0 or > MaxReturnedIndices)
            throw new ArgumentOutOfRangeException(nameof(maxFrames));
        if (chronologicalFrames.Count == 0)
            return new TraceEventWindow(startNanoseconds, endNanoseconds, [], 0, 0, null);

        if (chronologicalFrames[0].TimestampNanoseconds >
            chronologicalFrames[^1].TimestampNanoseconds)
            throw new InvalidDataException("Trace is not sorted by timestamp.");

        var first = LowerBound(chronologicalFrames, startNanoseconds);
        var lastExclusive = UpperBound(chronologicalFrames, endNanoseconds);
        var count = Math.Max(0, lastExclusive - first);
        var kept = Math.Min(count, maxFrames);
        var indices = new int[kept];
        for (var i = 0; i < kept; i++)
            indices[i] = first + i;
        return new TraceEventWindow(startNanoseconds, endNanoseconds,
            indices, count, count - kept, count > kept
                ? "Context frame export is bounded; some matching source frames are not listed."
                : null);
    }

    private static int LowerBound(IReadOnlyList<CanFrame> frames, long timestamp)
    {
        var lo = 0;
        var hi = frames.Count;
        while (lo < hi)
        {
            var mid = lo + (hi - lo) / 2;
            if (frames[mid].TimestampNanoseconds < timestamp) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    private static int UpperBound(IReadOnlyList<CanFrame> frames, long timestamp)
    {
        var lo = 0;
        var hi = frames.Count;
        while (lo < hi)
        {
            var mid = lo + (hi - lo) / 2;
            if (frames[mid].TimestampNanoseconds <= timestamp) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }
}
