using System.Buffers.Binary;
using System.Text;
using RbgCanInsight.Domain;

namespace RbgCanInsight.TraceIO;

/// <summary>
/// Outcome of an MDF4 export. Frames the importer-compatible CAN_DataFrame layout cannot
/// represent faithfully are skipped (never altered) and counted here.
/// </summary>
/// <param name="FramesWritten">Frames stored in the .mf4 data block.</param>
/// <param name="RemoteFramesSkipped">RTR frames: the CAN_DataFrame group has no remote marker.</param>
/// <param name="ErrorFramesSkipped">Error frames: they belong in a CAN_ErrorFrame group the importer does not read.</param>
/// <param name="InvalidLengthFramesSkipped">Payload longer than 8 bytes without the FD flag, or not a valid CAN FD length.</param>
/// <param name="ChannelOutOfRangeFramesSkipped">Zero-based channel above 254 (BusChannel is an 8-bit 1-based field).</param>
public sealed record Mdf4WriteResult(
    long FramesWritten,
    long RemoteFramesSkipped,
    long ErrorFramesSkipped,
    long InvalidLengthFramesSkipped,
    long ChannelOutOfRangeFramesSkipped)
{
    public long FramesSkipped => RemoteFramesSkipped + ErrorFramesSkipped +
        InvalidLengthFramesSkipped + ChannelOutOfRangeFramesSkipped;
}

/// <summary>Layout options of <see cref="Mdf4Writer"/>.</summary>
/// <param name="MaxRecordsPerDataBlock">Records per ##DT block. When the trace needs more than one block,
/// the blocks are chained from a ##DL data list. Default 262,144 (about 20 MB of records).</param>
/// <param name="Compress">Store every data block as a zlib ##DZ block (listed from a ##HL header list).</param>
/// <param name="TransposeCompression">With <paramref name="Compress"/>, use the byte-transposed
/// deflate variant (ZipType 1) that real loggers use for better ratios.</param>
public sealed record Mdf4WriteOptions(
    int MaxRecordsPerDataBlock = 262_144,
    bool Compress = false,
    bool TransposeCompression = false);

/// <summary>
/// Minimal ASAM MDF 4.1 (.mf4) writer producing one ##DT (or ##DL-chained ##DT / ##HL-##DL-##DZ) data group with a single
/// CAN_DataFrame bus-logging channel group, exactly the structure <see cref="Mdf4Importer"/> reads.
/// <para>Record layout (little endian, no record id): Timestamp f64 seconds, then the
/// CAN_DataFrame structure: BusChannel u8 (channel+1), ID u32, IDE, DLC (4 bit), DataLength u8
/// (bytes), Dir, EDL, BRS, ESI, DataBytes (8 or 64 bytes depending on whether any FD frame is present).</para>
/// <para>Known limitations: timestamps are stored as f64 seconds (exact round-trip to the nanosecond
/// while |t| is below ~2^53 ns, i.e. about 104 days; beyond that precision degrades). The header start
/// time is 0, so timestamps are relative. <c>Dir</c> is always 0 (Rx) and <c>ESI</c> always 0 because
/// <see cref="CanFrame"/> carries neither. <c>Sequence</c> is not stored (the importer reassigns it
/// in record order).</para>
/// </summary>
public static class Mdf4Writer
{
    private const int HeaderRecordBytes = 20; // everything before DataBytes
    private const int TimeBytes = 8;

    public static async Task<Mdf4WriteResult> WriteAsync(
        string path, IEnumerable<CanFrame> frames, CancellationToken cancellationToken = default,
        Mdf4WriteOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(frames);
        options ??= new Mdf4WriteOptions();
        if (options.MaxRecordsPerDataBlock < 1)
            throw new ArgumentOutOfRangeException(nameof(options), "MaxRecordsPerDataBlock must be positive.");
        var accepted = new List<CanFrame>();
        long remote = 0, error = 0, invalid = 0, channel = 0;
        var anyFd = false;
        foreach (var frame in frames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (frame.Flags.HasFlag(CanFrameFlags.Remote)) { remote++; continue; }
            if (frame.Flags.HasFlag(CanFrameFlags.Error)) { error++; continue; }
            var fd = frame.Flags.HasFlag(CanFrameFlags.FlexibleDataRate);
            if ((frame.DataLength > 8 && !fd) || !CanFdLength.IsValidFdLength(frame.DataLength))
            {
                invalid++;
                continue;
            }
            if (frame.Channel > 254) { channel++; continue; }
            anyFd |= fd;
            accepted.Add(frame);
        }

        var payloadBytes = anyFd ? 64 : 8;
        var recordLength = HeaderRecordBytes + payloadBytes;
        var blockCount = accepted.Count == 0 ? 0
            : (int)((accepted.Count + (long)options.MaxRecordsPerDataBlock - 1) / options.MaxRecordsPerDataBlock);
        var chained = options.Compress || blockCount > 1;
        List<byte[]>? zipped = null;
        long[]? blockLengths = null;
        if (chained)
        {
            blockLengths = new long[blockCount];
            if (options.Compress) zipped = new List<byte[]>(blockCount);
            for (var b = 0; b < blockCount; b++)
            {
                var first = b * options.MaxRecordsPerDataBlock;
                var count = Math.Min(options.MaxRecordsPerDataBlock, accepted.Count - first);
                if (options.Compress)
                {
                    var raw = new byte[(long)recordLength * count];
                    for (var i = 0; i < count; i++)
                        WriteRecord(raw.AsSpan(i * recordLength, recordLength), accepted[first + i], payloadBytes);
                    var dz = DzBlock(raw, recordLength, options.TransposeCompression);
                    zipped!.Add(dz);
                    blockLengths[b] = dz.Length;
                }
                else
                    blockLengths[b] = 24L + (long)recordLength * count;
            }
        }

        var layout = BuildMetadata(accepted.Count, recordLength, payloadBytes, out var dtOffset,
            blockLengths, options.Compress);

        await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None,
            bufferSize: 1 << 20, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await stream.WriteAsync(layout, cancellationToken);
        if (accepted.Count > 0)
        {
            if (!chained)
            {
                var dtHeader = new byte[24];
                Encoding.ASCII.GetBytes("##DT").CopyTo(dtHeader, 0);
                BinaryPrimitives.WriteUInt64LittleEndian(dtHeader.AsSpan(8), (ulong)(24L + (long)recordLength * accepted.Count));
                await stream.WriteAsync(dtHeader, cancellationToken);
            }

            // Chained layout: DL (+ HL) are part of the metadata; here come the data blocks, each
            // streamed through a bounded record buffer so memory does not grow with the trace.
            var perBlock = options.MaxRecordsPerDataBlock;
            var buffer = new byte[(int)Math.Min(perBlock, 4096) * recordLength];
            for (var first = 0; first < accepted.Count; first += perBlock)
            {
                var count = Math.Min(perBlock, accepted.Count - first);
                if (!chained)
                {
                    await WriteRecordsAsync(stream, accepted, first, count, recordLength, payloadBytes, buffer, cancellationToken);
                }
                else if (!options.Compress)
                {
                    var header = new byte[24];
                    Encoding.ASCII.GetBytes("##DT").CopyTo(header, 0);
                    BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(8), (ulong)(24L + (long)recordLength * count));
                    await stream.WriteAsync(header, cancellationToken);
                    await WriteRecordsAsync(stream, accepted, first, count, recordLength, payloadBytes, buffer, cancellationToken);
                }
                else
                {
                    await stream.WriteAsync(zipped![first / perBlock], cancellationToken);
                }
            }
        }
        _ = dtOffset;
        await stream.FlushAsync(cancellationToken);
        return new Mdf4WriteResult(accepted.Count, remote, error, invalid, channel);
    }

    private static async Task WriteRecordsAsync(Stream stream, List<CanFrame> frames, int first, int count,
        int recordLength, int payloadBytes, byte[] buffer, CancellationToken cancellationToken)
    {
        var perChunk = buffer.Length / recordLength;
        for (var done = 0; done < count; done += perChunk)
        {
            var n = Math.Min(perChunk, count - done);
            for (var i = 0; i < n; i++)
                WriteRecord(buffer.AsSpan(i * recordLength, recordLength), frames[first + done + i], payloadBytes);
            await stream.WriteAsync(buffer.AsMemory(0, n * recordLength), cancellationToken);
        }
    }

    /// <summary>Encodes <paramref name="raw"/> as a complete ##DZ block (header included).</summary>
    internal static byte[] DzBlock(byte[] raw, int recordLength, bool transpose)
    {
        var source = raw;
        if (transpose)
        {
            // Column-major over one record: byte c of record r is stored at c * rows + r.
            var rows = raw.Length / recordLength;
            source = new byte[raw.Length];
            for (var r = 0; r < rows; r++)
                for (var c = 0; c < recordLength; c++)
                    source[c * rows + r] = raw[r * recordLength + c];
            for (var i = rows * recordLength; i < raw.Length; i++) source[i] = raw[i];
        }

        using var compressed = new MemoryStream();
        using (var zlib = new System.IO.Compression.ZLibStream(compressed, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
            zlib.Write(source, 0, source.Length);
        var data = compressed.ToArray();
        var block = new byte[24 + 24 + data.Length + (8 - data.Length % 8) % 8];
        Encoding.ASCII.GetBytes("##DZ").CopyTo(block, 0);
        BinaryPrimitives.WriteUInt64LittleEndian(block.AsSpan(8), (ulong)block.Length);
        Encoding.ASCII.GetBytes("DT").CopyTo(block, 24);
        block[26] = transpose ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(28), transpose ? (uint)recordLength : 0);
        BinaryPrimitives.WriteUInt64LittleEndian(block.AsSpan(32), (ulong)raw.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(block.AsSpan(40), (ulong)data.Length);
        data.CopyTo(block, 48);
        return block;
    }

    private static void WriteRecord(Span<byte> record, in CanFrame frame, int payloadBytes)
    {
        record.Clear();
        BinaryPrimitives.WriteDoubleLittleEndian(record, ExactSeconds(frame.TimestampNanoseconds));
        record[8] = (byte)(frame.Channel + 1);
        BinaryPrimitives.WriteUInt32LittleEndian(record[9..], frame.Identifier);
        record[13] = frame.IsExtended ? (byte)1 : (byte)0;
        record[14] = (byte)CanFdLength.ToDlc(frame.DataLength);
        record[15] = frame.DataLength;
        record[16] = 0; // Dir: unknown in CanFrame; Rx by convention.
        var fd = frame.Flags.HasFlag(CanFrameFlags.FlexibleDataRate);
        record[17] = fd ? (byte)1 : (byte)0;
        record[18] = frame.Flags.HasFlag(CanFrameFlags.BitRateSwitch) ? (byte)1 : (byte)0;
        record[19] = 0; // ESI: not representable in CanFrame.
        frame.CopyPayloadTo(record.Slice(HeaderRecordBytes, payloadBytes));
    }

    /// <summary>
    /// Chooses the double whose product with 1e9, truncated by the importer, equals
    /// <paramref name="nanoseconds"/> (nudging by ULPs); falls back to the nearest value if impossible.
    /// </summary>
    internal static double ExactSeconds(long nanoseconds)
    {
        var seconds = nanoseconds / 1e9;
        for (var step = 0; step < 8; step++)
        {
            var back = (long)(seconds * 1_000_000_000d);
            if (back == nanoseconds) return seconds;
            seconds = back < nanoseconds ? Math.BitIncrement(seconds) : Math.BitDecrement(seconds);
        }
        return nanoseconds / 1e9;
    }

    // ---------------------------------------------------------------- block graph

    private sealed class Blk
    {
        public Blk(string id, byte[] data, int linkCount)
        {
            Id = id; Data = data; Links = new Blk?[linkCount];
        }
        public string Id { get; }
        public byte[] Data { get; }
        public Blk?[] Links { get; }
        public long Offset { get; set; }
        public long Length => 24 + 8L * Links.Length + Data.Length;
    }

    private static Blk Text(string id, string text)
    {
        var raw = Encoding.UTF8.GetBytes(text);
        var data = new byte[(raw.Length + 1 + 7) / 8 * 8]; // zero terminator + pad to 8
        raw.CopyTo(data, 0);
        return new Blk(id, data, 0);
    }

    private static Blk Channel(List<Blk> all, string name, byte type, byte sync, byte dataType,
        uint byteOffset, byte bitOffset, uint bits, Blk? unit = null)
    {
        var data = new byte[72];
        data[0] = type; data[1] = sync; data[2] = dataType; data[3] = bitOffset;
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), byteOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8), bits);
        var cn = new Blk("##CN", data, 8);
        var tx = Text("##TX", name);
        cn.Links[2] = tx;
        cn.Links[6] = unit;
        all.Add(cn);
        all.Add(tx);
        return cn;
    }

    private static byte[] BuildMetadata(long cycleCount, int recordLength, int payloadBytes, out long dtOffset,
        long[]? blockLengths, bool compress)
    {
        var all = new List<Blk>();
        var hd = new Blk("##HD", new byte[32], 6);
        all.Add(hd);
        var hdComment = Text("##MD",
            "<HDcomment><TX>RBG CAN Insight export; timestamps are relative to the first sample time base.</TX></HDcomment>");
        hd.Links[5] = hdComment;
        all.Add(hdComment);

        var fhData = new byte[16];
        BinaryPrimitives.WriteUInt64LittleEndian(fhData,
            (ulong)((DateTime.UtcNow - DateTime.UnixEpoch).Ticks * 100));
        var fh = new Blk("##FH", fhData, 2);
        var fhComment = Text("##MD",
            "<FHcomment><TX>Created by RBG CAN Insight</TX><tool_id>RbgCanInsight</tool_id>" +
            "<tool_vendor>RBG</tool_vendor><tool_version>1.0</tool_version></FHcomment>");
        fh.Links[1] = fhComment;
        hd.Links[1] = fh;
        all.Add(fh);
        all.Add(fhComment);

        var dg = new Blk("##DG", new byte[8], 4); // record id size 0
        hd.Links[0] = dg;
        all.Add(dg);

        var cgData = new byte[32];
        BinaryPrimitives.WriteUInt64LittleEndian(cgData.AsSpan(8), (ulong)cycleCount);
        BinaryPrimitives.WriteUInt16LittleEndian(cgData.AsSpan(16), 0x6); // bus event + plain bus event
        BinaryPrimitives.WriteUInt16LittleEndian(cgData.AsSpan(18), '.');
        BinaryPrimitives.WriteUInt32LittleEndian(cgData.AsSpan(24), (uint)recordLength);
        var cg = new Blk("##CG", cgData, 6);
        dg.Links[1] = cg;
        all.Add(cg);
        var cgName = Text("##TX", "CAN_DataFrame");
        cg.Links[2] = cgName;
        all.Add(cgName);

        var siData = new byte[8];
        siData[0] = 2; // source type: bus
        siData[1] = 2; // bus type: CAN
        var si = new Blk("##SI", siData, 3);
        si.Links[0] = Text("##TX", "CAN");
        cg.Links[3] = si;
        all.Add(si);
        all.Add(si.Links[0]!);

        var seconds = Text("##TX", "s");
        all.Add(seconds);
        var time = Channel(all, "Timestamp", type: 2, sync: 1, dataType: 4, 0, 0, 64, seconds);
        cg.Links[1] = time;

        var frame = Channel(all, "CAN_DataFrame", 0, 0, 10, TimeBytes, 0,
            (uint)((recordLength - TimeBytes) * 8));
        time.Links[0] = frame;

        var members = new[]
        {
            Channel(all, "CAN_DataFrame.BusChannel", 0, 0, 0, 8, 0, 8),
            Channel(all, "CAN_DataFrame.ID", 0, 0, 0, 9, 0, 32),
            Channel(all, "CAN_DataFrame.IDE", 0, 0, 0, 13, 0, 1),
            Channel(all, "CAN_DataFrame.DLC", 0, 0, 0, 14, 0, 4),
            Channel(all, "CAN_DataFrame.DataLength", 0, 0, 0, 15, 0, 8),
            Channel(all, "CAN_DataFrame.Dir", 0, 0, 0, 16, 0, 1),
            Channel(all, "CAN_DataFrame.EDL", 0, 0, 0, 17, 0, 1),
            Channel(all, "CAN_DataFrame.BRS", 0, 0, 0, 18, 0, 1),
            Channel(all, "CAN_DataFrame.ESI", 0, 0, 0, 19, 0, 1),
            Channel(all, "CAN_DataFrame.DataBytes", 0, 0, 10, HeaderRecordBytes, 0, (uint)(payloadBytes * 8)),
        };
        for (var i = 0; i + 1 < members.Length; i++) members[i].Links[0] = members[i + 1];
        frame.Links[1] = members[0];

        var ordered = new List<Blk>();
        var seen = new HashSet<Blk>();
        foreach (var block in all) if (seen.Add(block)) ordered.Add(block);

        var offset = 64L + 0;
        foreach (var block in ordered) { block.Offset = offset; offset += block.Length; }
        dtOffset = offset;
        List<Blk>? chain = null;
        if (cycleCount > 0 && blockLengths is null)
            dg.Links[2] = new Blk("##DT", [], 0) { Offset = dtOffset };
        else if (cycleCount > 0)
        {
            // [HL ->] DL -> data blocks; the data blocks follow the list blocks in the file.
            chain = [];
            Blk? hl = null;
            if (compress)
            {
                hl = new Blk("##HL", new byte[8], 1) { Offset = offset };
                hl.Data[0] = 0; // flags: not equal-length
                hl.Data[2] = 0; // zip type: deflate
                offset += hl.Length;
            }

            var dl = new Blk("##DL", new byte[8 + 8 * 0 + 8 * blockLengths!.Length], 1 + blockLengths.Length)
            {
                Offset = offset
            };
            dl.Data[0] = 0; // not equal length -> per-block offsets follow the count
            BinaryPrimitives.WriteUInt32LittleEndian(dl.Data.AsSpan(4), (uint)blockLengths.Length);
            offset += dl.Length;
            var dataOffset = offset;
            long recordsBefore = 0;
            for (var i = 0; i < blockLengths.Length; i++)
            {
                dl.Links[1 + i] = new Blk(compress ? "##DZ" : "##DT", [], 0) { Offset = dataOffset };
                dataOffset += blockLengths[i];
                BinaryPrimitives.WriteInt64LittleEndian(dl.Data.AsSpan(8 + 8 * i), recordsBefore);
                recordsBefore += compress ? 0 : (blockLengths[i] - 24) / recordLength;
            }

            if (hl is not null) hl.Links[0] = dl;
            dg.Links[2] = (Blk?)hl ?? dl;
            if (hl is not null) chain.Add(hl);
            chain.Add(dl);
        }

        using var output = new MemoryStream();
        output.Write(IdentificationBlock());
        var linkBuffer = new byte[8];
        foreach (var block in ordered.Concat(chain ?? []))
        {
            var header = new byte[24];
            Encoding.ASCII.GetBytes(block.Id).CopyTo(header, 0);
            BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(8), (ulong)block.Length);
            BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(16), (ulong)block.Links.Length);
            output.Write(header);
            foreach (var link in block.Links)
            {
                BinaryPrimitives.WriteInt64LittleEndian(linkBuffer, link?.Offset ?? 0);
                output.Write(linkBuffer);
            }
            output.Write(block.Data);
        }
        return output.ToArray();
    }

    private static byte[] IdentificationBlock()
    {
        var id = new byte[64];
        Encoding.ASCII.GetBytes("MDF     ").CopyTo(id, 0);
        Encoding.ASCII.GetBytes("4.10    ").CopyTo(id, 8);
        Encoding.ASCII.GetBytes("RBGCAN  ").CopyTo(id, 16);
        BinaryPrimitives.WriteUInt16LittleEndian(id.AsSpan(28), 410);
        return id;
    }
}
