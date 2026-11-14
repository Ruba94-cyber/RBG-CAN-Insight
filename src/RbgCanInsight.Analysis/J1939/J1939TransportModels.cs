namespace RbgCanInsight.Analysis;

public enum J1939TransportStatus { Complete, Incomplete, Aborted, TimedOut }

public enum J1939TransportKind { Bam, RtsCts }

/// <summary>One accepted TP.DT packet (the 7 data bytes after the sequence number).</summary>
public sealed record J1939TransportPacket(
    int SequenceNumber, int FrameIndex, long TimestampNanoseconds, byte[] Data);

/// <summary>
/// Result of one J1939 transport-protocol session. <see cref="Payload"/> is only non-null for
/// <see cref="J1939TransportStatus.Complete"/>; for other statuses only the genuinely received
/// packets are exposed in <see cref="Packets"/> (no byte is ever invented).
/// </summary>
public sealed record J1939TransportMessage(
    int Channel,
    byte SourceAddress,
    byte DestinationAddress,
    J1939TransportKind Kind,
    uint Pgn,
    int DeclaredSize,
    int DeclaredPacketCount,
    J1939TransportStatus Status,
    byte[]? Payload,
    IReadOnlyList<J1939TransportPacket> Packets,
    IReadOnlyList<int> MissingSequenceNumbers,
    IReadOnlyList<int> DuplicateSequenceNumbers,
    bool OutOfOrder,
    byte? AbortReason,
    IReadOnlyList<int> FrameIndices,
    IReadOnlyList<long> FrameSequences,
    long StartTimestampNanoseconds,
    long EndTimestampNanoseconds,
    IReadOnlyList<string> Notes)
{
    public bool IsComplete => Status == J1939TransportStatus.Complete;
}
