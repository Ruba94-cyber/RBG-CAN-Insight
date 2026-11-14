using System.Globalization;
using RbgCanInsight.DiagData;

namespace RbgCanInsight.Diagnostics;

public sealed record DiagnosticDidSample(
    int Did,
    string Name,
    string Field,
    double? NumericValue,
    string TextValue,
    string Unit,
    long TimestampNanoseconds,
    int FrameIndex,
    string Ecu,
    string Quality,
    int Channel = -1,
    IReadOnlyList<int>? ResponseFrameIndices = null)
{
    /// <summary>Complete original frame indices of the positive ISO-TP
    /// response that produced this DID. A multi-frame response cannot be
    /// audited from its final frame index alone.</summary>
    public IReadOnlyList<int> ResponseFrames => ResponseFrameIndices ?? [];
    /// <summary>Original logger of the complete positive UDS response;
    /// -1 for legacy single-file callers without a source mapping.</summary>
    public int OriginalSourceIndex { get; init; } = -1;
}

public sealed record DtcStatusTransition(
    long TimestampNanoseconds,
    byte? Status,
    string StatusText,
    string Operation,
    DiagnosticOutcome Outcome,
    IReadOnlyList<int> FrameIndices,
    string ExtraData,
    IReadOnlyList<DecodedDtcRecord>? RecordList = null)
{
    /// <summary>Snapshot / extended-data records returned together with this observation.</summary>
    public IReadOnlyList<DecodedDtcRecord> Records => RecordList ?? [];
}

public sealed record FaultMemoryEntry(
    int Code,
    string CodeText,
    string Name,
    string Description,
    string Ecu,
    long FirstSeenNanoseconds,
    long LastSeenNanoseconds,
    int Occurrences,
    byte? CurrentStatus,
    string CurrentStatusText,
    bool WasCleared,
    long? ClearedAtNanoseconds,
    IReadOnlyList<DtcStatusTransition> Timeline)
{
    /// <summary>Most recent decoded snapshot / extended-data record per (kind, record number).</summary>
    public IReadOnlyList<DecodedDtcRecord> LatestRecords => Timeline
        .SelectMany(item => item.Records)
        .GroupBy(record => (record.Kind, record.RecordNumber))
        .Select(group => group.Last())
        .OrderBy(record => record.Kind).ThenBy(record => record.RecordNumber)
        .ToArray();

    /// <summary>Original physical recording, not a CAN channel. -1 when
    /// this API was called without source evidence for a legacy single trace.</summary>
    public int OriginalSourceIndex { get; init; } = -1;
    /// <summary>Physical CAN bus from verified UDS response/request evidence.</summary>
    public int SourceChannel { get; init; } = -1;
    /// <summary>Catalog file with a matching DTC number. An ECU match
    /// is tracked separately and is not inferred from the code alone.</summary>
    public string DefinitionSourceFile { get; init; } = string.Empty;
    public IReadOnlyList<string> DefinitionSourceFiles { get; init; } = [];
    public string DefinitionSourceSheet { get; init; } = string.Empty;
    /// <summary>Multiple ECU-matched definitions disagree; alternatives are visible
    /// as catalog references, never accepted as measurements or a fault diagnosis.</summary>
    public IReadOnlyList<string> DefinitionConflicts { get; init; } = [];
    /// <summary>Additional DTC table text supplied by an OEM; never observations from the trace.</summary>
    public IReadOnlyDictionary<string, string> DefinitionAttributes { get; init; } =
        new Dictionary<string, string>();
    public bool DefinitionEcuMatched { get; init; }
    public string SourceText => OriginalSourceIndex < 0
        ? "Current recording" : $"Logger {OriginalSourceIndex + 1}";
    public string ChannelText => SourceChannel < 0
        ? "CAN channel unknown" : $"CAN {SourceChannel + 1}";
    public string DefinitionFileNames => (DefinitionSourceFiles.Count > 0
        ? string.Join(" + ", DefinitionSourceFiles.Select(file => System.IO.Path.GetFileName(file)))
        : System.IO.Path.GetFileName(DefinitionSourceFile)) +
        (string.IsNullOrWhiteSpace(DefinitionSourceSheet) ? string.Empty :
            $" [Excel sheet: {DefinitionSourceSheet}]");
    public string DefinitionText => string.IsNullOrWhiteSpace(DefinitionSourceFile)
        ? "No unambiguous ECU-matched DTC definition in loaded CDD/ODX or DTC tables"
        : DefinitionEcuMatched
            ? DefinitionFileNames
            : "Possible code-only definition (ECU not verified) · " +
              System.IO.Path.GetFileName(DefinitionSourceFile);
    public string DurationText => $"{FirstSeenNanoseconds / 1_000_000_000d:F3}s … {LastSeenNanoseconds / 1_000_000_000d:F3}s";
}

public enum RoutineExecutionState
{
    Started,
    Completed,
    Stopped,
    Failed,
    Incomplete
}

public sealed record RoutineStep(
    byte Action,
    string ActionName,
    long TimestampNanoseconds,
    DiagnosticOutcome Outcome,
    string RequestData,
    string ResponseData,
    string NegativeResponse,
    int ResponsePendingCount,
    IReadOnlyList<int> FrameIndices);

public sealed record RoutineExecution(
    int Sequence,
    int Identifier,
    string IdentifierText,
    string Name,
    string Ecu,
    long StartTimestampNanoseconds,
    long EndTimestampNanoseconds,
    double DurationMilliseconds,
    RoutineExecutionState State,
    string InputSummary,
    string ResultSummary,
    int ResponsePendingCount,
    IReadOnlyList<RoutineStep> Steps,
    IReadOnlyList<int> FrameIndices)
{
    public int OriginalSourceIndex { get; init; } = -1;
    public int SourceChannel { get; init; } = -1;
    public string DefinitionSourceFile { get; init; } = string.Empty;
    public string SourceText => OriginalSourceIndex < 0
        ? "Current recording" : $"Logger {OriginalSourceIndex + 1}";
    public string ChannelText => SourceChannel < 0
        ? "CAN channel unknown" : $"CAN {SourceChannel + 1}";
    public string DefinitionText => string.IsNullOrWhiteSpace(DefinitionSourceFile)
        ? "CDD/ODX routine definition unverified"
        : System.IO.Path.GetFileName(DefinitionSourceFile);
}

public sealed record DiagnosticServiceStatistic(
    byte ServiceId,
    string ServiceName,
    int Requests,
    int Positive,
    int Negative,
    int Missing,
    double? AverageResponseMilliseconds,
    double? MaximumResponseMilliseconds);

public sealed record DiagnosticInsightSnapshot(
    IReadOnlyList<DiagnosticTransaction> Transactions,
    IReadOnlyList<DiagnosticDidSample> DidSamples,
    IReadOnlyList<FaultMemoryEntry> Faults,
    IReadOnlyList<RoutineExecution> Routines,
    IReadOnlyList<DiagnosticServiceStatistic> Services,
    IReadOnlyList<string> TransportIssues,
    DiagnosticTimingReport? Timing = null)
{
    public static DiagnosticInsightSnapshot Empty { get; } = new([], [], [], [], [], []);

    /// <summary>P2/P2*, NRC 0x78, S3/TesterPresent and ISO-TP timing findings with their source frames.</summary>
    public DiagnosticTimingReport TimingReport => Timing ?? DiagnosticTimingReport.Empty;
}

/// <summary>Builds reusable diagnostic evidence from transaction-level UDS decoding. This is
/// deliberately UI-independent so the same lifecycle data feeds tables, plots and reports.</summary>
public static class DiagnosticInsightAnalyzer
{
    public static DiagnosticInsightSnapshot Analyze(
        IReadOnlyList<DiagnosticTransaction> transactions,
        IReadOnlyList<DidCatalog> catalogs,
        IReadOnlyList<RbgCanInsight.Domain.CanFrame>? frames = null,
        DiagnosticTimingOptions? timingOptions = null,
        Func<int, int>? sourceAt = null)
    {
        if (transactions.Count == 0) return DiagnosticInsightSnapshot.Empty;
        var didSamples = BuildDidSamples(transactions, sourceAt);
        var faults = BuildFaultMemory(transactions, catalogs, sourceAt);
        var routines = BuildRoutines(transactions, catalogs, sourceAt);
        var services = transactions
            .GroupBy(transaction => (transaction.Decoded.ServiceId, transaction.Decoded.ServiceName))
            .Select(group => new DiagnosticServiceStatistic(
                group.Key.ServiceId,
                group.Key.ServiceName,
                group.Count(transaction => transaction.Request is not null),
                group.Count(transaction => transaction.Outcome == DiagnosticOutcome.Positive),
                group.Count(transaction => transaction.Outcome == DiagnosticOutcome.Negative),
                group.Count(transaction => transaction.Outcome == DiagnosticOutcome.NoResponse),
                Average(group.Select(transaction => transaction.ResponseTimeMilliseconds)),
                group.Select(transaction => transaction.ResponseTimeMilliseconds).Where(value => value is not null)
                    .Select(value => value!.Value).DefaultIfEmpty().Max() is var max && max > 0 ? max : null))
            .OrderByDescending(statistic => statistic.Requests)
            .ToArray();
        var transport = transactions
            .Where(transaction => transaction.Outcome == DiagnosticOutcome.Malformed ||
                                  transaction.Quality.StartsWith("Malformed", StringComparison.OrdinalIgnoreCase))
            .Select(transaction => $"#{transaction.Sequence}: {transaction.Quality}")
            .Distinct().ToArray();
        var timing = DiagnosticTimingAnalyzer.Analyze(transactions, frames, timingOptions);
        return new DiagnosticInsightSnapshot(transactions, didSamples, faults, routines, services, transport, timing);
    }

    private static IReadOnlyList<DiagnosticDidSample> BuildDidSamples(
        IReadOnlyList<DiagnosticTransaction> transactions, Func<int, int>? sourceAt)
    {
        var result = new List<DiagnosticDidSample>();
        foreach (var transaction in transactions.Where(item => item.Outcome == DiagnosticOutcome.Positive))
        {
            // The aggregate transaction frame list contains the tester
            // request and flow-control traffic as well as reply segments.
            // A DID is decoded from the *terminal positive reply*, never
            // from an arbitrary last entry in that aggregate list.
            var response = transaction.Responses.LastOrDefault();
            if (response is null || !response.IsComplete ||
                response.FrameIndices.Count == 0)
                continue;
            var responseFrame = response.FrameIndices[^1];
            var originalSource = sourceAt is null ? -1 : sourceAt(responseFrame);
            if (sourceAt is not null &&
                (originalSource < 0 ||
                 response.FrameIndices.Any(frameIndex =>
                     frameIndex < 0 || sourceAt(frameIndex) != originalSource)))
                throw new InvalidOperationException(
                    "Decoded DID response has frames from different original loggers or no valid source.");
            foreach (var value in transaction.Decoded.Values.Where(value => value.Did is not null))
            {
                result.Add(new DiagnosticDidSample(
                    value.Did!.Value, value.Name, value.Path, value.NumericValue, value.Physical,
                    value.Unit, transaction.EndTimestampNanoseconds,
                    responseFrame, transaction.Endpoint.Ecu, transaction.Quality,
                    response.Channel, response.FrameIndices.ToArray())
                {
                    OriginalSourceIndex = originalSource
                });
            }
        }

        return result.OrderBy(sample => sample.TimestampNanoseconds).ToArray();
    }

    private static IReadOnlyList<FaultMemoryEntry> BuildFaultMemory(
        IReadOnlyList<DiagnosticTransaction> transactions,
        IReadOnlyList<DidCatalog> catalogs,
        Func<int, int>? sourceAt)
    {
        var definitions = catalogs.SelectMany(catalog => catalog.DtcDefinitions)
            .GroupBy(dtc => dtc.Code).ToDictionary(group => group.Key, group => group.ToArray());
        // A DTC number is neither global across ECUs nor across physical
        // recorders. ECU labels may also be reused on multiple CAN buses.
        var events = new Dictionary<
            (int Code, string Ecu, int Source, int Channel),
            List<DtcStatusTransition>>();

        (int Source, int Channel) VerifiedScope(DiagnosticTransaction transaction)
        {
            if (sourceAt is null) return (-1, -1); // legacy one-file behavior
            var indices = transaction.FrameIndices;
            if (indices.Count == 0)
                throw new InvalidOperationException(
                    "DTC evidence has no original CAN frame indices; a physical logger cannot be established.");
            var first = indices[0];
            if (first < 0)
                throw new InvalidOperationException("Invalid negative DTC source-frame index.");
            var source = sourceAt(first);
            if (source < 0)
                throw new InvalidOperationException("DTC evidence refers to an invalid physical logger.");
            // Request, response, Flow Control and any other recorded witnesses
            // must all belong to the same recording. Check sub-PDU evidence
            // independently, not merely the aggregate transaction list.
            foreach (var index in indices
                .Concat(transaction.Request?.FrameIndices ?? [])
                .Concat(transaction.Responses.SelectMany(reply => reply.FrameIndices)))
            {
                if (index < 0 || sourceAt(index) != source)
                    throw new InvalidOperationException(
                        "A DTC read or clear operation contains evidence from different original loggers.");
            }
            var channel = transaction.Request?.Channel ??
                transaction.Responses.FirstOrDefault()?.Channel ?? -1;
            return (source, channel);
        }
        var clearEvents = transactions
            .Where(transaction => transaction.Decoded.ServiceId == 0x14 &&
                                  transaction.Outcome == DiagnosticOutcome.Positive)
            .Select(transaction => (Transaction: transaction, Scope: VerifiedScope(transaction)))
            .OrderBy(item => item.Transaction.EndTimestampNanoseconds)
            .ToArray();

        foreach (var transaction in transactions.Where(item => item.Decoded.ServiceId == 0x19))
        {
            foreach (var dtc in transaction.Decoded.Dtcs)
            {
                var scope = VerifiedScope(transaction);
                var key = (dtc.Code, transaction.Endpoint.Ecu, scope.Source, scope.Channel);
                if (!events.TryGetValue(key, out var timeline))
                {
                    timeline = [];
                    events[key] = timeline;
                }

                timeline.Add(new DtcStatusTransition(
                    transaction.EndTimestampNanoseconds, dtc.Status, dtc.StatusText,
                    transaction.Decoded.ObjectText, transaction.Outcome,
                    transaction.FrameIndices, dtc.ExtraData,
                    transaction.Decoded.DtcRecords.Where(record => record.Code == dtc.Code).ToArray()));
            }
        }

        var result = new List<FaultMemoryEntry>();
        foreach (var pair in events)
        {
            var timeline = pair.Value.OrderBy(item => item.TimestampNanoseconds).ToList();
            // UDS 0x14 clears a specified 24-bit groupOfDTC, not necessarily
            // every fault stored by this ECU. 0xFFFFFF means all; exact matches
            // are unambiguous. Unknown group masks are left unconfirmed rather
            // than claiming that unrelated codes were cleared.
            var clear = clearEvents.LastOrDefault(item =>
                item.Scope.Source == pair.Key.Source &&
                item.Scope.Channel == pair.Key.Channel &&
                string.Equals(item.Transaction.Endpoint.Ecu, pair.Key.Ecu,
                    StringComparison.OrdinalIgnoreCase) &&
                item.Transaction.EndTimestampNanoseconds >= timeline[0].TimestampNanoseconds &&
                item.Transaction.Request?.Payload is { Length: >= 4 } request &&
                ((request[1] << 16) | (request[2] << 8) | request[3]) is var group &&
                (group == 0xFFFFFF || group == pair.Key.Code));
            if (clear.Transaction is not null)
            {
                var operation = clear.Transaction;
                timeline.Add(new DtcStatusTransition(
                    operation.EndTimestampNanoseconds, 0, "cleared", "ClearDiagnosticInformation",
                    operation.Outcome, operation.FrameIndices, string.Empty));
                timeline.Sort((left, right) => left.TimestampNanoseconds.CompareTo(right.TimestampNanoseconds));
            }

            definitions.TryGetValue(pair.Key.Code, out var candidates);
            var matchingDefinitions = candidates?.Where(item =>
                string.Equals(item.Ecu, pair.Key.Ecu, StringComparison.OrdinalIgnoreCase)).ToArray() ?? [];
            // Complementary definitions in a DTC table and CDD/ODX can supply
            // the display text and binary record layout together, but only if
            // their metadata is compatible. Contradictions stay unresolved.
            var definition = UdsServiceDecoder.FindUniqueDtc(pair.Key.Code, catalogs, pair.Key.Ecu);
            // A single dictionary entry from another ECU may still be useful
            // as a code-only reference, but its name, fault cause and severity
            // must never be attached to the observed ECU as established facts.
            var codeOnlyReference = definition is null && matchingDefinitions.Length == 0 &&
                candidates is { Length: 1 } ? candidates[0] : null;
            var sourceDefinition = definition ?? codeOnlyReference;
            var ecuMatched = definition is not null && matchingDefinitions.Length > 0;
            var conflicts = definition is null && matchingDefinitions.Length > 1
                ? matchingDefinitions.OrderBy(item => item.SourceFile, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(item => item.SourceSheet, StringComparer.OrdinalIgnoreCase)
                    .Select(item =>
                        $"{item.SourceFile}" +
                        (string.IsNullOrWhiteSpace(item.SourceSheet) ? string.Empty :
                            $" [sheet: {item.SourceSheet}]") +
                        $" · name: {item.DisplayName}" +
                        (string.IsNullOrWhiteSpace(item.Description) ? string.Empty :
                            $" · description: {item.Description}") +
                        (item.TableAttributes.Count == 0 ? string.Empty :
                            " · OEM reference: " + string.Join("; ", item.TableAttributes
                                .OrderBy(field => field.Key, StringComparer.OrdinalIgnoreCase)
                                .Select(field => $"{field.Key}={field.Value}"))))
                    .ToArray()
                : Array.Empty<string>();
            var observations = timeline.Where(item => item.Operation != "ClearDiagnosticInformation").ToArray();
            var last = observations[^1];
            var currentlyCleared = clear.Transaction is not null &&
                clear.Transaction.EndTimestampNanoseconds >= last.TimestampNanoseconds;
            result.Add(new FaultMemoryEntry(
                pair.Key.Code, $"0x{pair.Key.Code:X6}", definition?.DisplayName ?? string.Empty,
                definition?.Description ?? string.Empty, pair.Key.Ecu,
                observations[0].TimestampNanoseconds, observations[^1].TimestampNanoseconds,
                observations.Length,
                currentlyCleared ? (byte)0 : last.Status,
                currentlyCleared ? "cleared" : last.StatusText,
                clear.Transaction is not null, clear.Transaction?.EndTimestampNanoseconds, timeline)
            {
                OriginalSourceIndex = pair.Key.Source,
                SourceChannel = pair.Key.Channel,
                DefinitionSourceFile = sourceDefinition?.SourceFile ?? string.Empty,
                DefinitionSourceFiles = sourceDefinition?.DefinitionFiles ?? [],
                DefinitionSourceSheet = sourceDefinition?.SourceSheet ?? string.Empty,
                DefinitionConflicts = conflicts,
                DefinitionAttributes = ecuMatched
                    ? definition!.TableAttributes : new Dictionary<string, string>(),
                DefinitionEcuMatched = ecuMatched
            });
        }

        return result.OrderByDescending(entry => entry.CurrentStatus is not 0)
            .ThenBy(entry => entry.OriginalSourceIndex)
            .ThenBy(entry => entry.SourceChannel)
            .ThenBy(entry => entry.Ecu, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.Code).ToArray();
    }

    private static IReadOnlyList<RoutineExecution> BuildRoutines(
        IReadOnlyList<DiagnosticTransaction> transactions,
        IReadOnlyList<DidCatalog> catalogs,
        Func<int, int>? sourceAt)
    {
        // 0x31 start/results/stop cannot be paired by numeric routine ID +
        // ECU label alone: independent recordings and CAN buses reuse both.
        var definitions = catalogs.SelectMany(catalog => catalog.RoutineDefinitions)
            .GroupBy(routine => routine.Identifier)
            .ToDictionary(group => group.Key, group => group.ToArray());
        var builders = new List<RoutineBuilder>();
        foreach (var transaction in transactions.Where(item =>
                     item.Decoded.ServiceId == 0x31 &&
                     item.Request?.Payload.Length >= 4))
        {
            var payload = transaction.Request!.Payload;
            var action = (byte)(payload[1] & 0x7F);
            var identifier = (payload[2] << 8) | payload[3];
            var keyEcu = transaction.Endpoint.Ecu;
            var source = -1;
            var channel = -1;
            if (sourceAt is not null)
            {
                if (transaction.FrameIndices.Count == 0 ||
                    transaction.FrameIndices[0] < 0)
                    throw new InvalidOperationException(
                        "RoutineControl has no recorded source-frame evidence.");
                source = sourceAt(transaction.FrameIndices[0]);
                if (source < 0)
                    throw new InvalidOperationException("RoutineControl has an invalid original recording.");
                foreach (var index in transaction.FrameIndices
                    .Concat(transaction.Request?.FrameIndices ?? [])
                    .Concat(transaction.Responses.SelectMany(reply => reply.FrameIndices)))
                {
                    if (index < 0 || sourceAt(index) != source)
                        throw new InvalidOperationException(
                            "RoutineControl request/results cross independent original logger files.");
                }
                channel = transaction.Request?.Channel ??
                    transaction.Responses.FirstOrDefault()?.Channel ?? -1;
            }
            var builder = action == 1 ? null
                : builders.LastOrDefault(item =>
                    item.Identifier == identifier &&
                    string.Equals(item.Ecu, keyEcu, StringComparison.OrdinalIgnoreCase) &&
                    item.OriginalSourceIndex == source &&
                    item.SourceChannel == channel &&
                    !item.Closed);
            if (builder is null)
            {
                definitions.TryGetValue(identifier, out var alternatives);
                var matches = alternatives?.Where(item =>
                    string.Equals(item.Ecu, keyEcu, StringComparison.OrdinalIgnoreCase))
                    .ToArray() ?? [];
                // If more than one conflicting CDD/ODX variant claims this
                // same ECU/routine, do not arbitrarily pick the first.
                var definition = matches.Length == 1 ? matches[0] : null;
                builder = new RoutineBuilder(identifier, definition?.DisplayName ?? string.Empty,
                    keyEcu, transaction.StartTimestampNanoseconds,
                    source, channel, definition?.SourceFile ?? string.Empty);
                builders.Add(builder);
            }

            builder.Add(transaction, action);
        }

        return builders.Select((builder, index) => builder.Build(index + 1)).ToArray();
    }

    private static double? Average(IEnumerable<double?> values)
    {
        var materialized = values.Where(value => value is not null).Select(value => value!.Value).ToArray();
        return materialized.Length == 0 ? null : materialized.Average();
    }

    private sealed class RoutineBuilder(int identifier, string name, string ecu, long start,
        int originalSourceIndex, int sourceChannel, string definitionSourceFile)
    {
        private readonly List<RoutineStep> _steps = [];
        private readonly HashSet<int> _frames = [];
        public int Identifier { get; } = identifier;
        public string Ecu { get; } = ecu;
        public int OriginalSourceIndex { get; } = originalSourceIndex;
        public int SourceChannel { get; } = sourceChannel;
        public bool Closed { get; private set; }

        public void Add(DiagnosticTransaction transaction, byte action)
        {
            foreach (var frame in transaction.FrameIndices) _frames.Add(frame);
            var requestData = transaction.Request!.Payload.Length > 4
                ? Hex(transaction.Request.Payload.AsSpan(4)) : string.Empty;
            var terminal = transaction.Responses.LastOrDefault(response => response.Payload.Length > 0 &&
                !(response.Payload[0] == 0x7F && response.Payload.Length > 2 && response.Payload[2] == 0x78));
            var responseData = terminal?.Payload.Length > 4 ? Hex(terminal.Payload.AsSpan(4)) : string.Empty;
            _steps.Add(new RoutineStep(
                action, ActionName(action), transaction.StartTimestampNanoseconds, transaction.Outcome,
                requestData, responseData, transaction.Decoded.NegativeResponseText,
                transaction.Responses.Count(response => response.Payload is [0x7F, 0x31, 0x78, ..]),
                transaction.FrameIndices));
            Closed = transaction.Outcome == DiagnosticOutcome.Negative || action == 2 || action == 3;
        }

        public RoutineExecution Build(int sequence)
        {
            var last = _steps[^1];
            var state = _steps.Any(step => step.Outcome == DiagnosticOutcome.Negative)
                ? RoutineExecutionState.Failed
                : last.Action == 2 ? RoutineExecutionState.Stopped
                : last.Action == 3 && last.Outcome == DiagnosticOutcome.Positive ? RoutineExecutionState.Completed
                : last.Action == 1 && last.Outcome == DiagnosticOutcome.Positive ? RoutineExecutionState.Completed
                : RoutineExecutionState.Incomplete;
            var end = _steps.Max(step => step.TimestampNanoseconds);
            return new RoutineExecution(
                sequence, Identifier, $"0x{Identifier:X4}", name, Ecu, start, end,
                Math.Max(0, end - start) / 1_000_000d, state,
                _steps.FirstOrDefault(step => step.Action == 1)?.RequestData ?? string.Empty,
                _steps.LastOrDefault(step => !string.IsNullOrWhiteSpace(step.ResponseData))?.ResponseData ??
                    _steps.LastOrDefault()?.NegativeResponse ?? string.Empty,
                _steps.Sum(step => step.ResponsePendingCount),
                _steps, _frames.OrderBy(frame => frame).ToArray())
            {
                OriginalSourceIndex = OriginalSourceIndex,
                SourceChannel = SourceChannel,
                DefinitionSourceFile = definitionSourceFile
            };
        }

        private static string ActionName(byte action) => action switch
        {
            1 => "Start", 2 => "Stop", 3 => "Request results", _ => $"Action 0x{action:X2}"
        };

        private static string Hex(ReadOnlySpan<byte> data) =>
            string.Join(' ', data.ToArray().Select(value => value.ToString("X2", CultureInfo.InvariantCulture)));
    }
}
