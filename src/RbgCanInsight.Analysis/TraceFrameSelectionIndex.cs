using RbgCanInsight.Domain;

namespace RbgCanInsight.Analysis;

/// <summary>
/// Lightweight source-frame indexing for replay and filtering large offline traces.
/// Build each query's full ordered result once; replay changes the visible PREFIX
/// using a binary search rather than scanning all captured frames again.
/// Overwrite/latest-ID mode incrementally advances its per-physical-ID state.
/// Source indices always point to the original sorted frame list; no CAN payloads
/// or row view-models are copied into this index.
/// </summary>
public sealed class TraceFrameSelectionIndex(IReadOnlyList<CanFrame> frames)
{
    private readonly Dictionary<(int Channel, uint Identifier, bool Extended), int> _latestById = [];
    private int _latestProcessedThrough = -1;
    private int _latestScopeToken = -1;

    /// <summary>Number of source frames processed by incremental Latest mode since
    /// construction or last reset. Useful to detect accidental O(n) replay scans.</summary>
    public long LatestScannedFrames { get; private set; }

    /// <summary>Build both global and physical-source-local previous same-ID
    /// links in one pass. A focused dataset must not show payload deltas from
    /// a frame recorded by a different logger with the same CAN ID/channel.
    /// The two arrays share storage for single-file sessions.</summary>
    public static (int[] Global, int[] BySource) BuildPreviousIndices(
        IReadOnlyList<CanFrame> frames, Func<int, int>? sourceAt = null)
    {
        ArgumentNullException.ThrowIfNull(frames);
        var global = new int[frames.Count];
        var bySource = sourceAt is null ? global : new int[frames.Count];
        var latestGlobal = new Dictionary<(int Channel, uint Id, bool Extended), int>();
        Dictionary<(int Source, int Channel, uint Id, bool Extended), int>? latestSource =
            sourceAt is null ? null : new();
        for (var i = 0; i < frames.Count; i++)
        {
            var frame = frames[i];
            var key = (frame.Channel, frame.Identifier, frame.IsExtended);
            global[i] = latestGlobal.TryGetValue(key, out var prev) ? prev : -1;
            latestGlobal[key] = i;
            if (latestSource is null) continue;
            var sourceKey = (sourceAt!(i), frame.Channel, frame.Identifier, frame.IsExtended);
            bySource[i] = latestSource.TryGetValue(sourceKey, out var localPrev) ? localPrev : -1;
            latestSource[sourceKey] = i;
        }
        return (global, bySource);
    }

    /// <summary>Returns monotonically increasing indices for all matching frames.
    /// Call only when the query/DBC context changes, not on each replay tick.</summary>
    public int[] BuildMatchingIndex(
        Func<CanFrame, bool>? matches,
        Func<int, bool>? indexIsEligible = null,
        CancellationToken cancellationToken = default,
        Action<double>? progress = null)
    {
        const int checkInterval = 16_384;
        var hits = new List<int>(Math.Min(frames.Count, 4096));
        for (var index = 0; index < frames.Count; index++)
        {
            if (index % checkInterval == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Invoke(index / (double)frames.Count);
            }

            if (indexIsEligible is not null && !indexIsEligible(index))
                continue;
            if (matches is null || matches(frames[index]))
                hits.Add(index);
        }
        return hits.ToArray();
    }

    /// <summary>Count of sorted frame indices at or before the requested
    /// original source index. O(log matches), independent of trace length.</summary>
    public static int VisiblePrefixLength(int[] orderedIndices, int throughFrameIndex)
    {
        var low = 0;
        var high = orderedIndices.Length;
        while (low < high)
        {
            var mid = low + (high - low) / 2;
            if (orderedIndices[mid] <= throughFrameIndex)
                low = mid + 1;
            else
                high = mid;
        }
        return low;
    }

    /// <summary>Last frame for each channel + numeric CAN ID + 11/29-bit format.
    /// A forward seek only processes newly exposed frames; a backward seek
    /// rebuilds to avoid showing frames from the future.</summary>
    public int[] LatestIndicesThrough(int throughFrameIndex,
        Func<int, bool>? indexIsEligible = null, int scopeToken = -1)
    {
        // A new physical source scope must never reuse latest-by-ID state
        // from a different source, even if it shares the same numeric CAN IDs.
        if (_latestScopeToken != scopeToken)
        {
            _latestById.Clear();
            _latestProcessedThrough = -1;
            _latestScopeToken = scopeToken;
        }
        if (throughFrameIndex < 0 || frames.Count == 0)
        {
            _latestById.Clear();
            _latestProcessedThrough = -1;
            return [];
        }

        var end = Math.Min(throughFrameIndex, frames.Count - 1);
        if (end < _latestProcessedThrough)
        {
            _latestById.Clear();
            _latestProcessedThrough = -1;
        }

        for (var index = _latestProcessedThrough + 1; index <= end; index++)
        {
            if (indexIsEligible is not null && !indexIsEligible(index))
            {
                LatestScannedFrames++;
                continue;
            }
            var frame = frames[index];
            _latestById[(frame.Channel, frame.Identifier, frame.IsExtended)] = index;
            LatestScannedFrames++;
        }
        _latestProcessedThrough = end;
        return _latestById.Values.OrderBy(index => index).ToArray();
    }
}
