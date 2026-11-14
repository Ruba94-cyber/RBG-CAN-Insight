using System.Globalization;
using RbgCanInsight.DiagData;

namespace RbgCanInsight.TraceIO;

/// <summary>Source-backed, read-only export of saved mapping comparisons.
/// Source document names are display labels only, never cryptographic proof.</summary>
public static class MappingComparisonExport
{
    public static IReadOnlyList<ExportSheet> BuildSheets(
        MappingDocument baseline, MappingDocument current,
        MappingComparisonReport comparison, string baselineFile, string currentFile)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(comparison);
        var summary = new (string Name, string Value)[]
        {
            ("Baseline file", Path.GetFileName(baselineFile)),
            ("Current file", Path.GetFileName(currentFile)),
            ("Baseline saved", baseline.SavedAt.ToString("O", CultureInfo.InvariantCulture)),
            ("Current saved", current.SavedAt.ToString("O", CultureInfo.InvariantCulture)),
            ("Baseline records", baseline.Mappings.Count.ToString(CultureInfo.InvariantCulture)),
            ("Current records", current.Mappings.Count.ToString(CultureInfo.InvariantCulture)),
            ("Compared DID/ECU groups", comparison.Rows.Count.ToString(CultureInfo.InvariantCulture)),
            ("Added", comparison.Added.ToString(CultureInfo.InvariantCulture)),
            ("Removed", comparison.Removed.ToString(CultureInfo.InvariantCulture)),
            ("Changed", comparison.Changed.ToString(CultureInfo.InvariantCulture)),
            ("Unchanged", comparison.Unchanged.ToString(CultureInfo.InvariantCulture)),
            ("Ambiguous", comparison.Ambiguous.ToString(CultureInfo.InvariantCulture)),
            ("Unverifiable", comparison.Unverifiable.ToString(CultureInfo.InvariantCulture)),
            ("Requiring review", comparison.RequiresReview.ToString(CultureInfo.InvariantCulture)),
            ("Evidence policy", "Semantic fingerprints cover parsed definitions only. Missing hashes cannot prove equivalence."),
            ("Identity policy", "Duplicate DID/ECU variants are reported ambiguous; no forced automatic pairing."),
            ("Safety", "Comparison never modifies the source maps, DBCs or diagnostic descriptions.")
        };
        var detailHeaders = new[]
        {
            "DID", "ECU", "Comparison", "Needs review", "Reason",
            "Baseline entries", "Current entries",
            "Baseline parameter", "Current parameter",
            "Baseline CAN", "Current CAN",
            "Baseline review", "Current review",
            "DID definition evidence", "CAN definition evidence",
            "Baseline DID fingerprint", "Current DID fingerprint",
            "Baseline CAN fingerprint", "Current CAN fingerprint",
            "Baseline match score", "Current match score",
            "Baseline notes", "Current notes",
            "Baseline ranking reason", "Current ranking reason"
        };
        IReadOnlyList<string>[] rows = comparison.Rows.Select(row => (IReadOnlyList<string>)new[]
        {
            row.DidHex,
            row.Ecu,
            row.Kind.ToString(),
            row.RequiresReview ? "YES" : "NO",
            row.Explanation,
            row.BaselineCount.ToString(CultureInfo.InvariantCulture),
            row.CurrentCount.ToString(CultureInfo.InvariantCulture),
            row.Baseline?.DidName ?? string.Empty,
            row.Current?.DidName ?? string.Empty,
            row.BaselineSignal,
            row.CurrentSignal,
            row.Baseline?.Status.ToString() ?? string.Empty,
            row.Current?.Status.ToString() ?? string.Empty,
            row.DidDefinition.ToString(),
            row.CanDefinition.ToString(),
            row.Baseline?.DidDefinitionFingerprint ?? string.Empty,
            row.Current?.DidDefinitionFingerprint ?? string.Empty,
            row.Baseline?.CanSignalFingerprint ?? string.Empty,
            row.Current?.CanSignalFingerprint ?? string.Empty,
            Score(row.Baseline),
            Score(row.Current),
            row.Baseline?.Notes ?? string.Empty,
            row.Current?.Notes ?? string.Empty,
            row.Baseline?.Reason ?? string.Empty,
            row.Current?.Reason ?? string.Empty
        }).ToArray();

        return [
            new ExportSheet("Comparison summary", ["Metric", "Value"],
                summary.Select(item => (IReadOnlyList<string>)new[] { item.Name, item.Value }).ToArray()),
            new ExportSheet("Map comparison", detailHeaders, rows)
        ];
    }

    /// <summary>Stage the complete workbook/archive alongside the destination,
    /// then replace it in one same-directory move. An interrupted or failed
    /// export does not leave a partial XLSX/ZIP in place of an existing report.</summary>
    public static void Write(string path, IReadOnlyList<ExportSheet> sheets, bool csvZip)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(sheets);
        var destination = Path.GetFullPath(path);
        var temporary = destination + ".partial-" + Guid.NewGuid().ToString("N");
        try
        {
            if (csvZip)
                StructuredCsvArchiveExport.Write(temporary, sheets);
            else
                StructuredWorkbookExport.Write(temporary, sheets);
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static string Score(DidSignalMapping? mapping) =>
        mapping is null ? string.Empty :
        mapping.Confidence.ToString("R", CultureInfo.InvariantCulture);
}
