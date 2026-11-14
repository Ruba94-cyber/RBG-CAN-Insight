using System.Data;
using System.Globalization;
using RbgCanInsight.Analysis;
using RbgCanInsight.Analysis.Analytics;
using RbgCanInsight.Analysis.BusHealth;
using RbgCanInsight.Analysis.Heuristics;
using RbgCanInsight.Dbc;
using RbgCanInsight.Diagnostics.Obd;
using RbgCanInsight.Domain;

namespace RbgCanInsight.App.Reports;

/// <summary>Builds read-only evidence tables from the loaded trace for the offline protocol tools.</summary>
public static class ProtocolReports
{
    private static string T(long ns) => (ns / 1e9).ToString("F6", CultureInfo.InvariantCulture);

    private static string Hex(byte[] bytes, int max = 24) =>
        string.Join(' ', bytes.Take(max).Select(b => b.ToString("X2", CultureInfo.InvariantCulture))) +
        (bytes.Length > max ? " …" : "");

    private static string Frames(IReadOnlyList<int> indices) =>
        indices.Count == 0 ? "" : indices.Count <= 4 ? string.Join(",", indices) : $"{indices[0]}…{indices[^1]} ({indices.Count})";

    public static DataTable J1939(IReadOnlyList<CanFrame> frames)
    {
        var table = new DataTable();
        foreach (var c in new[] { "Time [s]", "Ch", "SA", "DA", "PGN", "Kind", "Bytes", "Status", "Decoded / evidence", "Frames" })
            table.Columns.Add(c);
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
            {
                decoded = "missing packets: " + string.Join(",", m.MissingSequenceNumbers);
            }

            table.Rows.Add(T(m.StartTimestampNanoseconds), m.Channel + 1, $"0x{m.SourceAddress:X2}", $"0x{m.DestinationAddress:X2}",
                $"0x{m.Pgn:X5}", m.Kind, $"{m.Payload?.Length.ToString() ?? "-"}/{m.DeclaredSize}", m.Status, decoded, Frames(m.FrameIndices));
        }

        foreach (var f in frames)
        {
            if (J1939DiagnosticDecoder.DecodeAddressClaim(f) is { } claim)
                table.Rows.Add(T(f.TimestampNanoseconds), f.Channel + 1, $"0x{claim.ClaimedAddress:X2}", "", "0x0EE00", "AddressClaim", "8", "Complete",
                    $"{claim.Name.IndustryGroupName}; function {claim.Name.Function}; manufacturer {claim.Name.ManufacturerCode}; identity {claim.Name.IdentityNumber}", "");
        }

        return table;
    }

    public static DataTable Obd(IReadOnlyList<CanFrame> frames)
    {
        var table = new DataTable();
        foreach (var c in new[] { "Time [s]", "Mode", "PID", "Name", "Value", "Outcome", "Responder", "Latency [ms]", "Frames" })
            table.Columns.Add(c);
        foreach (var t in ObdDecoder.Decode(frames))
        {
            if (t.Responses.Count == 0)
            {
                table.Rows.Add(T(t.StartTimestampNanoseconds), $"{t.Mode:X2}", t.Pid is { } p ? $"{p:X2}" : "", "", "", t.Outcome, "", "", Frames(t.FrameIndices));
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
                table.Rows.Add(T(r.TimestampNanoseconds), $"{t.Mode:X2}", t.Pid is { } pid ? $"{pid:X2}" : "",
                    r.Values.FirstOrDefault()?.Name ?? "", values + (r.Issue.Length > 0 ? " [" + r.Issue + "]" : ""), t.Outcome,
                    $"0x{r.ResponderId:X}", r.LatencyMilliseconds?.ToString("0.0", CultureInfo.InvariantCulture) ?? "", Frames(r.FrameIndices));
            }
        }

        return table;
    }

    public static DataTable Candidates(IReadOnlyList<CanFrame> frames)
    {
        var table = new DataTable();
        foreach (var c in new[] { "Ch", "ID", "Frames", "Kind", "Candidate", "Evidence", "Note" })
            table.Columns.Add(c);
        foreach (var a in ByteSignalHeuristics.AnalyzeFrames(frames))
        {
            var id = a.IsExtended ? $"{a.Identifier:X8}x" : $"{a.Identifier:X3}";
            foreach (var c in a.Counters)
                table.Rows.Add(a.Channel + 1, id, a.FrameCount, "Counter", $"bits {c.StartBit}..{c.StartBit + c.BitLength - 1}, modulo {c.Modulus}",
                    $"{c.ConsistentTransitions}/{c.TotalTransitions} transitions ({c.Confidence:P0})", "candidate, not a signal definition");
            foreach (var c in a.Checksums)
                table.Rows.Add(a.Channel + 1, id, a.FrameCount, "Checksum", $"byte {c.ByteIndex}: {c.Algorithm}",
                    $"{c.ConsistentFrames}/{c.TotalFrames} frames ({c.Fit:P0}), {c.VaryingInputBytes} varying input bytes",
                    "candidate; coincidence possible when few bytes vary");
        }

        return table;
    }

    /// <param name="health">When the source file carried logger-written Statistic:/Status: lines, the
    /// report adds the logged load and controller state per second and labels each row's source:
    /// "logged" (the logger measured it) or "estimated" (computed from frames at the assumed bitrate).</param>
    public static DataTable BusLoad(IReadOnlyList<CanFrame> frames, double kbps,
        TraceIO.TraceImportHealthSnapshot? health = null)
    {
        var table = new DataTable();
        var logged = health?.LoggedBusStatistics;
        var states = health?.LoggedControllerStatuses;
        var hasLogged = logged is { Count: > 0 } || states is { Count: > 0 };
        foreach (var c in new[] { "Ch", "Time [s]", "Frames", "Load % (estimated, per second)", "Errors", "Remote", "IDs" })
            table.Columns.Add(c);
        if (hasLogged)
            foreach (var c in new[] { "Logged load %", "Logged controller state", "Load source" })
                table.Columns.Add(c);
        if (frames.Count == 0)
            return table;
        var bitrates = frames.Select(f => f.Channel).Distinct()
            .ToDictionary(ch => ch, _ => new ChannelBitrate(kbps * 1000d, kbps * 1000d));
        var result = BusHealthTimeline.Compute(frames, new BusHealthOptions(1_000_000_000L, bitrates));
        foreach (var ch in result.Channels)
            foreach (var b in ch.Buckets.Where(b => b.FrameCount > 0))
            {
                var estimate = b.BusLoadPercent.ToString("0.0", CultureInfo.InvariantCulture);
                if (!hasLogged)
                {
                    table.Rows.Add(ch.Channel + 1, T(b.StartNanoseconds), b.FrameCount, estimate,
                        b.ErrorFrames, b.RemoteFrames, b.DistinctIds);
                    continue;
                }

                var loggedLoad = TraceIO.LoggedBusMetadata.LoggedLoadInWindow(
                    logged, ch.Channel, b.StartNanoseconds, result.BucketWidthNanoseconds);
                var state = TraceIO.LoggedBusMetadata.StatusAt(states, ch.Channel, b.StartNanoseconds);
                table.Rows.Add(ch.Channel + 1, T(b.StartNanoseconds), b.FrameCount, estimate,
                    b.ErrorFrames, b.RemoteFrames, b.DistinctIds,
                    loggedLoad is { } l ? l.ToString("0.0", CultureInfo.InvariantCulture) : "",
                    state is null ? "" : state.State + (state.TxErrorCounter is { } tx ? $" (Tx {tx}, Rx {state.RxErrorCounter})" : ""),
                    loggedLoad is null ? "estimated" : "logged");
            }
        return table;
    }

    public static DataTable SuggestDbc(IReadOnlyList<CanFrame> frames, IReadOnlyList<DbcDatabase> databases)
    {
        var table = new DataTable();
        foreach (var c in new[] { "Ch", "Rank", "DBC", "Score", "IDs matched", "Frames matched", "DLC agree", "Signals in range", "Unmatched IDs" })
            table.Columns.Add(c);
        var candidates = databases.Select(d => new DbcSuggestionCandidate(d.Name, d)).ToList();
        foreach (var ch in DbcSuggester.Rank(frames, candidates))
        {
            var rank = 0;
            foreach (var s in ch.Ranking)
            {
                var b = s.Breakdown;
                table.Rows.Add(ch.Channel + 1, ++rank, s.DatabaseName, b.CompositeScore.ToString("P0", CultureInfo.InvariantCulture),
                    $"{b.MatchedIdentifiers}/{b.DistinctIdentifiers}", $"{b.MatchedFrames}/{b.Frames}",
                    b.DlcShare.ToString("P0", CultureInfo.InvariantCulture), b.SignalRangeShare.ToString("P0", CultureInfo.InvariantCulture),
                    s.UnmatchedIdentifiers.Count);
            }
        }

        return table;
    }

    public static DataTable DbcCoverage(IReadOnlyList<CanFrame> frames, IReadOnlyList<DbcDatabase> databases)
    {
        var table = new DataTable();
        foreach (var c in new[] { "DBC", "Kind", "Item", "Frames", "Detail" })
            table.Columns.Add(c);
        foreach (var database in databases)
        {
            var r = TraceDbcCoverageAnalyzer.Analyze(frames, database);
            table.Rows.Add(database.Name, "Summary", "Frame coverage", r.FrameCount,
                $"{r.MatchedFrames:N0} matched, {r.UnmatchedFrames:N0} unmatched in {r.UnmatchedIds.Count} ID(s); " +
                $"{r.UndecodableBytes:N0} payload byte(s) no signal can explain; {r.OutOfRange.Count} signal(s) out of range");
            foreach (var u in r.UnmatchedIds)
                table.Rows.Add(database.Name, "Unmatched ID", $"CH{u.Channel + 1} {(u.IsExtended ? $"0x{u.Identifier:X8} (29-bit)" : $"0x{u.Identifier:X3}")}",
                    u.FrameCount, $"{u.PayloadBytes:N0} byte(s); first at frame index {u.FirstFrameIndex}");
            foreach (var m in r.Messages.Where(m => m.ShortFrames > 0 || m.LongFrames > 0 || m.UncoveredBytes.Count > 0))
                table.Rows.Add(database.Name, "Undecodable bytes", m.Message, m.Frames,
                    (m.UncoveredBytes.Count > 0 ? $"byte(s) {string.Join(",", m.UncoveredBytes)} not covered by any signal; " : "") +
                    (m.ShortFrames > 0 ? $"{m.ShortFrames:N0} frame(s) shorter than the declared {m.DeclaredLength} bytes (first at index {m.FirstShortFrameIndex}); " : "") +
                    (m.LongFrames > 0 ? $"{m.LongFrames:N0} frame(s) longer than declared; " : "").TrimEnd(' ', ';'));
            foreach (var o in r.OutOfRange)
                table.Rows.Add(database.Name, "Out of range", $"{o.Message}.{o.Signal}", o.OutOfRangeSamples,
                    string.Create(CultureInfo.InvariantCulture,
                        $"observed {o.ObservedMin:G6}..{o.ObservedMax:G6} {o.Unit}, declared {o.DeclaredMin:G6}..{o.DeclaredMax:G6}; {o.OutOfRangeSamples}/{o.Samples} sample(s), first at frame index {o.FirstFrameIndex}"));
        }
        return table;
    }

    public static DataTable SignalAnalysis(string name, IReadOnlyList<SignalSample> samples)
    {
        var table = new DataTable();
        foreach (var c in new[] { "Section", "Item", "Value", "Note" })
            table.Columns.Add(c);
        var series = samples.Select(x => new AnalyticsSample(x.TimestampNanoseconds, x.Value)).ToList();
        string F(double v) => double.IsNaN(v) ? "n/a" : v.ToString("G6", CultureInfo.InvariantCulture);
        if (series.Count == 0)
            return table;

        var sum = SignalAnalytics.Summarize(series);
        void Add(string item, string value, string note = "") => table.Rows.Add("Summary", item, value, note);
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
                table.Rows.Add("Histogram (time held)", $"[{F(h.Edges[i])} .. {F(h.Edges[i + 1])}{(i == h.Counts.Length - 1 ? "]" : ")")}",
                    h.Weights[i].ToString("0.###", CultureInfo.InvariantCulture) + " s", h.Counts[i] + (h.Counts[i] == 1 ? " sample" : " samples"));
        }

        // Spectrum: the grid rate is derived from the median sample interval and is shown, never hidden.
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
                        table.Rows.Add("Spectrum", "Dominant frequency", F(fft.PeakFrequencyHz) + " Hz",
                            $"amplitude {F(fft.PeakAmplitude)}; grid {F(rate)} Hz (median sample rate, sample-and-hold); Hann window");
                    }
                    catch (ArgumentException ex)
                    {
                        table.Rows.Add("Spectrum", "Not computed", "", ex.Message);
                    }
                }
            }
        }

        foreach (var st in SignalAnalytics.DetectStuck(series, 5_000_000_000L).Take(10))
            table.Rows.Add("Stuck (>=5 s)", $"{F(st.StartNs / 1e9)} s", F(st.DurationNs / 1e9) + " s", "constant value");
        return table;
    }

    /// <summary>Per (source address, PGN, parameter) statistics of the J1939 values the catalog can decode exactly.
    /// Not-available and error sentinels are counted, never scaled.</summary>
    public static DataTable J1939Signals(IReadOnlyList<CanFrame> frames)
    {
        var table = new DataTable();
        foreach (var c in new[] { "SA", "PGN", "Message", "Parameter", "SPN", "Unit", "Samples", "Min", "Max", "Last", "Not available / error" })
            table.Columns.Add(c);
        var stats = new Dictionary<(byte Sa, uint Pgn, string Name), (int? Spn, string Unit, int N, double Min, double Max, double Last, int Bad)>();
        var order = new List<(byte Sa, uint Pgn, string Name)>();
        // One buffer for the whole trace: a stackalloc inside the loop is only released when the
        // method returns, so a long extended-ID trace would overflow the stack and kill the process.
        Span<byte> payload = stackalloc byte[64];
        foreach (var f in frames)
        {
            if (!f.IsExtended || f.Flags.HasFlag(CanFrameFlags.Error) || f.Flags.HasFlag(CanFrameFlags.Remote))
                continue;
            var pgn = RbgCanInsight.Dbc.J1939.ExtractPgn(f.Identifier);
            if (pgn is 0xEC00 or 0xEB00) continue; // transport frames are reassembled in the J1939 messages report
            f.CopyPayloadTo(payload);
            foreach (var v in J1939SignalDecoder.Decode(pgn, payload[..f.DataLength]))
            {
                var key = ((byte)(f.Identifier & 0xFF), pgn, v.Name);
                stats.TryGetValue(key, out var s);
                if (s.N == 0 && s.Bad == 0) order.Add(key);
                if (v.State == J1939ValueState.Valid && v.Value is { } value)
                    s = (v.Spn, v.Unit, s.N + 1, s.N == 0 ? value : Math.Min(s.Min, value), s.N == 0 ? value : Math.Max(s.Max, value), value, s.Bad);
                else
                    s = (v.Spn, v.Unit, s.N, s.Min, s.Max, s.Last, s.Bad + 1);
                stats[key] = s;
            }
        }

        foreach (var key in order)
        {
            var s = stats[key];
            string F(double d) => s.N == 0 ? "" : d.ToString("G6", CultureInfo.InvariantCulture);
            table.Rows.Add($"0x{key.Sa:X2}", $"0x{key.Pgn:X5}", J1939Catalog.TryDescribe(key.Pgn)?.Name ?? "", key.Name,
                s.Spn?.ToString(CultureInfo.InvariantCulture) ?? "", s.Unit, s.N, F(s.Min), F(s.Max), F(s.Last), s.Bad);
        }

        return table;
    }
}
