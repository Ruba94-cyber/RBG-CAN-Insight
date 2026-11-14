namespace RbgCanInsight.Correlation;

/// <summary>
/// Finds the exact pair of recorded CAN/diagnostic observations used in an
/// offline statistical correlation. Indices are from the original imported
/// trace, never the chart's pixel-reduced display cache.
/// </summary>
public static class CorrelationEvidenceNavigator
{
    /// <summary>
    /// Returns the first pair referring to a raw source frame, whether it is
    /// the CAN signal frame, the decoded DID response terminal frame or any
    /// segment of the original ISO-TP response.
    /// </summary>
    public static int FindPairForFrame(
        IReadOnlyList<CorrelationPoint> points, int frameIndex)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (frameIndex < 0) return -1;
        for (var i = 0; i < points.Count; i++)
        {
            var point = points[i];
            if (point.SignalFrameIndex == frameIndex ||
                point.DidFrameIndex == frameIndex ||
                point.DidResponseFrameIndices?.Contains(frameIndex) == true)
                return i;
        }
        return -1;
    }

    /// <summary>
    /// Focus the most recent source DID observation from an explicitly chosen
    /// DID field/endpoint group. Never use only the numeric DID: the same DID
    /// may legitimately appear in multiple ECUs and multiplexed fields.
    /// </summary>
    public static int FindLastDidPair(
        IReadOnlyList<CorrelationPoint> points, IReadOnlySet<int> didFrameIndices)
    {
        ArgumentNullException.ThrowIfNull(points);
        ArgumentNullException.ThrowIfNull(didFrameIndices);
        for (var i = points.Count - 1; i >= 0; i--)
        {
            var point = points[i];
            if (didFrameIndices.Contains(point.DidFrameIndex) ||
                point.DidResponseFrameIndices?.Any(didFrameIndices.Contains) == true)
                return i;
        }
        return -1;
    }
}
