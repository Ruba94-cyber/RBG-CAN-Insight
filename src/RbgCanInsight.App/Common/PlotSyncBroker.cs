namespace RbgCanInsight.App.Common;

/// <summary>
/// Lightweight event bus that keeps zoom/pan in sync across every open dedicated signal plot
/// window: when the user zooms or pans one, every other open window's plot follows the same
/// trace-time range, so comparing two signals side by side never means zooming each manually.
/// </summary>
public static class PlotSyncBroker
{
    /// <summary>Trace-time start/end (nanoseconds) of the new view range; both -1 means "full
    /// trace". <c>source</c> is the control that originated the change, so it can ignore its own
    /// broadcast instead of reacting to it a second time.</summary>
    public static event Action<long, long, object?>? ZoomChanged;

    public static void NotifyZoomChanged(long startNs, long endNs, object? source) =>
        ZoomChanged?.Invoke(startNs, endNs, source);

    /// <summary>
    /// Shared offline trace-time cursor. Unlike zoom, this is a single instant:
    /// Trace selection, the main plot and dedicated Quick Plots can show
    /// the same physical time without conflating it with measurement cursor X2.
    /// Senders must ignore their own broadcast to prevent echo loops.
    /// </summary>
    public static event Action<long, object?>? CursorChanged;

    public static void NotifyCursorChanged(long timestampNanoseconds, object? source)
    {
        if (timestampNanoseconds >= 0)
            CursorChanged?.Invoke(timestampNanoseconds, source);
    }
}
