namespace RbgCanInsight.Analysis;

/// <summary>How one plotted analog signal is drawn. Auto follows the plot's global interpolation switch.</summary>
public enum PlotLineStyle
{
    Auto,
    /// <summary>Sample-and-hold steps (the honest CAN rendering).</summary>
    Steps,
    /// <summary>Straight segments between samples (visual interpolation only).</summary>
    Line,
    /// <summary>One dot per recorded sample, no connecting line.</summary>
    Dots
}

/// <summary>Pure helpers for the keyboard-driven plot cursors and per-signal line styles.</summary>
public static class PlotCursorMath
{
    public static PlotLineStyle NextStyle(PlotLineStyle style) => style switch
    {
        PlotLineStyle.Auto => PlotLineStyle.Steps,
        PlotLineStyle.Steps => PlotLineStyle.Line,
        PlotLineStyle.Line => PlotLineStyle.Dots,
        _ => PlotLineStyle.Auto
    };

    public static string StyleLabel(PlotLineStyle style) => style switch
    {
        PlotLineStyle.Auto => "default",
        PlotLineStyle.Steps => "steps (hold)",
        PlotLineStyle.Line => "line",
        _ => "dots"
    };

    /// <summary>Moves a cursor by <paramref name="pixels"/> screen pixels of the visible window, clamped to it.
    /// A cursor that is not set (negative) starts at the window centre.</summary>
    public static long MoveByPixels(long current, long viewMin, long viewMax, double widthPixels, int pixels)
    {
        if (viewMax <= viewMin) return viewMin;
        var start = current < viewMin || current > viewMax ? viewMin + (viewMax - viewMin) / 2 : current;
        var perPixel = (viewMax - viewMin) / Math.Max(1d, widthPixels);
        var moved = start + (long)Math.Round(perPixel * pixels);
        return Math.Clamp(moved, viewMin, viewMax);
    }

    /// <summary>Timestamp of the recorded sample after (direction &gt; 0) or before (direction &lt; 0) the cursor,
    /// or null when there is none in that direction.</summary>
    public static long? NextSampleTime(IReadOnlyList<SignalSample> samples, long current, int direction)
    {
        if (samples.Count == 0) return null;
        var low = 0;
        var high = samples.Count;
        // First index with timestamp > current.
        while (low < high)
        {
            var mid = low + (high - low) / 2;
            if (samples[mid].TimestampNanoseconds <= current) low = mid + 1;
            else high = mid;
        }

        if (direction > 0)
            return low < samples.Count ? samples[low].TimestampNanoseconds : null;

        // Last sample strictly before the cursor.
        var index = low - 1;
        while (index >= 0 && samples[index].TimestampNanoseconds >= current) index--;
        return index >= 0 ? samples[index].TimestampNanoseconds : null;
    }
}
