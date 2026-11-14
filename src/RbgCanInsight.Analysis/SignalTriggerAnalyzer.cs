using RbgCanInsight.Dbc;

namespace RbgCanInsight.Analysis;

/// <summary>Deterministic, passive predicates over already-decoded offline
/// observations. No ECU control, CAN transmission or live capture.</summary>
public enum SignalTriggerMode
{
    CrossesAbove,
    CrossesBelow,
    ChangesByMoreThan,
    EntersNamedState,
    LeavesNamedState
}

/// <summary>Every rule is scoped to the exact database/channel/CAN identity
/// and signal name held by the timeline key, never just a human-readable name.</summary>
public sealed record SignalTriggerRule(
    string SignalKey,
    SignalTriggerMode Mode,
    double Threshold = 0,
    string? NamedState = null,
    double ChangeTolerance = 0,
    long MinimumEventSeparationNanoseconds = 0);

/// <summary>Two actual adjacent observations bracketing a matching rule.
/// The real vehicle transition may have happened anywhere between them.</summary>
public sealed record SignalTriggerEvent(
    string SignalKey,
    long PreviousTimestampNanoseconds,
    long CurrentTimestampNanoseconds,
    int PreviousFrameIndex,
    int CurrentFrameIndex,
    double PreviousPhysicalValue,
    double CurrentPhysicalValue,
    string? PreviousNamedState,
    string? CurrentNamedState)
{
    public long UncertaintyNanoseconds =>
        Math.Max(0, CurrentTimestampNanoseconds - PreviousTimestampNanoseconds);
}

public sealed record SignalTriggerReport(
    string SignalKey,
    SignalTriggerMode Mode,
    IReadOnlyList<SignalTriggerEvent> Events,
    long MatchedEvents,
    long SuppressedBySeparation,
    long OmittedEvents,
    string? Warning)
{
    public string Summary => $"{MatchedEvents:N0} matches · {Events.Count:N0} displayed" +
        (SuppressedBySeparation > 0 ? $" · {SuppressedBySeparation:N0} separation-suppressed" : "") +
        (OmittedEvents > 0 ? $" · {OmittedEvents:N0} omitted by display limit" : "");
}

/// <summary>
/// Conservative trigger evaluator: every emitted event has two source frame
/// indices, and state triggers require BOTH observations to have unambiguous
/// documented names. Non-finite samples break continuity. Timing separation
/// is applied to observed event timestamps, not unobserved ECU switch times.
/// </summary>
public static class SignalTriggerAnalyzer
{
    private const long MaxExactInteger = 9007199254740991L;

    public static SignalTriggerReport Evaluate(
        SignalTimeline timeline, SignalTriggerRule rule,
        int maxEvents = 25_000, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        ArgumentNullException.ThrowIfNull(rule);
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(timeline.Key, rule.SignalKey, StringComparison.Ordinal))
            throw new ArgumentException("The trigger signal identity does not match this physical timeline.",
                nameof(rule));
        if (!Enum.IsDefined(rule.Mode))
            throw new ArgumentOutOfRangeException(nameof(rule), "Unknown signal trigger mode.");
        if (maxEvents < 0 || maxEvents > 100_000)
            throw new ArgumentOutOfRangeException(nameof(maxEvents));
        if (!double.IsFinite(rule.Threshold) ||
            !double.IsFinite(rule.ChangeTolerance) || rule.ChangeTolerance < 0 ||
            rule.MinimumEventSeparationNanoseconds < 0)
            throw new ArgumentException("Trigger threshold, tolerance and separation must be finite nonnegative values where applicable.",
                nameof(rule));

        var stateTrigger = rule.Mode is SignalTriggerMode.EntersNamedState or
            SignalTriggerMode.LeavesNamedState;
        if (stateTrigger && string.IsNullOrWhiteSpace(rule.NamedState))
            throw new ArgumentException("Named-state triggers require a declared target label.", nameof(rule));

        var stateNames = stateTrigger ? BuildUnambiguousStateNames(timeline.Signal) : null;
        if (stateTrigger && stateNames!.Count == 0)
            return new SignalTriggerReport(rule.SignalKey, rule.Mode, [], 0, 0, 0,
                "No unambiguous named raw codes with verified physical conversion; state trigger cannot run.");
        if (stateTrigger && !stateNames!.Values.Any(value =>
                string.Equals(value, rule.NamedState, StringComparison.OrdinalIgnoreCase)))
            return new SignalTriggerReport(rule.SignalKey, rule.Mode, [], 0, 0, 0,
                "Target named state is not declared unambiguously for this signal.");

        var output = new List<SignalTriggerEvent>(Math.Min(maxEvents, 256));
        long found = 0, suppressed = 0;
        long? lastKeptTime = null;
        SignalSample? previous = null;
        string? previousName = null;
        var sequence = 0;

        foreach (var sample in timeline.Samples)
        {
            if ((++sequence & 8191) == 0) cancellationToken.ThrowIfCancellationRequested();
            if (!double.IsFinite(sample.Value))
            {
                previous = null;
                previousName = null;
                continue;
            }

            var name = stateNames is not null && stateNames.TryGetValue(sample.Value, out var value)
                ? value : null;
            if (previous is { } prior)
            {
                if (sample.TimestampNanoseconds < prior.TimestampNanoseconds)
                    throw new InvalidDataException("Signal timeline is not chronological; trigger ordering cannot be trusted.");

                var fire = rule.Mode switch
                {
                    SignalTriggerMode.CrossesAbove =>
                        prior.Value <= rule.Threshold && sample.Value > rule.Threshold,
                    SignalTriggerMode.CrossesBelow =>
                        prior.Value >= rule.Threshold && sample.Value < rule.Threshold,
                    SignalTriggerMode.ChangesByMoreThan =>
                        Math.Abs(sample.Value - prior.Value) > rule.ChangeTolerance,
                    SignalTriggerMode.EntersNamedState =>
                        previousName is not null && name is not null &&
                        !string.Equals(previousName, rule.NamedState, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(name, rule.NamedState, StringComparison.OrdinalIgnoreCase),
                    SignalTriggerMode.LeavesNamedState =>
                        previousName is not null && name is not null &&
                        string.Equals(previousName, rule.NamedState, StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(name, rule.NamedState, StringComparison.OrdinalIgnoreCase),
                    _ => false
                };
                if (fire)
                {
                    if (lastKeptTime is { } last &&
                        (decimal)sample.TimestampNanoseconds - last <
                            rule.MinimumEventSeparationNanoseconds)
                    {
                        suppressed++;
                    }
                    else
                    {
                        found++;
                        lastKeptTime = sample.TimestampNanoseconds;
                        if (output.Count < maxEvents)
                            output.Add(new SignalTriggerEvent(rule.SignalKey,
                                prior.TimestampNanoseconds, sample.TimestampNanoseconds,
                                prior.FrameIndex, sample.FrameIndex,
                                prior.Value, sample.Value, previousName, name));
                    }
                }
            }
            previous = sample;
            previousName = name;
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new SignalTriggerReport(rule.SignalKey, rule.Mode, output, found,
            suppressed, found - output.Count, null);
    }

    private static Dictionary<double, string> BuildUnambiguousStateNames(DbcSignal signal)
    {
        var result = new Dictionary<double, string>();
        var conflicts = new HashSet<double>();
        if (signal.PhysicalConversionUnsupported || signal.ValueDescriptions is not { Count: > 0 })
            return result;
        foreach (var (code, text) in signal.ValueDescriptions)
        {
            if (code > MaxExactInteger || code < -MaxExactInteger ||
                string.IsNullOrWhiteSpace(text) ||
                !signal.TryGetPhysicalValue(code, out var value))
                continue;
            if (result.TryGetValue(value, out var previous) &&
                !string.Equals(previous, text, StringComparison.Ordinal))
                conflicts.Add(value);
            else
                result.TryAdd(value, text);
        }
        foreach (var value in conflicts) result.Remove(value);
        return result;
    }
}
