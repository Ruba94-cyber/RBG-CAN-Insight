using System.Globalization;
using System.Text;
using RbgCanInsight.Domain;

namespace RbgCanInsight.Diagnostics.Obd;

/// <summary>Offline SAE J1979 / ISO 15765-4 decoder. ISO-TP reassembly is delegated to
/// <see cref="IsoTpReassembler"/>; this class only classifies OBD identifiers, pairs
/// requests with responses and decodes the application payload.</summary>
public static class ObdDecoder
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    private enum Role { None, Request, Response }

    private readonly record struct Classification(Role Role, bool Functional, byte Address);

    private sealed class Open
    {
        public required ObdRequest Request { get; init; }
        public required byte Address { get; init; }
        public required int Channel { get; init; }
        public List<ObdResponse> Responses { get; } = [];
        public HashSet<uint> Final { get; } = [];
    }

    private sealed class Entry
    {
        public ObdRequest? Request;
        public int Channel;
        public List<ObdResponse> Responses = [];
        public byte Mode;
        public byte? Pid;
        public bool Orphan;
    }

    public static IReadOnlyList<ObdTransaction> Decode(
        IReadOnlyList<CanFrame> frames,
        TimeSpan? responseTimeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frames);
        var timeoutNs = (long)(responseTimeout ?? DefaultTimeout).TotalMilliseconds * 1_000_000L;
        var messages = IsoTpReassembler.Reassemble(frames, cancellationToken)
            .OrderBy(m => m.StartTimestampNanoseconds)
            .ThenBy(m => m.FrameIndices.Count > 0 ? m.FrameIndices[0] : int.MaxValue)
            .ToList();

        var entries = new List<Entry>();
        var opens = new List<Open>();

        foreach (var message in messages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var cls = Classify(message);
            if (cls.Role == Role.None || message.Payload.Length == 0)
                continue;

            if (cls.Role == Role.Request)
            {
                if (!TryParseRequest(message, cls, out var request))
                    continue;
                var open = new Open { Request = request, Address = cls.Address, Channel = message.Channel };
                opens.Add(open);
                entries.Add(new Entry
                {
                    Request = request, Channel = message.Channel,
                    Responses = open.Responses, Mode = request.Mode,
                    Pid = request.Pids.Count > 0 ? request.Pids[0] : null
                });
                continue;
            }

            var parsed = ParseResponse(message, cls.Address, out var firstPid);
            if (parsed is null)
                continue;

            Open? match = null;
            for (var i = opens.Count - 1; i >= 0; i--)
            {
                var candidate = opens[i];
                if (candidate.Channel != message.Channel ||
                    candidate.Request.IsExtended != message.IsExtended)
                    continue;
                if (message.StartTimestampNanoseconds - candidate.Request.EndTimestampNanoseconds > timeoutNs)
                    continue;
                if (!candidate.Request.IsFunctional && candidate.Address != cls.Address)
                    continue;
                if (candidate.Request.Mode != parsed.Mode || candidate.Final.Contains(message.Identifier))
                    continue;
                if (!parsed.IsNegative && firstPid is { } pid && candidate.Request.Mode is 1 or 2 or 9 &&
                    !candidate.Request.Pids.Contains(pid))
                    continue;
                match = candidate;
                break;
            }

            if (match is null)
            {
                entries.Add(new Entry
                {
                    Channel = message.Channel, Responses = [WithLatency(parsed, null)],
                    Mode = parsed.Mode, Pid = firstPid, Orphan = true
                });
                continue;
            }

            var latency = (message.StartTimestampNanoseconds - match.Request.EndTimestampNanoseconds) / 1e6;
            match.Responses.Add(WithLatency(parsed, latency));
            if (!parsed.IsPending)
                match.Final.Add(message.Identifier);
        }

        var result = new List<ObdTransaction>(entries.Count);
        foreach (var entry in entries.OrderBy(e => e.Request?.TimestampNanoseconds ??
                                                   e.Responses[0].TimestampNanoseconds))
        {
            var finals = entry.Responses.Where(r => !r.IsPending).ToList();
            var outcome = entry.Orphan ? ObdOutcome.Orphan
                : finals.Any(r => !r.IsNegative) ? ObdOutcome.Positive
                : finals.Count > 0 ? ObdOutcome.Negative
                : ObdOutcome.NoResponse;
            var indices = (entry.Request?.FrameIndices ?? [])
                .Concat(entry.Responses.SelectMany(r => r.FrameIndices))
                .Distinct().Order().ToList();
            var start = entry.Request?.TimestampNanoseconds ?? entry.Responses[0].TimestampNanoseconds;
            var end = entry.Responses.Count > 0
                ? entry.Responses.Max(r => r.EndTimestampNanoseconds)
                : entry.Request!.EndTimestampNanoseconds;
            result.Add(new ObdTransaction(result.Count, entry.Mode, entry.Pid, entry.Request,
                entry.Responses, outcome, indices, start, end));
        }
        return result;
    }

    // ---------------------------------------------------------------- classification

    private static Classification Classify(IsoTpMessage m)
    {
        var id = m.Identifier;
        if (!m.IsExtended)
        {
            if (id == 0x7DF) return new(Role.Request, true, 0);
            if (id is >= 0x7E0 and <= 0x7E7) return new(Role.Request, false, (byte)(id - 0x7E0));
            if (id is >= 0x7E8 and <= 0x7EF) return new(Role.Response, false, (byte)(id - 0x7E8));
            return default;
        }

        if (id == 0x18DB33F1) return new(Role.Request, true, 0x33);
        if ((id & 0xFFFF0000) == 0x18DA0000)
        {
            var target = (byte)(id >> 8);
            var source = (byte)id;
            if (source == 0xF1 && target != 0xF1) return new(Role.Request, false, target);
            if (target == 0xF1 && source != 0xF1) return new(Role.Response, false, source);
        }
        return default;
    }

    private static bool IsObdMode(byte mode) => mode is 0x01 or 0x02 or 0x03 or 0x04 or 0x07 or 0x09 or 0x0A;

    private static bool TryParseRequest(IsoTpMessage m, Classification cls, out ObdRequest request)
    {
        request = null!;
        var p = m.Payload;
        var mode = p[0];
        if (!IsObdMode(mode))
            return false; // e.g. UDS on 0x7E0: not OBD traffic.
        var pids = new List<byte>();
        byte? frame = null;
        switch (mode)
        {
            case 0x01:
            case 0x09:
                if (p.Length < 2) return false;
                pids.AddRange(p.AsSpan(1).ToArray());
                break;
            case 0x02:
                if (p.Length < 3) return false;
                pids.Add(p[1]);
                frame = p[2];
                break;
            default:
                if (p.Length != 1) return false;
                break;
        }
        request = new ObdRequest(m.Identifier, m.IsExtended, cls.Functional, mode, pids, frame, p,
            m.FrameIndices, m.StartTimestampNanoseconds, m.EndTimestampNanoseconds);
        return true;
    }

    // ---------------------------------------------------------------- response parsing

    private static ObdResponse WithLatency(ObdResponse r, double? latency) => r with { LatencyMilliseconds = latency };

    private static ObdResponse? ParseResponse(IsoTpMessage m, byte address, out byte? firstPid)
    {
        firstPid = null;
        var p = m.Payload;
        var sid = p[0];

        ObdResponse Make(byte mode, bool neg, byte? nrc, bool pending, byte? frame,
            List<ObdValue> values, List<ObdDtc> dtcs, string? vin, List<string> cal, string issue) =>
            new(m.Identifier, m.IsExtended, address, mode, neg, nrc,
                nrc is { } n ? ObdPidCatalog.NegativeResponseName(n) : string.Empty, pending, frame,
                values, dtcs, vin, cal, p, m.FrameIndices, m.StartTimestampNanoseconds,
                m.EndTimestampNanoseconds, null, issue);

        if (sid == 0x7F)
        {
            if (p.Length < 3 || !IsObdMode(p[1]))
                return null;
            return Make(p[1], true, p[2], p[2] == 0x78, null, [], [], null, [], m.IsComplete ? "" : m.TransportIssue);
        }
        if (sid < 0x41 || !IsObdMode((byte)(sid - 0x40)))
            return null;
        var mode = (byte)(sid - 0x40);
        if (p.Length > 1 && mode is 1 or 2 or 9)
            firstPid = p[1];

        var values = new List<ObdValue>();
        var dtcs = new List<ObdDtc>();
        var cal = new List<string>();
        string? vin = null;
        byte? freezeFrame = null;
        var issue = m.IsComplete ? string.Empty : $"Incomplete ISO-TP message: {m.TransportIssue}";

        if (m.IsComplete)
        {
            switch (mode)
            {
                case 0x01:
                case 0x02:
                    issue = DecodeData(mode, p, values, dtcs, out freezeFrame);
                    break;
                case 0x03:
                case 0x07:
                case 0x0A:
                    issue = DecodeDtcList(p, dtcs);
                    break;
                case 0x04:
                    if (p.Length > 1) issue = "Unexpected data after positive response to mode 04.";
                    break;
                case 0x09:
                    issue = DecodeVehicleInfo(p, values, ref vin, cal);
                    break;
            }
        }
        return Make(mode, false, null, false, freezeFrame, values, dtcs, vin, cal, issue);
    }

    private static string DecodeData(byte mode, byte[] p, List<ObdValue> values, List<ObdDtc> dtcs,
        out byte? freezeFrame)
    {
        freezeFrame = null;
        var offset = 1;
        if (p.Length < 2)
            return "Response carries no PID.";
        while (offset < p.Length)
        {
            var pid = p[offset];
            var dataStart = offset + 1;
            if (mode == 2)
            {
                if (dataStart >= p.Length)
                    return "Truncated freeze-frame response.";
                freezeFrame = p[dataStart];
                dataStart++;
            }
            var remaining = p.Length - dataStart;
            if (!ObdPidCatalog.TryGet(mode, pid, out var def))
            {
                // Length of an unknown PID is not derivable: keep everything that is left raw.
                values.Add(new ObdValue(pid, $"PID 0x{pid:X2}", false, null, "",
                    Hex(p.AsSpan(dataStart)), p[dataStart..],
                    "Unknown PID: bytes kept raw."));
                return string.Empty;
            }
            if (remaining < def.Length)
            {
                values.Add(new ObdValue(pid, def.Name, true, null, "", Hex(p.AsSpan(dataStart)), p[dataStart..],
                    $"Truncated: expected {def.Length} byte(s), got {remaining}."));
                return "Truncated PID data.";
            }
            var data = p[dataStart..(dataStart + def.Length)];
            var value = DecodeKnown(def, data);
            values.Add(value);
            if (def.Kind == ObdValueKind.DtcCode && !(data[0] == 0 && data[1] == 0))
                dtcs.Add(new ObdDtc(DtcText(data[0], data[1]), data));
            offset = dataStart + def.Length;
        }
        return string.Empty;
    }

    private static ObdValue DecodeKnown(ObdPidDefinition def, byte[] d)
    {
        switch (def.Kind)
        {
            case ObdValueKind.Numeric:
                var v = def.Convert!(d);
                var text = Format(v) + (def.Unit.Length > 0 ? " " + def.Unit : "");
                return new ObdValue(def.Pid, def.Name, true, v, def.Unit, text, d);
            case ObdValueKind.SupportedPids:
                var list = new List<int>();
                for (var i = 0; i < 32; i++)
                    if ((d[i / 8] & (0x80 >> (i % 8))) != 0)
                        list.Add(def.Pid + 1 + i);
                return new ObdValue(def.Pid, def.Name, true, null, "",
                    list.Count == 0 ? "none" : string.Join(", ", list.Select(x => x.ToString("X2"))),
                    d, SupportedPids: list);
            case ObdValueKind.MonitorStatus:
                var status = new ObdMonitorStatus((d[0] & 0x80) != 0, d[0] & 0x7F, (d[1] & 0x08) != 0);
                return new ObdValue(def.Pid, def.Name, true, status.DtcCount, "",
                    $"MIL {(status.MilOn ? "on" : "off")}, {status.DtcCount} DTC(s), " +
                    (status.CompressionIgnition ? "compression ignition" : "spark ignition"),
                    d, Monitor: status, Readiness: DecodeReadiness(d));
            case ObdValueKind.Composite:
                return new ObdValue(def.Pid, def.Name, true, null, "", def.Describe!(d), d);
            case ObdValueKind.FuelSystemStatus:
                return new ObdValue(def.Pid, def.Name, true, null, "",
                    $"System 1: {FuelStatus(d[0])}; System 2: {FuelStatus(d[1])}", d);
            case ObdValueKind.DtcCode:
                return new ObdValue(def.Pid, def.Name, true, null, "",
                    d[0] == 0 && d[1] == 0 ? "no DTC" : DtcText(d[0], d[1]), d);
            default:
                return new ObdValue(def.Pid, def.Name, true, null, "", Hex(d), d);
        }
    }

    /// <summary>SAE J1979 PID 01: B.0-2 continuous monitors supported (misfire, fuel system, components),
    /// B.4-6 the matching "not complete" flags, B.3 ignition type; C = non-continuous supported,
    /// D = non-continuous "not complete".</summary>
    public static ObdReadiness DecodeReadiness(byte[] d)
    {
        var compression = (d[1] & 0x08) != 0;
        var list = new List<ObdReadinessMonitor>();
        string[] cont = ["Misfire", "Fuel system", "Components"];
        for (var i = 0; i < 3; i++)
            list.Add(new(cont[i], (d[1] & (1 << i)) != 0, (d[1] & (0x10 << i)) == 0));
        string?[] spark =
        [
            "Catalyst", "Heated catalyst", "Evaporative system", "Secondary air system",
            "A/C refrigerant", "Oxygen sensor", "Oxygen sensor heater", "EGR / VVT system"
        ];
        string?[] diesel =
        [
            "NMHC catalyst", "NOx/SCR aftertreatment", null, "Boost pressure",
            null, "Exhaust gas sensor", "PM filter", "EGR / VVT system"
        ];
        var names = compression ? diesel : spark;
        for (var i = 0; i < 8; i++)
        {
            if (names[i] is not { } name) continue; // reserved bit
            list.Add(new(name, (d[2] & (1 << i)) != 0, (d[3] & (1 << i)) == 0));
        }
        return new ObdReadiness(compression, list);
    }

    private static string FuelStatus(byte b) => b switch
    {
        0x00 => "not reported",
        0x01 => "open loop (engine temperature too low)",
        0x02 => "closed loop (using O2 sensor)",
        0x04 => "open loop (engine load / fuel cut)",
        0x08 => "open loop (system failure)",
        0x10 => "closed loop (O2 sensor fault)",
        _ => $"unknown (0x{b:X2})"
    };

    /// <summary>Mode 03/07/0A. ISO 15765-4 puts a DTC-count byte first; some tools omit it, so
    /// an odd remaining length means "count byte present", an even one means "pairs only".</summary>
    private static string DecodeDtcList(byte[] p, List<ObdDtc> dtcs)
    {
        var start = 1;
        string issue = string.Empty;
        if ((p.Length - 1) % 2 == 1)
        {
            start = 2;
            var declared = p[1];
            var available = (p.Length - 2) / 2;
            if (declared != available)
                issue = $"Declared DTC count {declared} differs from {available} code(s) present.";
        }
        for (var i = start; i + 1 < p.Length; i += 2)
        {
            if (p[i] == 0 && p[i + 1] == 0)
                continue; // 0000 is padding / "no DTC", never a code.
            dtcs.Add(new ObdDtc(DtcText(p[i], p[i + 1]), [p[i], p[i + 1]]));
        }
        return issue;
    }

    private static string DecodeVehicleInfo(byte[] p, List<ObdValue> values, ref string? vin, List<string> cal)
    {
        if (p.Length < 2)
            return "Response carries no PID.";
        var pid = p[1];
        var data = p[2..];
        if (!ObdPidCatalog.TryGet(9, pid, out var def))
        {
            values.Add(new ObdValue(pid, $"Mode 09 PID 0x{pid:X2}", false, null, "", Hex(data), data,
                "Unknown PID: bytes kept raw."));
            return string.Empty;
        }
        switch (pid)
        {
            case 0x00:
                if (data.Length < 4)
                {
                    values.Add(new ObdValue(pid, def.Name, true, null, "", Hex(data), data, "Truncated."));
                    return "Truncated PID data.";
                }
                values.Add(DecodeKnown(def, data[..4]));
                return string.Empty;
            case 0x02:
            case 0x0A:
                {
                    var text = data.Length == def.Length + 1 ? data[1..] : data;
                    if (text.Length != def.Length)
                    {
                        values.Add(new ObdValue(pid, def.Name, true, null, "", Hex(data), data,
                            $"Unexpected length {data.Length}, expected {def.Length} (+1 count byte)."));
                        return "Unexpected length.";
                    }
                    if (!TryAscii(text, out var ascii))
                    {
                        values.Add(new ObdValue(pid, def.Name, true, null, "", Hex(data), data,
                            "Non-printable characters; raw bytes kept."));
                        return "Non-ASCII content.";
                    }
                    if (pid == 0x02) vin = ascii;
                    values.Add(new ObdValue(pid, def.Name, true, null, "", ascii, data));
                    return string.Empty;
                }
            case 0x06:
                {
                    // Optional leading count byte (NODI), then 4-byte CVNs.
                    var cvn = data.Length % 4 == 1 ? data[1..] : data;
                    if (cvn.Length == 0 || cvn.Length % 4 != 0)
                    {
                        values.Add(new ObdValue(pid, def.Name, true, null, "", Hex(data), data,
                            $"CVN payload length {data.Length} is not a multiple of 4."));
                        return "Unexpected length.";
                    }
                    var items = new List<ObdNamedCounter>();
                    for (var i = 0; i < cvn.Length; i += 4)
                        items.Add(new($"CVN {i / 4 + 1}",
                            System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(cvn.AsSpan(i)),
                            Hex(cvn.AsSpan(i, 4))));
                    values.Add(new ObdValue(pid, def.Name, true, null, "",
                        string.Join(", ", items.Select(x => x.Text)), data, Counters: items));
                    return string.Empty;
                }
            case 0x08:
            case 0x0B:
                {
                    // Optional leading count byte, then 16-bit items. Only the first two (OBDCOND, IGNCNTR) are
                    // named; per-monitor numerator/denominator names are not assigned (kept as indexed items).
                    var items16 = data.Length % 2 == 1 ? data[1..] : data;
                    if (items16.Length < 4)
                    {
                        values.Add(new ObdValue(pid, def.Name, true, null, "", Hex(data), data,
                            "Too short for OBDCOND/IGNCNTR."));
                        return "Truncated PID data.";
                    }
                    var counters = new List<ObdNamedCounter>();
                    for (var i = 0; i + 1 < items16.Length; i += 2)
                    {
                        var v = (uint)(items16[i] << 8 | items16[i + 1]);
                        var n = i / 2;
                        var nm = n == 0 ? "OBDCOND (monitoring conditions encountered)"
                            : n == 1 ? "IGNCNTR (ignition cycle counter)" : $"Item {n + 1}";
                        counters.Add(new(nm, v, v.ToString(CultureInfo.InvariantCulture)));
                    }
                    values.Add(new ObdValue(pid, def.Name, true, null, "",
                        string.Join(", ", counters.Select(c => c.Value)), data, Counters: counters));
                    return string.Empty;
                }
            default: // 0x04
                {
                    var ids = data.Length % 16 == 1 ? data[1..] : data;
                    var issue = ids.Length % 16 != 0 || ids.Length == 0
                        ? $"Calibration payload length {data.Length} is not a multiple of 16." : string.Empty;
                    var texts = new List<string>();
                    for (var i = 0; i + 16 <= ids.Length; i += 16)
                    {
                        if (TryAscii(ids[i..(i + 16)], out var s)) { cal.Add(s); texts.Add(s); }
                        else issue = "Non-printable calibration ID; raw bytes kept.";
                    }
                    values.Add(new ObdValue(pid, def.Name, true, null, "",
                        texts.Count > 0 ? string.Join(", ", texts) : Hex(data), data, issue));
                    return issue;
                }
        }
    }

    /// <summary>ASCII with trailing NUL padding removed; fails on any other non-printable byte.</summary>
    private static bool TryAscii(byte[] bytes, out string text)
    {
        var end = bytes.Length;
        while (end > 0 && bytes[end - 1] == 0) end--;
        var sb = new StringBuilder(end);
        for (var i = 0; i < end; i++)
        {
            if (bytes[i] is < 0x20 or > 0x7E)
            {
                text = string.Empty;
                return false;
            }
            sb.Append((char)bytes[i]);
        }
        text = sb.ToString();
        return true;
    }

    /// <summary>SAE J1979 DTC: bits 7-6 of A select P/C/B/U, bits 5-4 the first digit, then 3 hex digits.</summary>
    public static string DtcText(byte a, byte b)
    {
        var letter = "PCBU"[a >> 6];
        return $"{letter}{(a >> 4) & 0x3}{a & 0xF:X}{b:X2}";
    }

    private static string Hex(ReadOnlySpan<byte> bytes) => Convert.ToHexString(bytes);

    private static string Format(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
}
