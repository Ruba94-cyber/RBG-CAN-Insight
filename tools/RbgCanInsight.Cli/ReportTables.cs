using System.Globalization;
using RbgCanInsight.Analysis;
using RbgCanInsight.Analysis.Analytics;
using RbgCanInsight.Analysis.BusHealth;
using RbgCanInsight.Analysis.Heuristics;
using RbgCanInsight.Dbc;
using RbgCanInsight.Diagnostics.Obd;
using RbgCanInsight.Domain;

namespace RbgCanInsight.Cli;

/// <summary>A plain string table written as CSV by the report commands.</summary>
public sealed class ReportTable(params string[] headers)
{
    public string[] Headers { get; } = headers;
    public List<string[]> Rows { get; } = [];
    public void Add(params object?[] cells) =>
        Rows.Add(cells.Select(c => Convert.ToString(c, CultureInfo.InvariantCulture) ?? "").ToArray());
}

/// <summary>Builds the same evidence tables as the GUI "Analysis tools" (App/Reports/ProtocolReports.cs), without the UI dependency.</summary>
public static class ReportTables
{
    private static string T(long ns) => (ns / 1e9).ToString("F6", CultureInfo.InvariantCulture);

    private static string Hex(byte[] bytes, int max = 24) =>
        string.Join(' ', bytes.Take(max).Select(b => b.ToString("X2", CultureInfo.InvariantCulture))) +
        (bytes.Length > max ? " ..." : "");

    private static string Frames(IReadOnlyList<int> indices) =>
        indices.Count == 0 ? "" : indices.Count <= 4 ? string.Join(",", indices) : $"{indices[0]}...{indices[^1]} ({indices.Count})";

    public static ReportTable J1939(IReadOnlyList<CanFrame> frames)
    {
        var table = new ReportTable("Time [s]", "Ch", "SA", "DA", "PGN", "Kind", "Bytes", "Status", "Decoded / evidence", "Frames");
        foreach (var m in J1939TransportAnalyzer.Reassemble(frames))
        {
            var decoded = "";
            if (m.Payload is { } payload)
            {
                decoded = J1939DiagnosticDecoder.TryDecode(m.Pgn, payload, out var dm) && dm is not null
                    ? dm.NoDtcs
                        ? "No active DTCs"
                        : string.Join("; ", dm.Dtcs.Select(d => $"SPN {d.Spn} FMI {d.Fmi} ({d.FmiDescription}) x{d.OccurrenceCount}"))
                    : Hex(payload);
            }
            else if (m.MissingSequenceNumbers.Count > 0)
                decoded = "missing packets: " + string.Join(",", m.MissingSequenceNumbers);

            table.Add(T(m.StartTimestampNanoseconds), m.Channel, $"0x{m.SourceAddress:X2}", $"0x{m.DestinationAddress:X2}",
                $"0x{m.Pgn:X5}", m.Kind, $"{m.Payload?.Length.ToString() ?? "-"}/{m.DeclaredSize}", m.Status, decoded, Frames(m.FrameIndices));
        }
        foreach (var f in frames)
            if (J1939DiagnosticDecoder.DecodeAddressClaim(f) is { } claim)
                table.Add(T(f.TimestampNanoseconds), f.Channel, $"0x{claim.ClaimedAddress:X2}", "", "0x0EE00", "AddressClaim", "8", "Complete",
                    $"{claim.Name.IndustryGroupName}; function {claim.Name.Function}; manufacturer {claim.Name.ManufacturerCode}; identity {claim.Name.IdentityNumber}", "");
        return table;
    }

    public static ReportTable Obd(IReadOnlyList<CanFrame> frames)
    {
        var table = new ReportTable("Time [s]", "Mode", "PID", "Name", "Value", "Outcome", "Responder", "Latency [ms]", "Frames");
        foreach (var t in ObdDecoder.Decode(frames))
        {
            if (t.Responses.Count == 0)
            {
                table.Add(T(t.StartTimestampNanoseconds), $"{t.Mode:X2}", t.Pid is { } p ? $"{p:X2}" : "", "", "", t.Outcome, "", "", Frames(t.FrameIndices));
                continue;
            }
            foreach (var r in t.Responses)
            {
                var values = r.IsNegative ? $"NRC 0x{r.NegativeResponseCode:X2} {r.NegativeResponseName}"
                    : r.Vin is not null ? "VIN " + r.Vin
                    : r.Dtcs.Count > 0 ? string.Join(", ", r.Dtcs.Select(d => d.Code))
                    : string.Join("; ", r.Values.Select(v => v.IsKnown && v.NumericValue is { } n
                        ? $"{v.Name}={n.ToString("0.##", CultureInfo.InvariantCulture)} {v.Unit}".Trim()
                        : $"{v.Name} {v.Text}".Trim() + (v.IsKnown ? "" : " (raw " + Hex(v.Raw) + ")")));
                table.Add(T(r.TimestampNanoseconds), $"{t.Mode:X2}", t.Pid is { } pid ? $"{pid:X2}" : "",
                    r.Values.FirstOrDefault()?.Name ?? "", values + (r.Issue.Length > 0 ? " [" + r.Issue + "]" : ""), t.Outcome,
                    $"0x{r.ResponderId:X}", r.LatencyMilliseconds?.ToString("0.0", CultureInfo.InvariantCulture) ?? "", Frames(r.FrameIndices));
            }
        }
        return table;
    }

    public static ReportTable Candidates(IReadOnlyList<CanFrame> frames)
    {
        var table = new ReportTable("Ch", "ID", "Frames", "Kind", "Candidate", "Evidence", "Note");
        foreach (var a in ByteSignalHeuristics.AnalyzeFrames(frames))
        {
            var id = a.IsExtended ? $"{a.Identifier:X8}x" : $"{a.Identifier:X3}";
            foreach (var c in a.Counters)
                table.Add(a.Channel, id, a.FrameCount, "Counter", $"bits {c.StartBit}..{c.StartBit + c.BitLength - 1}, modulo {c.Modulus}",
                    $"{c.ConsistentTransitions}/{c.TotalTransitions} transitions ({c.Confidence:P0})", "candidate, not a signal definition");
            foreach (var c in a.Checksums)
                table.Add(a.Channel, id, a.FrameCount, "Checksum", $"byte {c.ByteIndex}: {c.Algorithm}",
                    $"{c.ConsistentFrames}/{c.TotalFrames} frames ({c.Fit:P0}), {c.VaryingInputBytes} varying input bytes",
                    "candidate; coincidence possible when few bytes vary");
        }
        return table;
    }

    public static ReportTable BusLoad(IReadOnlyList<CanFrame> frames, double kbps, double? dataKbps)
    {
        var table = new ReportTable("Ch", "Time [s]", "Frames", "Load % (upper estimate)", "Errors", "Remote", "IDs");
        if (frames.Count == 0) return table;
        var bitrates = frames.Select(f => f.Channel).Distinct()
            .ToDictionary(ch => ch, _ => new ChannelBitrate(kbps * 1000d, (dataKbps ?? kbps) * 1000d));
        var result = BusHealthTimeline.Compute(frames, new BusHealthOptions(1_000_000_000L, bitrates));
        foreach (var ch in result.Channels)
            foreach (var b in ch.Buckets.Where(b => b.FrameCount > 0))
                table.Add(ch.Channel, T(b.StartNanoseconds), b.FrameCount,
                    b.BusLoadPercent.ToString("0.0", CultureInfo.InvariantCulture), b.ErrorFrames, b.RemoteFrames, b.DistinctIds);
        return table;
    }

    public static ReportTable SuggestDbc(IReadOnlyList<CanFrame> frames, IReadOnlyList<(string Name, DbcDatabase Database)> databases)
    {
        var table = new ReportTable("Ch", "Rank", "DBC", "Score", "IDs matched", "Frames matched", "DLC agree", "Signals in range", "Unmatched IDs");
        var candidates = databases.Select(d => new DbcSuggestionCandidate(d.Name, d.Database)).ToList();
        foreach (var ch in DbcSuggester.Rank(frames, candidates))
        {
            var rank = 0;
            foreach (var s in ch.Ranking)
            {
                var b = s.Breakdown;
                table.Add(ch.Channel, ++rank, s.DatabaseName, b.CompositeScore.ToString("P0", CultureInfo.InvariantCulture),
                    $"{b.MatchedIdentifiers}/{b.DistinctIdentifiers}", $"{b.MatchedFrames}/{b.Frames}",
                    b.DlcShare.ToString("P0", CultureInfo.InvariantCulture), b.SignalRangeShare.ToString("P0", CultureInfo.InvariantCulture),
                    s.UnmatchedIdentifiers.Count);
            }
        }
        return table;
    }

    public static ReportTable SignalStats(string name, IReadOnlyList<AnalyticsSample> series)
    {
        var table = new ReportTable("Section", "Item", "Value", "Note");
        string F(double v) => double.IsNaN(v) ? "n/a" : v.ToString("G6", CultureInfo.InvariantCulture);
        if (series.Count == 0) return table;

        var sum = SignalAnalytics.Summarize(series);
        void Add(string item, string value, string note = "") => table.Add("Summary", item, value, note);
        Add("Signal", name);
        Add("Samples", sum.Count.ToString(CultureInfo.InvariantCulture));
        Add("Min / Max", $"{F(sum.Min)} / {F(sum.Max)}");
        Add("Mean (per sample)", F(sum.Mean));
        Add("Mean (time-weighted)", F(sum.TimeWeightedMean), "sample-and-hold between frames");
        Add("Std dev", F(sum.StdDev), "sample (n-1)");
        Add("Median / P5 / P95", $"{F(sum.Median)} / {F(sum.P5)} / {F(sum.P95)}");
        Add("Distinct values", sum.DistinctValues.ToString(CultureInfo.InvariantCulture));
        Add("Changes", sum.Changes.ToString(CultureInfo.InvariantCulture));
        Add("Longest constant run [s]", F(sum.LongestConstantRunNs / 1e9));

        if (sum.Max > sum.Min)
        {
            var h = SignalAnalytics.Histogram(series, 12, HistogramWeighting.ByTimeHeld);
            for (var i = 0; i < h.Counts.Length; i++)
                table.Add("Histogram (time held)", $"[{F(h.Edges[i])} .. {F(h.Edges[i + 1])}{(i == h.Counts.Length - 1 ? "]" : ")")}",
                    h.Weights[i].ToString("0.###", CultureInfo.InvariantCulture) + " s", h.Counts[i] + (h.Counts[i] == 1 ? " sample" : " samples"));
        }

        if (series.Count >= 16)
        {
            var intervals = new List<long>();
            for (var i = 1; i < series.Count; i++)
                if (series[i].TimestampNs > series[i - 1].TimestampNs)
                    intervals.Add(series[i].TimestampNs - series[i - 1].TimestampNs);
            if (intervals.Count > 8)
            {
                intervals.Sort();
                var rate = 1e9 / intervals[intervals.Count / 2];
                var spanSeconds = (series[^1].TimestampNs - series[0].TimestampNs) / 1e9;
                if (rate > 0 && spanSeconds * rate is >= 8 and <= (1 << 22))
                {
                    try
                    {
                        var fft = SignalAnalytics.Fft(series, rate, ResampleMode.SampleAndHold, FftWindow.Hann, removeMean: true, excludeDcFromPeak: true);
                        table.Add("Spectrum", "Dominant frequency", F(fft.PeakFrequencyHz) + " Hz",
                            $"amplitude {F(fft.PeakAmplitude)}; grid {F(rate)} Hz (median sample rate, sample-and-hold); Hann window");
                    }
                    catch (ArgumentException ex)
                    {
                        table.Add("Spectrum", "Not computed", "", ex.Message);
                    }
                }
            }
        }

        foreach (var st in SignalAnalytics.DetectStuck(series, 5_000_000_000L).Take(10))
            table.Add("Stuck (>=5 s)", $"{F(st.StartNs / 1e9)} s", F(st.DurationNs / 1e9) + " s", "constant value");
        return table;
    }
}
