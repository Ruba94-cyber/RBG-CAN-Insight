namespace RbgCanInsight.Analysis;

/// <summary>Statistics of one signal between two times (the X1/X2 plot cursors).</summary>
/// <param name="SampleCount">Recorded samples with a timestamp inside [from, to].</param>
/// <param name="Minimum">Smallest value the signal held in the window (includes the value held at <c>from</c>).</param>
/// <param name="Maximum">Largest value the signal held in the window.</param>
/// <param name="Mean">Plain average of the recorded samples inside the window (NaN when there are none).</param>
/// <param name="TimeWeightedMean">Average of the sample-and-hold signal over the window duration.</param>
/// <param name="HeldAtStart">Value held at <c>from</c> (last sample at or before it), when one exists.</param>
public readonly record struct SignalRangeStats(
    int SampleCount, double Minimum, double Maximum, double Mean, double TimeWeightedMean,
    double? HeldAtStart, long FromNanoseconds, long ToNanoseconds)
{
    public double SpanSeconds => (ToNanoseconds - FromNanoseconds) / 1e9;
}

/// <summary>
/// Min / max / mean / time-weighted mean of a recorded signal between two cursor times. CAN samples
/// are sample-and-hold: before the first recorded sample nothing is known and nothing is invented.
/// </summary>
public static class SignalRangeStatistics
{
    /// <summary>Returns null when the signal has no recorded value at or inside the window.</summary>
    public static SignalRangeStats? Compute(IReadOnlyList<SignalSample> samples, long cursorANanoseconds, long cursorBNanoseconds)
    {
        var from = Math.Min(cursorANanoseconds, cursorBNanoseconds);
        var to = Math.Max(cursorANanoseconds, cursorBNanoseconds);
        if (samples.Count == 0) return null;

        // First sample with timestamp >= from.
        var low = 0;
        var high = samples.Count;
        while (low < high)
        {
            var mid = low + (high - low) / 2;
            if (samples[mid].TimestampNanoseconds < from) low = mid + 1;
            else high = mid;
        }

        var firstInside = low;
        double? held = null;
        // Value held at 'from': the sample exactly at 'from', else the one just before it.
        if (firstInside < samples.Count && samples[firstInside].TimestampNanoseconds == from)
            held = Finite(samples[firstInside].Value);
        else if (firstInside > 0)
            held = Finite(samples[firstInside - 1].Value);

        var count = 0;
        var min = double.PositiveInfinity;
        var max = double.NegativeInfinity;
        var sum = 0d;
        var weighted = 0d;
        var cursorTime = from;
        var currentValue = held;
        var knownFrom = held is null ? long.MaxValue : from;
        if (held is { } start)
        {
            min = max = start;
        }

        for (var index = firstInside; index < samples.Count && samples[index].TimestampNanoseconds <= to; index++)
        {
            var sample = samples[index];
            var value = sample.Value;
            if (!double.IsFinite(value)) continue;
            count++;
            knownFrom = Math.Min(knownFrom, sample.TimestampNanoseconds);
            sum += value;
            min = Math.Min(min, value);
            max = Math.Max(max, value);
            if (currentValue is { } previous)
                weighted += previous * (sample.TimestampNanoseconds - cursorTime);
            cursorTime = Math.Max(cursorTime, sample.TimestampNanoseconds);
            currentValue = value;
        }

        if (currentValue is null) return null;
        weighted += currentValue.Value * (to - cursorTime);

        // Time before the first recorded sample is unknown; weight only the known part.
        var knownSpan = to - knownFrom;
        var timeWeighted = knownSpan > 0 ? weighted / knownSpan : currentValue.Value;

        return new SignalRangeStats(count, min, max, count > 0 ? sum / count : double.NaN, timeWeighted,
            held, from, to);
    }

    private static double? Finite(double value) => double.IsFinite(value) ? value : null;
}
