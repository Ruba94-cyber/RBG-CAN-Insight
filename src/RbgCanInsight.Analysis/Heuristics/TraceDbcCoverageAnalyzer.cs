using RbgCanInsight.Dbc;
using RbgCanInsight.Domain;

namespace RbgCanInsight.Analysis.Heuristics;

/// <summary>A CAN identifier seen in the trace that no loaded DBC message matches.</summary>
public sealed record UnmatchedCanId(
    int Channel, uint Identifier, bool IsExtended, long FrameCount, long PayloadBytes, int FirstFrameIndex);

/// <summary>Per-message coverage of the observed payloads by the DBC definition.</summary>
public sealed record MessageCoverage(
    string Message, uint Identifier, bool IsExtended, int Channel, int DeclaredLength, long Frames,
    long ShortFrames, long LongFrames, IReadOnlyList<int> UncoveredBytes, long UndecodableBytes, int FirstShortFrameIndex);

/// <summary>A signal whose observed physical values leave its declared [min|max].</summary>
public sealed record OutOfRangeSignal(
    string Message, string Signal, string Unit, long Samples, long OutOfRangeSamples,
    double ObservedMin, double ObservedMax, double DeclaredMin, double DeclaredMax, int FirstFrameIndex);

public sealed record TraceDbcCoverageReport(
    string Database,
    long FrameCount,
    long MatchedFrames,
    IReadOnlyList<UnmatchedCanId> UnmatchedIds,
    IReadOnlyList<MessageCoverage> Messages,
    IReadOnlyList<OutOfRangeSignal> OutOfRange)
{
    public long UnmatchedFrames => UnmatchedIds.Sum(id => id.FrameCount);

    /// <summary>Payload bytes that no DBC signal can explain: all bytes of unmatched IDs plus
    /// bytes of matched frames that are not covered by any signal bit or that arrive beyond the declared length.</summary>
    public long UndecodableBytes => UnmatchedIds.Sum(id => id.PayloadBytes) + Messages.Sum(m => m.UndecodableBytes);
}

/// <summary>
/// Trace x DBC consistency summary: which recorded IDs the database does not know, which bytes of
/// known messages no signal covers (or are missing because the frame is shorter than declared),
/// and which signals leave their declared physical limits. Matching and decoding reuse
/// <see cref="DbcDecoder"/> (including PGN matching and multiplexing), nothing is re-implemented.
/// Error and remote frames are ignored. Signals with Minimum == Maximum have no declared range and
/// are not range-checked. CDD/ODX diagnostic coverage of the same trace is reported separately by
/// <c>OfflineAnalysisAnalyzer</c> (catalog coverage rows).
/// </summary>
public static class TraceDbcCoverageAnalyzer
{
    public static TraceDbcCoverageReport Analyze(
        IReadOnlyList<CanFrame> frames, DbcDatabase database, DbcDecoderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentNullException.ThrowIfNull(database);
        var decoder = new DbcDecoder(database, options ?? DbcDecoderOptions.Default);

        var unmatched = new Dictionary<(int, uint, bool), (long Frames, long Bytes, int First)>();
        var perMessage = new Dictionary<(int, uint, bool), MessageAcc>();
        var ranges = new Dictionary<(string Message, string Signal), RangeAcc>();
        long total = 0, matched = 0;

        for (var index = 0; index < frames.Count; index++)
        {
            var frame = frames[index];
            if (frame.IsError || frame.Flags.HasFlag(CanFrameFlags.Remote)) continue;
            total++;
            var key = (frame.Channel, frame.Identifier, frame.IsExtended);
            if (!decoder.TryGetMessage(frame, out var message))
            {
                unmatched.TryGetValue(key, out var acc);
                unmatched[key] = (acc.Frames + 1, acc.Bytes + frame.DataLength, acc.Frames == 0 ? index : acc.First);
                continue;
            }
            matched++;
            if (!perMessage.TryGetValue(key, out var m))
                perMessage[key] = m = new MessageAcc(message);
            m.Frames++;
            if (frame.DataLength < message.Length)
            {
                m.Short++;
                if (m.FirstShort < 0) m.FirstShort = index;
            }
            else if (frame.DataLength > message.Length)
            {
                m.Long++;
                m.ExtraBytes += frame.DataLength - message.Length;
            }

            foreach (var value in decoder.Decode(frame))
            {
                var signal = message.Signals.FirstOrDefault(s => s.Name == value.Signal);
                if (signal is null || !double.IsFinite(value.PhysicalValue)) continue;
                if (!ranges.TryGetValue((message.Name, signal.Name), out var r))
                    ranges[(message.Name, signal.Name)] = r = new RangeAcc(message.Name, signal);
                r.Add(value.PhysicalValue, index);
            }
        }

        var messages = perMessage.Select(kv => kv.Value.ToCoverage(kv.Key.Item1))
            .OrderBy(c => c.Message, StringComparer.Ordinal).ThenBy(c => c.Channel).ToArray();
        var unmatchedIds = unmatched
            .Select(kv => new UnmatchedCanId(kv.Key.Item1, kv.Key.Item2, kv.Key.Item3, kv.Value.Frames, kv.Value.Bytes, kv.Value.First))
            .OrderByDescending(u => u.FrameCount).ThenBy(u => u.Channel).ThenBy(u => u.Identifier).ThenBy(u => u.IsExtended)
            .ToArray();
        var outOfRange = ranges.Values.Where(r => r.OutOfRange > 0).Select(r => r.ToReport())
            .OrderByDescending(r => r.OutOfRangeSamples).ThenBy(r => r.Message, StringComparer.Ordinal).ThenBy(r => r.Signal, StringComparer.Ordinal)
            .ToArray();
        return new TraceDbcCoverageReport(database.Name, total, matched, unmatchedIds, messages, outOfRange);
    }

    private sealed class MessageAcc(DbcMessage message)
    {
        public long Frames, Short, Long, ExtraBytes;
        public int FirstShort = -1;

        public MessageCoverage ToCoverage(int channel)
        {
            var covered = new bool[Math.Max(0, message.Length)];
            foreach (var signal in message.Signals)
            {
                if (signal.BitLength is < 1 or > 64 || signal.StartBit < 0) continue;
                foreach (var bit in DbcSignalInspector.PhysicalBits(signal))
                    if (bit >= 0 && bit / 8 < covered.Length) covered[bit / 8] = true;
            }
            var uncovered = Enumerable.Range(0, covered.Length).Where(i => !covered[i]).ToArray();
            // Bytes missing from short frames are "not decodable" too, but are counted as lost data, not payload.
            var undecodable = Frames * uncovered.Length + ExtraBytes;
            return new MessageCoverage(message.Name, message.Identifier, message.IsExtended, channel, message.Length,
                Frames, Short, Long, uncovered, undecodable, FirstShort);
        }
    }

    private sealed class RangeAcc(string message, DbcSignal signal)
    {
        public long Samples, OutOfRange;
        public double Min = double.PositiveInfinity, Max = double.NegativeInfinity;
        public int First = -1;


        public void Add(double value, int frameIndex)
        {
            Samples++;
            Min = Math.Min(Min, value);
            Max = Math.Max(Max, value);
            if (signal.Minimum == signal.Maximum) return; // no declared range
            var lo = Math.Min(signal.Minimum, signal.Maximum);
            var hi = Math.Max(signal.Minimum, signal.Maximum);
            var eps = 1e-9 * Math.Max(1.0, hi - lo);
            if (value < lo - eps || value > hi + eps)
            {
                OutOfRange++;
                if (First < 0) First = frameIndex;
            }
        }

        public OutOfRangeSignal ToReport() =>
            new(message, signal.Name, signal.Unit, Samples, OutOfRange, Min, Max, signal.Minimum, signal.Maximum, First);
    }
}
