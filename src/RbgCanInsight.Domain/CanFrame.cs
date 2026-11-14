using System.Buffers.Binary;

namespace RbgCanInsight.Domain;

[Flags]
public enum CanFrameFlags : byte
{
    None = 0,
    Extended = 1,
    Remote = 2,
    Error = 4,
    FlexibleDataRate = 8,
    BitRateSwitch = 16,
    /// <summary>Frame was transmitted by the logging node (Tx). Absent = received (Rx) or direction unknown.
    /// Uses a spare bit of the existing flag byte: no size change and no extra per-frame work.</summary>
    Transmit = 32
}

/// <summary>
/// Immutable CAN / CAN-FD frame with inline payload storage (no heap allocation per frame),
/// suitable for holding multi-million-frame traces in memory.
/// </summary>
public readonly record struct CanFrame
{
    private readonly ulong _payload0;
    private readonly ulong _payload1;
    private readonly ulong _payload2;
    private readonly ulong _payload3;
    private readonly ulong _payload4;
    private readonly ulong _payload5;
    private readonly ulong _payload6;
    private readonly ulong _payload7;

    public CanFrame(
        int channel,
        uint identifier,
        ReadOnlySpan<byte> payload,
        long timestampNanoseconds,
        CanFrameFlags flags = CanFrameFlags.None,
        long sequence = 0,
        byte? declaredDataLength = null)
    {
        if (channel < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(channel));
        }

        var maxIdentifier = flags.HasFlag(CanFrameFlags.Extended) ? 0x1FFF_FFFFu : 0x7FFu;
        if (identifier > maxIdentifier)
        {
            throw new ArgumentOutOfRangeException(nameof(identifier));
        }

        if (payload.Length > 64)
        {
            throw new ArgumentOutOfRangeException(nameof(payload), "CAN FD payloads cannot exceed 64 bytes.");
        }

        // Remote frames have no payload even when the wire DLC requests N bytes.
        // Preserve that length without inventing N zero-valued observed bytes.
        if (flags.HasFlag(CanFrameFlags.Remote) &&
            (payload.Length != 0 || (declaredDataLength is { } requested && requested > 8)))
        {
            throw new ArgumentException("Remote CAN frames must have an empty payload and a DLC of 0..8.");
        }
        if (!flags.HasFlag(CanFrameFlags.Remote) &&
            declaredDataLength is { } explicitLength && explicitLength != payload.Length)
        {
            throw new ArgumentException("A data frame's declared byte length must equal its actual payload.");
        }

        Span<byte> storage = stackalloc byte[64];
        storage.Clear(); // Stable equality: unused payload bytes must never contain stack garbage.
        payload.CopyTo(storage);

        Channel = channel;
        Identifier = identifier;
        TimestampNanoseconds = timestampNanoseconds;
        Flags = flags;
        DataLength = (byte)payload.Length;
        DeclaredDataLength = declaredDataLength ?? DataLength;
        Sequence = sequence;
        _payload0 = BinaryPrimitives.ReadUInt64LittleEndian(storage);
        _payload1 = BinaryPrimitives.ReadUInt64LittleEndian(storage[8..]);
        _payload2 = BinaryPrimitives.ReadUInt64LittleEndian(storage[16..]);
        _payload3 = BinaryPrimitives.ReadUInt64LittleEndian(storage[24..]);
        _payload4 = BinaryPrimitives.ReadUInt64LittleEndian(storage[32..]);
        _payload5 = BinaryPrimitives.ReadUInt64LittleEndian(storage[40..]);
        _payload6 = BinaryPrimitives.ReadUInt64LittleEndian(storage[48..]);
        _payload7 = BinaryPrimitives.ReadUInt64LittleEndian(storage[56..]);
    }

    public int Channel { get; }
    public uint Identifier { get; }
    public long TimestampNanoseconds { get; }
    public CanFrameFlags Flags { get; }
    public byte DataLength { get; }
    /// <summary>Decoded on-wire requested length (bytes), which can differ from the
    /// actual zero-byte payload only for a remote/RTR frame. Not an FD DLC nibble.</summary>
    public byte DeclaredDataLength { get; }
    public long Sequence { get; init; }
    public bool IsExtended => Flags.HasFlag(CanFrameFlags.Extended);
    public bool IsError => Flags.HasFlag(CanFrameFlags.Error);
    /// <summary>True for a frame the log marks as transmitted (Tx). Rx and "unknown" both read false.</summary>
    public bool IsTransmit => Flags.HasFlag(CanFrameFlags.Transmit);

    public void CopyPayloadTo(Span<byte> destination)
    {
        if (destination.Length < DataLength)
        {
            throw new ArgumentException("Destination is too small.", nameof(destination));
        }

        Span<byte> storage = stackalloc byte[64];
        BinaryPrimitives.WriteUInt64LittleEndian(storage, _payload0);
        BinaryPrimitives.WriteUInt64LittleEndian(storage[8..], _payload1);
        BinaryPrimitives.WriteUInt64LittleEndian(storage[16..], _payload2);
        BinaryPrimitives.WriteUInt64LittleEndian(storage[24..], _payload3);
        BinaryPrimitives.WriteUInt64LittleEndian(storage[32..], _payload4);
        BinaryPrimitives.WriteUInt64LittleEndian(storage[40..], _payload5);
        BinaryPrimitives.WriteUInt64LittleEndian(storage[48..], _payload6);
        BinaryPrimitives.WriteUInt64LittleEndian(storage[56..], _payload7);
        storage[..DataLength].CopyTo(destination);
    }

    public byte[] GetPayload()
    {
        var result = new byte[DataLength];
        CopyPayloadTo(result);
        return result;
    }

    public byte GetByte(int index)
    {
        if ((uint)index >= DataLength)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        var wordIndex = index / 8;
        var shift = (index % 8) * 8;
        var word = wordIndex switch
        {
            0 => _payload0,
            1 => _payload1,
            2 => _payload2,
            3 => _payload3,
            4 => _payload4,
            5 => _payload5,
            6 => _payload6,
            7 => _payload7,
            _ => throw new ArgumentOutOfRangeException(nameof(index))
        };
        return (byte)(word >> shift);
    }
}
