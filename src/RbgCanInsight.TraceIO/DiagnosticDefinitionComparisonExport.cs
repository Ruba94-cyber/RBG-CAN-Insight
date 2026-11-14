using System.Globalization;
using RbgCanInsight.DiagData;

namespace RbgCanInsight.TraceIO;

/// <summary>
/// Read-only parsed CDD/ODX/PDX definition comparison export. Source metadata,
/// declared parameter positions and warnings are never proof of live ECU values.
/// </summary>
public static class DiagnosticDefinitionComparisonExport
{
    public static IReadOnlyList<ExportSheet> BuildSheets(
        IReadOnlyList<DidCatalog> baseline, IReadOnlyList<DidCatalog> current,
        DiagnosticDefinitionDiffReport report, string baselineFile, string currentFile)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(report);

        var summary = new (string Key, string Value)[]
        {
            ("Baseline file", PortableFileName(baselineFile)),
            ("Current file", PortableFileName(currentFile)),
            ("Baseline catalog variants", Num(baseline.Count)),
            ("Current catalog variants", Num(current.Count)),
            ("Baseline DIDs", Num(baseline.Sum(item => item.Parameters.Count))),
            ("Current DIDs", Num(current.Sum(item => item.Parameters.Count))),
            ("Baseline Engineering PARs", Num(baseline.Sum(item =>
                item.Parameters.Sum(did => did.EngineeringParameters.Count)))),
            ("Current Engineering PARs", Num(current.Sum(item =>
                item.Parameters.Sum(did => did.EngineeringParameters.Count)))),
            ("Baseline DTCs", Num(baseline.Sum(item => item.DtcDefinitions.Count))),
            ("Current DTCs", Num(current.Sum(item => item.DtcDefinitions.Count))),
            ("Baseline routines", Num(baseline.Sum(item => item.RoutineDefinitions.Count))),
            ("Current routines", Num(current.Sum(item => item.RoutineDefinitions.Count))),
            ("Compared definitions", Num(report.Rows.Count)),
            ("Added", Num(report.Added)),
            ("Removed", Num(report.Removed)),
            ("Changed", Num(report.Changed)),
            ("Unchanged", Num(report.Unchanged)),
            ("Ambiguous", Num(report.Ambiguous)),
            ("Requiring review", Num(report.RequiresReview)),
            ("Collision policy", "Duplicate ECU/type/identifier identities are ambiguous and their children are not automatically paired"),
            ("Engineering PAR positioning", "Hierarchy paths are source-extraction ordinals; changed ordering is not proof of semantic identity"),
            ("Scope", "Supported parsed CDD/ODX/PDX fields only; unknown OEM metadata not certified identical"),
            ("Safety", "Read-only offline comparison; no ECU operations or input file modification")
        };
        IReadOnlyList<string>[] rows = report.Rows.Select(row => (IReadOnlyList<string>)new[]
        {
            row.Scope, row.Ecu, row.Identifier, row.Path,
            row.Change.ToString(), row.Impact.ToString(),
            row.RequiresReview ? "YES" : "NO", row.ChangedFields,
            row.BaselineName, row.CurrentName,
            row.BaselineSource, row.CurrentSource,
            row.BaselineDefinition, row.CurrentDefinition
        }).ToArray();
        var issues = baseline.SelectMany(catalog => catalog.Parameters
                .SelectMany(did => did.EngineeringIssues.Select(issue =>
                    (IReadOnlyList<string>)new[]
                    {
                        "Baseline", PortableFileName(catalog.SourcePath),
                        did.Ecu, did.DidHex, issue
                    })))
            .Concat(current.SelectMany(catalog => catalog.Parameters
                .SelectMany(did => did.EngineeringIssues.Select(issue =>
                    (IReadOnlyList<string>)new[]
                    {
                        "Current", PortableFileName(catalog.SourcePath),
                        did.Ecu, did.DidHex, issue
                    }))))
            .ToArray();

        return
        [
            new ExportSheet("Diagnostic diff summary", ["Metric", "Value"],
                summary.Select(item => (IReadOnlyList<string>)new[] { item.Key, item.Value }).ToArray()),
            new ExportSheet("Diagnostic definitions",
                ["Kind", "ECU", "DID or code", "Field / PAR path",
                 "Change", "Impact", "Review required", "Changed properties",
                 "Baseline name", "Current name", "Baseline source", "Current source",
                 "Baseline definition", "Current definition"], rows),
            new ExportSheet("Definition issues",
                ["Version", "Source", "ECU", "DID", "Extraction or variant issue"], issues)
        ];
    }

    /// <summary>Promote a complete XLSX/CSV-ZIP report only after it has been
    /// written successfully; never touch the diagnostic description sources.</summary>
    public static void Write(string path, IReadOnlyList<ExportSheet> sheets, bool csvZip)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(sheets);
        var destination = Path.GetFullPath(path);
        var partial = destination + ".partial-" + Guid.NewGuid().ToString("N");
        try
        {
            if (csvZip)
                StructuredCsvArchiveExport.Write(partial, sheets);
            else
                StructuredWorkbookExport.Write(partial, sheets);
            File.Move(partial, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(partial)) File.Delete(partial);
        }
    }

    private static string Num(int number) => number.ToString(CultureInfo.InvariantCulture);

    private static string PortableFileName(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var normalized = value.Replace('\\', '/');
        return normalized[(normalized.LastIndexOf('/') + 1)..];
    }
}
