using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using RbgCanInsight.Domain;

namespace RbgCanInsight.TraceIO;

/// <summary>
/// Vector binary logging format (.blf) reader. Understands the LOBJ object stream, zlib
/// compressed LOG_CONTAINER blocks and the classic and FD CAN message object types.
/// Unknown object types are skipped, so files mixing CAN with LIN/FlexRay/Ethernet still load.
/// </summary>
public static class BlfImporter
{
    private const uint ObjectSignature = 0x4A424F4C;  // "LOBJ"
    private const int TypeLogContainer = 10;
    private const int TypeCanMessage = 1;
    private const int TypeCanError = 2;
    private const int TypeCanErrorExtended = 73;
    private const int MaxContainerBytes = 64 * 1024 * 1024;
    private const int MaxObjectBytes = 64 * 1024 * 1024;
    private const int TypeCanMessage2 = 86;
    private const int TypeCanFdMessage = 100;
    private const int TypeCanFdMessage64 = 101;

    public static async IAsyncEnumerable<CanFrame> ReadAsync(
        string path,
        [EnumeratorCancellation] CancellationToken cancellationToken = default,
        TraceImportHealth? diagnostics = null)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
            bufferSize: 1 << 20, FileOptions.SequentialScan);

        var fileHeader = new byte[144];
        if (stream.Length < 8)
            throw new InvalidDataException(stream.Length == 0
                ? "The BLF file is empty."
                : "The file is too short to be a BLF log.");
        await stream.ReadExactlyAsync(fileHeader.AsMemory(0, 8), cancellationToken);
        if (BinaryPrimitives.ReadUInt32LittleEndian(fileHeader) != 0x47474F4C) // "LOGG"
        {
            throw new InvalidDataException("Not a BLF file (missing LOGG signature).");
        }

        var headerSize = BinaryPrimitives.ReadUInt32LittleEndian(fileHeader.AsSpan(4));
        if (headerSize < 72 || headerSize > stream.Length)
            throw new InvalidDataException($"Invalid BLF file header size: {headerSize}.");
        // Read only the standard fixed header fields needed for import quality;
        // the remaining extension header bytes are ignored, not guessed.
        var previewLength = (int)Math.Min(headerSize, (uint)fileHeader.Length);
        await stream.ReadExactlyAsync(fileHeader.AsMemory(8, previewLength - 8), cancellationToken);
        var declaredFileSize = BinaryPrimitives.ReadUInt64LittleEndian(fileHeader.AsSpan(16));
        if (declaredFileSize > 0 && declaredFileSize != (ulong)stream.Length)
            diagnostics?.RecordFileMetadataWarning("BLF_DECLARED_FILE_SIZE",
                $"BLF LOGG header declares {declaredFileSize:N0} bytes; actual source length is {stream.Length:N0}. " +
                "The capture may be incomplete, still growing, or carry stale metadata.");
        stream.Position = headerSize;

        // Objects inside LOG_CONTAINERs can span container boundaries, so decompressed bytes are
        // accumulated here and parsed as a continuous stream.
        var buffer = new ContainerBuffer();
        long sequence = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var header = new byte[16];
            var read = await stream.ReadAtLeastAsync(header, 16, throwOnEndOfStream: false, cancellationToken);
            if (read < 16)
            {
                if (read >= 4 &&
                    BinaryPrimitives.ReadUInt32LittleEndian(header) == ObjectSignature)
                    throw new InvalidDataException("Truncated BLF object header.");
                if (read > 0)
                {
                    var nonzero = header.AsSpan(0, read).IndexOfAnyExcept((byte)0) >= 0;
                    diagnostics?.RecordTopLevelBytes(read, nonzero, stream.Position - read);
                }
                break; // optional zero padding at EOF is permitted
            }

            if (BinaryPrimitives.ReadUInt32LittleEndian(header) != ObjectSignature)
            {
                // Re-sync: record the lost byte, not a fabricated CAN frame.
                diagnostics?.RecordTopLevelBytes(1, header[0] != 0, stream.Position - 16);
                stream.Position -= 15;
                continue;
            }

            var objectHeaderSize = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(4));
            var objectSize = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8));
            var objectType = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12));
            if (objectSize < 16 || objectSize > MaxObjectBytes ||
                objectHeaderSize < 16 || objectHeaderSize > objectSize)
                throw new InvalidDataException("Invalid BLF object header or object size.");
            var bodySize = checked((int)objectSize - 16);

            var body = new byte[bodySize];
            read = await stream.ReadAtLeastAsync(body, bodySize, throwOnEndOfStream: false, cancellationToken);
            if (read < bodySize)
            {
                throw new InvalidDataException(
                    $"Truncated BLF object: expected {bodySize} body bytes, read {read}.");
            }

            // BLF writers vary in padding conventions. Scan for the next LOBJ signature
            // instead of assuming a padding length and potentially skipping a valid header.

            diagnostics?.RecordBlfObject(objectType == TypeLogContainer);
            if (objectType == TypeLogContainer)
            {
                DecompressContainer(body, buffer);
                foreach (var frame in DrainContainedObjects(buffer, diagnostics))
                    yield return frame with { Sequence = ++sequence };
            }
            else if (IsSupportedCanObject(objectType))
            {
                if (ParseCanObject(objectType, body, objectHeaderSize) is { } frame)
                    yield return frame with { Sequence = ++sequence };
                else
                    diagnostics?.RecordMalformedObject(objectType, "CAN object body could not be decoded.");
            }
            else if (BlfBusObjects.IsBusObject(objectType))
                BlfBusObjects.Handle(objectType, body, objectHeaderSize, diagnostics);
            else
                diagnostics?.RecordUnsupportedObject(objectType);
        }

        // A complete file may have alignment padding, but a pending partial
        // container object with nonzero bytes means lost/undecodable data.
        if (buffer.NonPaddingTailBytes > 0)
            diagnostics?.RecordIncompleteBlfTail(buffer.NonPaddingTailBytes);
    }

    private static bool IsSupportedCanObject(uint type) =>
        type is TypeCanMessage or TypeCanMessage2 or TypeCanError or
            TypeCanErrorExtended or TypeCanFdMessage or TypeCanFdMessage64;

    private static void DecompressContainer(byte[] body, ContainerBuffer buffer)
    {
        // Container body: compressionMethod(u16), reserved(6), uncompressedSize(u32), reserved(4),
        // then payload.
        if (body.Length < 16)
        {
            throw new InvalidDataException("Truncated BLF LOG_CONTAINER header.");
        }

        var method = BinaryPrimitives.ReadUInt16LittleEndian(body);
        var declaredLength = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(8));
        if (declaredLength > MaxContainerBytes)
            throw new InvalidDataException("BLF container exceeds the decompression limit.");
        if (method == 0)
        {
            var bytes = body.AsSpan(16);
            if (declaredLength != 0 && bytes.Length != declaredLength)
                throw new InvalidDataException("BLF container length mismatch.");
            buffer.Append(bytes);
            return;
        }
        if (method != 2)
            throw new NotSupportedException($"Unknown BLF compression method {method}.");

        using var source = new MemoryStream(body, 16, body.Length - 16, writable: false);
        using var zlib = new ZLibStream(source, CompressionMode.Decompress);
        using var target = new MemoryStream();
        var chunk = new byte[8192];
        int count;
        while ((count = zlib.Read(chunk, 0, chunk.Length)) > 0)
        {
            if (target.Length + count > MaxContainerBytes)
                throw new InvalidDataException("BLF container exceeds the decompression limit.");
            target.Write(chunk, 0, count);
        }
        if (declaredLength != 0 && target.Length != declaredLength)
            throw new InvalidDataException("BLF decompressed container length mismatch.");
        buffer.Append(target.GetBuffer().AsSpan(0, (int)target.Length));
    }

    private static IReadOnlyList<CanFrame> DrainContainedObjects(
        ContainerBuffer buffer, TraceImportHealth? diagnostics)
    {
        // A synchronous parser keeps ReadOnlySpan locals out of C# 12 iterators.
        var frames = new List<CanFrame>();
        while (true)
        {
            var data = buffer.Span;
            // Skip alignment padding between contained objects.
            var offset = 0;
            while (offset + 4 <= data.Length &&
                   BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]) != ObjectSignature)
            {
                offset++;
            }

            if (offset > 0)
                diagnostics?.RecordContainerBytes(offset,
                    data[..offset].IndexOfAnyExcept((byte)0) >= 0);

            if (offset + 16 > data.Length)
            {
                buffer.Consume(offset);
                return frames;
            }

            var headerSize = BinaryPrimitives.ReadUInt16LittleEndian(data[(offset + 4)..]);
            var objectSize = BinaryPrimitives.ReadUInt32LittleEndian(data[(offset + 8)..]);
            var objectType = BinaryPrimitives.ReadUInt32LittleEndian(data[(offset + 12)..]);
            if (objectSize < 16 || objectSize > MaxObjectBytes || headerSize < 16 || headerSize > objectSize)
            {
                // Malformed signature: advance to avoid blocking every subsequent container.
                diagnostics?.RecordMalformedObject(objectType, "Invalid LOBJ size/header; resynchronized.");
                buffer.Consume(offset + 1);
                continue;
            }
            if (offset + objectSize > data.Length)
            {
                buffer.Consume(offset);
                return frames;
            }

            CanFrame? frame = null;
            if (IsSupportedCanObject(objectType))
            {
                frame = ParseCanObject(objectType,
                    data.Slice(offset + 16, (int)objectSize - 16), headerSize);
                if (frame is null)
                    diagnostics?.RecordMalformedObject(objectType, "Contained CAN object body could not be decoded.");
            }
            else if (BlfBusObjects.IsBusObject(objectType))
                BlfBusObjects.Handle(objectType, data.Slice(offset + 16, (int)objectSize - 16), headerSize, diagnostics);
            else
                diagnostics?.RecordUnsupportedObject(objectType);

            buffer.Consume(offset + (int)objectSize);
            if (frame is { } value)
                frames.Add(value);
        }
    }

    private static CanFrame? ParseCanObject(uint objectType, ReadOnlySpan<byte> body, int objectHeaderSize)
    {
        // Body starts with the V1/V2 object header remainder:
        // V1: flags(u32) client(u16) version(u16) timestamp(u64)                    = 16 bytes
        // V2: flags(u32) status(u8) rsvd(u8) version(u16) timestamp(u64) orig(u64)  = 24 bytes
        if (body.Length < 16)
        {
            return null;
        }

        var flags = BinaryPrimitives.ReadUInt32LittleEndian(body);
        var timestampRaw = BinaryPrimitives.ReadUInt64LittleEndian(body[8..]);
        // headerSize (from LOBJ) describes the header layout. The body object's
        // objectVersion field at offset 6 does NOT identify a V1/V2 object header.
        // Accept 16 for compatibility with older local BLF exports.
        var headerRest = objectHeaderSize switch
        {
            40 => 24,
            32 or 16 => 16,
            _ => -1
        };
        if (headerRest < 0 || body.Length < headerRest)
        {
            return null;
        }

        // TIME_TEN_MICS is exactly 1; legacy/unspecified flags use nanoseconds,
        // as in python-can's Vector BLF reader. Never silently wrap a UInt64 timestamp.
        var unit = flags == 1 ? 10_000UL : 1UL;
        if (timestampRaw > (ulong)long.MaxValue / unit)
            throw new InvalidDataException("BLF timestamp exceeds supported nanosecond range.");
        var timestampNs = (long)(timestampRaw * unit);
        var payload = body[headerRest..];

        try
        {
            return objectType switch
            {
                TypeCanMessage or TypeCanMessage2 => ParseClassic(payload, timestampNs),
                TypeCanError or TypeCanErrorExtended => ParseError(payload, timestampNs),
                TypeCanFdMessage => ParseFd(payload, timestampNs),
                TypeCanFdMessage64 => ParseFd64(payload, timestampNs, body.Length + 16, objectHeaderSize),
                _ => null
            };
        }
        catch (ArgumentException)
        {
            // E.g. a standard (11-bit) frame whose identifier exceeds 0x7FF, or a remote frame
            // that claims payload bytes: that record is malformed, but it must not abort the
            // import of every other record in the file.
            return null;
        }
    }

    private static CanFrame? ParseClassic(ReadOnlySpan<byte> data, long timestampNs)
    {
        // channel(u16) flags(u8) dlc(u8) id(u32) data[8]
        if (data.Length < 16)
        {
            return null;
        }

        var channel = BinaryPrimitives.ReadUInt16LittleEndian(data);
        var dlc = Math.Min((int)data[3], 8);
        var rawId = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);
        var frameFlags = (rawId & 0x8000_0000u) != 0 ? CanFrameFlags.Extended : CanFrameFlags.None;
        var remote = (data[2] & 0x80) != 0;
        if (remote)
            frameFlags |= CanFrameFlags.Remote;
        if ((data[2] & 0x01) != 0) // flags bit 0: direction, 1 = Tx
            frameFlags |= CanFrameFlags.Transmit;
        return new CanFrame(
            Math.Max(0, channel - 1),
            rawId & 0x1FFF_FFFFu,
            remote ? ReadOnlySpan<byte>.Empty : data.Slice(8, dlc),
            timestampNs,
            frameFlags,
            declaredDataLength: (byte)dlc);
    }

    private static CanFrame? ParseError(ReadOnlySpan<byte> data, long timestampNs)
    {
        // channel(u16) length(u16) [reserved(u32)]
        if (data.Length < 4)
        {
            return null;
        }

        var channel = BinaryPrimitives.ReadUInt16LittleEndian(data);
        if (data.Length >= 32) // CAN_ERROR_EXT (type 73)
        {
            var rawId = BinaryPrimitives.ReadUInt32LittleEndian(data[16..]);
            var flags = CanFrameFlags.Error |
                ((rawId & 0x8000_0000u) != 0 ? CanFrameFlags.Extended : CanFrameFlags.None);
            var validBytes = Math.Min((int)data[10], 8);
            return new CanFrame(Math.Max(0, channel - 1), rawId & 0x1FFF_FFFFu,
                data.Slice(24, validBytes), timestampNs, flags);
        }
        return new CanFrame(Math.Max(0, channel - 1), 0, [], timestampNs, CanFrameFlags.Error);
    }

    private static CanFrame? ParseFd(ReadOnlySpan<byte> data, long timestampNs)
    {
        // channel(u16) flags(u8) dlc(u8) id(u32) frameLength(u32) arbBitCount(u8)
        // canFdFlags(u8) validDataBytes(u8) reserved(u8) reserved(u32) data[64]
        if (data.Length < 20)
        {
            return null;
        }

        var channel = BinaryPrimitives.ReadUInt16LittleEndian(data);
        var rawId = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);
        var canFdFlags = data[13];
        var validBytes = Math.Min((int)data[14], 64);
        var frameFlags = CanFrameFlags.None;
        if ((rawId & 0x8000_0000u) != 0)
        {
            frameFlags |= CanFrameFlags.Extended;
        }
        if ((data[2] & 0x80) != 0)
            frameFlags |= CanFrameFlags.Remote;
        if ((data[2] & 0x01) != 0)
            frameFlags |= CanFrameFlags.Transmit;

        if ((canFdFlags & 0x1) != 0)
        {
            frameFlags |= CanFrameFlags.FlexibleDataRate;
        }

        if ((canFdFlags & 0x2) != 0)
        {
            frameFlags |= CanFrameFlags.BitRateSwitch;
        }

        var available = Math.Min(validBytes, Math.Max(0, data.Length - 20));
        return new CanFrame(
            Math.Max(0, channel - 1),
            rawId & 0x1FFF_FFFFu,
            data.Slice(20, available),
            timestampNs,
            frameFlags);
    }

    private static CanFrame? ParseFd64(
        ReadOnlySpan<byte> data, long timestampNs, int objectSize, int objectHeaderSize)
    {
        // channel(u8) dlc(u8) validDataBytes(u8) txCount(u8) id(u32) frameLength(u32)
        // flags(u32) btrCfgArb(u32) btrCfgData(u32) timeOffsetBrs(u32) timeOffsetCrcDel(u32)
        // bitCount(u16) dir(u8) extDataOffset(u8) crc(u32) data[validDataBytes]
        if (data.Length < 40)
        {
            return null;
        }

        var channel = data[0];
        var validBytes = Math.Min((int)data[2], 64);
        var rawId = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);
        var fdFlags = BinaryPrimitives.ReadUInt32LittleEndian(data[12..]);
        var frameFlags = CanFrameFlags.None;
        if ((rawId & 0x8000_0000u) != 0)
        {
            frameFlags |= CanFrameFlags.Extended;
        }

        if ((fdFlags & 0x0010) != 0)
            frameFlags |= CanFrameFlags.Remote;
        if (data[34] == 1) // dir: 0 = Rx, 1 = Tx
            frameFlags |= CanFrameFlags.Transmit;
        if ((fdFlags & 0x1000) != 0)
        {
            frameFlags |= CanFrameFlags.FlexibleDataRate;
        }

        if ((fdFlags & 0x2000) != 0)
        {
            frameFlags |= CanFrameFlags.BitRateSwitch;
        }

        // validDataBytes may exceed the data actually stored in the object (python-can issue
        // 1905): the stored part ends at the extended-data offset (or the object end) and the
        // remaining declared bytes are zero. Never read past the object or past 64 bytes.
        var extDataOffset = data[35];
        var storedEnd = extDataOffset != 0 && extDataOffset <= objectSize ? extDataOffset : objectSize;
        var stored = Math.Max(0, storedEnd - objectHeaderSize - 40);
        var available = Math.Min(Math.Min(validBytes, stored), Math.Max(0, data.Length - 40));
        Span<byte> payload = stackalloc byte[64];
        payload.Clear();
        data.Slice(40, available).CopyTo(payload);
        return new CanFrame(
            Math.Max(0, channel - 1),
            rawId & 0x1FFF_FFFFu,
            payload[..validBytes],
            timestampNs,
            frameFlags);
    }

    /// <summary>Growable byte buffer with cheap front-consumption for the contained object stream.</summary>
    private sealed class ContainerBuffer
    {
        private byte[] _data = new byte[1 << 20];
        private int _start;
        private int _end;

        public ReadOnlySpan<byte> Span => _data.AsSpan(_start, _end - _start);
        public int NonPaddingTailBytes
        {
            get
            {
                for (var index = _start; index < _end; index++)
                    if (_data[index] != 0) return _end - _start;
                return 0;
            }
        }

        public void Append(ReadOnlySpan<byte> bytes)
        {
            if (_end + bytes.Length > _data.Length)
            {
                var length = _end - _start;
                if (length + bytes.Length > _data.Length)
                {
                    var grown = new byte[Math.Max(_data.Length * 2, length + bytes.Length)];
                    _data.AsSpan(_start, length).CopyTo(grown);
                    _data = grown;
                }
                else
                {
                    _data.AsSpan(_start, length).CopyTo(_data);
                }

                _end = length;
                _start = 0;
            }

            bytes.CopyTo(_data.AsSpan(_end));
            _end += bytes.Length;
        }

        public void Consume(int count)
        {
            _start = Math.Min(_end, _start + count);
            if (_start == _end)
            {
                _start = 0;
                _end = 0;
            }
        }
    }
}
