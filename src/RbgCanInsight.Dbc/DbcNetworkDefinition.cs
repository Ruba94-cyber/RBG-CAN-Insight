using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace RbgCanInsight.Dbc;

public enum DbcAttributeType { Int, Hex, Float, String, Enum }

public enum DbcAttributeScope { Network, Node, Message, Signal, EnvironmentVariable }

/// <summary>A typed BA_DEF_ (+ BA_DEF_DEF_ default). Min/Max apply to INT/HEX/FLOAT; EnumValues to ENUM.</summary>
public sealed record DbcAttributeDefinition(
    string Name, DbcAttributeScope Scope, DbcAttributeType Type, double? Minimum, double? Maximum,
    IReadOnlyList<string> EnumValues, string? DefaultRaw)
{
    public string RangeText => Type switch
    {
        DbcAttributeType.Enum => string.Join(", ", EnumValues.Select((v, i) => $"{i}={v}")),
        DbcAttributeType.String => "",
        _ => Minimum is { } lo && Maximum is { } hi
            ? lo.ToString("0.######", CultureInfo.InvariantCulture) + " .. " + hi.ToString("0.######", CultureInfo.InvariantCulture) : "",
    };

    public string DefaultText => DefaultRaw is null ? "(no default)" : DbcNetworkDefinition.FormatValue(this, DefaultRaw);

    /// <summary>Null when <paramref name="raw"/> is valid for this definition, otherwise the reason.</summary>
    public string? Validate(string raw)
    {
        var v = raw.Trim();
        switch (Type)
        {
            case DbcAttributeType.String:
                return v.StartsWith('"') ? null : "string value is not quoted";
            case DbcAttributeType.Enum:
                if (v.StartsWith('"')) return EnumValues.Contains(Unquote(v)) ? null : $"'{Unquote(v)}' is not in the enum list";
                return int.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out var i) && i < EnumValues.Count ? null : $"enum index {v} is out of range";
            default:
                if (!double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return $"'{v}' is not a number";
                if (Type != DbcAttributeType.Float && d != Math.Truncate(d)) return $"'{v}' is not an integer";
                if (Minimum is { } lo && Maximum is { } hi && !(lo == 0 && hi == 0) && (d < lo || d > hi)) return $"{v} is outside {lo}..{hi}";
                return null;
        }
    }

    internal static string Unquote(string s) => s.Length >= 2 && s[0] == '"' && s[^1] == '"' ? s[1..^1].Replace("\\\"", "\"") : s;
}

/// <summary>A BA_ value. <see cref="Target"/> is empty for network attributes, the node name, the message name or "Message.Signal".</summary>
public sealed record DbcAttributeAssignment(string Name, DbcAttributeScope Scope, string Target, string RawValue, string DisplayValue, string? Problem);

public sealed record DbcNode(string Name, string Comment);

/// <summary>Transmitters of one message beyond the BO_ transmitter (BO_TX_BU_), keyed by the raw BO_ identifier.</summary>
public sealed record DbcExtraTransmitters(uint RawIdentifier, IReadOnlyList<string> Nodes);

/// <summary>
/// Network-level DBC statements that the signal/message parser does not keep: BU_ nodes (with CM_ BU_ comments), BO_TX_BU_ extra
/// transmitters and typed attribute definitions/values (BA_DEF_, BA_DEF_DEF_, BA_). Read from the DBC text; definition-only.
/// </summary>
public sealed partial class DbcNetworkDefinition
{
    public IReadOnlyList<DbcNode> Nodes { get; private init; } = [];
    public IReadOnlyList<DbcExtraTransmitters> ExtraTransmitters { get; private init; } = [];
    public IReadOnlyList<DbcAttributeDefinition> AttributeDefinitions { get; private init; } = [];
    public IReadOnlyList<DbcAttributeAssignment> AttributeAssignments { get; private init; } = [];
    /// <summary>Statements of the attribute family that were recognised but not modelled (e.g. BA_DEF_REL_).</summary>
    public IReadOnlyList<string> Unmodelled { get; private init; } = [];

    public static string FormatValue(DbcAttributeDefinition def, string raw)
    {
        var v = raw.Trim();
        if (def.Type == DbcAttributeType.Enum)
        {
            if (v.StartsWith('"')) return DbcAttributeDefinition.Unquote(v);
            if (int.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out var i) && i < def.EnumValues.Count)
                return $"{def.EnumValues[i]} ({i})";
        }
        if (def.Type == DbcAttributeType.Hex && long.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out var h)) return $"0x{h:X} ({h})";
        return DbcAttributeDefinition.Unquote(v);
    }

    public static DbcNetworkDefinition Read(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var nodes = new List<string>();
        var comments = new Dictionary<string, string>(StringComparer.Ordinal);
        var extra = new List<DbcExtraTransmitters>();
        var defs = new List<DbcAttributeDefinition>();
        var defaults = new Dictionary<string, string>(StringComparer.Ordinal);
        var values = new List<(string Name, string Scope, string Remainder)>();
        var unmodelled = new List<string>();
        var messageNames = new Dictionary<uint, string>();

        foreach (var statement in Statements(text))
        {
            if (BuLine().Match(statement) is { Success: true } bu)
                nodes.AddRange(bu.Groups["n"].Value.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
            else if (BoLine().Match(statement) is { Success: true } bo)
            {
                if (uint.TryParse(bo.Groups["id"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
                    messageNames[id] = bo.Groups["name"].Value;
            }
            else if (TxBu().Match(statement) is { Success: true } tx)
            {
                if (uint.TryParse(tx.Groups["id"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
                    extra.Add(new DbcExtraTransmitters(id, tx.Groups["n"].Value.Split([',', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries)));
            }
            else if (NodeComment().Match(statement) is { Success: true } cm)
                comments[cm.Groups["n"].Value] = DbcAttributeDefinition.Unquote("\"" + cm.Groups["t"].Value + "\"");
            else if (AttrDef().Match(statement) is { Success: true } d)
            {
                var type = Enum.Parse<DbcAttributeType>(d.Groups["type"].Value, true);
                var args = d.Groups["args"].Value.Trim().TrimEnd(';').Trim();
                double? lo = null, hi = null;
                var labels = new List<string>();
                if (type == DbcAttributeType.Enum)
                    labels.AddRange(Quoted().Matches(args).Select(m => m.Groups[1].Value));
                else if (type != DbcAttributeType.String)
                {
                    var parts = args.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2 && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var a) &&
                        double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var b)) { lo = a; hi = b; }
                }
                var scope = d.Groups["scope"].Value switch
                {
                    "BU_" => DbcAttributeScope.Node, "BO_" => DbcAttributeScope.Message, "SG_" => DbcAttributeScope.Signal,
                    "EV_" => DbcAttributeScope.EnvironmentVariable, _ => DbcAttributeScope.Network,
                };
                defs.Add(new DbcAttributeDefinition(d.Groups["name"].Value, scope, type, lo, hi, labels, null));
            }
            else if (AttrDefault().Match(statement) is { Success: true } dd)
                defaults[dd.Groups["name"].Value] = dd.Groups["v"].Value.Trim().TrimEnd(';').Trim();
            else if (AttrValue().Match(statement) is { Success: true } av)
                values.Add((av.Groups["name"].Value, av.Groups["kind"].Value, av.Groups["rest"].Value.Trim().TrimEnd(';').Trim()));
            else if (statement.StartsWith("BA_DEF_REL_", StringComparison.Ordinal) || statement.StartsWith("BA_DEF_DEF_REL_", StringComparison.Ordinal) ||
                     statement.StartsWith("BA_REL_", StringComparison.Ordinal))
                unmodelled.Add(statement.Length > 80 ? statement[..80] + "..." : statement);
        }

        defs = defs.Select(d => defaults.TryGetValue(d.Name, out var def) ? d with { DefaultRaw = def } : d).ToList();
        var lookup = defs.GroupBy(d => (d.Name, d.Scope)).ToDictionary(g => g.Key, g => g.First());
        var assignments = new List<DbcAttributeAssignment>();
        foreach (var (name, kind, rest) in values)
        {
            var scope = kind switch { "BU_" => DbcAttributeScope.Node, "BO_" => DbcAttributeScope.Message, "SG_" => DbcAttributeScope.Signal,
                "EV_" => DbcAttributeScope.EnvironmentVariable, _ => DbcAttributeScope.Network };
            string target, value;
            var tokens = rest.Split([' ', '\t'], 3, StringSplitOptions.RemoveEmptyEntries);
            switch (scope)
            {
                case DbcAttributeScope.Network: target = ""; value = rest; break;
                case DbcAttributeScope.Message when tokens.Length >= 2:
                    target = uint.TryParse(tokens[0], NumberStyles.None, CultureInfo.InvariantCulture, out var mid) && messageNames.TryGetValue(mid, out var mn) ? mn : tokens[0];
                    value = rest[tokens[0].Length..].Trim(); break;
                case DbcAttributeScope.Signal when tokens.Length >= 3:
                    var sm = uint.TryParse(tokens[0], NumberStyles.None, CultureInfo.InvariantCulture, out var sid) && messageNames.TryGetValue(sid, out var sn) ? sn : tokens[0];
                    target = sm + "." + tokens[1];
                    value = rest[(tokens[0].Length + 1 + tokens[1].Length)..].Trim(); break;
                case DbcAttributeScope.Node or DbcAttributeScope.EnvironmentVariable when tokens.Length >= 2:
                    target = tokens[0]; value = rest[tokens[0].Length..].Trim(); break;
                default: continue;
            }
            lookup.TryGetValue((name, scope), out var def);
            string? problem = def is null
                ? (defs.Any(x => x.Name == name) ? "defined for a different object class" : "no BA_DEF_ for this attribute")
                : def.Validate(value);
            assignments.Add(new DbcAttributeAssignment(name, scope, target, value, def is null ? DbcAttributeDefinition.Unquote(value) : FormatValue(def, value), problem));
        }

        return new DbcNetworkDefinition
        {
            Nodes = nodes.Distinct(StringComparer.Ordinal).Select(n => new DbcNode(n, comments.GetValueOrDefault(n, ""))).ToList(),
            ExtraTransmitters = extra, AttributeDefinitions = defs, AttributeAssignments = assignments, Unmodelled = unmodelled,
        };
    }

    /// <summary>Splits into statements: ';'-terminated for the attribute/comment family (strings may span lines), one line otherwise.</summary>
    private static IEnumerable<string> Statements(string text)
    {
        var sb = new StringBuilder();
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (sb.Length > 0)
            {
                sb.Append('\n').Append(line);
                if (Ends(sb.ToString())) { yield return sb.ToString(); sb.Clear(); }
                continue;
            }
            if (line.Length == 0) continue;
            if (Multi().IsMatch(line))
            {
                if (Ends(line)) yield return line; else sb.Append(line);
            }
            else yield return line;
        }
        if (sb.Length > 0) yield return sb.ToString();

        static bool Ends(string s)
        {
            var q = false;
            for (var i = 0; i < s.Length; i++)
            {
                if (s[i] == '\\' && q) { i++; continue; }
                if (s[i] == '"') q = !q;
            }
            return !q && s.TrimEnd().EndsWith(';');
        }
    }

    [GeneratedRegex(@"^(BA_DEF_DEF_REL_|BA_DEF_REL_|BA_DEF_DEF_|BA_DEF_|BA_REL_|BA_|CM_|BO_TX_BU_)\s")] private static partial Regex Multi();
    [GeneratedRegex(@"^BU_\s*:\s*(?<n>.*)$")] private static partial Regex BuLine();
    [GeneratedRegex(@"^BO_\s+(?<id>\d+)\s+(?<name>\S+?)\s*:")] private static partial Regex BoLine();
    [GeneratedRegex(@"^BO_TX_BU_\s+(?<id>\d+)\s*:\s*(?<n>[^;]*);", RegexOptions.Singleline)] private static partial Regex TxBu();
    [GeneratedRegex(@"^CM_\s+BU_\s+(?<n>\S+)\s+""(?<t>(?:\\.|[^""])*)""\s*;", RegexOptions.Singleline)] private static partial Regex NodeComment();
    [GeneratedRegex(@"^BA_DEF_\s+(?:(?<scope>BU_|BO_|SG_|EV_)\s+)?""(?<name>[^""]+)""\s+(?<type>INT|HEX|FLOAT|STRING|ENUM)\s*(?<args>.*)$", RegexOptions.Singleline)] private static partial Regex AttrDef();
    [GeneratedRegex(@"^BA_DEF_DEF_\s+""(?<name>[^""]+)""\s+(?<v>.+)$", RegexOptions.Singleline)] private static partial Regex AttrDefault();
    [GeneratedRegex(@"^BA_\s+""(?<name>[^""]+)""\s+(?:(?<kind>BU_|BO_|SG_|EV_)\s+)?(?<rest>.+)$", RegexOptions.Singleline)] private static partial Regex AttrValue();
    [GeneratedRegex("\"((?:\\\\.|[^\"])*)\"")] private static partial Regex Quoted();
}
