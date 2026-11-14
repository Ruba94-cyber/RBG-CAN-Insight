using System.Globalization;
using System.Text;

namespace RbgCanInsight.Diagnostics;

/// <summary>ISO 14229-1 DTC status-byte bit names, bit 0 first.</summary>
public static class DtcStatusBits
{
    public static readonly IReadOnlyList<string> Names =
    [
        "testFailed", "testFailedThisOperationCycle", "pendingDTC", "confirmedDTC",
        "testNotCompletedSinceLastClear", "testFailedSinceLastClear",
        "testNotCompletedThisOperationCycle", "warningIndicatorRequested"
    ];

    public static bool IsSet(byte status, int bit) => (status & (1 << bit)) != 0;
}

/// <summary>One row of the per-ECU fault-memory table.</summary>
public sealed record FaultMemoryTableRow(
    string Ecu,
    string CodeText,
    string Name,
    byte? Status,
    string StatusText,
    long FirstSeenNanoseconds,
    long LastSeenNanoseconds,
    int Occurrences,
    bool WasCleared,
    string SnapshotText,
    string ExtendedDataText,
    IReadOnlyList<int> FrameIndices,
    int OriginalSourceIndex = -1,
    int SourceChannel = -1,
    string DefinitionSourceFile = "")
{
    public string Description { get; init; } = string.Empty;
    public bool DefinitionEcuMatched { get; init; }
    public IReadOnlyList<string> DefinitionSourceFiles { get; init; } = [];
    public string DefinitionSourceSheet { get; init; } = string.Empty;
    public IReadOnlyList<string> DefinitionConflicts { get; init; } = [];
    /// <summary>OEM table declarations, not UDS values observed in the trace.</summary>
    public IReadOnlyDictionary<string, string> DefinitionAttributes { get; init; } =
        new Dictionary<string, string>();
    public string Logger => OriginalSourceIndex < 0
        ? "Current recording" : $"Logger {OriginalSourceIndex + 1}";
    public string Bus => SourceChannel < 0
        ? "CAN channel unknown" : $"CAN {SourceChannel + 1}";
    /// <summary>"1"/"0" for each status bit of the latest recorded status; empty when no status byte
    /// was ever reported for this DTC (for example it only appeared in a snapshot identification).</summary>
    public string Bit(int bit) => Status is { } status ? (DtcStatusBits.IsSet(status, bit) ? "1" : "0") : string.Empty;
}

/// <summary>Fault-memory table over the recorded 0x19 traffic: one row per ECU and DTC with status
/// bit columns, first/last seen times and the latest decoded snapshot / extended-data records.
/// It is a record of what the trace showed, not a statement about the ECU's current memory.</summary>
public static class FaultMemoryTable
{
    public static IReadOnlyList<FaultMemoryTableRow> Build(IEnumerable<FaultMemoryEntry> faults) =>
        faults
            .OrderBy(entry => entry.Ecu, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.Code)
            .ThenBy(entry => entry.OriginalSourceIndex)
            .ThenBy(entry => entry.SourceChannel)
            .Select(entry => new FaultMemoryTableRow(
                entry.Ecu, entry.CodeText, entry.Name, entry.CurrentStatus, entry.CurrentStatusText,
                entry.FirstSeenNanoseconds, entry.LastSeenNanoseconds, entry.Occurrences, entry.WasCleared,
                string.Join(" | ", entry.LatestRecords.Where(r => r.Kind == DtcRecordKind.Snapshot).Select(r => r.SummaryText)),
                string.Join(" | ", entry.LatestRecords.Where(r => r.Kind == DtcRecordKind.ExtendedData).Select(r => r.SummaryText)),
                entry.Timeline.SelectMany(item => item.FrameIndices).Distinct().OrderBy(i => i).ToArray(),
                entry.OriginalSourceIndex, entry.SourceChannel, entry.DefinitionSourceFile)
            {
                Description = entry.Description,
                DefinitionEcuMatched = entry.DefinitionEcuMatched,
                DefinitionSourceFiles = entry.DefinitionSourceFiles,
                DefinitionSourceSheet = entry.DefinitionSourceSheet,
                DefinitionConflicts = entry.DefinitionConflicts,
                DefinitionAttributes = entry.DefinitionAttributes
            })
            .ToArray();

    public static string ToCsv(IReadOnlyList<FaultMemoryTableRow> rows)
    {
        var sb = new StringBuilder("ECU,DTC,Name,Status,Status text");
        foreach (var name in DtcStatusBits.Names) sb.Append(',').Append(name);
        sb.Append(",First seen (s),Last seen (s),Occurrences,Cleared,Snapshot,Extended data,Source frames,Original logger,CAN bus,CDD/ODX definition,DTC description,ECU definition verified,All definition files,Excel worksheet,OEM DTC-table reference fields,Competing DTC definitions (reference only)\r\n");
        foreach (var row in rows)
        {
            var cells = new List<string>
            {
                row.Ecu, row.CodeText, row.Name,
                row.Status is { } status ? $"0x{status:X2}" : string.Empty, row.StatusText
            };
            for (var bit = 0; bit < 8; bit++) cells.Add(row.Bit(bit));
            cells.Add((row.FirstSeenNanoseconds / 1e9).ToString("F6", CultureInfo.InvariantCulture));
            cells.Add((row.LastSeenNanoseconds / 1e9).ToString("F6", CultureInfo.InvariantCulture));
            cells.Add(row.Occurrences.ToString(CultureInfo.InvariantCulture));
            cells.Add(row.WasCleared ? "yes" : "no");
            cells.Add(row.SnapshotText);
            cells.Add(row.ExtendedDataText);
            cells.Add(string.Join(' ', row.FrameIndices));
            cells.Add(row.Logger);
            cells.Add(row.Bus);
            cells.Add(row.DefinitionSourceFile);
            cells.Add(row.Description);
            cells.Add(row.DefinitionEcuMatched ? "yes" : "no");
            cells.Add(string.Join("; ", row.DefinitionSourceFiles));
            cells.Add(row.DefinitionSourceSheet);
            cells.Add(string.Join(" | ", row.DefinitionAttributes.OrderBy(pair => pair.Key,
                StringComparer.OrdinalIgnoreCase).Select(pair => $"{pair.Key}: {pair.Value}")));
            cells.Add(string.Join(" || ", row.DefinitionConflicts));
            sb.Append(string.Join(',', cells.Select(Csv))).Append("\r\n");
        }
        return sb.ToString();
    }

    private static string Csv(string value)
    {
        if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@') value = "'" + value;
        return value.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    }
}
