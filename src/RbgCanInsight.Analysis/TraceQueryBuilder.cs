using System.Globalization;

namespace RbgCanInsight.Analysis;

/// <summary>What a query condition tests. Every field maps onto the existing Trace search grammar,
/// so a built query behaves exactly like the same text typed into the search box.</summary>
public enum QueryField
{
    CanId,
    MessageName,
    Channel,
    PayloadContains,
    UdsAny,
    UdsText,
    FrameKind,
    /// <summary>CAN ID range or relation: "100..1FF", ">=100", "&lt;7FF" (hex).</summary>
    IdRange,
    /// <summary>Data byte relation, mask or bit pattern: "0=5A", "1&gt;=10", "0&amp;0F=05", "2=b1xxxxxx0".</summary>
    ByteRelation,
    /// <summary>Payload length relation: "8", "&gt;=4" (bytes, decimal).</summary>
    DataLength,
    /// <summary>Time window in seconds from the first frame: "1.5..3", "&gt;=2".</summary>
    TimeWindow
}

/// <summary>Frame kinds understood by the Trace search grammar.</summary>
public enum QueryFrameKind
{
    Error,
    CanFd,
    Remote,
    Extended,
    Standard,
    NoDbcMatch,
    /// <summary>Logged as transmitted (Tx).</summary>
    Transmitted,
    /// <summary>Not logged as transmitted: received, or direction not recorded.</summary>
    ReceivedOrUnknown
}

/// <summary>One condition of a guided query. <see cref="Exclude"/> turns it into a stop term
/// ("-ch2", "-payload:AA") that removes matching frames instead of requiring them.</summary>
/// <summary><see cref="OrWithPrevious"/> starts a new alternative: "A B OR C" means (A and B) or C.</summary>
public sealed record QueryCondition(QueryField Field, string Value, bool Exclude = false, bool OrWithPrevious = false);

public sealed record QueryCompositionResult(
    string Query, IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings)
{
    public bool IsValid => Errors.Count == 0;
}

/// <summary>
/// Builds, validates, explains and re-reads Trace filter text. Conditions are ANDed unless marked
/// "OR with previous" (which starts a new alternative group); an excluded condition removes frames. The builder never invents syntax the Trace
/// filter would not accept, and refuses values that would silently mean something else.
/// </summary>
public static class TraceQueryBuilder
{
    private static readonly (QueryFrameKind Kind, string Term, string Label)[] KindTerms =
    [
        (QueryFrameKind.Error, "error", "CAN error frames"),
        (QueryFrameKind.CanFd, "fd", "CAN FD frames"),
        (QueryFrameKind.Remote, "remote", "remote frames"),
        (QueryFrameKind.Extended, "extended", "29-bit (extended) identifiers"),
        (QueryFrameKind.Standard, "standard", "11-bit (standard) identifiers"),
        (QueryFrameKind.NoDbcMatch, "unmatched", "frames without a matching DBC message"),
        (QueryFrameKind.Transmitted, "dir:tx", "frames logged as transmitted (Tx)"),
        (QueryFrameKind.ReceivedOrUnknown, "dir:rx", "frames not logged as transmitted (Rx or direction unknown)")
    ];

    public static IEnumerable<string> FrameKindNames => KindTerms.Select(item => item.Kind.ToString());

    public static string FrameKindTerm(QueryFrameKind kind) => KindTerms.First(item => item.Kind == kind).Term;

    /// <summary>Text shown in English for a field name in the builder.</summary>
    public static string FieldLabel(QueryField field) => field switch
    {
        QueryField.CanId => "CAN ID (hex)",
        QueryField.MessageName => "DBC message name contains",
        QueryField.Channel => "CAN channel",
        QueryField.PayloadContains => "Payload contains (hex)",
        QueryField.UdsAny => "Is a UDS / ISO-TP frame",
        QueryField.UdsText => "UDS description contains",
        QueryField.FrameKind => "Frame kind",
        QueryField.IdRange => "CAN ID range or relation (hex)",
        QueryField.ByteRelation => "Data byte (e.g. 0=5A, 1>=10, 0&0F=05)",
        QueryField.DataLength => "Payload length in bytes",
        QueryField.TimeWindow => "Time window in seconds (e.g. 1.5..3)",
        _ => field.ToString()
    };

    public static QueryCompositionResult Compose(IEnumerable<QueryCondition> conditions)
    {
        var terms = new List<string>();
        var errors = new List<string>();
        var warnings = new List<string>();
        var index = 0;
        foreach (var condition in conditions)
        {
            index++;
            var value = (condition.Value ?? string.Empty).Trim();
            string? term = null;
            switch (condition.Field)
            {
                case QueryField.CanId:
                    var hex = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? value[2..] : value;
                    if (hex.Length == 0 || hex.Length > 8 ||
                        !uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _))
                        errors.Add($"Condition {index}: \"{value}\" is not a hexadecimal CAN ID (up to 8 digits).");
                    else
                        term = hex.ToUpperInvariant();
                    break;

                case QueryField.MessageName:
                    if (value.Length == 0)
                        errors.Add($"Condition {index}: enter part of a message name.");
                    else if (value.Any(char.IsWhiteSpace))
                        errors.Add($"Condition {index}: a message name cannot contain spaces in a filter.");
                    else if (LooksLikeHexIdentifier(value))
                        errors.Add($"Condition {index}: \"{value}\" would also be read as a CAN ID. " +
                                   "Use the CAN ID field, or a longer name fragment.");
                    else if (IsReservedWord(value) || value.StartsWith("ch", StringComparison.OrdinalIgnoreCase) &&
                             int.TryParse(value[2..], out _))
                        errors.Add($"Condition {index}: \"{value}\" is a reserved filter keyword.");
                    else
                        term = value;
                    break;

                case QueryField.Channel:
                    if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var channel) ||
                        channel < 1)
                        errors.Add($"Condition {index}: the channel must be a number starting at 1.");
                    else
                        term = "ch" + channel.ToString(CultureInfo.InvariantCulture);
                    break;

                case QueryField.PayloadContains:
                    var payload = value.Replace(" ", string.Empty);
                    if (payload.Length == 0 || payload.Any(character => !Uri.IsHexDigit(character)))
                        errors.Add($"Condition {index}: the payload must be hexadecimal digits (spaces are allowed).");
                    else
                    {
                        term = "payload:" + payload.ToUpperInvariant();
                        if (payload.Length % 2 != 0)
                            warnings.Add($"Condition {index}: an odd number of digits matches half-byte " +
                                         "sequences anywhere in the payload.");
                    }
                    break;

                case QueryField.UdsAny:
                    term = "uds";
                    break;

                case QueryField.UdsText:
                    if (value.Length == 0 || value.Any(char.IsWhiteSpace))
                        errors.Add($"Condition {index}: enter one word of the UDS description, without spaces.");
                    else
                        term = "uds:" + value;
                    break;

                case QueryField.FrameKind:
                    if (!Enum.TryParse<QueryFrameKind>(value, ignoreCase: true, out var kind))
                        errors.Add($"Condition {index}: unknown frame kind \"{value}\".");
                    else
                        term = FrameKindTerm(kind);
                    break;

                case QueryField.IdRange:
                case QueryField.ByteRelation:
                case QueryField.DataLength:
                case QueryField.TimeWindow:
                    term = AdvancedTerm(condition.Field, value);
                    string? advancedError = null;
                    if (term is null || !FrameQuery.TryParseAdvancedTerm(term, 0, out _, out advancedError))
                    {
                        errors.Add($"Condition {index}: " + (advancedError is null
                            ? $"\"{value}\" is not valid for {FieldLabel(condition.Field).ToLowerInvariant()}."
                            : advancedError));
                        term = null;
                    }
                    break;
            }

            if (term is not null)
            {
                var text = condition.Exclude ? "-" + term : term;
                terms.Add(condition.OrWithPrevious && terms.Count > 0 ? "OR " + text : text);
            }
        }

        if (errors.Count == 0 && terms.Count > 0 && terms.All(term => term.StartsWith('-')))
            warnings.Add("Only exclusions are set: every frame except the excluded ones is shown.");
        return new QueryCompositionResult(string.Join(' ', terms), errors, warnings);
    }

    /// <summary>Turns the value typed for an advanced field into its grammar term, or null when empty.</summary>
    private static string? AdvancedTerm(QueryField field, string value)
    {
        value = value.Replace(" ", string.Empty);
        if (value.Length == 0) return null;
        var startsWithOperator = "<>=!:".Contains(value[0]);
        return field switch
        {
            QueryField.IdRange => startsWithOperator ? "id" + value : "id:" + value,
            QueryField.ByteRelation => "d" + value,
            QueryField.DataLength => startsWithOperator ? "dlc" + value : "dlc=" + value,
            QueryField.TimeWindow => startsWithOperator ? "t" + value : "t:" + value,
            _ => null
        };
    }

    /// <summary>A plain-English reading of the query, e.g. "channel 1 AND CAN ID 100 AND NOT payload contains AA".</summary>
    public static string Describe(IEnumerable<QueryCondition> conditions)
    {
        var parts = new List<string>();
        foreach (var condition in conditions)
        {
            var value = (condition.Value ?? string.Empty).Trim();
            var connector = condition.OrWithPrevious && parts.Count > 0 ? " OR " : " AND ";
            var text = condition.Field switch
            {
                QueryField.CanId => $"CAN ID is {value.ToUpperInvariant()}",
                QueryField.MessageName => $"message name contains \"{value}\"",
                QueryField.Channel => $"channel is {value}",
                QueryField.PayloadContains => $"payload contains {value.Replace(" ", string.Empty).ToUpperInvariant()}",
                QueryField.UdsAny => "frame is UDS / ISO-TP",
                QueryField.UdsText => $"UDS description contains \"{value}\"",
                QueryField.FrameKind => Enum.TryParse<QueryFrameKind>(value, true, out var kind)
                    ? "frame is one of " + KindTerms.First(item => item.Kind == kind).Label
                    : $"frame kind {value}",
                QueryField.IdRange => $"CAN ID {value.ToUpperInvariant()}",
                QueryField.ByteRelation => $"data byte {value.ToUpperInvariant()}",
                QueryField.DataLength => $"payload length {value}",
                QueryField.TimeWindow => $"time (s) {value}",
                _ => value
            };
            if (condition.Exclude) text = "NOT " + text;
            parts.Add(parts.Count == 0 ? text : connector + text);
        }

        return parts.Count == 0 ? "Every frame (no conditions)" : "Show frames where " + string.Concat(parts);
    }

    /// <summary>Reads existing filter text back into conditions so the builder can edit it.
    /// Terms the builder cannot represent are returned in <paramref name="unrecognized"/>.</summary>
    public static IReadOnlyList<QueryCondition> Parse(string query, out IReadOnlyList<string> unrecognized)
    {
        var conditions = new List<QueryCondition>();
        var rest = new List<string>();
        var pendingOr = false;
        foreach (var raw in (query ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries |
                                                               StringSplitOptions.TrimEntries))
        {
            if (raw == "OR" || raw == "|")
            {
                pendingOr = conditions.Count > 0;
                continue;
            }

            var exclude = raw.Length > 1 && raw[0] == '-';
            var term = exclude ? raw[1..] : raw;
            QueryCondition? condition = null;
            if (FrameQuery.LooksAdvanced(term))
                condition = ParseAdvanced(term, exclude);
            else if (term.Contains('|'))
                condition = null; // per-term alternatives ("100|200") are edited as text, not in the builder
            else if (term.StartsWith("ch", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(term[2..], NumberStyles.None, CultureInfo.InvariantCulture, out var channel))
                condition = new QueryCondition(QueryField.Channel,
                    channel.ToString(CultureInfo.InvariantCulture), exclude);
            else if (KindTerms.FirstOrDefault(item => item.Term.Equals(term, StringComparison.OrdinalIgnoreCase)
                         || term.Equals("errors", StringComparison.OrdinalIgnoreCase) && item.Kind == QueryFrameKind.Error
                         || (term.Equals("rtr", StringComparison.OrdinalIgnoreCase) && item.Kind == QueryFrameKind.Remote))
                     is { Term: not null } known)
                condition = new QueryCondition(QueryField.FrameKind, known.Kind.ToString(), exclude);
            else if (term.StartsWith("payload:", StringComparison.OrdinalIgnoreCase))
                condition = new QueryCondition(QueryField.PayloadContains, term[8..], exclude);
            else if (term.Equals("uds", StringComparison.OrdinalIgnoreCase))
                condition = new QueryCondition(QueryField.UdsAny, string.Empty, exclude);
            else if (term.StartsWith("uds:", StringComparison.OrdinalIgnoreCase) && term.Length > 4)
                condition = new QueryCondition(QueryField.UdsText, term[4..], exclude);
            else if (LooksLikeHexIdentifier(term))
                condition = new QueryCondition(QueryField.CanId, term.TrimStart('0', 'x', 'X') is { Length: > 0 } t
                    ? t : term, exclude);
            else if (term.Length > 0)
                condition = new QueryCondition(QueryField.MessageName, term, exclude);

            if (condition is null) rest.Add(raw);
            else
            {
                conditions.Add(pendingOr ? condition with { OrWithPrevious = true } : condition);
                pendingOr = false;
            }
        }

        unrecognized = rest;
        return conditions;
    }

    private static QueryCondition? ParseAdvanced(string term, bool exclude)
    {
        if (!FrameQuery.TryParseAdvancedTerm(term, 0, out _, out _)) return null;
        var lower = term.ToLowerInvariant();
        if (lower.StartsWith("dir:", StringComparison.Ordinal))
            return new QueryCondition(QueryField.FrameKind,
                (lower == "dir:tx" ? QueryFrameKind.Transmitted : QueryFrameKind.ReceivedOrUnknown).ToString(), exclude);
        if (lower.StartsWith("dlc", StringComparison.Ordinal))
            return new QueryCondition(QueryField.DataLength, StripLeadingEquals(term[3..]), exclude);
        if (lower.StartsWith("id", StringComparison.Ordinal))
            return new QueryCondition(QueryField.IdRange, StripLeadingEquals(term[2..]), exclude);
        if (lower[0] == 'd')
            return new QueryCondition(QueryField.ByteRelation, term[1..], exclude);
        return new QueryCondition(QueryField.TimeWindow, term[1..].TrimStart(':'), exclude);
    }

    private static string StripLeadingEquals(string rest) =>
        rest.Length > 1 && (rest[0] == ':' || rest[0] == '=' && rest[1] != '=') ? rest[1..] : rest;

    private static bool IsReservedWord(string value) =>
        KindTerms.Any(item => item.Term.Equals(value, StringComparison.OrdinalIgnoreCase)) ||
        value.Equals("errors", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("rtr", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("uds", StringComparison.OrdinalIgnoreCase) ||
        value.StartsWith("uds:", StringComparison.OrdinalIgnoreCase) ||
        value.StartsWith("payload:", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeHexIdentifier(string value)
    {
        var hex = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? value[2..] : value;
        return hex.Length > 0 && hex.Length <= 8 &&
               uint.TryParse(hex.TrimStart('0', 'x', 'X') is { Length: > 0 } trimmed ? trimmed : "0",
                   NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _) &&
               hex.All(Uri.IsHexDigit);
    }
}
