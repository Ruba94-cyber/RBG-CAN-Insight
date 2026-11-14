namespace RbgCanInsight.Analysis;

/// <summary>An explicit pair of ORIGINAL recorded CAN value changes, not a
/// reconstructed continuous response and not proof of ECU influence.</summary>
public sealed record SignalChangeWitness(
    SignalSample ReferenceBefore,
    SignalSample ReferenceAfter,
    SignalSample CandidateBefore,
    SignalSample CandidateAfter)
{
    /// <summary>Positive means the candidate change preceded reference.</summary>
    public long CandidateLeadNanoseconds =>
        ReferenceAfter.TimestampNanoseconds - CandidateAfter.TimestampNanoseconds;
    public double ReferenceDelta => ReferenceAfter.Value - ReferenceBefore.Value;
    public double CandidateDelta => CandidateAfter.Value - CandidateBefore.Value;
    public string TimingText => CandidateLeadNanoseconds switch
    {
        > 0 => $"candidate before by {CandidateLeadNanoseconds / 1e6:F1} ms",
        < 0 => $"candidate after by {-CandidateLeadNanoseconds / 1e6:F1} ms",
        _ => "same recorded timestamp"
    };
    public string ReferenceChangeText =>
        $"{ReferenceBefore.Value:G7} → {ReferenceAfter.Value:G7} (Δ {ReferenceDelta:+0.####;-0.####;0})";
    public string CandidateChangeText =>
        $"{CandidateBefore.Value:G7} → {CandidateAfter.Value:G7} (Δ {CandidateDelta:+0.####;-0.####;0})";
    public string OriginalFrameText =>
        $"Reference #{ReferenceAfter.FrameIndex + 1}; candidate #{CandidateAfter.FrameIndex + 1}";
}

public sealed record SignalChangeWitnessReport(
    SignalTimeline Reference,
    SignalTimeline Candidate,
    IReadOnlyList<SignalChangeWitness> Witnesses,
    int ReferenceTransitions,
    int CandidateTransitions,
    bool Truncated,
    string? Limitation = null)
{
    public string Summary => Limitation is not null
        ? Limitation
        : $"{Witnesses.Count} independently paired recorded transitions; " +
          $"{ReferenceTransitions} reference and {CandidateTransitions} candidate transitions inspected" +
          (Truncated ? " · capped, more transitions not inspected" : "") +
          ". Event ordering is an observation, NOT evidence of ECU influence or physical causality.";
}

/// <summary>
/// On-demand source-qualified transition inspection for one candidate edge.
/// Unlike correlation, this shows two explicit before/after source frames
/// for every matched change. Each candidate transition can be used once;
/// there is no interpolation, zero-fill, sample hold or cross-logger join.
/// </summary>
public static class SignalChangeWitnessFinder
{
    public static SignalChangeWitnessReport Find(
        SignalTimeline reference,
        SignalTimeline candidate,
        Func<int, int> originalSourceAt,
        long maxDistanceNanoseconds = 1_000_000_000L,
        int maxWitnesses = 60,
        int maxTransitionsPerSignal = 20_000,
        CancellationToken token = default,
        long? centerTimestampNanoseconds = null,
        long windowHalfWidthNanoseconds = 30_000_000_000L)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(originalSourceAt);
        if (maxDistanceNanoseconds < 0 || maxDistanceNanoseconds > 30_000_000_000L)
            throw new ArgumentOutOfRangeException(nameof(maxDistanceNanoseconds));
        if (maxWitnesses is < 1 or > 1000 || maxTransitionsPerSignal is < 2 or > 100_000)
            throw new ArgumentOutOfRangeException(nameof(maxWitnesses));
        if (windowHalfWidthNanoseconds <= 0 ||
            windowHalfWidthNanoseconds > 3_600_000_000_000L ||
            centerTimestampNanoseconds is < 0)
            throw new ArgumentOutOfRangeException(nameof(windowHalfWidthNanoseconds));
        if (reference.OriginalSourceIndex < 0 ||
            reference.OriginalSourceIndex != candidate.OriginalSourceIndex ||
            ReferenceEquals(reference, candidate))
            return new SignalChangeWitnessReport(reference, candidate, [], 0, 0,
                false, "No two distinct signals with the same verified physical logger.");

        var start = Math.Max(reference.Samples.Count == 0 ? 0 : reference.Samples[0].TimestampNanoseconds,
            candidate.Samples.Count == 0 ? 0 : candidate.Samples[0].TimestampNanoseconds);
        var end = Math.Min(reference.Samples.Count == 0 ? -1 : reference.Samples[^1].TimestampNanoseconds,
            candidate.Samples.Count == 0 ? -1 : candidate.Samples[^1].TimestampNanoseconds);
        if (centerTimestampNanoseconds is { } center)
        {
            // A 10-million-frame trace can still inspect the 60 seconds
            // surrounding the actual source witness. Original frame indices
            // remain intact: only the binary-search view range is narrowed.
            var lower = center >= windowHalfWidthNanoseconds
                ? center - windowHalfWidthNanoseconds : 0;
            var upper = center > long.MaxValue - windowHalfWidthNanoseconds
                ? long.MaxValue : center + windowHalfWidthNanoseconds;
            start = Math.Max(start, lower);
            end = Math.Min(end, upper);
        }
        if (end <= start)
            return new SignalChangeWitnessReport(reference, candidate, [], 0, 0,
                false, "No shared recorded-time window for these two source-qualified signals.");

        var source = reference.OriginalSourceIndex;
        // Hard cap applies to the CURRENT TIME SLICE, not the full file.
        // The linked view is usable on multi-million-frame recordings.
        const int maxSourceSamples = 250_000;
        if (CountInWindow(reference.Samples, start, end) > maxSourceSamples ||
            CountInWindow(candidate.Samples, start, end) > maxSourceSamples)
            return new SignalChangeWitnessReport(reference, candidate, [], 0, 0, true,
                "More than 250,000 recorded samples occur in the selected event window. " +
                "Choose a shorter period before inspecting individual changes.");
        var refs = Extract(reference.Samples, start, end, source,
            originalSourceAt, maxTransitionsPerSignal, token);
        var candidates = Extract(candidate.Samples, start, end, source,
            originalSourceAt, maxTransitionsPerSignal, token);
        var output = new List<SignalChangeWitness>();
        var lastUsed = -1;
        var cursor = 0;
        foreach (var referenceChange in refs.Items)
        {
            token.ThrowIfCancellationRequested();
            var timestamp = referenceChange.After.TimestampNanoseconds;
            // At most two nearby actual recorded candidate transitions are
            // tested; choose the nearest unused one in absolute time.
            while (cursor < candidates.Items.Count &&
                   candidates.Items[cursor].After.TimestampNanoseconds < timestamp)
                cursor++;
            var previous = cursor - 1;
            var next = cursor;
            int chosen = -1;
            var distance = long.MaxValue;
            foreach (var index in new[] { previous, next })
            {
                if (index <= lastUsed || index < 0 || index >= candidates.Items.Count) continue;
                var other = candidates.Items[index].After.TimestampNanoseconds;
                var gap = Math.Abs((double)timestamp - other);
                if (gap > maxDistanceNanoseconds || gap >= distance) continue;
                distance = (long)gap;
                chosen = index;
            }
            if (chosen < 0) continue;
            var match = candidates.Items[chosen];
            output.Add(new SignalChangeWitness(referenceChange.Before,
                referenceChange.After, match.Before, match.After));
            lastUsed = chosen;
            // Preserve chronological order of source observations.
            if (output.Count >= maxWitnesses) break;
        }
        var reachedMax = output.Count >= maxWitnesses && refs.Items.Count > output.Count;
        return new SignalChangeWitnessReport(reference, candidate, output,
            refs.Items.Count, candidates.Items.Count,
            refs.Truncated || candidates.Truncated || reachedMax,
            output.Count == 0
                ? "No distinct original-frame transitions were paired within the selected time tolerance. " +
                  "A correlation of values can still exist without near-synchronous changes."
                : null);
    }

    private sealed record Transition(SignalSample Before, SignalSample After);
    private sealed record TransitionSet(IReadOnlyList<Transition> Items, bool Truncated);

    private static int LowerBound(IReadOnlyList<SignalSample> samples, long timeNs)
    {
        var low = 0;
        var high = samples.Count;
        while (low < high)
        {
            var mid = low + (high - low) / 2;
            if (samples[mid].TimestampNanoseconds < timeNs) low = mid + 1;
            else high = mid;
        }
        return low;
    }

    private static int UpperBound(IReadOnlyList<SignalSample> samples, long timeNs)
    {
        var low = 0;
        var high = samples.Count;
        while (low < high)
        {
            var mid = low + (high - low) / 2;
            if (samples[mid].TimestampNanoseconds <= timeNs) low = mid + 1;
            else high = mid;
        }
        return low;
    }

    private static int CountInWindow(IReadOnlyList<SignalSample> samples,
        long startNs, long endNs) =>
        Math.Max(0, UpperBound(samples, endNs) - LowerBound(samples, startNs));

    private static TransitionSet Extract(
        IReadOnlyList<SignalSample> samples, long startNs, long endNs, int logger,
        Func<int, int> sourceAt, int limit, CancellationToken token)
    {
        var result = new List<Transition>(Math.Min(limit, 512));
        var truncated = false;
        var lower = Math.Max(1, LowerBound(samples, startNs));
        var upper = UpperBound(samples, endNs);
        for (var i = lower; i < upper; i++)
        {
            if ((i & 2047) == 0) token.ThrowIfCancellationRequested();
            var before = samples[i - 1];
            var after = samples[i];
            if (after.TimestampNanoseconds < startNs || after.TimestampNanoseconds > endNs ||
                before.TimestampNanoseconds < startNs ||
                !double.IsFinite(before.Value) || !double.IsFinite(after.Value) ||
                before.Value == after.Value ||
                !SourceValid(before.FrameIndex, logger, sourceAt) ||
                !SourceValid(after.FrameIndex, logger, sourceAt))
                continue;
            if (result.Count >= limit)
            {
                truncated = true;
                break;
            }
            result.Add(new Transition(before, after));
        }
        return new TransitionSet(result, truncated);
    }

    private static bool SourceValid(int index, int expected, Func<int, int> sourceAt)
    {
        if (index < 0) return false;
        try { return sourceAt(index) == expected; }
        catch (IndexOutOfRangeException) { return false; }
        catch (ArgumentOutOfRangeException) { return false; }
    }
}
