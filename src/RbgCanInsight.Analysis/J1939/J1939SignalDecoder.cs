using System.Globalization;
using System.Text;

namespace RbgCanInsight.Analysis;

/// <summary>Validity class of a raw J1939 value (SAE J1939-71 / -21 conventions).</summary>
public enum J1939ValueState
{
    Valid,
    /// <summary>Reserved range (0xFB..0xFD in a byte, or in the high byte of 16-bit values).</summary>
    Reserved,
    /// <summary>Error indicator: all-ones minus one (0xFE, 0xFExx, 2-bit 10b, 4-bit 0xE).</summary>
    Error,
    /// <summary>Not available: all ones (0xFF, 0xFFxx, 11b, 0xF).</summary>
    NotAvailable
}

/// <summary>One decoded parameter. <see cref="Value"/> is null unless <see cref="State"/> is Valid and
/// the parameter is numeric.</summary>
public sealed record J1939SignalValue(
    int? Spn, string Name, double? Value, string Unit, J1939ValueState State, uint Raw, string Text);

/// <summary>Decodes a handful of well-known J1939 messages. Only parameters whose layout and scaling are
/// certain per SAE J1939-71 are listed; everything else is intentionally left undecoded.</summary>
public static class J1939SignalDecoder
{
    private sealed record Sig(int Spn, string Name, string Unit, int Byte, int Bit, int Bits,
        double Scale = 1, double Offset = 0, string[]? Labels = null);

    private static readonly Dictionary<uint, Sig[]> Layouts = new()
    {
        [61444] =
        [
            new(899, "Engine Torque Mode", "", 0, 0, 4),
            new(512, "Driver's Demand Engine - Percent Torque", "%", 1, 0, 8, 1, -125),
            new(513, "Actual Engine - Percent Torque", "%", 2, 0, 8, 1, -125),
            new(190, "Engine Speed", "rpm", 3, 0, 16, 0.125),
            new(1483, "Source Address of Controlling Device for Engine Control", "", 5, 0, 8),
            new(2432, "Engine Demand - Percent Torque", "%", 7, 0, 8, 1, -125),
        ],
        [65265] =
        [
            new(70, "Parking Brake Switch", "", 0, 2, 2, Labels: ["not set", "set"]),
            new(84, "Wheel-Based Vehicle Speed", "km/h", 1, 0, 16, 1.0 / 256),
            new(595, "Cruise Control Active", "", 3, 0, 2, Labels: ["switched off", "switched on"]),
            new(597, "Brake Switch", "", 3, 4, 2, Labels: ["pedal released", "pedal depressed"]),
            new(86, "Cruise Control Set Speed", "km/h", 4, 0, 8),
        ],
        [65262] =
        [
            new(110, "Engine Coolant Temperature", "°C", 0, 0, 8, 1, -40),
            new(174, "Engine Fuel Temperature 1", "°C", 1, 0, 8, 1, -40),
            new(175, "Engine Oil Temperature 1", "°C", 2, 0, 16, 0.03125, -273),
            new(176, "Engine Turbocharger Oil Temperature", "°C", 4, 0, 16, 0.03125, -273),
            new(52, "Engine Intercooler Temperature", "°C", 6, 0, 8, 1, -40),
            new(1134, "Engine Intercooler Thermostat Opening", "%", 7, 0, 8, 0.4),
        ],
        [65266] =
        [
            new(183, "Engine Fuel Rate", "L/h", 0, 0, 16, 0.05),
            new(184, "Engine Instantaneous Fuel Economy", "km/L", 2, 0, 16, 1.0 / 512),
            new(185, "Engine Average Fuel Economy", "km/L", 4, 0, 16, 1.0 / 512),
            new(51, "Engine Throttle Valve 1 Position", "%", 6, 0, 8, 0.4),
        ],
        [65263] =
        [
            new(94, "Engine Fuel Delivery Pressure", "kPa", 0, 0, 8, 4),
            new(22, "Engine Extended Crankcase Blow-by Pressure", "kPa", 1, 0, 8, 0.05),
            new(98, "Engine Oil Level", "%", 2, 0, 8, 0.4),
            new(100, "Engine Oil Pressure", "kPa", 3, 0, 8, 4),
            new(101, "Engine Crankcase Pressure", "kPa", 4, 0, 16, 1.0 / 128, -250),
            new(109, "Engine Coolant Pressure", "kPa", 6, 0, 8, 2),
            new(111, "Engine Coolant Level", "%", 7, 0, 8, 0.4),
        ],
    };

    /// <summary>Decodes <paramref name="payload"/> for <paramref name="pgn"/>. Unknown PGNs return an empty list.
    /// Parameters whose bytes lie beyond the payload end are omitted (no guessing).</summary>
    public static IReadOnlyList<J1939SignalValue> Decode(uint pgn, ReadOnlySpan<byte> payload)
    {
        pgn = J1939Catalog.Normalize(pgn);
        if (pgn == 59904) return DecodeRequest(payload);
        if (pgn == 59392) return DecodeAck(payload);
        if (pgn == 60928) return DecodeAddressClaimed(payload);
        if (pgn is 65242 or 65259 or 65260 or 64965) return DecodeStrings(pgn, payload);
        if (!Layouts.TryGetValue(pgn, out var sigs)) return [];
        var result = new List<J1939SignalValue>();
        foreach (var s in sigs)
        {
            var nbytes = s.Bits == 16 ? 2 : 1;
            if (s.Byte + nbytes > payload.Length) continue;
            uint raw = s.Bits == 16 ? (uint)(payload[s.Byte] | payload[s.Byte + 1] << 8)
                : ((uint)payload[s.Byte] >> s.Bit) & ((1u << s.Bits) - 1);
            result.Add(Make(s, raw));
        }
        return result;
    }

    /// <summary>Classifies a raw value of <paramref name="bits"/> width (2, 4, 8 or 16).</summary>
    public static J1939ValueState Classify(uint raw, int bits)
    {
        if (bits >= 8)
        {
            var top = bits == 16 ? raw >> 8 : raw;
            return top switch
            {
                0xFF => J1939ValueState.NotAvailable,
                0xFE => J1939ValueState.Error,
                >= 0xFB => J1939ValueState.Reserved,
                _ => J1939ValueState.Valid
            };
        }
        var max = (1u << bits) - 1;
        return raw == max ? J1939ValueState.NotAvailable : raw == max - 1 ? J1939ValueState.Error : J1939ValueState.Valid;
    }

    private static J1939SignalValue Make(Sig s, uint raw)
    {
        var state = Classify(raw, s.Bits);
        if (s.Labels is not null)
        {
            // Status switches: 00/01 defined, 10 error, 11 not available.
            var text = state switch
            {
                J1939ValueState.NotAvailable => "not available",
                J1939ValueState.Error => "error",
                _ => raw < s.Labels.Length ? s.Labels[raw] : $"reserved ({raw})"
            };
            return new(s.Spn, s.Name, state == J1939ValueState.Valid && raw < s.Labels.Length ? raw : null, "",
                state, raw, text);
        }
        if (state != J1939ValueState.Valid)
            return new(s.Spn, s.Name, null, s.Unit, state, raw, StateText(state));
        var v = raw * s.Scale + s.Offset;
        return new(s.Spn, s.Name, v, s.Unit, state, raw,
            v.ToString("0.###", CultureInfo.InvariantCulture) + (s.Unit.Length > 0 ? " " + s.Unit : ""));
    }

    private static string StateText(J1939ValueState st) => st switch
    {
        J1939ValueState.NotAvailable => "not available",
        J1939ValueState.Error => "error",
        J1939ValueState.Reserved => "reserved",
        _ => ""
    };

    private static string PgnText(uint pgn)
    {
        var info = J1939Catalog.TryDescribe(pgn);
        return $"{pgn} (0x{pgn:X})" + (info is null ? "" : " " + info.Acronym + " - " + info.Name);
    }

    /// <summary>Request (PGN 59904): 3 bytes, requested PGN little-endian.</summary>
    private static List<J1939SignalValue> DecodeRequest(ReadOnlySpan<byte> p)
    {
        var r = new List<J1939SignalValue>();
        if (p.Length < 3) return r;
        var pgn = (uint)(p[0] | p[1] << 8 | p[2] << 16);
        r.Add(new(null, "Requested PGN", pgn, "", J1939ValueState.Valid, pgn, PgnText(pgn)));
        return r;
    }

    /// <summary>Acknowledgment (PGN 59392): control byte, group function value, address acknowledged
    /// (byte 5) and the PGN being acknowledged (bytes 6-8, little-endian) per J1939-21.</summary>
    private static List<J1939SignalValue> DecodeAck(ReadOnlySpan<byte> p)
    {
        var r = new List<J1939SignalValue>();
        if (p.Length < 1) return r;
        var control = p[0];
        var ctl = control switch
        {
            0 => "positive acknowledgment (ACK)",
            1 => "negative acknowledgment (NACK)",
            2 => "access denied",
            3 => "cannot respond",
            _ => "reserved"
        };
        r.Add(new(null, "Control Byte", control, "", J1939ValueState.Valid, control, ctl));
        if (p.Length >= 2)
            r.Add(new(null, "Group Function Value", p[1] == 0xFF ? null : p[1], "",
                p[1] == 0xFF ? J1939ValueState.NotAvailable : J1939ValueState.Valid, p[1],
                p[1] == 0xFF ? "not available" : p[1].ToString(CultureInfo.InvariantCulture)));
        if (p.Length >= 5)
            r.Add(new(null, "Address Acknowledged", p[4], "", J1939ValueState.Valid, p[4], $"0x{p[4]:X2}"));
        if (p.Length >= 8)
        {
            var pgn = (uint)(p[5] | p[6] << 8 | p[7] << 16);
            r.Add(new(null, "Acknowledged PGN", pgn, "", J1939ValueState.Valid, pgn, PgnText(pgn)));
        }
        return r;
    }

    private static List<J1939SignalValue> DecodeAddressClaimed(ReadOnlySpan<byte> p)
    {
        var r = new List<J1939SignalValue>();
        if (!J1939DiagnosticDecoder.TryDecodeName(p, out var n) || n is null) return r;
        void Add(string name, long v, string text) =>
            r.Add(new(null, name, v, "", J1939ValueState.Valid, (uint)v, text));
        Add("Identity Number", n.IdentityNumber, n.IdentityNumber.ToString(CultureInfo.InvariantCulture));
        Add("Manufacturer Code", n.ManufacturerCode, n.ManufacturerCode.ToString(CultureInfo.InvariantCulture));
        Add("ECU Instance", n.EcuInstance, n.EcuInstance.ToString(CultureInfo.InvariantCulture));
        Add("Function", n.Function, n.Function.ToString(CultureInfo.InvariantCulture));
        Add("Vehicle System", n.VehicleSystem, n.VehicleSystem.ToString(CultureInfo.InvariantCulture));
        Add("Industry Group", n.IndustryGroup, n.IndustryGroupName);
        Add("Arbitrary Address Capable", n.ArbitraryAddressCapable ? 1 : 0, n.ArbitraryAddressCapable ? "yes" : "no");
        return r;
    }

    /// <summary>Component ID, Vehicle ID, ECU ID and Software ID carry '*'-delimited ASCII fields
    /// (Software ID is preceded by a field-count byte). Field meaning beyond the order is not assigned.</summary>
    private static List<J1939SignalValue> DecodeStrings(uint pgn, ReadOnlySpan<byte> p)
    {
        var r = new List<J1939SignalValue>();
        var body = p;
        if (pgn == 65242)
        {
            if (p.Length < 1) return r;
            body = p[1..];
        }
        var sb = new StringBuilder();
        foreach (var b in body)
        {
            if (b is >= 0x20 and <= 0x7E) sb.Append((char)b);
            else if (b is 0xFF or 0) continue;
            else return r; // not text: refuse to interpret
        }
        var fields = sb.ToString().Split('*');
        if (fields.Length > 0 && fields[^1].Length == 0) fields = fields[..^1];
        string[] names = pgn switch
        {
            65259 => ["Make", "Model", "Serial Number", "Unit Number"],
            65260 => ["Vehicle Identification Number"],
            _ => []
        };
        for (var i = 0; i < fields.Length; i++)
            r.Add(new(null, i < names.Length ? names[i] : $"Field {i + 1}", null, "", J1939ValueState.Valid, 0, fields[i]));
        return r;
    }
}
