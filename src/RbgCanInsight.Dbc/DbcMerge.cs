using System.Text.RegularExpressions;

namespace RbgCanInsight.Dbc;

public enum DbcMergeKind { AddMessage, AddSignal, ChangeMessage, ChangeSignal }

public enum DbcMergeDecision { TakeIncoming, KeepBase }

/// <summary>One mergeable difference between a base DBC and an incoming DBC version.</summary>
public sealed record DbcMergeItem(
    string Id,
    DbcMergeKind Kind,
    DbcMessageIdentity Identity,
    string MessageName,
    string Signal,
    string Summary,
    string BaseDefinition,
    string IncomingDefinition,
    DbcMergeDecision DefaultDecision)
{
    public string IdentifierText => $"0x{Identity.Identifier:X}";
    public string FrameFormat => Identity.IsExtended ? "29-bit" : "11-bit";
}

public sealed record DbcMergePlan(
    IReadOnlyList<DbcMergeItem> Items,
    IReadOnlyList<string> NotMergeable)
{
    /// <summary>Definitions present only in the base are never deleted by a merge.</summary>
    public int KeptBaseOnly { get; init; }
}

public sealed record DbcMergeResult(
    string Text,
    IReadOnlyList<string> Applied,
    IReadOnlyList<string> Issues,
    bool IsValid);

/// <summary>
/// Non-destructive text-level merge of an incoming DBC version into a base DBC. Statements that are
/// not part of a selected item (nodes, attributes, vendor statements, comments) are preserved
/// byte-for-byte. The merge never deletes, never edits the source files and is validated by re-parsing
/// the result and re-comparing it with the base: only selected identities may differ.
/// </summary>
public static class DbcMerge
{
    private enum StatementKind { Other, Bu, Message, Signal, MessageAssociation, SignalAssociation }

    private sealed class Statement
    {
        public List<string> Lines { get; init; } = [];
        public StatementKind Kind { get; set; } = StatementKind.Other;
        public uint RawId { get; set; }
        public string Signal { get; set; } = string.Empty;
        public uint OwnerRawId { get; set; }   // owning message for Signal statements
        public string Text => string.Join("\n", Lines);
    }

    private static readonly Regex BoRegex = new(@"^\s*BO_\s+(\d+)\s+(\S+?)\s*:", RegexOptions.Compiled);
    private static readonly Regex SgRegex = new(@"^\s*SG_\s+(\S+)", RegexOptions.Compiled);
    private static readonly Regex[] MessageAssociations =
    [
        new(@"^\s*CM_\s+BO_\s+(\d+)\b", RegexOptions.Compiled),
        new(@"^\s*BA_\s+""[^""]*""\s+BO_\s+(\d+)\b", RegexOptions.Compiled),
        new(@"^\s*BO_TX_BU_\s+(\d+)\b", RegexOptions.Compiled)
    ];
    private static readonly Regex[] SignalAssociations =
    [
        new(@"^\s*CM_\s+SG_\s+(\d+)\s+(\S+)", RegexOptions.Compiled),
        new(@"^\s*VAL_\s+(\d+)\s+(\S+)", RegexOptions.Compiled),
        new(@"^\s*BA_\s+""[^""]*""\s+SG_\s+(\d+)\s+(\S+)", RegexOptions.Compiled),
        new(@"^\s*SIG_VALTYPE_\s+(\d+)\s+(\S+)", RegexOptions.Compiled),
        new(@"^\s*SG_MUL_VAL_\s+(\d+)\s+(\S+)", RegexOptions.Compiled)
    ];

    private static uint RawId(DbcMessageIdentity identity) =>
        identity.IsExtended ? identity.Identifier | 0x8000_0000u : identity.Identifier;

    private static DbcDatabase Parse(string name, string text) =>
        new DbcParser().Parse(name, new StringReader(text));

    public static DbcMergePlan Plan(string baseText, string incomingText)
    {
        var baseline = Parse("base", baseText);
        var incoming = Parse("incoming", incomingText);
        var report = DbcDefinitionComparer.Compare(baseline, incoming);
        var items = new List<DbcMergeItem>();
        var notMergeable = new List<string>();
        var addedMessages = report.Rows
            .Where(row => row.Scope == "Message" && row.Change == DbcDefinitionChange.Added)
            .Select(row => new DbcMessageIdentity(row.Identifier, row.IsExtended)).ToHashSet();
        foreach (var row in report.Rows)
        {
            var identity = new DbcMessageIdentity(row.Identifier, row.IsExtended);
            var prefix = $"{(row.IsExtended ? "X" : "S")}{row.Identifier:X}";
            if (row.Change == DbcDefinitionChange.Ambiguous)
            {
                notMergeable.Add($"{row.Scope} {row.IdentifierText} {row.Signal}: {row.Differences}");
                continue;
            }
            if (row.Scope == "Message")
            {
                if (row.Change == DbcDefinitionChange.Added)
                    items.Add(new($"ADDMSG:{prefix}", DbcMergeKind.AddMessage, identity, row.CurrentMessage,
                        string.Empty, $"Add message {row.CurrentMessage} ({row.FrameFormat} {row.IdentifierText}) with its signals",
                        string.Empty, row.CurrentDefinition, DbcMergeDecision.TakeIncoming));
                else if (row.Change == DbcDefinitionChange.Changed)
                    items.Add(new($"CHGMSG:{prefix}", DbcMergeKind.ChangeMessage, identity, row.BaselineMessage,
                        string.Empty, $"Message {row.BaselineMessage}: {row.Differences}",
                        row.BaselineDefinition, row.CurrentDefinition, DbcMergeDecision.KeepBase));
                continue;
            }
            if (addedMessages.Contains(identity)) continue;    // covered by AddMessage
            if (row.Change == DbcDefinitionChange.Added)
                items.Add(new($"ADDSIG:{prefix}:{row.Signal}", DbcMergeKind.AddSignal, identity, row.CurrentMessage,
                    row.Signal, $"Add signal {row.Signal} to {row.CurrentMessage}",
                    string.Empty, row.CurrentDefinition, DbcMergeDecision.TakeIncoming));
            else if (row.Change == DbcDefinitionChange.Changed)
                items.Add(new($"CHGSIG:{prefix}:{row.Signal}", DbcMergeKind.ChangeSignal, identity, row.BaselineMessage,
                    row.Signal, $"Signal {row.Signal} in {row.BaselineMessage}: {row.Differences}",
                    row.BaselineDefinition, row.CurrentDefinition, DbcMergeDecision.KeepBase));
        }
        return new DbcMergePlan(items, notMergeable)
        {
            KeptBaseOnly = report.Rows.Count(row => row.Change == DbcDefinitionChange.Removed)
        };
    }

    public static DbcMergeResult Apply(
        string baseText, string incomingText, DbcMergePlan plan,
        IReadOnlyDictionary<string, DbcMergeDecision>? decisions = null)
    {
        var lineEnding = baseText.Contains("\r\n") ? "\r\n" : "\n";
        var baseStatements = Split(baseText);
        var incomingStatements = Split(incomingText);
        var applied = new List<string>();
        var issues = new List<string>();
        var accepted = new List<DbcMergeItem>();

        DbcMergeDecision Decide(DbcMergeItem item) =>
            decisions is not null && decisions.TryGetValue(item.Id, out var value) ? value : item.DefaultDecision;

        // Order: messages first, then signals, so signal additions land in the final message blocks.
        foreach (var item in plan.Items.Where(item => Decide(item) == DbcMergeDecision.TakeIncoming)
                     .OrderBy(item => item.Kind))
        {
            var raw = RawId(item.Identity);
            try
            {
                var done = item.Kind switch
                {
                    DbcMergeKind.AddMessage => AddMessage(baseStatements, incomingStatements, raw),
                    DbcMergeKind.AddSignal => AddSignal(baseStatements, incomingStatements, raw, item.Signal),
                    DbcMergeKind.ChangeMessage => ChangeMessage(baseStatements, incomingStatements, raw),
                    DbcMergeKind.ChangeSignal => ChangeSignal(baseStatements, incomingStatements, raw, item.Signal),
                    _ => false
                };
                if (done) { applied.Add(item.Summary); accepted.Add(item); }
                else issues.Add($"Not applied (definition not found in source text): {item.Summary}");
            }
            catch (InvalidOperationException exception)
            {
                issues.Add($"Not applied: {item.Summary} — {exception.Message}");
            }
        }

        EnsureNodes(baseStatements, incomingStatements, accepted, issues);
        var text = string.Join(lineEnding, baseStatements.SelectMany(statement => statement.Lines));
        var keepsTrailingNewline = baseText.EndsWith('\n');
        if (keepsTrailingNewline && !text.EndsWith('\n')) text += lineEnding;

        issues.AddRange(Validate(baseText, text, accepted));
        return new DbcMergeResult(text, applied, issues, issues.Count == 0);
    }

    // ---------------------------------------------------------------- files

    private const long MaximumInputBytes = 64L * 1024 * 1024;

    private static string ReadStrictUtf8(string path, out bool hasBom)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException("DBC file not found.", path);
        if (info.Length > MaximumInputBytes)
            throw new NotSupportedException("DBC exceeds the verified 64 MiB merge limit.");
        var bytes = File.ReadAllBytes(path);
        hasBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        try
        {
            return new System.Text.UTF8Encoding(false, true)
                .GetString(bytes, hasBom ? 3 : 0, bytes.Length - (hasBom ? 3 : 0));
        }
        catch (System.Text.DecoderFallbackException)
        {
            throw new NotSupportedException(
                "Only UTF-8 DBC files can be merged without risking altered characters; convert the file first.");
        }
    }

    public static DbcMergePlan PlanFiles(string basePath, string incomingPath) =>
        Plan(ReadStrictUtf8(basePath, out _), ReadStrictUtf8(incomingPath, out _));

    /// <summary>Writes the merged DBC to a NEW file. Neither source is ever modified, an existing
    /// destination is never overwritten, and a result that fails validation is not written.</summary>
    public static DbcMergeResult WriteMergedFile(
        string basePath, string incomingPath, string destinationPath,
        IReadOnlyDictionary<string, DbcMergeDecision>? decisions = null)
    {
        var baseFull = Path.GetFullPath(basePath);
        var incomingFull = Path.GetFullPath(incomingPath);
        var destination = Path.GetFullPath(destinationPath);
        if (string.Equals(destination, baseFull, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(destination, incomingFull, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The merged DBC must use a new filename; both source DBC files stay read-only.");
        if (!string.Equals(Path.GetExtension(destination), ".dbc", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("The merged file must have the .dbc extension.");
        if (File.Exists(destination))
            throw new IOException("The destination already exists. Select an unused filename to avoid overwriting.");

        var baseText = ReadStrictUtf8(baseFull, out var bom);
        var incomingText = ReadStrictUtf8(incomingFull, out _);
        var plan = Plan(baseText, incomingText);
        var result = Apply(baseText, incomingText, plan, decisions);
        if (!result.IsValid)
            throw new InvalidDataException("The merged DBC failed validation and was not written: " +
                string.Join(" | ", result.Issues));

        var generated = new System.Text.UTF8Encoding(bom).GetPreamble()
            .Concat(new System.Text.UTF8Encoding(false).GetBytes(result.Text)).ToArray();
        var temporary = destination + ".partial-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(generated);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, destination);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { /* keep the primary error; sources are never touched */ }
        }
        return result;
    }

    // ---------------------------------------------------------------- statements

    private static List<Statement> Split(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        if (lines.Length > 0 && lines[^1].Length == 0 && text.EndsWith('\n'))
            lines = lines[..^1];
        var result = new List<Statement>();
        uint currentMessage = 0;
        var index = 0;
        while (index < lines.Length)
        {
            var statement = new Statement { Lines = [lines[index]] };
            // A statement continues while a quoted string is still open (multi-line CM_ etc.).
            while (OpenQuote(string.Join("\n", statement.Lines)) && index + 1 < lines.Length)
            {
                index++;
                statement.Lines.Add(lines[index]);
            }
            index++;
            Classify(statement, ref currentMessage);
            result.Add(statement);
        }
        return result;
    }

    private static bool OpenQuote(string text)
    {
        var quotes = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\\') { i++; continue; }
            if (text[i] == '"') quotes++;
        }
        return quotes % 2 == 1;
    }

    private static void Classify(Statement statement, ref uint currentMessage)
    {
        var first = statement.Lines[0];
        var trimmed = first.TrimStart();
        if (BoRegex.Match(first) is { Success: true } bo && trimmed.StartsWith("BO_ ", StringComparison.Ordinal))
        {
            statement.Kind = StatementKind.Message;
            statement.RawId = uint.Parse(bo.Groups[1].Value);
            currentMessage = statement.RawId;
            return;
        }
        if (SgRegex.Match(first) is { Success: true } sg && trimmed.StartsWith("SG_ ", StringComparison.Ordinal))
        {
            statement.Kind = StatementKind.Signal;
            statement.Signal = sg.Groups[1].Value;
            statement.OwnerRawId = currentMessage;
            statement.RawId = currentMessage;
            return;
        }
        foreach (var regex in SignalAssociations)
            if (regex.Match(first) is { Success: true } match)
            {
                statement.Kind = StatementKind.SignalAssociation;
                statement.RawId = uint.Parse(match.Groups[1].Value);
                statement.Signal = match.Groups[2].Value;
                return;
            }
        foreach (var regex in MessageAssociations)
            if (regex.Match(first) is { Success: true } match)
            {
                statement.Kind = StatementKind.MessageAssociation;
                statement.RawId = uint.Parse(match.Groups[1].Value);
                return;
            }
        if (trimmed.StartsWith("BU_", StringComparison.Ordinal) && trimmed.Length > 3 &&
            (trimmed[3] == ':' || char.IsWhiteSpace(trimmed[3])))
            statement.Kind = StatementKind.Bu;
        if (trimmed.Length > 0 && !trimmed.StartsWith("SG_", StringComparison.Ordinal) &&
            statement.Kind == StatementKind.Other)
        {
            // Any other top-level keyword ends a message block for SG_ ownership purposes.
            if (!char.IsWhiteSpace(first[0])) currentMessage = 0;
        }
    }

    private static IEnumerable<Statement> MessageAssociationsOf(IEnumerable<Statement> all, uint raw) =>
        all.Where(s => s.Kind == StatementKind.MessageAssociation && s.RawId == raw);

    private static IEnumerable<Statement> SignalAssociationsOf(IEnumerable<Statement> all, uint raw, string signal) =>
        all.Where(s => s.Kind == StatementKind.SignalAssociation && s.RawId == raw &&
            string.Equals(s.Signal, signal, StringComparison.Ordinal));

    private static int MessageIndex(List<Statement> statements, uint raw) =>
        statements.FindIndex(s => s.Kind == StatementKind.Message && s.RawId == raw);

    private static int LastSignalIndexOfMessage(List<Statement> statements, uint raw)
    {
        var start = MessageIndex(statements, raw);
        if (start < 0) return -1;
        var last = start;
        for (var i = start + 1; i < statements.Count && statements[i].Kind == StatementKind.Signal; i++) last = i;
        return last;
    }

    private static Statement Copy(Statement source) => new()
    {
        Lines = [.. source.Lines], Kind = source.Kind, RawId = source.RawId,
        Signal = source.Signal, OwnerRawId = source.OwnerRawId
    };

    private static int AppendIndex(List<Statement> statements)
    {
        var last = statements.Count;
        while (last > 0 && string.IsNullOrWhiteSpace(statements[last - 1].Text)) last--;
        return last;
    }

    // ---------------------------------------------------------------- operations

    private static bool AddMessage(List<Statement> target, List<Statement> source, uint raw)
    {
        if (MessageIndex(target, raw) >= 0)
            throw new InvalidOperationException("the base already defines this message identity");
        var start = MessageIndex(source, raw);
        if (start < 0) return false;
        var block = new List<Statement> { Copy(source[start]) };
        for (var i = start + 1; i < source.Count && source[i].Kind == StatementKind.Signal; i++)
            block.Add(Copy(source[i]));
        // Place the new block right after the last existing message block (or before the first
        // association statement when the base has no messages yet).
        var lastMessage = target.FindLastIndex(s => s.Kind == StatementKind.Message);
        int insertAt;
        if (lastMessage >= 0)
            insertAt = LastSignalIndexOfMessage(target, target[lastMessage].RawId) + 1;
        else
        {
            var firstAssociation = target.FindIndex(s => s.Kind is StatementKind.MessageAssociation or StatementKind.SignalAssociation);
            insertAt = firstAssociation >= 0 ? firstAssociation : AppendIndex(target);
        }
        target.InsertRange(insertAt, block);
        var associations = MessageAssociationsOf(source, raw).Select(Copy).ToList();
        foreach (var signal in block.Where(s => s.Kind == StatementKind.Signal))
            associations.AddRange(SignalAssociationsOf(source, raw, signal.Signal).Select(Copy));
        target.InsertRange(AppendIndex(target), associations);
        return true;
    }

    private static bool AddSignal(List<Statement> target, List<Statement> source, uint raw, string signal)
    {
        var existing = target.FindIndex(s => s.Kind == StatementKind.Signal && s.OwnerRawId == raw &&
            s.Signal == signal);
        if (existing >= 0)
            throw new InvalidOperationException("the base message already defines this signal");
        var sourceIndex = source.FindIndex(s => s.Kind == StatementKind.Signal && s.OwnerRawId == raw && s.Signal == signal);
        var insertAfter = LastSignalIndexOfMessage(target, raw);
        if (sourceIndex < 0 || insertAfter < 0) return false;
        var copy = Copy(source[sourceIndex]);
        copy.OwnerRawId = raw;
        target.Insert(insertAfter + 1, copy);
        target.InsertRange(AppendIndex(target), SignalAssociationsOf(source, raw, signal).Select(Copy));
        return true;
    }

    private static bool ChangeSignal(List<Statement> target, List<Statement> source, uint raw, string signal)
    {
        var targetIndex = target.FindIndex(s => s.Kind == StatementKind.Signal && s.OwnerRawId == raw && s.Signal == signal);
        var sourceIndex = source.FindIndex(s => s.Kind == StatementKind.Signal && s.OwnerRawId == raw && s.Signal == signal);
        if (targetIndex < 0 || sourceIndex < 0) return false;
        var replacement = Copy(source[sourceIndex]);
        replacement.OwnerRawId = raw;
        target[targetIndex] = replacement;
        // The signal's value tables, comments and attributes come from the same (incoming) version.
        target.RemoveAll(s => s.Kind == StatementKind.SignalAssociation && s.RawId == raw && s.Signal == signal);
        target.InsertRange(AppendIndex(target), SignalAssociationsOf(source, raw, signal).Select(Copy));
        return true;
    }

    private static bool ChangeMessage(List<Statement> target, List<Statement> source, uint raw)
    {
        var targetIndex = MessageIndex(target, raw);
        var sourceIndex = MessageIndex(source, raw);
        if (targetIndex < 0 || sourceIndex < 0) return false;
        target[targetIndex] = Copy(source[sourceIndex]);     // header only: signals stay as in the base
        target.RemoveAll(s => s.Kind == StatementKind.MessageAssociation && s.RawId == raw);
        target.InsertRange(AppendIndex(target), MessageAssociationsOf(source, raw).Select(Copy));
        return true;
    }

    private static void EnsureNodes(
        List<Statement> target, List<Statement> source, List<DbcMergeItem> accepted, List<string> issues)
    {
        if (accepted.Count == 0) return;
        var bu = target.FindIndex(s => s.Kind == StatementKind.Bu);
        if (bu < 0) return;   // the base never declared its nodes: nothing to extend, and nothing is invented
        var known = Tokens(target[bu].Text).ToHashSet(StringComparer.Ordinal);
        var needed = new List<string>();
        foreach (var statement in target.Where(s => s.Kind is StatementKind.Message or StatementKind.Signal))
            foreach (var node in NodeNames(statement))
                if (!known.Contains(node) && !needed.Contains(node)) needed.Add(node);
        if (needed.Count == 0) return;
        var line = target[bu].Lines[0].TrimEnd();
        target[bu].Lines[0] = line + " " + string.Join(" ", needed);
    }

    private static IEnumerable<string> Tokens(string buLine) =>
        buLine.Replace("BU_", " ").Replace(":", " ")
            .Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);

    private static IEnumerable<string> NodeNames(Statement statement)
    {
        var line = statement.Lines[0];
        if (statement.Kind == StatementKind.Message)
        {
            var match = Regex.Match(line, @":\s*\d+\s+(\S+)\s*$");
            if (match.Success && match.Groups[1].Value != "Vector__XXX") yield return match.Groups[1].Value;
        }
        else if (statement.Kind == StatementKind.Signal)
        {
            var match = Regex.Match(line, @"\[[^\]]*\]\s*""[^""]*""\s*(.*)$");
            if (!match.Success) yield break;
            foreach (var node in match.Groups[1].Value.Split([',', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
                if (node != "Vector__XXX") yield return node;
        }
    }

    // ---------------------------------------------------------------- validation

    private static List<string> Validate(string baseText, string mergedText, List<DbcMergeItem> accepted)
    {
        var problems = new List<string>();
        DbcDatabase merged;
        try { merged = Parse("merged", mergedText); }
        catch (Exception exception) when (exception is FormatException or InvalidDataException or ArgumentException)
        {
            problems.Add("The merged DBC cannot be parsed: " + exception.Message);
            return problems;
        }
        var baseline = Parse("base", baseText);
        foreach (var warning in merged.ParseWarnings.Except(baseline.ParseWarnings))
            problems.Add("Merged DBC parse warning: " + warning);

        var allowed = accepted.Select(item => (item.Identity, item.Kind, item.Signal)).ToList();
        foreach (var row in DbcDefinitionComparer.Compare(baseline, merged).Rows)
        {
            if (row.Change == DbcDefinitionChange.Unchanged) continue;
            var identity = new DbcMessageIdentity(row.Identifier, row.IsExtended);
            var explained = row.Change switch
            {
                DbcDefinitionChange.Added when row.Scope == "Message" =>
                    allowed.Any(a => a.Identity == identity && a.Kind == DbcMergeKind.AddMessage),
                DbcDefinitionChange.Added =>
                    allowed.Any(a => a.Identity == identity &&
                        (a.Kind == DbcMergeKind.AddMessage || a.Kind == DbcMergeKind.AddSignal && a.Signal == row.Signal)),
                DbcDefinitionChange.Changed when row.Scope == "Message" =>
                    allowed.Any(a => a.Identity == identity && a.Kind == DbcMergeKind.ChangeMessage),
                DbcDefinitionChange.Changed =>
                    allowed.Any(a => a.Identity == identity && a.Kind == DbcMergeKind.ChangeSignal && a.Signal == row.Signal),
                _ => false
            };
            if (!explained)
                problems.Add($"Unexpected {row.Change} difference after merge: {row.Scope} {row.IdentifierText} {row.Signal}".TrimEnd());
        }
        return problems;
    }
}
