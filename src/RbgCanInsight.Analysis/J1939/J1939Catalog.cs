namespace RbgCanInsight.Analysis;

/// <summary>Name of a well-known J1939 parameter group. <see cref="Acronym"/> is the SAE J1939 label.</summary>
public sealed record J1939PgnInfo(uint Pgn, string Acronym, string Name, bool IsProprietary = false);

/// <summary>Compact built-in catalog of well-known PGN names (evidence-only labels, never used to rewrite
/// user data). PGNs not listed are simply unknown: the catalog never guesses a name.</summary>
public static class J1939Catalog
{
    private static readonly Dictionary<uint, J1939PgnInfo> Known = Build();

    public static IReadOnlyCollection<J1939PgnInfo> All => Known.Values;

    /// <summary>Normalises a PDU1 PGN (PF &lt; 240) by clearing the destination-address byte, so that
    /// a PGN taken from a raw 29-bit identifier (e.g. 0xEA2A) matches the catalog entry 0xEA00.</summary>
    public static uint Normalize(uint pgn)
    {
        pgn &= 0x3FFFF;
        return ((pgn >> 8) & 0xFF) < 240 ? pgn & 0x3FF00 : pgn;
    }

    /// <summary>Returns the catalog entry for <paramref name="pgn"/>, or null when unknown. Proprietary B/B2
    /// (0xFF00-0xFFFF, 0x1FF00-0x1FFFF) are recognised as ranges; Proprietary A/A2 are single PGNs.</summary>
    public static J1939PgnInfo? TryDescribe(uint pgn)
    {
        pgn = Normalize(pgn);
        if (Known.TryGetValue(pgn, out var info)) return info;
        if (pgn is >= 0xFF00 and <= 0xFFFF)
            return new J1939PgnInfo(pgn, "PropB", "Proprietary B (manufacturer specific)", true);
        if (pgn is >= 0x1FF00 and <= 0x1FFFF)
            return new J1939PgnInfo(pgn, "PropB2", "Proprietary B2 (manufacturer specific, data page 1)", true);
        return null;
    }

    private static Dictionary<uint, J1939PgnInfo> Build()
    {
        var list = new List<J1939PgnInfo>();
        void Add(uint pgn, string acr, string name, bool prop = false) => list.Add(new(pgn, acr, name, prop));
        Add(0, "TSC1", "Torque/Speed Control 1");
        Add(59392, "ACK", "Acknowledgment");
        Add(59904, "Request", "Request");
        Add(60160, "TP.DT", "Transport Protocol - Data Transfer");
        Add(60416, "TP.CM", "Transport Protocol - Connection Management");
        Add(60928, "AC", "Address Claimed / Cannot Claim");
        Add(61184, "PropA", "Proprietary A (manufacturer specific)", true);
        Add(126720, "PropA2", "Proprietary A2 (manufacturer specific, data page 1)", true);
        Add(61442, "ETC1", "Electronic Transmission Controller 1");
        Add(61443, "EEC2", "Electronic Engine Controller 2");
        Add(61444, "EEC1", "Electronic Engine Controller 1");
        Add(61445, "ETC2", "Electronic Transmission Controller 2");
        Add(65132, "TCO1", "Tachograph");
        Add(65226, "DM1", "Active Diagnostic Trouble Codes");
        Add(65227, "DM2", "Previously Active Diagnostic Trouble Codes");
        Add(65228, "DM3", "Diagnostic Data Clear/Reset of Previously Active DTCs");
        Add(65229, "DM4", "Freeze Frame Parameters");
        Add(65230, "DM5", "Diagnostic Readiness 1");
        Add(65231, "DM6", "Emission Related Pending DTCs");
        Add(58112, "DM7", "Command Non-Continuously Monitored Test");
        Add(65232, "DM8", "Test Results for Non-Continuously Monitored Systems");
        Add(65235, "DM11", "Diagnostic Data Clear/Reset for Active DTCs");
        Add(65236, "DM12", "Emissions Related Active DTCs");
        Add(57088, "DM13", "Stop Start Broadcast");
        Add(55552, "DM14", "Memory Access Request");
        Add(55296, "DM15", "Memory Access Response");
        Add(55040, "DM16", "Binary Data Transfer");
        Add(54016, "DM19", "Calibration Information");
        Add(49664, "DM20", "Monitor Performance Ratio");
        Add(49408, "DM21", "Diagnostic Readiness 2");
        Add(49920, "DM22", "Individual Clear/Reset of Active and Previously Active DTC");
        Add(64949, "DM23", "Emission Related Previously Active DTCs");
        Add(64950, "DM24", "SPN Support");
        Add(64951, "DM25", "Expanded Freeze Frame");
        Add(64952, "DM26", "Diagnostic Readiness 3");
        Add(64898, "DM27", "All Pending DTCs");
        Add(64965, "ECUID", "ECU Identification Information");
        Add(65242, "SOFT", "Software Identification");
        Add(65247, "EEC3", "Electronic Engine Controller 3");
        Add(65248, "VD", "Vehicle Distance");
        Add(65253, "HOURS", "Engine Hours, Revolutions");
        Add(65259, "CI", "Component Identification");
        Add(65260, "VI", "Vehicle Identification");
        Add(65262, "ET1", "Engine Temperature 1");
        Add(65263, "EFL/P1", "Engine Fluid Level/Pressure 1");
        Add(65265, "CCVS", "Cruise Control/Vehicle Speed");
        Add(65266, "LFE", "Fuel Economy (Liquid)");
        Add(65269, "AMB", "Ambient Conditions");
        Add(65270, "IC1", "Intake/Exhaust Conditions 1");
        Add(65271, "VEP1", "Vehicle Electrical Power 1");
        return list.ToDictionary(x => x.Pgn);
    }
}
