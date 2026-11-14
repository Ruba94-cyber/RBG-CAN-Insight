namespace RbgCanInsight.Analysis;

/// <summary>
/// Event predicates over two precisely scoped decoded signal timelines.
/// ThenWithin requires strictly increasing observed timestamps: a CAN file's
/// order of frames with equal timestamp cannot prove a real physical sequence.
/// BothWithin means two event observations occur within a declared tolerance,
/// not that two ECU states were simultaneously true.
/// </summary>
public enum SignalTriggerSequenceMode
{
    ThenWithin,
    BothWithin
}

public sealed record SignalTriggerSequenceRule(
    SignalTriggerRule First,
    SignalTriggerRule Second,
    SignalTriggerSequenceMode Mode,
    long MinObservedDelayNanoseconds,
    long MaxObservedDelayNanoseconds,
    long PreContextNanoseconds = 0,
    long PostContextNanoseconds = 0);

public sealed record SignalTriggerSequenceEvent(
    SignalTriggerEvent First,
    SignalTriggerEvent Second,
    long ObservedDelayNanoseconds,
    long ContextStartNanoseconds,
    long ContextEndNanoseconds)
{
    public int FirstSourceFrameIndex => First.CurrentFrameIndex;
    public int SecondSourceFrameIndex => Second.CurrentFrameIndex;
    public long FirstObservationNanoseconds => First.CurrentTimestampNanoseconds;
    public long SecondObservationNanoseconds => Second.CurrentTimestampNanoseconds;
    public string Interpretation =>
        "Observed trigger pair only; actual ECU switching instants are unknown and bounded " +
        "by their respective previous/current source observations.";
}

public sealed record SignalTriggerSequenceReport(
    SignalTriggerSequenceRule Rule,
    IReadOnlyList<SignalTriggerSequenceEvent> Events,
    long MatchedPairs,
    long OmittedPairs,
    long FirstInputMatches,
    long SecondInputMatches,
    long FirstInputOmitted,
    long SecondInputOmitted,
    string? FirstWarning,
    string? SecondWarning)
{
    public bool IsComplete => FirstInputOmitted == 0 && SecondInputOmitted == 0 &&
                              OmittedPairs == 0 && FirstWarning is null && SecondWarning is null;
    public string Summary =>
        $"{MatchedPairs:N0} retained-input pairs · {Events.Count:N0} displayed" +
        (IsComplete ? "" : " · partial evidence (source/event limit reached)");
}

/// <summary>
/// Deterministic one-to-one temporal pairing. The earliest available B
/// satisfying each observed A is consumed at most once. Input cap and output
/// cap are explicit; never claim global completeness if source events were
/// truncated. Complexity: O(n log n) sorting and O(n) temporal merge.
/// </summary>
public static class SignalTriggerSequenceAnalyzer
{
    public const int MaxInputEventsPerSignal = 100_000;
    public const int MaxOutputPairs = 25_000;
    public const long MaxSearchWindowNanoseconds = 24L * 60 * 60 * 1_000_000_000;
    public const long MaxContextNanoseconds = 60L * 60 * 1_000_000_000;

    public static SignalTriggerSequenceReport Evaluate(
        IReadOnlyList<SignalTimeline> timelines,
        SignalTriggerSequenceRule rule,
        int maxPairs = MaxOutputPairs,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(timelines);
        ArgumentNullException.ThrowIfNull(rule);
        cancellationToken.ThrowIfCancellationRequested();
        Validate(rule, maxPairs);

        var firstMatches = timelines.Where(item => item.Key == rule.First.SignalKey)
            .Take(2).ToArray();
        var secondMatches = timelines.Where(item => item.Key == rule.Second.SignalKey)
            .Take(2).ToArray();
        if (firstMatches.Length != 1 || secondMatches.Length != 1)
            throw new ArgumentException("Both triggers need one uniquely resolved source-scoped timeline. " +
                "No fallback across databases, CAN channels or 11/29-bit arbitration formats is allowed.",
                nameof(rule));

        var first = SignalTriggerAnalyzer.Evaluate(firstMatches[0], rule.First,
            maxEvents: MaxInputEventsPerSignal, cancellationToken: cancellationToken);
        var second = SignalTriggerAnalyzer.Evaluate(secondMatches[0], rule.Second,
            maxEvents: MaxInputEventsPerSignal, cancellationToken: cancellationToken);
        return Pair(rule, first, second, maxPairs, cancellationToken);
    }

    /// <summary>Pairs previously evaluated bounded reports for testable and
    /// auditable logic without recomputing the underlying decoded samples.</summary>
    public static SignalTriggerSequenceReport Pair(
        SignalTriggerSequenceRule rule,
        SignalTriggerReport first,
        SignalTriggerReport second,
        int maxPairs = MaxOutputPairs,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);
        cancellationToken.ThrowIfCancellationRequested();
        Validate(rule, maxPairs);
        if (first.SignalKey != rule.First.SignalKey || second.SignalKey != rule.Second.SignalKey ||
            first.Mode != rule.First.Mode || second.Mode != rule.Second.Mode)
            throw new ArgumentException("Sequence inputs must match both source-scoped trigger rules exactly.");

        var eventsA = first.Events.OrderBy(item => item.CurrentTimestampNanoseconds)
            .ThenBy(item => item.CurrentFrameIndex).ToArray();
        var eventsB = second.Events.OrderBy(item => item.CurrentTimestampNanoseconds)
            .ThenBy(item => item.CurrentFrameIndex).ToArray();

        // One-to-one earliest-eligible consumption is deterministic and
        // avoids inventing N×M combinations for highly active signals.
        var output = new List<SignalTriggerSequenceEvent>(Math.Min(maxPairs, 1024));
        long matched = 0;
        var cursorB = 0;

        for (var i = 0; i < eventsA.Length; i++)
        {
            if ((i & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
            var a = eventsA[i];
            var start = rule.Mode == SignalTriggerSequenceMode.ThenWithin
                ? SaturatingAdd(a.CurrentTimestampNanoseconds, rule.MinObservedDelayNanoseconds)
                : SaturatingAdd(a.CurrentTimestampNanoseconds, -rule.MaxObservedDelayNanoseconds);
            var end = SaturatingAdd(a.CurrentTimestampNanoseconds, rule.MaxObservedDelayNanoseconds);
            while (cursorB < eventsB.Length &&
                   eventsB[cursorB].CurrentTimestampNanoseconds < start)
                cursorB++;

            // A THEN B is not provable when timestamp A==B. Skip identical
            // observation instants rather than inferring order from row index.
            if (rule.Mode == SignalTriggerSequenceMode.ThenWithin)
                while (cursorB < eventsB.Length &&
                       eventsB[cursorB].CurrentTimestampNanoseconds <=
                       a.CurrentTimestampNanoseconds)
                    cursorB++;

            // A single source frame cannot simultaneously be both independent
            // stages of a same-signal co-occurrence. Continue searching for the
            // earliest distinct B rather than dropping this otherwise valid A.
            if (rule.First.SignalKey == rule.Second.SignalKey)
            {
                while (cursorB < eventsB.Length &&
                       eventsB[cursorB].CurrentTimestampNanoseconds <= end &&
                       eventsB[cursorB].CurrentFrameIndex == a.CurrentFrameIndex)
                    cursorB++;
            }
            if (cursorB >= eventsB.Length) break;
            var b = eventsB[cursorB];
            if (b.CurrentTimestampNanoseconds > end) continue;

            // All rule fields bounded below 24 h, so this difference is safe
            // after verifying that both observations lie in the search window.
            var delta = (decimal)b.CurrentTimestampNanoseconds -
                a.CurrentTimestampNanoseconds;
            if (delta < long.MinValue || delta > long.MaxValue)
                continue;

            matched++;
            if (output.Count < maxPairs)
            {
                var firstObserved = Math.Min(a.CurrentTimestampNanoseconds,
                    b.CurrentTimestampNanoseconds);
                var lastObserved = Math.Max(a.CurrentTimestampNanoseconds,
                    b.CurrentTimestampNanoseconds);
                output.Add(new SignalTriggerSequenceEvent(
                    a, b, (long)delta,
                    SaturatingAdd(firstObserved, -rule.PreContextNanoseconds),
                    SaturatingAdd(lastObserved, rule.PostContextNanoseconds)));
            }
            cursorB++; // B is consumed; no fabricated repeated pair ownership.
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new SignalTriggerSequenceReport(rule, output, matched,
            matched - output.Count, first.MatchedEvents, second.MatchedEvents,
            first.OmittedEvents, second.OmittedEvents, first.Warning, second.Warning);
    }

    public static void Validate(SignalTriggerSequenceRule rule, int maxPairs)
    {
        ArgumentNullException.ThrowIfNull(rule);
        if (rule.First is null || rule.Second is null ||
            string.IsNullOrWhiteSpace(rule.First.SignalKey) ||
            string.IsNullOrWhiteSpace(rule.Second.SignalKey))
            throw new ArgumentException("Both source-scoped signal rules must be supplied.");
        if (!Enum.IsDefined(rule.Mode))
            throw new ArgumentOutOfRangeException(nameof(rule), "Unknown temporal predicate.");
        if (rule.MinObservedDelayNanoseconds < 0 ||
            rule.MaxObservedDelayNanoseconds < rule.MinObservedDelayNanoseconds ||
            rule.MaxObservedDelayNanoseconds > MaxSearchWindowNanoseconds ||
            rule.PreContextNanoseconds is < 0 or > MaxContextNanoseconds ||
            rule.PostContextNanoseconds is < 0 or > MaxContextNanoseconds)
            throw new ArgumentException("Invalid sequence delay/context range.");
        if (rule.Mode == SignalTriggerSequenceMode.BothWithin &&
            rule.MinObservedDelayNanoseconds != 0)
            throw new ArgumentException("BothWithin is symmetric and requires minimum delay zero.");
        if (maxPairs is < 0 or > MaxOutputPairs)
            throw new ArgumentOutOfRangeException(nameof(maxPairs));
    }

    private static long SaturatingAdd(long value, long offset) =>
        offset > 0 && value > long.MaxValue - offset ? long.MaxValue :
        offset < 0 && value < long.MinValue - offset ? long.MinValue :
        value + offset;
}
