namespace RbgCanInsight.Analysis;

/// <summary>An observed relationship between two distinct source-exact CAN
/// signals. No edge proves an ECU dependency, influence or root cause.</summary>
public sealed record SignalMapEdge(SignalRelationship Evidence)
{
    public SignalTimeline A => Evidence.Reference;
    public SignalTimeline B => Evidence.Candidate;
    public bool SameRecordedMessage =>
        A.Channel == B.Channel &&
        A.SourceIdentifier == B.SourceIdentifier &&
        A.SourceIsExtended == B.SourceIsExtended &&
        A.OriginalSourceIndex == B.OriginalSourceIndex;
    public string ALabel => A.Message.Name + "." + A.Signal.Name;
    public string BLabel => B.Message.Name + "." + B.Signal.Name;
    public string SourceText => $"Logger {A.OriginalSourceIndex + 1}";
    public string TypeText => Evidence.AssociationType +
        (SameRecordedMessage ? " · same recorded CAN message" : "");
    public string StrengthText => Evidence.IsChangeSequenceOnly
        ? $"{Evidence.PairedSamples} ordered changes"
        : $"r {Evidence.LevelText} · Δ {Evidence.ChangeText}";
    public string InvestigationHint => SameRecordedMessage
        ? "Same CAN message: shared sampling; check distinct meanings in DBC"
        : Evidence.IsChangeSequenceOnly
            ? "Different CAN messages: inspect command/state change and source frames"
            : Evidence.HasStrongChangeAgreement
                ? "Different CAN messages: compare changes and operating states"
                : "Different CAN messages: check common drive-cycle trend and sample rate";
    public string OrderText => Evidence.LeadText;
    public string EvidenceText => Evidence.EvidenceText +
        (SameRecordedMessage
            ? " Both signals are decoded from the SAME CAN message; shared " +
              "sampling time alone cannot establish a relationship."
            : " Independent CAN IDs can have a common upstream input.");
}

/// <summary>A connected graph component of evidence links. A-B and B-C do
/// not imply an observed A-C association. Every edge must be reviewed.</summary>
public sealed record SignalMapGroup(
    int Number,
    IReadOnlyList<SignalTimeline> Members,
    IReadOnlyList<SignalMapEdge> Edges)
{
    public string Title => $"Group {Number} · {Members.Count} recorded signals · {Edges.Count} observed links";
    public string SignalNames => string.Join(" · ", Members.Take(5)
        .Select(member => member.Signal.Name)) +
        (Members.Count > 5 ? $" · +{Members.Count - 5} more" : "");
    /// <summary>Highest connected degree (number of individually observed
    /// association links), NOT the strongest influence or causal centrality.</summary>
    public SignalTimeline? SuggestedStartingSignal => Members
        // Prefer diverse observed CAN messages instead of picking a signal
        // just because a single high-density DBC message has many fields.
        .OrderByDescending(member => Edges.Count(edge =>
            !edge.SameRecordedMessage &&
            (ReferenceEquals(edge.A, member) || ReferenceEquals(edge.B, member))))
        .ThenByDescending(member => Edges.Count(edge =>
            ReferenceEquals(edge.A, member) || ReferenceEquals(edge.B, member)))
        .ThenBy(member => member.Key, StringComparer.Ordinal)
        .FirstOrDefault();
    public string StartingPointText => SuggestedStartingSignal is { } signal
        ? $"Suggested first signal: {signal.Message.Name}.{signal.Signal.Name} " +
          "(strongest cross-message link coverage; not proven influence)."
        : "No starting signal available.";
    public string Warning => "Connected signals only; not all pairs necessarily correlate. " +
        "No evidence of physical causality or validated ECU dependency.";
}

/// <summary>One explicitly source-scoped, bounded, automatically discovered
/// network of observable signal relationships.</summary>
public sealed record SignalRelationshipMapReport(
    int OriginalSourceIndex,
    int EligibleSignals,
    int ExaminedSignals,
    int TotalPairsConsidered,
    int TotalEdgesDetected,
    bool SignalSelectionTruncated,
    bool EdgeSelectionTruncated,
    IReadOnlyList<SignalMapGroup> Groups,
    IReadOnlyList<SignalMapEdge> Edges)
{
    public int PreservedOperatorSignals { get; init; }
    /// <summary>Reference signals analysed / of which the level correlation used an evenly spaced subsample.</summary>
    public int ReferenceSignalsAnalyzed { get; init; }
    public int ReferenceSignalsSubsampled { get; init; }
    public int MaxReferenceSamples { get; init; }
    public string Summary => $"Logger {OriginalSourceIndex + 1} · " +
        $"{Groups.Count} groups · {Edges.Count}/{TotalEdgesDetected} individually observed pairwise links displayed · " +
        $"{ExaminedSignals}/{EligibleSignals} activity-screened source-exact signals examined" +
        (SignalSelectionTruncated ? " · bounded selection: other signals not evaluated" : "") +
        (EdgeSelectionTruncated ? " · some weaker links omitted from display" : "") +
        (ReferenceSignalsSubsampled > 0
            ? $" · level correlations used at most {MaxReferenceSamples} evenly spaced samples per reference signal " +
              $"({ReferenceSignalsSubsampled}/{ReferenceSignalsAnalyzed} reference signals were subsampled)"
            : "") +
        (PreservedOperatorSignals > 0
            ? $" · {PreservedOperatorSignals} selected/plotted signal(s) prioritized"
            : "") +
        ". Connected groups are exploration guides, NOT causal diagrams.";
}

/// <summary>
/// Automatically builds small networks of pairwise evidence for one logger.
/// Reuses the signal-to-signal pairing engine instead of matching DBC labels.
/// Only individually verified edges are drawn; components are descriptive,
/// not a causal graph or a transitive statistical assertion.
/// </summary>
public static class SignalRelationshipMapAnalyzer
{
    public static SignalRelationshipMapReport Analyze(
        IReadOnlyList<SignalTimeline> timelines,
        Func<int, int> sourceAt,
        int originalSourceIndex,
        int maxSignals = 36,
        int maxEdges = 120,
        int maxReferenceSamples = 160,
        CancellationToken cancellationToken = default,
        IReadOnlySet<string>? focusSignalKeys = null)
    {
        ArgumentNullException.ThrowIfNull(timelines);
        ArgumentNullException.ThrowIfNull(sourceAt);
        if (originalSourceIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(originalSourceIndex),
                "The physical logger must be selected explicitly.");
        if (maxSignals is < 2 or > 120 || maxEdges is < 1 or > 2000 ||
            maxReferenceSamples is < 32 or > 2048)
            throw new ArgumentOutOfRangeException(nameof(maxSignals));

        // Never infer logger identity from CAN IDs, names, close timestamps
        // or paths. A duplicate series key is ambiguous and excluded.
        var unique = timelines.Where(timeline =>
                timeline.OriginalSourceIndex == originalSourceIndex &&
                timeline.Channel >= 0 && timeline.Samples.Count >= 12)
            .GroupBy(timeline => timeline.Key, StringComparer.Ordinal)
            .Where(group => group.Count() == 1)
            .Select(group => group.Single())
            .ToArray();

        var activity = new List<(SignalTimeline Timeline, int Changes)>();
        foreach (var timeline in unique)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var samples = timeline.Samples;
            var changes = 0;
            var hasVerifiedSource = false;
            // Large MF4/BLF captures can contain millions of observations.
            // Ranking probes at most 2048 DISTINCT adjacent source-frame
            // pairs per timeline rather than scanning every frame in every
            // signal. This is a transparent discovery heuristic; rare short
            // changes may fall outside the probe and need manual selection.
            var pairs = samples.Count - 1;
            const int activityProbeLimit = 2048;
            var probes = Math.Min(pairs, activityProbeLimit);
            for (var step = 0; step < probes; step++)
            {
                if ((step & 127) == 0) cancellationToken.ThrowIfCancellationRequested();
                var j = probes == pairs
                    ? step + 1
                    : 1 + (int)((long)step * (pairs - 1) / (probes - 1));
                var now = samples[j];
                if (!double.IsFinite(now.Value) ||
                    !SourceValid(now.FrameIndex, originalSourceIndex, sourceAt))
                    continue;
                hasVerifiedSource = true;
                var before = samples[j - 1];
                if (double.IsFinite(before.Value) && now.Value != before.Value &&
                    SourceValid(before.FrameIndex, originalSourceIndex, sourceAt))
                    changes++;
            }
            if (hasVerifiedSource && changes >= 3)
                activity.Add((timeline, changes));
        }

        // Strong activity is a transparent discovery choice, not importance
        // or causality. Expose when source cap hides other signals.
        var rankedActivity = activity
            .OrderByDescending(item => item.Changes)
            .ThenBy(item => item.Timeline.Key, StringComparer.Ordinal)
            .ToArray();
        // An operator who explicitly selected a CAN signal or plotted
        // several curves must not lose those active source-exact signals
        // simply because many faster-changing DBC channels outrank them.
        // Retain at most 8 qualified operator interests, then fill the
        // remaining cap by observed activity. Constant or missing-source
        // selections remain ineligible and must not be fabricated.
        var focus = focusSignalKeys is { Count: > 0 }
            ? rankedActivity.Where(item => focusSignalKeys.Contains(item.Timeline.Key))
                .Take(Math.Min(8, maxSignals)).ToArray()
            : Array.Empty<(SignalTimeline Timeline, int Changes)>();
        var pinnedKeys = focus.Select(item => item.Timeline.Key)
            .ToHashSet(StringComparer.Ordinal);
        var selected = focus.Concat(rankedActivity
                .Where(item => !pinnedKeys.Contains(item.Timeline.Key))
                .Take(maxSignals - focus.Length))
            .Select(item => item.Timeline)
            .ToArray();
        var selectionTruncated = activity.Count > selected.Length;
        var edges = new List<SignalMapEdge>();
        var pairCount = 0;
        var referencesAnalyzed = 0;
        var referencesSubsampled = 0;
        for (var i = 0; i < selected.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = selected[i];
            var suffix = selected.Skip(i).ToArray(); // include anchor, avoid duplicate A-B
            if (suffix.Length <= 1) continue;
            var start = Math.Max(0, source.Samples[0].TimestampNanoseconds);
            var end = source.Samples[^1].TimestampNanoseconds;
            if (end <= start) continue;
            var candidateCount = suffix.Length - 1;
            pairCount += candidateCount;

            var results = SignalRelationshipAnalyzer.Analyze(source, suffix,
                sourceAt, start, end, maxCandidates: candidateCount,
                maxReferenceSamples: maxReferenceSamples, cancellationToken: cancellationToken);
            referencesAnalyzed++;
            if (results.ReferenceWasSubsampled) referencesSubsampled++;
            foreach (var relation in results.Results)
            {
                if (!ReferenceEquals(relation.Reference, source) ||
                    relation.Candidate.OriginalSourceIndex != originalSourceIndex)
                    continue;
                edges.Add(new SignalMapEdge(relation));
            }
        }
        // Every real edge is retained as a specific source witness; only
        // top edges are shown for legibility and bounded WPF data binding.
        var sorted = edges
            // For engineering exploration, independent observed CAN
            // messages deserve review before correlated fields that share
            // one physical message/cycle. This is a DISPLAY priority, not
            // a stronger statistical or causal claim.
            .OrderBy(edge => edge.SameRecordedMessage)
            .ThenByDescending(edge =>
                edge.Evidence.IsChangeSequenceOnly || edge.Evidence.HasStrongChangeAgreement)
            .ThenByDescending(edge => edge.Evidence.IsChangeSequenceOnly
                ? edge.Evidence.TransitionEnrichment * 0.85
                : Math.Abs(edge.Evidence.LevelPearson) * 0.65 +
                  Math.Abs(edge.Evidence.ChangePearson ?? 0) * 0.35)
            .ThenByDescending(edge => edge.Evidence.PairedSamples)
            .ThenBy(edge => edge.A.Key, StringComparer.Ordinal)
            .ThenBy(edge => edge.B.Key, StringComparer.Ordinal)
            .Take(maxEdges).ToArray();

        // Connected components of the displayed *observed* edges only.
        // No cross-source, name-based or transitive edges are constructed.
        var adjacency = new Dictionary<string, List<SignalMapEdge>>(StringComparer.Ordinal);
        foreach (var edge in sorted)
        {
            if (!adjacency.TryGetValue(edge.A.Key, out var a))
                adjacency[edge.A.Key] = a = [];
            if (!adjacency.TryGetValue(edge.B.Key, out var b))
                adjacency[edge.B.Key] = b = [];
            a.Add(edge);
            b.Add(edge);
        }
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var groups = new List<SignalMapGroup>();
        foreach (var member in selected.Where(item => adjacency.ContainsKey(item.Key)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!visited.Add(member.Key)) continue;
            var stack = new Stack<string>();
            stack.Push(member.Key);
            var componentEdges = new HashSet<SignalMapEdge>();
            var keys = new HashSet<string>(StringComparer.Ordinal) { member.Key };
            while (stack.Count > 0)
            {
                var current = stack.Pop();
                foreach (var edge in adjacency[current])
                {
                    componentEdges.Add(edge);
                    var other = edge.A.Key == current ? edge.B.Key : edge.A.Key;
                    keys.Add(other);
                    if (visited.Add(other)) stack.Push(other);
                }
            }
            var members = selected.Where(item => keys.Contains(item.Key))
                .OrderBy(item => item.Key, StringComparer.Ordinal).ToArray();
            groups.Add(new SignalMapGroup(0, members,
                sorted.Where(componentEdges.Contains).ToArray()));
        }
        var indexed = groups.OrderByDescending(group => group.Members.Count)
            .ThenByDescending(group => group.Edges.Count)
            .ThenBy(group => group.Members[0].Key, StringComparer.Ordinal)
            .Select((group, index) => group with { Number = index + 1 })
            .ToArray();

        return new SignalRelationshipMapReport(originalSourceIndex,
            activity.Count, selected.Length, pairCount, edges.Count,
            selectionTruncated, edges.Count > sorted.Length, indexed, sorted)
        {
            PreservedOperatorSignals = focus.Length,
            ReferenceSignalsAnalyzed = referencesAnalyzed,
            ReferenceSignalsSubsampled = referencesSubsampled,
            MaxReferenceSamples = maxReferenceSamples
        };
    }

    private static bool SourceValid(int frameIndex, int expected, Func<int, int> sourceAt)
    {
        if (frameIndex < 0) return false;
        try { return sourceAt(frameIndex) == expected; }
        catch (IndexOutOfRangeException) { return false; }
        catch (ArgumentOutOfRangeException) { return false; }
    }
}
