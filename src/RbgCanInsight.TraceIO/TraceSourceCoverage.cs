using System.Globalization;
using RbgCanInsight.Domain;

namespace RbgCanInsight.TraceIO;

/// <summary>One canonical physical CAN bus/ID identity across source recordings.
/// The original channel is used, not the post-merge channel offset.</summary>
public sealed record TraceSourceCoverageRow(
    int OriginalChannel, uint Identifier, bool Extended,
    long[] Counts, long TotalFrames, long FirstTimestampNanoseconds,
    long LastTimestampNanoseconds, int PresentSources)
{
    public string ChannelText => $"Ch {OriginalChannel + 1}";
    public string IdText => $"0x{Identifier.ToString(Extended ? "X8" : "X3", CultureInfo.InvariantCulture)}" +
                            (Extended ? "x" : "");
    public string PresenceText => $"{PresentSources}/{Counts.Length}";
    public string Finding => PresentSources == Counts.Length ? "All sources" :
        PresentSources == 1 ? "Single source" : "Partial coverage";
    public string FirstSeconds => (FirstTimestampNanoseconds / 1e9)
        .ToString("F6", CultureInfo.InvariantCulture);
    public string LastSeconds => (LastTimestampNanoseconds / 1e9)
        .ToString("F6", CultureInfo.InvariantCulture);
}

public sealed record TraceSourceCoverageReport(
    IReadOnlyList<string> SourcePaths, TraceMergeMode MergeMode,
    IReadOnlyList<TraceSourceCoverageRow> Rows,
    int TotalIdentifiers, int SingleSourceIdentifiers,
    int SharedIdentifiers, int PartialIdentifiers, bool IsTruncated)
{
    public string Summary =>
        $"{SourcePaths.Count} recordings · {TotalIdentifiers:N0} bus/ID identities · " +
        $"{SingleSourceIdentifiers:N0} in only one recording · " +
        $"{SharedIdentifiers:N0} in every recording · " +
        $"{PartialIdentifiers:N0} in some but not all" +
        (IsTruncated ? $" · top {Rows.Count:N0} rows shown" : "");
}

/// <summary>
/// Cross-file coverage table for an already loaded merged trace. Unlike grouping on
/// the merged channel, it reverses per-source channel remapping so CAN ID coverage
/// is compared on physical source channels. A presence difference is observational:
/// recording durations, bus connections and clocks may not be comparable.
/// </summary>
public static class TraceSourceCoverage
{
    private readonly record struct Key(int Channel, uint Identifier, bool Extended);

    private sealed class Accumulator
    {
        public readonly Dictionary<int, long> Counts = new();
        public long Total;
        public long First;
        public long Last;
    }

    public static TraceSourceCoverageReport Analyze(
        IReadOnlyList<CanFrame> frames, TraceSessionSourceIndex sourceIndex,
        TraceMergeMode mode, int maxRows = 5_000)
    {
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentNullException.ThrowIfNull(sourceIndex);
        if (sourceIndex.Count != frames.Count)
            throw new ArgumentException("Source index must correspond to the exact merged frame snapshot.", nameof(sourceIndex));
        if (maxRows < 1) throw new ArgumentOutOfRangeException(nameof(maxRows));

        var counts = new Dictionary<Key, Accumulator>();
        for (var i = 0; i < frames.Count; i++)
        {
            var f = frames[i];
            var location = sourceIndex.Locate(i);
            var key = new Key(checked(f.Channel - location.ChannelOffset), f.Identifier, f.IsExtended);
            if (!counts.TryGetValue(key, out var acc))
                counts[key] = acc = new Accumulator
                {
                    First = f.TimestampNanoseconds, Last = f.TimestampNanoseconds
                };
            acc.Total++;
            acc.Counts[location.SourceIndex] = acc.Counts.GetValueOrDefault(location.SourceIndex) + 1;
            if (f.TimestampNanoseconds < acc.First) acc.First = f.TimestampNanoseconds;
            if (f.TimestampNanoseconds > acc.Last) acc.Last = f.TimestampNanoseconds;
        }

        var sourceCount = sourceIndex.SourceCount;
        var unique = sourceCount > 1 ? counts.Values.Count(x => x.Counts.Count == 1) : 0;
        var shared = counts.Values.Count(x => x.Counts.Count == sourceCount);
        var partial = counts.Count - unique - shared;
        // Aggregate once across all keys, then materialize dynamic per-source count
        // arrays only for the rows displayed by the virtualized report grid.
        var rows = counts.OrderBy(x => x.Value.Counts.Count)
            .ThenByDescending(x => x.Value.Total)
            .ThenBy(x => x.Key.Channel)
            .ThenBy(x => x.Key.Identifier)
            .ThenBy(x => x.Key.Extended)
            .Take(maxRows)
            .Select(pair =>
            {
                var perSource = new long[sourceCount];
                foreach (var (source, value) in pair.Value.Counts)
                    perSource[source] = value;
                return new TraceSourceCoverageRow(pair.Key.Channel, pair.Key.Identifier,
                    pair.Key.Extended, perSource, pair.Value.Total,
                    pair.Value.First, pair.Value.Last, pair.Value.Counts.Count);
            }).ToArray();

        return new TraceSourceCoverageReport(sourceIndex.Paths, mode, rows,
            counts.Count, unique, shared, partial, counts.Count > rows.Length);
    }
}
