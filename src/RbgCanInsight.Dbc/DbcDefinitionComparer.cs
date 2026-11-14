using System.Globalization;

namespace RbgCanInsight.Dbc;

public enum DbcDefinitionChange
{
    Unchanged, Added, Removed, Changed, Ambiguous
}

public enum DbcDefinitionImpact
{
    None, Metadata, Decoding, Identity, Ambiguous
}

/// <summary>A source-definition comparison, NOT a comparison of CAN observations.
/// An equal parsed DBC does not prove opaque vendor metadata was preserved.</summary>
public sealed record DbcDefinitionDiffRow(
    string Scope,
    uint Identifier,
    bool IsExtended,
    string BaselineMessage,
    string CurrentMessage,
    string Signal,
    DbcDefinitionChange Change,
    DbcDefinitionImpact Impact,
    string Differences,
    string BaselineDefinition,
    string CurrentDefinition,
    bool RequiresReview)
{
    public string IdentifierText => $"0x{Identifier:X}";
    public string FrameFormat => IsExtended ? "29-bit" : "11-bit";
}

public sealed record DbcDefinitionDiffReport(
    IReadOnlyList<DbcDefinitionDiffRow> Rows,
    IReadOnlyList<string> BaselineWarnings,
    IReadOnlyList<string> CurrentWarnings)
{
    public int Added => Rows.Count(row => row.Change == DbcDefinitionChange.Added);
    public int Removed => Rows.Count(row => row.Change == DbcDefinitionChange.Removed);
    public int Changed => Rows.Count(row => row.Change == DbcDefinitionChange.Changed);
    public int Ambiguous => Rows.Count(row => row.Change == DbcDefinitionChange.Ambiguous);
    public int Unchanged => Rows.Count(row => row.Change == DbcDefinitionChange.Unchanged);
    public int RequiresReview => Rows.Count(row => row.RequiresReview);
    public string Summary => $"{Rows.Count:N0} definition rows: {Added:N0} added, " +
        $"{Removed:N0} removed, {Changed:N0} changed, {Ambiguous:N0} ambiguous, " +
        $"{RequiresReview:N0} to review";
}

/// <summary>
/// Compare two parsed DBC databases by exact (CAN ID, 11/29-bit frame format)
/// and then by signal name within the same message. Never align same-named
/// signals across CAN IDs, and never pick the first duplicated definition.
/// Unsupported/opaque source statements remain outside this comparison.
/// </summary>
public static class DbcDefinitionComparer
{
    public static DbcDefinitionDiffReport Compare(DbcDatabase baseline, DbcDatabase current)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(current);
        var previous = baseline.AllMessageDefinitions
            .GroupBy(message => new DbcMessageIdentity(message.Identifier, message.IsExtended))
            .ToDictionary(group => group.Key, group => group.ToArray());
        var latest = current.AllMessageDefinitions
            .GroupBy(message => new DbcMessageIdentity(message.Identifier, message.IsExtended))
            .ToDictionary(group => group.Key, group => group.ToArray());
        var keys = previous.Keys.Concat(latest.Keys).Distinct()
            .OrderBy(key => key.Identifier).ThenBy(key => key.IsExtended).ToArray();
        var rows = new List<DbcDefinitionDiffRow>();
        foreach (var key in keys)
        {
            previous.TryGetValue(key, out var oldGroup);
            latest.TryGetValue(key, out var newGroup);
            var oldCount = oldGroup?.Length ?? 0;
            var newCount = newGroup?.Length ?? 0;
            if (oldCount > 1 || newCount > 1)
            {
                rows.Add(new DbcDefinitionDiffRow("Message", key.Identifier, key.IsExtended,
                    oldCount == 1 ? oldGroup![0].Name : string.Empty,
                    newCount == 1 ? newGroup![0].Name : string.Empty,
                    string.Empty, DbcDefinitionChange.Ambiguous, DbcDefinitionImpact.Ambiguous,
                    $"Repeated frame identity: baseline {oldCount}, current {newCount}; no first-match guessing.",
                    string.Empty, string.Empty, true));
                continue;
            }
            var oldMessage = oldCount == 1 ? oldGroup![0] : null;
            var newMessage = newCount == 1 ? newGroup![0] : null;
            rows.Add(CompareMessage(key, oldMessage, newMessage));
            if (oldMessage is null || newMessage is null)
            {
                // A complete message add/remove must still enumerate its
                // constituent signal definitions: downstream change reviews
                // need to see every affected bit layout and VAL_ description.
                var source = oldMessage ?? newMessage!;
                foreach (var group in source.Signals
                             .GroupBy(signal => signal.Name, StringComparer.Ordinal)
                             .OrderBy(group => group.Key, StringComparer.Ordinal))
                {
                    if (group.Count() != 1)
                    {
                        rows.Add(new DbcDefinitionDiffRow(
                            "Signal", key.Identifier, key.IsExtended,
                            oldMessage?.Name ?? string.Empty,
                            newMessage?.Name ?? string.Empty,
                            group.Key, DbcDefinitionChange.Ambiguous,
                            DbcDefinitionImpact.Ambiguous,
                            "Repeated signal name; cannot identify each removed/added definition.",
                            string.Empty, string.Empty, true));
                        continue;
                    }
                    var signal = group.Single();
                    var added = oldMessage is null;
                    rows.Add(new DbcDefinitionDiffRow(
                        "Signal", key.Identifier, key.IsExtended,
                        oldMessage?.Name ?? string.Empty,
                        newMessage?.Name ?? string.Empty,
                        signal.Name,
                        added ? DbcDefinitionChange.Added : DbcDefinitionChange.Removed,
                        DbcDefinitionImpact.Identity,
                        added ? "Containing CAN message added" : "Containing CAN message removed",
                        added ? string.Empty : SignalText(signal),
                        added ? SignalText(signal) : string.Empty, true));
                }
                continue;
            }

            var oldSignals = oldMessage.Signals.GroupBy(signal => signal.Name, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
            var newSignals = newMessage.Signals.GroupBy(signal => signal.Name, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
            foreach (var name in oldSignals.Keys.Concat(newSignals.Keys).Distinct(StringComparer.Ordinal)
                         .OrderBy(value => value, StringComparer.Ordinal))
            {
                oldSignals.TryGetValue(name, out var oldOptions);
                newSignals.TryGetValue(name, out var newOptions);
                if ((oldOptions?.Length ?? 0) > 1 || (newOptions?.Length ?? 0) > 1)
                {
                    rows.Add(new DbcDefinitionDiffRow("Signal", key.Identifier, key.IsExtended,
                        oldMessage.Name, newMessage.Name, name,
                        DbcDefinitionChange.Ambiguous, DbcDefinitionImpact.Ambiguous,
                        "Repeated signal name in the same message; compare manually.",
                        string.Empty, string.Empty, true));
                    continue;
                }
                rows.Add(CompareSignal(key, oldMessage, newMessage, name,
                    oldOptions?.FirstOrDefault(), newOptions?.FirstOrDefault()));
            }
        }
        return new DbcDefinitionDiffReport(rows, baseline.ParseWarnings.ToArray(), current.ParseWarnings.ToArray());
    }

    private static DbcDefinitionDiffRow CompareMessage(
        DbcMessageIdentity id, DbcMessage? before, DbcMessage? after)
    {
        var fields = new List<string>();
        if (before is not null && after is not null)
        {
            Changed(fields, "Message name", before.Name, after.Name);
            Changed(fields, "DLC", before.Length, after.Length);
            Changed(fields, "Transmitter", before.Transmitter, after.Transmitter);
            Changed(fields, "Cycle time", before.NominalCycleTimeMilliseconds, after.NominalCycleTimeMilliseconds);
            Changed(fields, "Comment", before.Comment, after.Comment);
            Changed(fields, "Attributes", Attributes(before.SourceAttributes), Attributes(after.SourceAttributes));
        }
        var kind = Classify(before, after, fields);
        var impact = kind switch
        {
            DbcDefinitionChange.Added or DbcDefinitionChange.Removed => DbcDefinitionImpact.Identity,
            DbcDefinitionChange.Changed when before?.Length != after?.Length => DbcDefinitionImpact.Decoding,
            DbcDefinitionChange.Changed => DbcDefinitionImpact.Metadata,
            _ => DbcDefinitionImpact.None
        };
        return new DbcDefinitionDiffRow("Message", id.Identifier, id.IsExtended,
            before?.Name ?? string.Empty, after?.Name ?? string.Empty, string.Empty,
            kind, impact, Notes(kind, fields),
            MessageText(before), MessageText(after), kind != DbcDefinitionChange.Unchanged);
    }

    private static DbcDefinitionDiffRow CompareSignal(
        DbcMessageIdentity id, DbcMessage oldMessage, DbcMessage newMessage,
        string name, DbcSignal? before, DbcSignal? after)
    {
        var changed = new List<string>();
        if (before is not null && after is not null)
        {
            Changed(changed, "Start bit", before.StartBit, after.StartBit);
            Changed(changed, "Bit length", before.BitLength, after.BitLength);
            Changed(changed, "Byte order", before.ByteOrder, after.ByteOrder);
            Changed(changed, "Signedness", before.IsSigned, after.IsSigned);
            Changed(changed, "Value type", before.ValueType, after.ValueType);
            Changed(changed, "Factor", before.Factor, after.Factor);
            Changed(changed, "Offset", before.Offset, after.Offset);
            Changed(changed, "Minimum", before.Minimum, after.Minimum);
            Changed(changed, "Maximum", before.Maximum, after.Maximum);
            Changed(changed, "Unit", before.Unit, after.Unit);
            Changed(changed, "Receiver", before.Receiver, after.Receiver);
            Changed(changed, "Multiplexor", before.IsMultiplexor, after.IsMultiplexor);
            Changed(changed, "Mux value", before.MultiplexValue, after.MultiplexValue);
            Changed(changed, "Mux conditions", Conditions(before), Conditions(after));
            Changed(changed, "Value meanings", Values(before), Values(after));
            Changed(changed, "Comment", before.Comment, after.Comment);
            Changed(changed, "Attributes", Attributes(before.SourceAttributes), Attributes(after.SourceAttributes));
        }
        var kind = Classify(before, after, changed);
        var important = new HashSet<string>(StringComparer.Ordinal)
        {
            "Start bit", "Bit length", "Byte order", "Signedness", "Value type",
            "Factor", "Offset", "Minimum", "Maximum", "Unit",
            "Multiplexor", "Mux value", "Mux conditions", "Value meanings"
        };
        var impact = kind switch
        {
            DbcDefinitionChange.Added or DbcDefinitionChange.Removed => DbcDefinitionImpact.Identity,
            DbcDefinitionChange.Changed when changed.Any(important.Contains) => DbcDefinitionImpact.Decoding,
            DbcDefinitionChange.Changed => DbcDefinitionImpact.Metadata,
            _ => DbcDefinitionImpact.None
        };
        return new DbcDefinitionDiffRow("Signal", id.Identifier, id.IsExtended,
            oldMessage.Name, newMessage.Name, name, kind, impact, Notes(kind, changed),
            SignalText(before), SignalText(after), kind != DbcDefinitionChange.Unchanged);
    }

    private static DbcDefinitionChange Classify<T>(T? a, T? b, List<string> changed)
        where T : class =>
        a is null ? DbcDefinitionChange.Added :
        b is null ? DbcDefinitionChange.Removed :
        changed.Count == 0 ? DbcDefinitionChange.Unchanged : DbcDefinitionChange.Changed;

    private static string Notes(DbcDefinitionChange kind, List<string> changed) => kind switch
    {
        DbcDefinitionChange.Added => "Not present in baseline",
        DbcDefinitionChange.Removed => "Not present in current",
        DbcDefinitionChange.Unchanged => "Same parsed definition",
        _ => string.Join("; ", changed)
    };

    private static void Changed<T>(List<string> changed, string label, T before, T after)
    {
        if (!EqualityComparer<T>.Default.Equals(before, after))
            changed.Add(label);
    }

    private static string Attributes(IReadOnlyList<DbcSourceAttribute> attrs) =>
        string.Join("|", attrs.Select(x => (x.Name, x.RawValue))
            .OrderBy(x => x.Name, StringComparer.Ordinal)
            .ThenBy(x => x.RawValue, StringComparer.Ordinal)
            .Select(x => $"{x.Name.Length}:{x.Name}{x.RawValue.Length}:{x.RawValue}"));

    private static string Conditions(DbcSignal signal) =>
        signal.MultiplexConditions is null ? string.Empty :
        string.Join("|", signal.MultiplexConditions.Select(condition =>
            condition.Multiplexor + ":" + string.Join(",", condition.Ranges
                .Select(r => (r.Minimum, r.Maximum)).OrderBy(r => r.Minimum)
                .ThenBy(r => r.Maximum).Select(r => $"[{r.Minimum},{r.Maximum}]")))
            .OrderBy(value => value, StringComparer.Ordinal));

    private static string Values(DbcSignal signal) =>
        signal.ValueDescriptions is null ? string.Empty :
        string.Join("|", signal.ValueDescriptions.OrderBy(kv => kv.Key)
            .Select(kv => $"{kv.Key}:{kv.Value.Length}:{kv.Value}"));

    private static string MessageText(DbcMessage? message) => message is null ? string.Empty :
        $"{message.Name}; DLC={message.Length}; TX={message.Transmitter}; cycle={message.NominalCycleTimeMilliseconds?.ToString(CultureInfo.InvariantCulture) ?? "?"}; " +
        $"attrs={Attributes(message.SourceAttributes)}; note={message.Comment}";

    private static string SignalText(DbcSignal? signal) => signal is null ? string.Empty :
        $"start={signal.StartBit}; bits={signal.BitLength}; endian={signal.ByteOrder}; signed={signal.IsSigned}; " +
        $"type={signal.ValueType}; factor={Fmt(signal.Factor)}; offset={Fmt(signal.Offset)}; " +
        $"min={Fmt(signal.Minimum)}; max={Fmt(signal.Maximum)}; unit={signal.Unit}; RX={signal.Receiver}; " +
        $"mux={signal.IsMultiplexor}/{signal.MultiplexValue?.ToString(CultureInfo.InvariantCulture) ?? "none"}/{Conditions(signal)}; " +
        $"VAL_={Values(signal)}; attrs={Attributes(signal.SourceAttributes)}; note={signal.Comment}";

    private static string Fmt(double number) => number.ToString("R", CultureInfo.InvariantCulture);
}
