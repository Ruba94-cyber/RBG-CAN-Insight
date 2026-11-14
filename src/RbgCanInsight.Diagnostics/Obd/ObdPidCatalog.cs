namespace RbgCanInsight.Diagnostics.Obd;

public enum ObdValueKind
{
    Numeric,
    SupportedPids,
    MonitorStatus,
    FuelSystemStatus,
    DtcCode,
    Ascii,
    Composite,
    CalibrationVerification,
    PerformanceTracking
}

/// <summary>Static description of one standard PID (SAE J1979 formula, unit, byte count).</summary>
public sealed record ObdPidDefinition(
    byte Pid,
    string Name,
    int Length,
    string Unit,
    string Formula,
    ObdValueKind Kind,
    Func<byte[], double>? Convert = null,
    Func<byte[], string>? Describe = null);

public static class ObdPidCatalog
{
    private static readonly Dictionary<byte, ObdPidDefinition> Current = Build();
    private static readonly ObdPidDefinition FreezeFrameDtc = new(
        0x02, "DTC that caused freeze frame", 2, "", "two-byte DTC", ObdValueKind.DtcCode);

    private static readonly Dictionary<byte, ObdPidDefinition> VehicleInfo = new()
    {
        [0x00] = new(0x00, "Supported mode 09 PIDs [01-20]", 4, "", "bit-encoded", ObdValueKind.SupportedPids),
        [0x02] = new(0x02, "Vehicle Identification Number", 17, "", "ASCII", ObdValueKind.Ascii),
        [0x04] = new(0x04, "Calibration IDs", 16, "", "ASCII, 16 bytes each", ObdValueKind.Ascii),
        [0x06] = new(0x06, "Calibration verification numbers (CVN)", 4, "", "4 bytes each, shown as hex",
            ObdValueKind.CalibrationVerification),
        [0x08] = new(0x08, "In-use performance tracking (spark ignition)", 2, "", "16-bit big-endian items",
            ObdValueKind.PerformanceTracking),
        [0x0B] = new(0x0B, "In-use performance tracking (compression ignition)", 2, "", "16-bit big-endian items",
            ObdValueKind.PerformanceTracking),
        [0x0A] = new(0x0A, "ECU name", 20, "", "ASCII", ObdValueKind.Ascii)
    };

    /// <summary>Looks up a PID for mode 01 / 02 (current / freeze frame data) or 09 (vehicle info).
    /// Modes without PIDs (03, 04, 07, 0A) always return false.</summary>
    public static bool TryGet(byte mode, byte pid, out ObdPidDefinition definition)
    {
        switch (mode)
        {
            case 0x01:
                return Current.TryGetValue(pid, out definition!);
            case 0x02:
                if (pid == 0x02)
                {
                    definition = FreezeFrameDtc;
                    return true;
                }
                return Current.TryGetValue(pid, out definition!);
            case 0x09:
                return VehicleInfo.TryGetValue(pid, out definition!);
            default:
                definition = null!;
                return false;
        }
    }

    public static IReadOnlyCollection<ObdPidDefinition> CurrentDataPids => Current.Values;

    public static string ModeName(byte mode) => mode switch
    {
        0x01 => "Current powertrain data",
        0x02 => "Freeze frame data",
        0x03 => "Stored DTCs",
        0x04 => "Clear DTCs and stored values",
        0x07 => "Pending DTCs",
        0x09 => "Vehicle information",
        0x0A => "Permanent DTCs",
        _ => $"Mode 0x{mode:X2}"
    };

    public static string NegativeResponseName(byte nrc) => nrc switch
    {
        0x10 => "generalReject",
        0x11 => "serviceNotSupported",
        0x12 => "subFunctionNotSupported",
        0x13 => "incorrectMessageLengthOrInvalidFormat",
        0x14 => "responseTooLong",
        0x21 => "busyRepeatRequest",
        0x22 => "conditionsNotCorrect",
        0x24 => "requestSequenceError",
        0x31 => "requestOutOfRange",
        0x33 => "securityAccessDenied",
        0x35 => "invalidKey",
        0x78 => "requestCorrectlyReceived-ResponsePending",
        0x7E => "subFunctionNotSupportedInActiveSession",
        0x7F => "serviceNotSupportedInActiveSession",
        _ => $"unknown (0x{nrc:X2})"
    };

    private static Dictionary<byte, ObdPidDefinition> Build()
    {
        var list = new List<ObdPidDefinition>();
        foreach (var pid in new byte[] { 0x00, 0x20, 0x40, 0x60, 0x80, 0xA0, 0xC0 })
            list.Add(new(pid, $"Supported PIDs [{pid + 1:X2}-{pid + 0x20:X2}]", 4, "",
                "bit-encoded, A.7 = PID+1 ... D.0 = PID+0x20", ObdValueKind.SupportedPids));

        static double U16(byte[] d) => d[0] * 256.0 + d[1];
        void Num(byte pid, string name, int len, string unit, string formula, Func<byte[], double> f) =>
            list.Add(new(pid, name, len, unit, formula, ObdValueKind.Numeric, f));

        list.Add(new(0x01, "Monitor status since DTCs cleared", 4, "", "A.7 MIL, A.0-6 DTC count",
            ObdValueKind.MonitorStatus));
        list.Add(new(0x03, "Fuel system status", 2, "", "enumerated per system", ObdValueKind.FuelSystemStatus));
        Num(0x04, "Calculated engine load", 1, "%", "100*A/255", d => 100.0 * d[0] / 255);
        Num(0x05, "Engine coolant temperature", 1, "°C", "A-40", d => d[0] - 40);
        Num(0x06, "Short term fuel trim - bank 1", 1, "%", "(A-128)*100/128", d => (d[0] - 128) * 100.0 / 128);
        Num(0x07, "Long term fuel trim - bank 1", 1, "%", "(A-128)*100/128", d => (d[0] - 128) * 100.0 / 128);
        Num(0x08, "Short term fuel trim - bank 2", 1, "%", "(A-128)*100/128", d => (d[0] - 128) * 100.0 / 128);
        Num(0x09, "Long term fuel trim - bank 2", 1, "%", "(A-128)*100/128", d => (d[0] - 128) * 100.0 / 128);
        Num(0x0A, "Fuel pressure (gauge)", 1, "kPa", "3*A", d => 3.0 * d[0]);
        Num(0x0B, "Intake manifold absolute pressure", 1, "kPa", "A", d => d[0]);
        Num(0x0C, "Engine speed", 2, "rpm", "(256*A+B)/4", d => U16(d) / 4);
        Num(0x0D, "Vehicle speed", 1, "km/h", "A", d => d[0]);
        Num(0x0E, "Timing advance (cyl. 1)", 1, "° before TDC", "A/2-64", d => d[0] / 2.0 - 64);
        Num(0x0F, "Intake air temperature", 1, "°C", "A-40", d => d[0] - 40);
        Num(0x10, "Mass air flow rate", 2, "g/s", "(256*A+B)/100", d => U16(d) / 100);
        Num(0x11, "Throttle position", 1, "%", "100*A/255", d => 100.0 * d[0] / 255);
        Num(0x1F, "Run time since engine start", 2, "s", "256*A+B", U16);
        Num(0x21, "Distance traveled with MIL on", 2, "km", "256*A+B", U16);
        Num(0x2F, "Fuel tank level input", 1, "%", "100*A/255", d => 100.0 * d[0] / 255);
        Num(0x31, "Distance since DTCs cleared", 2, "km", "256*A+B", U16);
        Num(0x33, "Absolute barometric pressure", 1, "kPa", "A", d => d[0]);
        Num(0x42, "Control module voltage", 2, "V", "(256*A+B)/1000", d => U16(d) / 1000);
        Num(0x43, "Absolute load value", 2, "%", "100/255*(256*A+B)", d => 100.0 / 255 * U16(d));
        Num(0x45, "Relative throttle position", 1, "%", "100*A/255", d => 100.0 * d[0] / 255);
        Num(0x46, "Ambient air temperature", 1, "°C", "A-40", d => d[0] - 40);
        Num(0x49, "Accelerator pedal position D", 1, "%", "100*A/255", d => 100.0 * d[0] / 255);
        Num(0x4A, "Accelerator pedal position E", 1, "%", "100*A/255", d => 100.0 * d[0] / 255);
        // 0x4F / 0x50 / 0x5D per SAE J1979. 0x5F (emission requirements) is an enumeration and is
        // intentionally NOT decoded: it stays an unknown PID with raw bytes.
        list.Add(new(0x4F, "Maximum values (equivalence ratio, O2 voltage, O2 current, MAP)", 4, "",
            "A; B V; C mA; D*10 kPa", ObdValueKind.Composite, null,
            d => $"max equivalence ratio {d[0]}, max O2 sensor voltage {d[1]} V, " +
                 $"max O2 sensor current {d[2]} mA, max intake MAP {d[3] * 10} kPa"));
        list.Add(new(0x50, "Maximum value for air flow rate from MAF sensor", 4, "g/s", "A*10 (B-D reserved)",
            ObdValueKind.Numeric, d => d[0] * 10.0));
        Num(0x5D, "Fuel injection timing", 2, "°", "(256*A+B)/128-210", d => U16(d) / 128.0 - 210);
        Num(0x5C, "Engine oil temperature", 1, "°C", "A-40", d => d[0] - 40);
        Num(0x5E, "Engine fuel rate", 2, "L/h", "(256*A+B)/20", d => U16(d) / 20);
        return list.ToDictionary(d => d.Pid);
    }
}
