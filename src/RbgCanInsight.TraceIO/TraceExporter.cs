using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using RbgCanInsight.Domain;

namespace RbgCanInsight.TraceIO;

/// <summary>Writes CAN frames back out as Vector ASC, CSV, PCAN TRC or Vector BLF, e.g. for
/// exporting a selected interval or converting between the formats this app can already read.</summary>
public static class TraceExporter
{
    public static async Task WriteAscAsync(
        string path,
        IEnumerable<CanFrame> frames,
        CancellationToken cancellationToken = default)
    {
        await using var writer = new StreamWriter(
            new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 20),
            new UTF8Encoding(false));
        await writer.WriteLineAsync($"date {DateTime.Now:ddd MMM dd HH:mm:ss.fff yyyy}");
        await writer.WriteLineAsync("base hex  timestamps absolute");
        await writer.WriteLineAsync("internal events logged");

        var batch = new StringBuilder(1 << 20);
        foreach (var frame in frames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var id = frame.Identifier.ToString(frame.IsExtended ? "X8" : "X3", CultureInfo.InvariantCulture) +
                     (frame.IsExtended ? "x" : string.Empty);
            var data = string.Join(' ', frame.GetPayload().Select(value =>
                value.ToString("X2", CultureInfo.InvariantCulture)));
            var kind = frame.Flags.HasFlag(CanFrameFlags.Remote) ? "r" : "d";
            var length = frame.DeclaredDataLength;
            batch.AppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"{frame.TimestampNanoseconds / 1_000_000_000d:F6} {frame.Channel + 1} {id} {(frame.IsTransmit ? "Tx" : "Rx")} {kind} {length} {data}"));
            if (batch.Length >= 1 << 20)
            {
                await writer.WriteAsync(batch.ToString().AsMemory(), cancellationToken);
                batch.Clear();
            }
        }

        if (batch.Length > 0)
        {
            await writer.WriteAsync(batch.ToString().AsMemory(), cancellationToken);
        }
    }

    public static async Task WriteCsvAsync(
        string path,
        IEnumerable<CanFrame> frames,
        CancellationToken cancellationToken = default)
    {
        await using var writer = new StreamWriter(
            new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 20),
            new UTF8Encoding(false));
        await writer.WriteLineAsync("time_s,channel,id_hex,dlc,data");
        foreach (var frame in frames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var data = Convert.ToHexString(frame.GetPayload());
            await writer.WriteLineAsync(string.Create(
                CultureInfo.InvariantCulture,
                $"{frame.TimestampNanoseconds / 1_000_000_000m:F9},{frame.Channel},{frame.Identifier:X},{frame.DeclaredDataLength},{data}"));
        }
    }

    /// <summary>Writes a PCAN-View .trc file in the 2.x layout that <see cref="TrcImporter"/>
    /// already reads, so a trace loaded from any supported format can be converted to TRC.</summary>
    public static async Task WriteTrcAsync(
        string path,
        IEnumerable<CanFrame> frames,
        CancellationToken cancellationToken = default)
    {
        await using var writer = new StreamWriter(
            new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 20),
            new UTF8Encoding(false));
        await writer.WriteLineAsync(";$FILEVERSION=2.1");
        await writer.WriteLineAsync($";$STARTTIME={DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}");
        await writer.WriteLineAsync(";$COLUMNS=N,O,T,B,I,d,R,L,D");
        await writer.WriteLineAsync(";");
        await writer.WriteLineAsync(";   Message   Time    Bus   Identifier  Rx/Tx  Data Length   Data");
        await writer.WriteLineAsync(";   Number    Offset  (ms)  (hex)");
        await writer.WriteLineAsync(";---------------------------------------------------------------------------");

        var batch = new StringBuilder(1 << 20);
        var number = 0L;
        foreach (var frame in frames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            number++;
            var id = frame.Identifier.ToString(frame.IsExtended ? "X8" : "X3", CultureInfo.InvariantCulture);
            var data = string.Join(' ', frame.GetPayload().Select(value =>
                value.ToString("X2", CultureInfo.InvariantCulture)));
            var kind = frame.Flags.HasFlag(CanFrameFlags.Remote) ? "RR" : "DT";
            batch.AppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"{number,7} {frame.TimestampNanoseconds / 1_000_000d,13:F4} {kind} {frame.Channel,2} {id,8} {(frame.IsTransmit ? "Tx" : "Rx")} -  {frame.DeclaredDataLength}    {data}"));
            if (batch.Length >= 1 << 20)
            {
                await writer.WriteAsync(batch.ToString().AsMemory(), cancellationToken);
                batch.Clear();
            }
        }

        if (batch.Length > 0)
        {
            await writer.WriteAsync(batch.ToString().AsMemory(), cancellationToken);
        }
    }

    /// <summary>
    /// Writes BLF messages inside actual LOG_CONTAINER records. Vector and python-can readers
    /// expect top-level containers rather than bare CAN message objects.
    /// Uncompressed containers are intentional: avoids vendor dependencies and data loss.
    /// </summary>
    public static async Task WriteBlfAsync(
        string path,
        IEnumerable<CanFrame> frames,
        CancellationToken cancellationToken = default)
    {
        const int containerLimit = 128 * 1024;
        await using var stream = new FileStream(
            path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read, 1 << 20);

        var header = new byte[144];
        BinaryPrimitives.WriteUInt32LittleEndian(header, 0x47474F4C); // LOGG
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), (uint)header.Length);
        // Application=5, BLF writer version. File counts/size are finalized below.
        header[8] = 5;
        header[12] = 2;
        header[13] = 6;
        await stream.WriteAsync(header, cancellationToken);

        var objectBuffer = new byte[16 + 16 + 20 + 64];
        using var container = new MemoryStream(containerLimit);
        ulong uncompressedSize = (ulong)header.Length; // BLF uncompressed size includes LOGG header
        uint frameCount = 0;
        foreach (var frame in frames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var objectSize = EncodeBlfObject(objectBuffer, frame);
            if (container.Length + objectSize > containerLimit && container.Length > 0)
            {
                uncompressedSize += await WriteBlfContainerAsync(stream, container, cancellationToken);
                container.SetLength(0);
                container.Position = 0;
            }

            container.Write(objectBuffer, 0, objectSize);
            frameCount = checked(frameCount + 1);
        }

        if (container.Length > 0)
        {
            uncompressedSize += await WriteBlfContainerAsync(stream, container, cancellationToken);
        }

        // The BLF header records the final file size, uncompressed stream size
        // and object count. Internal CAN frames are all included in those counts.
        var sizes = new byte[20];
        BinaryPrimitives.WriteUInt64LittleEndian(sizes.AsSpan(0), (ulong)stream.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(sizes.AsSpan(8), uncompressedSize);
        BinaryPrimitives.WriteUInt32LittleEndian(sizes.AsSpan(16), frameCount);
        stream.Position = 16;
        await stream.WriteAsync(sizes, cancellationToken);
    }

    private static async Task<ulong> WriteBlfContainerAsync(
        FileStream output, MemoryStream uncompressed, CancellationToken cancellationToken)
    {
        var count = checked((int)uncompressed.Length);
        var objectLength = checked(32 + count);
        var header = new byte[32];
        BinaryPrimitives.WriteUInt32LittleEndian(header, 0x4A424F4C); // LOBJ
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4), 16); // base header, no V1/V2 timestamp
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(6), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), (uint)objectLength);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), 10); // LOG_CONTAINER
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(16), 0); // no compression
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(24), (uint)count);
        await output.WriteAsync(header, cancellationToken);
        await output.WriteAsync(uncompressed.GetBuffer().AsMemory(0, count), cancellationToken);
        // Vector BLF commonly uses objectLength % 4 bytes of padding at top level.
        var padding = objectLength % 4;
        if (padding != 0)
        {
            await output.WriteAsync(new byte[padding], cancellationToken);
        }

        // Uncompressed size excludes top-level alignment padding (python-can / BLF spec).
        return (ulong)objectLength;
    }

    /// <summary>Fills <paramref name="objectBuffer"/> with one LOBJ object (header + V1 sub-header
    /// + classic or FD CAN payload) for <paramref name="frame"/> and returns its total size. A
    /// plain (non-async) method, since ref structs like <see cref="Span{T}"/> used as locals
    /// cannot safely span an await point at this project's C# language version.</summary>
    private static int EncodeBlfObject(byte[] objectBuffer, in CanFrame frame)
    {
        if (frame.Channel >= ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(frame), "BLF channel exceeds UInt16 capacity.");
        if (frame.Flags.HasFlag(CanFrameFlags.Remote) &&
            (frame.DeclaredDataLength > 8 || frame.Flags.HasFlag(CanFrameFlags.FlexibleDataRate)))
            throw new NotSupportedException("CAN-FD remote frames are not valid.");
        var isError = frame.IsError;
        if (isError && frame.DataLength > 8)
            throw new NotSupportedException("BLF CAN_ERROR_EXT supports at most 8 error payload bytes.");
        var isFd = !isError && (frame.DataLength > 8 || frame.Flags.HasFlag(CanFrameFlags.FlexibleDataRate));
        // CAN_ERROR_EXT is 32 bytes, CAN_MESSAGE is 16 bytes, CAN_FD_MESSAGE 84 bytes.
        var typeSpecificSize = isError ? 32 : isFd ? 20 + 64 : 16;
        var bodySize = 16 + typeSpecificSize;
        var objectSize = 16 + bodySize;
        var objectSpan = objectBuffer.AsSpan(0, objectSize);
        objectSpan.Clear();

        // Object header: signature, headerSize/headerVersion (unread by our importer, filled for
        // structural completeness), objectSize, objectType.
        BinaryPrimitives.WriteUInt32LittleEndian(objectSpan, 0x4A424F4C); // "LOBJ"
        BinaryPrimitives.WriteUInt16LittleEndian(objectSpan[4..], 32);
        BinaryPrimitives.WriteUInt16LittleEndian(objectSpan[6..], 1);
        BinaryPrimitives.WriteUInt32LittleEndian(objectSpan[8..], (uint)objectSize);
        BinaryPrimitives.WriteUInt32LittleEndian(objectSpan[12..], isError ? 73u : isFd ? 100u : 1u);

        // V1 object header remainder: flags (2 = nanosecond ticks), client/version, timestamp.
        var body = objectSpan[16..];
        BinaryPrimitives.WriteUInt32LittleEndian(body, 2);
        BinaryPrimitives.WriteUInt64LittleEndian(body[8..], (ulong)Math.Max(0, frame.TimestampNanoseconds));

        var rawId = frame.Identifier | (frame.IsExtended ? 0x8000_0000u : 0);
        var payload = body[16..];
        if (isError)
        {
            // CAN_ERROR_EXT: channel(u16), length(u16), flags(u32), ecc(u8),
            // position(u8), dlc(u8), pad, frameLength(u32), id(u32),
            // flagsExt(u16), pad2, data[8].
            BinaryPrimitives.WriteUInt16LittleEndian(payload, (ushort)(frame.Channel + 1));
            payload[10] = (byte)Math.Min(frame.DataLength, (byte)8);
            BinaryPrimitives.WriteUInt32LittleEndian(payload[16..], rawId);
            frame.CopyPayloadTo(payload.Slice(24, Math.Min((int)frame.DataLength, 8)));
        }
        else if (isFd)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(payload, (ushort)(frame.Channel + 1));
            BinaryPrimitives.WriteUInt32LittleEndian(payload[4..], rawId);
            payload[3] = ToFdDlc(frame.DataLength);
            var fdFlags = (byte)((frame.Flags.HasFlag(CanFrameFlags.BitRateSwitch) ? 0x2 : 0) | 0x1);
            payload[13] = fdFlags;
            if (frame.IsTransmit) payload[2] |= 0x01;
            payload[14] = frame.DataLength;
            frame.CopyPayloadTo(payload.Slice(20, frame.DataLength));
        }
        else
        {
            BinaryPrimitives.WriteUInt16LittleEndian(payload, (ushort)(frame.Channel + 1));
            if (frame.Flags.HasFlag(CanFrameFlags.Remote)) payload[2] = 0x80;
            if (frame.IsTransmit) payload[2] |= 0x01;
            payload[3] = frame.DeclaredDataLength;
            BinaryPrimitives.WriteUInt32LittleEndian(payload[4..], rawId);
            frame.CopyPayloadTo(payload.Slice(8, Math.Min((int)frame.DataLength, 8)));
        }

        return objectSize;
    }

    private static byte ToFdDlc(int count) => count switch
    {
        <= 8 => (byte)count,
        <= 12 => 9,
        <= 16 => 10,
        <= 20 => 11,
        <= 24 => 12,
        <= 32 => 13,
        <= 48 => 14,
        _ => 15
    };
}
