using System.Globalization;
using RbgCanInsight.Domain;

namespace RbgCanInsight.Analysis.Buses;

/// <summary>UI-independent string table shared by the desktop report windows and the CLI.</summary>
public sealed class BusTable(params string[] headers)
{
    public string[] Headers { get; } = headers;
    public List<string[]> Rows { get; } = [];
    public void Add(params object?[] cells) =>
        Rows.Add(cells.Select(c => Convert.ToString(c, CultureInfo.InvariantCulture) ?? "").ToArray());
}

/// <summary>Evidence tables for LIN, FlexRay and CAN XL. Every row names the source frame index/indices
/// (position in the corresponding imported frame list, zero based).</summary>
public static class BusReportTables
{
    private static string T(long ns) => (ns / 1e9).ToString("F6", CultureInfo.InvariantCulture);

    private static string Hex(ReadOnlySpan<byte> bytes, int max = 32) =>
        string.Join(' ', bytes[..Math.Min(bytes.Length, max)].ToArray().Select(b => b.ToString("X2", CultureInfo.InvariantCulture))) +
        (bytes.Length > max ? " ..." : "");

    private static string Refs(IReadOnlyList<int> indices) =>
        indices.Count == 0 ? "" : indices.Count <= 4 ? string.Join(",", indices) : $"{indices[0]}...{indices[^1]} ({indices.Count})";

    public static BusTable LinFrames(IReadOnlyList<LinFrame> frames, LdfFile? ldf = null)
    {
        var table = new BusTable("Frame #", "Time [s]", "Ch", "Event", "ID", "PID", "Dir", "Len", "Data", "Checksum", "Checksum model", "PID check", "Name (LDF)", "Verdict");
        var names = ldf?.Frames.GroupBy(f => f.Id).ToDictionary(g => g.Key, g => g.First().Name);
        foreach (var c in LinFrameAnalyzer.Check(frames))
        {
            var f = c.Frame;
            var isFrame = f.Event is LinEventKind.Message or LinEventKind.CrcError or LinEventKind.SendError or LinEventKind.ReceiveError;
            var name = names is not null && isFrame && names.TryGetValue(f.FrameId, out var n) ? n : "";
            table.Add(c.Index, T(f.TimestampNanoseconds), f.Channel + 1, f.Event,
                isFrame ? $"0x{f.FrameId:X2}" : "", isFrame ? $"0x{LinProtocol.ProtectedId(f.FrameId):X2}" + (f.IdIsProtected ? "" : " (computed)") : "",
                f.Direction, f.DeclaredLength, Hex(f.Data),
                f.Checksum is { } cs ? $"0x{cs:X2}" : "",
                c.Checksum switch
                {
                    LinChecksumStatus.ClassicOnly => "classic",
                    LinChecksumStatus.EnhancedOnly => "enhanced",
                    LinChecksumStatus.ClassicAndEnhanced => "classic or enhanced",
                    LinChecksumStatus.Mismatch => "mismatch",
                    LinChecksumStatus.Truncated => "not checked (truncated)",
                    _ => ""
                },
                c.Pid switch { LinPidStatus.Valid => "parity ok", LinPidStatus.Invalid => "parity wrong", _ => "n/a (ID only recorded)" },
                name, c.Verdict);
        }
        return table;
    }

    public static BusTable LinTiming(IReadOnlyList<LinFrame> frames)
    {
        var table = new BusTable("Ch", "ID", "Frames", "Min period [ms]", "Mean period [ms]", "Max period [ms]", "Jitter (max-min) [ms]", "First Frame #", "Last Frame #");
        foreach (var t in LinFrameAnalyzer.Timing(frames))
            table.Add(t.Channel + 1, $"0x{t.FrameId:X2}", t.Count, t.MinMs.ToString("F3", CultureInfo.InvariantCulture),
                t.MeanMs.ToString("F3", CultureInfo.InvariantCulture), t.MaxMs.ToString("F3", CultureInfo.InvariantCulture),
                (t.MaxMs - t.MinMs).ToString("F3", CultureInfo.InvariantCulture), t.FirstIndex, t.LastIndex);
        return table;
    }

    public static BusTable LinDiagnostics(IReadOnlyList<LinFrame> frames)
    {
        var table = new BusTable("Time [s]", "Ch", "Direction", "NAD", "NAD meaning", "Length", "Payload", "Status", "Decoded", "Frame #");
        foreach (var m in LinDiagnosticsDecoder.Decode(frames))
            table.Add(T(m.StartTimestampNanoseconds), m.Channel + 1, m.Direction, $"0x{m.Nad:X2}", LinDiagnosticsDecoder.NadMeaning(m.Nad),
                m.DeclaredLength, Hex(m.Payload, 48), m.Status, m.Decoded, Refs(m.FrameIndices));
        return table;
    }

    public static BusTable LinSignals(LdfFile ldf, IReadOnlyList<LinFrame> frames)
    {
        var table = new BusTable("Frame", "Signal", "Samples", "Min", "Max", "Last raw", "Last value", "Unit", "First Frame #", "Last Frame #");
        var r = LdfSignalDecoder.Decode(ldf, frames);
        foreach (var s in r.Signals)
            table.Add(s.Frame, s.Signal, s.Count, s.Min.ToString("0.######", CultureInfo.InvariantCulture),
                s.Max.ToString("0.######", CultureInfo.InvariantCulture), s.LastRaw, s.LastValue, s.Unit, s.FirstIndex, s.LastIndex);
        table.Add("(summary)", "", r.FramesDecoded, "", "", "", $"{r.FramesNotInLdf} frame(s) with an ID not in the LDF; {r.FramesLengthMismatch} with a length different from the LDF; {r.FramesSkippedForErrors} skipped for PID/checksum problems", "", "", "");
        return table;
    }

    public static BusTable FlexRayMatrix(IReadOnlyList<FlexRayFrame> frames)
    {
        var table = new BusTable("Ch", "FR channel", "Slot", "Segment", "Frames", "Null", "Recorded errors", "Sync", "Startup", "Payload bytes",
            "Cycles seen", "Frames without cycle", "Inferred repetition", "Missing (inferred)", "First Frame #", "Last Frame #", "First error Frame #");
        foreach (var r in FlexRayAnalyzer.Analyze(frames).Rows)
            table.Add(r.Channel + 1, r.FlexRayChannel == FlexRayChannel.Unknown ? "n/a" : r.FlexRayChannel.ToString(), r.Slot, r.Segment, r.Frames,
                r.NullFrames, r.ErrorFrames, r.Sync ? "yes" : "", r.Startup ? "yes" : "", string.Join("/", r.PayloadLengths),
                FlexRayAnalyzer.CompressRanges(r.SeenCycles), r.FramesWithoutCycle,
                r.SeenCycles.Count == 0 ? "" : $"every {r.InferredRepetition} (base {r.InferredBaseCycle})",
                r.MissingInstances.Count == 0 ? "" : $"{r.MissingInstances.Count}: " + string.Join("; ", r.MissingInstances.Take(5)) + (r.MissingInstances.Count > 5 ? "; ..." : ""),
                r.FirstIndex, r.LastIndex, r.FirstErrorIndex >= 0 ? r.FirstErrorIndex : "");
        return table;
    }

    /// <summary>The SDT column shows the raw value, plus a name only for 0x01 and 0x03 (see CanXlSdt); other meanings are not
    /// verified and therefore not guessed.</summary>
    public static BusTable CanXlList(IReadOnlyList<CanXlFrame> frames)
    {
        var table = new BusTable("Frame #", "Time [s]", "Ch", "Priority ID", "VCID", "SDT", "SEC", "RRS", "Acceptance field", "Dir", "Length", "Payload");
        for (var i = 0; i < frames.Count; i++)
        {
            var f = frames[i];
            table.Add(i, T(f.TimestampNanoseconds), f.Channel + 1, $"0x{f.PriorityId:X3}", $"0x{f.Vcid:X2}", $"0x{f.Sdt:X2}" + (CanXlSdt.Name(f.Sdt) is { } sdtName ? $" ({sdtName})" : ""),
                f.Sec ? "1" : "0", f.Rrs ? "1" : "0", $"0x{f.AcceptanceField:X8}", f.Direction, f.Payload.Length, Hex(f.Payload, 64));
        }
        return table;
    }
}
