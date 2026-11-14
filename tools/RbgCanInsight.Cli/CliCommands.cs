using System.Globalization;
using System.Text;
using RbgCanInsight.Analysis.Analytics;
using RbgCanInsight.Analysis.Buses;
using RbgCanInsight.Analysis.E2E;
using RbgCanInsight.Analysis.Xcp;
using RbgCanInsight.Dbc;
using RbgCanInsight.Domain;
using RbgCanInsight.TraceIO;

namespace RbgCanInsight.Cli;

/// <summary>Thrown for bad command lines (exit code 2).</summary>
public sealed class UsageException(string message) : Exception(message);

/// <summary>Testable command logic. Exit codes: 0 ok, 1 error, 2 usage.</summary>
public static partial class CliCommands
{
    public const string UsageText =
        """
        Usage: rbgcan <command> [arguments]
          convert <in> <out>                         convert any importable trace (asc/blf/trc/csv/log/mf4/pcap/pcapng) to .asc/.csv/.blf/.trc/.mf4 (by extension of <out>)
          info <trace> [--dbc a.dbc ...]             frame counts per channel, id count, time range, DBC coverage
          merge <out> <in1> <in2> ...                time-merge traces (channels are offset per input so they do not collide)
          cut <in> <out> --from <sec> --to <sec>     keep frames with from <= t < to, seconds relative to the first frame
          shift <in> <out> --seconds <x>             shift all timestamps by x seconds (result must stay >= 0)
          export-signals <trace> <dbc> <out.csv> [--signals A,B]   decoded physical values as CSV
          report j1939 <trace> <out.csv>             J1939 transport (BAM/RTS-CTS) reassembly, DM1/DM2 and address claims
          report xcp <trace> <out.csv> [--cmd-id <hex> --res-id <hex> [--protocol xcp|ccp] [--daq-ids <hex,hex>] [--channel <n>] [--extended true] [--timeout-ms <n>]] [--a2l <file.a2l>]
                                                     XCP-on-CAN / CCP decoding; without ids the pair is auto-detected from CONNECT handshakes (heuristic)
          report obd <trace> <out.csv>               OBD-II requests and decoded responses
          report lin <trace> <out.csv> [--ldf f.ldf] LIN frames with PID/checksum verification (BLF input; optional LDF frame names)
          report flexray <trace> <out.csv>           FlexRay slot/cycle matrix with inferred missing slots and recorded errors (BLF input)
          report canxl <trace> <out.csv>             CAN XL frame list (candump -L logs and SocketCAN pcap/pcapng captures)
          report candidates <trace> <out.csv>        counter and checksum candidates per identifier
          report busload <trace> <out.csv> --kbps <n> [--data-kbps <n>]   bus load per channel and second (upper estimate)
          report suggest-dbc <trace> <out.csv> <dbc1> [dbc2 ...]          rank DBC files by how well they fit the trace
          report e2e <trace> <out.csv> --id <hex> [--channel <n>] --profile p1|p2|p5 --data-id <hex> [--cycle-ms <n>]
                                                     AUTOSAR E2E check; prints one verdict line, exit 1 when problems are found
          report signal-stats <trace> <dbc> <out.csv> --signal Message.Signal   statistics, histogram, spectrum of one signal
          check-dbc <dbc> [--out report.csv] [--fail-on error|warning|review]   DBC consistency check (DBC-0xx rules); exit 1 when a finding at or above the level exists (default error)
          check-cdd <cdd|odx|pdx> [--out report.csv] [--fail-on error|warning|review]   catalog consistency check (CAT-0xx rules), same exit codes
          compare <a> <b> [--out report.csv]         compare two DBCs or two CDD/ODX/PDX descriptions (same kind); exit 1 when they differ
          export-odx <cdd|odx|pdx> <out.odx> [--ecu <name>]   write the catalog as ODX 2.2 and structurally validate it; exit 1 on problems
          report timing <trace> <out.csv> [--p2-ms <n>] [--p2star-ms <n>] [--s3-ms <n>] [--nbs-ms <n>] [--ncr-ms <n>]   UDS/ISO-TP timing findings (P2, P2*, NRC 0x78, S3, N_Bs, N_Cr, STmin); exit 1 on errors
          report faults <trace> <out.csv>             per-ECU fault memory table with status bits and decoded snapshot / extended data
        Exit codes: 0 ok, 1 findings or errors, 2 usage. Report commands print a single summary line on stdout.
        """;

    public static async Task<int> RunAsync(string[] args, TextWriter stdout, TextWriter stderr, CancellationToken ct = default)
    {
        try
        {
            if (args.Length == 0)
            {
                await stderr.WriteLineAsync(UsageText);
                return 2;
            }
            if (args[0] is "--help" or "-h" or "help")
            {
                await stdout.WriteLineAsync(UsageText);
                return 0;
            }
            var rest = args[1..];
            var code = 0;
            switch (args[0])
            {
                case "convert": await ConvertAsync(rest, stdout, stderr, ct); break;
                case "info": await InfoAsync(rest, stdout, ct); break;
                case "merge": await MergeAsync(rest, stdout, stderr, ct); break;
                case "cut": await CutAsync(rest, stdout, stderr, ct); break;
                case "shift": await ShiftAsync(rest, stdout, stderr, ct); break;
                case "export-signals": await ExportSignalsAsync(rest, stdout, ct); break;
                case "report": code = await ReportAsync(rest, stdout, ct); break;
                case "check-dbc": code = await CheckDbcAsync(rest, stdout, ct); break;
                case "check-cdd": code = await CheckCddAsync(rest, stdout, ct); break;
                case "compare": code = await CompareAsync(rest, stdout, ct); break;
                case "export-odx": code = await ExportOdxAsync(rest, stdout, ct); break;
                default: throw new UsageException($"Unknown command '{args[0]}'.");
            }
            return code;
        }
        catch (UsageException ex)
        {
            await stderr.WriteLineAsync("Usage error: " + ex.Message);
            await stderr.WriteLineAsync("Run 'rbgcan --help' for usage.");
            return 2;
        }
        catch (OperationCanceledException)
        {
            await stderr.WriteLineAsync("Error: cancelled.");
            return 1;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException
                                       or ArgumentException or InvalidDataException or FormatException
                                       or InvalidOperationException or OverflowException)
        {
            // Expected failures: message only, no stack trace, no payload content.
            await stderr.WriteLineAsync("Error: " + ex.Message);
            return 1;
        }
    }

    // ---- commands ----

    private static async Task ConvertAsync(string[] a, TextWriter o, TextWriter e, CancellationToken ct)
    {
        var (pos, _) = Parse(a, 2, 2, []);
        var frames = await LoadAsync(pos[0], ct);
        await WriteAsync(pos[1], frames, ct, e);
        await o.WriteLineAsync($"Converted {frames.Count} frames to {Path.GetFileName(pos[1])}.");
    }

    private static async Task InfoAsync(string[] a, TextWriter o, CancellationToken ct)
    {
        var (pos, opts) = Parse(a, 1, 1, ["--dbc"], multi: ["--dbc"]);
        var frames = await LoadAsync(pos[0], ct);
        await o.WriteLineAsync($"Frames: {frames.Count}");
        if (frames.Count == 0) return;
        var first = frames.Min(f => f.TimestampNanoseconds);
        var last = frames.Max(f => f.TimestampNanoseconds);
        await o.WriteLineAsync($"Time range: {Sec(first)} s .. {Sec(last)} s (duration {Sec(last - first)} s)");
        // Count per channel without GroupBy: GroupBy would buffer a second copy of every 96-byte frame
        // (measured: ~2 GB extra peak memory at 10M frames).
        var perChannel = new SortedDictionary<int, long>();
        foreach (var f in frames) perChannel[f.Channel] = perChannel.GetValueOrDefault(f.Channel) + 1;
        foreach (var (channel, count) in perChannel)
            await o.WriteLineAsync($"Channel {channel}: {count} frames");
        var ids = frames.Select(f => (f.IsExtended, f.Identifier)).Distinct().Count();
        await o.WriteLineAsync($"Distinct identifiers: {ids}");

        foreach (var path in opts.GetValueOrDefault("--dbc") ?? [])
        {
            var db = new DbcParser().ParseFile(path);
            var decoder = new DbcDecoder(db);
            var covered = frames.Count(f => decoder.TryGetMessage(f, out _));
            var idSet = frames.Select(f => (f.Channel, f.IsExtended, f.Identifier)).Distinct().ToList();
            var idCovered = idSet.Count(k => decoder.TryGetMessage(k.Channel, k.Identifier, k.IsExtended, out _));
            await o.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
                $"DBC {Path.GetFileName(path)}: {100.0 * covered / frames.Count:F1}% of frames, {(idSet.Count == 0 ? 0 : 100.0 * idCovered / idSet.Count):F1}% of identifiers ({idCovered}/{idSet.Count})"));
        }
    }

    private static async Task MergeAsync(string[] a, TextWriter o, TextWriter e, CancellationToken ct)
    {
        var (pos, _) = Parse(a, 3, int.MaxValue, []);
        var sources = new List<IReadOnlyList<CanFrame>>();
        var offsets = new List<int>();
        var next = 0;
        foreach (var input in pos.Skip(1))
        {
            var frames = await LoadAsync(input, ct);
            sources.Add(frames);
            offsets.Add(next);
            next += frames.Count == 0 ? 0 : frames.Max(f => f.Channel) + 1;
        }
        var merged = TraceTools.Merge(sources, offsets);
        await WriteAsync(pos[0], merged, ct, e);
        await o.WriteLineAsync($"Merged {sources.Count} traces into {merged.Count} frames.");
    }

    private static async Task CutAsync(string[] a, TextWriter o, TextWriter e, CancellationToken ct)
    {
        var (pos, opts) = Parse(a, 2, 2, ["--from", "--to"]);
        var from = ParseSeconds(Required(opts, "--from"), "--from");
        var to = ParseSeconds(Required(opts, "--to"), "--to");
        var frames = await LoadAsync(pos[0], ct);
        var origin = frames.Count == 0 ? 0 : frames.Min(f => f.TimestampNanoseconds);
        var cut = TraceTools.Cut(frames, checked(origin + from), checked(origin + to));
        await WriteAsync(pos[1], cut, ct, e);
        await o.WriteLineAsync($"Kept {cut.Count} of {frames.Count} frames.");
    }

    private static async Task ShiftAsync(string[] a, TextWriter o, TextWriter e, CancellationToken ct)
    {
        var (pos, opts) = Parse(a, 2, 2, ["--seconds"]);
        var shift = ParseSeconds(Required(opts, "--seconds"), "--seconds");
        var shifted = TraceTools.ShiftTime(await LoadAsync(pos[0], ct), shift);
        await WriteAsync(pos[1], shifted, ct, e);
        await o.WriteLineAsync($"Shifted {shifted.Count} frames.");
    }

    private static async Task ExportSignalsAsync(string[] a, TextWriter o, CancellationToken ct)
    {
        var (pos, opts) = Parse(a, 3, 3, ["--signals"]);
        var wanted = opts.TryGetValue("--signals", out var s)
            ? new HashSet<string>(s[0].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), StringComparer.Ordinal)
            : null;
        var frames = await LoadAsync(pos[0], ct);
        var decoder = new DbcDecoder(new DbcParser().ParseFile(pos[1]));
        var rows = 0;
        await using var w = new StreamWriter(pos[2], false, new UTF8Encoding(false));
        await w.WriteLineAsync("time_s,channel,message,signal,raw,physical,unit");
        foreach (var f in frames)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var v in decoder.Decode(f))
            {
                if (wanted is not null && !wanted.Contains(v.Signal)) continue;
                await w.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
                    $"{Sec(v.TimestampNanoseconds)},{v.Channel},{Csv(v.Message)},{Csv(v.Signal)},{v.RawValue:R},{v.PhysicalValue:R},{Csv(v.Unit)}"));
                rows++;
            }
        }
        await o.WriteLineAsync($"Wrote {rows} signal samples.");
    }

    // ---- report commands ----

    private static async Task<int> ReportAsync(string[] a, TextWriter o, CancellationToken ct)
    {
        if (a.Length == 0) throw new UsageException("report needs a sub-command (j1939, xcp, obd, lin, flexray, canxl, candidates, busload, suggest-dbc, e2e, signal-stats, timing, faults).");
        var rest = a[1..];
        switch (a[0])
        {
            case "j1939":
            case "obd":
            case "candidates":
            {
                var (pos, _) = Parse(rest, 2, 2, []);
                var frames = await LoadAsync(pos[0], ct);
                var table = a[0] switch { "j1939" => ReportTables.J1939(frames), "obd" => ReportTables.Obd(frames), _ => ReportTables.Candidates(frames) };
                await WriteTableAsync(pos[1], table, ct);
                await o.WriteLineAsync($"{a[0]}: {table.Rows.Count} rows from {frames.Count} frames written to {Path.GetFileName(pos[1])}.");
                return 0;
            }
            case "xcp":
            {
                var (pos, opts) = Parse(rest, 2, 2, ["--cmd-id", "--res-id", "--daq-ids", "--protocol", "--channel", "--extended", "--timeout-ms", "--a2l"]);
                var frames = await LoadAsync(pos[0], ct);
                XcpEndpoint? endpoint = null;
                if (opts.ContainsKey("--cmd-id") || opts.ContainsKey("--res-id"))
                {
                    var ext = opts.TryGetValue("--extended", out var e) && e[0].Equals("true", StringComparison.OrdinalIgnoreCase);
                    var proto = !opts.TryGetValue("--protocol", out var pr) || pr[0].Equals("xcp", StringComparison.OrdinalIgnoreCase) ? XcpProtocol.Xcp
                        : pr[0].Equals("ccp", StringComparison.OrdinalIgnoreCase) ? XcpProtocol.Ccp
                        : throw new UsageException("--protocol expects xcp or ccp.");
                    int? channel = null;
                    if (opts.TryGetValue("--channel", out var ch))
                        channel = int.TryParse(ch[0], NumberStyles.None, CultureInfo.InvariantCulture, out var c) ? c : throw new UsageException("--channel expects a whole number.");
                    var daq = opts.TryGetValue("--daq-ids", out var dl)
                        ? dl[0].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(s => ParseHexId(s, "--daq-ids")).ToList()
                        : null;
                    endpoint = new XcpEndpoint(proto, ParseHexId(Required(opts, "--cmd-id"), "--cmd-id"), ParseHexId(Required(opts, "--res-id"), "--res-id"),
                        ext, ext, channel, daq, ext);
                }
                else if (opts.Count > 0 && opts.Keys.Any(k => k != "--timeout-ms" && k != "--a2l"))
                    throw new UsageException("--cmd-id and --res-id must be given together; other options need them.");

                XcpOptions? xo = null;
                RbgCanInsight.Analysis.Xcp.A2l.A2lDatabase? a2l = null;
                if (opts.TryGetValue("--a2l", out var a2lPath))
                {
                    try { a2l = RbgCanInsight.Analysis.Xcp.A2l.A2lReader.ReadFile(a2lPath[0]); }
                    catch (RbgCanInsight.Analysis.Xcp.A2l.A2lFormatException ex) { throw new UsageException("--a2l: " + ex.Message); }
                }
                if (opts.TryGetValue("--timeout-ms", out var to))
                    xo = new XcpOptions((long)(ParsePositive(to[0], "--timeout-ms") * 1_000_000), a2l);
                else if (a2l is not null) xo = new XcpOptions(A2l: a2l);
                XcpReportTable report;
                try { report = XcpReportRows.Build(frames, endpoint, xo); }
                catch (ArgumentException ex) { throw new UsageException(ex.Message); }
                var table = new ReportTable([.. report.Headers]);
                table.Rows.AddRange(report.Rows);
                await WriteTableAsync(pos[1], table, ct);
                var detections = report.Rows.Count(r => r[3] == "Detection" && r[7] != "");
                var cmds = report.Rows.Count(r => r[3] == "Command");
                var errs = report.Rows.Count(r => r[3] == "Command" && r[7].StartsWith("Error", StringComparison.Ordinal));
                var unanswered = report.Rows.Count(r => r[3] == "Command" && r[7] == "Unanswered");
                await o.WriteLineAsync($"xcp: {(endpoint is null ? detections + " detected pair(s); " : "")}{cmds} command(s), {errs} error response(s), {unanswered} unanswered; {report.Rows.Count} rows from {frames.Count} frames written to {Path.GetFileName(pos[1])}.");
                return 0;
            }
            case "lin":
            case "flexray":
            case "canxl":
            {
                var (pos, opts) = Parse(rest, 2, 2, ["--ldf"]);
                if (a[0] != "lin" && opts.ContainsKey("--ldf")) throw new UsageException("--ldf only applies to 'report lin'.");
                if (!File.Exists(pos[0])) throw new FileNotFoundException($"File not found: {Path.GetFileName(pos[0])}");
                if (!TraceImporter.IsSupported(pos[0])) throw new NotSupportedException($"Unsupported trace format '{Path.GetExtension(pos[0])}'.");
                var imported = await TraceImporter.ReadAllAsync(pos[0], ct);
                BusTable table;
                int count;
                if (a[0] == "lin")
                {
                    LdfFile? ldf = null;
                    if (opts.TryGetValue("--ldf", out var ldfPath)) ldf = LdfParser.Parse(await File.ReadAllTextAsync(ldfPath[0], ct));
                    table = BusReportTables.LinFrames(imported.BusFrames.Lin, ldf);
                    count = imported.BusFrames.Lin.Count;
                }
                else if (a[0] == "canxl")
                {
                    table = BusReportTables.CanXlList(imported.BusFrames.CanXl);
                    count = imported.BusFrames.CanXl.Count;
                }
                else
                {
                    table = BusReportTables.FlexRayMatrix(imported.BusFrames.FlexRay);
                    count = imported.BusFrames.FlexRay.Count;
                }
                var report = new ReportTable(table.Headers);
                foreach (var row in table.Rows) report.Add(row.Cast<object>().ToArray());
                await WriteTableAsync(pos[1], report, ct);
                var problems = a[0] == "canxl" ? 0 : a[0] == "lin"
                    ? LinFrameAnalyzer.Check(imported.BusFrames.Lin).Count(c => c.Checksum == LinChecksumStatus.Mismatch || c.Pid == LinPidStatus.Invalid || c.Frame.Event is LinEventKind.CrcError or LinEventKind.SendError or LinEventKind.ReceiveError)
                    : FlexRayAnalyzer.Analyze(imported.BusFrames.FlexRay).Rows.Sum(r => r.ErrorFrames + r.MissingInstances.Count);
                var busName = a[0] switch { "lin" => "LIN", "canxl" => "CAN XL", _ => "FlexRay" };
                await o.WriteLineAsync($"{a[0]}: {count} {busName} record(s), {problems} with problems, {table.Rows.Count} rows written to {Path.GetFileName(pos[1])}." +
                    (count == 0 ? (a[0] == "canxl" ? " No such records were found (CAN XL is read from candump -L logs and SocketCAN pcap/pcapng files)."
                        : " No such records were found (only BLF files are read for this bus).") : ""));
                return 0;
            }
            case "busload":
            {
                var (pos, opts) = Parse(rest, 2, 2, ["--kbps", "--data-kbps"]);
                var kbps = ParsePositive(Required(opts, "--kbps"), "--kbps");
                double? data = opts.TryGetValue("--data-kbps", out var d) ? ParsePositive(d[0], "--data-kbps") : null;
                var frames = await LoadAsync(pos[0], ct);
                var table = ReportTables.BusLoad(frames, kbps, data);
                await WriteTableAsync(pos[1], table, ct);
                await o.WriteLineAsync($"busload: {table.Rows.Count} rows from {frames.Count} frames written to {Path.GetFileName(pos[1])}.");
                return 0;
            }
            case "suggest-dbc":
            {
                var (pos, _) = Parse(rest, 3, int.MaxValue, []);
                var frames = await LoadAsync(pos[0], ct);
                var dbs = pos.Skip(2).Select(p => (Path.GetFileName(p), new DbcParser().ParseFile(p))).ToList();
                var table = ReportTables.SuggestDbc(frames, dbs);
                await WriteTableAsync(pos[1], table, ct);
                var best = table.Rows.Where(r => r[1] == "1").Select(r => $"ch{r[0]}={r[2]} ({r[3]})");
                await o.WriteLineAsync($"suggest-dbc: {dbs.Count} DBC(s) ranked; best {string.Join(", ", best)}; {table.Rows.Count} rows written to {Path.GetFileName(pos[1])}.");
                return 0;
            }
            case "signal-stats":
            {
                var (pos, opts) = Parse(rest, 3, 3, ["--signal"]);
                var spec = Required(opts, "--signal");
                var dot = spec.IndexOf('.');
                if (dot <= 0 || dot == spec.Length - 1) throw new UsageException("--signal expects Message.Signal.");
                var msg = spec[..dot];
                var sig = spec[(dot + 1)..];
                var frames = await LoadAsync(pos[0], ct);
                var decoder = new DbcDecoder(new DbcParser().ParseFile(pos[1]));
                var series = new List<AnalyticsSample>();
                foreach (var f in frames)
                {
                    ct.ThrowIfCancellationRequested();
                    foreach (var v in decoder.Decode(f))
                        if (v.Message == msg && v.Signal == sig && double.IsFinite(v.PhysicalValue))
                            series.Add(new AnalyticsSample(v.TimestampNanoseconds, v.PhysicalValue));
                }
                if (series.Count == 0)
                {
                    await o.WriteLineAsync($"signal-stats: no samples for {spec}.");
                    return 1;
                }
                var table = ReportTables.SignalStats(spec, series);
                await WriteTableAsync(pos[2], table, ct);
                await o.WriteLineAsync($"signal-stats: {series.Count} samples of {spec}; {table.Rows.Count} rows written to {Path.GetFileName(pos[2])}.");
                return 0;
            }
            case "e2e": return await E2eAsync(rest, o, ct);
            case "timing": return await TimingReportAsync(rest, o, ct);
            case "faults": return await FaultsReportAsync(rest, o, ct);
            default: throw new UsageException($"Unknown report '{a[0]}'.");
        }
    }

    private static async Task<int> E2eAsync(string[] a, TextWriter o, CancellationToken ct)
    {
        var (pos, opts) = Parse(a, 2, 2, ["--id", "--channel", "--profile", "--data-id", "--cycle-ms"]);
        var idArg = Required(opts, "--id").Trim();
        var idText = idArg;
        var extended = idText.EndsWith('x') || idText.EndsWith('X');
        if (extended) idText = idText[..^1];
        if (idText.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) idText = idText[2..];
        if (!uint.TryParse(idText, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var id))
            throw new UsageException("--id expects a hex identifier (append x for a 29-bit id).");
        if (id > 0x7FF) extended = true;
        int? channel = null;
        if (opts.TryGetValue("--channel", out var ch))
        {
            if (!int.TryParse(ch[0], NumberStyles.None, CultureInfo.InvariantCulture, out var c)) throw new UsageException("--channel expects a whole number.");
            channel = c;
        }
        var profile = Required(opts, "--profile").ToLowerInvariant() switch
        {
            "p1" => E2eProfile.P01,
            "p2" => E2eProfile.P02,
            "p5" => E2eProfile.P05,
            _ => throw new UsageException("--profile expects p1, p2 or p5.")
        };
        var dataId = Required(opts, "--data-id");
        var cycle = opts.TryGetValue("--cycle-ms", out var cm) ? cm[0] : null;

        var frames = await LoadAsync(pos[0], ct);
        var entries = E2eCheckLogic.ListIds(frames).Where(e => e.Id == id && e.Extended == extended && (channel is null || e.Channel == channel)).ToList();
        if (entries.Count == 0)
        {
            await o.WriteLineAsync($"e2e: FAIL no data frames for id {idArg}{(channel is null ? "" : " on channel " + channel)}.");
            return 1;
        }
        var entry = entries[0];
        var stream = E2eCheckLogic.StreamFrames(frames, entry);
        var (cfg, error) = E2eCheckLogic.BuildConfig(entry, profile, dataId, cycle, null, null, E2eCheckLogic.DominantLength(stream));
        if (cfg is null) throw new UsageException(error ?? "Invalid E2E settings.");
        var report = E2eChecker.Check(stream, [cfg]);
        if (report.Summaries.Count == 0) throw new InvalidOperationException(report.ConfigError ?? "The E2E check produced no result.");
        var summary = report.Summaries[0];
        var rows = E2eCheckLogic.BuildRows(summary);
        var table = new ReportTable("Message", "Time (s)", "Status", "Counter", "Received CRC", "Expected CRC", "Lost frames", "Gap (ms)");
        foreach (var r in rows) table.Add(r.Message, r.Time, r.Status, r.Counter, r.ReceivedCrc, r.ExpectedCrc, r.Lost, r.GapMs);
        await WriteTableAsync(pos[1], table, ct);
        var outcome = E2eCheckLogic.Summarize(summary);
        var label = outcome.Chip switch { E2eChip.Green => "PASS", E2eChip.Amber => "WARN", _ => "FAIL" };
        await o.WriteLineAsync($"e2e: {label} {outcome.Headline}; ok {summary.Ok}, wrong CRC {summary.WrongCrc}, wrong sequence {summary.WrongSequence}, repeated {summary.Repeated}, timeouts {summary.TimeoutCount}; {rows.Count} rows written to {Path.GetFileName(pos[1])}.");
        return outcome.Chip == E2eChip.Red ? 1 : 0;
    }

    private static async Task WriteTableAsync(string path, ReportTable table, CancellationToken ct)
    {
        await using var w = new StreamWriter(path, false, new UTF8Encoding(false));
        await w.WriteLineAsync(string.Join(",", table.Headers.Select(Csv)));
        foreach (var row in table.Rows)
        {
            ct.ThrowIfCancellationRequested();
            await w.WriteLineAsync(string.Join(",", row.Select(Csv)));
        }
    }

    private static double ParsePositive(string text, string name)
    {
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || !double.IsFinite(v) || v <= 0)
            throw new UsageException($"{name} expects a positive number, got '{text}'.");
        return v;
    }

    // ---- helpers ----

    public static async Task<List<CanFrame>> LoadAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path)) throw new FileNotFoundException($"File not found: {Path.GetFileName(path)}");
        if (!TraceImporter.IsSupported(path))
            throw new NotSupportedException($"Unsupported trace format '{Path.GetExtension(path)}'.");
        var list = new List<CanFrame>();
        await foreach (var f in TraceImporter.ReadAsync(path, ct)) list.Add(f);
        return list;
    }

    public static async Task WriteAsync(string path, IReadOnlyList<CanFrame> frames, CancellationToken ct, TextWriter? warnings = null)
    {
        switch (Path.GetExtension(path).ToLowerInvariant())
        {
            case ".asc": await TraceExporter.WriteAscAsync(path, frames, ct); break;
            case ".csv": await TraceExporter.WriteCsvAsync(path, frames, ct); break;
            case ".blf": await TraceExporter.WriteBlfAsync(path, frames, ct); break;
            case ".trc": await TraceExporter.WriteTrcAsync(path, frames, ct); break;
            case ".mf4":
                var r = await Mdf4Writer.WriteAsync(path, frames, ct);
                var skipped = r.RemoteFramesSkipped + r.ErrorFramesSkipped + r.InvalidLengthFramesSkipped + r.ChannelOutOfRangeFramesSkipped;
                if (skipped > 0 && warnings is not null)
                    await warnings.WriteLineAsync($"Warning: {skipped} frame(s) skipped for MDF4 (remote {r.RemoteFramesSkipped}, error {r.ErrorFramesSkipped}, invalid length {r.InvalidLengthFramesSkipped}, channel out of range {r.ChannelOutOfRangeFramesSkipped}).");
                break;
            default: throw new NotSupportedException("Output extension must be .asc, .csv, .blf, .trc or .mf4.");
        }
    }

    private static string Sec(long ns) => (ns / 1e9).ToString("0.000000", CultureInfo.InvariantCulture);

    private static long ParseSeconds(string text, string name)
    {
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var sec) ||
            double.IsNaN(sec) || Math.Abs(sec) > 9e9)
            throw new UsageException($"{name} expects a number of seconds, got '{text}'.");
        return (long)Math.Round(sec * 1e9);
    }

    private static uint ParseHexId(string text, string name) =>
        uint.TryParse(text.Trim().Replace("0x", "", StringComparison.OrdinalIgnoreCase), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var id)
            ? id : throw new UsageException($"{name} expects hexadecimal CAN identifiers, got '{text}'.");

    private static string Required(Dictionary<string, List<string>> opts, string name) =>
        opts.TryGetValue(name, out var v) ? v[0] : throw new UsageException($"Missing {name}.");

    public static string Csv(string s)
    {
        if (s.Length > 0 && s[0] is '=' or '+' or '-' or '@') s = "'" + s;
        return s.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }

    private static (List<string> Positional, Dictionary<string, List<string>> Options) Parse(
        string[] args, int minPos, int maxPos, string[] known, string[]? multi = null)
    {
        var pos = new List<string>();
        var opts = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith("--", StringComparison.Ordinal))
            {
                var name = args[i];
                if (!known.Contains(name)) throw new UsageException($"Unknown option '{name}'.");
                if (++i >= args.Length) throw new UsageException($"Option '{name}' needs a value.");
                if (!opts.TryGetValue(name, out var list)) opts[name] = list = [];
                else if (multi is null || !multi.Contains(name)) throw new UsageException($"Option '{name}' given twice.");
                list.Add(args[i]);
            }
            else pos.Add(args[i]);
        }
        if (pos.Count < minPos || pos.Count > maxPos) throw new UsageException("Wrong number of arguments.");
        return (pos, opts);
    }
}
