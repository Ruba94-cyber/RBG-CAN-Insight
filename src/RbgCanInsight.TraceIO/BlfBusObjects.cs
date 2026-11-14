using System.Buffers.Binary;
using RbgCanInsight.Domain;

namespace RbgCanInsight.TraceIO;

/// <summary>
/// BLF LIN and FlexRay object readers. Layouts follow the public vector_blf object definitions
/// (Technica-Engineering/vector_blf); they were NOT checked against files written by Vector tools,
/// only against hand-built byte streams. Object types whose layout is not established (including every
/// CAN XL object) are not handled here and are counted as unsupported by the caller.
/// </summary>
internal static class BlfBusObjects
{
    internal const uint LinMessage = 11, LinCrcError = 12, LinReceiveError = 14, LinSendError = 15,
        LinSleep = 20, LinWakeup = 21, FlexRayData = 29, FlexRaySync = 30, FlexRayMessage = 41,
        FlexRayRcvMessage = 50, LinMessage2 = 57, FlexRayRcvMessageEx = 66;

    public static bool IsBusObject(uint type) => type is LinMessage or LinCrcError or LinReceiveError
        or LinSendError or LinSleep or LinWakeup or FlexRayData or FlexRaySync or FlexRayMessage
        or FlexRayRcvMessage or LinMessage2 or FlexRayRcvMessageEx;

    /// <summary>Decodes one bus object into <paramref name="diagnostics"/>' side collection.
    /// Malformed bodies are counted as rejected records, never turned into frames.</summary>
    public static void Handle(uint type, ReadOnlySpan<byte> body, int objectHeaderSize, TraceImportHealth? diagnostics)
    {
        if (diagnostics is null) return;
        IBusFrame? frame = null;
        try { frame = Parse(type, body, objectHeaderSize); }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException or OverflowException) { }
        if (frame is null)
            diagnostics.RecordMalformedObject(type, "LIN/FlexRay object body could not be decoded.");
        else
            diagnostics.BusFrames.Add(frame);
    }

    private static IBusFrame? Parse(uint type, ReadOnlySpan<byte> body, int objectHeaderSize)
    {
        var headerRest = objectHeaderSize switch { 40 => 24, 32 or 16 => 16, _ => -1 };
        if (headerRest < 0 || body.Length < headerRest) return null;
        var flags = BinaryPrimitives.ReadUInt32LittleEndian(body);
        var raw = BinaryPrimitives.ReadUInt64LittleEndian(body[8..]);
        var unit = flags == 1 ? 10_000UL : 1UL;
        if (raw > (ulong)long.MaxValue / unit) return null;
        var ts = (long)(raw * unit);
        var d = body[headerRest..];
        return type switch
        {
            LinMessage => ParseLinMessage(d, ts, type, LinEventKind.Message),
            LinCrcError => ParseLinMessage(d, ts, type, LinEventKind.CrcError),
            LinSendError => ParseLinSend(d, ts, type),
            LinReceiveError => ParseLinReceive(d, ts, type),
            LinWakeup => ParseLinWakeup(d, ts, type),
            LinSleep => ParseLinSleep(d, ts, type),
            LinMessage2 => ParseLinMessage2(d, ts, type),
            FlexRayData => ParseFlexRayLegacy(d, ts, type, sync: false),
            FlexRaySync => ParseFlexRayLegacy(d, ts, type, sync: true),
            FlexRayMessage => ParseFlexRayV6(d, ts, type),
            FlexRayRcvMessage => ParseFlexRayRcv(d, ts, type, extended: false),
            FlexRayRcvMessageEx => ParseFlexRayRcv(d, ts, type, extended: true),
            _ => null
        };
    }

    private static int Ch(ushort channel) => Math.Max(0, channel - 1);

    private static BusDirection Dir(byte value) => value switch { 0 => BusDirection.Rx, 1 => BusDirection.Tx, _ => BusDirection.Unknown };

    private static LinFrame Lin(int channel, long ts, uint type, LinEventKind kind, byte id, int dlc, byte[] data, byte? checksum,
        BusDirection dir, string detail = "") => new()
    {
        Channel = channel, TimestampNanoseconds = ts, SourceObjectType = type, Event = kind, RawIdByte = id,
        IdIsProtected = id > 0x3F, DeclaredLength = dlc, Data = data, Checksum = checksum, Direction = dir, Detail = detail
    };

    // LIN_MESSAGE / LIN_CRC_ERROR: channel u16, id u8, dlc u8, data[8], fsmId, fsmState, headerTime, fullTime, crc u16, dir u8
    private static LinFrame? ParseLinMessage(ReadOnlySpan<byte> d, long ts, uint type, LinEventKind kind)
    {
        if (d.Length < 20 || d[3] > 8) return null;
        var dlc = d[3];
        var crc = BinaryPrimitives.ReadUInt16LittleEndian(d[16..]);
        return Lin(Ch(BinaryPrimitives.ReadUInt16LittleEndian(d)), ts, type, kind, d[2], dlc,
            d.Slice(4, dlc).ToArray(), (byte)(crc & 0xFF), Dir(d[18]));
    }

    // LIN_SND_ERROR: channel u16, id, dlc, fsmId, fsmState, headerTime, fullTime
    private static LinFrame? ParseLinSend(ReadOnlySpan<byte> d, long ts, uint type)
    {
        if (d.Length < 8) return null;
        return Lin(Ch(BinaryPrimitives.ReadUInt16LittleEndian(d)), ts, type, LinEventKind.SendError, d[2], d[3], [], null,
            BusDirection.Unknown, $"header time {d[6]} bit, full time {d[7]} bit");
    }

    // LIN_RCV_ERROR: ... + stateReason, offendingByte, shortError, timeoutDuringDlcDetection
    private static LinFrame? ParseLinReceive(ReadOnlySpan<byte> d, long ts, uint type)
    {
        if (d.Length < 12) return null;
        return Lin(Ch(BinaryPrimitives.ReadUInt16LittleEndian(d)), ts, type, LinEventKind.ReceiveError, d[2], d[3], [], null,
            BusDirection.Unknown,
            $"state reason {d[8]}, offending byte 0x{d[9]:X2}, short error {d[10]}, DLC-detection timeout {d[11]}");
    }

    private static LinFrame? ParseLinWakeup(ReadOnlySpan<byte> d, long ts, uint type)
    {
        if (d.Length < 4) return null;
        return Lin(Ch(BinaryPrimitives.ReadUInt16LittleEndian(d)), ts, type, LinEventKind.Wakeup, 0, 0, [], null,
            BusDirection.Unknown, $"signal {d[2]}, external {d[3]}");
    }

    private static LinFrame? ParseLinSleep(ReadOnlySpan<byte> d, long ts, uint type)
    {
        if (d.Length < 4) return null;
        return Lin(Ch(BinaryPrimitives.ReadUInt16LittleEndian(d)), ts, type, LinEventKind.Sleep, 0, 0, [], null,
            BusDirection.Unknown, $"reason {d[2]}, flags 0x{d[3]:X2}");
    }

    // LIN_MESSAGE2: sof u64, baudrate u32, channel u16, rsv u16, breakLen u64, delLen u64, supplier u16, messageId u16,
    // nad u8, id u8, dlc u8, checksumModel u8, byteTimestamps u64[9], data[8], crc u16, dir u8, ...
    private static LinFrame? ParseLinMessage2(ReadOnlySpan<byte> d, long ts, uint type)
    {
        if (d.Length < 123 || d[38] > 8) return null;
        var dlc = d[38];
        var crc = BinaryPrimitives.ReadUInt16LittleEndian(d[120..]);
        var brk = BinaryPrimitives.ReadUInt64LittleEndian(d[16..]);
        var del = BinaryPrimitives.ReadUInt64LittleEndian(d[24..]);
        var frame = Lin(Ch(BinaryPrimitives.ReadUInt16LittleEndian(d[12..])), ts, type, LinEventKind.Message, d[37], dlc,
            d.Slice(112, dlc).ToArray(), (byte)(crc & 0xFF), Dir(d[122]));
        return frame with
        {
            Baudrate = BinaryPrimitives.ReadUInt32LittleEndian(d[8..]),
            BreakLengthNs = brk <= long.MaxValue ? (long)brk : null,
            SyncDelimiterNs = del <= long.MaxValue ? (long)del : null
        };
    }

    // FLEXRAY_DATA: channel u16, mux u8, len u8, messageId u16, crc u16, dir u8, rsv u8, rsv u16, data[12]
    // FLEXRAY_SYNC: same header, data[11], cycle u8. The legacy objects name no A/B channel and no segment.
    private static FlexRayFrame? ParseFlexRayLegacy(ReadOnlySpan<byte> d, long ts, uint type, bool sync)
    {
        if (d.Length < 24) return null;
        var maxLen = sync ? 11 : 12;
        var len = d[3];
        if (len > maxLen) return null;
        return new FlexRayFrame
        {
            Channel = Ch(BinaryPrimitives.ReadUInt16LittleEndian(d)), TimestampNanoseconds = ts, SourceObjectType = type,
            Slot = BinaryPrimitives.ReadUInt16LittleEndian(d[4..]),
            Cycle = sync ? (d[23] <= 63 ? d[23] : -1) : -1,
            Payload = d.Slice(12, len).ToArray(), DeclaredPayloadBytes = len,
            HeaderCrc = BinaryPrimitives.ReadUInt16LittleEndian(d[6..]),
            Flags = sync ? FlexRayFlags.Sync : FlexRayFlags.None,
            Direction = Dir(d[8]), Detail = $"mux {d[2]}"
        };
    }

    // FLEXRAY_MESSAGE (V6): channel u16, dir u8, lowTime u8, fpgaTick u32, overflow u32, client u32, clusterTime u32,
    // frameId u16, headerCrc u16, frameState u16, length u8, cycle u8, headerBitMask u8, rsv u8, rsv u16, data[64]
    private static FlexRayFrame? ParseFlexRayV6(ReadOnlySpan<byte> d, long ts, uint type)
    {
        if (d.Length < 32) return null;
        var state = BinaryPrimitives.ReadUInt16LittleEndian(d[24..]);
        var len = d[26];
        var avail = Math.Min(len, Math.Min(64, d.Length - 32));
        var f = FlexRayFlags.None;
        if ((state & 0x01) != 0) f |= FlexRayFlags.PayloadPreamble;
        if ((state & 0x02) != 0) f |= FlexRayFlags.Sync;
        if ((state & 0x08) != 0) f |= FlexRayFlags.NullFrame;
        if ((state & 0x10) != 0) f |= FlexRayFlags.Startup;
        return new FlexRayFrame
        {
            Channel = Ch(BinaryPrimitives.ReadUInt16LittleEndian(d)), TimestampNanoseconds = ts, SourceObjectType = type,
            Slot = BinaryPrimitives.ReadUInt16LittleEndian(d[20..]), Cycle = d[27] <= 63 ? d[27] : -1,
            Payload = d.Slice(32, avail).ToArray(), DeclaredPayloadBytes = len,
            HeaderCrc = BinaryPrimitives.ReadUInt16LittleEndian(d[22..]),
            Flags = f, Direction = Dir(d[2])
        };
    }

    // FLEXRAY_RCVMESSAGE: channel u16, version u16, channelMask u16, dir u8, rsv u8, client u32, clusterNo u32,
    // frameId u16, hcrc1 u16, hcrc2 u16, byteCount u16, dataCount u16, cycle u8, rsv u8, tag u32, data u32,
    // frameFlags u32, appParam u32, dataBytes[254]. The _EX variant widens dir/cycle to u16, adds frameCrc u32,
    // frameLengthNs u32, frameId1 u16, pduOffset u16, blfLogMask u16, reserved u16[13] and a dataCount-sized payload.
    private static FlexRayFrame? ParseFlexRayRcv(ReadOnlySpan<byte> d, long ts, uint type, bool extended)
    {
        var dataOffset = extended ? 84 : 44;
        if (d.Length < dataOffset) return null;
        var mask = BinaryPrimitives.ReadUInt16LittleEndian(d[4..]);
        var byteCount = BinaryPrimitives.ReadUInt16LittleEndian(d[22..]);
        var dataCount = BinaryPrimitives.ReadUInt16LittleEndian(d[24..]);
        var cycle = d[26];
        var fl = BinaryPrimitives.ReadUInt32LittleEndian(d[36..]);
        var stored = Math.Min(Math.Min((int)dataCount, (int)byteCount), Math.Min(254, d.Length - dataOffset));
        var flags = FlexRayFlags.None;
        if ((fl & 0x01) != 0) flags |= FlexRayFlags.NullFrame;
        if ((fl & 0x04) != 0) flags |= FlexRayFlags.Sync;
        if ((fl & 0x08) != 0) flags |= FlexRayFlags.Startup;
        if ((fl & 0x10) != 0) flags |= FlexRayFlags.PayloadPreamble;
        if ((fl & 0x40) != 0) flags |= FlexRayFlags.Error;
        var chan = mask switch { 1 => FlexRayChannel.A, 2 => FlexRayChannel.B, 3 => FlexRayChannel.AB, _ => FlexRayChannel.Unknown };
        return new FlexRayFrame
        {
            Channel = Ch(BinaryPrimitives.ReadUInt16LittleEndian(d)), TimestampNanoseconds = ts, SourceObjectType = type,
            Slot = BinaryPrimitives.ReadUInt16LittleEndian(d[16..]), Cycle = cycle <= 63 ? cycle : -1,
            FlexRayChannel = chan,
            Payload = d.Slice(dataOffset, Math.Max(0, stored)).ToArray(), DeclaredPayloadBytes = byteCount,
            HeaderCrc = BinaryPrimitives.ReadUInt16LittleEndian(d[18..]),
            HeaderCrcB = BinaryPrimitives.ReadUInt16LittleEndian(d[20..]),
            FrameCrc = extended ? BinaryPrimitives.ReadUInt32LittleEndian(d[44..]) : null,
            IsDynamic = (fl & 0x0010_0000) != 0,
            Flags = flags, Direction = Dir(d[6])
        };
    }
}
