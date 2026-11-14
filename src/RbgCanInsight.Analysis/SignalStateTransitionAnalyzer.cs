using RbgCanInsight.Dbc;

namespace RbgCanInsight.Analysis;

/// <summary>Two consecutive, independently observed named states in a decoded
/// offline timeline. This does NOT assert when the ECU actually transitioned.</summary>
public sealed record SignalStateTransition(
    string SignalKey,
    string Message,
    string Signal,
    string PreviousDeclaredState,
    string CurrentDeclaredState,
    long LastPreviousObservationNanoseconds,
    long FirstCurrentObservationNanoseconds,
    int PreviousSourceFrameIndex,
    int CurrentSourceFrameIndex,
    long PreviousStateObservedSpanNanoseconds,
    long TransitionTimeUncertaintyNanoseconds);

public sealed record SignalStateTransitionReport(
    IReadOnlyList<SignalStateTransition> Transitions,
    long TotalTransitions,
    long OmittedTransitions,
    int UnsupportedOrAmbiguousSignals);

/// <summary>
/// Conservative transition detector for source-declared named DBC values.
/// It never invents unnamed states, does not infer ECU behavior between
/// observations, and refuses ambiguous physical-to-code conversions.
/// </summary>
public static class SignalStateTransitionAnalyzer
{
    private const long MaxExactInteger = 9007199254740991L;

    public static SignalStateTransitionReport Analyze(
        IReadOnlyList<SignalTimeline> timelines, int maxTransitions = 25000,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(timelines);
        if (maxTransitions < 0)
            throw new ArgumentOutOfRangeException(nameof(maxTransitions));

        var result = new List<SignalStateTransition>(Math.Min(maxTransitions, 1024));
        long total = 0;
        var unsupported = 0;
        foreach (var timeline in timelines)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var meanings = timeline.Signal.ValueDescriptions;
            if (meanings is null || meanings.Count == 0) continue;
            if (timeline.Signal.PhysicalConversionUnsupported)
            {
                unsupported++;
                continue;
            }

            // The time-series stores physical doubles. Distinct raw codes
            // mapping to one physical value are ambiguous and not trusted.
            var states = new Dictionary<double, string>();
            var collisions = new HashSet<double>();
            foreach (var (raw, label) in meanings)
            {
                if (raw is > MaxExactInteger or < -MaxExactInteger ||
                    string.IsNullOrWhiteSpace(label) ||
                    !timeline.Signal.TryGetPhysicalValue(raw, out var physical))
                    continue;
                if (states.TryGetValue(physical, out var existing) &&
                    !string.Equals(existing, label, StringComparison.Ordinal))
                    collisions.Add(physical);
                else
                    states.TryAdd(physical, label);
            }
            foreach (var ambiguous in collisions) states.Remove(ambiguous);
            if (states.Count == 0)
            {
                unsupported++;
                continue;
            }

            string? active = null;
            long first = 0;
            SignalSample previous = default;
            var sampleOrdinal = 0;
            foreach (var sample in timeline.Samples)
            {
                if ((++sampleOrdinal & 8191) == 0)
                    cancellationToken.ThrowIfCancellationRequested();
                if (!double.IsFinite(sample.Value) ||
                    !states.TryGetValue(sample.Value, out var state))
                {
                    active = null; // An unknown intervening code breaks continuity.
                    continue;
                }
                if (active is null)
                {
                    active = state;
                    first = sample.TimestampNanoseconds;
                }
                else if (!string.Equals(active, state, StringComparison.Ordinal))
                {
                    // Timestamps are observed samples, not proof of the actual
                    // switch instant. The unknown interval is explicitly retained.
                    total++;
                    if (result.Count < maxTransitions)
                    {
                        result.Add(new SignalStateTransition(
                            timeline.Key, timeline.Message.Name, timeline.Signal.Name,
                            active, state,
                            previous.TimestampNanoseconds, sample.TimestampNanoseconds,
                            previous.FrameIndex, sample.FrameIndex,
                            Math.Max(0, previous.TimestampNanoseconds - first),
                            Math.Max(0, sample.TimestampNanoseconds -
                                previous.TimestampNanoseconds)));
                    }
                    active = state;
                    first = sample.TimestampNanoseconds;
                }
                previous = sample;
            }
        }
        return new SignalStateTransitionReport(
            result, total, total - result.Count, unsupported);
    }
}
