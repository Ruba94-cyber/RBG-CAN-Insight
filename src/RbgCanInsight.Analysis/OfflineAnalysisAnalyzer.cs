using RbgCanInsight.DiagData;
using RbgCanInsight.Diagnostics;

namespace RbgCanInsight.Analysis;

public enum OfflineFindingSeverity
{
    Critical,
    Warning,
    Information
}

public enum OfflineFindingCategory
{
    Diagnostic,
    FaultMemory,
    Routine,
    Transport,
    Timing,
    Signal,
    Catalog
}

public sealed record OfflineAnalysisFinding(
    OfflineFindingSeverity Severity,
    OfflineFindingCategory Category,
    string Title,
    string Details,
    long? TimestampNanoseconds,
    string Ecu,
    IReadOnlyList<int> FrameIndices);

public sealed record DiagnosticSequenceEvent(
    int Sequence,
    long TimestampNanoseconds,
    string Category,
    string Ecu,
    string Service,
    string Operation,
    string Outcome,
    string State,
    IReadOnlyList<int> FrameIndices);

public sealed record RecordedEcuValue(
    string Ecu,
    int Did,
    string Parameter,
    string Value,
    string Unit,
    long TimestampNanoseconds,
    string Quality,
    int FrameIndex);

public sealed record CatalogCoverageRow(
    string Entity,
    int Defined,
    int Observed,
    int Resolved,
    IReadOnlyList<string> UnknownItems)
{
    public int Unknown => UnknownItems.Count;
    public double Coverage => Observed == 0 ? 1 : Resolved / (double)Observed;
}

public sealed record OfflineAnalysisSnapshot(
    IReadOnlyList<OfflineAnalysisFinding> Findings,
    IReadOnlyList<DiagnosticSequenceEvent> Sequence,
    IReadOnlyList<RecordedEcuValue> EcuSnapshot,
    IReadOnlyList<CatalogCoverageRow> CatalogCoverage)
{
    public static OfflineAnalysisSnapshot Empty { get; } = new([], [], [], []);
}

/// <summary>Combines trace, signal and recorded diagnostic evidence into one explainable,
/// read-only review result. It has no dependency on hardware or diagnostic transport APIs.</summary>
public static class OfflineAnalysisAnalyzer
{
    private static readonly string[] IdentificationTerms =
    [
        "vin", "serial", "software", "hardware", "version", "part number", "variant",
        "fingerprint", "calibration", "supplier"
    ];

    public static OfflineAnalysisSnapshot Analyze(
        IReadOnlyList<SignalChangeReport> signalReports,
        IReadOnlyList<TimingIncident> timingIncidents,
        DiagnosticInsightSnapshot diagnostic,
        IReadOnlyList<DidCatalog> catalogs,
        DbcCycleTimingResult? dbcCycleTiming = null)
    {
        var coverage = BuildCoverage(diagnostic.Transactions, catalogs);
        var findings = BuildFindings(signalReports, timingIncidents, diagnostic, coverage, dbcCycleTiming);
        var sequence = diagnostic.Transactions.Select(BuildSequenceEvent).ToArray();
        var ecuSnapshot = diagnostic.DidSamples
            .Where(IsIdentificationValue)
            .GroupBy(sample => (sample.Ecu, sample.Did, sample.Field))
            .Select(group => group.OrderBy(sample => sample.TimestampNanoseconds).Last())
            .Select(sample => new RecordedEcuValue(
                sample.Ecu, sample.Did, sample.Name, sample.TextValue, sample.Unit,
                sample.TimestampNanoseconds, sample.Quality, sample.FrameIndex))
            .OrderBy(value => value.Ecu, StringComparer.OrdinalIgnoreCase)
            .ThenBy(value => value.Did)
            .ToArray();
        return new OfflineAnalysisSnapshot(findings, sequence, ecuSnapshot, coverage);
    }

    private static IReadOnlyList<OfflineAnalysisFinding> BuildFindings(
        IReadOnlyList<SignalChangeReport> signalReports,
        IReadOnlyList<TimingIncident> timingIncidents,
        DiagnosticInsightSnapshot diagnostic,
        IReadOnlyList<CatalogCoverageRow> coverage,
        DbcCycleTimingResult? dbcCycleTiming)
    {
        var findings = new List<OfflineAnalysisFinding>();
        foreach (var transaction in diagnostic.Transactions)
        {
            var severity = transaction.Outcome switch
            {
                DiagnosticOutcome.Malformed => OfflineFindingSeverity.Critical,
                DiagnosticOutcome.Negative or DiagnosticOutcome.NoResponse or DiagnosticOutcome.OrphanResponse => OfflineFindingSeverity.Warning,
                _ => (OfflineFindingSeverity?)null
            };
            if (severity is null) continue;
            findings.Add(new OfflineAnalysisFinding(
                severity.Value, transaction.Outcome == DiagnosticOutcome.Malformed
                    ? OfflineFindingCategory.Transport : OfflineFindingCategory.Diagnostic,
                $"{transaction.Outcome}: {transaction.Decoded.ServiceName}",
                JoinDetails(transaction.Decoded.ObjectText, transaction.Decoded.NegativeResponseText, transaction.Quality),
                transaction.StartTimestampNanoseconds, transaction.Endpoint.Ecu, transaction.FrameIndices));
        }

        foreach (var serviceGroup in diagnostic.Transactions
                     .Where(transaction => transaction.Outcome == DiagnosticOutcome.Positive &&
                                           transaction.ResponseTimeMilliseconds is not null)
                     .GroupBy(transaction => transaction.Decoded.ServiceId)
                     .Where(group => group.Count() >= 5))
        {
            var ordered = serviceGroup.Select(transaction => transaction.ResponseTimeMilliseconds!.Value)
                .OrderBy(value => value).ToArray();
            var median = Median(ordered);
            var mad = Median(ordered.Select(value => Math.Abs(value - median)).OrderBy(value => value).ToArray());
            var threshold = median + Math.Max(20, mad * 6);
            foreach (var transaction in serviceGroup.Where(transaction => transaction.ResponseTimeMilliseconds > threshold))
            {
                findings.Add(new OfflineAnalysisFinding(
                    OfflineFindingSeverity.Warning, OfflineFindingCategory.Diagnostic,
                    $"Response-time outlier: {transaction.Decoded.ServiceName}",
                    $"{transaction.ResponseTimeMilliseconds:F2} ms versus a recorded median of {median:F2} ms for service 0x{serviceGroup.Key:X2}.",
                    transaction.EndTimestampNanoseconds, transaction.Endpoint.Ecu, transaction.FrameIndices));
            }
        }

        foreach (var fault in diagnostic.Faults.Where(fault => !fault.WasCleared && fault.CurrentStatus is not 0))
        {
            findings.Add(new OfflineAnalysisFinding(
                OfflineFindingSeverity.Warning, OfflineFindingCategory.FaultMemory,
                $"Active recorded DTC {fault.CodeText}",
                JoinDetails(fault.Name, fault.CurrentStatusText, fault.Description,
                    string.Join(" | ", fault.LatestRecords.Select(record => record.SummaryText))),
                fault.LastSeenNanoseconds, fault.Ecu,
                fault.Timeline.LastOrDefault()?.FrameIndices ?? []));
        }

        foreach (var timing in diagnostic.TimingReport.Findings)
        {
            findings.Add(new OfflineAnalysisFinding(
                timing.Severity switch
                {
                    DiagnosticTimingSeverity.Error => OfflineFindingSeverity.Critical,
                    DiagnosticTimingSeverity.Warning => OfflineFindingSeverity.Warning,
                    _ => OfflineFindingSeverity.Information
                },
                OfflineFindingCategory.Timing,
                $"{timing.Rule.Id} {timing.Rule.Title}",
                timing.Message, timing.TimestampNanoseconds, timing.Ecu, timing.FrameIndices));
        }

        foreach (var routine in diagnostic.Routines.Where(routine =>
                     routine.State is RoutineExecutionState.Failed or RoutineExecutionState.Incomplete))
        {
            findings.Add(new OfflineAnalysisFinding(
                OfflineFindingSeverity.Warning, OfflineFindingCategory.Routine,
                $"Recorded routine {routine.State.ToString().ToLowerInvariant()}",
                JoinDetails(routine.IdentifierText, routine.Name, routine.ResultSummary),
                routine.EndTimestampNanoseconds, routine.Ecu, routine.FrameIndices));
        }

        foreach (var incident in timingIncidents)
        {
            findings.Add(new OfflineAnalysisFinding(
                incident.RatioVsAverage >= 10 ? OfflineFindingSeverity.Critical : OfflineFindingSeverity.Warning,
                OfflineFindingCategory.Timing,
                $"Large timing gap on 0x{incident.Identifier:X}",
                $"{incident.GapMilliseconds:F2} ms gap, {incident.RatioVsAverage:F1}x the observed average of {incident.AveragePeriodMilliseconds:F2} ms.",
                incident.TimestampNanoseconds, $"Channel {incident.Channel + 1}", [incident.FrameIndex]));
        }

        // DBC nominal-cycle evidence is distinct from outliers relative to an
        // observed average. Add one source-backed finding per physical ID,
        // never one per gap (which would drown the investigation dashboard).
        if (dbcCycleTiming is not null)
        {
            var sampleIncidents = dbcCycleTiming.Incidents
                .GroupBy(item => (item.Database, item.Channel, item.Identifier, item.IsExtended))
                .ToDictionary(group => group.Key, group =>
                    group.OrderByDescending(item => item.GapRatio).First());
            foreach (var row in dbcCycleTiming.Rows
                         .Where(item => item.LateGapCount > 0)
                         .OrderByDescending(item => item.MaxGapRatio)
                         .Take(100))
            {
                if (!sampleIncidents.TryGetValue(
                        (row.Database, row.Channel, row.Identifier, row.IsExtended), out var example))
                    continue; // never publish a finding without an actual source frame

                findings.Add(new OfflineAnalysisFinding(
                    example.GapRatio >= 3
                        ? OfflineFindingSeverity.Warning : OfflineFindingSeverity.Information,
                    OfflineFindingCategory.Timing,
                    $"DBC nominal cycle deviation: {row.Message} (CAN {row.Channel + 1}, {row.IdentifierText})",
                    $"DBC '{row.Database}' defines a nominal period of {row.NominalCycleMs} ms. " +
                    $"The selected recorded interval is {example.ObservedGapMilliseconds:F3} ms " +
                    $"({example.GapRatio:F2}x nominal); {row.LateGapCount:N0} longer-than-20%-tolerance intervals " +
                    $"occurred. {example.Note} This is timing evidence, not proof of lost packets.",
                    example.TimestampNanoseconds,
                    row.Transmitter, [example.FrameIndex]));
            }
        }

        foreach (var report in signalReports.Where(report => report.HasOutOfRange))
        {
            var signal = report.Timeline.Signal;
            var evidence = report.Timeline.Samples.FirstOrDefault(sample =>
                sample.Value < signal.Minimum || sample.Value > signal.Maximum);
            findings.Add(new OfflineAnalysisFinding(
                OfflineFindingSeverity.Warning, OfflineFindingCategory.Signal,
                $"DBC range exceeded: {report.SignalName}",
                $"{report.MessageName} (0x{report.Identifier:X}) observed {report.MinValue:G6} to {report.MaxValue:G6} {report.Unit}; DBC range is {report.Timeline.Signal.Minimum:G6} to {report.Timeline.Signal.Maximum:G6} {report.Unit}.",
                evidence.TimestampNanoseconds,
                report.Timeline.Message.Transmitter, [evidence.FrameIndex]));
        }

        var missingSignals = signalReports.Count(report => report.Activity == SignalActivity.Missing);
        if (missingSignals > 0)
        {
            findings.Add(new OfflineAnalysisFinding(
                OfflineFindingSeverity.Information, OfflineFindingCategory.Signal,
                $"{missingSignals:N0} DBC signal definitions were not observed",
                "The parent messages were present, but these signals produced no samples. Multiplexing, short payloads or database mismatch can explain the gap.",
                null, string.Empty, []));
        }

        foreach (var row in coverage.Where(row => row.Unknown > 0))
        {
            findings.Add(new OfflineAnalysisFinding(
                OfflineFindingSeverity.Information, OfflineFindingCategory.Catalog,
                $"{row.Unknown:N0} observed {row.Entity.ToLowerInvariant()} not resolved by CDD/ODX",
                string.Join(", ", row.UnknownItems.Take(20)), null, string.Empty, []));
        }

        return findings
            .OrderBy(finding => finding.Severity)
            .ThenBy(finding => finding.TimestampNanoseconds ?? long.MaxValue)
            .ToArray();
    }

    private static DiagnosticSequenceEvent BuildSequenceEvent(DiagnosticTransaction transaction)
    {
        var service = transaction.Decoded.ServiceId;
        var category = service switch
        {
            0x10 => "Session",
            0x27 => "Security",
            0x19 or 0x14 => "Fault memory",
            0x31 => "Routine",
            0x22 or 0x23 or 0x24 or 0x2A or 0x2C or 0x2E or 0x2F => "Data",
            0x28 or 0x3E or 0x85 => "Communication",
            0x34 or 0x35 or 0x36 or 0x37 or 0x38 => "Transfer",
            _ => "Diagnostic"
        };
        return new DiagnosticSequenceEvent(
            transaction.Sequence, transaction.StartTimestampNanoseconds, category,
            transaction.Endpoint.Ecu, $"0x{service:X2} {transaction.Decoded.ServiceName}",
            transaction.Decoded.ObjectText, transaction.Outcome.ToString(),
            JoinDetails(transaction.Decoded.ResponseSummary, transaction.Quality), transaction.FrameIndices);
    }

    private static IReadOnlyList<CatalogCoverageRow> BuildCoverage(
        IReadOnlyList<DiagnosticTransaction> transactions,
        IReadOnlyList<DidCatalog> catalogs)
    {
        var definedServices = catalogs.SelectMany(catalog => catalog.ServiceDefinitions).Select(item => (int)item.ServiceId).ToHashSet();
        var definedDids = catalogs.SelectMany(catalog => catalog.Parameters).Select(item => item.Did).ToHashSet();
        var definedDtcs = catalogs.SelectMany(catalog => catalog.DtcDefinitions).Select(item => item.Code).ToHashSet();
        var definedRoutines = catalogs.SelectMany(catalog => catalog.RoutineDefinitions).Select(item => item.Identifier).ToHashSet();

        var observedServices = transactions.Select(item => (int)item.Decoded.ServiceId).ToHashSet();
        var observedDids = transactions.SelectMany(ObservedDids).ToHashSet();
        var observedDtcs = transactions.SelectMany(item => item.Decoded.Dtcs).Select(item => item.Code).ToHashSet();
        var observedRoutines = transactions.Select(ObservedRoutine).Where(value => value is not null).Select(value => value!.Value).ToHashSet();

        return
        [
            Coverage("Services", definedServices, observedServices, value => $"0x{value:X2}"),
            Coverage("DIDs", definedDids, observedDids, value => $"0x{value:X4}"),
            Coverage("DTCs", definedDtcs, observedDtcs, value => $"0x{value:X6}"),
            Coverage("Routines", definedRoutines, observedRoutines, value => $"0x{value:X4}")
        ];
    }

    private static CatalogCoverageRow Coverage(
        string entity, IReadOnlySet<int> defined, IReadOnlySet<int> observed, Func<int, string> format)
    {
        var unknown = observed.Where(value => !defined.Contains(value)).OrderBy(value => value).Select(format).ToArray();
        return new CatalogCoverageRow(entity, defined.Count, observed.Count, observed.Count - unknown.Length, unknown);
    }

    private static IEnumerable<int> ObservedDids(DiagnosticTransaction transaction)
    {
        foreach (var did in transaction.Decoded.Values.Where(value => value.Did is not null).Select(value => value.Did!.Value))
            yield return did;
        var payload = transaction.Request?.Payload;
        if (payload is { Length: >= 3 } && transaction.Decoded.ServiceId is 0x22 or 0x2E or 0x2F)
            yield return (payload[1] << 8) | payload[2];
    }

    private static int? ObservedRoutine(DiagnosticTransaction transaction)
    {
        var payload = transaction.Request?.Payload;
        return transaction.Decoded.ServiceId == 0x31 && payload is { Length: >= 4 }
            ? (payload[2] << 8) | payload[3]
            : null;
    }

    private static bool IsIdentificationValue(DiagnosticDidSample sample)
    {
        if (sample.Did is >= 0xF180 and <= 0xF19F) return true;
        var text = $"{sample.Name} {sample.Field}";
        return IdentificationTerms.Any(term => text.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    private static string JoinDetails(params string?[] parts) =>
        string.Join(" · ", parts.Where(part => !string.IsNullOrWhiteSpace(part)).Distinct(StringComparer.OrdinalIgnoreCase));

    private static double Median(IReadOnlyList<double> ordered) => ordered.Count % 2 == 0
        ? (ordered[ordered.Count / 2 - 1] + ordered[ordered.Count / 2]) / 2
        : ordered[ordered.Count / 2];
}
