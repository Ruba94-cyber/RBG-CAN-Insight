namespace RbgCanInsight.Diagnostics.Calibration;

public enum CalibrationProtocol { Xcp, Ccp }

/// <summary>Master (CRO / XCP command) and slave (CRM+DTO / XCP RES+DAQ) identifiers on one channel.</summary>
public sealed record CalibrationEndpoint(
    CalibrationProtocol Protocol,
    int Channel,
    uint MasterId,
    bool MasterExtended,
    uint SlaveId,
    bool SlaveExtended);

/// <summary>Auto-detected endpoint pair; <see cref="Evidence"/> is the number of observed
/// CONNECT/answer pairs only, never a confidence claim.</summary>
public sealed record CalibrationEndpointCandidate(CalibrationEndpoint Endpoint, int Evidence);

public sealed record CalibrationField(string Name, string Value);

public enum CalibrationResult { Positive, Error, Timeout }

public sealed record CalibrationTransaction(
    CalibrationProtocol Protocol,
    int CommandFrameIndex,
    long CommandTimestampNanoseconds,
    int? ResponseFrameIndex,
    long? ResponseTimestampNanoseconds,
    byte CommandCode,
    string CommandName,
    IReadOnlyList<CalibrationField> RequestFields,
    IReadOnlyList<CalibrationField> ResponseFields,
    CalibrationResult Result,
    byte? ErrorCode,
    string? ErrorName,
    double? LatencyMilliseconds);

/// <summary>A DAQ/DTO packet kept raw (XCP PID 0x00..0xFB, CCP PID 0x00..0xFD).</summary>
public sealed record CalibrationDaqSample(int FrameIndex, long TimestampNanoseconds, byte Pid, byte[] Data);

/// <summary>XCP EV / SERV packet or CCP event message.</summary>
public sealed record CalibrationEvent(int FrameIndex, long TimestampNanoseconds, string Kind, byte Code);

public sealed record CalibrationSummary(
    int TransactionCount,
    IReadOnlyDictionary<string, int> CommandCounts,
    IReadOnlyDictionary<string, int> ErrorCounts,
    IReadOnlyDictionary<byte, int> DaqPacketsPerPid,
    int Unanswered,
    int UnmatchedResponses,
    int EventCount,
    int StimPackets);

public sealed record CalibrationAnalysis(
    CalibrationEndpoint Endpoint,
    IReadOnlyList<CalibrationTransaction> Transactions,
    IReadOnlyList<CalibrationDaqSample> DaqSamples,
    IReadOnlyList<CalibrationEvent> Events,
    CalibrationSummary Summary,
    bool? BigEndian);
