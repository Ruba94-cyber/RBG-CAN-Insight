using System.Globalization;

namespace RbgCanInsight.Correlation;

/// <summary>
/// Best-effort per-frame interpreter for diagnostic traffic: recognises ISO-TP framing
/// (single/first/consecutive/flow-control) and decodes the UDS service inside, including
/// ReadDataByIdentifier requests/responses with the DID name resolved from a loaded CDD/ODX
/// catalog when available. Purely offline and stateless — each frame is described on its own,
/// which is what a trace grid needs (continuation frames are labelled as such rather than
/// reassembled; full reassembly stays in <see cref="IsoTpDidExtractor"/>).
/// </summary>
public static class UdsFrameInterpreter
{
    private static readonly Dictionary<byte, string> ServiceNames = new()
    {
        [0x10] = "DiagnosticSessionControl",
        [0x11] = "EcuReset",
        [0x14] = "ClearDiagnosticInformation",
        [0x19] = "ReadDTCInformation",
        [0x22] = "ReadDataByIdentifier",
        [0x23] = "ReadMemoryByAddress",
        [0x24] = "ReadScalingDataByIdentifier",
        [0x27] = "SecurityAccess",
        [0x28] = "CommunicationControl",
        [0x29] = "Authentication",
        [0x2A] = "ReadDataByPeriodicIdentifier",
        [0x2C] = "DynamicallyDefineDataIdentifier",
        [0x2E] = "WriteDataByIdentifier",
        [0x2F] = "InputOutputControlByIdentifier",
        [0x31] = "RoutineControl",
        [0x34] = "RequestDownload",
        [0x35] = "RequestUpload",
        [0x36] = "TransferData",
        [0x37] = "RequestTransferExit",
        [0x38] = "RequestFileTransfer",
        [0x3D] = "WriteMemoryByAddress",
        [0x3E] = "TesterPresent",
        [0x83] = "AccessTimingParameter",
        [0x84] = "SecuredDataTransmission",
        [0x85] = "ControlDTCSetting",
        [0x86] = "ResponseOnEvent",
        [0x87] = "LinkControl"
    };

    private static readonly Dictionary<byte, string> SessionNames = new()
    {
        [0x01] = "default",
        [0x02] = "programming",
        [0x03] = "extended",
        [0x04] = "safety system"
    };

    private static readonly Dictionary<byte, string> ResetNames = new()
    {
        [0x01] = "hard reset",
        [0x02] = "key off/on",
        [0x03] = "soft reset",
        [0x04] = "enable rapid power shutdown",
        [0x05] = "disable rapid power shutdown"
    };

    private static readonly Dictionary<byte, string> RoutineControlNames = new()
    {
        [0x01] = "start",
        [0x02] = "stop",
        [0x03] = "request results"
    };

    /// <summary>ISO 14229 well-known data identifiers, used when no CDD/ODX resolver knows the DID.</summary>
    private static readonly Dictionary<int, string> StandardDidNames = new()
    {
        [0xF186] = "ActiveDiagnosticSession",
        [0xF187] = "SparePartNumber",
        [0xF188] = "EcuSoftwareNumber",
        [0xF189] = "EcuSoftwareVersion",
        [0xF18A] = "SystemSupplierId",
        [0xF18C] = "EcuSerialNumber",
        [0xF190] = "VIN",
        [0xF191] = "EcuHardwareNumber",
        [0xF192] = "SupplierEcuHardwareNumber",
        [0xF193] = "SupplierEcuHardwareVersion",
        [0xF194] = "SupplierEcuSoftwareNumber",
        [0xF195] = "SupplierEcuSoftwareVersion",
        [0xF197] = "SystemName",
        [0xF198] = "RepairShopCode",
        [0xF199] = "ProgrammingDate",
        [0xF19D] = "InstallationDate"
    };

    /// <summary>
    /// True for identifiers conventionally used by diagnostics: the ISO 15765-4 11-bit range
    /// 0x700-0x7FF, and 29-bit normal fixed addressing 0x18DAxxyy (physical) / 0x18DBxxyy
    /// (functional).
    /// </summary>
    public static bool LooksLikeDiagnosticId(uint identifier, bool isExtended) =>
        isExtended
            ? (identifier & 0x1FFF0000u) is 0x18DA0000u or 0x18DB0000u
            : identifier is >= 0x700u and <= 0x7FFu;

    /// <summary>
    /// Strong per-frame evidence that a payload carries UDS: a well-formed ISO-TP single/first
    /// frame with a known service, a negative response, or a Read/WriteDataByIdentifier whose
    /// DID exists in the loaded CDD/ODX catalog. Used to LEARN which trace identifiers are
    /// diagnostic — once one frame on an ID qualifies, every frame on that ID (continuations,
    /// flow controls) is interpreted, regardless of the ID's numeric range.
    /// </summary>
    public static bool LooksLikeUdsFrame(ReadOnlySpan<byte> payload, Func<int, string?>? didNameResolver = null)
    {
        if (payload.Length < 2)
        {
            return false;
        }

        // A catalog hit on the DID bytes of a 22/62/2E/6E frame is diagnostic evidence on its
        // own — this is what ties "I loaded a CDD" to decoding on OEM-specific identifiers.
        if (didNameResolver is not null)
        {
            var frameKind = payload[0] >> 4;
            var uds = frameKind switch
            {
                0x0 => payload[1..],
                0x1 when payload.Length > 2 => payload[2..],
                _ => ReadOnlySpan<byte>.Empty
            };
            if (uds.Length >= 3 && uds[0] is 0x22 or 0x62 or 0x2E or 0x6E &&
                !string.IsNullOrWhiteSpace(didNameResolver((uds[1] << 8) | uds[2])))
            {
                return true;
            }
        }

        return Describe(payload, identifier: 0, isExtended: false, didNameResolver) is not null;
    }

    /// <summary>
    /// Describes a frame as ISO-TP/UDS if it plausibly is one; null otherwise. Frames on
    /// diagnostic identifiers (numeric range, or <paramref name="assumeDiagnostic"/> learned
    /// from the trace) are always interpreted; on any other identifier the payload must be a
    /// well-formed single/first frame carrying a known UDS service, so normal signal traffic is
    /// not mislabelled.
    /// </summary>
    public static string? Describe(
        ReadOnlySpan<byte> payload,
        uint identifier,
        bool isExtended,
        Func<int, string?>? didNameResolver = null,
        bool assumeDiagnostic = false)
    {
        if (payload.Length == 0)
        {
            return null;
        }

        var isDiagnosticId = assumeDiagnostic || LooksLikeDiagnosticId(identifier, isExtended);
        var frameType = payload[0] >> 4;
        switch (frameType)
        {
            case 0x0:
            {
                var length = payload[0] & 0x0F;
                if (length == 0 && payload.Length > 2 && payload[1] > 0 && payload[1] <= payload.Length - 2)
                {
                    // CAN FD escape: SF_DL in the second byte.
                    return DescribeUds(payload.Slice(2, payload[1]), isDiagnosticId, didNameResolver);
                }

                if (length < 1 || length > 7 || payload.Length < 1 + length)
                {
                    return null;
                }

                return DescribeUds(payload.Slice(1, length), isDiagnosticId, didNameResolver);
            }

            case 0x1:
            {
                if (payload.Length < 3)
                {
                    return null;
                }

                var totalLength = ((payload[0] & 0x0F) << 8) | payload[1];
                if (totalLength < 8)
                {
                    return null;
                }

                var head = DescribeUds(payload[2..], isDiagnosticId, didNameResolver);
                return head is null
                    ? null
                    : $"{head} · first frame, {totalLength} B total";
            }

            case 0x2 when isDiagnosticId:
                return $"ISO-TP continuation #{payload[0] & 0x0F}";

            case 0x3 when isDiagnosticId:
            {
                var status = (payload[0] & 0x0F) switch
                {
                    0 => "continue to send",
                    1 => "wait",
                    2 => "overflow/abort",
                    var other => $"status {other}"
                };
                return payload.Length >= 3
                    ? $"ISO-TP flow control: {status} (BS={payload[1]}, STmin={payload[2]})"
                    : $"ISO-TP flow control: {status}";
            }

            default:
                return null;
        }
    }

    private static string? DescribeUds(
        ReadOnlySpan<byte> data,
        bool isDiagnosticId,
        Func<int, string?>? didNameResolver)
    {
        if (data.Length == 0)
        {
            return null;
        }

        var sid = data[0];

        if (sid == 0x7F)
        {
            if (!isDiagnosticId && data.Length < 3)
            {
                return null;
            }

            var rejected = data.Length > 1 && ServiceNames.TryGetValue(data[1], out var serviceName)
                ? serviceName
                : data.Length > 1 ? $"service 0x{data[1]:X2}" : "unknown service";
            var reason = data.Length > 2 ? RbgCanInsight.Diagnostics.UdsServiceDecoder.NrcName(data[2]) : "unknown NRC";
            return $"UDS Neg ✗ {rejected} → {reason}";
        }

        var isResponse = sid >= 0x40 && ServiceNames.ContainsKey((byte)(sid - 0x40));
        var requestSid = isResponse ? (byte)(sid - 0x40) : sid;
        if (!ServiceNames.ContainsKey(requestSid))
        {
            return null;
        }

        // On non-diagnostic identifiers require the service payload to carry enough substance
        // (sub-function/DID) so a random data byte pattern is not mislabelled as UDS.
        if (!isDiagnosticId && data.Length < MinimumServiceLength(requestSid))
        {
            return null;
        }

        var kind = isResponse ? "UDS Resp" : "UDS Req";
        var name = ServiceNames[requestSid];
        var detail = DescribeServiceDetail(requestSid, isResponse, data, didNameResolver);
        return string.IsNullOrEmpty(detail) ? $"{kind} {name}" : $"{kind} {name} {detail}";
    }

    private static int MinimumServiceLength(byte requestSid) => requestSid switch
    {
        0x22 or 0x2E or 0x2F or 0x24 => 3,
        0x31 => 4,
        _ => 2
    };

    private static string DescribeServiceDetail(
        byte requestSid,
        bool isResponse,
        ReadOnlySpan<byte> data,
        Func<int, string?>? didNameResolver)
    {
        switch (requestSid)
        {
            case 0x10:
                return data.Length > 1 && SessionNames.TryGetValue((byte)(data[1] & 0x7F), out var session)
                    ? $"— {session} session"
                    : SubFunctionText(data);

            case 0x11:
                return data.Length > 1 && ResetNames.TryGetValue((byte)(data[1] & 0x7F), out var reset)
                    ? $"— {reset}"
                    : SubFunctionText(data);

            case 0x22 or 0x2E or 0x2F or 0x24 or 0x2C:
            {
                if (data.Length < 3)
                {
                    return string.Empty;
                }

                if (requestSid == 0x22 && !isResponse)
                {
                    // A request may list several DIDs, two bytes each.
                    var dids = new List<string>();
                    for (var index = 1; index + 1 < data.Length && dids.Count < 4; index += 2)
                    {
                        dids.Add(DidText((data[index] << 8) | data[index + 1], didNameResolver));
                    }

                    return dids.Count == 0 ? string.Empty : "— " + string.Join(", ", dids);
                }

                var did = (data[1] << 8) | data[2];
                var suffix = isResponse && data.Length > 3
                    ? $" = {FormatDataPreview(data[3..])}"
                    : string.Empty;
                return $"— {DidText(did, didNameResolver)}{suffix}";
            }

            case 0x27:
            {
                if (data.Length < 2)
                {
                    return string.Empty;
                }

                var level = data[1] & 0x7F;
                return (level & 1) == 1 ? $"— request seed (level 0x{level:X2})" : $"— send key (level 0x{level:X2})";
            }

            case 0x31:
            {
                if (data.Length < 4)
                {
                    return string.Empty;
                }

                var action = RoutineControlNames.TryGetValue((byte)(data[1] & 0x7F), out var routineAction)
                    ? routineAction
                    : $"sub 0x{data[1]:X2}";
                var routine = (data[2] << 8) | data[3];
                return $"— {action} routine 0x{routine:X4}";
            }

            case 0x3E:
                return data.Length > 1 && (data[1] & 0x80) != 0 ? "(suppress response)" : string.Empty;

            case 0x19:
                return data.Length > 1 ? $"— sub 0x{data[1]:X2}" : string.Empty;

            case 0x36:
                return data.Length > 1 ? $"— block #{data[1]}" : string.Empty;

            default:
                return string.Empty;
        }
    }

    private static string SubFunctionText(ReadOnlySpan<byte> data) =>
        data.Length > 1 ? $"— sub 0x{data[1]:X2}" : string.Empty;

    private static string DidText(int did, Func<int, string?>? didNameResolver)
    {
        var name = didNameResolver?.Invoke(did);
        if (string.IsNullOrWhiteSpace(name) && StandardDidNames.TryGetValue(did, out var standard))
        {
            name = standard;
        }

        return string.IsNullOrWhiteSpace(name) ? $"DID 0x{did:X4}" : $"DID 0x{did:X4} ({name})";
    }

    /// <summary>Short hex preview of response data; ASCII is shown too when it looks like text.</summary>
    private static string FormatDataPreview(ReadOnlySpan<byte> data)
    {
        const int maxBytes = 8;
        var shown = data.Length <= maxBytes ? data : data[..maxBytes];
        var hex = new string[shown.Length];
        var printable = data.Length >= 3;
        for (var index = 0; index < shown.Length; index++)
        {
            hex[index] = shown[index].ToString("X2", CultureInfo.InvariantCulture);
        }

        foreach (var value in data)
        {
            if (value is < 0x20 or > 0x7E)
            {
                printable = false;
                break;
            }
        }

        var text = string.Join(' ', hex) + (data.Length > maxBytes ? " …" : string.Empty);
        if (printable)
        {
            Span<char> ascii = stackalloc char[data.Length];
            for (var index = 0; index < data.Length; index++)
            {
                ascii[index] = (char)data[index];
            }

            text += $" \"{new string(ascii)}\"";
        }

        return text;
    }
}
