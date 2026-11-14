using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text;
using RbgCanInsight.Domain;

namespace RbgCanInsight.TraceIO;

/// <summary>Tunable safety limits of the streaming MDF4 reader. None of them is a file-size
/// limit: the file is read lazily through a seekable stream, so only these bounded working
/// buffers are ever held in memory.</summary>
public sealed record Mdf4ReadOptions
{
    /// <summary>Largest single block the reader will hold in memory at once: a metadata block
    /// (channel, text, link list), one decompressed DZ block, or one pending record.
    /// Larger declarations are treated as corrupt or hostile. Default 256 MiB.</summary>
    public long MaxBlockBytes { get; init; } = 256L * 1024 * 1024;

    /// <summary>Size of the read buffer used for uncompressed DT/DV data. Default 1 MiB.</summary>
    public int ChunkBytes { get; init; } = 1 << 20;

    /// <summary>Optional probe filled during the read (for tests and diagnostics).</summary>
    public Mdf4ReadStats? Stats { get; init; }

    public static Mdf4ReadOptions Default { get; } = new();
}

/// <summary>Observed working-set of a streaming MDF4 read.</summary>
public sealed class Mdf4ReadStats
{
    /// <summary>Largest number of data bytes buffered at one moment (read chunk plus carried
    /// partial record, or a compressed + decompressed DZ block).</summary>
    public long PeakBufferedBytes { get; private set; }
    public long DataBytesRead { get; private set; }
    public int DataBlocks { get; private set; }

    internal void Observe(long buffered) { if (buffered > PeakBufferedBytes) PeakBufferedBytes = buffered; }
    internal void Block(long bytes) { DataBlocks++; DataBytesRead += bytes; }
}

/// <summary>
/// Streaming ASAM MDF 4.x (.mf4/.mdf) reader focused on CAN bus logging as produced by common
/// data loggers: channel groups carrying a CAN_DataFrame structure with ID / data length /
/// DataBytes members plus a time master channel. The file is read through a seekable stream;
/// DT/DV blocks, DL lists, HL header lists and zlib-compressed DZ blocks (plain and transposed)
/// are decoded block by block, so memory stays bounded by <see cref="Mdf4ReadOptions"/> instead of
/// the file size. Frames are emitted in record order per data group and merged by timestamp across
/// data groups. Signal-oriented MF4 files without raw CAN frames are rejected with a clear message.
/// </summary>
public static class Mdf4Importer
{
    private const int MaxCanDataGroups = 512;
    private const int MaxVisitedBlocks = 4_000_000;

    public static async IAsyncEnumerable<CanFrame> ReadAsync(
        string path,
        [EnumeratorCancellation] CancellationToken cancellationToken = default,
        TraceImportHealth? diagnostics = null,
        Mdf4ReadOptions? options = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await Task.CompletedTask;
        Stream Open() => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
            bufferSize: 64 * 1024, FileOptions.RandomAccess);
        foreach (var frame in ReadStream(Open, diagnostics, options ?? Mdf4ReadOptions.Default))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return frame;
        }
    }

    internal static IEnumerable<CanFrame> Read(byte[] file, TraceImportHealth? diagnostics = null,
        Mdf4ReadOptions? options = null) =>
        ReadStream(() => new MemoryStream(file, writable: false), diagnostics,
            options ?? Mdf4ReadOptions.Default);

    private sealed record DataGroupPlan(int RecordIdSize, List<GroupLayout> Groups, long DataLink);

    private static IEnumerable<CanFrame> ReadStream(
        Func<Stream> open, TraceImportHealth? diagnostics, Mdf4ReadOptions options)
    {
        if (options.MaxBlockBytes < 1024 || options.ChunkBytes < 64)
            throw new ArgumentOutOfRangeException(nameof(options), "MDF4 read limits are too small.");
        var plans = new List<DataGroupPlan>();
        bool unfinalized;
        using (var meta = open())
        {
            // Loggers such as the CSS Electronics CANedge write "UnFinMF " until the file is finalized;
            // the data are readable, only some length/count fields may be placeholders.
            if (meta.Length < 128)
                throw new InvalidDataException("Not an MDF file.");
            var ident = new byte[16];
            meta.Position = 0;
            meta.ReadExactly(ident);
            var magic = Encoding.ASCII.GetString(ident, 0, 8);
            if (!magic.StartsWith("MDF", StringComparison.Ordinal) && magic != "UnFinMF ")
                throw new InvalidDataException("Not an MDF file.");
            var version = Encoding.ASCII.GetString(ident, 8, 4).Trim();
            if (!version.StartsWith('4'))
                throw new NotSupportedException($"MDF version {version} is not supported (MDF 4.x only).");

            unfinalized = magic == "UnFinMF ";
            if (unfinalized)
                diagnostics?.RecordFileMetadataWarning("MDF_UNFINALIZED",
                    "The MF4 file is unfinalized (written by a logger that was not closed cleanly or never finalized). " +
                    "The last data block is read up to the end of the file; a trailing partial record is ignored.");

            // HD block sits at offset 64; link[0] = first DG.
            var hd = Block.ReadHeader(meta, 64, options.MaxBlockBytes);
            var anyGroup = false;
            var visitedDg = new HashSet<long>();
            for (var dgLink = hd.Link(0); dgLink != 0;)
            {
                if (!visitedDg.Add(dgLink))
                    throw new InvalidDataException("Cyclic MDF4 data-group link.");
                var dg = Block.Load(meta, dgLink, options.MaxBlockBytes);
                if (dg.Data.Length < 1)
                    throw new InvalidDataException("Truncated MDF4 data-group block.");
                var recordIdSize = dg.Data[0];

                var groups = new List<GroupLayout>();
                var visitedCg = new HashSet<long>();
                for (var cgLink = dg.Link(1); cgLink != 0;)
                {
                    if (!visitedCg.Add(cgLink))
                        throw new InvalidDataException("Cyclic MDF4 channel-group link.");
                    var cg = Block.Load(meta, cgLink, options.MaxBlockBytes);
                    var layout = GroupLayout.TryBuild(meta, cg, options.MaxBlockBytes);
                    if (layout is not null)
                    {
                        groups.Add(layout);
                        anyGroup |= layout.IsCanFrameGroup;
                        if (!layout.IsCanFrameGroup)
                            diagnostics?.RecordUnsupportedSourceObject(
                                "UNSUPPORTED_MDF_CHANNEL_GROUP",
                                "An MDF4 channel group does not contain a complete supported raw CAN_DataFrame layout; skipped.");
                    }
                    else
                        diagnostics?.RecordRejectedSourceRecord(
                            "MDF_INVALID_GROUP", "An MDF4 channel group definition could not be read.");

                    cgLink = cg.Link(0);
                }

                if (groups.Any(group => group.IsCanFrameGroup))
                {
                    if (recordIdSize is not (0 or 1 or 2 or 4 or 8))
                        throw new NotSupportedException($"MDF4 record identifier width {recordIdSize} is not supported.");
                    if (recordIdSize == 0 && groups.Count > 1)
                        throw new InvalidDataException(
                            "Multiple MDF4 record groups share a data block without record identifiers; their boundaries are ambiguous.");
                    if (groups.Any(group => (group.RecordLength <= 0 && !group.IsVariableLength) ||
                                            group.RecordLength > options.MaxBlockBytes))
                        throw new InvalidDataException("MDF4 contains a group with an invalid record length.");
                    if (plans.Count >= MaxCanDataGroups)
                        throw new NotSupportedException(
                            $"MDF4 file has more than {MaxCanDataGroups} CAN data groups.");
                    plans.Add(new DataGroupPlan(recordIdSize, groups, dg.Link(2)));
                }

                dgLink = dg.Link(0);
            }

            if (!anyGroup)
            {
                throw new NotSupportedException(
                    "This MF4 file does not contain raw CAN frames (CAN_DataFrame bus logging). " +
                    "Signal-only MF4 recordings are not supported yet.");
            }
        }

        var sources = plans.Select(plan => new DataGroupReader(plan, open, unfinalized, diagnostics, options)).ToList();
        var enumerators = new List<IEnumerator<CanFrame>>();
        try
        {
            long sequence = 0;
            if (sources.Count == 1)
            {
                foreach (var frame in sources[0].Frames())
                    yield return frame with { Sequence = ++sequence };
                yield break;
            }

            // Several data groups: k-way merge by timestamp, ties by data-group order then record order.
            var heap = new PriorityQueue<int, (long Timestamp, int Group, long Order)>();
            var heads = new CanFrame[sources.Count];
            var orders = new long[sources.Count];
            for (var index = 0; index < sources.Count; index++)
            {
                var enumerator = sources[index].Frames().GetEnumerator();
                enumerators.Add(enumerator);
                if (enumerator.MoveNext())
                {
                    heads[index] = enumerator.Current;
                    heap.Enqueue(index, (heads[index].TimestampNanoseconds, index, orders[index]++));
                }
            }

            while (heap.TryDequeue(out var group, out _))
            {
                yield return heads[group] with { Sequence = ++sequence };
                if (enumerators[group].MoveNext())
                {
                    heads[group] = enumerators[group].Current;
                    heap.Enqueue(group, (heads[group].TimestampNanoseconds, group, orders[group]++));
                }
            }
        }
        finally
        {
            foreach (var enumerator in enumerators) enumerator.Dispose();
        }
    }

    /// <summary>Streams one data group: walks its DT/DV/DZ/DL/HL chain lazily and decodes records,
    /// carrying a partially received record across chunk and block boundaries.</summary>
    private sealed class DataGroupReader(
        DataGroupPlan plan, Func<Stream> open, bool unfinalized,
        TraceImportHealth? diagnostics, Mdf4ReadOptions options)
    {
        private readonly Dictionary<ulong, GroupLayout> _byId = plan.Groups
            .GroupBy(group => group.RecordId).ToDictionary(g => g.Key, g => g.First());
        private readonly GroupLayout? _single = plan.Groups.Count == 1 && plan.RecordIdSize == 0 ? plan.Groups[0] : null;
        private byte[] _carry = new byte[4096];
        private int _carryLength;
        private int _position;
        private bool _stopped;

        public IEnumerable<CanFrame> Frames()
        {
            if (plan.DataLink == 0) yield break;
            using var stream = open();
            var chunk = new byte[options.ChunkBytes];
            foreach (var data in EnumerateData(stream, chunk))
            {
                Append(data.Span);
                options.Stats?.Observe(chunk.Length + _carry.Length);
                foreach (var frame in Drain(final: false)) yield return frame;
                if (_stopped) yield break;
            }

            foreach (var frame in Drain(final: true)) yield return frame;
        }

        private void Append(ReadOnlySpan<byte> data)
        {
            if ((long)_carryLength + data.Length > options.MaxBlockBytes + options.ChunkBytes + 65536L)
                throw new InvalidDataException("A pending MDF4 record exceeds the safe buffer budget.");
            if (_carryLength + data.Length > _carry.Length)
                Array.Resize(ref _carry, (int)Math.Min(
                    Math.Max((long)_carry.Length * 2, (long)_carryLength + data.Length), int.MaxValue / 2));
            data.CopyTo(_carry.AsSpan(_carryLength));
            _carryLength += data.Length;
        }

        private IEnumerable<CanFrame> Drain(bool final)
        {
            _position = 0;
            while (!_stopped)
            {
                var status = Step(final, out var frame);
                if (status == StepStatus.Frame)
                    yield return frame;
                else if (status != StepStatus.Skipped)
                    break;
            }

            var leftover = _carryLength - _position;
            if (leftover > 0 && _position > 0)
                Buffer.BlockCopy(_carry, _position, _carry, 0, leftover);
            _carryLength = Math.Max(0, leftover);
            _position = 0;
        }

        private enum StepStatus { Frame, Skipped, NeedMore }

        private StepStatus Step(bool final, out CanFrame frame)
        {
            frame = default;
            var data = _carry.AsSpan(0, _carryLength);
            var offset = _position;
            if (offset >= data.Length) return StepStatus.NeedMore;
            GroupLayout? group;
            if (_single is not null)
            {
                group = _single;
            }
            else
            {
                if (offset + plan.RecordIdSize > data.Length)
                {
                    if (final)
                    {
                        diagnostics?.RecordRejectedSourceRecord(
                            "MDF_TRUNCATED_RECORD_ID",
                            "A trailing MDF4 record is missing some record identifier bytes.");
                        _stopped = true;
                    }
                    return StepStatus.NeedMore;
                }

                ulong recordId = plan.RecordIdSize switch
                {
                    1 => data[offset],
                    2 => BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]),
                    4 => BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]),
                    8 => BinaryPrimitives.ReadUInt64LittleEndian(data[offset..]),
                    _ => 0
                };
                offset += plan.RecordIdSize;
                if (!_byId.TryGetValue(recordId, out group))
                {
                    // Unknown record id: record length cannot be inferred.
                    diagnostics?.RecordRejectedSourceRecord(
                        "MDF_UNKNOWN_RECORD_ID",
                        "A record ID is not described by the active MDF4 channel groups; remaining DG records could not be decoded.");
                    _stopped = true;
                    return StepStatus.NeedMore;
                }

                if (group.IsVariableLength)
                {
                    // VLSD record: u32 length + payload.
                    if (offset + 4 > data.Length)
                    {
                        if (final)
                        {
                            diagnostics?.RecordRejectedSourceRecord(
                                "MDF_TRUNCATED_VLSD",
                                "A variable-length MDF4 record has no complete length field.");
                            _stopped = true;
                        }
                        return StepStatus.NeedMore;
                    }

                    var vlsdLength = BinaryPrimitives.ReadInt32LittleEndian(data[offset..]);
                    if (vlsdLength < 0 || vlsdLength > options.MaxBlockBytes ||
                        (final && (long)offset + 4 + vlsdLength > data.Length))
                    {
                        diagnostics?.RecordRejectedSourceRecord(
                            "MDF_INVALID_VLSD_LENGTH",
                            "A variable-length MDF4 record extends beyond the source block.");
                        _stopped = true;
                        return StepStatus.NeedMore;
                    }

                    if ((long)offset + 4 + vlsdLength > data.Length) return StepStatus.NeedMore;
                    _position = offset + 4 + vlsdLength;
                    return StepStatus.Skipped;
                }
            }

            if ((long)offset + group.RecordLength > data.Length)
            {
                if (final)
                {
                    diagnostics?.RecordRejectedSourceRecord(
                        "MDF_TRUNCATED_RECORD",
                        "The final MDF4 record is shorter than its declared byte length.");
                    _stopped = true;
                }
                return StepStatus.NeedMore;
            }

            var status = StepStatus.Skipped;
            if (group.IsCanFrameGroup)
            {
                try
                {
                    var parsed = group.ParseFrame(data.Slice(offset, group.RecordLength));
                    if (parsed is { } value)
                    {
                        frame = value;
                        status = StepStatus.Frame;
                    }
                    else
                        diagnostics?.RecordRejectedSourceRecord(
                            "MDF_UNDECODABLE_CAN_RECORD",
                            "A raw CAN_DataFrame record lacked sufficient supported fields.");
                }
                catch (Exception ex) when (ex is ArgumentException or OverflowException or InvalidDataException)
                {
                    diagnostics?.RecordRejectedSourceRecord(
                        "MDF_INVALID_CAN_RECORD",
                        "A CAN_DataFrame record contains invalid ID, DLC, timestamp or field bounds.");
                }
            }

            _position = offset + group.RecordLength;
            return status;
        }

        /// <summary>Walks the block chain with an explicit work stack (no recursion depth limit) and
        /// yields data in order: DT/DV in read-buffer chunks, DZ as one decompressed block.</summary>
        private IEnumerable<ReadOnlyMemory<byte>> EnumerateData(Stream stream, byte[] chunk)
        {
            var visited = new HashSet<long>();
            var pending = new Stack<long>();
            pending.Push(plan.DataLink);
            while (pending.Count > 0)
            {
                var link = pending.Pop();
                if (link == 0) continue;
                if (visited.Count >= MaxVisitedBlocks)
                    throw new InvalidDataException("MDF4 data-block count exceeds the safety limit.");
                if (!visited.Add(link))
                    throw new InvalidDataException("Cyclic or repeated MDF4 data-block link; refusing duplicate records.");
                var block = Block.ReadHeader(stream, link, options.MaxBlockBytes);
                switch (block.Id)
                {
                    case "##DT" or "##DV":
                    {
                        var length = block.DataLength;
                        if (unfinalized && length == 0)
                        {
                            // Unfinalized logger files leave the length of the last data block as a placeholder:
                            // the records simply run to the end of the file.
                            length = Math.Max(0, stream.Length - block.DataStart);
                        }

                        options.Stats?.Block(length);
                        var remaining = length;
                        stream.Position = block.DataStart;
                        while (remaining > 0)
                        {
                            var read = stream.Read(chunk, 0, (int)Math.Min(chunk.Length, remaining));
                            if (read <= 0) break;
                            remaining -= read;
                            yield return chunk.AsMemory(0, read);
                        }

                        break;
                    }
                    case "##DZ":
                        yield return InflateDz(stream, block);
                        break;
                    case "##DL":
                    {
                        // Data list: flags(u8) reserved(3) count(u32) ... links[1..count] = data blocks.
                        var header = new byte[8];
                        if (block.DataLength < 8)
                            throw new InvalidDataException("Truncated MDF4 data-list block.");
                        stream.Position = block.DataStart;
                        stream.ReadExactly(header);
                        var count = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
                        if (count > block.LinkCount - 1)
                            throw new InvalidDataException("MDF4 data-list child count exceeds the available links.");
                        // Children are read before the next list in the chain.
                        pending.Push(block.Link(0));
                        for (var index = (int)count - 1; index >= 0; index--)
                            pending.Push(block.Link(1 + index));
                        break;
                    }
                    case "##HL":
                        // Header list wraps a DL chain: link[0] = first DL.
                        pending.Push(block.Link(0));
                        break;
                    default:
                        // Never complete a nominally successful import after silently
                        // discarding a data block of an unimplemented MDF type.
                        throw new NotSupportedException(
                            $"MDF4 data block '{block.Id}' is not supported by the raw CAN importer.");
                }
            }
        }

        private byte[] InflateDz(Stream stream, Block block)
        {
            // DZ data: orgBlockType(2) zipType(u8) reserved(u8) zipParameter(u32)
            //          orgDataLength(u64) dataLength(u64) compressed data...
            if (block.DataLength < 24)
                throw new InvalidDataException("Truncated MDF4 DZ block metadata.");
            var meta = new byte[24];
            stream.Position = block.DataStart;
            stream.ReadExactly(meta);
            var zipType = meta[2];
            if (zipType is not (0 or 1))
                throw new NotSupportedException($"Unsupported MDF4 DZ zip type {zipType}.");
            var zipParameter = BinaryPrimitives.ReadUInt32LittleEndian(meta.AsSpan(4));
            var originalLength = BinaryPrimitives.ReadUInt64LittleEndian(meta.AsSpan(8));
            var compressedLength = BinaryPrimitives.ReadUInt64LittleEndian(meta.AsSpan(16));
            if (compressedLength > (ulong)(block.DataLength - 24) ||
                originalLength > (ulong)options.MaxBlockBytes ||
                compressedLength > (ulong)options.MaxBlockBytes)
                throw new InvalidDataException(
                    "MDF4 DZ declares impossible compressed size or excessive uncompressed size.");
            if (zipType == 1 && (zipParameter < 2 || zipParameter > originalLength ||
                zipParameter > int.MaxValue))
                throw new InvalidDataException("MDF4 transposed DZ declares an invalid row width.");

            options.Stats?.Observe((long)compressedLength + (long)originalLength * (zipType == 1 ? 2 : 1));
            options.Stats?.Block((long)originalLength);
            var compressed = new byte[(int)compressedLength];
            stream.ReadExactly(compressed);
            var data = new byte[(int)originalLength];
            using (var source = new MemoryStream(compressed, writable: false))
            using (var zlib = new ZLibStream(source, CompressionMode.Decompress))
            {
                var filled = 0;
                while (filled < data.Length)
                {
                    var read = zlib.Read(data, filled, data.Length - filled);
                    if (read == 0) break;
                    filled += read;
                }

                if (filled != data.Length)
                    throw new InvalidDataException("MDF4 DZ does not expand to its declared uncompressed length.");
                if (zlib.ReadByte() >= 0)
                    throw new InvalidDataException("MDF4 DZ expands beyond its declared uncompressed size.");
            }

            if (zipType == 1)
            {
                // Transposed deflate: bytes stored column-major over one record length.
                var columns = (int)zipParameter;
                var rows = data.Length / columns;
                var transposed = new byte[data.Length];
                for (var row = 0; row < rows; row++)
                    for (var column = 0; column < columns; column++)
                        transposed[row * columns + column] = data[column * rows + row];
                for (var index = rows * columns; index < data.Length; index++)
                    transposed[index] = data[index];
                data = transposed;
            }

            return data;
        }
    }

    /// <summary>An MDF block: id, links and (for metadata blocks) the loaded data section.</summary>
    private sealed class Block
    {
        private long[] _links = [];

        public string Id { get; private init; } = string.Empty;
        public long Offset { get; private init; }
        public long Length { get; private init; }
        public long LinkCount { get; private init; }
        public long DataStart => Offset + 24 + LinkCount * 8;
        public long DataLength => Length - 24 - LinkCount * 8;
        public byte[] Data { get; private set; } = [];

        public long Link(long index) => index >= 0 && index < LinkCount ? _links[index] : 0;

        public static Block ReadHeader(Stream stream, long offset, long maxBytes)
        {
            var streamLength = stream.Length;
            if (offset < 0 || offset > streamLength - 24)
                throw new InvalidDataException("MDF4 block points outside the source file.");
            Span<byte> header = stackalloc byte[24];
            stream.Position = offset;
            stream.ReadExactly(header);
            var blockLength = BinaryPrimitives.ReadUInt64LittleEndian(header[8..]);
            var links = BinaryPrimitives.ReadUInt64LittleEndian(header[16..]);
            if (blockLength < 24 || blockLength > (ulong)(streamLength - offset) ||
                links > (blockLength - 24) / 8)
                throw new InvalidDataException("MDF4 block size or link count exceeds its source boundaries.");
            if (links * 8 > (ulong)maxBytes)
                throw new InvalidDataException("MDF4 link table exceeds the safe buffer budget.");
            var linkValues = new long[links];
            if (links > 0)
            {
                var raw = new byte[links * 8];
                stream.ReadExactly(raw);
                for (var index = 0; index < linkValues.Length; index++)
                    linkValues[index] = BinaryPrimitives.ReadInt64LittleEndian(raw.AsSpan(index * 8));
            }

            return new Block
            {
                Id = Encoding.ASCII.GetString(header[..4]),
                Offset = offset,
                Length = (long)blockLength,
                LinkCount = (long)links,
                _links = linkValues
            };
        }

        public static Block Load(Stream stream, long offset, long maxBytes)
        {
            var block = ReadHeader(stream, offset, maxBytes);
            if (block.DataLength > maxBytes)
                throw new InvalidDataException("MDF4 metadata block exceeds the safe buffer budget.");
            block.Data = new byte[block.DataLength];
            stream.Position = block.DataStart;
            stream.ReadExactly(block.Data);
            return block;
        }
    }

    /// <summary>Byte/bit layout of one channel group's record, resolved to CAN frame fields.</summary>
    private sealed class GroupLayout
    {
        public ulong RecordId { get; private init; }
        public int RecordLength { get; private init; }
        public bool IsVariableLength { get; private init; }
        public bool IsCanFrameGroup { get; private init; }

        private ChannelField? _time;
        private ChannelField? _id;
        private ChannelField? _dataLength;
        private bool _lengthIsDlc;
        private ChannelField? _dataBytes;
        private ChannelField? _fdFormat;
        private ChannelField? _bitRateSwitch;
        private ChannelField? _busChannel;
        private ChannelField? _ide;
        private double _timeFactor = 1;
        private double _timeOffset;

        public static GroupLayout? TryBuild(Stream stream, Block cg, long maxBytes)
        {
            if (cg.Data.Length < 32)
                throw new InvalidDataException("Truncated MDF4 channel-group metadata.");
            var recordId = BinaryPrimitives.ReadUInt64LittleEndian(cg.Data);
            var flags = BinaryPrimitives.ReadUInt16LittleEndian(cg.Data.AsSpan(16));
            var dataBytes = BinaryPrimitives.ReadUInt32LittleEndian(cg.Data.AsSpan(24));
            var invalBytes = BinaryPrimitives.ReadUInt32LittleEndian(cg.Data.AsSpan(28));
            var isVlsd = (flags & 0x1) != 0;
            var recordLength = (long)dataBytes + invalBytes;

            var layout = new GroupLayout
            {
                RecordId = recordId,
                RecordLength = (int)Math.Min(recordLength, int.MaxValue),
                IsVariableLength = isVlsd,
                IsCanFrameGroup = false
            };

            if (isVlsd)
            {
                return layout;
            }

            var visitedCn = new HashSet<long>();
            for (var cnLink = cg.Link(1); cnLink != 0;)
            {
                if (!visitedCn.Add(cnLink))
                    throw new InvalidDataException("Cyclic MDF4 channel link.");
                var cn = Block.Load(stream, cnLink, maxBytes);
                layout.CollectChannel(stream, cn, maxBytes, parentByteOffset: 0,
                    visited: new HashSet<long>());
                cnLink = cn.Link(0);
            }

            var isCan = layout is { _time: not null, _id: not null, _dataBytes: not null };
            return isCan
                ? new GroupLayout
                {
                    RecordId = layout.RecordId,
                    RecordLength = layout.RecordLength,
                    IsVariableLength = false,
                    IsCanFrameGroup = true,
                    _time = layout._time,
                    _id = layout._id,
                    _dataLength = layout._dataLength,
                    _lengthIsDlc = layout._lengthIsDlc,
                    _dataBytes = layout._dataBytes,
                    _fdFormat = layout._fdFormat,
                    _bitRateSwitch = layout._bitRateSwitch,
                    _busChannel = layout._busChannel,
                    _ide = layout._ide,
                    _timeFactor = layout._timeFactor,
                    _timeOffset = layout._timeOffset
                }
                : layout;
        }

        private void CollectChannel(Stream stream, Block cn, long maxBytes, int parentByteOffset,
            HashSet<long> visited)
        {
            if (!visited.Add(cn.Offset) || visited.Count > 1024)
                throw new InvalidDataException("Cyclic or excessive MDF4 channel composition.");
            if (cn.Data.Length < 12)
                throw new InvalidDataException("Truncated MDF4 channel metadata.");
            var data = cn.Data;
            var channelType = data[0];
            var syncType = data[1];
            var dataType = data[2];
            var bitOffset = data[3];
            var byteOffset = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4)) + parentByteOffset;
            var bitCount = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(8));
            var name = ReadText(stream, cn.Link(2), maxBytes);

            var field = new ChannelField((int)byteOffset, bitOffset, (int)bitCount, dataType);

            // Master time channel (type 2/3, sync 1) with optional linear conversion.
            if (channelType is 2 or 3 && syncType == 1)
            {
                _time = field;
                var ccLink = cn.Link(4);
                if (ccLink != 0)
                {
                    var cc = Block.Load(stream, ccLink, maxBytes);
                    // CCBLOCK data: cc_type, cc_precision, cc_flags, cc_ref_count, cc_val_count (8 bytes),
                    // cc_phy_range_min, cc_phy_range_max (2 x REAL), then cc_val[]. For a linear
                    // conversion cc_val[0] is the offset and cc_val[1] the factor: phys = val0 + val1 * raw.
                    if (cc.Data.Length >= 40 && cc.Data[0] == 1 &&
                        BinaryPrimitives.ReadUInt16LittleEndian(cc.Data.AsSpan(6)) >= 2)
                    {
                        var offset = BitConverter.ToDouble(cc.Data, 24);
                        var factor = BitConverter.ToDouble(cc.Data, 32);
                        if (double.IsFinite(offset) && double.IsFinite(factor) && factor != 0)
                        {
                            _timeOffset = offset;
                            _timeFactor = factor;
                        }
                    }
                }
            }
            else
            {
                var leaf = name[(name.LastIndexOf('.') + 1)..];
                switch (leaf)
                {
                    case "ID":
                        _id ??= field;
                        break;
                    case "IDE":
                        _ide ??= field;
                        break;
                    case "DLC":
                        if (_dataLength is null)
                        {
                            _dataLength = field;
                            _lengthIsDlc = true;
                        }
                        break;
                    case "DataLength":
                        // DataLength describes a byte count, not a CAN-FD DLC code.
                        _dataLength = field;
                        _lengthIsDlc = false;
                        break;
                    case "FDF" or "EDL" or "IsCANFD":
                        _fdFormat ??= field;
                        break;
                    case "BRS" or "BitRateSwitch":
                        _bitRateSwitch ??= field;
                        break;
                    case "DataBytes":
                        _dataBytes ??= field;
                        break;
                    case "BusChannel":
                        _busChannel ??= field;
                        break;
                }
            }

            // Composition: structure members (e.g. the CAN_DataFrame struct) hang off link[1].
            var composition = cn.Link(1);
            if (composition != 0)
            {
                var child = Block.ReadHeader(stream, composition, maxBytes);
                if (child.Id == "##CN")
                {
                    for (var link = composition; link != 0;)
                    {
                        var member = Block.Load(stream, link, maxBytes);
                        CollectChannel(stream, member, maxBytes, parentByteOffset, visited);
                        link = member.Link(0);
                    }
                }
            }
        }

        public CanFrame? ParseFrame(ReadOnlySpan<byte> record)
        {
            if (_time is null || _id is null || _dataBytes is null)
            {
                return null;
            }

            var seconds = _time.Value.ReadDouble(record) * _timeFactor + _timeOffset;
            var id = (uint)_id.Value.ReadUnsigned(record);
            var lengthRaw = _dataLength is { } dl ? dl.ReadUnsigned(record) :
                (ulong)(_dataBytes.Value.BitCount / 8);
            if (lengthRaw > (_lengthIsDlc ? 15UL : 64UL))
                throw new InvalidDataException("MDF4 CAN length or DLC exceeds its valid domain.");
            var length = _lengthIsDlc ? CanFdLength.FromDlc((int)lengthRaw) : (int)lengthRaw;
            var bytesField = _dataBytes.Value;
            if (bytesField.ByteOffset < 0 || bytesField.ByteOffset > record.Length ||
                bytesField.BitCount < 0)
                throw new InvalidDataException("MDF4 CAN payload field points outside the record.");
            var available = Math.Min(bytesField.BitCount / 8, record.Length - bytesField.ByteOffset);
            if (length > available)
                return null; // Preserve import-health warning; never invent a shortened frame.
            var payload = record.Slice(bytesField.ByteOffset, length);

            var flags = CanFrameFlags.None;
            if (length > 8 || (_fdFormat is { } fd && fd.ReadUnsigned(record) != 0))
                flags |= CanFrameFlags.FlexibleDataRate;
            if (_bitRateSwitch is { } brs && brs.ReadUnsigned(record) != 0)
                flags |= CanFrameFlags.BitRateSwitch;
            var extended = _ide is { } ide ? ide.ReadUnsigned(record) != 0 : (id & 0x1FFF_F800) != 0 && id > 0x7FF;
            if (extended)
            {
                flags |= CanFrameFlags.Extended;
            }

            var channel = _busChannel is { } bus ? Math.Max(0, (int)bus.ReadUnsigned(record) - 1) : 0;
            return new CanFrame(
                channel,
                id & 0x1FFF_FFFFu,
                payload,
                checked((long)(seconds * 1_000_000_000d)),
                flags);
        }

        private static string ReadText(Stream stream, long link, long maxBytes)
        {
            if (link == 0)
            {
                return string.Empty;
            }

            var header = Block.ReadHeader(stream, link, maxBytes);
            if (header.Id != "##TX")
            {
                return string.Empty;
            }

            var block = Block.Load(stream, link, maxBytes);
            var text = Encoding.UTF8.GetString(block.Data);
            var terminator = text.IndexOf('\0');
            return terminator >= 0 ? text[..terminator] : text;
        }
    }

    private readonly record struct ChannelField(int ByteOffset, int BitOffset, int BitCount, byte DataType)
    {
        public ulong ReadUnsigned(ReadOnlySpan<byte> record)
        {
            var byteCount = (BitOffset + BitCount + 7) / 8;
            ulong value = 0;
            if (DataType is 1 or 3 or 5) // big endian
            {
                for (var index = 0; index < byteCount && ByteOffset + index < record.Length; index++)
                {
                    value = (value << 8) | record[ByteOffset + index];
                }
            }
            else
            {
                for (var index = byteCount - 1; index >= 0; index--)
                {
                    if (ByteOffset + index < record.Length)
                    {
                        value = (value << 8) | record[ByteOffset + index];
                    }
                }
            }

            value >>= BitOffset;
            if (BitCount < 64)
            {
                value &= (1UL << BitCount) - 1;
            }

            return value;
        }

        public double ReadDouble(ReadOnlySpan<byte> record)
        {
            // Float channels (data types 4/5) are read directly; integers via ReadUnsigned.
            if (DataType == 4 && BitCount == 64 && ByteOffset + 8 <= record.Length)
            {
                return BinaryPrimitives.ReadDoubleLittleEndian(record[ByteOffset..]);
            }

            if (DataType == 4 && BitCount == 32 && ByteOffset + 4 <= record.Length)
            {
                return BinaryPrimitives.ReadSingleLittleEndian(record[ByteOffset..]);
            }

            if (DataType == 5 && BitCount == 64 && ByteOffset + 8 <= record.Length)
            {
                return BinaryPrimitives.ReadDoubleBigEndian(record[ByteOffset..]);
            }

            if (DataType == 5 && BitCount == 32 && ByteOffset + 4 <= record.Length)
            {
                return BinaryPrimitives.ReadSingleBigEndian(record[ByteOffset..]);
            }

            return ReadUnsigned(record);
        }
    }
}
