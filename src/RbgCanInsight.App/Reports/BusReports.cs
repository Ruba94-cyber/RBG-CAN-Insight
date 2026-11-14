using System.Data;
using RbgCanInsight.Analysis.Buses;
using RbgCanInsight.Domain;

namespace RbgCanInsight.App.Reports;

/// <summary>Builds read-only evidence tables for the LIN, FlexRay and CAN XL records found in the loaded trace.</summary>
public static class BusReports
{
    public static DataTable ToDataTable(BusTable source)
    {
        var table = new DataTable();
        foreach (var header in source.Headers) table.Columns.Add(header);
        foreach (var row in source.Rows) table.Rows.Add(row.Cast<object>().ToArray());
        return table;
    }

    public static DataTable LinFrames(IReadOnlyList<LinFrame> frames, LdfFile? ldf = null) => ToDataTable(BusReportTables.LinFrames(frames, ldf));
    public static DataTable LinTiming(IReadOnlyList<LinFrame> frames) => ToDataTable(BusReportTables.LinTiming(frames));
    public static DataTable LinDiagnostics(IReadOnlyList<LinFrame> frames) => ToDataTable(BusReportTables.LinDiagnostics(frames));
    public static DataTable LinSignals(LdfFile ldf, IReadOnlyList<LinFrame> frames) => ToDataTable(BusReportTables.LinSignals(ldf, frames));
    public static DataTable FlexRayMatrix(IReadOnlyList<FlexRayFrame> frames) => ToDataTable(BusReportTables.FlexRayMatrix(frames));
    public static DataTable CanXlList(IReadOnlyList<CanXlFrame> frames) => ToDataTable(BusReportTables.CanXlList(frames));
}
