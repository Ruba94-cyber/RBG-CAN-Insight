using System.Buffers.Binary;
using System.Collections;
using System.IO.MemoryMappedFiles;
using RbgCanInsight.Domain;

namespace RbgCanInsight.TraceIO;

/// <summary>
/// Fixed-record, memory-mapped, disk-backed store of CAN frames for traces too large to hold
/// comfortably in RAM (tens of millions of frames). Implements <see cref="IReadOnlyList{T}"/>
/// directly — the same interface every consumer in this codebase already accepts
/// (<c>TraceStatistics</c>, <c>SignalTimelineBuilder</c>, <c>UdsTransactionAnalyzer</c>,
/// <c>VirtualRowList</c>, every exporter) — so none of them need to change to work against a
/// disk-backed trace instead of an in-memory <see cref="List{CanFrame}"/>; only the import path
/// that constructs the store differs. See <see cref="DiskBackedCanFrameStoreWriter"/> for how a
/// store is built, and docs/CANALYZER_PARITY_ROADMAP.md ("Disk-Backed Frame Store Architecture")
/// for the wider design this is one piece of.
/// </summary>
public sealed class DiskBackedCanFrameStore : IReadOnlyList<CanFrame>, IDisposable
{
    /// <summary>channel(4) + identifier(4) + timestamp(8) + flags(1) + dataLength(1) +
    /// declaredLength(1) + recordMarker(1) +
    /// sequence(8) + pad(4) + payload(64) = 96 bytes, one fixed record per frame regardless of its
    /// actual payload length (classic frames leave most of the 64 bytes as trailing zeros).</summary>
    public const int RecordSize = 96;

    private readonly MemoryMappedFile _mappedFile;
    private readonly MemoryMappedViewAccessor _accessor;
    private readonly string _path;
    private readonly bool _deleteFileOnDispose;
    private bool _disposed;

    private DiskBackedCanFrameStore(
        string path, MemoryMappedFile mappedFile, MemoryMappedViewAccessor accessor, int count, bool deleteFileOnDispose)
    {
        _path = path;
        _mappedFile = mappedFile;
        _accessor = accessor;
        Count = count;
        _deleteFileOnDispose = deleteFileOnDispose;
    }

    public int Count { get; }

    // accessor.ReadArray needs an actual byte[] (not a Span<byte>); this app already runs trace
    // analysis on a background thread while the UI thread can concurrently index into the same
    // frame list (e.g. DataGrid virtualization during RebuildAnalysisAsync), so the scratch buffer
    // is per-thread rather than a shared instance field, which would otherwise be a data race.
    [ThreadStatic]
    private static byte[]? _threadRecordScratch;

    public CanFrame this[int index]
    {
        get
        {
            if ((uint)index >= (uint)Count)
            {
                throw new ArgumentOutOfRangeException(nameof(index));
            }

            var buffer = _threadRecordScratch ??= new byte[RecordSize];
            _accessor.ReadArray((long)index * RecordSize, buffer, 0, RecordSize);
            return DecodeRecord(buffer);
        }
    }

    public IEnumerator<CanFrame> GetEnumerator()
    {
        for (var index = 0; index < Count; index++)
        {
            yield return this[index];
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>Opens a store previously built by <see cref="DiskBackedCanFrameStoreWriter"/>.
    /// <paramref name="deleteFileOnDispose"/> should be true for a temporary store created for one
    /// session (the common case: promoted automatically during import) and false for a store the
    /// caller manages the lifetime of independently.</summary>
    public static DiskBackedCanFrameStore Open(string path, bool deleteFileOnDispose = true)
    {
        var length = new FileInfo(path).Length;
        if (length == 0 || length % RecordSize != 0)
            throw new InvalidDataException(
                $"Disk-backed CAN frame store length {length:N0} is not a non-empty multiple of {RecordSize} bytes. " +
                "The snapshot may be partial or truncated.");
        if (length / RecordSize > int.MaxValue)
            throw new NotSupportedException(
                "Disk-backed frame store exceeds the supported 32-bit frame-index range.");
        var count = checked((int)(length / RecordSize));
        var mappedFile = MemoryMappedFile.CreateFromFile(
            path, FileMode.Open, mapName: null, capacity: 0, MemoryMappedFileAccess.Read);
        var accessor = mappedFile.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
        return new DiskBackedCanFrameStore(path, mappedFile, accessor, count, deleteFileOnDispose);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _accessor.Dispose();
        _mappedFile.Dispose();
        if (_deleteFileOnDispose)
        {
            try
            {
                File.Delete(_path);
            }
            catch
            {
                // Best effort: a leftover temp file is a cleanup nuisance, not a correctness issue.
            }
        }
    }

    internal static void EncodeRecord(Span<byte> destination, in CanFrame frame)
    {
        destination[..RecordSize].Clear();
        BinaryPrimitives.WriteInt32LittleEndian(destination, frame.Channel);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[4..], frame.Identifier);
        BinaryPrimitives.WriteInt64LittleEndian(destination[8..], frame.TimestampNanoseconds);
        destination[16] = (byte)frame.Flags;
        destination[17] = frame.DataLength;
        // Spare record bytes now carry the RTR requested DLC; old spill files
        // without marker remain readable as dataLength == declaredLength.
        destination[18] = frame.DeclaredDataLength;
        destination[19] = 0xA5;
        BinaryPrimitives.WriteInt64LittleEndian(destination[20..], frame.Sequence);
        frame.CopyPayloadTo(destination.Slice(32, frame.DataLength));
    }

    internal static CanFrame DecodeRecord(ReadOnlySpan<byte> source)
    {
        var channel = BinaryPrimitives.ReadInt32LittleEndian(source);
        var identifier = BinaryPrimitives.ReadUInt32LittleEndian(source[4..]);
        var timestamp = BinaryPrimitives.ReadInt64LittleEndian(source[8..]);
        var flags = (CanFrameFlags)source[16];
        var dataLength = source[17];
        if (dataLength > 64)
            throw new InvalidDataException(
                $"Disk-backed CAN frame store record declares invalid {dataLength}-byte CAN payload.");
        var sequence = BinaryPrimitives.ReadInt64LittleEndian(source[20..]);
        var declaredLength = source[19] == 0xA5 ? source[18] : dataLength;
        if (flags.HasFlag(CanFrameFlags.Remote) && declaredLength > 8)
            throw new InvalidDataException("RTR frame requests more than 8 bytes.");
        if (!flags.HasFlag(CanFrameFlags.Remote) && declaredLength != dataLength)
            throw new InvalidDataException("Data-frame record has inconsistent payload and declared lengths.");
        return new CanFrame(channel, identifier, source.Slice(32, dataLength), timestamp,
            flags, sequence, declaredLength);
    }
}

/// <summary>Sequentially writes frames to a fixed-record file as they are parsed during import —
/// no need to hold the whole trace in memory even while building the store — then hands off to a
/// read-only, randomly-accessible <see cref="DiskBackedCanFrameStore"/>.</summary>
public sealed class DiskBackedCanFrameStoreWriter : IDisposable
{
    private readonly FileStream _stream;
    private readonly byte[] _recordBuffer = new byte[DiskBackedCanFrameStore.RecordSize];
    private readonly bool _deleteIncompleteFileOnDispose;
    private bool _completed;

    public DiskBackedCanFrameStoreWriter(string path, bool deleteIncompleteFileOnDispose = true)
    {
        Path = path;
        _deleteIncompleteFileOnDispose = deleteIncompleteFileOnDispose;
        _stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
    }

    public string Path { get; }
    public int Count { get; private set; }

    public void Append(in CanFrame frame)
    {
        if (_completed) throw new InvalidOperationException("Frame store writer is already finalized.");
        if (Count == int.MaxValue)
            throw new NotSupportedException("Frame store cannot represent more than Int32.MaxValue frames.");
        DiskBackedCanFrameStore.EncodeRecord(_recordBuffer, frame);
        _stream.Write(_recordBuffer, 0, _recordBuffer.Length);
        Count++;
    }

    /// <summary>Flushes and closes the write stream, then opens the same file for random-access
    /// reading. The writer must not be used again afterward.</summary>
    public DiskBackedCanFrameStore CompleteAndOpenForReading(bool deleteFileOnDispose = true)
    {
        if (_completed) throw new InvalidOperationException("Frame store writer is already finalized.");
        _stream.Flush();
        _stream.Dispose();
        var store = DiskBackedCanFrameStore.Open(Path, deleteFileOnDispose);
        _completed = true; // only after the completed snapshot opens successfully
        return store;
    }

    public void Dispose()
    {
        if (_completed) return;
        _stream.Dispose();
        if (_deleteIncompleteFileOnDispose)
        {
            try { File.Delete(Path); }
            catch (IOException) { /* Best-effort cleanup; never mask original import failure. */ }
            catch (UnauthorizedAccessException) { /* Best-effort cleanup. */ }
        }
    }
}
