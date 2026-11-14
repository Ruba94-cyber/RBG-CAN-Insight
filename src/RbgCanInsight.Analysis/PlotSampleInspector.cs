namespace RbgCanInsight.Analysis;

/// <summary>
/// Source-backed point-in-time reading for a CAN plot. A sample at/before
/// the cursor is explicitly a held last observation, never an interpolated
/// measurement. Change witnesses use two ADJACENT original decoded frames.
/// </summary>
public sealed record PlotSampleInspection(
    long CursorNanoseconds,
    SignalSample? LastObserved,
    SignalSample? ChangeBefore,
    SignalSample? ChangeAfter)
{
    /// <summary>Conservative raw reconstruction for LastObserved only. Raw CAN bytes are not
    /// retained in the timeline, so unsupported conversions intentionally return null.</summary>
    public double? RawValue { get; init; }
    public string? DbcState { get; init; }
    public bool HasChange => ChangeBefore is not null && ChangeAfter is not null;
    public bool AtOriginalSample => LastObserved is { } sample &&
        sample.TimestampNanoseconds == CursorNanoseconds;
    public long? ObservationAgeNanoseconds => LastObserved is { } observed
        ? NonNegativeDifference(CursorNanoseconds, observed.TimestampNanoseconds)
        : null;
    public double? Delta => HasChange
        ? ChangeAfter!.Value.Value - ChangeBefore!.Value.Value
        : null;
    public bool CandidateChangeIsAfterCursor => ChangeAfter is { } change &&
        change.TimestampNanoseconds > CursorNanoseconds;
    public string CursorTimeText => FormatTimestamp(CursorNanoseconds);
    public string ChangeTimeText => ChangeAfter is { } change
        ? FormatTimestamp(change.TimestampNanoseconds) : "—";
    public string LastObservedTimeText => LastObserved is { } sample
        ? FormatTimestamp(sample.TimestampNanoseconds) : "not yet observed";
    public string ChangeBeforeTimeText => ChangeBefore is { } sample
        ? FormatTimestamp(sample.TimestampNanoseconds) : "—";
    public string ChangeAfterTimeText => ChangeAfter is { } sample
        ? FormatTimestamp(sample.TimestampNanoseconds) : "—";
    public string ObservedTimeText => LastObservedTimeText;
    public string FrameText => LastObserved is { FrameIndex: >= 0 } sample
        ? $"frame #{sample.FrameIndex + 1:N0}" : "source frame unavailable";
    public string ChangeFrameText => ChangeAfter is { FrameIndex: >= 0 } sample
        ? $"frame #{sample.FrameIndex + 1:N0}" : "source frame unavailable";

    private static string FormatTimestamp(long nanoseconds)
    {
        var negative = nanoseconds < 0;
        var magnitude = negative
            ? (ulong)(-(nanoseconds + 1)) + 1UL
            : (ulong)nanoseconds;
        var seconds = magnitude / 1_000_000_000UL;
        var fraction = magnitude % 1_000_000_000UL;
        return string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{(negative ? "-" : string.Empty)}{seconds}.{fraction:D9} s");
    }

    private static long NonNegativeDifference(long later, long earlier)
    {
        if (later <= earlier) return 0;
        if (earlier < 0 && later > long.MaxValue + earlier) return long.MaxValue;
        return later - earlier;
    }
}

/// <summary>
/// Fast, bounded inspection of real recorded source samples. No full-trace
/// search on mouse motion, no matching samples by DBC name, no synthetic
/// timestamps and no nearest-FUTURE value in the cursor readout.
/// </summary>
public static class PlotSampleInspector
{
    public static PlotSampleInspection Inspect(
        IReadOnlyList<SignalSample> samples,
        long cursorNanoseconds,
        long nearbyChangeNanoseconds,
        int maxChangeSamples = 96,
        RbgCanInsight.Dbc.DbcSignal? signal = null)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (nearbyChangeNanoseconds < 0)
            throw new ArgumentOutOfRangeException(nameof(nearbyChangeNanoseconds));
        if (maxChangeSamples < 2 || maxChangeSamples > 10_000)
            throw new ArgumentOutOfRangeException(nameof(maxChangeSamples));

        var upper = UpperBound(samples, cursorNanoseconds);
        SignalSample? last = upper == 0 ? null : samples[upper - 1];
        if (samples.Count <= 1)
            return WithDecodedValue(new PlotSampleInspection(cursorNanoseconds, last, null, null), signal);

        // Even on million-frame traces inspect only a bounded number of
        // ADJACENT original frames around the hovered timestamp. Looking
        // farther is a deliberate zoom-in / detailed analysis action.
        var beforeBudget = maxChangeSamples / 2;
        var afterBudget = maxChangeSamples - beforeBudget;
        var begin = Math.Max(1, upper - beforeBudget);
        var end = Math.Min(samples.Count, upper + afterBudget);
        var bestDistance = ulong.MaxValue;
        SignalSample? changeBefore = null;
        SignalSample? changeAfter = null;
        for (var i = begin; i < end; i++)
        {
            var a = samples[i - 1];
            var b = samples[i];
            if (!double.IsFinite(a.Value) || !double.IsFinite(b.Value) ||
                a.Value == b.Value || a.FrameIndex < 0 || b.FrameIndex < 0)
                continue;
            var distance = AbsoluteDistance(b.TimestampNanoseconds, cursorNanoseconds);
            // Identical timestamps may still refer to distinct CAN frames:
            // prefer the latest source frame on a distance tie.
            if (distance > (ulong)nearbyChangeNanoseconds || distance > bestDistance) continue;
            bestDistance = distance;
            changeBefore = a;
            changeAfter = b;
        }
        return WithDecodedValue(new PlotSampleInspection(cursorNanoseconds, last,
            changeBefore, changeAfter), signal);
    }

    private static PlotSampleInspection WithDecodedValue(
        PlotSampleInspection inspection, RbgCanInsight.Dbc.DbcSignal? signal)
    {
        if (signal is null || inspection.LastObserved is not { } sample ||
            !DbcSignalRawMath.TryReconstructRaw(signal, sample.Value, out var raw))
            return inspection;
        return inspection with
        {
            RawValue = raw,
            DbcState = signal.GetValueDescription(raw)
        };
    }

    private static ulong AbsoluteDistance(long left, long right)
    {
        // Flip the sign bit to map signed integers onto monotonically ordered
        // unsigned keys, then subtract without overflow or double precision loss.
        const ulong signBit = 0x8000_0000_0000_0000UL;
        var leftKey = unchecked((ulong)left) ^ signBit;
        var rightKey = unchecked((ulong)right) ^ signBit;
        return leftKey >= rightKey ? leftKey - rightKey : rightKey - leftKey;
    }

    /// <summary>Return a small, evenly distributed subset of true recorded
    /// transitions that survived the plot's visual decimation. Never derive
    /// dots from differences between nonadjacent decimated samples: those
    /// differences may have occurred at omitted original CAN frames.</summary>
    public static SignalSample[] ChangeDots(
        IReadOnlyList<SignalSample> original,
        IReadOnlyList<SignalSample> visibleDecimated,
        int maxDots = 28)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(visibleDecimated);
        if (maxDots is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(maxDots));
        if (original.Count < 2 || visibleDecimated.Count == 0) return [];

        // Index only the source timestamp groups represented in the visible
        // decimated data. A timestamp may map to many CAN frames, so scan each
        // such source group once instead of searching backward separately for
        // every visible point (which could become quadratic).
        var wantedByTimestamp = visibleDecimated
            .GroupBy(point => point.TimestampNanoseconds)
            .ToDictionary(group => group.Key,
                group => group.Select(point => point.FrameIndex).ToHashSet());
        var matchesBySourceFrame = new Dictionary<(long Timestamp, int Frame), SignalSample>();
        foreach (var (timestamp, wantedFrames) in wantedByTimestamp)
        {
            var first = LowerBound(original, timestamp);
            var end = UpperBound(original, timestamp);
            for (var i = first; i < end; i++)
            {
                var current = original[i];
                if (i == 0 || !wantedFrames.Contains(current.FrameIndex) ||
                    !double.IsFinite(current.Value) ||
                    !double.IsFinite(original[i - 1].Value) ||
                    current.Value == original[i - 1].Value)
                    continue;
                matchesBySourceFrame[(timestamp, current.FrameIndex)] = current;
            }
        }

        var matches = visibleDecimated
            .Where(point => matchesBySourceFrame.ContainsKey(
                (point.TimestampNanoseconds, point.FrameIndex)))
            .Select(point => matchesBySourceFrame[(point.TimestampNanoseconds, point.FrameIndex)])
            .ToList();
        if (matches.Count <= maxDots) return matches.ToArray();

        var reduced = new SignalSample[maxDots];
        if (maxDots == 1) { reduced[0] = matches[matches.Count / 2]; return reduced; }
        for (var i = 0; i < maxDots; i++)
            reduced[i] = matches[(int)((long)i * (matches.Count - 1) / (maxDots - 1))];
        return reduced;
    }

    public static int UpperBound(IReadOnlyList<SignalSample> samples, long timestamp)
    {
        ArgumentNullException.ThrowIfNull(samples);
        var lo = 0;
        var hi = samples.Count;
        while (lo < hi)
        {
            var mid = lo + (hi - lo) / 2;
            if (samples[mid].TimestampNanoseconds <= timestamp) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    private static int LowerBound(IReadOnlyList<SignalSample> samples, long timestamp)
    {
        var lo = 0;
        var hi = samples.Count;
        while (lo < hi)
        {
            var mid = lo + (hi - lo) / 2;
            if (samples[mid].TimestampNanoseconds < timestamp) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }
}
