using RbgCanInsight.Domain;

namespace RbgCanInsight.TraceIO;

/// <summary>An imported capture: an in-memory list for ordinary traces or a memory-mapped
/// disk-backed store for very large ones. Disposing releases (and deletes) any spill file; the
/// frames must not be read afterwards.</summary>
public sealed class CanFrameCollection : IDisposable
{
    private readonly IDisposable? _owner;

    internal CanFrameCollection(IReadOnlyList<CanFrame> frames, IDisposable? owner)
    {
        Frames = frames;
        _owner = owner;
    }

    public IReadOnlyList<CanFrame> Frames { get; }
    public bool IsDiskBacked => _owner is not null;
    public void Dispose() => _owner?.Dispose();
}

/// <summary>
/// Collects frames while a trace is imported. Frames stay in an ordinary in-memory list until
/// <c>spillThresholdFrames</c> is reached; from then on the whole capture is streamed to a
/// fixed-record file and exposed through <see cref="DiskBackedCanFrameStore"/>. Nothing is
/// dropped or truncated either way, and the in-memory result for small traces is unchanged.
/// If the source was not in chronological order, the result is sorted by (timestamp, sequence)
/// — for a spilled capture by sorting an index array and writing a second ordered store, so
/// the frames themselves are never all held in memory at once.
/// </summary>
public sealed class AdaptiveCanFrameCollector : IDisposable
{
    public const int DefaultSpillThresholdFrames = 3_000_000;

    private readonly int _spillThreshold;
    private readonly string _spillDirectory;
    private List<CanFrame>? _memory;
    private DiskBackedCanFrameStoreWriter? _writer;
    private bool _completed;

    public AdaptiveCanFrameCollector(
        int initialCapacity,
        int spillThresholdFrames = DefaultSpillThresholdFrames,
        string? spillDirectory = null)
    {
        if (spillThresholdFrames < 1) throw new ArgumentOutOfRangeException(nameof(spillThresholdFrames));
        _spillThreshold = spillThresholdFrames;
        _spillDirectory = spillDirectory ?? DefaultSpillDirectory;
        _memory = new List<CanFrame>(Math.Max(0, Math.Min(initialCapacity, spillThresholdFrames)));
    }

    public static string DefaultSpillDirectory { get; } =
        Path.Combine(Path.GetTempPath(), "RbgCanInsight", "spill");

    public int Count => _writer?.Count ?? _memory?.Count ?? 0;
    public bool HasSpilled => _writer is not null;

    public void Add(in CanFrame frame)
    {
        if (_completed) throw new InvalidOperationException("Collector is already completed.");
        if (_writer is not null)
        {
            _writer.Append(frame);
            return;
        }

        _memory!.Add(frame);
        if (_memory.Count >= _spillThreshold) Spill();
    }

    private void Spill()
    {
        Directory.CreateDirectory(_spillDirectory);
        var writer = new DiskBackedCanFrameStoreWriter(NewSpillPath());
        try
        {
            foreach (var frame in _memory!) writer.Append(frame);
        }
        catch
        {
            writer.Dispose();
            throw;
        }

        _writer = writer;
        _memory = null; // release the large in-memory buffer
    }

    private string NewSpillPath() =>
        Path.Combine(_spillDirectory, $"rbg-frames-{Guid.NewGuid():N}.bin");

    private static int Compare(in CanFrame a, in CanFrame b)
    {
        var byTime = a.TimestampNanoseconds.CompareTo(b.TimestampNanoseconds);
        return byTime != 0 ? byTime : a.Sequence.CompareTo(b.Sequence);
    }

    private readonly record struct SortKey(long Timestamp, long Sequence, int Index) : IComparable<SortKey>
    {
        public int CompareTo(SortKey other)
        {
            var byTime = Timestamp.CompareTo(other.Timestamp);
            if (byTime != 0) return byTime;
            var bySequence = Sequence.CompareTo(other.Sequence);
            return bySequence != 0 ? bySequence : Index.CompareTo(other.Index);
        }
    }

    /// <summary>Finishes the import. <paramref name="sortByTime"/> must be true when the source
    /// arrived out of chronological order.</summary>
    public CanFrameCollection Complete(bool sortByTime)
    {
        if (_completed) throw new InvalidOperationException("Collector is already completed.");
        _completed = true;

        if (_memory is not null)
        {
            if (sortByTime) _memory.Sort(static (a, b) => Compare(a, b));
            // List doubling can leave up to half of the buffer unused; give that memory back.
            if (_memory.Capacity > _memory.Count + _memory.Count / 8) _memory.TrimExcess();
            return new CanFrameCollection(_memory, null);
        }

        var store = _writer!.CompleteAndOpenForReading(deleteFileOnDispose: true);
        _writer = null;
        if (!sortByTime) return new CanFrameCollection(store, store);

        try
        {
            // Sort compact (timestamp, sequence, position) keys held in RAM (20 bytes per frame)
            // instead of comparing records through the memory-mapped file, which was ~10x slower.
            // Ties fall back to the original position so equal keys keep their recorded order.
            var keys = new SortKey[store.Count];
            var alreadyOrdered = true;
            for (var i = 0; i < keys.Length; i++)
            {
                var frame = store[i];
                keys[i] = new SortKey(frame.TimestampNanoseconds, frame.Sequence, i);
                if (i > 0 && keys[i].CompareTo(keys[i - 1]) < 0) alreadyOrdered = false;
            }

            if (alreadyOrdered) return new CanFrameCollection(store, store);
            Array.Sort(keys);

            using var sortedWriter = new DiskBackedCanFrameStoreWriter(NewSpillPath());
            foreach (var key in keys) sortedWriter.Append(store[key.Index]);
            var sortedStore = sortedWriter.CompleteAndOpenForReading(deleteFileOnDispose: true);
            store.Dispose();
            return new CanFrameCollection(sortedStore, sortedStore);
        }
        catch
        {
            store.Dispose();
            throw;
        }
    }

    /// <summary>Abandons an unfinished import (failure or cancellation) and removes its spill file.</summary>
    public void Dispose()
    {
        if (_completed) return;
        _writer?.Dispose();
        _writer = null;
        _memory = null;
    }

    /// <summary>Removes spill files left behind by a crashed session. Files still open in another
    /// running instance are locked and are skipped by the failed delete.</summary>
    public static void CleanUpStaleSpillFiles(string? spillDirectory = null, TimeSpan? olderThan = null)
    {
        var directory = spillDirectory ?? DefaultSpillDirectory;
        if (!Directory.Exists(directory)) return;
        var cutoff = DateTime.UtcNow - (olderThan ?? TimeSpan.FromHours(1));
        foreach (var file in Directory.EnumerateFiles(directory, "rbg-frames-*.bin"))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff) File.Delete(file);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
