namespace RbgCanInsight.Diagnostics;

public enum DiagnosticDirection
{
    TesterToEcu,
    EcuToTester,
    Unknown
}

public enum DiagnosticEvidence
{
    Confirmed,
    Inferred,
    Unknown
}

public enum DiagnosticOutcome
{
    Positive,
    Negative,
    NoResponse,
    SuppressedResponse,
    Malformed,
    OrphanResponse
}

public sealed record IsoTpMessage(
    int Channel,
    uint Identifier,
    bool IsExtended,
    long StartTimestampNanoseconds,
    long EndTimestampNanoseconds,
    IReadOnlyList<int> FrameIndices,
    byte[] Payload,
    bool IsComplete,
    string TransportIssue);

public sealed record DiagnosticEndpoint(
    string Tester,
    string Ecu,
    DiagnosticDirection Direction,
    DiagnosticEvidence Evidence,
    string Addressing);

public sealed record DecodedDiagnosticValue(
    string Path,
    string Name,
    string Raw,
    string Physical,
    string Unit,
    string Meaning,
    int? Did = null,
    double? NumericValue = null,
    // Exact bit interval from the UDS service-ID byte in the reassembled
    // response. No CAN frame claim until IsoTpSourceByteIndex verifies all
    // original source bytes, sequence and transport identity.
    int? ResponseBitOffset = null,
    int? ResponseBitLength = null,
    IReadOnlyList<DiagnosticSourceByteSpan>? SourceSpans = null,
    // Which ODX data-object-property / COMPU-METHOD produced the physical value (empty when none did).
    string Provenance = "")
{
    public bool HasVerifiedSource => ResponseBitOffset is >= 0 &&
        ResponseBitLength is > 0 && SourceSpans is { Count: > 0 };

    public string SourceEvidenceText => !HasVerifiedSource
        ? "CAN source byte mapping unavailable or not verified."
        : $"UDS response bit {ResponseBitOffset}, length {ResponseBitLength} · " +
          string.Join("; ", SourceSpans!.Select(span => span.DisplayText));
}

public sealed record DecodedDtc(
    int Code,
    string CodeText,
    string Name,
    byte? Status,
    string StatusText,
    string ExtraData);

public sealed record DecodedUdsOperation(
    byte ServiceId,
    string ServiceName,
    string ObjectText,
    string RequestSummary,
    string ResponseSummary,
    IReadOnlyList<DecodedDiagnosticValue> Values,
    IReadOnlyList<DecodedDtc> Dtcs,
    byte? NegativeResponseCode,
    string NegativeResponseText,
    // True only when all DID records and declared subfields of a positive
    // 0x62 response could be segmented. A known first DID does NOT prove
    // that later unknown/truncated DIDs were decoded completely.
    bool DidPayloadFullyDecoded = true,
    // Snapshot / extended-data records of a 0x19 response, decoded with catalog definitions.
    IReadOnlyList<DecodedDtcRecord>? DtcRecordList = null)
{
    public IReadOnlyList<DecodedDtcRecord> DtcRecords => DtcRecordList ?? [];
}

public sealed record DiagnosticTransaction(
    int Sequence,
    IsoTpMessage? Request,
    IReadOnlyList<IsoTpMessage> Responses,
    DiagnosticEndpoint Endpoint,
    DiagnosticOutcome Outcome,
    DecodedUdsOperation Decoded,
    long StartTimestampNanoseconds,
    long EndTimestampNanoseconds,
    double? ResponseTimeMilliseconds,
    IReadOnlyList<int> FrameIndices,
    string Quality);
