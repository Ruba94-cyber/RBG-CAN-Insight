using System.Globalization;
using System.Text;
using RbgCanInsight.Domain;

namespace RbgCanInsight.Diagnostics;

public enum DiagnosticTimingSeverity
{
    Information,
    Warning,
    Error
}

/// <summary>Documented timing rule. IDs are stable (TIM-0xx) so reports and CI can reference them.</summary>
public sealed record DiagnosticTimingRule(
    string Id, string Title, DiagnosticTimingSeverity DefaultSeverity, string Description);

/// <summary>One timing observation. <see cref="FrameIndices"/> are indices into the analysed frame
/// list: every finding names the frames that prove it.</summary>
public sealed record DiagnosticTimingFinding(
    DiagnosticTimingRule Rule,
    DiagnosticTimingSeverity Severity,
    string Ecu,
    string Message,
    double? MeasuredMilliseconds,
    double? LimitMilliseconds,
    long TimestampNanoseconds,
    IReadOnlyList<int> FrameIndices,
    int? TransactionSequence)
{
    public string RuleId => Rule.Id;
}

public sealed record DiagnosticTimingOptions
{
    /// <summary>P2server_max default (ISO 14229-2): 50 ms. Replaced per ECU by the value a 0x10 positive response announces.</summary>
    public double P2ServerMaxMilliseconds { get; init; } = 50;

    /// <summary>P2*server_max default: 5000 ms. Replaced per ECU by the 0x10 positive response.</summary>
    public double P2StarServerMaxMilliseconds { get; init; } = 5000;

    /// <summary>S3server default: 5000 ms.</summary>
    public double S3ServerMilliseconds { get; init; } = 5000;

    /// <summary>N_Bs (time until flow control) and N_Cr (time until next consecutive frame): 1000 ms (ISO 15765-2).</summary>
    public double NBsMilliseconds { get; init; } = 1000;
    public double NCrMilliseconds { get; init; } = 1000;

    /// <summary>Use P2/P2* announced by 0x10 positive responses (bytes 2..5) for later transactions of that ECU.</summary>
    public bool UseAnnouncedSessionTiming { get; init; } = true;

    /// <summary>Slack added to every limit to absorb logger timestamp resolution. Default 0 (strict).</summary>
    public double ToleranceMilliseconds { get; init; }

    /// <summary>STmin violation slack: fraction of STmin plus an absolute 0.1 ms.</summary>
    public double StMinToleranceFraction { get; init; } = 0.10;

    /// <summary>NRC 0x78 chain length that is reported as information / as a warning.</summary>
    public int PendingChainInformationCount { get; init; } = 5;
    public int PendingChainWarningCount { get; init; } = 20;

    public int MaxFindings { get; init; } = 5000;
}

public sealed record DiagnosticTimingReport(
    IReadOnlyList<DiagnosticTimingFinding> Findings,
    IReadOnlyList<DiagnosticTimingRule> RulesEvaluated,
    IReadOnlyList<string> Notes,
    bool Truncated)
{
    public static DiagnosticTimingReport Empty { get; } = new([], [], [], false);

    public int Count(DiagnosticTimingSeverity severity) => Findings.Count(f => f.Severity == severity);

    public string ToSummaryText()
    {
        var text = FormattableString.Invariant(
            $"Diagnostic timing: {Count(DiagnosticTimingSeverity.Error)} error(s), {Count(DiagnosticTimingSeverity.Warning)} warning(s), {Count(DiagnosticTimingSeverity.Information)} information item(s); {RulesEvaluated.Count} rule(s) evaluated");
        return text + (Truncated ? "; output truncated" : string.Empty) + ".";
    }

    public string ToCsv()
    {
        var sb = new StringBuilder("Rule,Title,Severity,ECU,Time (s),Measured (ms),Limit (ms),Message,Source frames\r\n");
        foreach (var f in Findings)
        {
            var cells = new[]
            {
                f.Rule.Id, f.Rule.Title, f.Severity.ToString(), f.Ecu,
                (f.TimestampNanoseconds / 1e9).ToString("F6", CultureInfo.InvariantCulture),
                f.MeasuredMilliseconds?.ToString("F3", CultureInfo.InvariantCulture) ?? string.Empty,
                f.LimitMilliseconds?.ToString("F3", CultureInfo.InvariantCulture) ?? string.Empty,
                f.Message, string.Join(' ', f.FrameIndices)
            };
            sb.Append(string.Join(',', cells.Select(Csv))).Append("\r\n");
        }
        return sb.ToString();

        static string Csv(string v)
        {
            if (v.Length > 0 && v[0] is '=' or '+' or '-' or '@') v = "'" + v;
            return v.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
        }
    }
}

/// <summary>
/// Post-hoc diagnostic timing checks on a recorded trace: P2/P2*, NRC 0x78 chains, S3 and
/// TesterPresent gaps (transaction level) and ISO-TP N_Bs / N_Cr / STmin (frame level).
/// Everything is measured from logger timestamps of frames that really are in the trace; no
/// finding exists without source frames. N_As and N_Ar are transmit-confirmation timers inside the
/// node's CAN driver and cannot be observed on the bus, so they are not evaluated (see Notes).
/// Limits default to the ISO 14229-2 / 15765-2 defaults and are replaced per ECU by the P2/P2* a
/// 0x10 positive response announces. S3 is only judged while a non-default session was recorded.
/// </summary>
public static class DiagnosticTimingAnalyzer
{
    public static readonly DiagnosticTimingRule P2Exceeded = new("TIM-001", "P2 response time exceeded",
        DiagnosticTimingSeverity.Warning,
        "The first response (final or NRC 0x78) arrived later than P2server_max after the end of the request.");
    public static readonly DiagnosticTimingRule P2StarExceeded = new("TIM-002", "P2* response time exceeded",
        DiagnosticTimingSeverity.Warning,
        "After an NRC 0x78 the next response arrived later than P2*server_max.");
    public static readonly DiagnosticTimingRule PendingChain = new("TIM-003", "NRC 0x78 response-pending chain",
        DiagnosticTimingSeverity.Information,
        "A request was answered by a long chain of NRC 0x78 before its final response.");
    public static readonly DiagnosticTimingRule PendingUnterminated = new("TIM-004", "Response-pending chain without final response",
        DiagnosticTimingSeverity.Warning,
        "NRC 0x78 was answered but no final positive or negative response followed in the trace.");
    public static readonly DiagnosticTimingRule S3Expired = new("TIM-005", "S3 timeout without request",
        DiagnosticTimingSeverity.Warning,
        "No request reached an ECU for longer than S3server while a non-default session was recorded; the ECU may have fallen back to the default session.");
    public static readonly DiagnosticTimingRule TesterPresentGap = new("TIM-006", "TesterPresent stream interrupted",
        DiagnosticTimingSeverity.Warning,
        "An established TesterPresent stream paused for longer than S3server while a non-default session was recorded.");
    public static readonly DiagnosticTimingRule NBsExceeded = new("TIM-007", "ISO-TP N_Bs exceeded",
        DiagnosticTimingSeverity.Warning,
        "The flow-control frame after a first frame (or after a block of consecutive frames) arrived later than N_Bs.");
    public static readonly DiagnosticTimingRule NCrExceeded = new("TIM-008", "ISO-TP N_Cr exceeded",
        DiagnosticTimingSeverity.Warning,
        "The next consecutive frame arrived later than N_Cr after the previous frame or flow control.");
    public static readonly DiagnosticTimingRule StMinViolated = new("TIM-009", "ISO-TP STmin violated",
        DiagnosticTimingSeverity.Warning,
        "Consecutive frames were sent closer together than the STmin requested by the receiver's flow control.");
    public static readonly DiagnosticTimingRule FlowControlOverflow = new("TIM-010", "ISO-TP flow control overflow",
        DiagnosticTimingSeverity.Warning,
        "The receiver answered a first frame with flow status OVERFLOW (0x32): the transfer was aborted.");

    public static readonly IReadOnlyList<DiagnosticTimingRule> Rules =
    [
        P2Exceeded, P2StarExceeded, PendingChain, PendingUnterminated, S3Expired, TesterPresentGap,
        NBsExceeded, NCrExceeded, StMinViolated, FlowControlOverflow
    ];

    private const double NanosPerMilli = 1_000_000d;

    public static DiagnosticTimingReport Analyze(
        IReadOnlyList<DiagnosticTransaction> transactions,
        IReadOnlyList<CanFrame>? frames = null,
        DiagnosticTimingOptions? options = null)
    {
        options ??= new DiagnosticTimingOptions();
        var findings = new List<DiagnosticTimingFinding>();
        var notes = new List<string>
        {
            "N_As / N_Ar are measured inside the node's CAN driver (transmit confirmation) and are not observable in a bus log; not evaluated.",
        };
        var evaluated = new List<DiagnosticTimingRule>
            { P2Exceeded, P2StarExceeded, PendingChain, PendingUnterminated, S3Expired, TesterPresentGap };

        AnalyzeTransactions(transactions, frames, options, findings, notes);

        if (frames is null)
            notes.Add("ISO-TP timing (N_Bs, N_Cr, STmin) needs the CAN frames and was skipped.");
        else
        {
            AnalyzeIsoTp(transactions, frames, options, findings);
            evaluated.AddRange([NBsExceeded, NCrExceeded, StMinViolated, FlowControlOverflow]);
        }

        var ordered = findings
            .OrderBy(f => f.TimestampNanoseconds).ThenBy(f => f.Rule.Id, StringComparer.Ordinal)
            .ToList();
        var truncated = ordered.Count > options.MaxFindings;
        if (truncated) ordered.RemoveRange(options.MaxFindings, ordered.Count - options.MaxFindings);
        return new DiagnosticTimingReport(ordered, evaluated, notes, truncated);
    }

    private static DiagnosticTimingSeverity Escalate(
        DiagnosticTimingRule rule, double measured, double limit) =>
        measured >= limit * 2 && rule.DefaultSeverity == DiagnosticTimingSeverity.Warning
            ? DiagnosticTimingSeverity.Error
            : rule.DefaultSeverity;

    private static string Ms(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    // ---- transaction level ----

    private static void AnalyzeTransactions(
        IReadOnlyList<DiagnosticTransaction> transactions,
        IReadOnlyList<CanFrame>? frames,
        DiagnosticTimingOptions options,
        List<DiagnosticTimingFinding> findings,
        List<string> notes)
    {
        var tolerance = options.ToleranceMilliseconds;
        var announced = new Dictionary<string, List<(long Time, double P2, double P2Star)>>(StringComparer.OrdinalIgnoreCase);
        if (options.UseAnnouncedSessionTiming)
        {
            foreach (var t in transactions.Where(t => t.Decoded.ServiceId == 0x10 && t.Outcome == DiagnosticOutcome.Positive))
            {
                var response = t.Responses.LastOrDefault(r => r.Payload.Length >= 6 && r.Payload[0] == 0x50);
                if (response is null || !UdsExtendedServices.TryReadTimingValues(response.Payload.AsSpan(2, 4), out var p2, out var p2Star))
                    continue;
                if (!announced.TryGetValue(t.Endpoint.Ecu, out var list)) announced[t.Endpoint.Ecu] = list = [];
                list.Add((t.EndTimestampNanoseconds, p2, p2Star));
            }
            foreach (var list in announced.Values) list.Sort((a, b) => a.Time.CompareTo(b.Time));
            if (announced.Count > 0)
                notes.Add($"P2/P2* announced by 0x10 positive responses were used for {announced.Count} ECU(s) after their announcement.");
        }

        (double P2, double P2Star) LimitsFor(string ecu, long time)
        {
            var p2 = options.P2ServerMaxMilliseconds;
            var p2Star = options.P2StarServerMaxMilliseconds;
            if (announced.TryGetValue(ecu, out var list))
                foreach (var entry in list)
                {
                    if (entry.Time > time) break;
                    // A zero P2 is a placeholder in some ECUs; keep the default then.
                    if (entry.P2 > 0) p2 = entry.P2;
                    if (entry.P2Star > 0) p2Star = entry.P2Star;
                }
            return (p2, p2Star);
        }

        foreach (var t in transactions.Where(t => t.Request is not null && t.Responses.Count > 0))
        {
            var request = t.Request!;
            var (p2Limit, p2StarLimit) = LimitsFor(t.Endpoint.Ecu, request.EndTimestampNanoseconds);
            var responses = t.Responses.OrderBy(r => r.StartTimestampNanoseconds).ToArray();
            var requestFrames = request.FrameIndices;

            var first = responses[0];
            var firstGap = (first.StartTimestampNanoseconds - request.EndTimestampNanoseconds) / NanosPerMilli;
            if (firstGap > p2Limit + tolerance)
            {
                findings.Add(new DiagnosticTimingFinding(P2Exceeded,
                    Escalate(P2Exceeded, firstGap, p2Limit), t.Endpoint.Ecu,
                    $"{t.Decoded.ServiceName}: first response after {Ms(firstGap)} ms, P2server_max is {Ms(p2Limit)} ms.",
                    firstGap, p2Limit, first.StartTimestampNanoseconds,
                    Join(requestFrames, first.FrameIndices), t.Sequence));
            }

            var pending = 0;
            for (var i = 0; i < responses.Length; i++)
            {
                var isPending = IsPending(responses[i]);
                if (isPending) pending++;
                if (i == 0 || !IsPending(responses[i - 1])) continue;
                var gap = (responses[i].StartTimestampNanoseconds - responses[i - 1].EndTimestampNanoseconds) / NanosPerMilli;
                if (gap > p2StarLimit + tolerance)
                {
                    findings.Add(new DiagnosticTimingFinding(P2StarExceeded,
                        Escalate(P2StarExceeded, gap, p2StarLimit), t.Endpoint.Ecu,
                        $"{t.Decoded.ServiceName}: {Ms(gap)} ms between NRC 0x78 and the next response, P2*server_max is {Ms(p2StarLimit)} ms.",
                        gap, p2StarLimit, responses[i].StartTimestampNanoseconds,
                        Join(responses[i - 1].FrameIndices, responses[i].FrameIndices), t.Sequence));
                }
            }

            if (pending >= options.PendingChainInformationCount)
            {
                var severity = pending >= options.PendingChainWarningCount
                    ? DiagnosticTimingSeverity.Warning : DiagnosticTimingSeverity.Information;
                var lastPending = responses.Last(IsPending);
                var duration = (lastPending.EndTimestampNanoseconds - request.EndTimestampNanoseconds) / NanosPerMilli;
                findings.Add(new DiagnosticTimingFinding(PendingChain, severity, t.Endpoint.Ecu,
                    $"{t.Decoded.ServiceName}: {pending} NRC 0x78 responses over {Ms(duration)} ms before the {(IsPending(responses[^1]) ? "chain ended" : "final response")}.",
                    duration, null, lastPending.EndTimestampNanoseconds,
                    Join(requestFrames, responses.SelectMany(r => r.FrameIndices)), t.Sequence));
            }

            if (pending > 0 && IsPending(responses[^1]) && t.Outcome != DiagnosticOutcome.Positive && t.Outcome != DiagnosticOutcome.Negative)
            {
                findings.Add(new DiagnosticTimingFinding(PendingUnterminated, PendingUnterminated.DefaultSeverity,
                    t.Endpoint.Ecu,
                    $"{t.Decoded.ServiceName}: {pending} NRC 0x78 response(s) but no final response in the trace.",
                    null, null, responses[^1].EndTimestampNanoseconds,
                    Join(requestFrames, responses.SelectMany(r => r.FrameIndices)), t.Sequence));
            }
        }

        AnalyzeS3(transactions, options, findings, notes);
    }

    private static bool IsPending(IsoTpMessage message) =>
        message.Payload is [0x7F, _, 0x78, ..];

    private static void AnalyzeS3(
        IReadOnlyList<DiagnosticTransaction> transactions,
        DiagnosticTimingOptions options,
        List<DiagnosticTimingFinding> findings,
        List<string> notes)
    {
        var tracker = DiagnosticSessionStateTracker.Reconstruct(transactions);
        var limit = options.S3ServerMilliseconds + options.ToleranceMilliseconds;
        var evaluatedEcus = 0;
        foreach (var group in transactions
                     .Where(t => t.Request is not null && !string.IsNullOrEmpty(t.Endpoint.Ecu))
                     .GroupBy(t => t.Endpoint.Ecu, StringComparer.OrdinalIgnoreCase))
        {
            var ordered = group.OrderBy(t => t.Request!.StartTimestampNanoseconds).ToArray();
            var sawNonDefault = false;
            var tpStreak = 0;
            for (var i = 1; i < ordered.Length; i++)
            {
                var previous = ordered[i - 1];
                var next = ordered[i];
                var gap = (next.Request!.StartTimestampNanoseconds - previous.EndTimestampNanoseconds) / NanosPerMilli;
                var bothTp = previous.Decoded.ServiceId == 0x3E && next.Decoded.ServiceId == 0x3E;
                var session = tracker.StateAt(group.Key, previous.EndTimestampNanoseconds).Session;
                var nonDefault = session is not null && session != 0x01;
                if (nonDefault) sawNonDefault = true;
                if (gap > limit && nonDefault)
                {
                    var frames = Join(previous.FrameIndices, next.Request.FrameIndices);
                    if (bothTp && tpStreak >= 2)
                    {
                        findings.Add(new DiagnosticTimingFinding(TesterPresentGap, TesterPresentGap.DefaultSeverity,
                            group.Key,
                            $"TesterPresent stream of {tpStreak + 1} requests paused for {Ms(gap)} ms (S3server {Ms(options.S3ServerMilliseconds)} ms) in session 0x{session:X2}.",
                            gap, options.S3ServerMilliseconds, next.Request.StartTimestampNanoseconds, frames, next.Sequence));
                    }
                    else
                    {
                        findings.Add(new DiagnosticTimingFinding(S3Expired, S3Expired.DefaultSeverity, group.Key,
                            $"No request for {Ms(gap)} ms (S3server {Ms(options.S3ServerMilliseconds)} ms) while session 0x{session:X2} was active; next request is {next.Decoded.ServiceName}.",
                            gap, options.S3ServerMilliseconds, next.Request.StartTimestampNanoseconds, frames, next.Sequence));
                    }
                }
                tpStreak = bothTp && gap <= limit ? tpStreak + 1 : 0;
            }
            if (sawNonDefault) evaluatedEcus++;
        }
        notes.Add(evaluatedEcus == 0
            ? "S3 / TesterPresent gaps were not evaluated: no non-default session was recorded (the state before the first 0x10 response is unknown)."
            : $"S3 / TesterPresent gaps were evaluated for {evaluatedEcus} ECU(s) with a recorded non-default session.");
    }

    // ---- ISO-TP level ----

    private static void AnalyzeIsoTp(
        IReadOnlyList<DiagnosticTransaction> transactions,
        IReadOnlyList<CanFrame> frames,
        DiagnosticTimingOptions options,
        List<DiagnosticTimingFinding> findings)
    {
        var seen = new HashSet<int>();
        void Check(IsoTpMessage message, uint partner, DiagnosticTransaction transaction)
        {
            if (message.FrameIndices.Count == 0 || !seen.Add(message.FrameIndices[0])) return;
            CheckMessage(message, partner, transaction, frames, options, findings);
        }

        foreach (var t in transactions)
        {
            if (t.Request is not { } request) continue;
            if (t.Responses.Count > 0) Check(request, t.Responses[0].Identifier, t);
            foreach (var response in t.Responses) Check(response, request.Identifier, t);
        }
    }

    private static void CheckMessage(
        IsoTpMessage message, uint partnerIdentifier, DiagnosticTransaction transaction,
        IReadOnlyList<CanFrame> frames, DiagnosticTimingOptions options, List<DiagnosticTimingFinding> findings)
    {
        if (message.FrameIndices.Count < 1) return;
        var firstIndex = message.FrameIndices[0];
        if (firstIndex < 0 || firstIndex >= frames.Count) return;
        var firstFrame = frames[firstIndex];
        if (firstFrame.DataLength == 0 || firstFrame.GetByte(0) >> 4 != 0x1) return;

        var messageFrames = new HashSet<int>(message.FrameIndices);
        var lastIndex = Math.Min(frames.Count - 1, message.FrameIndices[^1]);
        // An unfinished transfer has no known last frame; look a bounded distance ahead for its FC.
        var scanEnd = message.FrameIndices.Count == 1
            ? Math.Min(frames.Count - 1, firstIndex + 2000) : lastIndex;
        var tolerance = options.ToleranceMilliseconds;
        var ecu = transaction.Endpoint.Ecu;
        var waiting = true;
        var fcSeen = false;
        var waitStart = firstFrame.TimestampNanoseconds;
        var waitFrame = firstIndex;
        var lastEventTime = firstFrame.TimestampNanoseconds;
        var lastEventIndex = firstIndex;
        var lastWasFc = false;
        var blockSize = 0;
        var blockCount = 0;
        double stMin = 0;

        var nBsWorst = 0d; var nBsCount = 0; var nBsFrames = new List<int>(); long nBsTime = 0;
        var nCrWorst = 0d; var nCrCount = 0; var nCrFrames = new List<int>(); long nCrTime = 0;
        var stWorst = 0d; var stCount = 0; var stFrames = new List<int>(); long stTime = 0; double stLimit = 0;

        for (var index = firstIndex + 1; index <= scanEnd; index++)
        {
            var frame = frames[index];
            if (frame.Channel != message.Channel || frame.IsError || frame.Flags.HasFlag(CanFrameFlags.Remote) || frame.DataLength == 0)
                continue;
            if (frame.TimestampNanoseconds - firstFrame.TimestampNanoseconds > 120_000_000_000L) break;
            var pci = frame.GetByte(0) >> 4;
            if (messageFrames.Contains(index) && pci == 0x2)
            {
                if (waiting && !fcSeen)
                {
                    // No flow control in the trace (for example logged on another channel).
                    waiting = false;
                }
                else if (waiting)
                {
                    // Sender continued while a WAIT/block boundary was pending: not judged here.
                    waiting = false;
                }

                var gap = (frame.TimestampNanoseconds - lastEventTime) / NanosPerMilli;
                if (fcSeen)
                {
                    if (gap > options.NCrMilliseconds + tolerance)
                    {
                        nCrCount++;
                        if (gap > nCrWorst) { nCrWorst = gap; nCrTime = frame.TimestampNanoseconds; nCrFrames = [lastEventIndex, index]; }
                    }
                    if (!lastWasFc && stMin > 0 && gap < stMin - (stMin * options.StMinToleranceFraction + 0.1))
                    {
                        stCount++;
                        var shortfall = stMin - gap;
                        if (shortfall > stWorst) { stWorst = shortfall; stTime = frame.TimestampNanoseconds; stFrames = [lastEventIndex, index]; stLimit = stMin; }
                    }
                }

                lastEventTime = frame.TimestampNanoseconds;
                lastEventIndex = index;
                lastWasFc = false;
                blockCount++;
                if (blockSize > 0 && blockCount >= blockSize)
                {
                    waiting = true;
                    waitStart = frame.TimestampNanoseconds;
                    waitFrame = index;
                    blockCount = 0;
                }
                continue;
            }

            if (pci == 0x3 && frame.DataLength >= 3 && frame.Identifier == partnerIdentifier &&
                frame.IsExtended == message.IsExtended && !messageFrames.Contains(index))
            {
                var status = frame.GetByte(0) & 0x0F;
                if (status > 2) continue;
                if (waiting)
                {
                    var gap = (frame.TimestampNanoseconds - waitStart) / NanosPerMilli;
                    if (gap > options.NBsMilliseconds + tolerance)
                    {
                        nBsCount++;
                        if (gap > nBsWorst) { nBsWorst = gap; nBsTime = frame.TimestampNanoseconds; nBsFrames = [waitFrame, index]; }
                    }
                }
                fcSeen = true;
                lastEventTime = frame.TimestampNanoseconds;
                lastEventIndex = index;
                lastWasFc = true;
                if (status == 2)
                {
                    findings.Add(new DiagnosticTimingFinding(FlowControlOverflow, FlowControlOverflow.DefaultSeverity, ecu,
                        $"Flow control OVERFLOW (0x{frame.Identifier:X}) answered the first frame of a {message.Payload.Length}-byte transfer.",
                        null, null, frame.TimestampNanoseconds, [firstIndex, index], transaction.Sequence));
                    break;
                }
                if (status == 1)
                {
                    waiting = true; // N_Bs restarts after each WAIT
                    waitStart = frame.TimestampNanoseconds;
                    waitFrame = index;
                    continue;
                }
                waiting = false;
                blockSize = frame.GetByte(1);
                stMin = DecodeStMinMilliseconds(frame.GetByte(2));
                blockCount = 0;
            }
        }

        if (nBsCount > 0)
            findings.Add(new DiagnosticTimingFinding(NBsExceeded, Escalate(NBsExceeded, nBsWorst, options.NBsMilliseconds), ecu,
                $"{nBsCount} flow-control wait(s) longer than N_Bs {Ms(options.NBsMilliseconds)} ms; worst {Ms(nBsWorst)} ms.",
                nBsWorst, options.NBsMilliseconds, nBsTime, nBsFrames, transaction.Sequence));
        if (nCrCount > 0)
            findings.Add(new DiagnosticTimingFinding(NCrExceeded, Escalate(NCrExceeded, nCrWorst, options.NCrMilliseconds), ecu,
                $"{nCrCount} consecutive-frame gap(s) longer than N_Cr {Ms(options.NCrMilliseconds)} ms; worst {Ms(nCrWorst)} ms.",
                nCrWorst, options.NCrMilliseconds, nCrTime, nCrFrames, transaction.Sequence));
        if (stCount > 0)
            findings.Add(new DiagnosticTimingFinding(StMinViolated, StMinViolated.DefaultSeverity, ecu,
                $"{stCount} consecutive-frame spacing(s) below the requested STmin of {Ms(stLimit)} ms; worst shortfall {Ms(stWorst)} ms.",
                stLimit - stWorst, stLimit, stTime, stFrames, transaction.Sequence));
    }

    /// <summary>ISO 15765-2 STmin: 0x00..0x7F = ms, 0xF1..0xF9 = 100..900 µs, other values are reserved and read as 127 ms.</summary>
    public static double DecodeStMinMilliseconds(byte raw) => raw switch
    {
        <= 0x7F => raw,
        >= 0xF1 and <= 0xF9 => (raw - 0xF0) / 10d,
        _ => 127
    };

    private static IReadOnlyList<int> Join(IEnumerable<int> first, IEnumerable<int> second) =>
        first.Concat(second).Distinct().OrderBy(i => i).ToArray();
}
