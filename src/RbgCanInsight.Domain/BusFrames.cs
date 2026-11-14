namespace RbgCanInsight.Domain;

/// <summary>Bus kinds carried by the non-CAN frame models. Kept separate from <see cref="CanFrame"/>,
/// which stays the only frame model of the CAN pipeline.</summary>
public enum BusKind { Lin, FlexRay, CanXl }

/// <summary>Common view of an imported non-CAN bus record.</summary>
public interface IBusFrame
{
    BusKind Kind { get; }
    /// <summary>Zero-based channel (application channel minus one, as for CAN).</summary>
    int Channel { get; }
    long TimestampNanoseconds { get; }
    /// <summary>BLF object type the record came from (0 when not from BLF).</summary>
    uint SourceObjectType { get; }
}

public enum BusDirection { Unknown, Rx, Tx }

public enum LinEventKind { Message, CrcError, SendError, ReceiveError, Wakeup, Sleep }

/// <summary>One LIN bus record: a frame or a bus event. Immutable.</summary>
public sealed record LinFrame : IBusFrame
{
    public BusKind Kind => BusKind.Lin;
    public int Channel { get; init; }
    public long TimestampNanoseconds { get; init; }
    public uint SourceObjectType { get; init; }
    public LinEventKind Event { get; init; } = LinEventKind.Message;
    /// <summary>The identifier byte exactly as recorded.</summary>
    public byte RawIdByte { get; init; }
    /// <summary>True when the recorded byte is known to be a protected identifier (PID, includes parity).
    /// The BLF reader sets this only when the byte exceeds 0x3F, because a recorded value of 0..0x3F
    /// cannot be told apart from a bare identifier.</summary>
    public bool IdIsProtected { get; init; }
    /// <summary>6-bit frame identifier.</summary>
    public byte FrameId => (byte)(RawIdByte & 0x3F);
    /// <summary>Data length declared by the record (may exceed <see cref="Data"/> when truncated).</summary>
    public int DeclaredLength { get; init; }
    public byte[] Data { get; init; } = [];
    /// <summary>Recorded checksum byte, or null when the record carries none.</summary>
    public byte? Checksum { get; init; }
    public BusDirection Direction { get; init; }
    public uint? Baudrate { get; init; }
    /// <summary>Break and delimiter lengths in nanoseconds as recorded (LIN_MESSAGE2 only).</summary>
    public long? BreakLengthNs { get; init; }
    public long? SyncDelimiterNs { get; init; }
    /// <summary>Free-text evidence for error / wake-up / sleep events (recorded fields only).</summary>
    public string Detail { get; init; } = "";
}

[Flags]
public enum FlexRayFlags
{
    None = 0,
    NullFrame = 1,
    Sync = 2,
    Startup = 4,
    PayloadPreamble = 8,
    Error = 16
}

public enum FlexRayChannel { Unknown, A, B, AB }

/// <summary>One FlexRay frame as recorded. Immutable.</summary>
public sealed record FlexRayFrame : IBusFrame
{
    public BusKind Kind => BusKind.FlexRay;
    public int Channel { get; init; }
    public long TimestampNanoseconds { get; init; }
    public uint SourceObjectType { get; init; }
    public int Slot { get; init; }
    /// <summary>Cycle counter 0..63, or -1 when the source object carries no cycle.</summary>
    public int Cycle { get; init; } = -1;
    public FlexRayChannel FlexRayChannel { get; init; }
    public byte[] Payload { get; init; } = [];
    /// <summary>Payload length in bytes as declared by the record.</summary>
    public int DeclaredPayloadBytes { get; init; }
    public int? HeaderCrc { get; init; }
    /// <summary>Header CRC of channel B when the record carries both.</summary>
    public int? HeaderCrcB { get; init; }
    public uint? FrameCrc { get; init; }
    /// <summary>True dynamic segment, false static, null when the record does not say.</summary>
    public bool? IsDynamic { get; init; }
    public FlexRayFlags Flags { get; init; }
    public BusDirection Direction { get; init; }
    public string Detail { get; init; } = "";
    public bool IsNull => (Flags & FlexRayFlags.NullFrame) != 0;
    public bool IsError => (Flags & FlexRayFlags.Error) != 0;
}

/// <summary>One CAN XL frame. Immutable; payload up to 2048 bytes. Imported from Linux SocketCAN sources
/// (candump -L text, pcap/pcapng LINKTYPE_CAN_SOCKETCAN). BLF/ASC/MF4/TRC CAN XL records are counted as
/// unsupported because their layouts could not be verified from a primary source.</summary>
public sealed record CanXlFrame : IBusFrame
{
    public const int MaxPayloadBytes = 2048;
    public const int MinPayloadBytes = 1;
    /// <summary>True for a payload length a CAN XL frame can carry (1..2048, linux/can.h CANXL_MIN_DLEN/MAX_DLEN).</summary>
    public static bool IsValidPayloadLength(int length) => length is >= MinPayloadBytes and <= MaxPayloadBytes;
    private readonly byte[] _payload = [];
    private readonly uint _priorityId;

    public BusKind Kind => BusKind.CanXl;
    public int Channel { get; init; }
    public long TimestampNanoseconds { get; init; }
    public uint SourceObjectType { get; init; }
    /// <summary>11-bit priority identifier.</summary>
    public uint PriorityId
    {
        get => _priorityId;
        init => _priorityId = value <= 0x7FF
            ? value : throw new ArgumentOutOfRangeException(nameof(PriorityId), "CAN XL priority ID is 11 bits.");
    }
    public byte Vcid { get; init; }
    public byte Sdt { get; init; }
    /// <summary>Simple extended content bit.</summary>
    public bool Sec { get; init; }
    /// <summary>Remote request substitution bit (SocketCAN CANXL_RRS).</summary>
    public bool Rrs { get; init; }
    /// <summary>Addressing field (32 bit).</summary>
    public uint AcceptanceField { get; init; }
    public byte[] Payload
    {
        get => _payload;
        init => _payload = value.Length <= MaxPayloadBytes
            ? value : throw new ArgumentOutOfRangeException(nameof(Payload), "CAN XL payload is at most 2048 bytes.");
    }
    public BusDirection Direction { get; init; }
}

/// <summary>SDU type names. Only values on which two independent public sources agree are named: 0x01 (content-based
/// addressing) and 0x03 (CAN CC / CAN FD tunneling) per the Wireshark socketcan dissector update citing CiA 611-1
/// and the AUTOSAR CAN XL driver specification. Everything else returns null: the raw value is shown, never guessed.</summary>
public static class CanXlSdt
{
    public static string? Name(byte sdt) => sdt switch
    {
        0x01 => "Content-based addressing",
        0x03 => "CAN CC / CAN FD tunneling",
        _ => null
    };
}

/// <summary>Side collection filled by the importers next to the CAN frame stream. Single writer.</summary>
public sealed class BusFrameCollection
{
    /// <summary>Upper bound per bus kind so a hostile file cannot exhaust memory.</summary>
    public const int MaxFramesPerKind = 4_000_000;
    private readonly List<LinFrame> _lin = [];
    private readonly List<FlexRayFrame> _flexRay = [];
    private readonly List<CanXlFrame> _canXl = [];

    public IReadOnlyList<LinFrame> Lin => _lin;
    public IReadOnlyList<FlexRayFrame> FlexRay => _flexRay;
    public IReadOnlyList<CanXlFrame> CanXl => _canXl;
    /// <summary>Records dropped because a per-kind limit was reached.</summary>
    public long Dropped { get; private set; }
    public int Total => _lin.Count + _flexRay.Count + _canXl.Count;

    public void Add(IBusFrame frame)
    {
        switch (frame)
        {
            case LinFrame lin when _lin.Count < MaxFramesPerKind: _lin.Add(lin); break;
            case FlexRayFrame fr when _flexRay.Count < MaxFramesPerKind: _flexRay.Add(fr); break;
            case CanXlFrame xl when _canXl.Count < MaxFramesPerKind: _canXl.Add(xl); break;
            default: Dropped++; break;
        }
    }

    /// <summary>One-line status such as "1,204 LIN frames, 300 FlexRay frames, 12 CAN XL frames imported".</summary>
    public string Summary()
    {
        var parts = new List<string>();
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        if (_lin.Count > 0) parts.Add(string.Create(culture, $"{_lin.Count:N0} LIN frames"));
        if (_flexRay.Count > 0) parts.Add(string.Create(culture, $"{_flexRay.Count:N0} FlexRay frames"));
        if (_canXl.Count > 0) parts.Add(string.Create(culture, $"{_canXl.Count:N0} CAN XL frames"));
        return parts.Count == 0 ? "" : string.Join(", ", parts) + " imported" +
            (Dropped > 0 ? string.Create(culture, $" ({Dropped:N0} dropped at the per-kind limit)") : "");
    }
}
