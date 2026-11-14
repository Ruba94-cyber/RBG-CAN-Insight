using RbgCanInsight.Domain;

namespace RbgCanInsight.TraceIO;

/// <summary>What a row in the combined trace came from. The source frame index is in
/// chronological order within that original file (not the original byte offset).
/// No SHA is implied: callers must verify the file has not changed before re-navigation.</summary>
public readonly record struct TraceSourceLocation(
    string Path, int SourceIndex, int SourceFrameIndex,
    long TimeShiftNanoseconds, int ChannelOffset);

public sealed record TraceSessionSourceSummary(
    string Path, long Frames, int Channels, long OutOfOrderTimestamps,
    bool RequiresReview, string Format,
    long? FirstTimestampNanoseconds = null, long? LastTimestampNanoseconds = null)
{
    public double? DurationSeconds => FirstTimestampNanoseconds is { } first &&
        LastTimestampNanoseconds is { } last
        ? Math.Max(0, (last / 1e9) - (first / 1e9))
        : null;
}

/// <summary>
/// Compact map from merged row index to physical source file and its own chronological
/// frame index. Uses 1/2/4 bytes for source IDs and 4 bytes for the original position;
/// does not retain additional CanFrame objects or payloads.
/// </summary>
public sealed class TraceSessionSourceIndex
{
    private readonly byte[]? _ids8;
    private readonly ushort[]? _ids16;
    private readonly int[]? _ids32;
    private readonly int[] _positions;
    private readonly string[] _paths;
    private readonly long[] _shifts;
    private readonly int[] _offsets;

    internal TraceSessionSourceIndex(
        IReadOnlyList<string> paths, IReadOnlyList<long> shifts, IReadOnlyList<int> offsets,
        byte[]? ids8, ushort[]? ids16, int[]? ids32, int[] positions)
    {
        _paths = paths.ToArray();
        _shifts = shifts.ToArray();
        _offsets = offsets.ToArray();
        _ids8 = ids8;
        _ids16 = ids16;
        _ids32 = ids32;
        _positions = positions;
    }

    public int Count => _positions.Length;
    public IReadOnlyList<string> Paths => _paths;
    public int SourceCount => _paths.Length;

    /// <summary>Returns the source file number for a merged frame in O(1),
    /// without resolving a filename or allocating a row object. Useful for
    /// interactive source-focused filters over large disk-backed timelines.</summary>
    public int SourceAt(int mergedFrameIndex)
    {
        if ((uint)mergedFrameIndex >= (uint)_positions.Length)
            throw new ArgumentOutOfRangeException(nameof(mergedFrameIndex));
        return _ids8 is not null ? _ids8[mergedFrameIndex] :
            _ids16 is not null ? _ids16[mergedFrameIndex] : _ids32![mergedFrameIndex];
    }

    public TraceSourceLocation Locate(int mergedFrameIndex)
    {
        var source = SourceAt(mergedFrameIndex);
        return new TraceSourceLocation(
            _paths[source], source, _positions[mergedFrameIndex],
            _shifts[source], _offsets[source]);
    }

    public bool TryLocate(int mergedFrameIndex, out TraceSourceLocation source)
    {
        if ((uint)mergedFrameIndex >= (uint)_positions.Length)
        {
            source = default;
            return false;
        }
        source = Locate(mergedFrameIndex);
        return true;
    }

    internal sealed class Builder(int sourceCount)
    {
        private readonly List<byte>? _ids8 = sourceCount <= 256 ? new() : null;
        private readonly List<ushort>? _ids16 = sourceCount is > 256 and <= 65536 ? new() : null;
        private readonly List<int>? _ids32 = sourceCount > 65536 ? new() : null;
        private readonly List<int> _positions = new();

        public void Add(int sourceIndex, int sourcePosition)
        {
            _ids8?.Add(checked((byte)sourceIndex));
            _ids16?.Add(checked((ushort)sourceIndex));
            _ids32?.Add(sourceIndex);
            _positions.Add(sourcePosition);
        }

        public TraceSessionSourceIndex Build(
            IReadOnlyList<string> paths, IReadOnlyList<long> shifts, IReadOnlyList<int> offsets) =>
            new(paths, shifts, offsets, _ids8?.ToArray(), _ids16?.ToArray(),
                _ids32?.ToArray(), _positions.ToArray());
    }
}

public sealed record TraceSessionImportResult(
    TraceSessionSourceIndex SourceIndex,
    IReadOnlyList<TraceSessionSourceSummary> Sources);
