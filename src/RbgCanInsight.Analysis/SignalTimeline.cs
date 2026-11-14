using RbgCanInsight.Dbc;
using RbgCanInsight.Domain;

namespace RbgCanInsight.Analysis;

/// <summary>One decoded sample: timestamp, physical value and the index of the source frame.</summary>
public readonly record struct SignalSample(long TimestampNanoseconds, double Value, int FrameIndex);

/// <summary>The full decoded history of one DBC signal across a loaded trace.</summary>
public sealed class SignalTimeline
{
    private readonly List<SignalSample> _samples = [];

    public SignalTimeline(
        DbcMessage message, DbcSignal signal, string databaseName = "",
        int channel = -1, uint? sourceIdentifier = null,
        bool? sourceIsExtended = null, int originalSourceIndex = -1,
        string? originalSourcePath = null)
    {
        Message = message;
        Signal = signal;
        DatabaseName = databaseName;
        Channel = channel;
        SourceIdentifier = sourceIdentifier ?? message.Identifier;
        SourceIsExtended = sourceIsExtended ?? message.IsExtended;
        if (originalSourceIndex < -1)
            throw new ArgumentOutOfRangeException(nameof(originalSourceIndex));
        OriginalSourceIndex = originalSourceIndex;
        OriginalSourcePath = originalSourcePath;
    }

    public string DatabaseName { get; }
    /// <summary>Physical CAN interface, zero-based. -1 means unavailable (legacy/no frames).</summary>
    public int Channel { get; }
    public DbcMessage Message { get; }
    /// <summary>ACTUAL recorded wire arbitration identity, not the
    /// DBC definition's arbitrary source address in J1939 PGN mode.
    /// Different source addresses can emit the same PGN independently.</summary>
    public uint SourceIdentifier { get; }
    public bool SourceIsExtended { get; }
    /// <summary>Original physical recording, zero-based; -1 for legacy
    /// single-file timelines without a source map. Never infer ECU identity
    /// from matching CAN IDs or overlapping timestamps across recordings.</summary>
    public int OriginalSourceIndex { get; }
    public string? OriginalSourcePath { get; }
    public DbcSignal Signal { get; }
    /// <summary>Old workspace preset identifier, only safe for a unique matching series.</summary>
    public string LegacyKey => $"{Message.Name}.{Signal.Name}";
    /// <summary>Full identity used by watch, compare and plotting. CAN channels must not be mixed.</summary>
    public string Key => CrossRecordingKey +
        (OriginalSourceIndex >= 0 ? $"|logger{OriginalSourceIndex}" : "");
    /// <summary>Signal identity without the physical-recording suffix, for matching one
    /// signal between two separately imported traces (loaded vs reference). Loaded and
    /// reference traces never share a logger index space, so <see cref="Key"/> cannot match.</summary>
    public string CrossRecordingKey =>
        $"{DatabaseName}|ch{Channel}|{SourceIdentifier:X8}|{(SourceIsExtended ? 'E' : 'S')}|{LegacyKey}";
    public IReadOnlyList<SignalSample> Samples => _samples;

    internal void Add(long timestampNanoseconds, double value, int frameIndex) =>
        _samples.Add(new SignalSample(timestampNanoseconds, value, frameIndex));

    /// <summary>Recorded frames may arrive out of timestamp order, especially
    /// when multiple loggers are merged. The binary searches used by plotting
    /// and UDS↔CAN correlation require monotonic timeline timestamps. Sort
    /// once after import, preserving source frame indices as audit evidence.</summary>
    internal void SortChronologically()
    {
        if (_samples.Count <= 1) return;
        var ordered = true;
        for (var i = 1; i < _samples.Count; i++)
        {
            if (_samples[i - 1].TimestampNanoseconds <= _samples[i].TimestampNanoseconds)
                continue;
            ordered = false;
            break;
        }
        if (!ordered)
            _samples.Sort((a, b) =>
            {
                var time = a.TimestampNanoseconds.CompareTo(b.TimestampNanoseconds);
                return time != 0 ? time : a.FrameIndex.CompareTo(b.FrameIndex);
            });
    }

    /// <summary>Nearest sample at or before the given timestamp (binary search), or null.</summary>
    public SignalSample? SampleAt(long timestampNanoseconds)
    {
        if (_samples.Count == 0 || timestampNanoseconds < _samples[0].TimestampNanoseconds)
        {
            return null;
        }

        var low = 0;
        var high = _samples.Count - 1;
        while (low < high)
        {
            var mid = (low + high + 1) / 2;
            if (_samples[mid].TimestampNanoseconds <= timestampNanoseconds)
            {
                low = mid;
            }
            else
            {
                high = mid - 1;
            }
        }

        return _samples[low];
    }
}

/// <summary>
/// Decodes a whole trace against one or more DBC databases in a single pass and produces the
/// per-signal timelines that power the plot, the change matrix and the DID correlation engine.
/// </summary>
public static class SignalTimelineBuilder
{
    public static IReadOnlyList<SignalTimeline> Build(
        IReadOnlyList<CanFrame> frames,
        IReadOnlyList<DbcDatabase> databases,
        CancellationToken cancellationToken = default,
        Func<int, int>? sourceAt = null,
        IReadOnlyList<string>? sourcePaths = null) =>
        Build(frames, databases.Select(database => new DbcDecoder(database)).ToArray(),
            cancellationToken, sourceAt, sourcePaths);

    public static IReadOnlyList<SignalTimeline> Build(
        IReadOnlyList<CanFrame> frames,
        IReadOnlyList<DbcDecoder> decoders,
        CancellationToken cancellationToken = default,
        Func<int, int>? sourceAt = null,
        IReadOnlyList<string>? sourcePaths = null)
    {
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentNullException.ThrowIfNull(decoders);
        if (sourcePaths is { Count: > 1 } && sourceAt is null)
            throw new ArgumentException(
                "Multiple original recordings require an exact source index.",
                nameof(sourceAt));
        // Group by the actual physical CAN identifier, including the source
        // address in J1939 PGN mode, and never mix independent channels.
        // DBC message identity is still retained for layout interpretation.
        var timelines = new Dictionary<
            (string Database, int Source, int Channel, uint Id, bool Extended, string Signal), SignalTimeline>();
        var owners = new Dictionary<
            (string Database, int Source, int Channel, uint Id, bool Extended, string Signal),
            (DbcDatabase Database, DbcMessage Message, DbcSignal Signal)>();
        var ambiguous = new HashSet<
            (string Database, int Source, int Channel, uint Id, bool Extended, string Signal)>();
        // The same CAN arbitration identity can appear millions of times.
        // Inspect conflicting DBC definitions once per channel / wire ID,
        // even if one definition never yields a finite numeric sample.
        // TryGetMessage depends only on the physical channel, wire
        // arbitration ID and 11/29-bit format (including J1939 PGN
        // options). Resolve those immutable DBC owners ONCE per recorded
        // identifier; a million repeats must not probe every loaded DBC.
        // Cache both matches and misses. Never cache by display name alone.
        // A malformed/heterogeneous capture may contain millions of
        // distinct 29-bit arbitration IDs. Bound the decoder cache so
        // optimizing repeated messages cannot consume unbounded RAM.
        // After the cap, resolve an unseen ID normally without retaining
        // it. Previously cached IDs keep their fast path.
        const int maxResolvedCanIdentities = 65_536;
        var matchingDecoders = new Dictionary<
            (int Channel, uint Identifier, bool Extended),
            (DbcDecoder Decoder, DbcMessage Message)[]>();
        // Decode() returns signal names. Cache their owning definitions once,
        // rather than linearly searching each message for every decoded value
        // in every CAN frame of a multi-million-frame trace.
        var signalsByMessage = new Dictionary<DbcMessage,
            Dictionary<string, DbcSignal>>(ReferenceEqualityComparer.Instance);

        for (var frameIndex = 0; frameIndex < frames.Count; frameIndex++)
        {
            if ((frameIndex & 0xFFFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var frame = frames[frameIndex];
            // Error objects and remote request frames have no recorded
            // data signal. Do not use them as a first (and possibly only)
            // ownership-resolution observation of a CAN identifier.
            if (frame.IsError || frame.Flags.HasFlag(CanFrameFlags.Remote))
                continue;
            // Resolve the original logger once per frame. A merged frame's
            // numeric channel / CAN ID is not a physical acquisition identity.
            var source = sourceAt?.Invoke(frameIndex) ?? -1;
            if (source < -1 || (sourceAt is not null && source < 0) ||
                (sourcePaths is not null && source >= sourcePaths.Count))
                throw new ArgumentOutOfRangeException(nameof(sourceAt),
                    "A decoded frame refers to an invalid original recording.");
            var frameKey = (frame.Channel, frame.Identifier, frame.IsExtended);
            if (!matchingDecoders.TryGetValue(frameKey, out var applicable))
            {
                var matches = new List<(DbcDecoder Decoder, DbcMessage Message)>();
                foreach (var decoder in decoders)
                {
                    if (!decoder.TryGetMessage(frame, out var definition))
                        continue;
                    matches.Add((decoder, definition));
                    if (!signalsByMessage.ContainsKey(definition))
                    {
                        var byName = new Dictionary<string, DbcSignal>(
                            StringComparer.Ordinal);
                        foreach (var defined in definition.Signals)
                            byName.TryAdd(defined.Name, defined);
                        signalsByMessage.Add(definition, byName);
                    }
                    foreach (var sourceSignal in definition.Signals)
                    {
                        var sourceKey = (decoder.Database.Name, source, frame.Channel,
                            frame.Identifier, frame.IsExtended,
                            sourceSignal.Name);
                        if (owners.TryGetValue(sourceKey, out var declared))
                        {
                            if (!ReferenceEquals(declared.Database, decoder.Database) ||
                                !ReferenceEquals(declared.Message, definition) ||
                                !ReferenceEquals(declared.Signal, sourceSignal))
                            {
                                ambiguous.Add(sourceKey);
                                timelines.Remove(sourceKey);
                            }
                        }
                        else
                        {
                            owners.Add(sourceKey, (decoder.Database,
                                definition, sourceSignal));
                        }
                    }
                }
                applicable = matches.ToArray();
                if (matchingDecoders.Count < maxResolvedCanIdentities)
                    matchingDecoders.Add(frameKey, applicable);
            }
            foreach (var (decoder, message) in applicable)
            {
                foreach (var value in decoder.Decode(frame))
                {
                    // A float-coded CAN signal may legitimately contain NaN,
                    // infinity or an overflowing physical scaling result.
                    // Preserve those bytes in Trace, but do not insert
                    // nonfinite numbers into plots, statistics or correlation.
                    if (!double.IsFinite(value.PhysicalValue))
                        continue;
                    var key = (decoder.Database.Name, source, frame.Channel, frame.Identifier,
                        frame.IsExtended, value.Signal);
                    if (ambiguous.Contains(key))
                        continue;
                    // Two DBC files can share their display name yet define
                    // this CAN ID and signal differently. Do not silently mix
                    // their physical values into a single apparently proven
                    // timeline. This also guards duplicate same-name decoders.
                    if (!signalsByMessage.TryGetValue(message, out var byName) ||
                        !byName.TryGetValue(value.Signal, out var signal))
                        continue;
                    if (owners.TryGetValue(key, out var owner))
                    {
                        if (!ReferenceEquals(owner.Database, decoder.Database) ||
                            !ReferenceEquals(owner.Message, message) ||
                            !ReferenceEquals(owner.Signal, signal))
                        {
                            ambiguous.Add(key);
                            timelines.Remove(key);
                            continue;
                        }
                    }
                    else
                    {
                        owners.Add(key, (decoder.Database, message, signal));
                    }

                    if (!timelines.TryGetValue(key, out var timeline))
                    {
                        timeline = new SignalTimeline(message, signal,
                            decoder.Database.Name, frame.Channel,
                            frame.Identifier, frame.IsExtended, source,
                            source >= 0 && sourcePaths is not null
                                ? sourcePaths[source] : null);
                        timelines[key] = timeline;
                    }
                    // The same immutable DBC can be attached through two
                    // overlapping profiles. A single source frame must be
                    // counted as one observation, never two confirmations.
                    if (timeline.Samples.Count == 0 ||
                        timeline.Samples[^1].FrameIndex != frameIndex)
                        timeline.Add(frame.TimestampNanoseconds,
                            value.PhysicalValue, frameIndex);
                }
            }
        }

        foreach (var timeline in timelines.Values)
            timeline.SortChronologically();

        return timelines.Values
            .OrderBy(timeline => timeline.Message.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(timeline => timeline.Signal.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(timeline => timeline.DatabaseName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(timeline => timeline.OriginalSourceIndex)
            .ThenBy(timeline => timeline.Channel)
            .ThenBy(timeline => timeline.SourceIdentifier)
            .ThenBy(timeline => timeline.SourceIsExtended)
            .ToArray();
    }
}
