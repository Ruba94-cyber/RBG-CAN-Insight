namespace RbgCanInsight.Analysis;

/// <summary>
/// Advances the virtual CAN trace cursor by elapsed wall-clock time, independently
/// of plot-window width, frame density and timer callback scheduling.
/// A sparse recorded signal must hold its last value rather than fast-forwarding
/// to the next timestamp merely to create an animation.
/// </summary>
public static class OfflineReplayClock
{
    public static long Advance(long cursorNanoseconds, long endNanoseconds,
        TimeSpan elapsed, double playbackSpeed)
    {
        if (cursorNanoseconds >= endNanoseconds)
            return endNanoseconds;
        if (elapsed <= TimeSpan.Zero || !double.IsFinite(playbackSpeed) || playbackSpeed <= 0)
            return cursorNanoseconds;

        var step = elapsed.TotalSeconds * playbackSpeed * 1_000_000_000d;
        // Check against the available duration *before* converting to Int64.
        // Huge pause/suspension durations must never wrap a timestamp.
        var remaining = endNanoseconds - cursorNanoseconds;
        if (step >= remaining)
            return endNanoseconds;
        if (step < 1)
            return cursorNanoseconds;
        // Round nanoseconds rather than truncate floating-point noise: for
        // example 180ms × 10x may be represented as 1,799,999,999.9999998.
        return cursorNanoseconds + (long)Math.Round(step, MidpointRounding.AwayFromZero);
    }
}
