using System.Globalization;
using RbgCanInsight.DiagData;

namespace RbgCanInsight.Diagnostics;

/// <summary>Schema-aware UDS presentation used by the transaction workspace. CDD/ODX names and
/// physical DID metadata take precedence; ISO 14229 names remain a transparent fallback.</summary>
public static class UdsServiceDecoder
{
    private static readonly IReadOnlyDictionary<byte, string> Services = new Dictionary<byte, string>
    {
        [0x10] = "DiagnosticSessionControl", [0x11] = "EcuReset",
        [0x14] = "ClearDiagnosticInformation", [0x19] = "ReadDTCInformation",
        [0x22] = "ReadDataByIdentifier", [0x23] = "ReadMemoryByAddress",
        [0x24] = "ReadScalingDataByIdentifier", [0x27] = "SecurityAccess",
        [0x28] = "CommunicationControl", [0x29] = "Authentication", [0x2A] = "ReadDataByPeriodicIdentifier",
        [0x2C] = "DynamicallyDefineDataIdentifier", [0x2E] = "WriteDataByIdentifier",
        [0x2F] = "InputOutputControlByIdentifier", [0x31] = "RoutineControl",
        [0x34] = "RequestDownload", [0x35] = "RequestUpload", [0x36] = "TransferData",
        [0x37] = "RequestTransferExit", [0x38] = "RequestFileTransfer",
        [0x3D] = "WriteMemoryByAddress", [0x3E] = "TesterPresent",
        [0x83] = "AccessTimingParameter", [0x84] = "SecuredDataTransmission",
        [0x85] = "ControlDTCSetting", [0x86] = "ResponseOnEvent", [0x87] = "LinkControl"
    };

    /// <summary>ISO 14229-1 negative response codes (2020 edition naming). Ranges that the standard
    /// reserves or leaves to the vehicle manufacturer are resolved by <see cref="NrcName"/>.</summary>
    private static readonly IReadOnlyDictionary<byte, string> NrcNames = new Dictionary<byte, string>
    {
        [0x10] = "generalReject", [0x11] = "serviceNotSupported",
        [0x12] = "subFunctionNotSupported", [0x13] = "incorrectMessageLengthOrInvalidFormat",
        [0x14] = "responseTooLong", [0x21] = "busyRepeatRequest", [0x22] = "conditionsNotCorrect",
        [0x24] = "requestSequenceError", [0x25] = "noResponseFromSubnetComponent",
        [0x26] = "failurePreventsExecutionOfRequestedAction", [0x31] = "requestOutOfRange",
        [0x33] = "securityAccessDenied", [0x34] = "authenticationRequired",
        [0x35] = "invalidKey", [0x36] = "exceedNumberOfAttempts",
        [0x37] = "requiredTimeDelayNotExpired", [0x38] = "secureDataTransmissionRequired",
        [0x39] = "secureDataTransmissionNotAllowed", [0x3A] = "secureDataVerificationFailed",
        [0x50] = "certificateVerificationFailedInvalidTimePeriod",
        [0x51] = "certificateVerificationFailedInvalidSignature",
        [0x52] = "certificateVerificationFailedInvalidChainOfTrust",
        [0x53] = "certificateVerificationFailedInvalidType",
        [0x54] = "certificateVerificationFailedInvalidFormat",
        [0x55] = "certificateVerificationFailedInvalidContent",
        [0x56] = "certificateVerificationFailedInvalidScope",
        [0x57] = "certificateVerificationFailedInvalidCertificateRevoked",
        [0x58] = "ownershipVerificationFailed", [0x59] = "challengeCalculationFailed",
        [0x5A] = "settingAccessRightsFailed", [0x5B] = "sessionKeyCreationDerivationFailed",
        [0x5C] = "configurationDataUsageFailed", [0x5D] = "deAuthenticationFailed",
        [0x70] = "uploadDownloadNotAccepted",
        [0x71] = "transferDataSuspended", [0x72] = "generalProgrammingFailure",
        [0x73] = "wrongBlockSequenceCounter", [0x78] = "responsePending",
        [0x7E] = "subFunctionNotSupportedInActiveSession",
        [0x7F] = "serviceNotSupportedInActiveSession", [0x81] = "rpmTooHigh", [0x82] = "rpmTooLow",
        [0x83] = "engineIsRunning", [0x84] = "engineIsNotRunning", [0x85] = "engineRunTimeTooLow",
        [0x86] = "temperatureTooHigh", [0x87] = "temperatureTooLow", [0x88] = "vehicleSpeedTooHigh",
        [0x89] = "vehicleSpeedTooLow", [0x8A] = "throttlePedalTooHigh", [0x8B] = "throttlePedalTooLow",
        [0x8C] = "transmissionRangeNotInNeutral", [0x8D] = "transmissionRangeNotInGear",
        [0x8F] = "brakeSwitchNotClosed", [0x90] = "shifterLeverNotInPark",
        [0x91] = "torqueConverterClutchLocked", [0x92] = "voltageTooHigh",
        [0x93] = "voltageTooLow", [0x94] = "resourceTemporarilyNotAvailable"
    };

    /// <summary>True when <paramref name="code"/> has a name in the ISO 14229-1 table.</summary>
    public static bool IsStandardNrc(byte code) => NrcNames.ContainsKey(code);

    /// <summary>Name of a negative response code. Reserved and manufacturer-specific ranges get a
    /// descriptive label instead of a bare hex value.</summary>
    public static string NrcName(byte code) =>
        NrcNames.TryGetValue(code, out var name) ? name : code switch
        {
            >= 0x95 and <= 0xEF => $"reservedForSpecificConditionsNotCorrect (0x{code:X2})",
            >= 0xF0 and <= 0xFE => $"vehicleManufacturerSpecificConditionsNotCorrect (0x{code:X2})",
            >= 0x38 and <= 0x4F => $"reservedByExtendedDataLinkSecurityDocument (0x{code:X2})",
            _ => $"NRC 0x{code:X2}"
        };

    private static readonly IReadOnlyDictionary<byte, string> DtcSubFunctions = new Dictionary<byte, string>
    {
        [0x01] = "reportNumberOfDTCByStatusMask", [0x02] = "reportDTCByStatusMask",
        [0x03] = "reportDTCSnapshotIdentification", [0x04] = "reportDTCSnapshotRecordByDTCNumber",
        [0x05] = "reportDTCStoredDataByRecordNumber", [0x06] = "reportDTCExtDataRecordByDTCNumber",
        [0x07] = "reportNumberOfDTCBySeverityMaskRecord", [0x08] = "reportDTCBySeverityMaskRecord",
        [0x09] = "reportSeverityInformationOfDTC", [0x0A] = "reportSupportedDTC",
        [0x0B] = "reportFirstTestFailedDTC", [0x0C] = "reportFirstConfirmedDTC",
        [0x0D] = "reportMostRecentTestFailedDTC", [0x0E] = "reportMostRecentConfirmedDTC",
        [0x0F] = "reportMirrorMemoryDTCByStatusMask", [0x10] = "reportMirrorMemoryDTCExtDataRecordByDTCNumber",
        [0x11] = "reportNumberOfMirrorMemoryDTCByStatusMask", [0x12] = "reportNumberOfEmissionsOBDDTCByStatusMask",
        [0x13] = "reportEmissionsOBDDTCByStatusMask", [0x14] = "reportDTCFaultDetectionCounter",
        [0x15] = "reportDTCWithPermanentStatus", [0x16] = "reportDTCExtDataRecordByRecordNumber",
        [0x17] = "reportUserDefMemoryDTCByStatusMask", [0x18] = "reportUserDefMemoryDTCSnapshotRecordByDTCNumber",
        [0x19] = "reportUserDefMemoryDTCExtDataRecordByDTCNumber", [0x1A] = "reportSupportedDTCExtDataRecord",
        [0x42] = "reportWWHOBDDTCByMaskRecord", [0x55] = "reportWWHOBDDTCWithPermanentStatus",
        [0x56] = "reportDTCInformationByDTCReadinessGroupIdentifier"
    };

    private static readonly IReadOnlyDictionary<int, string> StandardDids = new Dictionary<int, string>
    {
        [0xF186] = "ActiveDiagnosticSession", [0xF187] = "SparePartNumber",
        [0xF188] = "EcuSoftwareNumber", [0xF189] = "EcuSoftwareVersion",
        [0xF18A] = "SystemSupplierId", [0xF18C] = "EcuSerialNumber",
        [0xF190] = "VIN", [0xF191] = "EcuHardwareNumber", [0xF194] = "SupplierEcuSoftwareNumber",
        [0xF195] = "SupplierEcuSoftwareVersion", [0xF197] = "SystemName"
    };

    public static bool IsKnownRequest(byte serviceId) => Services.ContainsKey(serviceId);

    public static bool IsPositiveResponse(byte serviceId) =>
        serviceId >= 0x40 && Services.ContainsKey((byte)(serviceId - 0x40));

    /// <summary>Protocol-aware variant: with <paramref name="kwp"/> the KWP2000 service set is used.</summary>
    public static bool IsKnownRequest(byte serviceId, bool kwp) =>
        kwp ? KwpServiceDecoder.IsKnownRequest(serviceId) : IsKnownRequest(serviceId);

    public static bool IsPositiveResponse(byte serviceId, bool kwp) =>
        kwp ? KwpServiceDecoder.IsPositiveResponse(serviceId) : IsPositiveResponse(serviceId);

    public static DecodedUdsOperation Decode(
        IsoTpMessage? request,
        IReadOnlyList<IsoTpMessage> responses,
        IReadOnlyList<DidCatalog> catalogs,
        string? ecu = null)
    {
        var response = responses.LastOrDefault(item => item.Payload.Length > 0 && item.Payload[0] != 0x7F) ??
                       responses.LastOrDefault();
        var requestPayload = request?.Payload ?? [];
        var responsePayload = response?.Payload ?? [];
        var serviceId = requestPayload.Length > 0
            ? requestPayload[0]
            : responsePayload.Length > 0 && responsePayload[0] == 0x7F && responsePayload.Length > 1
                ? responsePayload[1]
                : responsePayload.Length > 0 && responsePayload[0] >= 0x40
                    ? (byte)(responsePayload[0] - 0x40)
                    : (byte)0;

        var kwp = KwpServiceDecoder.IsKwpCatalogSet(catalogs);
        var catalogService = ResolveService(catalogs, serviceId, requestPayload);
        var serviceName = !string.IsNullOrWhiteSpace(catalogService?.LongName)
            ? catalogService.LongName
            : !string.IsNullOrWhiteSpace(catalogService?.Name)
                ? catalogService.Name
                : (kwp ? KwpServiceDecoder.Services : Services)
                    .GetValueOrDefault(serviceId, $"Service 0x{serviceId:X2}");

        byte? nrc = null;
        var nrcText = string.Empty;
        foreach (var candidate in responses)
        {
            if (candidate.Payload is [0x7F, _, var code, ..] && code != 0x78)
            {
                nrc = code;
                nrcText = NrcName(code);
            }
        }

        var values = new List<DecodedDiagnosticValue>();
        var dtcs = new List<DecodedDtc>();
        var objectText = string.Empty;
        var didPayloadFullyDecoded = true;
        string? requestSummary = kwp && requestPayload.Length > 0
            ? KwpServiceDecoder.DescribeRequest(serviceId, requestPayload, catalogs, out objectText)
            : null;
        requestSummary ??= DescribeRequest(serviceId, requestPayload, catalogs, out objectText, ecu);
        string? responseSummary = kwp && responsePayload.Length > 0 && responsePayload[0] != 0x7F
            ? KwpServiceDecoder.DescribeResponse(serviceId, responsePayload)
            : null;
        responseSummary ??= DescribeResponse(serviceId, responsePayload, requestPayload, catalogs,
            values, dtcs, ref objectText, out didPayloadFullyDecoded, ecu);
        if (nrc is not null)
        {
            responseSummary = $"Negative response: {nrcText}";
        }

        IReadOnlyList<DecodedDtcRecord> dtcRecords = [];
        if (serviceId == 0x19 && nrc is null && responsePayload.Length > 2 && responsePayload[0] == 0x59 &&
            DtcRecordDecoder.IsRecordSubFunction((byte)(responsePayload[1] & 0x7F)))
        {
            dtcRecords = DtcRecordDecoder.Decode(responsePayload, requestPayload, catalogs, ecu);
            if (dtcRecords.Count > 0)
            {
                // Replace the raw hex tail of the DTC entries with the decoded record text.
                var recordText = string.Join(" | ", dtcRecords.Select(record => record.SummaryText));
                var subName = DtcSubFunctions.GetValueOrDefault((byte)(responsePayload[1] & 0x7F), "ReadDTCInformation");
                if ((responsePayload[1] & 0x7F) == 0x05)
                {
                    foreach (var record in dtcRecords)
                        dtcs.Add(new DecodedDtc(record.Code, $"0x{record.Code:X6}",
                            FindUniqueDtc(record.Code, catalogs, ecu)?.DisplayName ?? string.Empty,
                            record.Status, record.Status is { } st ? DtcStatus(st) : string.Empty, record.SummaryText));
                    responseSummary = $"{subName}: {dtcRecords.Count} record(s) · {recordText}";
                }
                else
                {
                    for (var i = 0; i < dtcs.Count; i++)
                        dtcs[i] = dtcs[i] with { ExtraData = recordText };
                    responseSummary = $"{subName}: 0x{dtcRecords[0].Code:X6} · {recordText}";
                }
            }
        }

        return new DecodedUdsOperation(
            serviceId,
            serviceName,
            objectText,
            requestSummary,
            responseSummary,
            values,
            dtcs,
            nrc,
            nrcText,
            didPayloadFullyDecoded,
            dtcRecords);
    }

    private static string DescribeRequest(
        byte sid,
        byte[] data,
        IReadOnlyList<DidCatalog> catalogs,
        out string objectText,
        string? ecu)
    {
        objectText = string.Empty;
        if (data.Length == 0) return "Missing request";
        switch (sid)
        {
            case 0x22:
            case 0x2E:
            case 0x2F:
            case 0x24:
                if (data.Length < 3) return "Truncated identifier";
                var dids = new List<string>();
                for (var offset = 1; offset + 1 < data.Length && (sid != 0x22 || dids.Count < 16); offset += 2)
                {
                    var did = (data[offset] << 8) | data[offset + 1];
                    dids.Add(DidText(did, catalogs, ecu));
                    if (sid != 0x22) break;
                }
                objectText = string.Join(", ", dids);
                return sid == 0x22 ? $"Read {objectText}" : $"{Services.GetValueOrDefault(sid)} {objectText}";

            case 0x19:
                var sub = data.Length > 1 ? (byte)(data[1] & 0x7F) : (byte)0;
                objectText = DtcSubFunctions.GetValueOrDefault(sub, $"sub 0x{sub:X2}");
                return data.Length > 2
                    ? $"{objectText} · mask/record {FormatHex(data.AsSpan(2))}"
                    : objectText;

            case 0x14:
                if (data.Length >= 4)
                {
                    var group = (data[1] << 16) | (data[2] << 8) | data[3];
                    objectText = $"DTC group 0x{group:X6}";
                    return $"Clear {objectText}";
                }
                return "ClearDiagnosticInformation";

            case 0x31:
                if (data.Length >= 4)
                {
                    var action = RoutineAction(data[1]);
                    var routineId = (data[2] << 8) | data[3];
                    objectText = RoutineText(routineId, catalogs, ecu);
                    return $"{action} {objectText}" + (data.Length > 4 ? $" · input {FormatHex(data.AsSpan(4))}" : string.Empty);
                }
                return "Truncated routine request";

            case 0x10:
                objectText = data.Length > 1 ? SessionText((byte)(data[1] & 0x7F)) : string.Empty;
                return string.IsNullOrEmpty(objectText) ? "Session control" : $"Enter {objectText}";

            case 0x27:
                if (data.Length > 1)
                {
                    var level = (byte)(data[1] & 0x7F);
                    objectText = $"level 0x{level:X2}";
                    return (level & 1) == 1 ? $"Request seed {objectText}" : $"Send key {objectText}";
                }
                return "Security access";

            case 0x29 or 0x83 or 0x84:
                return UdsExtendedServices.DescribeRequest(sid, data, out objectText);

            case 0x3E:
                return data.Length > 1 && (data[1] & 0x80) != 0 ? "Tester present · suppress response" : "Tester present";

            case 0x36:
                // Byte 1 is the full 8-bit blockSequenceCounter (wraps 0xFF -> 0x00), not a sub-function.
                if (data.Length < 2) return "Truncated TransferData";
                objectText = $"block 0x{data[1]:X2}";
                return $"Transfer {objectText} · {data.Length - 2} data byte(s)";

            case 0x34 or 0x35:
                if (data.Length < 3) return "Truncated transfer request";
                return $"dataFormat 0x{data[1]:X2} · " + DescribeAddressAndLength(data.AsSpan(2), out objectText);

            case 0x23 or 0x3D:
                if (data.Length < 2) return "Truncated memory request";
                return DescribeAddressAndLength(data.AsSpan(1), out objectText);

            case 0x11 or 0x28 or 0x2C or 0x85 or 0x86 or 0x87:
                if (data.Length > 1)
                {
                    objectText = $"sub 0x{data[1] & 0x7F:X2}";
                }
                return data.Length > 1 ? $"{objectText} · {FormatHex(data.AsSpan(2))}".TrimEnd(' ', '·') : Services.GetValueOrDefault(sid, "UDS request");

            default:
                // No sub-function is defined for this service: keep the parameter bytes raw.
                return data.Length > 1 ? FormatHex(data.AsSpan(1)) : Services.GetValueOrDefault(sid, "UDS request");
        }
    }

    /// <summary>addressAndLengthFormatIdentifier (ISO 14229-1): high nibble = number of
    /// memorySize bytes, low nibble = number of memoryAddress bytes; the address comes first.</summary>
    private static string DescribeAddressAndLength(ReadOnlySpan<byte> data, out string objectText)
    {
        objectText = string.Empty;
        var sizeBytes = data[0] >> 4;
        var addressBytes = data[0] & 0x0F;
        if (addressBytes is 0 or > 8 || sizeBytes is 0 or > 8 || data.Length < 1 + addressBytes + sizeBytes)
            return $"addressAndLengthFormat 0x{data[0]:X2} · {FormatHex(data[1..])}".TrimEnd(' ', '·');
        ulong address = 0;
        ulong size = 0;
        for (var index = 0; index < addressBytes; index++) address = (address << 8) | data[1 + index];
        for (var index = 0; index < sizeBytes; index++) size = (size << 8) | data[1 + addressBytes + index];
        objectText = $"address 0x{address.ToString("X" + (addressBytes * 2), CultureInfo.InvariantCulture)}";
        var rest = data[(1 + addressBytes + sizeBytes)..];
        return $"{objectText} · size {size} byte(s)" + (rest.Length > 0 ? $" · {FormatHex(rest)}" : string.Empty);
    }

    private static string DescribeResponse(
        byte sid,
        byte[] data,
        byte[] requestPayload,
        IReadOnlyList<DidCatalog> catalogs,
        List<DecodedDiagnosticValue> values,
        List<DecodedDtc> dtcs,
        ref string objectText,
        out bool didPayloadFullyDecoded,
        string? ecu)
    {
        didPayloadFullyDecoded = true;
        if (data.Length == 0) return "No response";
        if (data[0] == 0x7F) return data.Length > 2 ? NrcName(data[2]) : "Truncated negative response";

        switch (sid)
        {
            case 0x22:
                return DecodeDidResponse(data, requestPayload, catalogs, values,
                    ref objectText, out didPayloadFullyDecoded, ecu);
            case 0x19:
                return DecodeDtcResponse(data, catalogs, dtcs, ecu);
            case 0x31:
                if (data.Length >= 4)
                {
                    var routineId = (data[2] << 8) | data[3];
                    objectText = RoutineText(routineId, catalogs, ecu);
                    return $"{RoutineAction(data[1])} accepted" +
                           (data.Length > 4 ? $" · result {FormatHex(data.AsSpan(4))}" : string.Empty);
                }
                break;
            case 0x10:
                return data.Length > 1
                    ? $"Active {SessionText((byte)(data[1] & 0x7F))}" +
                      (UdsExtendedServices.TryReadSessionTiming(data, out var timing) ? $" · {timing}" : string.Empty)
                    : "Session accepted";
            case 0x29 or 0x83 or 0x84:
                return UdsExtendedServices.DescribeResponse(sid, data, requestPayload);
            case 0x27:
                return data.Length > 2 ? $"Security data {FormatHex(data.AsSpan(2))}" : "Security step accepted";
            case 0x14:
                return "Clear request accepted";
        }

        return data.Length > 1 ? FormatHex(data.AsSpan(1)) : "Positive response";
    }

    /// <summary>Decode one or several 0x62 DIDs using their declared payload widths.
    /// A response is not self-delimiting: without a declared width, a multi-DID
    /// record cannot be separated reliably and must not become numeric evidence.</summary>
    private static string DecodeDidResponse(
        byte[] data,
        byte[] requestPayload,
        IReadOnlyList<DidCatalog> catalogs,
        List<DecodedDiagnosticValue> values,
        ref string objectText,
        out bool didPayloadFullyDecoded,
        string? ecu)
    {
        didPayloadFullyDecoded = true;
        if (data.Length < 3)
        {
            didPayloadFullyDecoded = false;
            return "Truncated DID response";
        }
        // The original request may repeat a DID. Count occurrences rather
        // than using a set: duplicate responses are only acceptable when
        // exactly that many occurrences were actually requested.
        var requested = new Dictionary<int, int>();
        var observedDids = new Dictionary<int, int>();
        var requestedTotal = 0;
        var malformedRequest = requestPayload.Length > 0 &&
            (requestPayload[0] != 0x22 || requestPayload.Length < 3 ||
             (requestPayload.Length - 1) % 2 != 0);
        if (malformedRequest)
            didPayloadFullyDecoded = false;
        if (requestPayload.Length >= 3 && requestPayload[0] == 0x22 &&
            (requestPayload.Length - 1) % 2 == 0)
        {
            for (var i = 1; i + 1 < requestPayload.Length; i += 2)
            {
                var requestedDid = (requestPayload[i] << 8) | requestPayload[i + 1];
                requested.TryGetValue(requestedDid, out var occurrences);
                requested[requestedDid] = occurrences + 1;
                requestedTotal++;
            }
        }

        var offset = 1;
        var labels = new List<string>();
        var summaries = new List<string>();
        var ambiguous = string.Empty;
        while (offset + 1 < data.Length)
        {
            var did = (data[offset] << 8) | data[offset + 1];
            if (requested.Count > 0 && !requested.ContainsKey(did))
            {
                ambiguous = $"Unexpected DID 0x{did:X4} in positive response";
                break;
            }
            observedDids.TryGetValue(did, out var occurrences);
            if ((requested.TryGetValue(did, out var requestedOccurrences) &&
                    occurrences >= requestedOccurrences) ||
                (requestedTotal == 0 && occurrences > 0))
            {
                // When the request names F405 once, two otherwise plausible
                // F405 response records must not certify a complete DID
                // sequence. Keep earlier verified fields inspectable.
                ambiguous = $"Unexpected repeated DID 0x{did:X4} in positive response";
                break;
            }
            observedDids[did] = occurrences + 1;

            var definition = FindUniqueDid(did, catalogs, ecu);
            if (definition is null || definition.EngineeringIssues.Count > 0)
                didPayloadFullyDecoded = false;
            var available = data.Length - (offset + 2);
            var declaredBits = definition?.Fields.Count > 0
                ? definition.Fields.All(field => field.BitOffset >= 0 && field.BitLength > 0 &&
                    (long)field.BitOffset + field.BitLength <= int.MaxValue)
                    ? definition.Fields.Max(field => field.BitOffset + field.BitLength)
                    : (int?)null // unresolved ODX positions are not valid multi-DID boundaries
                : definition?.BitLength;
            // Multi-DID responses with unknown or variable-length field layouts cannot
            // be split by searching for arbitrary 0xFxxx patterns inside binary data.
            if (requestedTotal > 1 && declaredBits is not > 0)
            {
                ambiguous = $"Cannot segment DID 0x{did:X4}: no unambiguous CDD/ODX payload length";
                break;
            }
            var declaredBytes = declaredBits is > 0 ? ((long)declaredBits.Value + 7) / 8 : available;
            if (declaredBytes <= 0 || declaredBytes > available || declaredBytes > int.MaxValue)
            {
                ambiguous = $"Truncated or invalid DID 0x{did:X4} payload length";
                break;
            }

            if (declaredBits is not > 0)
                didPayloadFullyDecoded = false;
            var byteLength = (int)declaredBytes;
            var raw = data.AsSpan(offset + 2, byteLength);
            var label = DidText(did, catalogs, ecu);
            // Fully decoded requires an unambiguous union of all declared bit
            // positions. The former field-count check certified overlapping
            // or sparse CDD/ODX layouts as complete, and could feed numeric
            // values from the same source bits into unrelated correlations.
            var hasOverlappingFields = false;
            var uncoveredBits = 0L;
            if (definition is { Fields.Count: > 0 } && declaredBits is > 0)
            {
                var cursor = 0L;
                foreach (var field in definition.Fields.OrderBy(field => field.BitOffset))
                {
                    if (field.BitOffset < cursor) hasOverlappingFields = true;
                    if (field.BitOffset > cursor) uncoveredBits += field.BitOffset - cursor;
                    cursor = Math.Max(cursor, (long)field.BitOffset + field.BitLength);
                }
                if (cursor < (long)byteLength * 8)
                    uncoveredBits += (long)byteLength * 8 - cursor;
                if (hasOverlappingFields || uncoveredBits > 0)
                    didPayloadFullyDecoded = false;
            }
            if (hasOverlappingFields)
                summaries.Add($"{label}: overlapping CDD/ODX field positions; numeric values withheld");
            else if (uncoveredBits > 0)
                summaries.Add($"{label}: {uncoveredBits} unmapped bit(s); complete decoding not established");
            labels.Add(label);
            var valueStart = values.Count;
            if (definition?.Fields.Count > 0)
            {
                foreach (var field in definition.Fields)
                {
                    if (!TryDecodeField(raw, field, out var rawValue, out var numeric,
                        out var physical, out var meaning))
                        continue;
                    // A merge conflict can preserve the definition for
                    // engineering inspection, but its field offsets and
                    // physical conversion are not independently established.
                    var trustedLayout = definition.EngineeringIssues.Count == 0 &&
                        declaredBits is > 0 && !hasOverlappingFields;
                    if (!trustedLayout)
                    {
                        numeric = null;
                        physical = rawValue;
                        meaning = hasOverlappingFields
                            ? "Overlapping CDD/ODX field positions: raw code only; numeric correlation forbidden."
                            : "Unresolved or conflicting CDD/ODX DID layout: raw code only.";
                    }
                    values.Add(new DecodedDiagnosticValue(
                        $"DID 0x{did:X4}/{field.Name}", field.DisplayName, rawValue,
                        physical, field.Unit, meaning, did, numeric,
                        ResponseBitOffset: trustedLayout
                            ? (offset + 2) * 8 + field.BitOffset : null,
                        ResponseBitLength: trustedLayout ? field.BitLength : null,
                        Provenance: trustedLayout && field.Odx is not null
                            ? $"{field.Odx.Provenance} · {definition.SourceFile}" : string.Empty));
                }
            }
            else
            {
                var physical = DecodePhysical(raw, definition);
                // Unknown or ECU-ambiguous DID metadata must never create a
                // confident numerical observation for DID/CAN correlation.
                var numeric = definition is { BitLength: > 0 } &&
                    definition.EngineeringIssues.Count == 0 &&
                    !definition.DataType.Contains("ASCII", StringComparison.OrdinalIgnoreCase) &&
                    !definition.DataType.Contains("STRING", StringComparison.OrdinalIgnoreCase) &&
                    !definition.DataType.Contains("BYTEFIELD", StringComparison.OrdinalIgnoreCase) &&
                    double.TryParse(physical, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) &&
                    double.IsFinite(parsed)
                    ? parsed : (double?)null;
                values.Add(new DecodedDiagnosticValue(
                    $"DID 0x{did:X4}",
                    definition?.DisplayName ?? StandardDids.GetValueOrDefault(did, $"DID 0x{did:X4}"),
                    FormatHex(raw), physical, definition?.Unit ?? string.Empty,
                    definition?.Description ?? string.Empty, did, numeric,
                    ResponseBitOffset: definition is { BitLength: > 0 } &&
                        definition.EngineeringIssues.Count == 0
                        ? (offset + 2) * 8 : null,
                    ResponseBitLength: definition?.EngineeringIssues.Count == 0
                        ? definition?.BitLength : null));
            }

            var decodedFields = values.Count - valueStart;
            if (decodedFields == 0 ||
                definition?.Fields.Count > 0 && decodedFields != definition.Fields.Count)
                didPayloadFullyDecoded = false;
            if (decodedFields > 1)
                summaries.Add($"{label}: {decodedFields} distinct decoded fields (inspect individual source bytes)");
            else if (decodedFields == 0)
                summaries.Add($"{label} = {FormatHex(raw)} (field layout not decoded)");
            else
            {
                var first = values[valueStart];
                summaries.Add(string.IsNullOrWhiteSpace(first.Physical)
                    ? $"{label} = {FormatHex(raw)}"
                    : $"{label} = {first.Physical}" +
                      (string.IsNullOrWhiteSpace(first.Unit) ? string.Empty : " " + first.Unit));
            }
            offset += 2 + byteLength;
            if (declaredBits is not > 0) break; // only a terminal, unknown-width DID
        }

        objectText = string.Join(", ", labels);
        if (malformedRequest)
            summaries.Add("Request DID list was malformed; the positive response cannot be fully validated");
        if (offset < data.Length && string.IsNullOrEmpty(ambiguous))
            ambiguous = $"Unparsed {data.Length - offset} byte(s) of DID response";
        if (requested.Count > 0 && requested.Any(item =>
                !observedDids.TryGetValue(item.Key, out var count) ||
                count != item.Value))
        {
            didPayloadFullyDecoded = false;
            if (string.IsNullOrEmpty(ambiguous))
                ambiguous = "Positive DID response does not contain all requested DIDs";
        }
        if (!string.IsNullOrEmpty(ambiguous))
        {
            didPayloadFullyDecoded = false;
            summaries.Add(ambiguous);
        }
        return summaries.Count == 0 ? "Truncated DID response" : string.Join("; ", summaries);
    }

    internal static bool TryDecodeField(
        ReadOnlySpan<byte> data,
        DiagnosticFieldDefinition field,
        out string rawText,
        out double? numeric,
        out string physical,
        out string meaning)
    {
        rawText = string.Empty;
        numeric = null;
        physical = string.Empty;
        meaning = string.Empty;
        // An ISO-TP payload may contain long, byte-aligned ASCII or raw
        // arrays; only *numeric integer conversion* is limited to 64
        // bits. The former 64-bit early guard discarded legitimate
        // VIN-like and extended text fields instead of decoding them.
        if (field.BitLength <= 0 || field.BitOffset < 0 ||
            (long)field.BitOffset + field.BitLength > (long)data.Length * 8)
            return false;

        var firstByte = field.BitOffset / 8;
        var finalByteExclusive = (field.BitOffset + field.BitLength + 7) / 8;
        var bytes = data[firstByte..finalByteExclusive];
        var littleEndian = field.ByteOrder.Contains("LITTLE", StringComparison.OrdinalIgnoreCase) ||
                           field.ByteOrder.Contains("LOWHIGH", StringComparison.OrdinalIgnoreCase);
        var bigEndian = field.ByteOrder.Contains("BIG", StringComparison.OrdinalIgnoreCase) ||
                        field.ByteOrder.Contains("HIGHLOW", StringComparison.OrdinalIgnoreCase);
        var fullByte = field.BitLength == 8 && field.BitOffset % 8 == 0;

        if (field.DataType.Contains("ASCII", StringComparison.OrdinalIgnoreCase) ||
            field.DataType.Contains("STRING", StringComparison.OrdinalIgnoreCase) ||
            field.DataType.Contains("BYTEFIELD", StringComparison.OrdinalIgnoreCase))
        {
            // For text, byte alignment and an explicitly supported encoding
            // are required before the UI displays characters. Unaligned
            // or non-ASCII payloads remain exact raw evidence.
            var isAscii = field.DataType.Contains("ASCII", StringComparison.OrdinalIgnoreCase);
            var isAligned = field.BitOffset % 8 == 0 && field.BitLength % 8 == 0;
            rawText = FormatHex(bytes);
            physical = isAscii && isAligned &&
                       TryDecodePrintableAscii(bytes, out var ascii)
                ? ascii
                : rawText;
            meaning = physical == rawText && isAscii
                ? "ASCII interpretation unavailable (unaligned, control or non-ASCII bytes); raw source retained."
                : field.Description;
            return true; // text and binary arrays never become numeric evidence
        }

        if (field.Odx is not null &&
            field.DataType.Contains("FLOAT", StringComparison.OrdinalIgnoreCase))
            return DecodeOdxFloat(bytes, field, littleEndian, bigEndian,
                out rawText, out numeric, out physical, out meaning);

        if (field.BitLength > 64)
        {
            rawText = FormatHex(bytes);
            physical = rawText;
            meaning = "Integer exceeds 64 bits; exact raw byte evidence only, no invented double value.";
            return true;
        }

        if (!field.DataType.Contains("UINT", StringComparison.OrdinalIgnoreCase) &&
            !field.DataType.Contains("INT", StringComparison.OrdinalIgnoreCase))
        {
            // A float, BCD or vendor-specific scalar cannot be decoded as
            // a plain two's-complement integer merely because it occupies
            // a known bit interval. Preserve the exact bytes and source.
            rawText = FormatHex(bytes);
            physical = rawText;
            meaning = "Source scalar encoding is not a verified integer; raw bytes only.";
            return true;
        }

        if (!littleEndian && !bigEndian && !fullByte)
        {
            // CANdela/partial ODX definitions may omit the byte order. For
            // multi-byte and sub-byte values, the same raw bytes could encode
            // different numbers. Preserve the bytes, never guess a physical
            // number or promote it into DID↔CAN correlation evidence.
            rawText = FormatHex(bytes);
            physical = rawText;
            meaning = "Source does not define the field bit/byte order; raw bytes only.";
            return true;
        }

        ulong raw = 0;
        for (var bit = 0; bit < field.BitLength; bit++)
        {
            var absolute = field.BitOffset + bit;
            if (littleEndian)
            {
                var sourceBit = absolute % 8;
                raw |= (ulong)((data[absolute / 8] >> sourceBit) & 1) << bit;
            }
            else
            {
                var sourceBit = 7 - absolute % 8;
                raw = (raw << 1) | (uint)((data[absolute / 8] >> sourceBit) & 1);
            }
        }

        long signed = field.IsSigned && field.BitLength < 64 &&
                      (raw & (1UL << (field.BitLength - 1))) != 0
            ? unchecked((long)(raw | (ulong.MaxValue << field.BitLength)))
            : unchecked((long)raw);
        if (field.Odx is { } odx)
        {
            // The DOP's COMPU-METHOD decides. Raw stays visible next to the interpreted value, a value the
            // file gives no meaning for is flagged (never clamped or guessed), and conflicting variants never
            // reach this point because the merger/lookup withholds their field definitions.
            rawText = $"0x{raw:X}";
            var exactCoded = field.IsSigned
                ? signed >= -9007199254740991L && signed <= 9007199254740991L
                : raw <= 9007199254740991UL;
            if (!exactCoded)
            {
                physical = rawText;
                meaning = "Wide raw integer is not exactly representable in double; physical value unverified.";
                return true;
            }
            var label = field.IsSigned && signed < 0 ? $"{rawText} (signed {signed})" : rawText;
            var result = odx.Evaluate(field.IsSigned ? signed : raw, label);
            physical = result.Physical;
            numeric = result.Numeric;
            meaning = result.Meaning;
            return true;
        }
        if (!field.SupportsPhysicalConversion)
        {
            rawText = $"0x{raw:X}";
            physical = rawText;
            var label = (field.IsSigned || raw <= long.MaxValue
                ? field.EnumValues.FirstOrDefault(value => value.Value == signed)?.Text
                : null);
            meaning = string.IsNullOrWhiteSpace(label)
                ? "Physical transformation unsupported or piecewise: raw coded value only."
                : label + " (symbolic ODX label; physical numeric conversion not established)";
            return true; // Never feed an unverified physical number into DID↔CAN correlation
        }

        double coded = field.IsSigned ? signed : raw;
        rawText = $"0x{raw:X}";
        var exactInteger = field.IsSigned
            ? signed >= -9007199254740991L && signed <= 9007199254740991L
            : raw <= 9007199254740991UL;
        var physicalCandidate = coded * (field.Factor ?? 1d) +
                                (field.Offset ?? 0d);
        if (!exactInteger || !double.IsFinite(physicalCandidate))
        {
            // IEEE-754 double does not preserve every 54..64-bit integer
            // or non-finite scaling result. Keep exact coded raw source
            // but never promote an approximate/unbounded value to the
            // DID-CAN numerical correlation timeline.
            numeric = null;
            physical = rawText;
            meaning = !exactInteger
                ? "Wide raw integer is not exactly representable in double; physical value unverified."
                : "Source conversion is not finite; raw coded value only.";
            return true;
        }
        numeric = physicalCandidate;
        physical = numeric.Value.ToString("R", CultureInfo.InvariantCulture);
        meaning = (field.IsSigned || raw <= long.MaxValue
            ? field.EnumValues.FirstOrDefault(value => value.Value == signed)?.Text
            : null) ?? field.Description;
        return true;
    }

    /// <summary>A_FLOAT32 / A_FLOAT64 field of an ODX DOP: IEEE-754, byte aligned, with a declared byte order.
    /// The decoded value is the internal value fed to the COMPU-METHOD; NaN/Infinity are flagged, not converted.</summary>
    private static bool DecodeOdxFloat(
        ReadOnlySpan<byte> bytes, DiagnosticFieldDefinition field, bool littleEndian, bool bigEndian,
        out string rawText, out double? numeric, out string physical, out string meaning)
    {
        rawText = FormatHex(bytes);
        physical = rawText;
        numeric = null;
        var width = field.DataType.Contains("FLOAT32", StringComparison.OrdinalIgnoreCase) ? 32
            : field.DataType.Contains("FLOAT64", StringComparison.OrdinalIgnoreCase) ? 64 : 0;
        if (width == 0 || field.BitLength != width || field.BitOffset % 8 != 0)
        {
            meaning = $"{field.DataType} needs a byte-aligned {(width == 0 ? "32 or 64" : width)}-bit field " +
                      $"(declared {field.BitLength} bit(s) at bit {field.BitOffset}); raw bytes only.";
            return true;
        }
        if (littleEndian == bigEndian)
        {
            meaning = "Source does not define the float byte order; raw bytes only.";
            return true;
        }
        double value = width == 32
            ? littleEndian ? System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(bytes)
                           : System.Buffers.Binary.BinaryPrimitives.ReadSingleBigEndian(bytes)
            : littleEndian ? System.Buffers.Binary.BinaryPrimitives.ReadDoubleLittleEndian(bytes)
                           : System.Buffers.Binary.BinaryPrimitives.ReadDoubleBigEndian(bytes);
        if (!double.IsFinite(value))
        {
            meaning = $"{field.DataType} value is NaN or infinite; not converted, raw bytes kept.";
            return true;
        }
        var result = field.Odx!.Evaluate(value, rawText);
        physical = result.Physical;
        numeric = result.Numeric;
        meaning = result.Meaning;
        return true;
    }

    private static string DecodeDtcResponse(
        byte[] data,
        IReadOnlyList<DidCatalog> catalogs,
        List<DecodedDtc> dtcs,
        string? ecu)
    {
        if (data.Length < 2) return "Truncated DTC response";
        var sub = (byte)(data[1] & 0x7F);
        var name = DtcSubFunctions.GetValueOrDefault(sub, $"sub 0x{sub:X2}");
        if (sub is 0x01 or 0x07 or 0x11 or 0x12 && data.Length >= 6)
        {
            var count = (data[^2] << 8) | data[^1];
            return $"{name}: {count} DTC(s) · availability 0x{data[2]:X2}";
        }

        if (sub == 0x05)
        {
            // Records carry their own DTC numbers; Decode() fills the DTC list from them.
            return name;
        }

        // ISO 14229-1 11.3: with no stored snapshot/extended record the response holds just the
        // 4-byte DTCAndStatusRecord (59 sub DTC[3] status), i.e. 6 bytes.
        if (sub is 0x04 or 0x06 or 0x10 && data.Length >= 6)
        {
            var code = (data[2] << 16) | (data[3] << 8) | data[4];
            var status = data[5];
            var definition = FindUniqueDtc(code, catalogs, ecu);
            var recordType = sub == 0x04 ? "snapshot" : "extended data";
            var extra = $"{recordType}: {FormatHex(data.AsSpan(6))}";
            dtcs.Add(new DecodedDtc(code, $"0x{code:X6}", definition?.DisplayName ?? string.Empty,
                status, DtcStatus(status), extra));
            return $"{name}: 0x{code:X6} · {extra}";
        }

        if (sub == 0x03)
        {
            var snapshotRecords = new List<string>();
            for (var index = 2; index + 3 < data.Length; index += 4)
            {
                var code = (data[index] << 16) | (data[index + 1] << 8) | data[index + 2];
                var record = data[index + 3];
                var definition = FindUniqueDtc(code, catalogs, ecu);
                dtcs.Add(new DecodedDtc(code, $"0x{code:X6}", definition?.DisplayName ?? string.Empty,
                    null, "snapshot identified", $"snapshot record 0x{record:X2}"));
                snapshotRecords.Add($"0x{code:X6}/0x{record:X2}");
            }
            return $"{name}: {string.Join(", ", snapshotRecords)}";
        }

        if (sub == 0x14)
        {
            var counters = new List<string>();
            for (var index = 2; index + 3 < data.Length; index += 4)
            {
                var code = (data[index] << 16) | (data[index + 1] << 8) | data[index + 2];
                var counter = data[index + 3];
                var definition = FindUniqueDtc(code, catalogs, ecu);
                dtcs.Add(new DecodedDtc(code, $"0x{code:X6}", definition?.DisplayName ?? string.Empty,
                    null, "fault detection counter", counter.ToString(CultureInfo.InvariantCulture)));
                counters.Add($"0x{code:X6}={counter}");
            }
            return $"{name}: {string.Join(", ", counters)}";
        }

        // Record layout per ISO 14229-1 for each report type. A report type whose layout is not
        // listed stays raw: reading DTCs at a guessed offset would invent fault codes.
        //   prefix   = bytes between the sub-function and the first record
        //   leading  = bytes of each record before the 3-byte DTC (severity / functional unit)
        int prefix, leading;
        switch (sub)
        {
            case 0x02 or 0x0A or 0x0B or 0x0C or 0x0D or 0x0E or 0x0F or 0x13 or 0x15:
                (prefix, leading) = (1, 0); // DTCStatusAvailabilityMask
                break;
            case 0x17:
                (prefix, leading) = (2, 0); // MemorySelection, DTCStatusAvailabilityMask
                break;
            case 0x08 or 0x09:
                (prefix, leading) = (1, 2); // mask; records = severity, functionalUnit, DTC, status
                break;
            case 0x42:
                (prefix, leading) = (4, 1); // FGID, status mask, severity mask, format; records = severity, DTC, status
                break;
            case 0x55:
                (prefix, leading) = (3, 0); // FGID, status mask, format
                break;
            default:
                return $"{name} · {FormatHex(data.AsSpan(2))}".TrimEnd(' ', '·');
        }

        var offset = 2 + prefix;
        var recordLength = leading + 4;
        while (offset + recordLength <= data.Length)
        {
            var codeAt = offset + leading;
            var code = (data[codeAt] << 16) | (data[codeAt + 1] << 8) | data[codeAt + 2];
            var status = data[codeAt + 3];
            var definition = FindUniqueDtc(code, catalogs, ecu);
            var extra = leading switch
            {
                2 => $"severity 0x{data[offset]:X2} · functional unit 0x{data[offset + 1]:X2}",
                1 => $"severity 0x{data[offset]:X2}",
                _ => string.Empty
            };
            dtcs.Add(new DecodedDtc(
                code,
                $"0x{code:X6}",
                definition?.DisplayName ?? string.Empty,
                status,
                DtcStatus(status),
                extra));
            offset += recordLength;
        }

        return dtcs.Count == 0
            ? $"{name} · {FormatHex(data.AsSpan(2))}"
            : $"{name}: {dtcs.Count} DTC(s) · " + string.Join(", ", dtcs.Take(4).Select(item =>
                string.IsNullOrWhiteSpace(item.Name) ? item.CodeText : $"{item.CodeText} {item.Name}")) +
              (dtcs.Count > 4 ? " …" : string.Empty);
    }

    /// <summary>Only render catalog-declared, byte-aligned ASCII which is
    /// printable after optional trailing NUL padding. Keep embedded
    /// controls/non-ASCII as raw evidence, even on plain scalar DIDs.</summary>
    private static bool TryDecodePrintableAscii(
        ReadOnlySpan<byte> bytes, out string value)
    {
        value = string.Empty;
        var end = bytes.Length;
        while (end > 0 && bytes[end - 1] == 0)
            end--;
        if (end == 0)
            return false;
        for (var i = 0; i < end; i++)
            if (bytes[i] is < 0x20 or > 0x7E)
                return false;
        value = System.Text.Encoding.ASCII.GetString(bytes[..end]);
        return true;
    }

    internal static string DecodePhysical(ReadOnlySpan<byte> raw, DidParameter? definition)
    {
        if (raw.Length == 0) return string.Empty;
        // Do not infer a text encoding from pleasant-looking unknown
        // bytes. A known ASCII DOP without conflicting OEM metadata is
        // the only basis for displaying this value as characters.
        if (definition is null || definition.EngineeringIssues.Count > 0)
            return FormatHex(raw);
        if (definition.DataType.Contains("ASCII", StringComparison.OrdinalIgnoreCase))
            return TryDecodePrintableAscii(raw, out var ascii) ? ascii : FormatHex(raw);
        if (!definition.DataType.Contains("UINT", StringComparison.OrdinalIgnoreCase) &&
            !definition.DataType.Contains("INT", StringComparison.OrdinalIgnoreCase))
            return FormatHex(raw);
        // A width-less or conflicting catalog declaration is not enough to
        // interpret arbitrary response bytes as a physical scalar.
        if (definition.EngineeringIssues.Count > 0 ||
            definition.BitLength is not > 0 ||
            definition.BitLength != raw.Length * 8 ||
            definition.BitLength > 64)
            return FormatHex(raw);
        var byteCount = raw.Length;
        if (byteCount > 8) return FormatHex(raw);
        ulong unsigned = 0;
        for (var index = 0; index < byteCount; index++) unsigned = (unsigned << 8) | raw[index];
        var bits = Math.Min(64, (int)(byteCount * 8));
        var isSigned = definition.DataType.Contains("INT", StringComparison.OrdinalIgnoreCase) &&
                       !definition.DataType.Contains("UINT", StringComparison.OrdinalIgnoreCase);
        double valueRaw = unsigned;
        if (isSigned && bits > 0)
        {
            valueRaw = bits == 64 ? unchecked((long)unsigned) :
                (unsigned & (1UL << (bits - 1))) != 0
                    ? unchecked((long)(unsigned | (~0UL << bits)))
                    : (long)unsigned;
        }
        const double maxExactInteger = 9007199254740991d;
        if (!double.IsFinite(valueRaw) || valueRaw < -maxExactInteger ||
            valueRaw > maxExactInteger)
            return FormatHex(raw);
        var value = valueRaw * (definition.Factor ?? 1d) + (definition.Offset ?? 0d);
        return double.IsFinite(value)
            ? value.ToString("R", CultureInfo.InvariantCulture)
            : FormatHex(raw);
    }

    private static DiagnosticServiceDefinition? ResolveService(
        IReadOnlyList<DidCatalog> catalogs,
        byte sid,
        byte[] request)
    {
        var sub = request.Length > 1 ? (byte?)(request[1] & 0x7F) : null;
        return catalogs.SelectMany(catalog => catalog.ServiceDefinitions)
            .Where(service => service.ServiceId == sid)
            .OrderByDescending(service => service.SubFunction == sub)
            .FirstOrDefault();
    }

    private static T? FindUniqueForEcu<T>(
        IEnumerable<T> definitions, string? ecu, Func<T, string> ecuSelector)
        where T : class
    {
        var matches = definitions.ToArray();
        if (matches.Length == 0) return null;
        // A confirmed ECU identity is a constraint, not a name-search
        // preference: an unrelated unique definition must never silently
        // become the physical interpretation of this ECU's DID/DTC.
        if (!string.IsNullOrWhiteSpace(ecu))
        {
            var exact = matches.Where(item =>
                string.Equals(ecuSelector(item), ecu,
                    StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
            return exact.Length == 1 ? exact[0] : null;
        }
        // Without a proved ECU, a single global definition is usable as
        // an explicitly unattributed catalog interpretation; duplicates
        // remain unresolved rather than choosing by load order.
        return matches.Length == 1 ? matches[0] : null;
    }

    internal static DidParameter? FindUniqueDid(
        int did, IReadOnlyList<DidCatalog> catalogs, string? ecu) =>
        FindUniqueForEcu(
            catalogs.SelectMany(catalog => catalog.Parameters).Where(item => item.Did == did),
            ecu, item => item.Ecu);

    /// <summary>Resolve matching DTC definitions across CDD/ODX and external tables.
    /// Merge complementary metadata only if every supplied value agrees. Never select a
    /// conflicting ECU definition by catalog load order, and never borrow from another ECU.</summary>
    internal static DiagnosticTroubleCode? FindUniqueDtc(
        int code, IReadOnlyList<DidCatalog> catalogs, string? ecu)
    {
        var candidates = catalogs.SelectMany(catalog => catalog.DtcDefinitions)
            .Where(item => item.Code == code).ToArray();
        if (!string.IsNullOrWhiteSpace(ecu))
            candidates = candidates.Where(item => string.Equals(item.Ecu, ecu,
                StringComparison.OrdinalIgnoreCase)).ToArray();
        if (candidates.Length == 0) return null;
        // An unidentified ECU is not proof that two equally numbered DTCs belong
        // to the same module. Even consistent text is insufficient across ECUs.
        if (candidates.Select(item => item.Ecu).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 1)
            return null;
        if (candidates.Length == 1) return candidates[0];

        static bool Compatible(IEnumerable<string> texts)
        {
            return texts.Where(text => !string.IsNullOrWhiteSpace(text))
                .Select(text => text.Trim()).Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(2).Count() <= 1;
        }
        if (!Compatible(candidates.Select(item => item.Name)) ||
            !Compatible(candidates.Select(item => item.DisplayText)) ||
            !Compatible(candidates.Select(item => item.Description)))
            return null;

        var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates)
        {
            foreach (var attribute in candidate.TableAttributes)
            {
                if (attributes.TryGetValue(attribute.Key, out var earlier) &&
                    !string.Equals(earlier.Trim(), attribute.Value.Trim(), StringComparison.OrdinalIgnoreCase))
                    return null; // contradictory OEM reference text
                attributes[attribute.Key] = attribute.Value;
            }
        }

        // Multiple nonempty binary layouts would require a definition-level
        // equivalence proof. Reject them instead of discarding an OEM variant.
        var snapshot = candidates.Where(item => item.SnapshotFields.Count > 0).ToArray();
        var extended = candidates.Where(item => item.ExtendedDataFields.Count > 0).ToArray();
        if (snapshot.Length > 1 || extended.Length > 1) return null;
        var primary = candidates.FirstOrDefault(item => item.SnapshotFields.Count > 0 ||
            item.ExtendedDataFields.Count > 0) ?? candidates[0];
        string First(Func<DiagnosticTroubleCode, string> select) =>
            candidates.Select(select).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
        return primary with
        {
            Name = First(item => item.Name),
            DisplayText = First(item => item.DisplayText),
            Description = First(item => item.Description),
            SnapshotDefinitions = snapshot.Length == 1 ? snapshot[0].SnapshotDefinitions : null,
            ExtendedDataDefinitions = extended.Length == 1 ? extended[0].ExtendedDataDefinitions : null,
            SourceFilesOverride = candidates.SelectMany(item => item.DefinitionFiles)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            TableAttributesOverride = attributes,
            SourceSheet = string.Join(" + ", candidates.Select(item => item.SourceSheet)
                .Where(sheet => !string.IsNullOrWhiteSpace(sheet))
                .Distinct(StringComparer.OrdinalIgnoreCase))
        };
    }

    internal static string DidText(int did, IReadOnlyList<DidCatalog> catalogs, string? ecu = null)
    {
        var definition = FindUniqueDid(did, catalogs, ecu);
        var name = definition?.DisplayName ?? StandardDids.GetValueOrDefault(did, string.Empty);
        return string.IsNullOrWhiteSpace(name) ? $"DID 0x{did:X4}" : $"DID 0x{did:X4} · {name}";
    }

    private static string RoutineText(int identifier, IReadOnlyList<DidCatalog> catalogs, string? ecu = null)
    {
        var routine = FindUniqueForEcu(
            catalogs.SelectMany(catalog => catalog.RoutineDefinitions)
                .Where(item => item.Identifier == identifier),
            ecu, item => item.Ecu);
        return routine is null ? $"Routine 0x{identifier:X4}" : $"Routine 0x{identifier:X4} · {routine.DisplayName}";
    }

    private static string RoutineAction(byte value) => (value & 0x7F) switch
    {
        1 => "Start", 2 => "Stop", 3 => "Request results", var other => $"Action 0x{other:X2}"
    };

    private static string SessionText(byte value) => value switch
    {
        1 => "default session", 2 => "programming session", 3 => "extended session",
        4 => "safety-system session", _ => $"session 0x{value:X2}"
    };

    internal static string DtcStatus(byte status)
    {
        var names = new[]
        {
            "testFailed", "testFailedThisCycle", "pending", "confirmed",
            "notCompletedSinceClear", "failedSinceClear", "notCompletedThisCycle", "warningIndicator"
        };
        var active = Enumerable.Range(0, 8).Where(bit => (status & (1 << bit)) != 0).Select(bit => names[bit]).ToArray();
        return active.Length == 0 ? "no status bits" : string.Join(", ", active);
    }

    /// <summary>Linear-time, one-pass canonical hex for long DIDs.
    /// The previous Aggregate concatenated a new growing string for every
    /// byte (quadratic time and allocations for 4 KiB+ byte fields).</summary>
    internal static string FormatHex(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0)
            return string.Empty;
        // A delegate cannot capture a ref struct ReadOnlySpan. Copy the
        // already-decoded field only once, then write the output string
        // once; no per-byte strings, LINQ enumerators or intermediate hex.
        var bytes = data.ToArray();
        return string.Create(bytes.Length * 3 - 1, bytes, static (destination, source) =>
        {
            const string digits = "0123456789ABCDEF";
            var output = 0;
            for (var i = 0; i < source.Length; i++)
            {
                if (i > 0)
                    destination[output++] = ' ';
                var value = source[i];
                destination[output++] = digits[value >> 4];
                destination[output++] = digits[value & 0x0F];
            }
        });
    }
}
