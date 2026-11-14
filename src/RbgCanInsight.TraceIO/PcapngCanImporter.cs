using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using RbgCanInsight.Domain;

namespace RbgCanInsight.TraceIO;

/// <summary>
/// Reader for pcapng (and classic pcap) captures holding LINKTYPE_CAN_SOCKETCAN (227) packets.
/// <para>Each packet is a Linux SocketCAN <c>can_frame</c> (16 bytes) or <c>canfd_frame</c> (72 bytes):
/// 4-byte CAN id + EFF/RTR/ERR flags in network byte order (independent of the pcapng section
/// endianness, per the libpcap link-type specification), length, FD flags (BRS 0x01, ESI 0x02,
/// FDF 0x04), two reserved bytes, then the data.</para>
/// <para>Interface id becomes the zero-based channel (classic pcap: channel 0). Timestamps honour
/// <c>if_tsresol</c> (default 10^-6) and <c>if_tsoffset</c>; resolutions finer than 1 ns are truncated
/// to the nanosecond. Packets of other link types are skipped and counted through
/// <see cref="TraceImportHealth"/>; truncated or inconsistent blocks raise
/// <see cref="InvalidDataException"/>. Limitations: ESI is not representable in <see cref="CanFrame"/>
/// (payload kept, a warning is recorded); SocketCAN error frames are kept as <c>Error</c> frames and
/// carry the 29-bit error-class word as identifier (marked Extended when it exceeds 11 bits);
/// Simple Packet Blocks and obsolete Packet Blocks carry no usable timestamp and are skipped;
/// CAN XL packets (byte 4 bit 0x80) are decoded by <see cref="CanXlWire"/> into
/// <see cref="TraceImportHealth.BusFrames"/> and never enter the CAN frame stream.</para>
/// </summary>
public static class PcapngCanImporter
{
    public const int LinkTypeCanSocketCan = 227;
    private const uint ShbType = 0x0A0D0D0A;
    private const uint IdbType = 1;
    private const uint ObsoletePacketType = 2;
    private const uint SimplePacketType = 3;
    private const uint EnhancedPacketType = 6;
    private const uint ByteOrderMagic = 0x1A2B3C4D;
    private const int MaxBlockBytes = 64 * 1024 * 1024;

    private const uint EffFlag = 0x8000_0000, RtrFlag = 0x4000_0000, ErrFlag = 0x2000_0000;
    private const int ClassicFrameBytes = 16, FdFrameBytes = 72;

    private readonly record struct InterfaceInfo(int LinkType, bool Base2, int Exponent, long OffsetSeconds);

    /// <summary>Consumed: a CAN XL packet that went to the bus side collection and yields no CAN frame.</summary>
    private enum Outcome { Ok, Rejected, Consumed }

    public static async IAsyncEnumerable<CanFrame> ReadAsync(
        string path,
        [EnumeratorCancellation] CancellationToken cancellationToken = default,
        TraceImportHealth? diagnostics = null)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
            bufferSize: 1 << 20, FileOptions.SequentialScan | FileOptions.Asynchronous);
        var first = new byte[4];
        var got = await ReadAsync(stream, first, 4, cancellationToken);
        if (got < 4)
            throw new InvalidDataException("File is too short to be a pcap or pcapng capture.");
        var magic = BinaryPrimitives.ReadUInt32LittleEndian(first);

        IAsyncEnumerable<CanFrame> source =
            magic == ShbType ? ReadPcapngAsync(stream, first, diagnostics, cancellationToken)
            : magic is 0xA1B2C3D4 or 0xD4C3B2A1 or 0xA1B23C4D or 0x4D3CB2A1
                ? ReadClassicAsync(stream, magic, diagnostics, cancellationToken)
                : throw new InvalidDataException("Not a pcap or pcapng file (unknown magic number).");
        await foreach (var frame in source.WithCancellation(cancellationToken))
            yield return frame;
    }

    // ------------------------------------------------------------------ pcapng

    private static async IAsyncEnumerable<CanFrame> ReadPcapngAsync(
        Stream stream, byte[] firstTypeBytes, TraceImportHealth? diagnostics,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var interfaces = new List<InterfaceInfo>();
        var littleEndian = true;
        long sequence = 0;
        long offset = 4;
        var typeBytes = firstTypeBytes;
        var haveType = true;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var blockStart = offset - (haveType ? 4 : 0);
            if (!haveType)
            {
                var n = await ReadAsync(stream, typeBytes, 4, cancellationToken);
                if (n == 0) yield break;
                if (n < 4) throw Truncated("block type", blockStart);
                offset += 4;
            }
            haveType = false;

            var lenBytes = new byte[4];
            if (await ReadAsync(stream, lenBytes, 4, cancellationToken) < 4)
                throw Truncated("block length", blockStart);
            offset += 4;

            uint type;
            uint totalLength;
            byte[] body;
            if (BinaryPrimitives.ReadUInt32LittleEndian(typeBytes) == ShbType)
            {
                type = ShbType;
                // Byte order is only known once the magic after the length has been read.
                var magicBytes = new byte[4];
                if (await ReadAsync(stream, magicBytes, 4, cancellationToken) < 4)
                    throw Truncated("section header", blockStart);
                offset += 4;
                var magicLe = BinaryPrimitives.ReadUInt32LittleEndian(magicBytes);
                if (magicLe == ByteOrderMagic) littleEndian = true;
                else if (magicLe == BinaryPrimitives.ReverseEndianness(ByteOrderMagic)) littleEndian = false;
                else throw new InvalidDataException(
                    $"Invalid pcapng section header byte-order magic at offset {blockStart}.");
                totalLength = ReadU32(lenBytes, 0, littleEndian);
                CheckLength(totalLength, 28, blockStart, stream);
                body = new byte[totalLength - 8];
                magicBytes.CopyTo(body, 0);
                var rest = (int)totalLength - 12;
                if (await ReadAsync(stream, body.AsMemory(4), rest, cancellationToken) < rest)
                    throw Truncated("section header body", blockStart);
                offset += rest;
                if (ReadU16(body, 4, littleEndian) != 1)
                    throw new InvalidDataException(
                        $"Unsupported pcapng major version {ReadU16(body, 4, littleEndian)}.");
                interfaces.Clear();
            }
            else
            {
                type = ReadU32(typeBytes, 0, littleEndian);
                totalLength = ReadU32(lenBytes, 0, littleEndian);
                CheckLength(totalLength, 12, blockStart, stream);
                body = new byte[totalLength - 8];
                if (await ReadAsync(stream, body, body.Length, cancellationToken) < body.Length)
                    throw Truncated("block body", blockStart);
                offset += body.Length;
            }

            if (ReadU32(body, body.Length - 4, littleEndian) != totalLength)
                throw new InvalidDataException(
                    $"pcapng block at offset {blockStart} has mismatching leading and trailing lengths.");

            switch (type)
            {
                case ShbType:
                    break;
                case IdbType:
                    interfaces.Add(ParseInterface(body, littleEndian, blockStart));
                    break;
                case EnhancedPacketType:
                {
                    var outcome = ParseEnhancedPacket(body, littleEndian, interfaces, blockStart,
                        diagnostics, out var frame);
                    if (outcome == Outcome.Ok)
                        yield return frame with { Sequence = ++sequence };
                    break;
                }
                case SimplePacketType or ObsoletePacketType:
                    diagnostics?.RecordUnsupportedSourceObject("PCAPNG_PACKET_WITHOUT_TIMESTAMP",
                        "A pcapng Simple/obsolete Packet Block carries no usable timestamp or interface mapping; skipped.");
                    break;
                default:
                    break; // name resolution, statistics, decryption secrets, custom blocks...
            }
        }
    }

    private static InterfaceInfo ParseInterface(byte[] body, bool le, long blockStart)
    {
        if (body.Length < 12)
            throw new InvalidDataException($"Truncated pcapng interface description block at offset {blockStart}.");
        var linkType = ReadU16(body, 0, le);
        var base2 = false;
        var exponent = 6; // default: microseconds
        long tsOffset = 0;
        var position = 8;
        var end = body.Length - 4;
        while (position + 4 <= end)
        {
            var code = ReadU16(body, position, le);
            var length = ReadU16(body, position + 2, le);
            position += 4;
            if (code == 0) break;
            var padded = (length + 3) & ~3;
            if (position + padded > end)
                throw new InvalidDataException(
                    $"pcapng interface option overruns its block at offset {blockStart}.");
            if (code == 9)
            {
                if (length != 1)
                    throw new InvalidDataException($"Invalid if_tsresol option length at offset {blockStart}.");
                var value = body[position];
                base2 = (value & 0x80) != 0;
                exponent = value & 0x7F;
                if (base2 ? exponent > 120 : exponent > 30)
                    throw new InvalidDataException(
                        $"Unsupported if_tsresol value 0x{value:X2} at offset {blockStart}.");
            }
            else if (code == 14 && length == 8)
            {
                tsOffset = le
                    ? BinaryPrimitives.ReadInt64LittleEndian(body.AsSpan(position))
                    : BinaryPrimitives.ReadInt64BigEndian(body.AsSpan(position));
            }
            position += padded;
        }
        return new InterfaceInfo(linkType, base2, exponent, tsOffset);
    }

    private static Outcome ParseEnhancedPacket(byte[] body, bool le, List<InterfaceInfo> interfaces,
        long blockStart, TraceImportHealth? diagnostics, out CanFrame frame)
    {
        frame = default;
        if (body.Length < 20 + 4)
            throw new InvalidDataException($"Truncated pcapng enhanced packet block at offset {blockStart}.");
        var interfaceId = ReadU32(body, 0, le);
        var timestamp = ((ulong)ReadU32(body, 4, le) << 32) | ReadU32(body, 8, le);
        var captured = ReadU32(body, 12, le);
        var original = ReadU32(body, 16, le);
        if (captured > body.Length - 24)
            throw new InvalidDataException(
                $"pcapng packet at offset {blockStart} declares {captured} captured bytes but its block is shorter.");
        if (interfaceId >= interfaces.Count)
            throw new InvalidDataException(
                $"pcapng packet at offset {blockStart} references undefined interface {interfaceId}.");
        var iface = interfaces[(int)interfaceId];
        if (iface.LinkType != LinkTypeCanSocketCan)
        {
            diagnostics?.RecordUnsupportedSourceObject("PCAPNG_NON_CAN_LINKTYPE",
                $"A packet on a link type {iface.LinkType} interface was skipped; only SocketCAN (227) is supported.");
            return Outcome.Rejected;
        }
        if (!TryConvertTimestamp(timestamp, iface, out var nanoseconds))
        {
            diagnostics?.RecordRejectedSourceRecord("PCAPNG_TIMESTAMP_RANGE",
                "A pcapng packet timestamp does not fit a signed 64-bit nanosecond value.");
            return Outcome.Rejected;
        }
        if (captured < original)
        {
            diagnostics?.RecordRejectedSourceRecord("PCAPNG_SNAPLEN_TRUNCATED",
                "A packet was truncated by the capture snap length; skipped rather than reconstructed.");
            return Outcome.Rejected;
        }
        return ParseSocketCan(body.AsSpan(20, (int)captured), (int)interfaceId, nanoseconds, diagnostics, out frame);
    }

    // ------------------------------------------------------------------ classic pcap

    private static async IAsyncEnumerable<CanFrame> ReadClassicAsync(
        Stream stream, uint magicLe, TraceImportHealth? diagnostics,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var littleEndian = magicLe is 0xA1B2C3D4 or 0xA1B23C4D;
        var nano = magicLe is 0xA1B23C4D or 0x4D3CB2A1;
        var header = new byte[20];
        if (await ReadAsync(stream, header, 20, cancellationToken) < 20)
            throw Truncated("pcap file header", 4);
        var linkType = (int)(ReadU32(header, 16, littleEndian) & 0xFFFF);
        long sequence = 0;
        long offset = 24;
        var record = new byte[16];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var n = await ReadAsync(stream, record, 16, cancellationToken);
            if (n == 0) yield break;
            if (n < 16) throw Truncated("pcap record header", offset);
            var seconds = ReadU32(record, 0, littleEndian);
            var fraction = ReadU32(record, 4, littleEndian);
            var included = ReadU32(record, 8, littleEndian);
            var original = ReadU32(record, 12, littleEndian);
            if (included > MaxBlockBytes)
                throw new InvalidDataException($"Implausible pcap record length {included} at offset {offset}.");
            var data = new byte[included];
            if (await ReadAsync(stream, data, data.Length, cancellationToken) < data.Length)
                throw Truncated("pcap record data", offset);
            offset += 16 + included;

            if (linkType != LinkTypeCanSocketCan)
            {
                diagnostics?.RecordUnsupportedSourceObject("PCAP_NON_CAN_LINKTYPE",
                    $"A packet on link type {linkType} was skipped; only SocketCAN (227) is supported.");
                continue;
            }
            if (fraction >= (nano ? 1_000_000_000u : 1_000_000u))
            {
                diagnostics?.RecordRejectedSourceRecord("PCAP_TIMESTAMP_FRACTION",
                    "A pcap record has a sub-second timestamp field out of range; skipped.");
                continue;
            }
            if (included < original)
            {
                diagnostics?.RecordRejectedSourceRecord("PCAP_SNAPLEN_TRUNCATED",
                    "A packet was truncated by the capture snap length; skipped rather than reconstructed.");
                continue;
            }
            var ns = checked(seconds * 1_000_000_000L + (nano ? fraction : fraction * 1000L));
            if (ParseSocketCan(data, 0, ns, diagnostics, out var frame) == Outcome.Ok)
                yield return frame with { Sequence = ++sequence };
        }
    }

    // ------------------------------------------------------------------ SocketCAN payload

    private static Outcome ParseSocketCan(ReadOnlySpan<byte> packet, int channel, long timestampNs,
        TraceImportHealth? diagnostics, out CanFrame frame)
    {
        frame = default;
        if (CanXlWire.IsPcapXl(packet))
        {
            var xl = CanXlWire.ParsePcapPacket(packet, channel, timestampNs, out var xlError);
            if (xl is null)
                return Reject(diagnostics, "SOCKETCAN_INVALID_CANXL", xlError!);
            diagnostics?.BusFrames.Add(xl);
            return Outcome.Consumed;
        }
        if (packet.Length is not (ClassicFrameBytes or FdFrameBytes))
        {
            diagnostics?.RecordRejectedSourceRecord("SOCKETCAN_UNSUPPORTED_SIZE",
                $"A SocketCAN packet of {packet.Length} bytes is neither a can_frame (16) nor a canfd_frame (72); skipped.");
            return Outcome.Rejected;
        }

        var canId = BinaryPrimitives.ReadUInt32BigEndian(packet);
        var length = packet[4];
        var isFd = packet.Length == FdFrameBytes;
        var flags = CanFrameFlags.None;
        var isError = (canId & ErrFlag) != 0;
        var isRemote = (canId & RtrFlag) != 0;
        var isExtended = (canId & EffFlag) != 0;
        var identifier = canId & 0x1FFF_FFFFu;

        if (isError)
        {
            if (isRemote || isFd || length > 8)
                return Reject(diagnostics, "SOCKETCAN_INVALID_ERROR_FRAME",
                    "A SocketCAN error frame has an inconsistent RTR/FD/length combination; skipped.");
            flags |= CanFrameFlags.Error;
            if (isExtended || identifier > 0x7FF) flags |= CanFrameFlags.Extended;
        }
        else
        {
            if (isExtended) flags |= CanFrameFlags.Extended;
            else if (identifier > 0x7FF)
                return Reject(diagnostics, "SOCKETCAN_INVALID_IDENTIFIER",
                    "A standard-format SocketCAN frame carries identifier bits above bit 10; skipped.");
        }

        if (isFd)
        {
            var fdFlags = packet[5];
            if (isRemote || isError)
                return Reject(diagnostics, "SOCKETCAN_INVALID_FD_FRAME",
                    "A CAN FD packet is marked RTR or error; skipped.");
            if (length > 64 || !CanFdLength.IsValidFdLength(length))
                return Reject(diagnostics, "SOCKETCAN_INVALID_FD_LENGTH",
                    "A CAN FD packet declares a length that is not a valid CAN FD payload size; skipped.");
            flags |= CanFrameFlags.FlexibleDataRate;
            if ((fdFlags & 0x01) != 0) flags |= CanFrameFlags.BitRateSwitch;
            if ((fdFlags & 0x02) != 0)
                diagnostics?.RecordFileMetadataWarning("PCAPNG_ESI_NOT_REPRESENTED",
                    "At least one CAN FD frame had the error-state-indicator bit set; the model cannot store ESI, payload kept.");
            frame = new CanFrame(channel, identifier, packet.Slice(8, length), timestampNs, flags);
            return Outcome.Ok;
        }

        if (length > 8)
            return Reject(diagnostics, "SOCKETCAN_INVALID_LENGTH",
                "A classic SocketCAN frame declares more than 8 data bytes; skipped.");
        if (isRemote)
        {
            if (isError) return Reject(diagnostics, "SOCKETCAN_INVALID_REMOTE", "Remote and error flags both set; skipped.");
            frame = new CanFrame(channel, identifier, [], timestampNs, flags | CanFrameFlags.Remote,
                declaredDataLength: length);
            return Outcome.Ok;
        }
        frame = new CanFrame(channel, identifier, packet.Slice(8, length), timestampNs, flags);
        return Outcome.Ok;
    }

    private static Outcome Reject(TraceImportHealth? diagnostics, string code, string message)
    {
        diagnostics?.RecordRejectedSourceRecord(code, message);
        return Outcome.Rejected;
    }

    // ------------------------------------------------------------------ helpers

    private static bool TryConvertTimestamp(ulong ticks, InterfaceInfo iface, out long nanoseconds)
    {
        UInt128 value;
        if (iface.Base2)
            value = ((UInt128)ticks * 1_000_000_000UL) >> iface.Exponent;
        else if (iface.Exponent <= 9)
            value = (UInt128)ticks * Pow10(9 - iface.Exponent);
        else
            value = (UInt128)ticks / Pow10(iface.Exponent - 9);

        nanoseconds = 0;
        if (value > (UInt128)long.MaxValue) return false;
        try { nanoseconds = checked((long)value + checked(iface.OffsetSeconds * 1_000_000_000L)); }
        catch (OverflowException) { return false; }
        return true;
    }

    private static UInt128 Pow10(int exponent)
    {
        UInt128 result = 1;
        for (var i = 0; i < exponent; i++) result *= 10;
        return result;
    }

    private static void CheckLength(uint total, uint minimum, long blockStart, Stream stream)
    {
        if (total < minimum || (total & 3) != 0 || total > MaxBlockBytes)
            throw new InvalidDataException(
                $"Invalid pcapng block length {total} at offset {blockStart}.");
        if (stream.CanSeek && blockStart + total > stream.Length)
            throw Truncated("block", blockStart);
    }

    private static InvalidDataException Truncated(string what, long offset) =>
        new($"Truncated pcap/pcapng data: {what} at offset {offset} extends past the end of the file.");

    private static uint ReadU32(byte[] data, int offset, bool le) => le
        ? BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset))
        : BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset));

    private static ushort ReadU16(byte[] data, int offset, bool le) => le
        ? BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset))
        : BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset));

    private static async Task<int> ReadAsync(Stream stream, byte[] buffer, int count, CancellationToken token) =>
        await ReadAsync(stream, buffer.AsMemory(), count, token);

    private static async Task<int> ReadAsync(Stream stream, Memory<byte> buffer, int count, CancellationToken token)
    {
        var total = 0;
        while (total < count)
        {
            var read = await stream.ReadAsync(buffer.Slice(total, count - total), token);
            if (read == 0) break;
            total += read;
        }
        return total;
    }
}
