using RbgCanInsight.Domain;

namespace RbgCanInsight.TraceIO;

/// <summary>
/// Offline multi-file import. Sources are parsed into bounded, spill-backed
/// collections, then interleaved by a stable O(log sourceCount) priority queue.
/// Only one frame per source must be read during the final merge. Source indexes
/// preserve original per-file positions independently of the combined row number.
/// </summary>
public static class TraceSession
{
    /// <summary>Compatibility API for consumers that explicitly need a materialized List.
    /// Prefer <see cref="ImportIntoAsync"/> for large analysis sessions.</summary>
    public static async Task<List<CanFrame>> ImportAsync(
        IReadOnlyList<string> paths, TraceMergeMode mode, TraceImportHealth health,
        CancellationToken cancellationToken = default)
    {
        var frames = new List<CanFrame>();
        await ImportIntoAsync(paths, mode, health, frames.Add, cancellationToken);
        return frames;
    }

    /// <summary>
    /// Streams a fully merged, stable timeline directly into the supplied consumer.
    /// Ordinary sources are kept in memory only until they pass the per-source spill
    /// threshold. They are always disposed, including after cancellation or errors.
    /// The returned map makes a frame's source file and original sorted index
    /// explicit without changing its raw payload or diagnostic interpretation.
    /// </summary>
    public static async Task<TraceSessionImportResult> ImportIntoAsync(
        IReadOnlyList<string> paths, TraceMergeMode mode, TraceImportHealth health,
        Action<CanFrame> append, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(health);
        ArgumentNullException.ThrowIfNull(append);
        if (paths.Count == 0) throw new ArgumentException("No trace files given.", nameof(paths));
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        var stores = new List<CanFrameCollection>(paths.Count);
        var snapshots = new List<TraceImportHealthSnapshot>(paths.Count);
        try
        {
            // An upper bound on the combined source-buffer footprint even when
            // dozens of files are below the main 3M-frame spill threshold.
            var sourceThreshold = Math.Max(1_024, 200_000 / paths.Count);
            foreach (var path in paths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var sourceCollector = new AdaptiveCanFrameCollector(
                    initialCapacity: Math.Min(sourceThreshold, 4_096),
                    spillThresholdFrames: sourceThreshold);
                var diagnostics = new TraceImportHealth();
                long sourceOrdinal = 0;
                await foreach (var frame in TraceImporter.ReadAsync(path, cancellationToken, diagnostics))
                    sourceCollector.Add(frame with { Sequence = ++sourceOrdinal });
                var snapshot = diagnostics.Snapshot();
                stores.Add(sourceCollector.Complete(sortByTime: snapshot.OutOfOrderTimestamps > 0));
                snapshots.Add(snapshot);
            }

            var shifts = new long[stores.Count];
            var channelOffsets = new int[stores.Count];
            if (mode == TraceMergeMode.SeparateChannels)
            {
                var nextChannel = 0;
                for (var i = 0; i < stores.Count; i++)
                {
                    channelOffsets[i] = nextChannel;
                    if (snapshots[i].Channels.Count > 0)
                        nextChannel = checked(nextChannel + snapshots[i].Channels.Max(c => c.Channel) + 1);
                }
            }
            else if (mode == TraceMergeMode.Sequential)
            {
                var any = false;
                long previousEnd = 0;
                for (var i = 0; i < stores.Count; i++)
                {
                    var frames = stores[i].Frames;
                    if (frames.Count == 0) continue;
                    var start = frames[0].TimestampNanoseconds;
                    if (any && start < previousEnd)
                        shifts[i] = checked(previousEnd - start);
                    previousEnd = checked(frames[^1].TimestampNanoseconds + shifts[i]);
                    any = true;
                }
            }

            health.Start(".session");
            // Interleaving independent loggers on one channel can fabricate
            // ISO-TP, UDS and cycle-timing continuity. Warn, never silently
            // interpret a timestamp overlap as proof of one physical bus.
            if (mode == TraceMergeMode.ByTime)
                WarnAboutOverlappingChannels(stores, snapshots, health);
            var summaries = new List<TraceSessionSourceSummary>(paths.Count);
            for (var i = 0; i < snapshots.Count; i++)
            {
                var s = snapshots[i];
                var sourceFrames = stores[i].Frames;
                summaries.Add(new TraceSessionSourceSummary(paths[i], s.Frames,
                    s.Channels.Count, s.OutOfOrderTimestamps, s.RequiresReview, s.Format,
                    sourceFrames.Count > 0 ? sourceFrames[0].TimestampNanoseconds : null,
                    sourceFrames.Count > 0 ? sourceFrames[^1].TimestampNanoseconds : null));
                if (s.RequiresReview)
                    health.RecordFileMetadataWarning("SESSION_SOURCE_REVIEW",
                        $"Session file {i + 1} ({Path.GetFileName(paths[i])}): {s.Summary}");
                foreach (var item in s.LoggedBusStatistics ?? [])
                    health.RecordLogged(item with
                    {
                        TimestampNanoseconds = checked(item.TimestampNanoseconds + shifts[i]),
                        Channel = checked(item.Channel + channelOffsets[i])
                    });
                foreach (var item in s.LoggedControllerStatuses ?? [])
                    health.RecordLogged(item with
                    {
                        TimestampNanoseconds = checked(item.TimestampNanoseconds + shifts[i]),
                        Channel = checked(item.Channel + channelOffsets[i])
                    });
            }

            var position = new int[stores.Count];
            var queue = new PriorityQueue<int, (long Timestamp, int Source)>();
            for (var i = 0; i < stores.Count; i++)
                if (stores[i].Frames.Count > 0)
                    queue.Enqueue(i, (checked(stores[i].Frames[0].TimestampNanoseconds + shifts[i]), i));

            var indexBuilder = new TraceSessionSourceIndex.Builder(paths.Count);
            var combinedIndex = 0;
            while (queue.TryDequeue(out var source, out _))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sourcePosition = position[source]++;
                var original = stores[source].Frames[sourcePosition];
                var shiftedTime = checked(original.TimestampNanoseconds + shifts[source]);
                var shiftedChannel = checked(original.Channel + channelOffsets[source]);
                CanFrame mapped;
                if (shifts[source] == 0 && channelOffsets[source] == 0)
                    mapped = original with { Sequence = combinedIndex };
                else
                    mapped = AdjustFrame(original, shiftedChannel, shiftedTime, combinedIndex);
                append(mapped);
                indexBuilder.Add(source, sourcePosition);
                health.RecordFrame(mapped);
                combinedIndex = checked(combinedIndex + 1);
                if (position[source] < stores[source].Frames.Count)
                {
                    var nextTimestamp = checked(stores[source].Frames[position[source]].TimestampNanoseconds + shifts[source]);
                    queue.Enqueue(source, (nextTimestamp, source));
                }
            }
            health.Complete();
            return new TraceSessionImportResult(
                indexBuilder.Build(paths, shifts, channelOffsets), summaries);
        }
        finally
        {
            foreach (var store in stores) store.Dispose();
        }
    }

    /// <summary>Observational warning, not an automatic source clock correction.
    /// Files with overlapping channel/timeline ranges may legitimately describe
    /// one physical bus, but may also belong to independent acquisition clocks.</summary>
    private static void WarnAboutOverlappingChannels(
        IReadOnlyList<CanFrameCollection> stores,
        IReadOnlyList<TraceImportHealthSnapshot> snapshots, TraceImportHealth health)
    {
        var intervalsByChannel = new Dictionary<int, List<(long Start, long End, int Source)>>();
        for (var i = 0; i < stores.Count; i++)
        {
            foreach (var channel in snapshots[i].Channels)
            {
                // Use *per-channel* observed clock ranges, not a file-wide range
                // that could flag buses recorded at completely different times.
                if (channel.FirstTimestampNanoseconds is not { } start ||
                    channel.LastTimestampNanoseconds is not { } end) continue;
                if (!intervalsByChannel.TryGetValue(channel.Channel, out var spans))
                    intervalsByChannel[channel.Channel] = spans = [];
                spans.Add((start, end, i));
            }
        }

        var overlappingChannels = 0;
        var examples = new List<string>(4);
        foreach (var (channel, spans) in intervalsByChannel)
        {
            if (spans.Count < 2) continue;
            spans.Sort((a, b) => a.Start.CompareTo(b.Start));
            var longest = spans[0];
            for (var i = 1; i < spans.Count; i++)
            {
                var next = spans[i];
                if (next.Start <= longest.End)
                {
                    overlappingChannels++;
                    if (examples.Count < 4)
                        examples.Add($"CAN {channel + 1} (recordings {longest.Source + 1} and {next.Source + 1})");
                    break;
                }
                if (next.End > longest.End) longest = next;
            }
        }

        if (overlappingChannels > 0)
            health.RecordFileMetadataWarning("SESSION_OVERLAPPING_CHANNELS",
                $"Time-merged recordings overlap on {overlappingChannels} CAN channel(s): " +
                string.Join(", ", examples) + ". This is not proof that the physical buses " +
                "or logger clocks are identical; ISO-TP/UDS or message-cycle analysis may " +
                "incorrectly span unrelated recordings. Use Separate channels for " +
                "independent loggers, or verify time synchronization first.");
    }

    // Keep stackalloc in a synchronous helper; ref-struct locals are unsupported
    // inside an async method when the solution targets C# 12.
    private static CanFrame AdjustFrame(in CanFrame original, int channel, long timestamp, long sequence)
    {
        Span<byte> payload = stackalloc byte[64];
        original.CopyPayloadTo(payload);
        return new CanFrame(channel, original.Identifier, payload[..original.DataLength],
            timestamp, original.Flags, sequence, original.DeclaredDataLength);
    }
}
