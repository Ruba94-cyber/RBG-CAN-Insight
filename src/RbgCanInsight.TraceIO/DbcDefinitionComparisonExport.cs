using System.Globalization;
using RbgCanInsight.Dbc;

namespace RbgCanInsight.TraceIO;

/// <summary>
/// Read-only DBC definition change report. This is based on the supported
/// parsed DBC subset, not a byte-for-byte source comparison or observed trace.
/// </summary>
public static class DbcDefinitionComparisonExport
{
    public static IReadOnlyList<ExportSheet> BuildSheets(
        DbcDatabase baseline, DbcDatabase current,
        DbcDefinitionDiffReport report, string baselineFile, string currentFile)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(report);
        var summary = new (string Metric, string Value)[]
        {
            ("Baseline file", Path.GetFileName(baselineFile)),
            ("Current file", Path.GetFileName(currentFile)),
            ("Baseline database", baseline.Name),
            ("Current database", current.Name),
            ("Baseline messages", baseline.MessageDefinitionCount.ToString(CultureInfo.InvariantCulture)),
            ("Current messages", current.MessageDefinitionCount.ToString(CultureInfo.InvariantCulture)),
            ("Baseline signals", baseline.AllSignals.Count().ToString(CultureInfo.InvariantCulture)),
            ("Current signals", current.AllSignals.Count().ToString(CultureInfo.InvariantCulture)),
            ("Rows compared", report.Rows.Count.ToString(CultureInfo.InvariantCulture)),
            ("Added", report.Added.ToString(CultureInfo.InvariantCulture)),
            ("Removed", report.Removed.ToString(CultureInfo.InvariantCulture)),
            ("Changed", report.Changed.ToString(CultureInfo.InvariantCulture)),
            ("Ambiguous", report.Ambiguous.ToString(CultureInfo.InvariantCulture)),
            ("Unchanged", report.Unchanged.ToString(CultureInfo.InvariantCulture)),
            ("Requires review", report.RequiresReview.ToString(CultureInfo.InvariantCulture)),
            ("Baseline parser warnings", report.BaselineWarnings.Count.ToString(CultureInfo.InvariantCulture)),
            ("Current parser warnings", report.CurrentWarnings.Count.ToString(CultureInfo.InvariantCulture)),
            ("Comparison method", "Exact CAN ID + 11/29-bit format; signal name within exact frame"),
            ("Limitations", "Parsed source definitions only, not proprietary/unknown source metadata or trace observations"),
            ("Read-only", "No original DBC files, active decoder or user mappings are modified")
        };
        IReadOnlyList<string>[] differences = report.Rows.Select(row => (IReadOnlyList<string>)new[]
        {
            row.Scope,
            row.IdentifierText,
            row.FrameFormat,
            row.Signal,
            row.Change.ToString(),
            row.Impact.ToString(),
            row.RequiresReview ? "YES" : "NO",
            row.Differences,
            row.BaselineMessage,
            row.CurrentMessage,
            row.BaselineDefinition,
            row.CurrentDefinition
        }).ToArray();
        var warnings = report.BaselineWarnings
            .Select(value => (IReadOnlyList<string>)new[] { "Baseline", value })
            .Concat(report.CurrentWarnings.Select(value =>
                (IReadOnlyList<string>)new[] { "Current", value }))
            .ToArray();
        return
        [
            new ExportSheet("DBC comparison summary", ["Metric", "Value"],
                summary.Select(row => (IReadOnlyList<string>)new[] { row.Metric, row.Value }).ToArray()),
            new ExportSheet("DBC differences",
                ["Type", "CAN ID", "Format", "Signal", "Change", "Impact", "Review",
                 "Changed fields", "Baseline message", "Current message",
                 "Baseline definition", "Current definition"], differences),
            new ExportSheet("DBC parser warnings", ["Version", "Source warning"], warnings)
        ];
    }

    /// <summary>Stage a complete XLSX or CSV ZIP before replacing an old report.
    /// This method does not update either DBC source file.</summary>
    public static void Write(string path, IReadOnlyList<ExportSheet> sheets, bool csvZip)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(sheets);
        var fullPath = Path.GetFullPath(path);
        var temporary = fullPath + ".partial-" + Guid.NewGuid().ToString("N");
        try
        {
            if (csvZip) StructuredCsvArchiveExport.Write(temporary, sheets);
            else StructuredWorkbookExport.Write(temporary, sheets);
            File.Move(temporary, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
