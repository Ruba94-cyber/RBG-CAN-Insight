using System.Globalization;
using RbgCanInsight.Domain;

namespace RbgCanInsight.Analysis.Xcp;

/// <summary>Plain string table shared by the GUI report window and the CLI CSV writer.</summary>
public sealed record XcpReportTable(IReadOnlyList<string> Headers, IReadOnlyList<string[]> Rows);

public static class XcpReportRows
{
    public static readonly string[] Headers =
        ["Time [s]", "Ch", "Protocol", "Type", "Item", "Request", "Response", "Outcome", "Latency [ms]", "Evidence", "Frames"];

    /// <summary>Minimum detector confidence for a candidate to be decoded automatically.</summary>
    public const double AutoMinConfidence = 0.60;

    private static string T(long ns) => (ns / 1e9).ToString("F6", CultureInfo.InvariantCulture);

    private static string Fr(IReadOnlyList<int> i) =>
        i.Count == 0 ? "" : i.Count <= 4 ? string.Join(",", i) : $"{i[0]}...{i[^1]} ({i.Count})";

    private static string Id(uint id, bool ext) => ext ? $"0x{id:X8}" : $"0x{id:X3}";

    /// <summary>Decodes with <paramref name="manual"/> when given, otherwise auto-detects identifier pairs.</summary>
    public static XcpReportTable Build(IReadOnlyList<CanFrame> frames, XcpEndpoint? manual = null, XcpOptions? options = null)
    {
        var rows = new List<string[]>();
        var endpoints = new List<XcpEndpoint>();
        if (manual is not null) endpoints.Add(manual);
        else
        {
            var candidates = XcpDetector.Detect(frames);
            foreach (var c in candidates)
            {
                rows.Add(["", c.Channel.ToString(CultureInfo.InvariantCulture), c.Protocol.ToString().ToUpperInvariant(), "Detection",
                    $"CMD {Id(c.CommandId, c.CommandExtended)} / RES {Id(c.ResponseId, c.ResponseExtended)}", "", "",
                    c.Confidence >= XcpReportRows.AutoMinConfidence ? "Decoded below" : "Low confidence, not decoded", "",
                    $"confidence {c.Confidence:0.00}: " + string.Join("; ", c.Reasons), Fr(c.EvidenceFrameIndices)]);
                if (c.Confidence >= AutoMinConfidence && endpoints.Count < 4) endpoints.Add(c.ToEndpoint());
            }

            if (candidates.Count == 0)
                rows.Add(["", "", "", "Detection", "No XCP/CCP handshake found", "", "", "", "",
                    "no CONNECT-like request answered by a positive response within 200 ms; supply the identifiers manually", ""]);

            // Identifiers declared by the A2L (IF_DATA XCP_ON_CAN) are used in addition to the heuristic ones.
            if (options?.A2l is { } a2l)
            {
                foreach (var can in a2l.XcpOnCan)
                {
                    var ep = can.ToEndpoint();
                    if (ep is null)
                    {
                        rows.Add(["", "", "XCP", "A2L", "XCP_ON_CAN without CAN_ID_MASTER/CAN_ID_SLAVE", "", "", "", "", "identifiers not usable", ""]);
                        continue;
                    }
                    var dup = endpoints.Any(e => e.CommandId == ep.CommandId && e.ResponseId == ep.ResponseId);
                    rows.Add(["", "", "XCP", "A2L", $"CMD {Id(ep.CommandId, ep.CommandExtended)} / RES {Id(ep.ResponseId, ep.ResponseExtended)}", "", "",
                        dup ? "Same pair as detected" : "Decoded below", "",
                        "from A2L IF_DATA XCP_ON_CAN" + (ep.DaqIds is { Count: > 0 } d ? "; DAQ ids " + string.Join(",", d.Select(x => Id(x, ep.DaqExtended))) : "") +
                        "; extended-id flag taken from bit 31", ""]);
                    if (dup) endpoints[endpoints.FindIndex(e => e.CommandId == ep.CommandId && e.ResponseId == ep.ResponseId)] = ep with { Channel = endpoints.First(e => e.CommandId == ep.CommandId && e.ResponseId == ep.ResponseId).Channel };
                    else if (endpoints.Count < 4) endpoints.Add(ep);
                }
            }
        }

        foreach (var ep in endpoints)
        {
            var a = XcpAnalyzer.Analyze(frames, ep, options);
            var proto = ep.Protocol.ToString().ToUpperInvariant();
            var chText = ep.Channel?.ToString(CultureInfo.InvariantCulture) ?? "";
            var body = new List<(long Ts, int Idx, string[] Row)>();
            foreach (var t in a.Transactions)
            {
                var evidence = string.Join("; ", t.Notes);
                body.Add((t.TimestampNanoseconds, t.FrameIndices[0], [T(t.TimestampNanoseconds), t.Channel.ToString(CultureInfo.InvariantCulture), proto, "Command",
                    t.CommandName + (t.Counter is { } c ? $" (CTR {c})" : ""),
                    Join(XcpCatalog.Hex(t.Request), t.RequestDecoded),
                    t.Response is null ? "" : Join(XcpCatalog.Hex(t.Response), t.ResponseDecoded),
                    t.Outcome == XcpOutcome.Negative ? $"Error {t.ErrorName ?? "unknown"} (0x{t.ErrorCode:X2})" : t.Outcome.ToString(),
                    t.LatencyMilliseconds is { } l ? l.ToString("0.###", CultureInfo.InvariantCulture) : "", evidence, Fr(t.FrameIndices)]));
            }

            foreach (var e in a.Events)
                body.Add((e.TimestampNanoseconds, e.FrameIndex, [T(e.TimestampNanoseconds), e.Channel.ToString(CultureInfo.InvariantCulture), proto, e.Kind, e.Name, "",
                    XcpCatalog.Hex(e.Data), "", "", e.Decoded, e.FrameIndex.ToString(CultureInfo.InvariantCulture)]));
            body.Sort((x, y) => x.Ts != y.Ts ? x.Ts.CompareTo(y.Ts) : x.Idx.CompareTo(y.Idx));
            rows.AddRange(body.Select(b => b.Row));

            foreach (var g in a.DaqPackets.GroupBy(p => (p.IsStimulation, p.Pid, p.CanId)).OrderBy(g => g.Key.IsStimulation).ThenBy(g => g.Key.Pid))
            {
                var list = g.ToList();
                var evidence = $"{list.Count} packet(s), first at {T(list[0].TimestampNanoseconds)} s";
                if (list.Count > 1)
                    evidence += $", mean period {((list[^1].TimestampNanoseconds - list[0].TimestampNanoseconds) / 1e6 / (list.Count - 1)).ToString("0.###", CultureInfo.InvariantCulture)} ms";
                rows.Add([T(list[0].TimestampNanoseconds), list[0].Channel.ToString(CultureInfo.InvariantCulture), proto, g.Key.IsStimulation ? "STIM" : "DAQ",
                    $"PID {g.Key.Pid} on {Id(g.Key.CanId, g.Key.CanId == ep.ResponseId ? ep.ResponseExtended : g.Key.CanId == ep.CommandId ? ep.CommandExtended : ep.DaqExtended)}",
                    "", "first: " + XcpCatalog.Hex(list[0].Data, 16), "", "", evidence + " (payload layout not interpreted: needs the DAQ configuration)",
                    Fr(list.Take(8).Select(p => p.FrameIndex).ToList())]);
            }

            if (a.DaqDecoded is { Count: > 0 } decoded)
            {
                var perSignal = decoded.SelectMany(p => p.Values.Select(v => (Packet: p, Value: v)))
                    .GroupBy(x => (x.Packet.DaqList, x.Packet.Odt, x.Value.Name));
                foreach (var g in perSignal.OrderBy(g => g.Key.DaqList).ThenBy(g => g.Key.Odt))
                {
                    var items = g.ToList();
                    var nums = items.Where(x => x.Value.Numeric is not null).Select(x => x.Value.Numeric!.Value).ToList();
                    var first = items[0];
                    var evidence = $"{items.Count} sample(s) in DAQ list {g.Key.DaqList} ODT {g.Key.Odt}";
                    if (first.Packet.EventName is { } en) evidence += $", A2L event '{en}'";
                    if (nums.Count > 1) evidence += $", min {nums.Min().ToString("0.######", CultureInfo.InvariantCulture)} max {nums.Max().ToString("0.######", CultureInfo.InvariantCulture)}";
                    if (first.Value.Note is { } vn) evidence += "; " + vn;
                    rows.Add([T(first.Packet.TimestampNanoseconds), first.Packet.Channel.ToString(CultureInfo.InvariantCulture), proto, "DAQ signal",
                        g.Key.Name, "", first.Value.Text.Length > 0 ? first.Value.Text : "raw " + first.Value.RawHex, "", "", evidence,
                        Fr(items.Take(8).Select(x => x.Packet.FrameIndex).ToList())]);
                }
            }

            foreach (var note in a.A2lNotes ?? [])
                rows.Add(["", chText, proto, "A2L", "note", "", "", "", "", note, ""]);

            var summary = $"{a.Transactions.Count} command(s), {a.Transactions.Count(t => t.Outcome == XcpOutcome.Positive)} positive, " +
                          $"{a.Transactions.Count(t => t.Outcome == XcpOutcome.Negative)} error, {a.UnansweredCount} unanswered, {a.DaqPackets.Count} DAQ/STIM packet(s), {a.Events.Count} event/other";
            rows.Add(["", chText, proto, "Summary", $"CMD {Id(ep.CommandId, ep.CommandExtended)} / RES {Id(ep.ResponseId, ep.ResponseExtended)}", "", "", "", "", summary, ""]);
            foreach (var (name, n) in a.ErrorCounts.OrderByDescending(k => k.Value))
                rows.Add(["", chText, proto, "Error summary", name, "", "", "", "", $"{n} occurrence(s)", ""]);
        }

        return new XcpReportTable(Headers, rows);
    }

    private static string Join(string a, string b) => b.Length == 0 ? a : $"{a} | {b}";
}
