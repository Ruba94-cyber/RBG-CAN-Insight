namespace RbgCanInsight.Correlation;

/// <summary>
/// An immutable, sorted index over both real DID and CAN timestamps for
/// interactive chart hit-testing. Selection remains based on ORIGINAL
/// observation pairs, not reduced display vertices. Once built, the scan
/// is limited to observations whose projected time lies near the click.
/// </summary>
public sealed class CorrelationPointHitIndex
{
    private readonly record struct Marker(long TimestampNanoseconds, int PairIndex, bool IsDid);

    private readonly IReadOnlyList<CorrelationPoint> _points;
    private readonly Marker[] _byTimestamp;

    public CorrelationPointHitIndex(IReadOnlyList<CorrelationPoint> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        _points = points;
        if (points.Count > int.MaxValue / 2)
            throw new ArgumentOutOfRangeException(nameof(points), "Too many evidence pairs to index.");

        _byTimestamp = new Marker[points.Count * 2];
        for (var index = 0; index < points.Count; index++)
        {
            var pair = points[index];
            _byTimestamp[2 * index] = new Marker(pair.DidTimestampNanoseconds, index, true);
            _byTimestamp[2 * index + 1] = new Marker(pair.SignalTimestampNanoseconds, index, false);
        }
        Array.Sort(_byTimestamp, (left, right) =>
        {
            var time = left.TimestampNanoseconds.CompareTo(right.TimestampNanoseconds);
            return time != 0 ? time : left.PairIndex.CompareTo(right.PairIndex);
        });
    }

    public int FindNearest(
        double clickX, double clickY,
        long startNs, long endNs,
        double left, double right, double top, double bottom,
        double didMin, double didMax, double canMin, double canMax,
        double radiusPixels = 18d)
    {
        if (_byTimestamp.Length == 0 || !double.IsFinite(clickX) ||
            !double.IsFinite(clickY) || radiusPixels <= 0 ||
            right <= left || bottom <= top || !double.IsFinite(radiusPixels))
            return -1;

        double Position(long timestamp) =>
            left + CorrelationTimeAxis.Fraction(timestamp, startNs, endNs) * (right - left);

        static double Y(double value, double minimum, double maximum,
            double topCoordinate, double bottomCoordinate)
        {
            var range = maximum - minimum;
            var normalized = range > 0 && double.IsFinite(range)
                ? (value - minimum) / range : 0.5d;
            if (!double.IsFinite(normalized))
                normalized = 0.5d;
            return bottomCoordinate - Math.Clamp(normalized, 0d, 1d) *
                (bottomCoordinate - topCoordinate);
        }

        var firstX = clickX - radiusPixels;
        var lastX = clickX + radiusPixels;
        var low = 0;
        var high = _byTimestamp.Length;
        while (low < high)
        {
            var midpoint = low + (high - low) / 2;
            if (Position(_byTimestamp[midpoint].TimestampNanoseconds) < firstX)
                low = midpoint + 1;
            else
                high = midpoint;
        }

        var radiusSquared = radiusPixels * radiusPixels;
        var bestDistanceSquared = radiusSquared;
        var nearest = -1;
        for (var index = low; index < _byTimestamp.Length; index++)
        {
            var marker = _byTimestamp[index];
            var x = Position(marker.TimestampNanoseconds);
            if (x > lastX)
                break;
            var pair = _points[marker.PairIndex];
            var y = marker.IsDid
                ? Y(pair.DidValue, didMin, didMax, top, bottom)
                : Y(pair.SignalValue, canMin, canMax, top, bottom);
            var dx = x - clickX;
            var dy = y - clickY;
            var distanceSquared = dx * dx + dy * dy;
            if (distanceSquared >= bestDistanceSquared)
                continue;
            bestDistanceSquared = distanceSquared;
            nearest = marker.PairIndex;
        }
        return nearest;
    }
}
