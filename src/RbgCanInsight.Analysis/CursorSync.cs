namespace RbgCanInsight.Analysis;

/// <summary>Helpers that keep several synchronized panes on one shared cursor (a source-frame index
/// or a timestamp): each pane finds the item of its own kind that is closest to the cursor.</summary>
public static class CursorSync
{
    /// <summary>Position in <paramref name="sortedKeys"/> whose key is nearest to
    /// <paramref name="target"/>, or -1 for an empty list. On an exact tie the earlier item wins, so
    /// the result never jumps forward in time past an equally close earlier event.</summary>
    public static int NearestPosition(IReadOnlyList<long> sortedKeys, long target)
    {
        if (sortedKeys.Count == 0) return -1;
        var low = 0;
        var high = sortedKeys.Count - 1;
        while (low < high)
        {
            var mid = low + (high - low) / 2;
            if (sortedKeys[mid] < target) low = mid + 1;
            else high = mid;
        }

        // low is the first key >= target; compare it with its predecessor.
        if (low > 0 && target - sortedKeys[low - 1] <= sortedKeys[low] - target) return low - 1;
        return low;
    }

    /// <summary>Like <see cref="NearestPosition"/> for items that are not already sorted: returns the
    /// index into <paramref name="items"/> of the item whose key is nearest to the target, or -1.</summary>
    public static int NearestItem<T>(IReadOnlyList<T> items, Func<T, long> key, long target)
    {
        var best = -1;
        var bestDistance = long.MaxValue;
        var bestKey = long.MaxValue;
        for (var i = 0; i < items.Count; i++)
        {
            var value = key(items[i]);
            var distance = Math.Abs(value - target);
            if (distance < bestDistance || distance == bestDistance && value < bestKey)
            {
                best = i;
                bestDistance = distance;
                bestKey = value;
            }
        }

        return best;
    }
}
