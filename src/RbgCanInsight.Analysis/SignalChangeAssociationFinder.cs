namespace RbgCanInsight.Analysis;

/// <summary>Observed order of independent decoded CAN value changes. A repeated
/// event sequence is a navigation clue, never proof of signal influence.</summary>
public sealed record SignalChangeAssociation(
    int ReferenceTransitions,
    int CandidateTransitions,
    int MatchedTransitions,
    long MedianCandidateLeadNanoseconds,
    double ExpectedRandomOverlapFraction,
    SignalSample ReferenceWitness,
    SignalSample CandidateWitness)
{
    public double ObservedMatchFraction => (double)MatchedTransitions / ReferenceTransitions;
    public string Description => $"{MatchedTransitions}/{ReferenceTransitions} reference changes " +
        $"follow a distinct candidate change by up to 1s (observed {ObservedMatchFraction:P0}, " +
        $"simple frequency baseline ~{ExpectedRandomOverlapFraction:P0}). " +
        "This is a temporal-search hint only, NOT physical influence or cause.";
}

/// <summary>
/// Finds timing-linked signal transitions even without linear value
/// correlation (e.g., a binary switch toggling before a measured response).
/// Rejects dense/high-base-rate and poorly supported event patterns.
/// </summary>
public static class SignalChangeAssociationFinder
{
    public static SignalChangeAssociation? Find(
        SignalTimeline reference,
        SignalTimeline candidate,
        Func<int, int> sourceAt,
        long startNs,
        long endNs,
        long maxLeadNs = 1_000_000_000L,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(sourceAt);
        if (endNs <= startNs || startNs < 0 || maxLeadNs <= 0 ||
            maxLeadNs > 30_000_000_000L)
            throw new ArgumentOutOfRangeException(nameof(maxLeadNs));
        if (reference.OriginalSourceIndex < 0 ||
            reference.OriginalSourceIndex != candidate.OriginalSourceIndex ||
            ReferenceEquals(reference, candidate) ||
            reference.Samples.Count > 50_000 || candidate.Samples.Count > 50_000)
            return null;

        var original = reference.OriginalSourceIndex;
        var refs = Transitions(reference.Samples, startNs, endNs, original, sourceAt, token);
        var cands = Transitions(candidate.Samples, startNs, endNs, original, sourceAt, token);
        if (refs.Count < 5 || cands.Count < 5) return null;
        var duration = (double)endNs - startNs;
        var baseline = Math.Min(1d, cands.Count * (double)maxLeadNs / duration);
        if (baseline >= 0.4) return null; // most times already near an event: no meaningful lead evidence

        var matches = new List<(SignalSample Ref, SignalSample Candidate)>();
        var j = 0;
        var lastUsed = -1;
        foreach (var target in refs)
        {
            token.ThrowIfCancellationRequested();
            while (j < cands.Count && cands[j].TimestampNanoseconds <= target.TimestampNanoseconds)
                j++;
            var index = j - 1;
            if (index <= lastUsed || index < 0) continue;
            var prior = cands[index];
            var elapsed = target.TimestampNanoseconds - prior.TimestampNanoseconds;
            if (elapsed > maxLeadNs) continue;
            matches.Add((target, prior));
            lastUsed = index;
        }
        if (matches.Count < 4 ||
            matches.Count < Math.Ceiling(refs.Count * 0.6) ||
            matches.Count / (double)refs.Count < baseline * 2)
            return null;

        var leads = matches.Select(item =>
            item.Ref.TimestampNanoseconds - item.Candidate.TimestampNanoseconds)
            .OrderBy(item => item).ToArray();
        var witness = matches[matches.Count / 2];
        return new SignalChangeAssociation(refs.Count, cands.Count, matches.Count,
            leads[leads.Length / 2], baseline, witness.Ref, witness.Candidate);
    }

    private static IReadOnlyList<SignalSample> Transitions(
        IReadOnlyList<SignalSample> samples, long startNs, long endNs,
        int source, Func<int, int> sourceAt, CancellationToken token)
    {
        var result = new List<SignalSample>();
        for (var i = 1; i < samples.Count; i++)
        {
            if ((i & 2047) == 0) token.ThrowIfCancellationRequested();
            var previous = samples[i - 1];
            var current = samples[i];
            if (current.TimestampNanoseconds < startNs || current.TimestampNanoseconds > endNs ||
                !double.IsFinite(previous.Value) || !double.IsFinite(current.Value) ||
                current.Value == previous.Value ||
                !SourceAt(previous.FrameIndex, source, sourceAt) ||
                !SourceAt(current.FrameIndex, source, sourceAt))
                continue;
            result.Add(current);
        }
        return result;
    }

    private static bool SourceAt(int frameIndex, int expected, Func<int, int> sourceAt)
    {
        if (frameIndex < 0) return false;
        try { return sourceAt(frameIndex) == expected; }
        catch (IndexOutOfRangeException) { return false; }
        catch (ArgumentOutOfRangeException) { return false; }
    }
}
