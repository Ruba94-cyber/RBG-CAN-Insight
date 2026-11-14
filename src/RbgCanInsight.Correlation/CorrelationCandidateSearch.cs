using System.Globalization;

namespace RbgCanInsight.Correlation;

/// <summary>
/// Source-aware, case-insensitive search grammar for offline UDS/CAN
/// correlations. Supports plain terms, typed filters and negative terms,
/// without interpreting source text as regex or automatically proving
/// a candidate identity.
/// </summary>
public static class CorrelationCandidateSearch
{
    /// <summary>Prepare search terms once for a complete candidate catalog.
    /// Avoid splitting and allocating the query once per row during filtering
    /// of large multi-DBC/UDS analyses.</summary>
    public static string[] Tokenize(string? query) =>
        string.IsNullOrWhiteSpace(query)
            ? []
            : query.Split(' ',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public static bool Matches(UdsCanCorrelationResult row, string? query) =>
        MatchesPrepared(row, Tokenize(query));

    public static bool MatchesPrepared(UdsCanCorrelationResult row,
        IReadOnlyList<string> preparedTokens)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(preparedTokens);

        foreach (var token in preparedTokens)
        {
            var exclude = token.StartsWith("-", StringComparison.Ordinal) && token.Length > 1;
            var positiveToken = exclude ? token[1..] : token;
            var matches = MatchesSingle(row, positiveToken);
            if (matches == exclude)
                return false;
        }
        return true;
    }

    private static bool MatchesSingle(UdsCanCorrelationResult row, string token)
    {
        var split = token.IndexOf(':');
        if (split > 0)
        {
            var key = token[..split].ToLowerInvariant();
            var value = token[(split + 1)..];
            if (value.Length == 0) return false;
            return key switch
            {
                "did" => MatchesHex(value, row.Candidate.Did.Did),
                "dbc" => Contains(row.Candidate.Database, value),
                "ecu" => Contains(row.Candidate.Did.Ecu, value),
                "field" => Contains(row.ObservedField, value),
                "signal" => Contains(row.SignalText, value),
                "message" => Contains(row.Candidate.Message.Name, value),
                "id" or "canid" => row.HasSourcePairs &&
                    row.SignalSourceIdentifier is { } wireId &&
                    MatchesHex(value, wireId),
                "dbc-id" or "definition-id" =>
                    MatchesHex(value, row.Candidate.Message.Identifier),
                "bus" or "channel" or "ch" => MatchChannel(row, value),
                "verdict" => Contains(row.Verdict.ToString(), value),
                "format" => MatchFrameFormat(row, value),
                "dbc-format" or "definition-format" =>
                    MatchDeclaredFrameFormat(row, value),
                "has" => MatchPresence(row, value),
                _ => MatchesFreeText(row, token)
            };
        }
        return MatchShorthandChannel(row, token) || MatchesFreeText(row, token);
    }

    private static bool MatchesFreeText(UdsCanCorrelationResult row, string term) =>
        Contains(row.DidText, term) ||
        Contains(row.DidText.AsSpan(2).ToString(), term) ||
        Contains(row.DidName, term) ||
        Contains(row.Candidate.Did.Ecu, term) ||
        Contains(row.ObservedField, term) ||
        Contains(row.Candidate.Database, term) ||
        Contains(row.SignalText, term) ||
        Contains(row.SignalSourceText, term) ||
        Contains($"DBC 0x{row.Candidate.Message.Identifier:X}", term) ||
        Contains(row.Verdict.ToString(), term) ||
        Contains(row.ConfidenceText, term);

    private static bool Contains(string? haystack, string needle) =>
        haystack?.Contains(needle, StringComparison.OrdinalIgnoreCase) == true;

    private static bool MatchesHex(string value, long actual)
    {
        var hex = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? value[2..] : value;
        return long.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture,
            out var parsed) && parsed == actual;
    }

    private static bool MatchChannel(UdsCanCorrelationResult row, string value)
    {
        if (!row.HasSourcePairs || row.SignalChannel < 0) return false;
        var normalized = value.Trim();
        if (normalized.StartsWith("CAN", StringComparison.OrdinalIgnoreCase))
            normalized = normalized[3..];
        else if (normalized.StartsWith("CH", StringComparison.OrdinalIgnoreCase))
            normalized = normalized[2..];
        return int.TryParse(normalized, NumberStyles.None,
            CultureInfo.InvariantCulture, out var oneBased) &&
            oneBased > 0 && row.SignalChannel + 1 == oneBased;
    }

    private static bool MatchShorthandChannel(UdsCanCorrelationResult row, string term)
    {
        if (term.StartsWith("CAN", StringComparison.OrdinalIgnoreCase) ||
            term.StartsWith("CH", StringComparison.OrdinalIgnoreCase))
            return MatchChannel(row, term);
        return false;
    }

    /// <summary>A CAN format filter means a *recorded, paired* source.
    /// The DBC's declaration is available separately with dbc-format:
    /// rather than impersonating missing physical-frame evidence.</summary>
    private static bool MatchFrameFormat(UdsCanCorrelationResult row, string value) =>
        row.HasSourcePairs &&
        row.SignalSourceIsExtended is { } recordedFormat &&
        MatchFormatValue(recordedFormat, value);

    private static bool MatchDeclaredFrameFormat(UdsCanCorrelationResult row, string value) =>
        MatchFormatValue(row.Candidate.Message.IsExtended, value);

    private static bool MatchFormatValue(bool isExtended, string value) =>
        value.ToLowerInvariant() switch
        {
            "standard" or "11" or "11-bit" => !isExtended,
            "extended" or "29" or "29-bit" => isExtended,
            _ => false
        };

    private static bool MatchPresence(UdsCanCorrelationResult row, string value) =>
        value.ToLowerInvariant() switch
        {
            "pairs" or "paired" or "source" => row.HasSourcePairs,
            "unpaired" or "missing" => !row.HasSourcePairs,
            _ => false
        };
}
