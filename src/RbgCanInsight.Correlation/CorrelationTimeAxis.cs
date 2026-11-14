namespace RbgCanInsight.Correlation;

/// <summary>
/// Source-time projection for recorded DID/CAN evidence. Both transports
/// keep their own timestamps; a statistical lag must never disappear by
/// placing CAN samples at the DID response's timestamp.
/// </summary>
public static class CorrelationTimeAxis
{
    public static (long Start, long End) Bounds(IReadOnlyList<CorrelationPoint> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count == 0)
            return (0, 0);
        var start = long.MaxValue;
        var end = long.MinValue;
        foreach (var point in points)
        {
            start = Math.Min(start, Math.Min(point.DidTimestampNanoseconds,
                point.SignalTimestampNanoseconds));
            end = Math.Max(end, Math.Max(point.DidTimestampNanoseconds,
                point.SignalTimestampNanoseconds));
        }
        return (start, end);
    }

    public static double Fraction(long timestampNanoseconds, long startNanoseconds,
        long endNanoseconds)
    {
        if (endNanoseconds <= startNanoseconds)
            return 0.5d;

        // Convert BEFORE subtraction. A legitimate imported capture could
        // cross extreme signed timestamps; integer subtraction would overflow.
        var fraction = ((double)timestampNanoseconds - startNanoseconds) /
            ((double)endNanoseconds - startNanoseconds);
        return Math.Clamp(fraction, 0d, 1d);
    }
}
