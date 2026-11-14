using System.Globalization;

namespace RbgCanInsight.Analysis.Xcp;

/// <summary>
/// Describes the parameters of well-defined XCP/CCP commands and responses. Multi-byte XCP fields are only
/// converted to numbers when the byte order is known from a CONNECT response in the trace; otherwise
/// the raw bytes are shown. Anything not defined by the spec is shown as raw hex, never interpreted.
/// </summary>
public static class XcpParameterDecoder
{
    /// <summary>Byte order announced by an XCP CONNECT response (COMM_MODE_BASIC bit 0): true = Motorola (big endian).</summary>
    public static bool? ByteOrderFromConnectResponse(ReadOnlySpan<byte> p) =>
        p.Length >= 3 && p[0] == 0xFF ? (p[2] & 1) == 1 : null;

    private static string Raw(ReadOnlySpan<byte> p, int off, int n) =>
        off >= p.Length ? "" : XcpCatalog.Hex(p.Slice(off, Math.Min(n, p.Length - off)));

    private static string Num(ReadOnlySpan<byte> p, int off, int n, bool? motorola)
    {
        if (off + n > p.Length) return "(truncated)";
        if (n == 1) return $"0x{p[off]:X2}";
        if (motorola is null) return "raw " + Raw(p, off, n);
        ulong v = 0;
        for (var i = 0; i < n; i++)
            v = motorola.Value ? (v << 8) | p[off + i] : v | ((ulong)p[off + i] << (8 * i));
        return "0x" + v.ToString(n == 2 ? "X4" : "X8", CultureInfo.InvariantCulture);
    }

    private static string Dec(ReadOnlySpan<byte> p, int off, int n, bool? motorola)
    {
        if (off + n > p.Length) return "(truncated)";
        if (n == 1) return p[off].ToString(CultureInfo.InvariantCulture);
        if (motorola is null) return "raw " + Raw(p, off, n);
        ulong v = 0;
        for (var i = 0; i < n; i++)
            v = motorola.Value ? (v << 8) | p[off + i] : v | ((ulong)p[off + i] << (8 * i));
        return v.ToString(CultureInfo.InvariantCulture);
    }

    private static string Join(params string[] parts) => string.Join("; ", parts.Where(s => s.Length > 0));

    // ---------------- XCP ----------------

    public static string DescribeXcpRequest(ReadOnlySpan<byte> p, bool? mot)
    {
        if (p.Length == 0) return "";
        switch (p[0])
        {
            case 0xFF: return p.Length > 1 ? $"mode {(p[1] == 0 ? "normal" : p[1] == 1 ? "user-defined" : "0x" + p[1].ToString("X2", CultureInfo.InvariantCulture))}" : "(no mode byte)";
            case 0xFA: return p.Length > 1 ? $"identification type {p[1]}" : "";
            case 0xF6: return Join($"ext {Num(p, 3, 1, mot)}", $"address {Num(p, 4, 4, mot)}");
            case 0xF5: return p.Length > 1 ? $"{p[1]} element(s)" : "";
            case 0xF4: return Join(p.Length > 1 ? $"{p[1]} element(s)" : "", $"ext {Num(p, 3, 1, mot)}", $"address {Num(p, 4, 4, mot)}");
            case 0xF3: return $"block size {Dec(p, 4, 4, mot)}";
            case 0xF0:
            case 0xEF:
            case 0xEE:
                return p.Length > 1 ? Join($"{p[1]} element(s)", "data " + Raw(p, 2, 62)) : "";
            case 0xED: return Join(p.Length > 1 ? $"{p[1]} element(s)" : "", $"ext {Num(p, 3, 1, mot)}", $"address {Num(p, 4, 4, mot)}", "data " + Raw(p, 8, 56));
            case 0xEB: return Join($"mode 0x{(p.Length > 1 ? p[1] : 0):X2}", p.Length > 2 ? $"segment {p[2]}" : "", p.Length > 3 ? $"page {p[3]}" : "");
            case 0xEA: return Join($"mode 0x{(p.Length > 1 ? p[1] : 0):X2}", p.Length > 2 ? $"segment {p[2]}" : "");
            case 0xE3: return $"DAQ list {Dec(p, 2, 2, mot)}";
            case 0xE2: return Join($"DAQ list {Dec(p, 2, 2, mot)}", p.Length > 4 ? $"ODT {p[4]}" : "", p.Length > 5 ? $"entry {p[5]}" : "");
            case 0xE1: return Join(p.Length > 1 ? $"bit offset {p[1]}" : "", p.Length > 2 ? $"size {p[2]}" : "", $"ext {Num(p, 3, 1, mot)}", $"address {Num(p, 4, 4, mot)}");
            case 0xE0:
                return Join($"mode 0x{(p.Length > 1 ? p[1] : 0):X2}", $"DAQ list {Dec(p, 2, 2, mot)}", $"event channel {Dec(p, 4, 2, mot)}",
                    p.Length > 6 ? $"prescaler {p[6]}" : "", p.Length > 7 ? $"priority {p[7]}" : "");
            case 0xDF: return $"DAQ list {Dec(p, 2, 2, mot)}";
            case 0xDE:
                return Join(p.Length > 1 ? p[1] switch { 0 => "stop", 1 => "start", 2 => "select", _ => $"mode 0x{p[1]:X2}" } : "", $"DAQ list {Dec(p, 2, 2, mot)}");
            case 0xDD:
                return p.Length > 1 ? p[1] switch { 0 => "stop all", 1 => "start selected", 2 => "stop selected", _ => $"mode 0x{p[1]:X2}" } : "";
            case 0xD8: return $"DAQ list {Dec(p, 2, 2, mot)}";
            case 0xD7: return $"event channel {Dec(p, 2, 2, mot)}";
            case 0xD5: return $"{Dec(p, 2, 2, mot)} DAQ list(s)";
            case 0xD4: return Join($"DAQ list {Dec(p, 2, 2, mot)}", p.Length > 4 ? $"{p[4]} ODT(s)" : "");
            case 0xD3: return Join($"DAQ list {Dec(p, 2, 2, mot)}", p.Length > 4 ? $"ODT {p[4]}" : "", p.Length > 5 ? $"{p[5]} entr(ies)" : "");
            case 0xF8: return Join($"mode {(p.Length > 1 ? p[1] : 0)}", $"resource 0x{(p.Length > 2 ? p[2] : 0):X2}");
            default: return p.Length > 1 ? "params " + Raw(p, 1, 63) : "";
        }
    }

    public static string DescribeXcpResponse(int commandCode, ReadOnlySpan<byte> p, bool? mot)
    {
        if (p.Length == 0) return "";
        switch (commandCode)
        {
            case 0xFF:
                if (p.Length < 8) return "(short CONNECT response) " + Raw(p, 1, 63);
                var res = p[1];
                var resources = new List<string>();
                if ((res & 0x01) != 0) resources.Add("CAL/PAG");
                if ((res & 0x04) != 0) resources.Add("DAQ");
                if ((res & 0x08) != 0) resources.Add("STIM");
                if ((res & 0x10) != 0) resources.Add("PGM");
                var cm = p[2];
                var bo = (cm & 1) == 1 ? "Motorola" : "Intel";
                var gran = ((cm >> 1) & 3) switch { 0 => "byte", 1 => "word", 2 => "dword", _ => "reserved" };
                var maxDto = ((cm & 1) == 1) ? (p[4] << 8) | p[5] : p[4] | (p[5] << 8);
                return Join("resources " + (resources.Count == 0 ? "none" : string.Join("+", resources)),
                    $"byte order {bo}", $"address granularity {gran}",
                    (cm & 0x40) != 0 ? "slave block mode" : "", (cm & 0x80) != 0 ? "optional comm mode info" : "",
                    $"MAX_CTO {p[3]}", $"MAX_DTO {maxDto}", $"protocol layer {p[6]}", $"transport layer {p[7]}");
            case 0xFD:
                if (p.Length < 6) return Raw(p, 1, 63);
                var st = p[1];
                var flags = new List<string>();
                if ((st & 0x01) != 0) flags.Add("STORE_CAL_REQ");
                if ((st & 0x04) != 0) flags.Add("STORE_DAQ_REQ");
                if ((st & 0x08) != 0) flags.Add("CLEAR_DAQ_REQ");
                if ((st & 0x40) != 0) flags.Add("DAQ_RUNNING");
                if ((st & 0x80) != 0) flags.Add("RESUME");
                return Join("session status " + (flags.Count == 0 ? "idle" : string.Join("+", flags)),
                    $"resource protection 0x{p[2]:X2}", $"session config id {Num(p, 4, 2, mot)}");
            case 0xFB:
                if (p.Length < 8) return Raw(p, 1, 63);
                return Join($"comm mode optional 0x{p[2]:X2}" + ((p[2] & 1) != 0 ? " (master block mode)" : "") + ((p[2] & 2) != 0 ? " (interleaved)" : ""),
                    $"MAX_BS {p[4]}", $"MIN_ST {p[5]}", $"QUEUE_SIZE {p[6]}", $"driver version 0x{p[7]:X2}");
            case 0xFA:
                return p.Length >= 8 ? Join($"mode {p[1]}", $"length {Dec(p, 4, 4, mot)}") : Raw(p, 1, 63);
            case 0xF5:
            case 0xF4:
                return "data " + Raw(p, 1, 63);
            case 0xF3:
                return p.Length >= 8 ? Join($"checksum type 0x{p[1]:X2}", $"checksum {Num(p, 4, 4, mot)}") : Raw(p, 1, 63);
            case 0xEA:
                return p.Length > 3 ? $"page {p[3]}" : "";
            case 0xDE:
                return p.Length > 1 ? $"first PID {p[1]}" : "";
            case 0xF8:
                return p.Length > 1 ? Join($"{p[1]} seed byte(s) remaining", "seed " + Raw(p, 2, 62)) : "";
            case 0xF7:
                return p.Length > 1 ? $"resource protection 0x{p[1]:X2}" : "";
            default:
                return p.Length > 1 ? "data " + Raw(p, 1, 63) : "";
        }
    }

    // ---------------- CCP ----------------

    public static string DescribeCcpRequest(ReadOnlySpan<byte> p)
    {
        if (p.Length < 2) return "";
        var d = p.Length > 2 ? p[2..] : [];
        switch (p[0])
        {
            case 0x01: return d.Length >= 2 ? $"station address 0x{d[0] | (d[1] << 8):X4}" : "";
            case 0x02: return Join(d.Length > 0 ? $"MTA{d[0]}" : "", d.Length > 1 ? $"ext 0x{d[1]:X2}" : "", "address raw " + Raw(d, 2, 4));
            case 0x03: return d.Length > 0 ? Join($"{d[0]} byte(s)", "data " + Raw(d, 1, 5)) : "";
            case 0x04: return d.Length > 0 ? $"{d[0]} byte(s)" : "";
            case 0x06:
                return Join(d.Length > 0 ? d[0] switch { 0 => "stop", 1 => "start", 2 => "prepare", _ => $"mode 0x{d[0]:X2}" } : "",
                    d.Length > 1 ? $"DAQ list {d[1]}" : "", d.Length > 2 ? $"last ODT {d[2]}" : "", d.Length > 3 ? $"event channel {d[3]}" : "",
                    "prescaler raw " + Raw(d, 4, 2));
            case 0x07: return Join(d.Length > 0 ? (d[0] == 0 ? "temporary" : d[0] == 1 ? "end of session" : $"type 0x{d[0]:X2}") : "", d.Length >= 4 ? $"station address 0x{d[2] | (d[3] << 8):X4}" : "");
            case 0x0F: return Join(d.Length > 0 ? $"{d[0]} byte(s)" : "", d.Length > 1 ? $"ext 0x{d[1]:X2}" : "", "address raw " + Raw(d, 2, 4));
            case 0x14: return Join(d.Length > 0 ? $"DAQ list {d[0]}" : "", "DTO id raw " + Raw(d, 2, 4));
            case 0x15: return Join(d.Length > 0 ? $"DAQ list {d[0]}" : "", d.Length > 1 ? $"ODT {d[1]}" : "", d.Length > 2 ? $"element {d[2]}" : "");
            case 0x16: return Join(d.Length > 0 ? $"size {d[0]}" : "", d.Length > 1 ? $"ext 0x{d[1]:X2}" : "", "address raw " + Raw(d, 2, 4));
            case 0x12: return d.Length > 0 ? $"resource 0x{d[0]:X2}" : "";
            case 0x1B: return d.Length >= 2 ? $"master CCP version {d[0]}.{d[1]}" : "";
            default: return "params " + Raw(d, 0, 6);
        }
    }

    public static string DescribeCcpResponse(int commandCode, ReadOnlySpan<byte> p)
    {
        // p = [0xFF, ERR, CTR, data...]
        var d = p.Length > 3 ? p[3..] : [];
        switch (commandCode)
        {
            case 0x1B: return d.Length >= 2 ? $"slave CCP version {d[0]}.{d[1]}" : "";
            case 0x14: return d.Length >= 2 ? Join($"DAQ list size {d[0]}", $"first PID {d[1]}") : "";
            case 0x17: return d.Length >= 4 ? Join($"slave device id length {d[0]}", $"data type 0x{d[1]:X2}", $"resource availability 0x{d[2]:X2}", $"protection 0x{d[3]:X2}") : "";
            case 0x12: return d.Length > 0 ? Join($"protection status 0x{d[0]:X2}", "seed " + Raw(d, 1, 4)) : "";
            case 0x13: return d.Length > 0 ? $"protection status 0x{d[0]:X2}" : "";
            case 0x04:
            case 0x0F: return d.Length > 0 ? "data " + Raw(d, 0, 5) : "";
            case 0x02: return "";
            default: return d.Length > 0 ? "data " + Raw(d, 0, 5) : "";
        }
    }
}
