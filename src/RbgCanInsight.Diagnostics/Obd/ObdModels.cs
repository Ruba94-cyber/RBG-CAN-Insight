namespace RbgCanInsight.Diagnostics.Obd;

public enum ObdOutcome
{
    /// <summary>At least one responder answered positively.</summary>
    Positive,
    /// <summary>Every final answer was a negative response (0x7F).</summary>
    Negative,
    /// <summary>No answer within the pairing window (or only "response pending").</summary>
    NoResponse,
    /// <summary>A response was seen without a matching request in the trace.</summary>
    Orphan
}

/// <summary>Request as observed on the bus (ISO 15765-4 / SAE J1979).</summary>
public sealed record ObdRequest(
    uint Identifier,
    bool IsExtended,
    bool IsFunctional,
    byte Mode,
    IReadOnlyList<byte> Pids,
    byte? FreezeFrameNumber,
    byte[] Raw,
    IReadOnlyList<int> FrameIndices,
    long TimestampNanoseconds,
    long EndTimestampNanoseconds);

public sealed record ObdMonitorStatus(bool MilOn, int DtcCount, bool CompressionIgnition);

/// <summary>One decoded PID record. Unknown PIDs keep <see cref="IsKnown"/> false and only raw bytes.</summary>
public sealed record ObdValue(
    byte Pid,
    string Name,
    bool IsKnown,
    double? NumericValue,
    string Unit,
    string Text,
    byte[] Raw,
    string Issue = "",
    IReadOnlyList<int>? SupportedPids = null,
    ObdMonitorStatus? Monitor = null,
    ObdReadiness? Readiness = null,
    IReadOnlyList<ObdNamedCounter>? Counters = null);

public sealed record ObdDtc(string Code, byte[] Raw);

public sealed record ObdResponse(
    uint ResponderId,
    bool IsExtended,
    byte ResponderAddress,
    byte Mode,
    bool IsNegative,
    byte? NegativeResponseCode,
    string NegativeResponseName,
    bool IsPending,
    byte? FreezeFrameNumber,
    IReadOnlyList<ObdValue> Values,
    IReadOnlyList<ObdDtc> Dtcs,
    string? Vin,
    IReadOnlyList<string> CalibrationIds,
    byte[] Raw,
    IReadOnlyList<int> FrameIndices,
    long TimestampNanoseconds,
    long EndTimestampNanoseconds,
    double? LatencyMilliseconds,
    string Issue);

public sealed record ObdTransaction(
    int Sequence,
    byte Mode,
    byte? Pid,
    ObdRequest? Request,
    IReadOnlyList<ObdResponse> Responses,
    ObdOutcome Outcome,
    IReadOnlyList<int> FrameIndices,
    long StartTimestampNanoseconds,
    long EndTimestampNanoseconds);

/// <summary>One readiness monitor from mode 01 PID 01 (SAE J1979).</summary>
public sealed record ObdReadinessMonitor(string Name, bool Supported, bool Complete)
{
    public string State => !Supported ? "not supported" : Complete ? "complete" : "not complete";
}

/// <summary>Decoded readiness-monitor bits of mode 01 PID 01. In byte D (and B.4-6) a set bit means
/// "test not complete"; in byte C (and B.0-2) a set bit means "monitor supported".</summary>
public sealed record ObdReadiness(
    bool CompressionIgnition,
    IReadOnlyList<ObdReadinessMonitor> Monitors)
{
    public IEnumerable<ObdReadinessMonitor> Supported => Monitors.Where(m => m.Supported);
    public IEnumerable<ObdReadinessMonitor> Incomplete => Monitors.Where(m => m.Supported && !m.Complete);
}

/// <summary>A named counter or 4-byte value from a mode 09 response.</summary>
public sealed record ObdNamedCounter(string Name, uint Value, string Text);
