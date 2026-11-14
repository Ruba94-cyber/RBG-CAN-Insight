namespace RbgCanInsight.Analysis;

public enum SignalOverlayVerdict
{
    /// <summary>Every compared sample is equal to the reference value.</summary>
    Identical,
    /// <summary>Differences exist but none exceeds the requested tolerance.</summary>
    WithinTolerance,
    Different,
    /// <summary>No loaded-trace sample had a recent enough reference sample to compare with.</summary>
    NoOverlap
}

/// <summary>Time origins that map each trace onto a common relative axis (usually each trace's
/// first frame). The comparison never warps or stretches time: only a constant offset is applied.</summary>
public readonly record struct OverlayOrigins(long ReferenceNanoseconds, long SubjectNanoseconds);

public readonly record struct OverlayChartPoint(double RelativeSeconds, double Value);

/// <summary>One compared sample of the loaded (subject) trace with the reference value in force
/// at the same relative time. Both frame indices point at original source frames.</summary>
public sealed record SignalOverlayDifference(
    double RelativeSeconds,
    double ReferenceValue,
    double SubjectValue,
    double Delta,
    int SubjectFrameIndex,
    int ReferenceFrameIndex);

public sealed record SignalOverlayResult(
    SignalOverlayVerdict Verdict,
    int ReferenceSamples,
    int SubjectSamples,
    int ComparedSamples,
    int UncomparedSamples,
    double MaxAbsDelta,
    double MeanAbsDelta,
    double RootMeanSquareDelta,
    double MeanDelta,
    SignalOverlayDifference? Largest,
    IReadOnlyList<SignalOverlayDifference> LargestDifferences,
    IReadOnlyList<OverlayChartPoint> ReferenceChart,
    IReadOnlyList<OverlayChartPoint> SubjectChart,
    double HoldLimitMilliseconds,
    string Alignment)
{
    public double Coverage => SubjectSamples == 0 ? 0 : (double)ComparedSamples / SubjectSamples;
}

/// <summary>
/// Overlays one decoded signal of the loaded trace on the same signal of a reference trace.
/// Reference value at a loaded-trace sample = the last reference sample at or before the same
/// relative time (step-hold; signals are discrete messages, so nothing is interpolated), and only
/// if it is not older than a hold limit. Samples without a recent reference are counted as
/// uncompared, never silently dropped or filled. Pure and deterministic.
/// </summary>
public static class SignalOverlayComparer
{
    public const int DefaultLargestCount = 200;
    public const int DefaultChartPoints = 2000;

    public static SignalOverlayResult Compare(
        IReadOnlyList<SignalSample> reference,
        IReadOnlyList<SignalSample> subject,
        OverlayOrigins origins,
        double valueTolerance = 0,
        int largestCount = DefaultLargestCount,
        int chartPoints = DefaultChartPoints)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(subject);
        if (double.IsNaN(valueTolerance) || valueTolerance < 0)
            throw new ArgumentOutOfRangeException(nameof(valueTolerance));

        var holdLimitNs = HoldLimit(reference);
        var compared = new List<SignalOverlayDifference>(subject.Count);
        double sumAbs = 0, sumSq = 0, sum = 0;
        foreach (var sample in subject)
        {
            var relative = sample.TimestampNanoseconds - origins.SubjectNanoseconds;
            var target = relative + origins.ReferenceNanoseconds;
            var index = LastAtOrBefore(reference, target);
            if (index < 0 || target - reference[index].TimestampNanoseconds > holdLimitNs) continue;
            var delta = sample.Value - reference[index].Value;
            compared.Add(new SignalOverlayDifference(relative / 1e9, reference[index].Value, sample.Value, delta,
                sample.FrameIndex, reference[index].FrameIndex));
            var abs = Math.Abs(delta);
            sumAbs += abs;
            sumSq += delta * delta;
            sum += delta;
        }

        var max = compared.Count == 0 ? 0 : compared.Max(item => Math.Abs(item.Delta));
        var verdict = compared.Count == 0 ? SignalOverlayVerdict.NoOverlap
            : max == 0 ? SignalOverlayVerdict.Identical
            : max <= valueTolerance ? SignalOverlayVerdict.WithinTolerance
            : SignalOverlayVerdict.Different;
        var largest = compared.OrderByDescending(item => Math.Abs(item.Delta))
            .ThenBy(item => item.RelativeSeconds).Take(Math.Max(0, largestCount)).ToArray();
        var holdMs = holdLimitNs == long.MaxValue ? double.PositiveInfinity : holdLimitNs / 1e6;
        return new SignalOverlayResult(
            verdict, reference.Count, subject.Count, compared.Count, subject.Count - compared.Count,
            max,
            compared.Count == 0 ? 0 : sumAbs / compared.Count,
            compared.Count == 0 ? 0 : Math.Sqrt(sumSq / compared.Count),
            compared.Count == 0 ? 0 : sum / compared.Count,
            largest.FirstOrDefault(), largest,
            Chart(reference, origins.ReferenceNanoseconds, chartPoints),
            Chart(subject, origins.SubjectNanoseconds, chartPoints),
            holdMs,
            "Aligned by a constant offset between the chosen origins (normally each trace's first frame); " +
            "reference value = last reference sample at or before the same relative time" +
            (double.IsInfinity(holdMs) ? "." : $" and not older than {holdMs:G4} ms.") +
            " No interpolation or time warping; differences describe what changed, not why.");
    }

    public static SignalOverlayResult Compare(
        SignalTimeline reference, SignalTimeline subject, OverlayOrigins origins,
        double valueTolerance = 0) =>
        Compare(reference.Samples, subject.Samples, origins, valueTolerance);

    /// <summary>Ten times the median reference period (a held value older than that no longer
    /// represents the signal). A reference with fewer than two samples has no period: only an exact
    /// timestamp match is accepted.</summary>
    private static long HoldLimit(IReadOnlyList<SignalSample> reference)
    {
        if (reference.Count < 2) return 0;
        var gaps = new List<long>(reference.Count - 1);
        for (var i = 1; i < reference.Count; i++)
        {
            var gap = reference[i].TimestampNanoseconds - reference[i - 1].TimestampNanoseconds;
            if (gap > 0) gaps.Add(gap);
        }
        if (gaps.Count == 0) return 0;
        gaps.Sort();
        return Math.Max(1_000_000, gaps[gaps.Count / 2] * 10);
    }

    private static int LastAtOrBefore(IReadOnlyList<SignalSample> samples, long timestamp)
    {
        int low = 0, high = samples.Count - 1, found = -1;
        while (low <= high)
        {
            var mid = low + (high - low) / 2;
            if (samples[mid].TimestampNanoseconds <= timestamp) { found = mid; low = mid + 1; }
            else high = mid - 1;
        }
        return found;
    }

    /// <summary>Min/max-per-bucket decimation keeps every peak visible while bounding point count.</summary>
    private static IReadOnlyList<OverlayChartPoint> Chart(
        IReadOnlyList<SignalSample> samples, long origin, int maxPoints)
    {
        if (samples.Count == 0 || maxPoints <= 0) return [];
        if (samples.Count <= maxPoints)
            return samples.Select(s => new OverlayChartPoint((s.TimestampNanoseconds - origin) / 1e9, s.Value)).ToArray();
        var buckets = Math.Max(1, maxPoints / 2);
        var result = new List<OverlayChartPoint>(maxPoints + 2);
        for (var bucket = 0; bucket < buckets; bucket++)
        {
            var start = (int)((long)samples.Count * bucket / buckets);
            var end = (int)((long)samples.Count * (bucket + 1) / buckets);
            if (end <= start) continue;
            var low = start;
            var high = start;
            for (var i = start + 1; i < end; i++)
            {
                if (samples[i].Value < samples[low].Value) low = i;
                if (samples[i].Value > samples[high].Value) high = i;
            }
            foreach (var index in new[] { low, high }.Distinct().Order())
                result.Add(new OverlayChartPoint((samples[index].TimestampNanoseconds - origin) / 1e9, samples[index].Value));
        }
        return result;
    }
}
