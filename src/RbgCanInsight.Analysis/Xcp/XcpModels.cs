namespace RbgCanInsight.Analysis.Xcp;

public enum XcpProtocol { Xcp, Ccp }

public enum XcpOutcome { Positive, Negative, Unanswered }

/// <summary>
/// User-supplied (or auto-detected) CAN identifier pair of one XCP/CCP slave. XCP and CCP have
/// no fixed identifiers, so the pair must come from the user or from <see cref="XcpDetector"/>.
/// </summary>
public sealed record XcpEndpoint(
    XcpProtocol Protocol,
    uint CommandId,
    uint ResponseId,
    bool CommandExtended = false,
    bool ResponseExtended = false,
    int? Channel = null,
    IReadOnlyList<uint>? DaqIds = null,
    bool DaqExtended = false);

/// <summary>Decoder options. <paramref name="A2l"/> (optional) lets SET_MTA/UPLOAD/DOWNLOAD/WRITE_DAQ addresses resolve to variable names and DAQ lists decode.</summary>
public sealed record XcpOptions(long ResponseTimeoutNanoseconds = 1_000_000_000L, A2l.A2lDatabase? A2l = null);

/// <summary>One command (CMD/CRO) paired with its response (RES/ERR/CRM) or marked unanswered.</summary>
public sealed record XcpTransaction(
    XcpProtocol Protocol,
    int Channel,
    long TimestampNanoseconds,
    int CommandCode,
    string CommandName,
    byte[] Request,
    string RequestDecoded,
    XcpOutcome Outcome,
    byte[]? Response,
    string ResponseDecoded,
    int? ErrorCode,
    string? ErrorName,
    int? Counter,
    double? LatencyMilliseconds,
    IReadOnlyList<int> FrameIndices,
    IReadOnlyList<string> Notes);

/// <summary>A data-acquisition (or stimulation) packet: first byte is the PID, the rest is not interpreted.</summary>
public sealed record XcpDaqPacket(
    XcpProtocol Protocol, int Channel, long TimestampNanoseconds, uint CanId, int Pid, byte[] Data, int FrameIndex, bool IsStimulation);

/// <summary>EV / SERV / CCP event message, or a response that matches no pending command.</summary>
public sealed record XcpEvent(
    XcpProtocol Protocol, int Channel, long TimestampNanoseconds, string Kind, int? Code, string Name, byte[] Data, string Decoded, int FrameIndex);

public sealed record XcpAnalysis(
    XcpEndpoint Endpoint,
    IReadOnlyList<XcpTransaction> Transactions,
    IReadOnlyList<XcpDaqPacket> DaqPackets,
    IReadOnlyList<XcpEvent> Events,
    IReadOnlyDictionary<string, int> ErrorCounts,
    int UnansweredCount,
    IReadOnlyList<XcpDaqDecodedPacket>? DaqDecoded = null,
    IReadOnlyList<string>? A2lNotes = null);

/// <summary>One decoded ODT entry of a DAQ packet. <see cref="Name"/> is the A2L symbol, or the raw address when none matches.</summary>
public sealed record XcpDaqValue(string Name, ulong Address, int Extension, int SizeBytes, string RawHex, string Text, double? Numeric, bool Resolved, string? Note);

/// <summary>A DAQ packet decoded with the DAQ configuration observed in the trace and the A2L symbols at the configured addresses.</summary>
public sealed record XcpDaqDecodedPacket(
    long TimestampNanoseconds, int FrameIndex, int Channel, int DaqList, int Odt, int? EventChannel, string? EventName,
    ulong? DeviceTimestamp, IReadOnlyList<XcpDaqValue> Values, string? Note);

/// <summary>An identifier pair that behaved like a CONNECT handshake, with the evidence behind the guess.</summary>
public sealed record XcpPairCandidate(
    XcpProtocol Protocol,
    int Channel,
    uint CommandId,
    bool CommandExtended,
    uint ResponseId,
    bool ResponseExtended,
    double Confidence,
    int Handshakes,
    IReadOnlyList<int> EvidenceFrameIndices,
    IReadOnlyList<string> Reasons)
{
    public XcpEndpoint ToEndpoint() => new(Protocol, CommandId, ResponseId, CommandExtended, ResponseExtended, Channel);
}
