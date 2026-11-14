using RbgCanInsight.DiagData;

namespace RbgCanInsight.Diagnostics;

public enum SpecTraceFindingKind
{
    ObservedDidNotDeclared,
    DeclaredDidNotObserved,
    ObservedRoutineNotDeclared,
    DeclaredRoutineNotObserved,
    ObservedDtcNotDeclared,
    NegativeResponse,
    NoResponse,
    RoutinePrerequisiteNotMet,
    RoutinePrerequisiteUnverifiable
}

/// <summary>One declared-versus-observed difference with the original frames that prove it.</summary>
public sealed record SpecTraceFinding(
    SpecTraceFindingKind Kind,
    string Identifier,
    string Description,
    int Count,
    IReadOnlyList<int> FrameIndices,
    string Caveat);

public sealed record SpecTraceReport(
    IReadOnlyList<SpecTraceFinding> Findings,
    int TransactionsAnalyzed,
    int DeclaredDidCount,
    int ObservedDidCount,
    int DeclaredRoutineCount,
    int ObservedRoutineCount,
    string Limitations);

/// <summary>Compares diagnostic definitions loaded from CDD/ODX with diagnostic traffic that was
/// actually recorded. It reports observation/declaration gaps only: an undeclared or unobserved
/// identifier is not proof of a defect (catalogs can be partial; a capture is a finite window).</summary>
public static class DiagnosticSpecTraceChecker
{
    private const int MaxFramesPerFinding = 20;

    public const string StandardLimitations =
        "Declared-vs-observed comparison only. Session/security state comes solely from positive " +
        "0x10/0x11/0x27 responses recorded in this capture (state before the first one is unknown); " +
        "prerequisite text that cannot be parsed is not evaluated; identifiers are matched across all " +
        "loaded catalogs regardless of ECU, and a finite capture cannot prove an identifier is never used.";

    public static SpecTraceReport Check(
        IReadOnlyList<DiagnosticTransaction> transactions,
        IReadOnlyList<DidCatalog> catalogs)
    {
        var declaredDids = catalogs.SelectMany(catalog => catalog.Parameters)
            .Select(parameter => parameter.Did).ToHashSet();
        var declaredRoutines = catalogs.SelectMany(catalog => catalog.RoutineDefinitions)
            .Select(routine => routine.Identifier).ToHashSet();
        var declaredDtcs = catalogs.SelectMany(catalog => catalog.DtcDefinitions)
            .Select(dtc => dtc.Code).ToHashSet();

        var didFrames = new Dictionary<int, List<int>>();
        var routineFrames = new Dictionary<int, List<int>>();
        var dtcFrames = new Dictionary<int, List<int>>();
        var nrcFrames = new Dictionary<(byte Service, byte Nrc), List<int>>();
        var noResponse = new Dictionary<byte, List<int>>();

        static void Add<TKey>(Dictionary<TKey, List<int>> map, TKey key, IReadOnlyList<int> frames)
            where TKey : notnull
        {
            if (!map.TryGetValue(key, out var list)) map[key] = list = [];
            list.AddRange(frames);
        }

        foreach (var transaction in transactions)
        {
            var payload = transaction.Request?.Payload;
            var service = transaction.Decoded.ServiceId;
            if (payload is { Length: > 0 })
            {
                if (service == 0x22)
                {
                    for (var offset = 1; offset + 1 < payload.Length; offset += 2)
                        Add(didFrames, (payload[offset] << 8) | payload[offset + 1], transaction.FrameIndices);
                }
                else if (service == 0x2E && payload.Length >= 3)
                    Add(didFrames, (payload[1] << 8) | payload[2], transaction.FrameIndices);
                else if (service == 0x31 && payload.Length >= 4)
                    Add(routineFrames, (payload[2] << 8) | payload[3], transaction.FrameIndices);
            }

            foreach (var dtc in transaction.Decoded.Dtcs)
                Add(dtcFrames, dtc.Code, transaction.FrameIndices);

            if (transaction.Outcome == DiagnosticOutcome.Negative &&
                transaction.Decoded.NegativeResponseCode is { } nrc)
                Add(nrcFrames, (service, nrc), transaction.FrameIndices);
            else if (transaction.Outcome == DiagnosticOutcome.NoResponse)
                Add(noResponse, service, transaction.FrameIndices);
        }

        var findings = new List<SpecTraceFinding>();
        static IReadOnlyList<int> Proof(List<int> frames) =>
            frames.Distinct().Order().Take(MaxFramesPerFinding).ToArray();

        foreach (var (did, frames) in didFrames.OrderBy(pair => pair.Key))
        {
            if (declaredDids.Contains(did)) continue;
            findings.Add(new SpecTraceFinding(SpecTraceFindingKind.ObservedDidNotDeclared, $"0x{did:X4}",
                "DID used in recorded traffic but absent from every loaded catalog.",
                frames.Distinct().Count(), Proof(frames),
                "The catalog may be partial or belong to another ECU variant."));
        }
        foreach (var did in declaredDids.Where(did => !didFrames.ContainsKey(did)).Order())
            findings.Add(new SpecTraceFinding(SpecTraceFindingKind.DeclaredDidNotObserved, $"0x{did:X4}",
                "DID declared in a catalog but not requested in this capture.", 0, [],
                "Absence in one finite capture does not mean the DID is unused."));

        foreach (var (rid, frames) in routineFrames.OrderBy(pair => pair.Key))
        {
            if (declaredRoutines.Contains(rid)) continue;
            findings.Add(new SpecTraceFinding(SpecTraceFindingKind.ObservedRoutineNotDeclared, $"0x{rid:X4}",
                "Routine identifier used in recorded traffic but absent from every loaded catalog.",
                frames.Distinct().Count(), Proof(frames),
                "The catalog may be partial or belong to another ECU variant."));
        }
        foreach (var rid in declaredRoutines.Where(rid => !routineFrames.ContainsKey(rid)).Order())
            findings.Add(new SpecTraceFinding(SpecTraceFindingKind.DeclaredRoutineNotObserved, $"0x{rid:X4}",
                "Routine declared in a catalog but not executed in this capture.", 0, [],
                "Absence in one finite capture does not mean the routine is unused."));

        foreach (var (dtc, frames) in dtcFrames.OrderBy(pair => pair.Key))
        {
            if (declaredDtcs.Contains(dtc)) continue;
            findings.Add(new SpecTraceFinding(SpecTraceFindingKind.ObservedDtcNotDeclared, $"0x{dtc:X6}",
                "DTC reported in recorded responses but absent from every loaded catalog.",
                frames.Distinct().Count(), Proof(frames),
                "Reported status belongs to the ECU; the catalog may be partial."));
        }

        foreach (var ((service, nrc), frames) in nrcFrames.OrderBy(pair => pair.Key.Service).ThenBy(pair => pair.Key.Nrc))
            findings.Add(new SpecTraceFinding(SpecTraceFindingKind.NegativeResponse,
                $"SID 0x{service:X2} / NRC 0x{nrc:X2}",
                $"Negative response 0x{nrc:X2} to service 0x{service:X2}.",
                frames.Distinct().Count(), Proof(frames),
                "Cause of the refusal is not determined by the trace alone."));
        foreach (var (service, frames) in noResponse.OrderBy(pair => pair.Key))
            findings.Add(new SpecTraceFinding(SpecTraceFindingKind.NoResponse, $"SID 0x{service:X2}",
                $"Request to service 0x{service:X2} without a recorded response.",
                frames.Distinct().Count(), Proof(frames),
                "The response may lie outside the capture window or on an unrecorded channel."));

        AddRoutinePrerequisiteFindings(findings, transactions, catalogs);

        return new SpecTraceReport(findings, transactions.Count, declaredDids.Count, didFrames.Count,
            declaredRoutines.Count, routineFrames.Count, StandardLimitations);
    }

    /// <summary>Declared session text → UDS session id: "0x03", "3", or the ISO names.</summary>
    public static int? ParseSession(string text)
    {
        var value = text.Trim();
        if (value.Length == 0) return null;
        if (value.Contains("default", StringComparison.OrdinalIgnoreCase)) return 0x01;
        if (value.Contains("programming", StringComparison.OrdinalIgnoreCase)) return 0x02;
        if (value.Contains("extended", StringComparison.OrdinalIgnoreCase)) return 0x03;
        return ParseNumber(value);
    }

    /// <summary>Declared security text → level; "none"/empty means no requirement (null).</summary>
    public static int? ParseSecurityLevel(string text)
    {
        var value = text.Trim();
        if (value.Length == 0 || value.Equals("none", StringComparison.OrdinalIgnoreCase)) return null;
        var named = System.Text.RegularExpressions.Regex.Match(value,
            @"^(?:security\s*)?(?:level|lvl)[\s_-]*(0x[0-9a-f]+|\d+)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return named.Success ? ParseNumber(named.Groups[1].Value) : ParseNumber(value);
    }

    private static int? ParseNumber(string value)
    {
        value = value.Trim();
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(value[2..], System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out var hex)) return hex;
        return int.TryParse(value, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var number) ? number : null;
    }

    /// <summary>"A | B" = any of the allowed states. Returns null when the text is empty or any part
    /// cannot be parsed: an unparseable prerequisite is not evaluated, never guessed.</summary>
    private static int[]? ParseSet(string text, Func<string, int?> parse)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var values = new List<int>();
        foreach (var part in text.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (parse(part) is not { } value) return null;
            values.Add(value);
        }
        return values.Count == 0 ? null : [.. values.Distinct()];
    }

    private static string Hex(int[] values) =>
        values.Length == 1 ? $"0x{values[0]:X2}" : "one of " + string.Join("/", values.Select(v => $"0x{v:X2}"));

    private static void AddRoutinePrerequisiteFindings(
        List<SpecTraceFinding> findings,
        IReadOnlyList<DiagnosticTransaction> transactions,
        IReadOnlyList<DidCatalog> catalogs)
    {
        var routines = catalogs.SelectMany(catalog => catalog.RoutineDefinitions)
            .GroupBy(routine => routine.Identifier).ToDictionary(group => group.Key, group => group.First());
        var tracker = DiagnosticSessionStateTracker.Reconstruct(transactions);
        var notMet = new Dictionary<string, List<int>>();
        var unverifiable = new Dictionary<string, List<int>>();
        foreach (var transaction in transactions)
        {
            if (transaction.Decoded.ServiceId != 0x31 || transaction.Request?.Payload is not { Length: >= 4 } payload)
                continue;
            if (!routines.TryGetValue((payload[2] << 8) | payload[3], out var routine)) continue;
            var requiredSession = ParseSet(routine.RequiredSession, ParseSession);
            var requiredLevel = ParseSet(routine.RequiredSecurityLevel, ParseSecurityLevel);
            if (requiredSession is null && requiredLevel is null) continue;

            var state = tracker.StateAt(transaction.Endpoint.Ecu, transaction.Request.StartTimestampNanoseconds);
            var label = $"{routine.IdentifierHex} {routine.DisplayName}".Trim();
            if (!state.Known || (requiredSession is not null && state.Session is null) ||
                (requiredLevel is not null && state.SecurityLevel is null))
            {
                if (!unverifiable.TryGetValue(label, out var list)) unverifiable[label] = list = [];
                list.AddRange(transaction.FrameIndices);
                continue;
            }
            var problems = new List<string>();
            if (requiredSession is { } sessions && !sessions.Contains(state.Session!.Value))
                problems.Add($"declared session {Hex(sessions)}, recorded session 0x{state.Session:X2}");
            if (requiredLevel is { } levels && !levels.Contains(state.SecurityLevel!.Value))
                problems.Add(state.SecurityLevel is 0
                    ? $"declared security level {Hex(levels)}, recorded locked"
                    : $"declared security level {Hex(levels)}, recorded level 0x{state.SecurityLevel:X2}");
            if (problems.Count == 0) continue;
            var key = label + ": " + string.Join("; ", problems);
            if (!notMet.TryGetValue(key, out var frames)) notMet[key] = frames = [];
            frames.AddRange(transaction.FrameIndices);
        }

        foreach (var (key, frames) in notMet.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            findings.Add(new SpecTraceFinding(SpecTraceFindingKind.RoutinePrerequisiteNotMet, key,
                "Routine requested while recorded session/security state differs from the catalog prerequisite.",
                frames.Distinct().Count(), frames.Distinct().Order().Take(MaxFramesPerFinding).ToArray(),
                "State is reconstructed only from positive responses in this capture; the catalog text may describe a different variant."));
        foreach (var (key, frames) in unverifiable.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            findings.Add(new SpecTraceFinding(SpecTraceFindingKind.RoutinePrerequisiteUnverifiable, key,
                "Routine has a declared prerequisite but the capture shows no session/security state before the request.",
                frames.Distinct().Count(), frames.Distinct().Order().Take(MaxFramesPerFinding).ToArray(),
                "The prerequisite may have been satisfied before the capture started."));
    }
}
