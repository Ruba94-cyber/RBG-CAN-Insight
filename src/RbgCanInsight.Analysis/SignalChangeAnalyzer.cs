using RbgCanInsight.Dbc;

namespace RbgCanInsight.Analysis;

public enum SignalActivity
{
    /// <summary>Declared in the DBC for a seen message but never decodable in the trace.</summary>
    Missing,
    /// <summary>Present with samples but a single constant value.</summary>
    Stable,
    /// <summary>Value changes over the trace.</summary>
    Variable,
    /// <summary>Out-of-range values (vs DBC min/max) or extreme steps were observed.</summary>
    Anomalous
}

/// <summary>One observed transition of a signal value.</summary>
public readonly record struct SignalChange(
    long TimestampNanoseconds,
    double FromValue,
    double ToValue,
    int FrameIndex)
{
    public double Delta => ToValue - FromValue;
}

/// <summary>Change-matrix row for one signal: everything the analyst needs to judge it at a glance.</summary>
public sealed record SignalChangeReport(
    SignalTimeline Timeline,
    int SampleCount,
    int ChangeCount,
    double FirstValue,
    double LastValue,
    double MinValue,
    double MaxValue,
    long FirstChangeNanoseconds,
    long LastChangeNanoseconds,
    double LargestDelta,
    bool HasOutOfRange,
    SignalActivity Activity,
    IReadOnlyList<SignalChange> Changes)
{
    public string MessageName => Timeline.Message.Name;
    public string SignalName => Timeline.Signal.Name;
    public string Unit => Timeline.Signal.Unit;
    public uint Identifier => Timeline.Message.Identifier;
    public double Amplitude => SampleCount == 0 ? 0 : MaxValue - MinValue;
}

/// <summary>
/// The engine behind the Signal Change Matrix: given the decoded timelines it reports, per signal,
/// whether it changed, when, from which value to which value, how large the swings were, and
/// whether anything looks anomalous — each change staying linked to its source frame index.
/// </summary>
public static class SignalChangeAnalyzer
{
    public static IReadOnlyList<SignalChangeReport> Analyze(
        IReadOnlyList<SignalTimeline> timelines,
        IReadOnlyList<DbcDatabase> databases,
        IReadOnlySet<uint>? seenIdentifiers = null,
        CancellationToken cancellationToken = default,
        IReadOnlySet<(int Channel, uint Identifier, bool Extended)>? seenFrameIdentities = null,
        IReadOnlyList<DbcDecoder>? activeDecoders = null,
        IReadOnlySet<(int Source, int Channel, uint Identifier, bool Extended)>? seenSourceFrameIdentities = null,
        IReadOnlyList<string>? sourcePaths = null)
    {
        var reports = new List<SignalChangeReport>(timelines.Count);
        foreach (var timeline in timelines)
        {
            cancellationToken.ThrowIfCancellationRequested();
            reports.Add(AnalyzeTimeline(timeline));
        }

        // Index distinct observed channels once. Scanning the entire raw-ID set
        // for every DBC message is O(DBC messages × observed IDs), which becomes
        // expensive on long, multi-network heavy-vehicle captures.
        var observedById = new Dictionary<(bool Extended, uint Id), HashSet<int>>();
        var observedByPgn = new Dictionary<uint, HashSet<int>>();
        // In a multi-logger import, source + physical CAN channel together
        // identify an acquisition. A message seen in Logger 1 must not mark
        // the same DBC signal as present or missing in Logger 2.
        var scopedById = new Dictionary<(bool Extended, uint Id), HashSet<(int Source, int Channel)>>();
        var scopedByPgn = new Dictionary<uint, HashSet<(int Source, int Channel)>>();
        if (seenSourceFrameIdentities is not null)
        {
            foreach (var item in seenSourceFrameIdentities)
            {
                if (item.Source < 0 ||
                    (sourcePaths is not null && item.Source >= sourcePaths.Count))
                    throw new ArgumentOutOfRangeException(nameof(seenSourceFrameIdentities),
                        "Source-scoped CAN evidence contains an invalid logger index.");

                if (!scopedById.TryGetValue((item.Extended, item.Identifier), out var locations))
                {
                    locations = [];
                    scopedById.Add((item.Extended, item.Identifier), locations);
                }
                locations.Add((item.Source, item.Channel));
                if (item.Extended)
                {
                    var pgn = J1939.ExtractPgn(item.Identifier);
                    if (!scopedByPgn.TryGetValue(pgn, out var pgLocations))
                    {
                        pgLocations = [];
                        scopedByPgn.Add(pgn, pgLocations);
                    }
                    pgLocations.Add((item.Source, item.Channel));
                }
            }
        }
        if (seenFrameIdentities is not null)
        {
            foreach (var item in seenFrameIdentities)
            {
                if (!observedById.TryGetValue((item.Extended, item.Identifier), out var channels))
                {
                    channels = [];
                    observedById[(item.Extended, item.Identifier)] = channels;
                }
                channels.Add(item.Channel);
                if (item.Extended)
                {
                    var pgn = J1939.ExtractPgn(item.Identifier);
                    if (!observedByPgn.TryGetValue(pgn, out var pgnChannels))
                    {
                        pgnChannels = [];
                        observedByPgn[pgn] = pgnChannels;
                    }
                    pgnChannels.Add(item.Channel);
                }
            }
        }

        // Missing unconditional signals are reported on their originating CAN
        // channel when the message appeared but no matching decoded sample exists.
        // Inactive multiplex branches are not missing evidence.
        if (seenIdentifiers is not null || seenFrameIdentities is not null ||
            seenSourceFrameIdentities is not null)
        {
            var covered = timelines
                .Select(timeline => (timeline.DatabaseName, Source: timeline.OriginalSourceIndex,
                    timeline.Channel, timeline.Message.Identifier,
                    timeline.Message.IsExtended, timeline.Signal.Name))
                .ToHashSet();
            foreach (var database in databases)
            {
                var scopedDecoders = activeDecoders?.Where(decoder =>
                    string.Equals(decoder.Database.Name, database.Name, StringComparison.OrdinalIgnoreCase))
                    .ToArray() ?? [];
                var canRead = (int channel, bool pgnMatch) =>
                    scopedDecoders.Length == 0
                        ? !pgnMatch
                        : scopedDecoders.Any(decoder =>
                            (!pgnMatch || decoder.Options.MatchByPgn) &&
                            (decoder.Options.Channel is null || decoder.Options.Channel == channel));
                foreach (var message in database.AllMessageDefinitions)
                {
                    (int Source, int Channel)[] locations;
                    if (seenSourceFrameIdentities is not null)
                    {
                        var observed = new HashSet<(int Source, int Channel)>();
                        if (scopedById.TryGetValue((message.IsExtended, message.Identifier), out var exact))
                        {
                            foreach (var location in exact)
                                if (canRead(location.Channel, false)) observed.Add(location);
                        }
                        if (message.IsExtended &&
                            scopedByPgn.TryGetValue(J1939.ExtractPgn(message.Identifier), out var pgLocations))
                        {
                            foreach (var location in pgLocations)
                                if (canRead(location.Channel, true)) observed.Add(location);
                        }
                        locations = observed.ToArray();
                    }
                    else if (seenFrameIdentities is not null)
                    {
                        var observed = new HashSet<(int Source, int Channel)>();
                        if (observedById.TryGetValue((message.IsExtended, message.Identifier), out var exact))
                        {
                            foreach (var channel in exact)
                                if (canRead(channel, false)) observed.Add((-1, channel));
                        }
                        if (message.IsExtended &&
                            observedByPgn.TryGetValue(J1939.ExtractPgn(message.Identifier), out var j1939))
                        {
                            foreach (var channel in j1939)
                                if (canRead(channel, true)) observed.Add((-1, channel));
                        }
                        locations = observed.ToArray();
                    }
                    else
                    {
                        locations = seenIdentifiers!.Contains(message.Identifier)
                            ? [(-1, -1)] : [];
                    }
                    foreach (var (source, channel) in locations)
                    {
                        foreach (var signal in message.Signals)
                        {
                            // A multiplexed signal is legitimately absent whenever
                            // its controller selects another branch. Without proof
                            // of an active branch, a "missing" finding is false.
                            // The Trace/DBC inspector exposes branch conditions.
                            if (signal.MultiplexValue is not null ||
                                signal.MultiplexConditions is { Count: > 0 })
                                continue;
                            if (channel < 0)
                            {
                                if (covered.Any(item => item.DatabaseName == database.Name &&
                                    item.Source == source &&
                                    item.Identifier == message.Identifier &&
                                    item.IsExtended == message.IsExtended && item.Name == signal.Name))
                                    continue;
                            }
                            else if (covered.Contains((database.Name, source, channel,
                                message.Identifier, message.IsExtended, signal.Name)))
                                continue;

                            reports.Add(new SignalChangeReport(
                                new SignalTimeline(message, signal, database.Name, channel,
                                    originalSourceIndex: source,
                                    originalSourcePath: source >= 0 && sourcePaths is not null
                                        ? sourcePaths[source] : null),
                                0, 0, 0, 0, 0, 0, 0, 0, 0,
                                HasOutOfRange: false,
                                SignalActivity.Missing,
                                []));
                        }
                    }
                }
            }
        }

        return reports;
    }

    private static SignalChangeReport AnalyzeTimeline(SignalTimeline timeline)
    {
        var samples = timeline.Samples;
        var signal = timeline.Signal;
        var changes = new List<SignalChange>();
        var min = double.MaxValue;
        var max = double.MinValue;
        var hasOutOfRange = false;
        var rangeValid = signal.Maximum > signal.Minimum;
        var largestDelta = 0d;

        for (var index = 0; index < samples.Count; index++)
        {
            var sample = samples[index];
            min = Math.Min(min, sample.Value);
            max = Math.Max(max, sample.Value);
            if (rangeValid && signal.IsOutsideDeclaredRange(sample.Value))
            {
                hasOutOfRange = true;
            }

            if (index > 0 && sample.Value != samples[index - 1].Value)
            {
                changes.Add(new SignalChange(
                    sample.TimestampNanoseconds,
                    samples[index - 1].Value,
                    sample.Value,
                    sample.FrameIndex));
                largestDelta = Math.Max(largestDelta, Math.Abs(sample.Value - samples[index - 1].Value));
            }
        }

        var activity = samples.Count == 0
            ? SignalActivity.Missing
            : hasOutOfRange
                ? SignalActivity.Anomalous
                : changes.Count > 0
                    ? SignalActivity.Variable
                    : SignalActivity.Stable;

        return new SignalChangeReport(
            timeline,
            samples.Count,
            changes.Count,
            samples.Count > 0 ? samples[0].Value : 0,
            samples.Count > 0 ? samples[^1].Value : 0,
            samples.Count > 0 ? min : 0,
            samples.Count > 0 ? max : 0,
            changes.Count > 0 ? changes[0].TimestampNanoseconds : 0,
            changes.Count > 0 ? changes[^1].TimestampNanoseconds : 0,
            largestDelta,
            hasOutOfRange,
            activity,
            changes);
    }
}
