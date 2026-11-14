using System.Globalization;
using System.Text;

namespace RbgCanInsight.Analysis;

/// <summary>Read-only, Excel-friendly export of computed aggregate rows.
/// Exporting a table is not the same as exporting a complete trace: the
/// total-group and truncated columns make this limitation explicit.</summary>
public static class TraceAggregateCsvExport
{
    public static string Create(TraceAggregateResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var csv = new StringBuilder(256 + result.Rows.Count * 180);
        csv.AppendLine("Source,Channel,CAN ID,Extended,Frames,Error frames,CAN FD frames," +
            "First time (ns),Last time (ns),First merged frame (1-based)," +
            "Last merged frame (1-based),Observed frames,Total groups,Truncated results," +
            "One-second bucket (source timestamp s)");
        foreach (var row in result.Rows)
        {
            // A path may be user-controlled. Quote text and neutralise Excel
            // formula prefixes to avoid executing spreadsheet expressions.
            csv.Append(QuoteText(row.SourceName)).Append(',')
               .Append(row.Channel < 0 ? "" : (row.Channel + 1).ToString(CultureInfo.InvariantCulture)).Append(',')
               .Append(row.Identifier == uint.MaxValue ? "" :
                   QuoteText(row.IdentifierText)).Append(',')
               .Append(row.Identifier == uint.MaxValue ? "" : row.IsExtended ? "true" : "false").Append(',')
               .Append(row.Frames.ToString(CultureInfo.InvariantCulture)).Append(',')
               .Append(row.ErrorFrames.ToString(CultureInfo.InvariantCulture)).Append(',')
               .Append(row.CanFdFrames.ToString(CultureInfo.InvariantCulture)).Append(',')
               .Append(row.FirstTimestampNanoseconds.ToString(CultureInfo.InvariantCulture)).Append(',')
               .Append(row.LastTimestampNanoseconds.ToString(CultureInfo.InvariantCulture)).Append(',')
               .Append((row.FirstFrameIndex + 1).ToString(CultureInfo.InvariantCulture)).Append(',')
               .Append((row.LastFrameIndex + 1).ToString(CultureInfo.InvariantCulture)).Append(',')
               .Append(result.ExaminedFrames.ToString(CultureInfo.InvariantCulture)).Append(',')
               .Append(result.UniqueGroups.ToString(CultureInfo.InvariantCulture)).Append(',')
               .Append(result.DisplayTruncated ? "true" : "false").Append(',')
               .Append(row.BucketSecond?.ToString(CultureInfo.InvariantCulture) ?? "")
               .AppendLine();
        }
        return csv.ToString();
    }

    private static string QuoteText(string? value)
    {
        var text = value ?? "";
        var leading = text.TrimStart();
        if (leading.Length > 0 && "=+-@".Contains(leading[0]))
            text = "'" + text;
        return "\"" + text.Replace("\"", "\"\"") + "\"";
    }
}
