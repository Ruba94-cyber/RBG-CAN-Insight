namespace RbgCanInsight.Analysis;

/// <summary>Text shown next to the Trace search box so typing, applying and an empty result are always visible.</summary>
public static class TraceFilterFeedback
{
    /// <param name="shown">Rows currently listed.</param>
    /// <param name="total">Frames in the trace.</param>
    /// <param name="filterApplied">A filter (search text confirmed with Enter, or a quick lens) is active.</param>
    /// <param name="editPending">The search box holds text that has not been applied yet.</param>
    public static string Format(int shown, int total, bool filterApplied, bool editPending)
    {
        var text = shown == total
            ? $"{total:N0} frames"
            : shown == 0 && total > 0
                ? $"0 of {total:N0} frames match the filter (Esc clears it)"
                : $"{shown:N0} of {total:N0} frames (filtered)";
        if (shown == total && filterApplied && total > 0)
            text = $"{total:N0} frames (all match the filter)";
        return editPending ? text + " · press Enter to apply the search" : text;
    }
}
