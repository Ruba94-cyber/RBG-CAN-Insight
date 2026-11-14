using System.Globalization;
using RbgCanInsight.Diagnostics;

namespace RbgCanInsight.Analysis;

public enum RegressionSeverity
{
    Info,
    Warning
}

/// <summary>One observed difference between a baseline (reference) trace and a candidate trace.
/// Findings describe what differs; they never claim why.</summary>
public sealed record RegressionFinding(
    RegressionSeverity Severity,
    string Category,
    string Subject,
    string Summary,
    string Baseline,
    string Candidate);

/// <param name="PeriodTolerance">Relative change of the average period that is reported (0.20 = 20 %).</param>
/// <param name="JitterFactor">A period standard deviation this many times larger is reported.</param>
/// <param name="MinJitterMilliseconds">Ignore jitter changes smaller than this absolute amount.</param>
/// <param name="MinFrames">Identifiers with fewer frames in either trace are not compared by timing.</param>
public sealed record RegressionOptions(
    double PeriodTolerance = 0.20,
    double JitterFactor = 2.0,
    double MinJitterMilliseconds = 1.0,
    int MinFrames = 3);

/// <summary>
/// Compares a candidate trace against a baseline: which identifiers appeared or disappeared, whose
/// period or jitter changed, which decoded signals changed status (for example stable to variable
/// or present to missing) and what changed in recorded diagnostics (services, negative responses,
/// fault codes). Pure and deterministic; findings are ordered most important first.
/// </summary>
public static class TraceRegressionAnalyzer
{
    public static IReadOnlyList<RegressionFinding> Compare(
        IReadOnlyList<MessageStatisticsRow> baseline,
        IReadOnlyList<MessageStatisticsRow> candidate,
        IReadOnlyList<SignalChangeReport>? baselineSignals = null,
        IReadOnlyList<SignalChangeReport>? candidateSignals = null,
        DiagnosticInsightSnapshot? baselineDiagnostics = null,
        DiagnosticInsightSnapshot? candidateDiagnostics = null,
        RegressionOptions? options = null)
    {
        options ??= new RegressionOptions();
        var findings = new List<RegressionFinding>();
        CompareBus(baseline, candidate, options, findings);
        if (baselineSignals is not null && candidateSignals is not null)
            CompareSignals(baselineSignals, candidateSignals, findings);
        if (baselineDiagnostics is not null && candidateDiagnostics is not null)
            CompareDiagnostics(baselineDiagnostics, candidateDiagnostics, findings);

        return findings
            .OrderByDescending(item => item.Severity)
            .ThenBy(item => CategoryOrder(item.Category))
            .ThenBy(item => item.Subject, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Summary, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static int CategoryOrder(string category) => category switch
    {
        "Bus" => 0,
        "Timing" => 1,
        "Signal" => 2,
        "Diagnostics" => 3,
        _ => 4
    };

    private static string IdText(MessageStatisticsRow row) =>
        $"CH{row.Channel + 1} " + row.Identifier.ToString(row.IsExtended ? "X8" : "X3", CultureInfo.InvariantCulture) +
        (row.IsExtended ? "x" : string.Empty);

    private static string FramesText(long count) =>
        count == 1 ? "1 frame" : count.ToString("N0", CultureInfo.InvariantCulture) + " frames";

    private static string RequestsText(int count) =>
        count == 1 ? "1 request" : count.ToString("N0", CultureInfo.InvariantCulture) + " requests";

    private static string Ms(double value) => value.ToString("F2", CultureInfo.InvariantCulture) + " ms";

    private static void CompareBus(
        IReadOnlyList<MessageStatisticsRow> baseline, IReadOnlyList<MessageStatisticsRow> candidate,
        RegressionOptions options, List<RegressionFinding> findings)
    {
        var left = baseline.ToDictionary(row => (row.Channel, row.Identifier, row.IsExtended));
        var right = candidate.ToDictionary(row => (row.Channel, row.Identifier, row.IsExtended));
        foreach (var (key, row) in left)
        {
            if (!right.TryGetValue(key, out var other))
            {
                findings.Add(new(RegressionSeverity.Warning, "Bus", IdText(row),
                    "Identifier is missing from the candidate trace",
                    FramesText(row.FrameCount), "0 frames"));
                continue;
            }

            if (row.MinLength != other.MinLength || row.MaxLength != other.MaxLength)
                findings.Add(new(RegressionSeverity.Warning, "Bus", IdText(row), "Payload length changed",
                    LengthText(row), LengthText(other)));

            if (row.FrameCount < options.MinFrames || other.FrameCount < options.MinFrames) continue;

            if (row.AveragePeriodMilliseconds > 0)
            {
                var change = (other.AveragePeriodMilliseconds - row.AveragePeriodMilliseconds) /
                             row.AveragePeriodMilliseconds;
                if (Math.Abs(change) > options.PeriodTolerance)
                    findings.Add(new(RegressionSeverity.Warning, "Timing", IdText(row),
                        $"Average period {(change > 0 ? "increased" : "decreased")} by " +
                        Math.Abs(change).ToString("P0", CultureInfo.InvariantCulture),
                        Ms(row.AveragePeriodMilliseconds), Ms(other.AveragePeriodMilliseconds)));
            }

            if (other.StdDevPeriodMilliseconds - row.StdDevPeriodMilliseconds >= options.MinJitterMilliseconds &&
                other.StdDevPeriodMilliseconds >= row.StdDevPeriodMilliseconds * options.JitterFactor)
                findings.Add(new(RegressionSeverity.Warning, "Timing", IdText(row), "Period jitter increased",
                    "σ " + Ms(row.StdDevPeriodMilliseconds), "σ " + Ms(other.StdDevPeriodMilliseconds)));

            if (other.BurstCount > row.BurstCount)
                findings.Add(new(RegressionSeverity.Info, "Timing", IdText(row), "More burst intervals",
                    row.BurstCount.ToString(CultureInfo.InvariantCulture),
                    other.BurstCount.ToString(CultureInfo.InvariantCulture)));
        }

        foreach (var (key, row) in right)
        {
            if (!left.ContainsKey(key))
                findings.Add(new(RegressionSeverity.Warning, "Bus", IdText(row),
                    "Identifier appears only in the candidate trace", "0 frames", FramesText(row.FrameCount)));
        }
    }

    private static string LengthText(MessageStatisticsRow row) =>
        row.MinLength == row.MaxLength
            ? $"{row.MinLength} bytes"
            : $"{row.MinLength}-{row.MaxLength} bytes";

    private static string SignalName(SignalChangeReport report) =>
        $"{report.Timeline.Message.Name}.{report.Timeline.Signal.Name}" +
        (report.Timeline.Channel >= 0 ? $" (CH{report.Timeline.Channel + 1})" : string.Empty);

    private static void CompareSignals(
        IReadOnlyList<SignalChangeReport> baseline, IReadOnlyList<SignalChangeReport> candidate,
        List<RegressionFinding> findings)
    {
        var left = baseline.GroupBy(report => report.Timeline.Key).ToDictionary(g => g.Key, g => g.First());
        var right = candidate.GroupBy(report => report.Timeline.Key).ToDictionary(g => g.Key, g => g.First());
        foreach (var (key, report) in left)
        {
            if (!right.TryGetValue(key, out var other))
            {
                findings.Add(new(RegressionSeverity.Info, "Signal", SignalName(report),
                    "Signal is not reported in the candidate trace", report.Activity.ToString(), "—"));
                continue;
            }

            if (report.Activity != other.Activity)
            {
                var worse = other.Activity is SignalActivity.Missing or SignalActivity.Anomalous;
                findings.Add(new(worse ? RegressionSeverity.Warning : RegressionSeverity.Info, "Signal",
                    SignalName(report), "Signal status changed",
                    report.Activity.ToString(), other.Activity.ToString()));
            }
            else if (report.HasOutOfRange != other.HasOutOfRange)
            {
                findings.Add(new(other.HasOutOfRange ? RegressionSeverity.Warning : RegressionSeverity.Info,
                    "Signal", SignalName(report),
                    other.HasOutOfRange ? "Values now leave the DBC range" : "Values no longer leave the DBC range",
                    report.HasOutOfRange ? "out of range" : "in range",
                    other.HasOutOfRange ? "out of range" : "in range"));
            }
        }

        foreach (var (key, report) in right)
        {
            if (!left.ContainsKey(key))
                findings.Add(new(RegressionSeverity.Info, "Signal", SignalName(report),
                    "Signal is reported only in the candidate trace", "—", report.Activity.ToString()));
        }
    }

    private static void CompareDiagnostics(
        DiagnosticInsightSnapshot baseline, DiagnosticInsightSnapshot candidate, List<RegressionFinding> findings)
    {
        var left = baseline.Services.ToDictionary(item => item.ServiceId);
        var right = candidate.Services.ToDictionary(item => item.ServiceId);
        foreach (var serviceId in left.Keys.Union(right.Keys).OrderBy(id => id))
        {
            left.TryGetValue(serviceId, out var a);
            right.TryGetValue(serviceId, out var b);
            var name = $"0x{serviceId:X2} {(a ?? b)!.ServiceName}";
            if (a is null || b is null)
            {
                findings.Add(new(RegressionSeverity.Info, "Diagnostics", name,
                    a is null ? "Service appears only in the candidate trace" : "Service is missing from the candidate trace",
                    a is null ? "0 requests" : RequestsText(a.Requests),
                    b is null ? "0 requests" : RequestsText(b.Requests)));
                continue;
            }

            if (b.Negative > a.Negative)
                findings.Add(new(RegressionSeverity.Warning, "Diagnostics", name, "More negative responses",
                    a.Negative.ToString(CultureInfo.InvariantCulture), b.Negative.ToString(CultureInfo.InvariantCulture)));
            if (b.Missing > a.Missing)
                findings.Add(new(RegressionSeverity.Warning, "Diagnostics", name, "More requests without a response",
                    a.Missing.ToString(CultureInfo.InvariantCulture), b.Missing.ToString(CultureInfo.InvariantCulture)));
            if (a.AverageResponseMilliseconds is > 0 && b.AverageResponseMilliseconds is { } average &&
                (average - a.AverageResponseMilliseconds.Value) / a.AverageResponseMilliseconds.Value > 0.5 &&
                average - a.AverageResponseMilliseconds.Value >= 5)
                findings.Add(new(RegressionSeverity.Warning, "Diagnostics", name, "Average response time increased",
                    Ms(a.AverageResponseMilliseconds.Value), Ms(average)));
        }

        var leftFaults = baseline.Faults.Select(item => (item.Ecu, item.Code)).ToHashSet();
        var rightFaults = candidate.Faults.ToDictionary(item => (item.Ecu, item.Code));
        foreach (var (key, fault) in rightFaults)
        {
            if (!leftFaults.Contains(key))
                findings.Add(new(RegressionSeverity.Warning, "Diagnostics",
                    $"{(string.IsNullOrEmpty(fault.Ecu) ? "ECU ?" : fault.Ecu)} {fault.CodeText}",
                    "Fault code appears only in the candidate trace", "not recorded", fault.CurrentStatusText));
        }

        foreach (var fault in baseline.Faults)
        {
            if (!rightFaults.ContainsKey((fault.Ecu, fault.Code)))
                findings.Add(new(RegressionSeverity.Info, "Diagnostics",
                    $"{(string.IsNullOrEmpty(fault.Ecu) ? "ECU ?" : fault.Ecu)} {fault.CodeText}",
                    "Fault code is not recorded in the candidate trace", fault.CurrentStatusText, "not recorded"));
        }
    }
}
