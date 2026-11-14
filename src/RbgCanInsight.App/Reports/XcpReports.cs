using System.Data;
using RbgCanInsight.Analysis.Xcp;
using RbgCanInsight.Analysis.Xcp.A2l;
using RbgCanInsight.Domain;

namespace RbgCanInsight.App.Reports;

/// <summary>Evidence table for offline XCP-on-CAN / CCP decoding (identifier pairs auto-detected from CONNECT handshakes; optional A2L).</summary>
public static class XcpReports
{
    public static DataTable Xcp(IReadOnlyList<CanFrame> frames, A2lDatabase? a2l = null)
    {
        var report = XcpReportRows.Build(frames, null, a2l is null ? null : new XcpOptions(A2l: a2l));
        var table = new DataTable();
        foreach (var h in report.Headers) table.Columns.Add(h);
        foreach (var row in report.Rows) table.Rows.Add(row.Cast<object>().ToArray());
        return table;
    }
}
