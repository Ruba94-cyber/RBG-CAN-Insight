using System.Globalization;

namespace RbgCanInsight.Analysis;

/// <summary>Association between TWO separately recorded DBC signal timelines.
/// A statistical association or positive observation lag is NOT a physical
/// dependency, calibration threshold, ECU implementation or causality.</summary>
public sealed record SignalRelationship(
    SignalTimeline Reference,
    SignalTimeline Candidate,
    long CandidateLeadNanoseconds,
    double LevelPearson,
    double? ChangePearson,
    int PairedSamples,
    int ReferenceChanges,
    int CandidateChanges,
    int ReferenceFrameIndex,
    int CandidateFrameIndex,
    long ReferenceTimestampNanoseconds,
    long CandidateTimestampNanoseconds)
{
    public bool IsChangeSequenceOnly { get; init; }
    /// <summary>Reference samples inside the analysed window (level-correlation stage; 0 = not applicable,
    /// e.g. change-sequence hints, which scan the whole selected window).</summary>
    public int ReferenceSamplesInWindow { get; init; }
    /// <summary>Reference samples actually used by the level correlation (evenly spaced, bounded).</summary>
    public int ReferenceSamplesAnalyzed { get; init; }
    public bool ReferenceWasSubsampled => ReferenceSamplesInWindow > ReferenceSamplesAnalyzed;
    public double TransitionEnrichment { get; init; }
    public string TransitionDescription { get; init; } = string.Empty;
    /// <summary>First-difference correlation must be materially strong
    /// before the UI calls the observed changes co-varying. A numerical
    /// Pearson value alone does not establish coherent dynamics.</summary>
    public bool HasStrongChangeAgreement =>
        !IsChangeSequenceOnly && ChangePearson is { } delta &&
        double.IsFinite(delta) && Math.Abs(delta) >= 0.65;
    public string AssociationType => IsChangeSequenceOnly
        ? "Change sequence (lead hint)"
        : HasStrongChangeAgreement
            ? "Values and changes co-vary"
            : ChangePearson is null
                ? "Similar levels · changes unavailable"
                : "Similar levels · change evidence weak";
    public string SignalText => Candidate.Message.Name + "." + Candidate.Signal.Name;
    public string SuggestedNextCheck => IsChangeSequenceOnly
        ? "Observed prior state/change · verify documented ECU logic"
        : !HasStrongChangeAgreement
            ? "Values trend together; changes do not strongly agree · check for a shared drive-cycle trend"
            : CandidateLeadNanoseconds > 0
                ? "Earlier CAN variation · review as possible precursor"
                : CandidateLeadNanoseconds < 0
                    ? "Later CAN variation · review as possible response"
                    : "Strongly co-varying measurements · check common inputs and DBC meaning";
    public string SourceText => $"Logger {Candidate.OriginalSourceIndex + 1} · " +
        (Candidate.Channel < 0 ? "CAN bus unknown" : $"CAN {Candidate.Channel + 1}") + " · " +
        $"0x{Candidate.SourceIdentifier.ToString(Candidate.SourceIsExtended ? "X8" : "X3", CultureInfo.InvariantCulture)}";
    public string LevelText => IsChangeSequenceOnly ? "not evaluated" :
        LevelPearson.ToString("+0.000;-0.000;0.000", CultureInfo.InvariantCulture);
    public string ChangeText => IsChangeSequenceOnly ? "not linear" : ChangePearson is { } change
        ? change.ToString("+0.000;-0.000;0.000", CultureInfo.InvariantCulture)
        : "insufficient independent changes";
    public string LeadText => CandidateLeadNanoseconds switch
    {
        > 0 => $"Candidate precedes reference by ~{CandidateLeadNanoseconds / 1e6:F0} ms",
        < 0 => $"Candidate follows reference by ~{-CandidateLeadNanoseconds / 1e6:F0} ms",
        _ => "Same-time observations (within sampling tolerance)"
    };
    public string EvidenceText => IsChangeSequenceOnly
        ? TransitionDescription
        : $"{PairedSamples} distinct paired observed CAN frames · " +
        $"{ReferenceChanges} reference / {CandidateChanges} candidate changes. " +
        $"{LeadText}. " +
        (HasStrongChangeAgreement
            ? "First differences also correlate, but this does NOT prove influence."
            : "Similarity is predominantly in the values; first-difference evidence is weak or unavailable. " +
              "This can be an operating-point or shared-time trend, not a dependency.");
    public string OriginalFrames => $"Ref #{ReferenceFrameIndex + 1} / candidate #{CandidateFrameIndex + 1}";
}

public sealed record SignalRelationshipReport(
    SignalTimeline Reference,
    long WindowStartNanoseconds,
    long WindowEndNanoseconds,
    long PairToleranceNanoseconds,
    int CandidateCountScanned,
    bool Truncated,
    IReadOnlyList<SignalRelationship> Results)
{
    /// <summary>Reference samples inside the selected window.</summary>
    public int ReferenceSamplesInWindow { get; init; }
    /// <summary>Reference samples actually analysed (evenly spaced, bounded).</summary>
    public int ReferenceSamplesAnalyzed { get; init; }
    /// <summary>Distinct candidate signals available before the candidate limit.</summary>
    public int CandidatesAvailable { get; init; }

    public bool ReferenceWasSubsampled => ReferenceSamplesInWindow > ReferenceSamplesAnalyzed;

    /// <summary>Explicit statement of every reduction applied to the data (empty when nothing was reduced).
    /// Results must never hide a subsample or a candidate cut-off.</summary>
    public string SamplingText
    {
        get
        {
            var parts = new List<string>();
            if (ReferenceWasSubsampled)
                parts.Add($"analysed {ReferenceSamplesAnalyzed} of {ReferenceSamplesInWindow} reference samples in the window " +
                          (ReferenceSamplesInWindow > ReferenceSamplesAnalyzed && ReferenceSamplesAnalyzed > 0
                              ? "(evenly spaced subsample, or invalid/other-source samples excluded)"
                              : "(no usable samples)"));
            if (Truncated)
                parts.Add($"evaluated {CandidateCountScanned} of {CandidatesAvailable} candidate signals (candidate limit reached)");
            return parts.Count == 0 ? string.Empty : "Data reduction: " + string.Join("; ", parts) + ".";
        }
    }

    public string Summary => (Results.Count == 0
        ? $"No source-supported level or sparse change-timing associations in Logger {Reference.OriginalSourceIndex + 1}. " +
          "Try a wider trace-time range or tolerance, or inspect original DBC sources."
        : $"{Results.Count} exploratory signal associations · {CandidateCountScanned} distinct source signals screened" +
          (Truncated ? " (candidate limit reached)" : "") +
          " · level correlations do not prove influence or a DTC trigger.") +
        (SamplingText.Length > 0 ? " " + SamplingText : string.Empty);
}

/// <summary>
/// Fast, bounded offline screen for co-varying decoded signals, independent
/// of any DTC. Uses *recorded* CAN frames, never synthetic interpolated values,
/// repeated held samples, different physical loggers or guessed ECU identities.
/// Scanning lag values is exploratory; it cannot establish causal direction.
/// </summary>
public static class SignalRelationshipAnalyzer
{
    public static SignalRelationshipReport Analyze(
        SignalTimeline reference,
        IReadOnlyList<SignalTimeline> timelines,
        Func<int, int> originalSourceAt,
        long windowStartNanoseconds,
        long windowEndNanoseconds,
        long toleranceNanoseconds = 250_000_000L,
        long maxLagNanoseconds = 1_000_000_000L,
        int maxCandidates = 800,
        int maxReferenceSamples = 512,
        int minPairs = 12,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(timelines);
        ArgumentNullException.ThrowIfNull(originalSourceAt);
        if (reference.OriginalSourceIndex < 0)
            throw new InvalidOperationException("Reference signal has no verified original physical logger.");
        if (windowStartNanoseconds < 0 || windowEndNanoseconds <= windowStartNanoseconds)
            throw new ArgumentOutOfRangeException(nameof(windowEndNanoseconds));
        if (toleranceNanoseconds <= 0 || toleranceNanoseconds > 5_000_000_000L)
            throw new ArgumentOutOfRangeException(nameof(toleranceNanoseconds));
        if (maxLagNanoseconds < 0 || maxLagNanoseconds > 30_000_000_000L)
            throw new ArgumentOutOfRangeException(nameof(maxLagNanoseconds));
        if (maxCandidates < 1 || maxReferenceSamples < 16 || minPairs < 5 || minPairs > maxReferenceSamples)
            throw new ArgumentOutOfRangeException(nameof(minPairs));

        var original = reference.OriginalSourceIndex;
        var unique = timelines
            .Where(item => item.OriginalSourceIndex == original && item.Samples.Count >= minPairs)
            .GroupBy(item => item.Key, StringComparer.Ordinal)
            .Where(group => group.Count() == 1)
            .Select(group => group.Single())
            .ToArray();
        if (!unique.Any(item => ReferenceEquals(item, reference)))
            throw new InvalidOperationException("Reference signal is missing, duplicated or ambiguous in the current decoded source.");

        cancellationToken.ThrowIfCancellationRequested();
        // Binary-search the actual selected time slice, then pull at most
        // 512 ORIGINAL frame references directly. Do not materialize a
        // million-sample array only to throw all but 512 points away.
        var (sampled, samplesInWindow) = SampleReferenceWindow(reference.Samples,
            windowStartNanoseconds, windowEndNanoseconds, maxReferenceSamples,
            original, originalSourceAt, cancellationToken);

        var candidates = unique.Where(item => !ReferenceEquals(item, reference)).ToArray();
        var truncated = candidates.Length > maxCandidates;
        var lagSteps = BuildLagSteps(maxLagNanoseconds);
        var results = new List<SignalRelationship>();
        foreach (var candidate in candidates.Take(maxCandidates))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (candidate.Samples.Count < minPairs) continue;
            SignalRelationship? best = null;
            var bestQuality = double.NegativeInfinity;
            foreach (var lag in lagSteps)
            {
                var paired = new List<(SignalSample Ref, SignalSample Candidate)>(sampled.Length);
                var lastCandidateIndex = -1;
                for (var i = 0; i < sampled.Length; i++)
                {
                    if ((i & 127) == 0) cancellationToken.ThrowIfCancellationRequested();
                    var a = sampled[i];
                    var requested = a.TimestampNanoseconds - lag;
                    if (requested < 0) continue;
                    var pos = Nearest(candidate.Samples, requested);
                    if (pos <= lastCandidateIndex) continue; // no repeated held-value evidence
                    var b = candidate.Samples[pos];
                    if (Math.Abs((double)b.TimestampNanoseconds - requested) > toleranceNanoseconds ||
                        b.TimestampNanoseconds < windowStartNanoseconds ||
                        b.TimestampNanoseconds > windowEndNanoseconds ||
                        !double.IsFinite(b.Value) ||
                        !SameSource(b.FrameIndex, original, originalSourceAt))
                        continue;
                    // For positive lead the candidate source frame must precede
                    // the reference; for negative lead it must follow. Do not
                    // invert a direction because of frame timestamp jitter.
                    if ((lag > 0 && b.TimestampNanoseconds >= a.TimestampNanoseconds) ||
                        (lag < 0 && b.TimestampNanoseconds <= a.TimestampNanoseconds))
                        continue;
                    paired.Add((a, b));
                    lastCandidateIndex = pos;
                }
                if (paired.Count < minPairs) continue;
                var levels = Pearson(paired.Select(pair => (pair.Ref.Value, pair.Candidate.Value)));
                if (levels is not { } level) continue;
                var changesA = 0;
                var changesB = 0;
                var deltas = new List<(double X, double Y)>(paired.Count - 1);
                for (var i = 1; i < paired.Count; i++)
                {
                    var da = paired[i].Ref.Value - paired[i - 1].Ref.Value;
                    var db = paired[i].Candidate.Value - paired[i - 1].Candidate.Value;
                    if (!double.IsFinite(da) || !double.IsFinite(db)) continue;
                    if (da != 0) changesA++;
                    if (db != 0) changesB++;
                    deltas.Add((da, db));
                }
                if (changesA < 3 || changesB < 3) continue;
                var changes = deltas.Count >= minPairs - 1 ? Pearson(deltas) : null;
                // Hundreds of candidate channels and multiple lag settings
                // inflate chance matches: reject weak small-sample fits.
                if (Math.Abs(level) < 0.72) continue;
                var quality = Math.Abs(level) * 0.65 +
                    (changes is { } delta ? Math.Abs(delta) * 0.35 : 0) +
                    Math.Min(0.02, paired.Count / 10_000d);
                if (quality <= bestQuality) continue;
                var witness = paired[paired.Count / 2];
                bestQuality = quality;
                best = new SignalRelationship(reference, candidate, lag, level,
                    changes, paired.Count, changesA, changesB,
                    witness.Ref.FrameIndex, witness.Candidate.FrameIndex,
                    witness.Ref.TimestampNanoseconds, witness.Candidate.TimestampNanoseconds)
                {
                    ReferenceSamplesInWindow = samplesInWindow,
                    ReferenceSamplesAnalyzed = sampled.Length
                };
            }
            if (best is not null)
            {
                results.Add(best);
                continue;
            }

            // Binary state/command transitions can precede a response without
            // having a strong LINEAR value correlation. Treat only sparse,
            // enriched, distinct original-frame change sequences as hints.
            var sequence = SignalChangeAssociationFinder.Find(reference, candidate,
                originalSourceAt, windowStartNanoseconds, windowEndNanoseconds,
                Math.Max(maxLagNanoseconds, 1_000_000_000L), cancellationToken);
            if (sequence is not null)
            {
                results.Add(new SignalRelationship(reference, candidate,
                    sequence.MedianCandidateLeadNanoseconds, 0, null,
                    sequence.MatchedTransitions, sequence.ReferenceTransitions,
                    sequence.CandidateTransitions, sequence.ReferenceWitness.FrameIndex,
                    sequence.CandidateWitness.FrameIndex,
                    sequence.ReferenceWitness.TimestampNanoseconds,
                    sequence.CandidateWitness.TimestampNanoseconds)
                {
                    IsChangeSequenceOnly = true,
                    TransitionDescription = sequence.Description,
                    TransitionEnrichment = Math.Max(0,
                        sequence.ObservedMatchFraction - sequence.ExpectedRandomOverlapFraction)
                });
            }
        }
        var ranked = results.OrderByDescending(item =>
                item.IsChangeSequenceOnly
                    ? item.TransitionEnrichment * 0.85
                    : Math.Abs(item.LevelPearson) * 0.65 +
                      (item.ChangePearson is { } delta ? Math.Abs(delta) * 0.35 : 0))
            .ThenByDescending(item => item.PairedSamples)
            .ThenBy(item => item.Candidate.Key, StringComparer.Ordinal)
            .Take(120).ToArray();
        return new SignalRelationshipReport(reference, windowStartNanoseconds,
            windowEndNanoseconds, toleranceNanoseconds,
            Math.Min(candidates.Length, maxCandidates), truncated, ranked)
        {
            ReferenceSamplesInWindow = samplesInWindow,
            ReferenceSamplesAnalyzed = sampled.Length,
            CandidatesAvailable = candidates.Length
        };
    }

    private static (SignalSample[] Items, int InWindow) SampleReferenceWindow(
        IReadOnlyList<SignalSample> samples, long fromNs, long toNs,
        int limit, int physicalSource, Func<int, int> sourceAt,
        CancellationToken token)
    {
        int LowerBound(long value)
        {
            var lo = 0;
            var hi = samples.Count;
            while (lo < hi)
            {
                var mid = lo + (hi - lo) / 2;
                if (samples[mid].TimestampNanoseconds < value) lo = mid + 1;
                else hi = mid;
            }
            return lo;
        }
        int UpperBound(long value)
        {
            var lo = 0;
            var hi = samples.Count;
            while (lo < hi)
            {
                var mid = lo + (hi - lo) / 2;
                if (samples[mid].TimestampNanoseconds <= value) lo = mid + 1;
                else hi = mid;
            }
            return lo;
        }
        var start = LowerBound(fromNs);
        var end = UpperBound(toNs);
        var count = end - start;
        var items = new List<SignalSample>(Math.Min(count, limit));
        for (var i = 0; i < Math.Min(count, limit); i++)
        {
            if ((i & 127) == 0) token.ThrowIfCancellationRequested();
            var offset = count <= limit ? i : (int)((long)i * (count - 1) / (limit - 1));
            var sample = samples[start + offset];
            if (double.IsFinite(sample.Value) &&
                SameSource(sample.FrameIndex, physicalSource, sourceAt))
                items.Add(sample);
        }
        return (items.ToArray(), count);
    }

    private static IReadOnlyList<long> BuildLagSteps(long maxLag)
    {
        if (maxLag == 0) return [0L];
        // Include fixed fractional offsets rather than an unbounded sweep.
        // Larger lags can show coincident time shifts but not directed effects.
        var offsets = new SortedSet<long> { 0L, maxLag };
        foreach (var fraction in new[] { 0.1, 0.25, 0.5, 0.75 })
            offsets.Add((long)(maxLag * fraction));
        return offsets.SelectMany(offset => offset == 0 ? [0L] : new[] { -offset, offset })
            .Distinct().ToArray();
    }

    private static int Nearest(IReadOnlyList<SignalSample> samples, long time)
    {
        var low = 0;
        var high = samples.Count;
        while (low < high)
        {
            var mid = low + (high - low) / 2;
            if (samples[mid].TimestampNanoseconds < time) low = mid + 1;
            else high = mid;
        }
        if (low == 0) return 0;
        if (low == samples.Count) return low - 1;
        return (double)time - samples[low - 1].TimestampNanoseconds <=
               (double)samples[low].TimestampNanoseconds - time ? low - 1 : low;
    }

    private static double? Pearson(IEnumerable<(double X, double Y)> values)
    {
        long n = 0;
        double meanX = 0, meanY = 0, covariance = 0, varX = 0, varY = 0;
        foreach (var (x, y) in values)
        {
            if (!double.IsFinite(x) || !double.IsFinite(y)) continue;
            n++;
            var dx = x - meanX;
            meanX += dx / n;
            var dy = y - meanY;
            meanY += dy / n;
            covariance += dx * (y - meanY);
            varX += dx * (x - meanX);
            varY += dy * (y - meanY);
        }
        if (n < 3 || varX <= 1e-18 || varY <= 1e-18) return null;
        var result = covariance / Math.Sqrt(varX * varY);
        return double.IsFinite(result) ? Math.Clamp(result, -1d, 1d) : null;
    }

    private static bool SameSource(int frameIndex, int expected, Func<int, int> sourceAt)
    {
        if (frameIndex < 0) return false;
        try { return sourceAt(frameIndex) == expected; }
        catch (ArgumentOutOfRangeException) { return false; }
        catch (IndexOutOfRangeException) { return false; }
    }
}
