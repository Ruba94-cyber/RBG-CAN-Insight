namespace RbgCanInsight.Analysis.E2E;

public enum E2eProfile { P01, P02, P05 }

/// <summary>Profile 1 data-ID inclusion in the CRC (16-bit data ID).</summary>
public enum E2eP1DataIdMode
{
    /// <summary>Low byte then high byte are both fed to the CRC.</summary>
    Both,
    /// <summary>Low byte on even counter values, high byte on odd ones.</summary>
    Alt,
    /// <summary>Only the low byte.</summary>
    Low,
    /// <summary>Only the high byte.</summary>
    High
}

/// <summary>Per-frame check outcome, modelled on AUTOSAR E2E_PCheckStatusType.</summary>
public enum E2eStatus
{
    Ok,
    WrongCrc,
    WrongSequence,
    Repeated,
    OkSomeLost,
    /// <summary>Frame is shorter than the configured payload length (offline-only state).</summary>
    WrongLength,
    /// <summary>P1 counter value 15 is invalid (offline-only state).</summary>
    InvalidCounter
}

/// <summary>A recorded frame as seen by the E2E checker (independent of the trace model).</summary>
public readonly record struct E2eFrame(int Channel, uint Id, bool Extended, double TimestampSeconds, byte[] Data);

/// <summary>User-supplied E2E configuration for one (channel, id, format) stream.</summary>
public sealed record E2eConfig
{
    public int Channel { get; init; }
    public uint Id { get; init; }
    public bool Extended { get; init; }
    public E2eProfile Profile { get; init; }
    public ushort DataId { get; init; }
    public E2eP1DataIdMode P1DataIdMode { get; init; } = E2eP1DataIdMode.Both;
    /// <summary>Profile 2: 16 data-ID-list bytes indexed by counter. Null = DataId low byte for every counter.</summary>
    public byte[]? P2DataIdList { get; init; }
    /// <summary>Bit offset of the CRC (multiple of 8).</summary>
    public int CrcBitOffset { get; init; }
    /// <summary>Bit offset of the counter's lowest bit (P1/P2: 4-bit counter at bit 0 or 4 of a byte; P5: 8-bit, byte aligned).</summary>
    public int CounterBitOffset { get; init; }
    public int PayloadLength { get; init; } = 8;
    /// <summary>Maximum accepted counter step; step 1 is Ok, 1 &lt; step &lt;= max is OkSomeLost.</summary>
    public int MaxDeltaCounter { get; init; } = 1;
    /// <summary>Expected cycle time. Null disables timeout detection.</summary>
    public double? CycleTimeSeconds { get; init; }
    /// <summary>Gap larger than cycle * factor counts as a timeout (AUTOSAR NONEWDATA equivalent).</summary>
    public double TimeoutFactor { get; init; } = 2.0;

    /// <summary>Default layout: CRC byte 0, counter low nibble of byte 1.</summary>
    public static E2eConfig P01(int channel, uint id, bool extended, ushort dataId, int length = 8) => new()
    {
        Channel = channel, Id = id, Extended = extended, Profile = E2eProfile.P01, DataId = dataId,
        CrcBitOffset = 0, CounterBitOffset = 8, PayloadLength = length
    };

    /// <summary>Default layout: CRC byte 0, counter low nibble of byte 1.</summary>
    public static E2eConfig P02(int channel, uint id, bool extended, byte[] dataIdList, int length = 8) => new()
    {
        Channel = channel, Id = id, Extended = extended, Profile = E2eProfile.P02, P2DataIdList = dataIdList,
        CrcBitOffset = 0, CounterBitOffset = 8, PayloadLength = length
    };

    /// <summary>Default layout: CRC bytes 0-1 (little endian), counter byte 2.</summary>
    public static E2eConfig P05(int channel, uint id, bool extended, ushort dataId, int length = 8) => new()
    {
        Channel = channel, Id = id, Extended = extended, Profile = E2eProfile.P05, DataId = dataId,
        CrcBitOffset = 0, CounterBitOffset = 16, PayloadLength = length
    };
}

/// <param name="TimeoutBefore">True when the gap to the previous frame of this stream exceeded the timeout.</param>
public sealed record E2eFrameResult(
    int FrameIndex, double TimestampSeconds, E2eStatus Status, int LostFrames,
    int Counter, uint ReceivedCrc, uint ExpectedCrc, bool TimeoutBefore, double? GapSeconds);

/// <param name="LongestOkRun">Longest run of consecutive frames with status Ok (any other status ends a run).</param>
public sealed record E2eSummary(
    E2eConfig Config, int FrameCount,
    int Ok, int WrongCrc, int WrongSequence, int Repeated, int OkSomeLost, int OtherErrors,
    int TotalLostFrames, int TimeoutCount,
    int? FirstFailingFrameIndex, double? FirstFailingTimestamp,
    int LongestOkRun,
    IReadOnlyList<E2eFrameResult> Results);

public sealed record E2eReport(IReadOnlyList<E2eSummary> Summaries, string? ConfigError);
