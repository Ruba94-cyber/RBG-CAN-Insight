using System.Globalization;
using System.Text;
using RbgCanInsight.Dbc;
using RbgCanInsight.DiagData;
using RbgCanInsight.Diagnostics;

namespace RbgCanInsight.Cli;

/// <summary>Database and diagnostic-description commands for CI use: consistency checks, comparison,
/// ODX export and the diagnostic timing / fault-memory reports. Exit codes: 0 clean, 1 findings or
/// differences or errors, 2 usage.</summary>
public static partial class CliCommands
{
    private static int SeverityRank(string name) => name switch
    {
        "Error" => 0,
        "Warning" => 1,
        _ => 2
    };

    private static int ParseFailOn(Dictionary<string, List<string>> opts) =>
        !opts.TryGetValue("--fail-on", out var v)
            ? 0
            : v[0].ToLowerInvariant() switch
            {
                "error" => 0,
                "warning" => 1,
                "review" => 2,
                _ => throw new UsageException("--fail-on expects error, warning or review.")
            };

    private static async Task<int> CheckDbcAsync(string[] a, TextWriter o, CancellationToken ct)
    {
        var (pos, opts) = Parse(a, 1, 1, ["--out", "--fail-on"]);
        var failOn = ParseFailOn(opts);
        if (!File.Exists(pos[0])) throw new FileNotFoundException($"File not found: {Path.GetFileName(pos[0])}");
        var database = new DbcParser().ParseFile(pos[0]);
        var report = DbcConsistencyChecker.Check(database, new DbcConsistencyOptions { SourceText = await File.ReadAllTextAsync(pos[0], ct) });
        if (opts.TryGetValue("--out", out var outPath))
            await File.WriteAllTextAsync(outPath[0], report.ToCsv(), new UTF8Encoding(true), ct);
        var failing = report.Findings.Count(f => SeverityRank(f.Severity.ToString()) <= failOn);
        await o.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
            $"check-dbc: {(failing == 0 ? "PASS" : "FAIL")} {Path.GetFileName(pos[0])}: {report.Count(DbcEngineeringSeverity.Error)} error(s), {report.Count(DbcEngineeringSeverity.Warning)} warning(s), {report.Count(DbcEngineeringSeverity.Review)} review item(s), {report.RulesEvaluated.Count} rule(s) evaluated, {report.RulesSkipped.Count} skipped."));
        return failing == 0 ? 0 : 1;
    }

    private static async Task<int> CheckCddAsync(string[] a, TextWriter o, CancellationToken ct)
    {
        var (pos, opts) = Parse(a, 1, 1, ["--out", "--fail-on"]);
        var failOn = ParseFailOn(opts);
        var catalogs = LoadCatalogs(pos[0]);
        var report = CatalogConsistencyChecker.Check(catalogs);
        if (opts.TryGetValue("--out", out var outPath))
            await File.WriteAllTextAsync(outPath[0], report.ToCsv(), new UTF8Encoding(true), ct);
        var failing = report.Findings.Count(f => SeverityRank(f.Severity.ToString()) <= failOn);
        await o.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
            $"check-cdd: {(failing == 0 ? "PASS" : "FAIL")} {Path.GetFileName(pos[0])}: {catalogs.Count} catalog(s), {report.Count(CatalogSeverity.Error)} error(s), {report.Count(CatalogSeverity.Warning)} warning(s), {report.Count(CatalogSeverity.Review)} review item(s), {report.RulesEvaluated.Count} rule(s) evaluated."));
        return failing == 0 ? 0 : 1;
    }

    private static IReadOnlyList<DidCatalog> LoadCatalogs(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException($"File not found: {Path.GetFileName(path)}");
        return DidCatalog.LoadAll(path);
    }

    private static async Task<int> CompareAsync(string[] a, TextWriter o, CancellationToken ct)
    {
        var (pos, opts) = Parse(a, 2, 2, ["--out"]);
        var isDbc = pos.Select(p => Path.GetExtension(p).Equals(".dbc", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (isDbc[0] != isDbc[1])
            throw new UsageException("compare needs two DBC files or two CDD/ODX/PDX files, not a mix.");

        var table = new ReportTable();
        string summary;
        bool differs;
        if (isDbc[0])
        {
            foreach (var p in pos)
                if (!File.Exists(p)) throw new FileNotFoundException($"File not found: {Path.GetFileName(p)}");
            var report = DbcDefinitionComparer.Compare(new DbcParser().ParseFile(pos[0]), new DbcParser().ParseFile(pos[1]));
            table = new ReportTable("Scope", "Identifier", "Format", "Baseline message", "Current message", "Signal", "Change", "Impact", "Differences", "Needs review");
            foreach (var r in report.Rows.Where(r => r.Change != DbcDefinitionChange.Unchanged))
                table.Add(r.Scope, r.IdentifierText, r.FrameFormat, r.BaselineMessage, r.CurrentMessage, r.Signal, r.Change, r.Impact, r.Differences, r.RequiresReview);
            summary = report.Summary;
            differs = report.Rows.Any(r => r.Change != DbcDefinitionChange.Unchanged);
        }
        else
        {
            var report = DiagnosticDefinitionComparer.Compare(LoadCatalogs(pos[0]), LoadCatalogs(pos[1]));
            table = new ReportTable("Scope", "ECU", "Identifier", "Path", "Change", "Impact", "Changed fields", "Baseline", "Current", "Needs review");
            foreach (var r in report.Rows.Where(r => r.Change != DiagnosticDefinitionChange.Unchanged))
                table.Add(r.Scope, r.Ecu, r.Identifier, r.Path, r.Change, r.Impact, r.ChangedFields, r.BaselineName, r.CurrentName, r.RequiresReview);
            summary = report.Summary;
            differs = report.Rows.Any(r => r.Change != DiagnosticDefinitionChange.Unchanged);
        }

        if (opts.TryGetValue("--out", out var outPath))
            await WriteTableAsync(outPath[0], table, ct);
        await o.WriteLineAsync($"compare: {(differs ? "DIFFERENT" : "IDENTICAL")} {Path.GetFileName(pos[0])} vs {Path.GetFileName(pos[1])}: {summary}");
        return differs ? 1 : 0;
    }

    private static async Task<int> ExportOdxAsync(string[] a, TextWriter o, CancellationToken ct)
    {
        var (pos, opts) = Parse(a, 2, 2, ["--ecu"]);
        ct.ThrowIfCancellationRequested();
        var catalogs = LoadCatalogs(pos[0]);
        DidCatalog selected;
        if (opts.TryGetValue("--ecu", out var ecu))
        {
            var matches = catalogs.Where(c => string.Equals(c.Ecu, ecu[0], StringComparison.OrdinalIgnoreCase)).ToArray();
            selected = matches.Length == 1 ? matches[0]
                : throw new InvalidOperationException(matches.Length == 0
                    ? $"No ECU named '{ecu[0]}'. Available: {string.Join(", ", catalogs.Select(c => c.Ecu))}."
                    : $"ECU name '{ecu[0]}' is ambiguous ({matches.Length} catalogs).");
        }
        else if (catalogs.Count == 1)
            selected = catalogs[0];
        else
            throw new InvalidOperationException(
                $"The file holds {catalogs.Count} ECU variants; choose one with --ecu. Available: {string.Join(", ", catalogs.Select(c => c.Ecu))}.");

        OdxCatalogWriter.Write(selected, pos[1]);
        var problems = OdxCatalogWriter.Validate(pos[1]);
        await o.WriteLineAsync(problems.Count == 0
            ? $"export-odx: wrote {Path.GetFileName(pos[1])} for ECU '{selected.Ecu}' ({selected.Parameters.Count} DIDs, {selected.DtcDefinitions.Count} DTCs, {selected.RoutineDefinitions.Count} routines); structural self-check passed (not an XSD validation)."
            : $"export-odx: wrote {Path.GetFileName(pos[1])} but the structural self-check found {problems.Count} problem(s): {string.Join(" | ", problems.Take(5))}");
        return problems.Count == 0 ? 0 : 1;
    }

    private static async Task<int> TimingReportAsync(string[] rest, TextWriter o, CancellationToken ct)
    {
        var (pos, opts) = Parse(rest, 2, 2, ["--p2-ms", "--p2star-ms", "--s3-ms", "--nbs-ms", "--ncr-ms"]);
        var options = new DiagnosticTimingOptions
        {
            P2ServerMaxMilliseconds = opts.TryGetValue("--p2-ms", out var p2) ? ParsePositive(p2[0], "--p2-ms") : 50,
            P2StarServerMaxMilliseconds = opts.TryGetValue("--p2star-ms", out var p2s) ? ParsePositive(p2s[0], "--p2star-ms") : 5000,
            S3ServerMilliseconds = opts.TryGetValue("--s3-ms", out var s3) ? ParsePositive(s3[0], "--s3-ms") : 5000,
            NBsMilliseconds = opts.TryGetValue("--nbs-ms", out var nbs) ? ParsePositive(nbs[0], "--nbs-ms") : 1000,
            NCrMilliseconds = opts.TryGetValue("--ncr-ms", out var ncr) ? ParsePositive(ncr[0], "--ncr-ms") : 1000
        };
        var frames = await LoadAsync(pos[0], ct);
        var transactions = UdsTransactionAnalyzer.Analyze(frames, [], null, ct);
        var report = DiagnosticTimingAnalyzer.Analyze(transactions, frames, options);
        await File.WriteAllTextAsync(pos[1], report.ToCsv(), new UTF8Encoding(true), ct);
        await o.WriteLineAsync($"timing: {report.ToSummaryText()} {transactions.Count} UDS transaction(s) from {frames.Count} frames written to {Path.GetFileName(pos[1])}.");
        return report.Count(DiagnosticTimingSeverity.Error) > 0 ? 1 : 0;
    }

    private static async Task<int> FaultsReportAsync(string[] rest, TextWriter o, CancellationToken ct)
    {
        var (pos, _) = Parse(rest, 2, 2, []);
        var frames = await LoadAsync(pos[0], ct);
        var transactions = UdsTransactionAnalyzer.Analyze(frames, [], null, ct);
        var insight = DiagnosticInsightAnalyzer.Analyze(transactions, []);
        var rows = FaultMemoryTable.Build(insight.Faults);
        await File.WriteAllTextAsync(pos[1], FaultMemoryTable.ToCsv(rows), new UTF8Encoding(true), ct);
        await o.WriteLineAsync($"faults: {rows.Count} DTC(s) on {rows.Select(r => r.Ecu).Distinct().Count()} ECU(s) from {frames.Count} frames written to {Path.GetFileName(pos[1])}.");
        return 0;
    }
}
