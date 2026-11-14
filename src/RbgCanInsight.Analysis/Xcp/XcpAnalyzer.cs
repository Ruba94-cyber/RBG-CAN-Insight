using System.Globalization;
using RbgCanInsight.Domain;

namespace RbgCanInsight.Analysis.Xcp;

/// <summary>
/// Offline XCP-on-CAN (ASAM MCD-1 XCP, classic CAN and CAN-FD) and CCP 2.1 decoder for one identifier pair.
/// Commands are paired with their responses; every row references the source frame indices.
/// Unknown bytes are never interpreted. Needs a user-supplied or detected <see cref="XcpEndpoint"/>.
/// </summary>
public static class XcpAnalyzer
{
    private sealed class Pending
    {
        public required byte[] Request;
        public required int Code;
        public required string Name;
        public int? Ctr;
        public required int FrameIndex;
        public required long Start;
        public required int Channel;
        public long LastActivity;
        public List<string> Notes = [];
        public List<int> Frames = [];
    }

    public static XcpAnalysis Analyze(IReadOnlyList<CanFrame> frames, XcpEndpoint endpoint, XcpOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentNullException.ThrowIfNull(endpoint);
        if (endpoint.CommandId == endpoint.ResponseId && endpoint.CommandExtended == endpoint.ResponseExtended)
            throw new ArgumentException("The command and response identifiers must differ.", nameof(endpoint));
        options ??= new XcpOptions();
        var isCcp = endpoint.Protocol == XcpProtocol.Ccp;
        var daqIds = endpoint.DaqIds is { Count: > 0 } ids ? new HashSet<uint>(ids) : [];

        var transactions = new List<XcpTransaction>();
        var daq = new List<XcpDaqPacket>();
        var events = new List<XcpEvent>();
        var pending = new List<Pending>();
        bool? motorola = null;
        var lastTs = long.MinValue;

        void Finish(Pending p, XcpOutcome outcome, byte[]? response, long? respTs, int? respIdx, string respText, int? err, string? errName, string? note)
        {
            if (note is not null) p.Notes.Add(note);
            if (respIdx is { } ri) p.Frames.Add(ri);
            transactions.Add(new XcpTransaction(endpoint.Protocol, p.Channel, p.Start, p.Code, p.Name, p.Request,
                isCcp ? XcpParameterDecoder.DescribeCcpRequest(p.Request) : XcpParameterDecoder.DescribeXcpRequest(p.Request, motorola),
                outcome, response, respText, err, errName, p.Ctr,
                respTs is { } rt ? (rt - p.Start) / 1e6 : null, p.Frames, p.Notes));
        }

        void Expire(long now, bool endOfTrace)
        {
            for (var i = 0; i < pending.Count; i++)
            {
                var p = pending[i];
                var waited = now - p.LastActivity;
                if (!endOfTrace && waited <= options.ResponseTimeoutNanoseconds) continue;
                var note = waited > options.ResponseTimeoutNanoseconds
                    ? $"no response within {options.ResponseTimeoutNanoseconds / 1_000_000} ms"
                    : "trace ended before the response timeout elapsed";
                Finish(p, XcpOutcome.Unanswered, null, null, null, "", null, null, note);
                pending.RemoveAt(i--);
            }
        }

        for (var idx = 0; idx < frames.Count; idx++)
        {
            var f = frames[idx];
            if (f.IsError || f.Flags.HasFlag(CanFrameFlags.Remote)) continue;
            if (endpoint.Channel is { } ch && f.Channel != ch) continue;
            var isCmd = f.Identifier == endpoint.CommandId && f.IsExtended == endpoint.CommandExtended;
            var isRes = f.Identifier == endpoint.ResponseId && f.IsExtended == endpoint.ResponseExtended;
            var isDaq = !isRes && !isCmd && daqIds.Contains(f.Identifier) && f.IsExtended == endpoint.DaqExtended;
            if (!isCmd && !isRes && !isDaq) continue;

            var ts = f.TimestampNanoseconds;
            lastTs = Math.Max(lastTs, ts);
            if (pending.Count > 0) Expire(ts, false);
            var data = f.GetPayload();
            if (data.Length == 0) continue;

            if (isCmd)
            {
                if (isCcp) HandleCcpCommand(f, idx, data);
                else HandleXcpCommand(f, idx, data);
            }
            else if (isCcp) HandleCcpSlave(f, idx, data, isDaq);
            else HandleXcpSlave(f, idx, data, isDaq);
        }

        if (pending.Count > 0) Expire(lastTs == long.MinValue ? 0 : lastTs, true);
        transactions.Sort((a, b) => a.TimestampNanoseconds != b.TimestampNanoseconds
            ? a.TimestampNanoseconds.CompareTo(b.TimestampNanoseconds)
            : a.FrameIndices[0].CompareTo(b.FrameIndices[0]));

        var errors = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var t in transactions)
            if (t.Outcome == XcpOutcome.Negative)
            {
                var key = $"{t.ErrorName ?? "unknown error"} (0x{t.ErrorCode:X2})";
                errors[key] = errors.GetValueOrDefault(key) + 1;
            }

        var analysis = new XcpAnalysis(endpoint, transactions, daq, events, errors, transactions.Count(t => t.Outcome == XcpOutcome.Unanswered));
        return options.A2l is { } a2l ? XcpA2lEnricher.Enrich(analysis, a2l) : analysis;

        // ---------------- XCP ----------------
        void HandleXcpCommand(CanFrame f, int idx, byte[] data)
        {
            var code = data[0];
            if (code < 0xC0)
            {
                // Master-to-slave DTO (stimulation); the PID is the first byte.
                daq.Add(new XcpDaqPacket(XcpProtocol.Xcp, f.Channel, f.TimestampNanoseconds, f.Identifier, code, data[1..], idx, true));
                return;
            }

            foreach (var old in pending.ToArray())
            {
                Finish(old, XcpOutcome.Unanswered, null, null, null, "", null, null, $"superseded by a new command at frame {idx}");
                pending.Remove(old);
            }

            var name = XcpCatalog.XcpCommandName(code);
            var p = new Pending
            {
                Request = data, Code = code, Name = name ?? $"UNKNOWN_0x{code:X2}", FrameIndex = idx, Start = f.TimestampNanoseconds,
                Channel = f.Channel, LastActivity = f.TimestampNanoseconds, Frames = [idx],
            };
            if (name is null) p.Notes.Add("command code is not defined by ASAM MCD-1 XCP; bytes left uninterpreted");
            pending.Add(p);
        }

        void HandleXcpSlave(CanFrame f, int idx, byte[] data, bool isDaqId)
        {
            var pid = data[0];
            if (pid < 0xFC)
            {
                daq.Add(new XcpDaqPacket(XcpProtocol.Xcp, f.Channel, f.TimestampNanoseconds, f.Identifier, pid, data[1..], idx, false));
                return;
            }

            if (isDaqId)
            {
                events.Add(new XcpEvent(XcpProtocol.Xcp, f.Channel, f.TimestampNanoseconds, "Unexpected packet on DAQ id", pid, $"PID 0x{pid:X2}", data, "", idx));
                return;
            }

            var cur = pending.Count > 0 ? pending[0] : null;
            switch (pid)
            {
                case 0xFF:
                    if (cur is null) { Orphan("Orphan response", f, idx, data, 0xFF, "RES"); return; }
                    pending.RemoveAt(0);
                    if (cur.Code == 0xFF) motorola = XcpParameterDecoder.ByteOrderFromConnectResponse(data);
                    else if (cur.Code == 0xFE) motorola = null;
                    Finish(cur, XcpOutcome.Positive, data, f.TimestampNanoseconds, idx, XcpParameterDecoder.DescribeXcpResponse(cur.Code, data, motorola), null, null, null);
                    break;
                case 0xFE:
                    if (data.Length < 2) { Orphan("Malformed ERR packet", f, idx, data, 0xFE, "ERR"); return; }
                    if (cur is null) { Orphan("Orphan error", f, idx, data, data[1], "ERR " + (XcpCatalog.XcpErrorName(data[1]) ?? "unknown")); return; }
                    pending.RemoveAt(0);
                    var en = XcpCatalog.XcpErrorName(data[1]);
                    var note = en is null ? "error code is not defined by ASAM MCD-1 XCP" : null;
                    Finish(cur, XcpOutcome.Negative, data, f.TimestampNanoseconds, idx, "ERR " + (en ?? "unknown") + (data.Length > 2 ? "; extra " + XcpCatalog.Hex(data.AsSpan(2), 8) : ""), data[1], en, note);
                    break;
                case 0xFD:
                    if (data.Length < 2) { Orphan("Malformed EV packet", f, idx, data, 0xFD, "EV"); return; }
                    var evName = XcpCatalog.XcpEventName(data[1]) ?? $"EV 0x{data[1]:X2}";
                    if (data[1] == 0x05 && cur is not null)
                    {
                        cur.LastActivity = f.TimestampNanoseconds;
                        cur.Frames.Add(idx);
                        cur.Notes.Add($"EV_CMD_PENDING at frame {idx}: slave asked to keep waiting");
                        return;
                    }

                    events.Add(new XcpEvent(XcpProtocol.Xcp, f.Channel, f.TimestampNanoseconds, "EV", data[1], evName, data, data.Length > 2 ? "data " + XcpCatalog.Hex(data.AsSpan(2), 8) : "", idx));
                    break;
                default: // 0xFC SERV
                    var code = data.Length > 1 ? data[1] : (byte)0xFF;
                    var text = "";
                    var servName = code == 0 ? "SERV_TEXT" : code == 1 ? "SERV_RESET" : $"SERV 0x{code:X2}";
                    if (code == 0 && data.Length > 2 && data.Skip(2).TakeWhile(b => b != 0).All(b => b is >= 0x20 and < 0x7F))
                        text = new string(data.Skip(2).TakeWhile(b => b != 0).Select(b => (char)b).ToArray());
                    else if (data.Length > 2) text = "data " + XcpCatalog.Hex(data.AsSpan(2), 8);
                    events.Add(new XcpEvent(XcpProtocol.Xcp, f.Channel, f.TimestampNanoseconds, "SERV", data.Length > 1 ? data[1] : null, servName, data, text, idx));
                    break;
            }
        }

        // ---------------- CCP ----------------
        void HandleCcpCommand(CanFrame f, int idx, byte[] data)
        {
            if (data.Length < 2)
            {
                events.Add(new XcpEvent(XcpProtocol.Ccp, f.Channel, f.TimestampNanoseconds, "Malformed CRO", null, "CRO shorter than 2 bytes", data, "", idx));
                return;
            }

            var code = data[0];
            var ctr = data[1];
            foreach (var old in pending.Where(x => x.Ctr == ctr).ToArray())
            {
                Finish(old, XcpOutcome.Unanswered, null, null, null, "", null, null, $"command with the same CTR sent again at frame {idx}");
                pending.Remove(old);
            }

            var name = XcpCatalog.CcpCommandName(code);
            var p = new Pending
            {
                Request = data, Code = code, Name = name ?? $"UNKNOWN_0x{code:X2}", Ctr = ctr, FrameIndex = idx,
                Start = f.TimestampNanoseconds, Channel = f.Channel, LastActivity = f.TimestampNanoseconds, Frames = [idx],
            };
            if (name is null) p.Notes.Add("command code is not defined by CCP 2.1; bytes left uninterpreted");
            if (data.Length != 8) p.Notes.Add($"CRO has {data.Length} bytes (CCP uses 8)");
            pending.Add(p);
        }

        void HandleCcpSlave(CanFrame f, int idx, byte[] data, bool isDaqId)
        {
            var pid = data[0];
            if (pid < 0xFE)
            {
                daq.Add(new XcpDaqPacket(XcpProtocol.Ccp, f.Channel, f.TimestampNanoseconds, f.Identifier, pid, data[1..], idx, false));
                return;
            }

            if (data.Length < 3)
            {
                events.Add(new XcpEvent(XcpProtocol.Ccp, f.Channel, f.TimestampNanoseconds, "Malformed DTO", pid, "DTO shorter than 3 bytes", data, "", idx));
                return;
            }

            var err = data[1];
            var ctr = data[2];
            var errName = XcpCatalog.CcpErrorName(err);
            if (pid == 0xFE)
            {
                events.Add(new XcpEvent(XcpProtocol.Ccp, f.Channel, f.TimestampNanoseconds, "Event message", err,
                    errName ?? $"0x{err:X2}", data, $"CTR {ctr}", idx));
                return;
            }

            var cur = pending.LastOrDefault(x => x.Ctr == ctr);
            if (cur is null)
            {
                events.Add(new XcpEvent(XcpProtocol.Ccp, f.Channel, f.TimestampNanoseconds, "Orphan CRM", err,
                    errName ?? $"0x{err:X2}", data, $"CTR {ctr} matches no pending command", idx));
                return;
            }

            pending.Remove(cur);
            if (err == 0)
                Finish(cur, XcpOutcome.Positive, data, f.TimestampNanoseconds, idx, XcpParameterDecoder.DescribeCcpResponse(cur.Code, data), null, null, null);
            else
                Finish(cur, XcpOutcome.Negative, data, f.TimestampNanoseconds, idx, "CRM " + (errName ?? "unknown error"), err, errName,
                    errName is null ? "error code is not defined by CCP 2.1" : null);
        }

        void Orphan(string kind, CanFrame f, int idx, byte[] data, int code, string name) =>
            events.Add(new XcpEvent(endpoint.Protocol, f.Channel, f.TimestampNanoseconds, kind, code, name, data, "matches no pending command", idx));
    }
}
