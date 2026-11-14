using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace RbgCanInsight.Dbc;

/// <summary>A named, documented consistency rule (CANdb++ "consistency check" equivalent).</summary>
public sealed record DbcConsistencyRule(
    string Id, string Title, DbcEngineeringSeverity DefaultSeverity, string Category,
    string Description, bool NeedsSourceText = false, bool NeedsObservation = false);

/// <summary>One violation of a rule. <see cref="Subject"/> is the message (or node/attribute) concerned.</summary>
public sealed record DbcConsistencyFinding(
    DbcConsistencyRule Rule, DbcEngineeringSeverity Severity, string Subject, string Signal, string Detail)
{
    public string RuleId => Rule.Id;
}

public sealed record DbcConsistencyReport(
    string DatabaseName,
    IReadOnlyList<DbcConsistencyFinding> Findings,
    IReadOnlyList<DbcConsistencyRule> RulesEvaluated,
    IReadOnlyList<DbcConsistencyRule> RulesSkipped,
    bool Truncated)
{
    public int Count(DbcEngineeringSeverity severity) => Findings.Count(f => f.Severity == severity);

    public string ToSummaryText()
    {
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture,
            $"DBC consistency check of '{DatabaseName}': {Count(DbcEngineeringSeverity.Error)} error(s), " +
            $"{Count(DbcEngineeringSeverity.Warning)} warning(s), {Count(DbcEngineeringSeverity.Review)} review item(s); " +
            $"{RulesEvaluated.Count} rule(s) evaluated");
        if (RulesSkipped.Count > 0)
            sb.Append(CultureInfo.InvariantCulture,
                $", {RulesSkipped.Count} skipped ({string.Join(", ", RulesSkipped.Select(r => r.Id))}: need the DBC source text or an observed trace)");
        if (Truncated) sb.Append("; output truncated");
        sb.AppendLine(".");
        foreach (var f in Findings)
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"[{f.Severity}] {f.Rule.Id} {f.Rule.Title} · {f.Subject}{(f.Signal.Length > 0 ? "." + f.Signal : "")} · {f.Detail}");
        return sb.ToString();
    }

    /// <summary>RFC 4180 CSV: rule, severity, category, subject, signal, detail.</summary>
    public string ToCsv()
    {
        var sb = new StringBuilder("Rule,Title,Severity,Category,Subject,Signal,Detail\r\n");
        foreach (var f in Findings)
            sb.Append(string.Join(',', new[] { f.Rule.Id, f.Rule.Title, f.Severity.ToString(), f.Rule.Category, f.Subject, f.Signal, f.Detail }.Select(Csv)))
              .Append("\r\n");
        return sb.ToString();

        static string Csv(string v) => v.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
    }
}

/// <summary>Optional inputs beyond the parsed database.</summary>
public sealed record DbcConsistencyOptions
{
    /// <summary>Original DBC text; enables rules that need statements the model does not keep (BU_, BA_DEF_, VAL_ ...).</summary>
    public string? SourceText { get; init; }

    /// <summary>Observed average cycle time per message in a trace (ms); enables DBC-051.</summary>
    public IReadOnlyDictionary<DbcMessageIdentity, double>? ObservedCycleMilliseconds { get; init; }

    /// <summary>Relative deviation of observed vs. declared cycle time that is reported (default 0.25).</summary>
    public double CycleTolerance { get; init; } = 0.25;

    public int MaxFindings { get; init; } = 2000;
}

/// <summary>
/// Rule-based DBC consistency checker. Every rule has a stable ID, a severity and a documented
/// meaning (see <see cref="Rules"/>). Rules that need statements the parsed model does not retain
/// (node list, attribute definitions, orphan VAL_ lines) run only when
/// <see cref="DbcConsistencyOptions.SourceText"/> is supplied and are reported as skipped otherwise.
/// Definition-only: nothing here claims anything about ECU behaviour. It was not compared against
/// Vector CANdb++'s own checker, so rule parity is by documented intent, not by identical output.
/// </summary>
public static partial class DbcConsistencyChecker
{
    private static DbcConsistencyRule R(string id, string title, DbcEngineeringSeverity sev, string cat, string desc,
        bool text = false, bool obs = false) => new(id, title, sev, cat, desc, text, obs);

    private const DbcEngineeringSeverity E = DbcEngineeringSeverity.Error;
    private const DbcEngineeringSeverity W = DbcEngineeringSeverity.Warning;
    private const DbcEngineeringSeverity V = DbcEngineeringSeverity.Review;

    public static readonly IReadOnlyList<DbcConsistencyRule> Rules =
    [
        R("DBC-001", "Duplicate message ID", E, "Messages", "Two BO_ definitions use the same identifier and frame format.", text: true),
        R("DBC-002", "Duplicate message name", E, "Messages", "Two messages share a name; tools address messages by name."),
        R("DBC-003", "Standard/extended ID collision", V, "Messages", "An 11-bit and a 29-bit message share the same numeric ID; they are different frames but easy to confuse."),
        R("DBC-004", "Identifier outside arbitration range", E, "Messages", "ID exceeds 11 bits (standard) or 29 bits (extended)."),
        R("DBC-005", "Invalid payload length", E, "Messages", "DLC outside 0..64 bytes; above 8 bytes requires CAN FD."),
        R("DBC-010", "Duplicate signal name in message", E, "Signals", "Signal names must be unique inside a message."),
        R("DBC-011", "Signal bit overlap", W, "Signals", "Two signals use the same payload bit without being provably mutually exclusive multiplexed signals."),
        R("DBC-012", "Signal beyond message length", E, "Signals", "Signal start/length place bits outside the message's DLC, or width is not 1..64."),
        R("DBC-013", "Scaling inconsistent with bit width", W, "Signals", "Declared physical minimum/maximum lies outside what the raw width, factor and offset can represent."),
        R("DBC-014", "Invalid scaling", E, "Signals", "Non-finite or zero factor, min greater than max, float encoding with wrong width."),
        R("DBC-015", "Unit missing on scaled signal", V, "Signals", "Factor/offset is not identity but no unit is declared."),
        R("DBC-020", "Value description out of range", W, "Value tables", "A VAL_ raw value cannot be represented by the signal width/signedness."),
        R("DBC-021", "Enumeration-like signal without value table", V, "Value tables", "Narrow (2..4 bit) identity-scaled signal without unit and without VAL_ entries; likely a state needing named values."),
        R("DBC-022", "VAL_ for unknown message or signal", E, "Value tables", "A VAL_ statement references a message or signal that does not exist.", text: true),
        R("DBC-030", "Sender not declared in BU_", W, "Nodes", "Message transmitter is not in the BU_ node list.", text: true),
        R("DBC-031", "Receiver not declared in BU_", W, "Nodes", "A signal receiver is not in the BU_ node list.", text: true),
        R("DBC-032", "Unused node", V, "Nodes", "A BU_ node neither sends a message nor receives a signal.", text: true),
        R("DBC-033", "Message without transmitter", V, "Nodes", "Transmitter is Vector__XXX/empty; the sender is unknown.", text: true),
        R("DBC-040", "Attribute used but not defined", E, "Attributes", "A BA_ or BA_DEF_DEF_ names an attribute with no BA_DEF_.", text: true),
        R("DBC-041", "Attribute default out of range", E, "Attributes", "BA_DEF_DEF_ default violates the attribute's INT/HEX/FLOAT range or ENUM list.", text: true),
        R("DBC-042", "Attribute value out of range", E, "Attributes", "A BA_ value violates the attribute definition (range, enum index, type).", text: true),
        R("DBC-043", "Attribute without default", V, "Attributes", "A BA_DEF_ has no BA_DEF_DEF_ default; objects without a BA_ value are undefined.", text: true),
        R("DBC-044", "Attribute applied to wrong object class", E, "Attributes", "BA_DEF_ scope (network/BU_/BO_/SG_) differs from where BA_ applies it.", text: true),
        R("DBC-045", "Attribute on unknown object", E, "Attributes", "BA_ references a message, signal or node that does not exist.", text: true),
        R("DBC-050", "Invalid cycle time", W, "Timing", "GenMsgCycleTime is zero/negative."),
        R("DBC-051", "Cycle time differs from observed", W, "Timing", "Declared cycle time deviates from the average observed in a trace.", obs: true),
        R("DBC-060", "Multiplex configuration problem", V, "Multiplexing", "Root selector, branch or extended SG_MUL_VAL_ controller/range is missing, ambiguous or out of range."),
        R("DBC-061", "Multiplexor without multiplexed signals", V, "Multiplexing", "A signal is marked M but no signal depends on it."),
        R("DBC-062", "Redundant extended multiplex ranges", V, "Multiplexing", "Overlapping or duplicate SG_MUL_VAL_ ranges for one signal and controller."),
        R("DBC-070", "J1939 frame format mismatch", E, "J1939", "A message flagged J1939 (VFrameFormat) is not a 29-bit message."),
        R("DBC-071", "J1939 PDU1 with destination in ID", V, "J1939", "PF < 240 message carries a fixed destination address byte; PGN matching must ignore it."),
        R("DBC-072", "J1939 payload above 8 bytes", V, "J1939", "A J1939 message longer than 8 bytes is a multi-packet (TP/ETP) PGN and cannot appear as one CAN frame."),
        R("DBC-073", "Duplicate PGN and source address", E, "J1939", "Two J1939 messages have the same PGN and source address."),
        R("DBC-074", "Standard-ID message in J1939 database", V, "J1939", "Database contains J1939 messages, but this message uses an 11-bit ID.")
    ];

    private static readonly Dictionary<string, DbcConsistencyRule> ById = Rules.ToDictionary(r => r.Id);

    /// <summary>Maps <see cref="DbcEngineeringValidator"/> codes to rule IDs.</summary>
    private static readonly Dictionary<string, string> ValidatorCodeToRule = new()
    {
        ["CAN_ID_RANGE"] = "DBC-004", ["DLC_RANGE"] = "DBC-005", ["CAN_FD_REQUIRED"] = "DBC-005",
        ["DUPLICATE_SIGNAL"] = "DBC-010", ["BIT_OVERLAP"] = "DBC-011", ["MUX_OVERLAP_REVIEW"] = "DBC-011",
        ["OUTSIDE_DLC"] = "DBC-012", ["BIT_LENGTH"] = "DBC-012",
        ["FLOAT_WIDTH"] = "DBC-014", ["NONFINITE_SCALE"] = "DBC-014", ["ZERO_FACTOR"] = "DBC-014", ["PHYSICAL_RANGE"] = "DBC-014",
        ["VAL_OUT_OF_RANGE"] = "DBC-020"
    };

    public static DbcConsistencyReport Check(DbcDatabase database, DbcConsistencyOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(database);
        options ??= new DbcConsistencyOptions();
        var findings = new List<DbcConsistencyFinding>();
        var truncated = false;
        void Add(string ruleId, string subject, string signal, string detail, DbcEngineeringSeverity? severity = null)
        {
            if (findings.Count >= options.MaxFindings) { truncated = true; return; }
            var rule = ById[ruleId];
            findings.Add(new DbcConsistencyFinding(rule, severity ?? rule.DefaultSeverity, subject, signal, detail));
        }

        var messages = database.AllMessageDefinitions.ToArray();
        var hasText = !string.IsNullOrWhiteSpace(options.SourceText);
        var source = hasText ? SourceModel.Parse(options.SourceText!) : null;

        // Messages
        foreach (var group in messages.GroupBy(m => m.Name, StringComparer.Ordinal).Where(g => g.Count() > 1))
            Add("DBC-002", group.Key, "", $"Name used by {group.Count()} messages (IDs {string.Join(", ", group.Select(m => FormatId(m)))}).");
        foreach (var group in messages.GroupBy(m => m.Identifier).Where(g => g.Select(m => m.IsExtended).Distinct().Count() > 1))
            Add("DBC-003", string.Join(" / ", group.Select(m => m.Name)), "",
                $"Numeric ID 0x{group.Key:X} exists as both 11-bit and 29-bit message.");

        foreach (var message in messages)
        {
            foreach (var f in DbcEngineeringValidator.Validate(message))
            {
                if (ValidatorCodeToRule.TryGetValue(f.Code, out var ruleId))
                    Add(ruleId, message.Name, f.Signal == "(message)" ? "" : f.Signal, f.Detail, f.Severity);
                else if (f.Code.StartsWith("MUX_", StringComparison.Ordinal) || f.Code.StartsWith("SOURCE_", StringComparison.Ordinal))
                    Add("DBC-060", message.Name, f.Signal, $"{f.Code}: {f.Detail}", f.Severity);
            }
            CheckScaling(message, Add);
            CheckMultiplexing(message, Add);
            if (message.NominalCycleTimeMilliseconds is <= 0)
                Add("DBC-050", message.Name, "", $"GenMsgCycleTime is {message.NominalCycleTimeMilliseconds}.");
            if (options.ObservedCycleMilliseconds is { } observed && message.NominalCycleTimeMilliseconds is > 0 and var nominal &&
                observed.TryGetValue(new DbcMessageIdentity(message.Identifier, message.IsExtended), out var seen) && seen > 0 &&
                Math.Abs(seen - nominal) / nominal > options.CycleTolerance)
                Add("DBC-051", message.Name, "", string.Create(CultureInfo.InvariantCulture,
                    $"Declared {nominal} ms, observed average {seen:0.###} ms (tolerance {options.CycleTolerance:P0})."));
        }

        CheckJ1939(messages, Add);

        if (source is not null)
            CheckSource(database, messages, source, Add);

        var skipped = Rules.Where(r => (r.NeedsSourceText && !hasText) ||
                                       (r.NeedsObservation && options.ObservedCycleMilliseconds is null)).ToArray();
        var evaluated = Rules.Except(skipped).ToArray();
        var ordered = findings
            .OrderBy(f => f.Severity).ThenBy(f => f.Rule.Id, StringComparer.Ordinal)
            .ThenBy(f => f.Subject, StringComparer.Ordinal).ThenBy(f => f.Signal, StringComparer.Ordinal)
            .ToArray();
        return new DbcConsistencyReport(database.Name, ordered, evaluated, skipped, truncated);
    }

    /// <summary>Parses DBC text and checks it with the source-text rules enabled.</summary>
    public static DbcConsistencyReport CheckText(string name, string dbcText, DbcConsistencyOptions? options = null)
    {
        var database = new DbcParser().Parse(name, new StringReader(dbcText));
        return Check(database, (options ?? new DbcConsistencyOptions()) with { SourceText = dbcText });
    }

    private static string FormatId(DbcMessage m) => m.IsExtended ? $"0x{m.Identifier:X8}x" : $"0x{m.Identifier:X3}";

    private static void CheckScaling(DbcMessage message, Action<string, string, string, string, DbcEngineeringSeverity?> add)
    {
        foreach (var s in message.Signals)
        {
            if (s.BitLength is < 1 or > 64 || s.ValueType != DbcSignalValueType.Integer || s.UsesRangeDependentScale ||
                s.PhysicalConversionUnsupported || !double.IsFinite(s.Factor) || !double.IsFinite(s.Offset) ||
                !double.IsFinite(s.Minimum) || !double.IsFinite(s.Maximum))
                continue;
            double rawMin = s.IsSigned ? -Math.Pow(2, s.BitLength - 1) : 0;
            double rawMax = s.IsSigned ? Math.Pow(2, s.BitLength - 1) - 1 : Math.Pow(2, s.BitLength) - 1;
            var a = rawMin * s.Factor + s.Offset;
            var b = rawMax * s.Factor + s.Offset;
            var lo = Math.Min(a, b);
            var hi = Math.Max(a, b);
            // [0|0] means "unspecified" in DBC and is not a range declaration.
            var declared = !(s.Minimum == 0 && s.Maximum == 0) && s.Minimum <= s.Maximum;
            if (declared)
            {
                var tol = Math.Abs(s.Factor) / 2 + 1e-9 * Math.Max(1, Math.Abs(hi - lo));
                if (s.Minimum < lo - tol || s.Maximum > hi + tol)
                    add("DBC-013", message.Name, s.Name, string.Create(CultureInfo.InvariantCulture,
                        $"Declared physical range [{s.Minimum}|{s.Maximum}] exceeds what {s.BitLength}-bit {(s.IsSigned ? "signed" : "unsigned")} raw data can encode: [{lo}|{hi}]."), null);
            }
            if ((s.Factor != 1 || s.Offset != 0) && string.IsNullOrWhiteSpace(s.Unit) && s.ValueDescriptions is not { Count: > 0 })
                add("DBC-015", message.Name, s.Name, "Factor/offset is not identity but the unit is empty.", null);
            if (s.BitLength is >= 2 and <= 4 && s.Factor == 1 && s.Offset == 0 && string.IsNullOrWhiteSpace(s.Unit) &&
                s.ValueDescriptions is not { Count: > 0 } && !s.IsMultiplexor && s.MultiplexValue is null &&
                s.MultiplexConditions is not { Count: > 0 })
                add("DBC-021", message.Name, s.Name, $"{s.BitLength}-bit signal with identity scaling has no unit and no value descriptions.", null);
        }
    }

    private static void CheckMultiplexing(DbcMessage message, Action<string, string, string, string, DbcEngineeringSeverity?> add)
    {
        foreach (var selector in message.Signals.Where(s => s.IsMultiplexor))
        {
            var used = message.Signals.Any(s => !ReferenceEquals(s, selector) &&
                (s.MultiplexValue is not null || s.MultiplexConditions?.Any(c => c.Multiplexor == selector.Name) == true));
            if (!used)
                add("DBC-061", message.Name, selector.Name, "No signal is multiplexed by this selector.", null);
        }
        foreach (var s in message.Signals.Where(s => s.MultiplexConditions is { Count: > 0 }))
            foreach (var condition in s.MultiplexConditions!)
            {
                var ranges = condition.Ranges.OrderBy(r => r.Minimum).ToArray();
                for (var i = 1; i < ranges.Length; i++)
                    if (ranges[i].Minimum <= ranges[i - 1].Maximum)
                    {
                        add("DBC-062", message.Name, s.Name,
                            $"Ranges {ranges[i - 1].Minimum}-{ranges[i - 1].Maximum} and {ranges[i].Minimum}-{ranges[i].Maximum} for controller '{condition.Multiplexor}' overlap.", null);
                        break;
                    }
            }
    }

    private static void CheckJ1939(DbcMessage[] messages, Action<string, string, string, string, DbcEngineeringSeverity?> add)
    {
        static bool IsJ1939(DbcMessage m) => m.SourceAttributes.Any(a =>
            a.Name.Equals("VFrameFormat", StringComparison.OrdinalIgnoreCase) &&
            (a.RawValue.Trim('"').Equals("J1939PG", StringComparison.OrdinalIgnoreCase) || a.RawValue.Trim() == "2"));
        var j1939 = messages.Where(IsJ1939).ToArray();
        if (j1939.Length == 0) return;
        foreach (var m in j1939)
        {
            if (!m.IsExtended) { add("DBC-070", m.Name, "", "Flagged J1939 but uses an 11-bit identifier.", null); continue; }
            if (m.Identifier > 0x1FFF_FFFFu) continue;
            var info = J1939.Explain(m.Identifier);
            if (info.IsPdu1 && info.PduSpecific != 0 && info.PduSpecific != 0xFF)
                add("DBC-071", m.Name, "", $"PF 0x{info.PduFormat:X2} < 240 with fixed destination 0x{info.PduSpecific:X2}; PGN is 0x{info.Pgn:X5}.", null);
            if (m.Length > 8)
                add("DBC-072", m.Name, "", $"{m.Length}-byte payload needs J1939 transport protocol (BAM/RTS-CTS).", null);
        }
        foreach (var dup in j1939.Where(m => m.IsExtended && m.Identifier <= 0x1FFF_FFFFu)
                     .GroupBy(m => (J1939.ExtractPgn(m.Identifier), (byte)(m.Identifier & 0xFF)))
                     .Where(g => g.Count() > 1))
            add("DBC-073", string.Join(" / ", dup.Select(m => m.Name)), "",
                $"PGN 0x{dup.Key.Item1:X5} with SA 0x{dup.Key.Item2:X2} defined {dup.Count()} times.", null);
        foreach (var m in messages.Where(m => !m.IsExtended && !IsJ1939(m)))
            add("DBC-074", m.Name, "", $"11-bit message 0x{m.Identifier:X3} in a database that contains J1939 messages.", null);
    }

    // ---------------------------------------------------------------- source-text rules

    private static void CheckSource(DbcDatabase db, DbcMessage[] messages, SourceModel src,
        Action<string, string, string, string, DbcEngineeringSeverity?> add)
    {
        foreach (var g in src.Messages.GroupBy(m => m.RawId).Where(g => g.Count() > 1))
            add("DBC-001", string.Join(" / ", g.Select(m => m.Name)), "",
                "Identifier " + ((g.Key & 0x8000_0000u) != 0
                    ? "0x" + (g.Key & 0x1FFF_FFFFu).ToString("X", CultureInfo.InvariantCulture) + "x (29-bit)"
                    : "0x" + g.Key.ToString("X", CultureInfo.InvariantCulture)) + $" defined {g.Count()} times.", null);

        var nodes = src.Nodes.ToHashSet(StringComparer.Ordinal);
        var senders = new HashSet<string>(StringComparer.Ordinal);
        var receivers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var m in src.Messages)
        {
            if (m.Transmitter is "" or "Vector__XXX")
                add("DBC-033", m.Name, "", "No transmitter node.", null);
            else
            {
                senders.Add(m.Transmitter);
                if (nodes.Count > 0 && !nodes.Contains(m.Transmitter))
                    add("DBC-030", m.Name, "", $"Transmitter '{m.Transmitter}' is not listed in BU_.", null);
            }
            foreach (var s in m.Signals)
                foreach (var rx in s.Receivers.Where(r => r != "Vector__XXX"))
                {
                    receivers.Add(rx);
                    if (nodes.Count > 0 && !nodes.Contains(rx))
                        add("DBC-031", m.Name, s.Name, $"Receiver '{rx}' is not listed in BU_.", null);
                }
        }
        foreach (var node in src.Nodes.Where(n => !senders.Contains(n) && !receivers.Contains(n)))
            add("DBC-032", node, "", "Node neither transmits a message nor receives a signal.", null);

        var messageIds = src.Messages.Select(m => m.RawId).ToHashSet();
        var signalKeys = src.Messages.SelectMany(m => m.Signals.Select(s => (m.RawId, s.Name))).ToHashSet();
        foreach (var v in src.ValueStatements.Where(v => !signalKeys.Contains((v.RawId, v.Signal))))
            add("DBC-022", messageIds.Contains(v.RawId) ? src.Messages.First(m => m.RawId == v.RawId).Name : $"ID {v.RawId}", v.Signal,
                messageIds.Contains(v.RawId) ? "VAL_ references a signal that is not in the message." : "VAL_ references an unknown message ID.", null);

        // Attributes
        var defs = new Dictionary<string, AttributeDef>(StringComparer.Ordinal);
        foreach (var d in src.AttributeDefs) defs[d.Name] = d;
        var defaults = src.AttributeDefaults.ToDictionary(d => d.Name, d => d.Value, StringComparer.Ordinal);
        foreach (var d in src.AttributeDefaults.Where(d => !defs.ContainsKey(d.Name)))
            add("DBC-040", d.Name, "", "BA_DEF_DEF_ has no BA_DEF_.", null);
        foreach (var d in defs.Values)
        {
            if (!defaults.ContainsKey(d.Name))
                add("DBC-043", d.Name, "", "No BA_DEF_DEF_ default.", null);
            else if (CheckAttributeValue(d, defaults[d.Name]) is { } problem)
                add("DBC-041", d.Name, "", $"Default {defaults[d.Name]}: {problem}", null);
        }
        var nodeSet = nodes;
        foreach (var a in src.AttributeValues)
        {
            if (!defs.TryGetValue(a.Name, out var def))
            {
                add("DBC-040", a.Name, "", "BA_ uses an attribute without BA_DEF_.", null);
                continue;
            }
            if (def.Scope != a.Scope)
                add("DBC-044", a.Name, "", $"Defined for {Scope(def.Scope)} but applied to {Scope(a.Scope)}.", null);
            if (CheckAttributeValue(def, a.Value) is { } problem)
                add("DBC-042", a.Name, a.Target, $"Value {a.Value}: {problem}", null);
            var known = a.Scope switch
            {
                'B' => a.RawId is { } id && messageIds.Contains(id),
                'S' => a.RawId is { } sid && a.Target.Length > 0 && signalKeys.Contains((sid, a.Target)),
                'N' => nodeSet.Contains(a.Target) || nodeSet.Count == 0,
                _ => true
            };
            if (!known)
                add("DBC-045", a.Name, a.Target, "Attribute references an object that does not exist.", null);
        }
    }

    private static string Scope(char scope) => scope switch { 'B' => "BO_", 'S' => "SG_", 'N' => "BU_", 'E' => "EV_", _ => "network" };

    /// <summary>Returns a problem description, or null when the value satisfies the definition.</summary>
    private static string? CheckAttributeValue(AttributeDef def, string raw)
    {
        raw = raw.Trim();
        switch (def.Type)
        {
            case "STRING":
                return raw.StartsWith('"') && raw.EndsWith('"') && raw.Length >= 2 ? null : "string value must be quoted";
            case "ENUM":
                if (raw.StartsWith('"'))
                    return def.EnumValues.Contains(raw.Trim('"')) ? null : "not one of the enumeration labels";
                if (!long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
                    return "enum value must be an index or a label";
                return index >= 0 && index < def.EnumValues.Count ? null : $"enum index outside 0..{def.EnumValues.Count - 1}";
            default:
                if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                    return "not a number";
                if (def.Type is "INT" or "HEX" && v != Math.Truncate(v)) return "integer attribute holds a fraction";
                if (def.Min is { } min && def.Max is { } max && !(min == 0 && max == 0) && (v < min || v > max))
                    return string.Create(CultureInfo.InvariantCulture, $"outside declared range [{min}|{max}]");
                return null;
        }
    }

    private sealed record SourceSignal(string Name, IReadOnlyList<string> Receivers);
    private sealed record SourceMessage(uint RawId, string Name, string Transmitter, List<SourceSignal> Signals);
    private sealed record ValueStatement(uint RawId, string Signal);
    private sealed record AttributeDef(string Name, char Scope, string Type, double? Min, double? Max, IReadOnlyList<string> EnumValues);
    private sealed record AttributeDefault(string Name, string Value);
    private sealed record AttributeValue(string Name, char Scope, uint? RawId, string Target, string Value);

    private sealed class SourceModel
    {
        public List<string> Nodes { get; } = [];
        public List<SourceMessage> Messages { get; } = [];
        public List<ValueStatement> ValueStatements { get; } = [];
        public List<AttributeDef> AttributeDefs { get; } = [];
        public List<AttributeDefault> AttributeDefaults { get; } = [];
        public List<AttributeValue> AttributeValues { get; } = [];

        public static SourceModel Parse(string text)
        {
            var model = new SourceModel();
            SourceMessage? current = null;
            var statement = new StringBuilder();
            foreach (var rawLine in text.Replace("\r\n", "\n").Split('\n'))
            {
                var line = rawLine.Trim();
                if (statement.Length > 0)
                {
                    statement.Append(' ').Append(line);
                    if (EndsStatement(statement.ToString())) { model.Statement(statement.ToString()); statement.Clear(); }
                    continue;
                }
                if (line.Length == 0) continue;
                if (BuLine().Match(line) is { Success: true } bu)
                {
                    model.Nodes.AddRange(bu.Groups["n"].Value.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries));
                    current = null;
                }
                else if (BoLine().Match(line) is { Success: true } bo)
                {
                    current = new SourceMessage(uint.Parse(bo.Groups["id"].Value, CultureInfo.InvariantCulture),
                        bo.Groups["name"].Value, bo.Groups["tx"].Value, []);
                    // Vector's container for signals without a message is not a real message.
                    if (current.Name == "VECTOR__INDEPENDENT_SIG_MSG") current = null;
                    else model.Messages.Add(current);
                }
                else if (current is not null && SgLine().Match(line) is { Success: true } sg)
                {
                    current.Signals.Add(new SourceSignal(sg.Groups["name"].Value,
                        sg.Groups["rx"].Value.Split([',', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries)));
                }
                else if (StatementStart().IsMatch(line))
                {
                    if (EndsStatement(line)) model.Statement(line);
                    else statement.Append(line);
                }
            }
            return model;
        }

        private static bool EndsStatement(string s)
        {
            var inQuote = false;
            for (var i = 0; i < s.Length; i++)
            {
                if (s[i] == '\\' && inQuote) { i++; continue; }
                if (s[i] == '"') inQuote = !inQuote;
            }
            return !inQuote && s.TrimEnd().EndsWith(';');
        }

        private void Statement(string s)
        {
            s = s.Trim().TrimEnd(';').Trim();
            if (AttrDef().Match(s) is { Success: true } d)
            {
                var type = d.Groups["type"].Value;
                var args = d.Groups["args"].Value.Trim();
                double? min = null, max = null;
                var labels = new List<string>();
                if (type == "ENUM")
                    labels.AddRange(Regex.Matches(args, "\"((?:\\\\.|[^\"])*)\"").Select(m => m.Groups[1].Value));
                else if (type is "INT" or "HEX" or "FLOAT")
                {
                    var parts = args.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2 && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var lo) &&
                        double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var hi)) { min = lo; max = hi; }
                }
                var scope = d.Groups["scope"].Value switch { "BO_" => 'B', "SG_" => 'S', "BU_" => 'N', "EV_" => 'E', _ => 'G' };
                AttributeDefs.Add(new AttributeDef(d.Groups["name"].Value, scope, type, min, max, labels));
            }
            else if (AttrDefault().Match(s) is { Success: true } dd)
                AttributeDefaults.Add(new AttributeDefault(dd.Groups["name"].Value, dd.Groups["v"].Value.Trim()));
            else if (AttrValue().Match(s) is { Success: true } a)
            {
                var kind = a.Groups["kind"].Value;
                var scope = kind switch { "BO_" => 'B', "SG_" => 'S', "BU_" => 'N', "EV_" => 'E', _ => 'G' };
                uint? rawId = a.Groups["id"].Success ? uint.Parse(a.Groups["id"].Value, CultureInfo.InvariantCulture) : null;
                var target = a.Groups["target"].Success ? a.Groups["target"].Value : string.Empty;
                AttributeValues.Add(new AttributeValue(a.Groups["name"].Value, scope, rawId, target, a.Groups["v"].Value.Trim()));
            }
            else if (ValLine().Match(s) is { Success: true } v)
                ValueStatements.Add(new ValueStatement(uint.Parse(v.Groups["id"].Value, CultureInfo.InvariantCulture), v.Groups["sig"].Value));
        }
    }

    [GeneratedRegex(@"^BU_\s*:\s*(?<n>.*)$")] private static partial Regex BuLine();
    [GeneratedRegex(@"^BO_\s+(?<id>\d+)\s+(?<name>\S+?)\s*:\s*(?<dlc>\d+)\s+(?<tx>\S*)\s*$")] private static partial Regex BoLine();
    [GeneratedRegex(@"^SG_\s+(?<name>\S+)(?:\s+(?:M|m\d+M?))?\s*:\s*\d+\|\d+@[01][+-]\s*\([^)]*\)\s*\[[^\]]*\]\s*""(?:\\.|[^""])*""\s*(?<rx>.*)$")] private static partial Regex SgLine();
    [GeneratedRegex(@"^(BA_DEF_DEF_|BA_DEF_|BA_|VAL_)\s")] private static partial Regex StatementStart();
    [GeneratedRegex(@"^BA_DEF_\s+(?:(?<scope>BU_|BO_|SG_|EV_)\s+)?""(?<name>[^""]+)""\s+(?<type>INT|HEX|FLOAT|STRING|ENUM)\s*(?<args>.*)$", RegexOptions.Singleline)] private static partial Regex AttrDef();
    [GeneratedRegex(@"^BA_DEF_DEF_\s+""(?<name>[^""]+)""\s+(?<v>.+)$", RegexOptions.Singleline)] private static partial Regex AttrDefault();
    [GeneratedRegex(@"^BA_\s+""(?<name>[^""]+)""\s+(?:(?<kind>BO_)\s+(?<id>\d+)|(?<kind>SG_)\s+(?<id>\d+)\s+(?<target>\S+)|(?<kind>BU_)\s+(?<target>\S+)|(?<kind>EV_)\s+(?<target>\S+))?\s*(?<v>.+)$", RegexOptions.Singleline)] private static partial Regex AttrValue();
    [GeneratedRegex(@"^VAL_\s+(?<id>\d+)\s+(?<sig>\S+)\s")] private static partial Regex ValLine();
}
