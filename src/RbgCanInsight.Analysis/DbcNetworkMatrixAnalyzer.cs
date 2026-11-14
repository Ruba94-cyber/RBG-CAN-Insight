using RbgCanInsight.Dbc;

namespace RbgCanInsight.Analysis;

/// <summary>
/// One DBC message definition, optionally accompanied by one uniquely
/// attributable physical CAN channel. TX/RX always come from the database:
/// observed CAN frames do not prove the identity of the transmitting ECU.
/// </summary>
public sealed record DbcNetworkMatrixRow(
    string Database,
    uint Identifier,
    bool IsExtended,
    string Message,
    string DeclaredTransmitter,
    string DeclaredReceivers,
    int DeclaredDlc,
    int SignalCount,
    int? ObservedChannel,
    long ObservedFrames,
    double? ObservedAveragePeriodMs,
    string Evidence,
    uint? PossibleJ1939Pgn)
{
    public string IdentifierText => (IsExtended ? "29-bit 0x" : "11-bit 0x") +
        Identifier.ToString(IsExtended ? "X8" : "X3");
    public string ChannelText => ObservedChannel is int channel
        ? $"CAN {channel + 1}" : "Not observed";
    public string PeriodText => ObservedAveragePeriodMs is double period
        ? period.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)
        : "—";
    public string PgnText => PossibleJ1939Pgn is uint pgn
        ? $"0x{pgn:X5} (possible J1939)" : "—";
}

public sealed record DbcNetworkMatrixIssue(
    int Channel,
    uint Identifier,
    bool IsExtended,
    long RecordedFrames,
    string Code,
    string Explanation)
{
    /// <summary>Same wording as the observed-channel column of the network matrix (1-based).</summary>
    public string ChannelText => $"CAN {Channel + 1}";

    /// <summary>Readable form of <see cref="Code"/>; the raw code stays available for exports.</summary>
    public string CodeText => Code switch
    {
        "NO_ACTIVE_DBC" => "No active DBC describes this ID",
        "AMBIGUOUS_ACTIVE_DBC" => "Several DBCs claim this ID",
        "OBSERVED_DLC_EXCEEDS_DEFINITION" => "Payload longer than the DBC length",
        _ => Code.Replace('_', ' ').ToLowerInvariant()
    };

    public string IdentifierText => (IsExtended ? "29-bit 0x" : "11-bit 0x") +
        Identifier.ToString(IsExtended ? "X8" : "X3");
}

public sealed record DbcNetworkMatrixReport(
    IReadOnlyList<DbcNetworkMatrixRow> Rows,
    IReadOnlyList<DbcNetworkMatrixIssue> Issues,
    int DefinedMessages,
    long UniquelyMappedFrames,
    long UnattributedFrames,
    long TotalIssueCount = 0)
{
    public long OmittedIssues => Math.Max(0, TotalIssueCount - Issues.Count);
    public string Summary =>
        $"{DefinedMessages:N0} DBC definitions · {Rows.Count(row => row.ObservedFrames > 0):N0} observed definition/channel rows · " +
        $"{UniquelyMappedFrames:N0} uniquely resolved frames · {UnattributedFrames:N0} unassigned/ambiguous frames" +
        (OmittedIssues > 0 ? $" · {OmittedIssues:N0} detail issues omitted from bounded display" : "");
}

/// <summary>
/// Conservative source-vs-observation network matrix. Uses the same active DBC
/// profile/channel/PGN rules as offline decoding, but refuses to attribute a
/// frame to any database when multiple active profiles claim it.
/// </summary>
public static class DbcNetworkMatrixAnalyzer
{
    private const int MaxRetainedIssues = 25000;

    public static DbcNetworkMatrixReport Analyze(
        IReadOnlyList<DbcDecoder> decoders,
        IReadOnlyList<MessageStatisticsRow> statistics)
    {
        ArgumentNullException.ThrowIfNull(decoders);
        ArgumentNullException.ThrowIfNull(statistics);

        var matched = new Dictionary<(DbcDecoder Decoder, DbcMessage Message),
            List<(MessageStatisticsRow Row, bool Exact)>>();
        var issues = new List<DbcNetworkMatrixIssue>();
        long issueCount = 0;
        void RecordIssue(DbcNetworkMatrixIssue issue)
        {
            issueCount++;
            if (issues.Count < MaxRetainedIssues) issues.Add(issue);
        }
        long uniquelyMappedFrames = 0, unattributedFrames = 0;

        foreach (var row in statistics)
        {
            var candidates = new List<(DbcDecoder Decoder, DbcMessage Message, bool Exact)>();
            foreach (var decoder in decoders)
            {
                if (!decoder.TryGetMessage(row.Channel, row.Identifier, row.IsExtended,
                    out var message))
                    continue;
                candidates.Add((decoder, message,
                    message.Identifier == row.Identifier && message.IsExtended == row.IsExtended));
            }
            if (candidates.Count != 1)
            {
                unattributedFrames += row.FrameCount;
                RecordIssue(new DbcNetworkMatrixIssue(row.Channel, row.Identifier,
                    row.IsExtended, row.FrameCount,
                    candidates.Count == 0 ? "NO_ACTIVE_DBC" : "AMBIGUOUS_ACTIVE_DBC",
                    candidates.Count == 0
                        ? "No active DBC profile uniquely describes this recorded CAN identity."
                        : $"{candidates.Count} active DBC profiles claim this same physical CAN identity; no ECU/database ownership is inferred."));
                continue;
            }

            var candidate = candidates[0];
            var key = (candidate.Decoder, candidate.Message);
            if (!matched.TryGetValue(key, out var rows))
                matched[key] = rows = [];
            rows.Add((row, candidate.Exact));
            uniquelyMappedFrames += row.FrameCount;

            if (row.MaxLength > candidate.Message.Length)
                RecordIssue(new DbcNetworkMatrixIssue(row.Channel, row.Identifier, row.IsExtended,
                    row.FrameCount, "OBSERVED_DLC_EXCEEDS_DEFINITION",
                    $"Observed payload length {row.MaxLength} bytes exceeds DBC length {candidate.Message.Length}; verify matching database and CAN FD metadata."));
        }

        var results = new List<DbcNetworkMatrixRow>();
        var definitionCount = 0;
        foreach (var decoder in decoders)
        {
            foreach (var message in decoder.Database.AllMessageDefinitions)
            {
                definitionCount++;
                var declaredReceivers = DbcNodeView.DeclaredReceivers(message);
                var receivers = declaredReceivers.Count == 0
                    ? "Not declared" : string.Join(", ", declaredReceivers);
                var transmitter = string.IsNullOrWhiteSpace(message.Transmitter) ||
                    message.Transmitter.Equals("Vector__XXX", StringComparison.OrdinalIgnoreCase)
                    ? "Not declared" : message.Transmitter;
                var potentialPgn = message.IsExtended && message.Identifier <= 0x1FFF_FFFFu
                    ? J1939.ExtractPgn(message.Identifier) : (uint?)null;

                if (!matched.TryGetValue((decoder, message), out var observations) ||
                    observations.Count == 0)
                {
                    results.Add(new DbcNetworkMatrixRow(decoder.Database.Name,
                        message.Identifier, message.IsExtended, message.Name, transmitter,
                        receivers, message.Length, message.Signals.Count, null, 0, null,
                        "Definition only; not observed or not uniquely attributable from loaded trace.",
                        potentialPgn));
                    continue;
                }

                foreach (var item in observations.OrderBy(item => item.Row.Channel))
                {
                    results.Add(new DbcNetworkMatrixRow(decoder.Database.Name,
                        message.Identifier, message.IsExtended, message.Name, transmitter,
                        receivers, message.Length, message.Signals.Count,
                        item.Row.Channel, item.Row.FrameCount,
                        item.Row.FrameCount > 1 ? item.Row.AveragePeriodMilliseconds : null,
                        item.Exact
                            ? "Exact CAN ID/format; database assignment is a configured decoding profile, not ECU proof."
                            : "Possible J1939 PGN match; sender/source address and ECU identity unverified.",
                        potentialPgn));
                }
            }
        }

        return new DbcNetworkMatrixReport(
            results.OrderBy(row => row.Database, StringComparer.OrdinalIgnoreCase)
                .ThenBy(row => row.Identifier).ThenBy(row => row.IsExtended)
                .ThenBy(row => row.ObservedChannel).ToArray(),
            issues.OrderBy(row => row.Channel).ThenBy(row => row.Identifier)
                .ThenBy(row => row.IsExtended).ToArray(),
            definitionCount, uniquelyMappedFrames, unattributedFrames, issueCount);
    }
}
