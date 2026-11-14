using System.Globalization;
using RbgCanInsight.Dbc;
using RbgCanInsight.Domain;

namespace RbgCanInsight.Analysis;

/// <summary>
/// Strict recorded CAN source filters for offline J1939 investigation.
/// SA and PGN are derived only from the *wire identifier* on an
/// extended frame, not from a catalog's arbitrary DBC source address.
/// </summary>
public static class RecordedCanSourceQuery
{
    public static bool IsSourceFilter(string term) =>
        term.StartsWith("sa:", StringComparison.OrdinalIgnoreCase) ||
        term.StartsWith("pgn:", StringComparison.OrdinalIgnoreCase);

    public static bool Matches(uint recordedIdentifier, bool isExtended, string term)
    {
        if (!isExtended || !IsSourceFilter(term))
            return false;
        var colon = term.IndexOf(':');
        if (!TryParseHex(term.AsSpan(colon + 1), out var expected))
            return false;

        if (term[0] is 's' or 'S')
            return expected <= byte.MaxValue &&
                   (recordedIdentifier & 0xFFu) == expected;
        if (term[0] is 'p' or 'P')
            return expected <= 0x3FFFFu &&
                   J1939.ExtractPgn(recordedIdentifier) == expected;
        return false;
    }

    private static bool TryParseHex(ReadOnlySpan<char> value, out uint parsed)
    {
        parsed = 0;
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            value = value[2..];
        // Avoid splitting/converting thousands of source query tokens
        // into temporary strings while filtering large signal catalogs.
        if (value.Length == 0 || value.Length > 8)
            return false;
        foreach (var digit in value)
            if (!Uri.IsHexDigit(digit))
                return false;
        return uint.TryParse(value, NumberStyles.AllowHexSpecifier,
            CultureInfo.InvariantCulture, out parsed);
    }
}
