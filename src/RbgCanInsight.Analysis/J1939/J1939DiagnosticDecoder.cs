using System.Buffers.Binary;
using RbgCanInsight.Domain;

namespace RbgCanInsight.Analysis;

public enum J1939LampState { Off = 0, On = 1, Reserved = 2, NotAvailable = 3 }

/// <summary>Lamp status: each lamp is a 2-bit field (state byte 1, flash byte 2 of DM1/DM2).</summary>
public sealed record J1939LampStatus(
    J1939LampState MalfunctionIndicator, J1939LampState RedStop,
    J1939LampState AmberWarning, J1939LampState Protect,
    byte RawStatusByte, byte RawFlashByte);

public sealed record J1939Dtc(int Spn, int Fmi, bool ConversionMethod, int OccurrenceCount, string FmiDescription);

public sealed record J1939DiagnosticMessage(
    uint Pgn, J1939LampStatus Lamps, IReadOnlyList<J1939Dtc> Dtcs, bool NoDtcs, int PaddingGroupsIgnored);

public sealed record J1939Name(
    ulong Raw, bool ArbitraryAddressCapable, int IndustryGroup, string IndustryGroupName,
    int VehicleSystemInstance, int VehicleSystem, int Function, int FunctionInstance,
    int EcuInstance, int ManufacturerCode, int IdentityNumber);

public sealed record J1939AddressClaim(byte ClaimedAddress, J1939Name Name);

public static class J1939DiagnosticDecoder
{
    public const uint Dm1Pgn = 0xFECA;
    public const uint Dm2Pgn = 0xFECB;
    public const uint AddressClaimPgn = 0xEE00;

    private static readonly string[] FmiNames =
    [
        "Data valid but above normal operational range - most severe",
        "Data valid but below normal operational range - most severe",
        "Data erratic, intermittent or incorrect",
        "Voltage above normal or shorted to high source",
        "Voltage below normal or shorted to low source",
        "Current below normal or open circuit",
        "Current above normal or grounded circuit",
        "Mechanical system not responding or out of adjustment",
        "Abnormal frequency, pulse width or period",
        "Abnormal update rate",
        "Abnormal rate of change",
        "Root cause not known",
        "Bad intelligent device or component",
        "Out of calibration",
        "Special instructions",
        "Data valid but above normal operating range - least severe",
        "Data valid but above normal operating range - moderately severe",
        "Data valid but below normal operating range - least severe",
        "Data valid but below normal operating range - moderately severe",
        "Received network data in error",
        "Data drifted high",
        "Data drifted low",
        "Reserved (21)", "Reserved (22)", "Reserved (23)", "Reserved (24)",
        "Reserved (25)", "Reserved (26)", "Reserved (27)", "Reserved (28)",
        "Reserved (29)", "Condition exists"
    ];

    public static bool TryDecodeDm1(ReadOnlySpan<byte> payload, out J1939DiagnosticMessage? message) =>
        TryDecode(Dm1Pgn, payload, out message);

    public static bool TryDecodeDm2(ReadOnlySpan<byte> payload, out J1939DiagnosticMessage? message) =>
        TryDecode(Dm2Pgn, payload, out message);

    /// <summary>
    /// Decodes DM1/DM2 per SAE J1939-73: 2 lamp bytes then 4-byte DTC groups. Trailing 0xFF
    /// padding (partial group or whole 0xFFFFFFFF group) is ignored; any other trailing bytes
    /// make the payload invalid (returns false) rather than being guessed.
    /// </summary>
    public static bool TryDecode(uint pgn, ReadOnlySpan<byte> payload, out J1939DiagnosticMessage? message)
    {
        message = null;
        if (pgn != Dm1Pgn && pgn != Dm2Pgn) return false;
        if (payload.Length < 2) return false;
        var rest = payload[2..];
        var full = rest.Length / 4;
        var tail = rest[(full * 4)..];
        foreach (var b in tail) if (b != 0xFF) return false;

        var dtcs = new List<J1939Dtc>();
        var padding = 0;
        for (var g = 0; g < full; g++)
        {
            var d = rest.Slice(g * 4, 4);
            if (d[0] == 0xFF && d[1] == 0xFF && d[2] == 0xFF && d[3] == 0xFF) { padding++; continue; }
            var spn = d[0] | (d[1] << 8) | ((d[2] >> 5) << 16);
            var fmi = d[2] & 0x1F;
            dtcs.Add(new J1939Dtc(spn, fmi, (d[3] & 0x80) != 0, d[3] & 0x7F, FmiNames[fmi]));
        }
        // "No DTCs": a single SPN 0 / FMI 0 / OC 0 group.
        var none = dtcs.Count == 1 && dtcs[0] is { Spn: 0, Fmi: 0, OccurrenceCount: 0, ConversionMethod: false };
        if (none) dtcs.Clear();

        var s = payload[0];
        var fl = payload[1];
        var lamps = new J1939LampStatus(
            (J1939LampState)((s >> 6) & 3), (J1939LampState)((s >> 4) & 3),
            (J1939LampState)((s >> 2) & 3), (J1939LampState)(s & 3), s, fl);
        message = new J1939DiagnosticMessage(pgn, lamps, dtcs, none, padding);
        return true;
    }

    /// <summary>Decodes an 8-byte NAME (little-endian) per J1939-81.</summary>
    public static bool TryDecodeName(ReadOnlySpan<byte> payload, out J1939Name? name)
    {
        name = null;
        if (payload.Length != 8) return false;
        var v = BinaryPrimitives.ReadUInt64LittleEndian(payload);
        var industry = (int)((v >> 60) & 7);
        name = new J1939Name(v, (v >> 63) != 0, industry, IndustryName(industry),
            (int)((v >> 56) & 0xF), (int)((v >> 49) & 0x7F), (int)((v >> 40) & 0xFF),
            (int)((v >> 35) & 0x1F), (int)((v >> 32) & 7), (int)((v >> 21) & 0x7FF), (int)(v & 0x1FFFFF));
        return true;
    }

    public static J1939Name? DecodeAddressClaim(ReadOnlySpan<byte> payload) =>
        TryDecodeName(payload, out var n) ? n : null;

    /// <summary>Decodes an address-claim frame (PGN 0xEE00); the claimed address is the frame's source address.</summary>
    public static J1939AddressClaim? DecodeAddressClaim(CanFrame frame)
    {
        if (!frame.IsExtended || frame.Flags.HasFlag(CanFrameFlags.Remote) || frame.IsError ||
            RbgCanInsight.Dbc.J1939.ExtractPgn(frame.Identifier) != AddressClaimPgn)
            return null;
        return TryDecodeName(frame.GetPayload(), out var n) ? new J1939AddressClaim((byte)(frame.Identifier & 0xFF), n!) : null;
    }

    private static string IndustryName(int g) => g switch
    {
        0 => "Global", 1 => "On-Highway", 2 => "Agricultural and Forestry", 3 => "Construction",
        4 => "Marine", 5 => "Industrial-Process Control", _ => "Reserved"
    };
}
