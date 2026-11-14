using System.Globalization;
using RbgCanInsight.Analysis.Xcp.A2l;

namespace RbgCanInsight.Analysis.Xcp;

/// <summary>
/// Adds A2L knowledge to an already decoded XCP analysis: addresses in SET_MTA / SHORT_UPLOAD / SHORT_DOWNLOAD / WRITE_DAQ are resolved to
/// variable names (the MTA is tracked through UPLOAD/DOWNLOAD), and DAQ packets are decoded with the DAQ configuration found in the trace
/// (ALLOC_DAQ/ALLOC_ODT/ALLOC_ODT_ENTRY/SET_DAQ_PTR/WRITE_DAQ/SET_DAQ_LIST_MODE/START_STOP_DAQ_LIST). Only dynamic DAQ configured inside the
/// trace is decoded; predefined DAQ lists, WRITE_DAQ_MULTIPLE and the CCP protocol are not. Anything uncertain is left as raw bytes.
/// </summary>
public static class XcpA2lEnricher
{
    private sealed class DaqList
    {
        public List<List<Entry>> Odts = [];
        public int? FirstPid;
        public int? Event;
        public bool Timestamp;
    }

    private sealed class Entry
    {
        public bool Set;
        public int Size;
        public int BitOffset = 0xFF;
        public ulong Address;
        public int Ext;
    }

    public static XcpAnalysis Enrich(XcpAnalysis analysis, A2lDatabase a2l)
    {
        if (analysis.Endpoint.Protocol != XcpProtocol.Xcp) return analysis with { A2lNotes = ["A2L enrichment applies to XCP only; CCP is not resolved."] };

        var notes = new List<string>();
        bool? motorola = null;
        var granularity = 1;
        ulong? mta = null; var mtaExt = 0;
        var lists = new Dictionary<int, DaqList>();
        (int List, int Odt, int Entry)? ptr = null;
        var tsSize = 0;
        var decoded = new List<XcpDaqDecodedPacket>();
        var packets = analysis.DaqPackets.Where(p => !p.IsStimulation).OrderBy(p => p.TimestampNanoseconds).ThenBy(p => p.FrameIndex).ToList();
        var pi = 0;
        var undecodable = 0;
        var result = new List<XcpTransaction>(analysis.Transactions.Count);

        ulong? U(byte[] p, int off, int n)
        {
            if (motorola is null || off + n > p.Length) return null;
            ulong v = 0;
            for (var i = 0; i < n; i++) v = motorola.Value ? (v << 8) | p[off + i] : v | ((ulong)p[off + i] << (8 * i));
            return v;
        }

        void Flush(long before)
        {
            while (pi < packets.Count && packets[pi].TimestampNanoseconds < before)
            {
                var d = DecodePacket(packets[pi], lists, a2l, motorola, tsSize);
                if (d is not null) decoded.Add(d); else undecodable++;
                pi++;
            }
        }

        string Describe(ulong addr, int ext)
        {
            var m = a2l.Resolve(addr, ext);
            return m is null ? $"A2L: no symbol at 0x{addr:X}" + (ext != 0 ? $" ext {ext}" : "") : $"A2L: {m.Text}";
        }

        string? Value(ulong addr, int ext, byte[]? data)
        {
            if (data is null || data.Length == 0) return null;
            var m = a2l.Resolve(addr, ext);
            if (m is null || m.Offset != 0 || m.Symbol.SizeBytes <= 0 || m.Symbol.ElementCount != 1 || m.Symbol.SizeBytes > data.Length) return null;
            var text = a2l.FormatValue(m.Symbol, data.AsSpan(0, m.Symbol.SizeBytes), motorola ?? false, out _);
            return text is null ? null : $"{m.Symbol.Name} = {text}";
        }

        foreach (var t in analysis.Transactions)
        {
            Flush(t.TimestampNanoseconds);
            var req = t.Request;
            var ok = t.Outcome == XcpOutcome.Positive;
            var reqNote = new List<string>();
            var resNote = new List<string>();

            switch (t.CommandCode)
            {
                case 0xFF when ok && t.Response is { Length: >= 8 } cr:
                    motorola = XcpParameterDecoder.ByteOrderFromConnectResponse(cr);
                    granularity = ((cr[2] >> 1) & 3) switch { 0 => 1, 1 => 2, 2 => 4, _ => 1 };
                    break;
                case 0xFE when ok:
                    motorola = null; lists.Clear(); mta = null; ptr = null;
                    break;

                case 0xF6: // SET_MTA
                    if (U(req, 4, 4) is { } sa)
                    {
                        reqNote.Add(Describe(sa, req[3]));
                        if (ok) { mta = sa; mtaExt = req[3]; }
                    }
                    else if (motorola is null) reqNote.Add("A2L: byte order unknown (no CONNECT response in trace)");
                    break;
                case 0xF5 when req.Length > 1: // UPLOAD
                {
                    var len = req[1] * granularity;
                    if (mta is { } a)
                    {
                        reqNote.Add($"from MTA 0x{a:X}: {Describe(a, mtaExt)}");
                        if (ok && t.Response is { Length: > 1 } rr && Value(a, mtaExt, rr[1..]) is { } v) resNote.Add(v);
                        if (ok) mta = a + (ulong)len; else mta = null;
                    }
                    break;
                }
                case 0xF4 when req.Length >= 8: // SHORT_UPLOAD
                    if (U(req, 4, 4) is { } ua)
                    {
                        reqNote.Add(Describe(ua, req[3]));
                        if (ok && t.Response is { Length: > 1 } rr && Value(ua, req[3], rr[1..]) is { } v) resNote.Add(v);
                    }
                    mta = null;
                    break;
                case 0xF0 or 0xEF when req.Length > 1: // DOWNLOAD / DOWNLOAD_NEXT
                {
                    var len = req[1] * granularity;
                    if (mta is { } a)
                    {
                        reqNote.Add($"to MTA 0x{a:X}: {Describe(a, mtaExt)}");
                        if (len > 0 && 2 + len <= req.Length && Value(a, mtaExt, req[2..(2 + len)]) is { } v) reqNote.Add("writes " + v);
                        mta = ok ? a + (ulong)len : null;
                    }
                    break;
                }
                case 0xEE: // DOWNLOAD_MAX: payload length depends on MAX_CTO, which CAN padding hides
                    if (mta is { } mm) reqNote.Add($"to MTA 0x{mm:X}: {Describe(mm, mtaExt)}");
                    mta = null;
                    break;
                case 0xED when req.Length >= 8: // SHORT_DOWNLOAD
                    if (U(req, 4, 4) is { } da)
                    {
                        reqNote.Add(Describe(da, req[3]));
                        var len = req[1] * granularity;
                        if (len > 0 && 8 + len <= req.Length && Value(da, req[3], req[8..(8 + len)]) is { } v) reqNote.Add("writes " + v);
                    }
                    mta = null;
                    break;

                // ---- DAQ configuration ----
                case 0xD6 when ok: lists.Clear(); ptr = null; break; // FREE_DAQ
                case 0xD5 when ok && U(req, 2, 2) is { } n: // ALLOC_DAQ
                    lists.Clear();
                    for (var i = 0; i < (int)Math.Min(n, 4096); i++) lists[i] = new DaqList();
                    break;
                case 0xD4 when ok && U(req, 2, 2) is { } dl && req.Length > 4: // ALLOC_ODT
                    if (lists.TryGetValue((int)dl, out var l4))
                        l4.Odts = Enumerable.Range(0, req[4]).Select(_ => new List<Entry>()).ToList();
                    break;
                case 0xD3 when ok && U(req, 2, 2) is { } dl2 && req.Length > 5: // ALLOC_ODT_ENTRY
                    if (lists.TryGetValue((int)dl2, out var l3) && req[4] < l3.Odts.Count)
                        l3.Odts[req[4]] = Enumerable.Range(0, req[5]).Select(_ => new Entry()).ToList();
                    break;
                case 0xE3 when ok && U(req, 2, 2) is { } dl3: // CLEAR_DAQ_LIST
                    if (lists.TryGetValue((int)dl3, out var l5))
                        foreach (var odt in l5.Odts) for (var i = 0; i < odt.Count; i++) odt[i] = new Entry();
                    break;
                case 0xE2 when ok && U(req, 2, 2) is { } dl4 && req.Length > 5: // SET_DAQ_PTR
                    ptr = ((int)dl4, req[4], req[5]);
                    break;
                case 0xE1 when req.Length >= 8: // WRITE_DAQ
                {
                    if (U(req, 4, 4) is { } wa)
                    {
                        reqNote.Add(Describe(wa, req[3]));
                        if (ok && ptr is { } p && lists.TryGetValue(p.List, out var wl) && p.Odt < wl.Odts.Count && p.Entry < wl.Odts[p.Odt].Count)
                        {
                            var e = wl.Odts[p.Odt][p.Entry];
                            e.Set = true; e.BitOffset = req[1]; e.Size = req[2]; e.Ext = req[3]; e.Address = wa;
                            ptr = (p.List, p.Odt, p.Entry + 1);
                        }
                        else if (ok) { ptr = null; notes.Add($"WRITE_DAQ at frame {t.FrameIndices[0]} does not fit the DAQ allocation seen in the trace; DAQ decoding for it is skipped."); }
                    }
                    break;
                }
                case 0xE0 when ok && U(req, 2, 2) is { } ml && U(req, 4, 2) is { } ev: // SET_DAQ_LIST_MODE
                    if (lists.TryGetValue((int)ml, out var lm))
                    {
                        lm.Event = (int)ev;
                        lm.Timestamp = (req[1] & 0x10) != 0;
                        var en = a2l.XcpEvents.FirstOrDefault(x => x.Channel == (int)ev)?.Name;
                        reqNote.Add(en is null ? $"A2L: event {ev} not listed" : $"A2L: event '{en}'");
                    }
                    break;
                case 0xDE when ok && U(req, 2, 2) is { } sl && t.Response is { Length: > 1 } sr: // START_STOP_DAQ_LIST
                    if (req[1] != 0 && lists.TryGetValue((int)sl, out var ls)) ls.FirstPid = sr[1];
                    break;
                case 0xD9 when ok && t.Response is { Length: >= 6 } rr2: // GET_DAQ_RESOLUTION_INFO
                    tsSize = (rr2[5] & 0x07) is 1 or 2 or 4 ? rr2[5] & 0x07 : 0;
                    break;
            }

            result.Add(reqNote.Count == 0 && resNote.Count == 0 ? t : t with
            {
                RequestDecoded = Append(t.RequestDecoded, reqNote),
                ResponseDecoded = Append(t.ResponseDecoded, resNote),
            });
        }
        Flush(long.MaxValue);

        if (undecodable > 0)
            notes.Add($"{undecodable} DAQ packet(s) could not be decoded (no matching DAQ list/ODT with a known first PID in the trace, an unsupported timestamp, or a truncated payload).");
        if (a2l.Warnings.Count > 0) notes.Add($"A2L reader: {a2l.Warnings.Count} warning(s); first: {a2l.Warnings[0]}");
        return analysis with { Transactions = result, DaqDecoded = decoded, A2lNotes = notes };
    }

    private static string Append(string text, List<string> add) =>
        add.Count == 0 ? text : text.Length == 0 ? string.Join("; ", add) : text + "; " + string.Join("; ", add);

    private static XcpDaqDecodedPacket? DecodePacket(XcpDaqPacket pk, Dictionary<int, DaqList> lists, A2lDatabase a2l, bool? motorola, int tsSize)
    {
        // The packet's PID is the absolute ODT number: FIRST_PID of the list (from START_STOP_DAQ_LIST) plus the ODT index.
        var match = new List<(int List, DaqList L, int Odt)>();
        foreach (var (num, l) in lists)
            if (l.FirstPid is { } f && pk.Pid >= f && pk.Pid < f + l.Odts.Count)
                match.Add((num, l, pk.Pid - f));
        if (match.Count != 1) return null;
        var (listNo, list, odtIdx) = match[0];

        var data = pk.Data;
        var offset = 0;
        ulong? deviceTs = null;
        if (odtIdx == 0 && list.Timestamp)
        {
            if (tsSize == 0 || motorola is null || data.Length < tsSize) return null;
            ulong v = 0;
            for (var i = 0; i < tsSize; i++) v = motorola.Value ? (v << 8) | data[i] : v | ((ulong)data[i] << (8 * i));
            deviceTs = v; offset = tsSize;
        }

        var values = new List<XcpDaqValue>();
        string? note = null;
        foreach (var e in list.Odts[odtIdx])
        {
            if (!e.Set || e.Size <= 0) continue;
            if (offset + e.Size > data.Length) { note = "payload shorter than the configured ODT entries"; break; }
            var raw = data.AsSpan(offset, e.Size);
            offset += e.Size;
            var hex = XcpCatalog.Hex(raw, 16);
            if (e.BitOffset != 0xFF)
            {
                values.Add(new XcpDaqValue($"0x{e.Address:X}.bit{e.BitOffset}", e.Address, e.Ext, e.Size, hex, "", null, false, "bit-field entry not decoded"));
                continue;
            }
            var m = a2l.Resolve(e.Address, e.Ext);
            if (m is null)
            {
                values.Add(new XcpDaqValue($"0x{e.Address:X}", e.Address, e.Ext, e.Size, hex, "", null, false, "no A2L symbol at this address"));
                continue;
            }
            var sym = m.Symbol;
            if (m.Offset == 0 && sym.SizeBytes == e.Size && sym.ElementCount == 1)
            {
                var text = a2l.FormatValue(sym, raw, motorola ?? false, out var vnote);
                double? num = null;
                if (text is not null && double.TryParse(text.Split(' ')[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var dv)) num = dv;
                values.Add(new XcpDaqValue(sym.Name, e.Address, e.Ext, e.Size, hex, text ?? "", num, true, vnote));
            }
            else
                values.Add(new XcpDaqValue(m.Text, e.Address, e.Ext, e.Size, hex, "", null, true,
                    sym.SizeBytes > 0 && sym.SizeBytes != e.Size && m.Offset == 0 ? $"entry size {e.Size} differs from A2L size {sym.SizeBytes}; raw bytes shown" : "partial or array access; raw bytes shown"));
        }

        var evName = list.Event is { } ec ? a2l.XcpEvents.FirstOrDefault(x => x.Channel == ec)?.Name : null;
        return new XcpDaqDecodedPacket(pk.TimestampNanoseconds, pk.FrameIndex, pk.Channel, listNo, odtIdx, list.Event, evName, deviceTs, values, note);
    }
}
