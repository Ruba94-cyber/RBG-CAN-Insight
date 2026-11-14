using System.Globalization;

namespace RbgCanInsight.Diagnostics;

/// <summary>Decoders for UDS services 0x29 (Authentication), 0x83 (AccessTimingParameter) and
/// 0x84 (SecuredDataTransmission) plus the timing record carried by a 0x10 positive response.
/// Structure only: certificate, challenge and signature payloads are never interpreted
/// cryptographically, they are reported as byte counts.</summary>
public static class UdsExtendedServices
{
    private static readonly IReadOnlyDictionary<byte, string> AuthenticationSubFunctions = new Dictionary<byte, string>
    {
        [0x00] = "deAuthenticate",
        [0x01] = "verifyCertificateUnidirectional",
        [0x02] = "verifyCertificateBidirectional",
        [0x03] = "proofOfOwnership",
        [0x04] = "transmitCertificate",
        [0x05] = "requestChallengeForAuthentication",
        [0x06] = "verifyProofOfOwnershipUnidirectional",
        [0x07] = "verifyProofOfOwnershipBidirectional",
        [0x08] = "authenticationConfiguration"
    };

    private static readonly IReadOnlyDictionary<byte, string> AuthenticationReturnParameters = new Dictionary<byte, string>
    {
        [0x00] = "requestAccepted",
        [0x01] = "generalReject",
        [0x02] = "authenticationConfiguration APCE",
        [0x03] = "authenticationConfiguration ACR with asymmetric cryptography",
        [0x04] = "authenticationConfiguration ACR with symmetric cryptography",
        [0x10] = "deAuthentication successful",
        [0x11] = "certificateVerified, ownershipVerification necessary",
        [0x12] = "ownershipVerified, authentication complete",
        [0x13] = "certificateVerified"
    };

    private static readonly IReadOnlyDictionary<byte, string> TimingSubFunctions = new Dictionary<byte, string>
    {
        [0x01] = "readExtendedTimingParameterSet",
        [0x02] = "setTimingParametersToDefaultValues",
        [0x03] = "readCurrentlyActiveTimingParameters",
        [0x04] = "setTimingParametersToGivenValues"
    };

    public static string AuthenticationSubFunctionName(byte sub) =>
        AuthenticationSubFunctions.GetValueOrDefault((byte)(sub & 0x7F), $"sub 0x{sub & 0x7F:X2}");

    public static string AuthenticationReturnName(byte value) =>
        AuthenticationReturnParameters.GetValueOrDefault(value, $"authenticationReturnParameter 0x{value:X2}");

    public static string TimingSubFunctionName(byte sub) =>
        TimingSubFunctions.GetValueOrDefault((byte)(sub & 0x7F), $"sub 0x{sub & 0x7F:X2}");

    public static string DescribeRequest(byte sid, byte[] data, out string objectText)
    {
        objectText = string.Empty;
        switch (sid)
        {
            case 0x29:
            {
                if (data.Length < 2) return "Truncated Authentication request";
                objectText = AuthenticationSubFunctionName(data[1]);
                var extra = data.Length > 2 ? $" · {data.Length - 2} parameter byte(s)" : string.Empty;
                return $"{objectText}{extra}";
            }
            case 0x83:
            {
                if (data.Length < 2) return "Truncated AccessTimingParameter request";
                var sub = (byte)(data[1] & 0x7F);
                objectText = TimingSubFunctionName(sub);
                if (sub == 0x04 && TryReadTimingRecord(data.AsSpan(2), out var record))
                    return $"{objectText} · {record}";
                return data.Length > 2 ? $"{objectText} · {data.Length - 2} parameter byte(s)" : objectText;
            }
            default:
            {
                // 0x84 has no sub-function byte: the request is the security data request record.
                if (data.Length < 3) return "Truncated SecuredDataTransmission request";
                objectText = $"administrative parameter 0x{(data[1] << 8) | data[2]:X4}";
                return $"{objectText} · {Math.Max(0, data.Length - 3)} security data byte(s)";
            }
        }
    }

    public static string DescribeResponse(byte sid, byte[] data, byte[] request)
    {
        switch (sid)
        {
            case 0x29:
            {
                if (data.Length < 2) return "Truncated Authentication response";
                var name = AuthenticationSubFunctionName(data[1]);
                var returnText = data.Length > 2 ? AuthenticationReturnName(data[2]) : "no returnParameter";
                return data.Length > 3 ? $"{name}: {returnText} · {data.Length - 3} data byte(s)" : $"{name}: {returnText}";
            }
            case 0x83:
            {
                if (data.Length < 2) return "Truncated AccessTimingParameter response";
                var sub = (byte)(data[1] & 0x7F);
                var name = TimingSubFunctionName(sub);
                return sub is 0x01 or 0x03 && TryReadTimingRecord(data.AsSpan(2), out var record)
                    ? $"{name}: {record}"
                    : data.Length > 2 ? $"{name} · {data.Length - 2} byte(s)" : name;
            }
            default:
                return data.Length > 3
                    ? $"Secured data response · administrative parameter 0x{(data[1] << 8) | data[2]:X4} · {data.Length - 3} byte(s)"
                    : "Secured data response";
        }
    }

    /// <summary>P2server_max (2 bytes, 1 ms) and P2*server_max (2 bytes, 10 ms) as reported by a
    /// 0x10 positive response or a 0x83 timing record.</summary>
    public static bool TryReadTimingRecord(ReadOnlySpan<byte> record, out string text)
    {
        if (!TryReadTimingValues(record, out var p2, out var p2Star))
        {
            text = string.Empty;
            return false;
        }
        text = string.Create(CultureInfo.InvariantCulture, $"P2 {p2:0} ms, P2* {p2Star:0} ms");
        return true;
    }

    public static bool TryReadTimingValues(ReadOnlySpan<byte> record, out double p2Milliseconds, out double p2StarMilliseconds)
    {
        p2Milliseconds = p2StarMilliseconds = 0;
        if (record.Length < 4) return false;
        p2Milliseconds = (record[0] << 8) | record[1];
        p2StarMilliseconds = ((record[2] << 8) | record[3]) * 10d;
        return true;
    }

    /// <summary>Timing parameters announced by a 0x10 positive response (bytes 2..5).</summary>
    public static bool TryReadSessionTiming(byte[] response, out string text)
    {
        text = string.Empty;
        return response.Length >= 6 && TryReadTimingRecord(response.AsSpan(2, 4), out text);
    }
}
