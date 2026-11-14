using RbgCanInsight.DiagData;

namespace RbgCanInsight.Diagnostics;

/// <summary>
/// KWP2000 (ISO 14230 / ISO 14230-3 over CAN) vocabulary. The UDS and KWP2000 service-ID spaces
/// overlap (0x10, 0x14, 0x27, 0x28, 0x31, 0x3E ...) so the protocol is never guessed from a single
/// frame: it is inferred from the loaded CDD/ODX services (see <see cref="IsKwpCatalogSet"/>) and
/// the result is only used to choose names and request layouts. UDS stays the default.
/// </summary>
public static class KwpServiceDecoder
{
    /// <summary>KWP2000 service names, keyed by request SID.</summary>
    public static readonly IReadOnlyDictionary<byte, string> Services = new Dictionary<byte, string>
    {
        [0x10] = "StartDiagnosticSession", [0x11] = "EcuReset", [0x12] = "ReadFreezeFrameData",
        [0x13] = "ReadDiagnosticTroubleCodes", [0x14] = "ClearDiagnosticInformation",
        [0x17] = "ReadStatusOfDiagnosticTroubleCodes",
        [0x18] = "ReadDiagnosticTroubleCodesByStatus", [0x1A] = "ReadEcuIdentification",
        [0x20] = "StopDiagnosticSession", [0x21] = "ReadDataByLocalIdentifier",
        [0x22] = "ReadDataByCommonIdentifier", [0x23] = "ReadMemoryByAddress",
        [0x25] = "StopTransmittingDataByLocalIdentifier",
        [0x26] = "SetDataRates", [0x27] = "SecurityAccess",
        [0x28] = "DisableNormalMessageTransmission", [0x29] = "EnableNormalMessageTransmission",
        [0x2C] = "DynamicallyDefineLocalIdentifier", [0x2E] = "WriteDataByCommonIdentifier",
        [0x2F] = "InputOutputControlByCommonIdentifier",
        [0x30] = "InputOutputControlByLocalIdentifier", [0x31] = "StartRoutineByLocalIdentifier",
        [0x32] = "StopRoutineByLocalIdentifier", [0x33] = "RequestRoutineResultsByLocalIdentifier",
        [0x34] = "RequestDownload", [0x35] = "RequestUpload", [0x36] = "TransferData",
        [0x37] = "RequestTransferExit", [0x38] = "StartRoutineByAddress",
        [0x39] = "StopRoutineByAddress", [0x3A] = "RequestRoutineResultsByAddress",
        [0x3B] = "WriteDataByLocalIdentifier", [0x3D] = "WriteMemoryByAddress",
        [0x3E] = "TesterPresent", [0x81] = "StartCommunication", [0x82] = "StopCommunication",
        [0x83] = "AccessTimingParameter"
    };

    // Service IDs that exist in KWP2000 but not as a UDS service of the same meaning.
    private static readonly HashSet<byte> KwpOnly =
        [0x12, 0x13, 0x17, 0x18, 0x1A, 0x20, 0x21, 0x25, 0x26, 0x30, 0x32, 0x33, 0x38, 0x39, 0x3A, 0x3B, 0x81, 0x82, 0x83];

    // UDS-only services: their presence in a catalog rules KWP2000 out.
    private static readonly HashSet<byte> UdsOnly = [0x19, 0x24, 0x2A, 0x85, 0x86, 0x87];

    /// <summary>
    /// True when the catalogs hold at least one KWP2000-only service (e.g. 0x1A, 0x21, 0x3B) and no
    /// UDS-only service (0x19, 0x24, 0x2A, 0x85...). Services 0x22/0x2E/0x2F are shared names in both
    /// worlds and are not used as evidence either way.
    /// </summary>
    public static bool IsKwpCatalogSet(IReadOnlyList<DidCatalog> catalogs)
    {
        var kwp = false;
        foreach (var catalog in catalogs)
            foreach (var service in catalog.ServiceDefinitions)
            {
                if (UdsOnly.Contains(service.ServiceId)) return false;
                if (KwpOnly.Contains(service.ServiceId)) kwp = true;
            }
        return kwp;
    }

    public static bool IsKnownRequest(byte serviceId) => Services.ContainsKey(serviceId);

    public static bool IsPositiveResponse(byte serviceId) =>
        serviceId >= 0x40 && serviceId < 0xC0 && Services.ContainsKey((byte)(serviceId - 0x40));

    /// <summary>
    /// Describes a KWP2000 request. Returns null when the service has no KWP-specific layout here,
    /// so the generic fallback can run. Identifier names come only from catalog services that
    /// declare this exact service ID and local identifier.
    /// </summary>
    public static string? DescribeRequest(
        byte sid, byte[] data, IReadOnlyList<DidCatalog> catalogs, out string objectText)
    {
        objectText = string.Empty;
        switch (sid)
        {
            case 0x21 or 0x1A or 0x30 or 0x3B or 0x31 or 0x32 or 0x33 when data.Length >= 2:
                objectText = LocalIdentifierText(sid, data[1], catalogs);
                var verb = sid switch
                {
                    0x21 or 0x1A => "Read", 0x3B => "Write", 0x30 => "Control", 0x31 => "Start",
                    0x32 => "Stop", _ => "Request results of"
                };
                return $"{verb} {objectText}" + (data.Length > 2 ? $" · data {Hex(data, 2)}" : string.Empty);
            case 0x10 when data.Length >= 2:
                objectText = data[1] == 0x81 ? "standard diagnostic mode" : $"diagnostic mode 0x{data[1]:X2}";
                return $"Start {objectText}";
            case 0x18 when data.Length >= 4:
                var group = (data[2] << 8) | data[3];
                objectText = $"status 0x{data[1]:X2}, group 0x{group:X4}";
                return $"Read DTCs by status {objectText}";
            case 0x14 when data.Length >= 3:
                objectText = $"DTC group 0x{(data[1] << 8) | data[2]:X4}";
                return $"Clear {objectText}";
            case 0x27 when data.Length >= 2:
                objectText = $"level 0x{data[1]:X2}";
                return (data[1] & 1) == 1 ? $"Request seed {objectText}" : $"Send key {objectText}";
            case 0x3E:
                return "Tester present";
            case 0x28 or 0x29:
                return Services[sid];
            default:
                return null;
        }
    }

    /// <summary>Describes a positive KWP2000 response (<paramref name="sid"/> is the request SID).</summary>
    public static string? DescribeResponse(byte sid, byte[] data)
    {
        if (data.Length < 1) return null;
        return sid switch
        {
            0x21 or 0x1A or 0x3B or 0x30 when data.Length >= 2 =>
                data.Length > 2 ? $"Local identifier 0x{data[1]:X2} · data {Hex(data, 2)}" : $"Local identifier 0x{data[1]:X2}",
            0x31 or 0x32 or 0x33 when data.Length >= 2 =>
                $"Routine 0x{data[1]:X2} accepted" + (data.Length > 2 ? $" · result {Hex(data, 2)}" : string.Empty),
            0x10 when data.Length >= 2 => $"Diagnostic mode 0x{data[1]:X2} active",
            0x18 when data.Length >= 2 => $"{(data.Length - 2) / 3} DTC record(s) · {Hex(data, 1)}",
            0x14 => "Clear request accepted",
            _ => null
        };
    }

    private static string LocalIdentifierText(byte sid, byte id, IReadOnlyList<DidCatalog> catalogs)
    {
        var names = catalogs.SelectMany(c => c.ServiceDefinitions)
            .Where(s => s.ServiceId == sid && s.Identifier == id)
            .Select(s => !string.IsNullOrWhiteSpace(s.LongName) ? s.LongName : s.Name)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.Ordinal)
            .Take(2).ToArray();
        return names.Length == 1 ? $"local ID 0x{id:X2} · {names[0]}" : $"local ID 0x{id:X2}";
    }

    private static string Hex(byte[] data, int from) =>
        string.Join(' ', data.Skip(from).Select(b => b.ToString("X2")));
}
