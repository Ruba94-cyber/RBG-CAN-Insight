using System.Globalization;
using System.Text.RegularExpressions;
using RbgCanInsight.Domain;

namespace RbgCanInsight.Analysis;

/// <summary>
/// The Trace search grammar's combinators and its "advanced" terms. Space-separated terms are ANDed,
/// a standalone <c>OR</c> (or <c>|</c>) starts a new alternative group, and a <c>|</c> inside one term
/// ("100|200") offers alternatives for that term. A leading <c>-</c> negates a term.
/// Advanced terms (all hex unless noted): <c>id:100..1FF</c>, <c>id&gt;=100</c>; byte relations
/// <c>d0=5A</c>, <c>d2&gt;=10</c>, <c>d1!=FF</c>; bit masks <c>d0&amp;0F=05</c> and bit patterns
/// <c>d0=b1xxxxxx0</c>; length <c>dlc=8</c> (decimal); time window <c>t:1.5..3</c> or <c>t&gt;=2</c>
/// (seconds from the first frame). Every other term is delegated to the host's basic-term factory
/// (channel, message name, payload, uds, frame kinds, plain IDs), so existing queries mean the same.
/// </summary>
public static class FrameQuery
{
    private const string Ops = @"==|!=|<=|>=|=|<|>";
    private static readonly RegexOptions Opt = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    private static readonly Regex IdRange = new(@"^id:(?<a>(?:0x)?[0-9a-f]+)\.\.(?<b>(?:0x)?[0-9a-f]+)$", Opt);
    private static readonly Regex IdRelation = new($@"^id(?<op>:|{Ops})(?<v>(?:0x)?[0-9a-f]+)$", Opt);
    private static readonly Regex ByteRelation = new(
        $@"^d(?<n>\d{{1,2}})(?:&(?<m>(?:0x)?[0-9a-f]{{1,2}}))?(?<op>{Ops})(?<v>(?:0x)?[0-9a-f]{{1,2}}|0?b[01x]{{8}})$", Opt);
    private static readonly Regex DlcRelation = new($@"^dlc(?<op>:|{Ops})(?<v>\d{{1,2}})$", Opt);
    private static readonly Regex TimeRange = new(@"^t:(?<a>\d*\.?\d*)\.\.(?<b>\d*\.?\d*)$", Opt);
    private static readonly Regex TimeRelation = new(@"^t(?<op>:|>=|<=|>|<)(?<v>\d+\.?\d*|\.\d+)$", Opt);
    private static readonly Regex DirRelation = new(@"^dir:(?<v>rx|tx)$", Opt);
    private static readonly Regex AdvancedShape = new($@"^(id|d\d{{1,2}}(&[^=<>!]*)?|dlc|dir|t)(:|{Ops}|&)", Opt);

    /// <summary>True when the term starts like an advanced term (it may still be malformed).</summary>
    public static bool LooksAdvanced(string term) => AdvancedShape.IsMatch(term);

    /// <summary>Splits a query into OR groups of raw terms (terms keep their leading "-").</summary>
    public static IReadOnlyList<IReadOnlyList<string>> SplitGroups(string query)
    {
        var groups = new List<IReadOnlyList<string>>();
        var current = new List<string>();
        foreach (var token in (query ?? string.Empty).Split(' ',
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (token == "OR" || token == "|")
            {
                if (current.Count > 0) groups.Add(current);
                current = [];
            }
            else current.Add(token);
        }

        if (current.Count > 0) groups.Add(current);
        return groups;
    }

    /// <summary>Compiles the full query. <paramref name="basicTerm"/> receives every non-advanced term
    /// (already stripped of "-" and split on "|").</summary>
    public static Func<CanFrame, bool> Compile(
        string query, long originNanoseconds, Func<string, Func<CanFrame, bool>> basicTerm)
    {
        var groups = SplitGroups(query)
            .Select(group => group.Select(raw => CompileTerm(raw, originNanoseconds, basicTerm)).ToArray())
            .Where(group => group.Length > 0)
            .ToArray();
        if (groups.Length == 0) return _ => true;
        if (groups.Length == 1)
        {
            var only = groups[0];
            return frame =>
            {
                foreach (var predicate in only) if (!predicate(frame)) return false;
                return true;
            };
        }

        return frame =>
        {
            foreach (var group in groups)
            {
                var all = true;
                foreach (var predicate in group)
                    if (!predicate(frame)) { all = false; break; }
                if (all) return true;
            }

            return false;
        };
    }

    private static Func<CanFrame, bool> CompileTerm(
        string raw, long origin, Func<string, Func<CanFrame, bool>> basicTerm)
    {
        var negate = raw.Length > 1 && raw[0] == '-';
        var term = negate ? raw[1..] : raw;
        Func<CanFrame, bool> predicate;
        if (term.Contains('|') && !term.StartsWith("payload:", StringComparison.OrdinalIgnoreCase)
            && term.Split('|', StringSplitOptions.RemoveEmptyEntries) is { Length: > 1 } alternatives)
        {
            var inner = alternatives.Select(item => CompileSingle(item, origin, basicTerm)).ToArray();
            predicate = frame =>
            {
                foreach (var candidate in inner) if (candidate(frame)) return true;
                return false;
            };
        }
        else predicate = CompileSingle(term, origin, basicTerm);

        return negate ? frame => !predicate(frame) : predicate;
    }

    private static Func<CanFrame, bool> CompileSingle(
        string term, long origin, Func<string, Func<CanFrame, bool>> basicTerm)
    {
        if (term.Length == 0) return _ => true;
        if (LooksAdvanced(term))
        {
            if (TryParseAdvancedTerm(term, origin, out var predicate, out _) && predicate is not null)
                return predicate;
            // Malformed advanced term: match nothing instead of silently meaning something else.
            return _ => false;
        }

        return basicTerm(term);
    }

    /// <summary>Parses one advanced term. Returns false with <paramref name="error"/> set when the term
    /// looks advanced but is malformed; returns false with a null error when it is not advanced at all.</summary>
    public static bool TryParseAdvancedTerm(
        string term, long originNanoseconds, out Func<CanFrame, bool>? predicate, out string? error)
    {
        predicate = null;
        error = null;
        if (!LooksAdvanced(term)) return false;

        if (DirRelation.Match(term) is { Success: true } dir)
        {
            // "dir:rx" means "not marked as transmitted": received, or direction not recorded in the log.
            var wantTx = dir.Groups["v"].Value.Equals("tx", StringComparison.OrdinalIgnoreCase);
            predicate = frame => frame.IsTransmit == wantTx;
            return true;
        }

        if (IdRange.Match(term) is { Success: true } range)
        {
            var low = ParseHex(range.Groups["a"].Value);
            var high = ParseHex(range.Groups["b"].Value);
            if (low is null || high is null) return Fail($"\"{term}\": the ID range needs hexadecimal bounds.", out error);
            var (a, b) = (Math.Min(low.Value, high.Value), Math.Max(low.Value, high.Value));
            predicate = frame => frame.Identifier >= a && frame.Identifier <= b;
            return true;
        }

        if (IdRelation.Match(term) is { Success: true } id)
        {
            var value = ParseHex(id.Groups["v"].Value);
            if (value is null) return Fail($"\"{term}\": the ID must be hexadecimal.", out error);
            var compare = Comparer(id.Groups["op"].Value);
            var v = value.Value;
            predicate = frame => compare(frame.Identifier.CompareTo(v));
            return true;
        }

        if (DlcRelation.Match(term) is { Success: true } dlc)
        {
            var compare = Comparer(dlc.Groups["op"].Value);
            var v = int.Parse(dlc.Groups["v"].Value, CultureInfo.InvariantCulture);
            predicate = frame => compare(((int)frame.DataLength).CompareTo(v));
            return true;
        }

        if (ByteRelation.Match(term) is { Success: true } bytes)
        {
            var index = int.Parse(bytes.Groups["n"].Value, CultureInfo.InvariantCulture);
            if (index > 63) return Fail($"\"{term}\": byte index must be 0 to 63.", out error);
            var op = bytes.Groups["op"].Value;
            var valueText = bytes.Groups["v"].Value;
            var maskText = bytes.Groups["m"].Success ? bytes.Groups["m"].Value : null;
            byte mask = 0xFF;
            if (maskText is not null)
                mask = (byte)(ParseHex(maskText) ?? 0xFF);

            var isPattern = valueText.Contains('b') || valueText.Contains('B');
            if (isPattern)
            {
                if (op is not ("=" or "==" or "!="))
                    return Fail($"\"{term}\": a bit pattern can only be used with = or !=.", out error);
                var bits = valueText[^8..];
                byte patternMask = 0, patternValue = 0;
                for (var i = 0; i < 8; i++)
                {
                    var bit = (byte)(0x80 >> i);
                    var c = char.ToLowerInvariant(bits[i]);
                    if (c == 'x') continue;
                    patternMask |= bit;
                    if (c == '1') patternValue |= bit;
                }

                var equal = op != "!=";
                var effectiveMask = (byte)(mask & patternMask);
                predicate = frame => index < frame.DataLength &&
                                     (((frame.GetByte(index) & effectiveMask) == (patternValue & effectiveMask)) == equal);
                return true;
            }

            var number = ParseHex(valueText);
            if (number is null || number > 0xFF) return Fail($"\"{term}\": byte values are 00 to FF.", out error);
            var wanted = (byte)number.Value;
            var cmp = Comparer(op);
            if (maskText is not null)
            {
                if (op is not ("=" or "==" or "!="))
                    return Fail($"\"{term}\": a masked byte can only be compared with = or !=.", out error);
                predicate = frame => index < frame.DataLength &&
                                     cmp(((byte)(frame.GetByte(index) & mask)).CompareTo((byte)(wanted & mask)));
                return true;
            }

            predicate = frame => index < frame.DataLength && cmp(frame.GetByte(index).CompareTo(wanted));
            return true;
        }

        if (TimeRange.Match(term) is { Success: true } time)
        {
            var from = ParseSeconds(time.Groups["a"].Value);
            var to = ParseSeconds(time.Groups["b"].Value);
            if (from is null && to is null) return Fail($"\"{term}\": give a start and/or end time in seconds.", out error);
            var lowNs = from is null ? long.MinValue : originNanoseconds + (long)(from.Value * 1e9);
            var highNs = to is null ? long.MaxValue : originNanoseconds + (long)(to.Value * 1e9);
            predicate = frame => frame.TimestampNanoseconds >= lowNs && frame.TimestampNanoseconds <= highNs;
            return true;
        }

        if (TimeRelation.Match(term) is { Success: true } rel)
        {
            var seconds = ParseSeconds(rel.Groups["v"].Value);
            if (seconds is null) return Fail($"\"{term}\": time must be a number of seconds.", out error);
            var op = rel.Groups["op"].Value;
            var ns = originNanoseconds + (long)(seconds.Value * 1e9);
            var cmp = Comparer(op == ":" ? ">=" : op);
            predicate = frame => cmp(frame.TimestampNanoseconds.CompareTo(ns));
            return true;
        }

        return Fail($"\"{term}\" is not a valid ID, byte, length or time condition.", out error);
    }

    private static bool Fail(string message, out string? error)
    {
        error = message;
        return false;
    }

    private static Func<int, bool> Comparer(string op) => op switch
    {
        "<" => c => c < 0,
        "<=" => c => c <= 0,
        ">" => c => c > 0,
        ">=" => c => c >= 0,
        "!=" => c => c != 0,
        _ => c => c == 0 // "=", "==", ":"
    };

    private static uint? ParseHex(string text)
    {
        var hex = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? text[2..] : text;
        return uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    private static double? ParseSeconds(string text) =>
        text.Length > 0 && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
        && double.IsFinite(value) ? value : null;
}
