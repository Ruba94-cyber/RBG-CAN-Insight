using System.Globalization;
using System.Xml.Linq;

namespace RbgCanInsight.DiagData;

/// <summary>Source-declared coded value / interval, without a claim that the
/// value is physically decodable from an arbitrary response layout.</summary>
public sealed record DiagnosticDeclaredValueMeaning(
    string LowerRaw, string UpperRaw, string Meaning, string Description,
    string Status)
{
    // Readers classify semantically equal hexadecimal/decimal boundaries
    // as exact even when the original source strings differ.
    public bool IsExact => Status == "Exact code";
}

/// <summary>
/// Extract textual ODX COMPU-SCALE meanings losslessly: ranges remain ranges,
/// absent bounds remain unknown, and decimal/noninteger values never silently
/// become integer code zero. Only exact int64 codes may populate legacy
/// DiagnosticEnumValue collections used for coded value lookup.
/// </summary>
public static class OdxDeclaredValueReader
{
    public static IReadOnlyList<DiagnosticDeclaredValueMeaning> Read(XElement dop)
    {
        ArgumentNullException.ThrowIfNull(dop);
        var result = new List<DiagnosticDeclaredValueMeaning>();
        var method = dop.Descendants().FirstOrDefault(node =>
            node.Name.LocalName == "COMPU-METHOD");
        if (method is null) return result;
        var category = method.Elements().FirstOrDefault(node =>
            node.Name.LocalName == "CATEGORY")?.Value.Trim() ?? string.Empty;
        var isTextTable = category.Contains("TEXTTABLE",
            StringComparison.OrdinalIgnoreCase);

        foreach (var scale in method.Descendants().Where(node =>
                     node.Name.LocalName == "COMPU-SCALE"))
        {
            var constant = scale.Elements().FirstOrDefault(node =>
                node.Name.LocalName == "COMPU-CONST");
            var meaning = constant?.Elements().FirstOrDefault(node =>
                node.Name.LocalName == "VT")?.Value.Trim() ??
                (isTextTable
                    ? scale.Elements().FirstOrDefault(node =>
                        node.Name.LocalName == "SHORT-LABEL")?.Value.Trim()
                    : null) ?? string.Empty;
            // Preserve incomplete TEXTTABLE entries with raw boundaries but
            // no translation. Ordinary LINEAR segments are not text mappings.
            if (string.IsNullOrWhiteSpace(meaning) && !isTextTable) continue;
            var lower = scale.Elements().FirstOrDefault(node =>
                node.Name.LocalName == "LOWER-LIMIT")?.Value.Trim() ?? string.Empty;
            var upper = scale.Elements().FirstOrDefault(node =>
                node.Name.LocalName == "UPPER-LIMIT")?.Value.Trim() ?? lower;
            var description = scale.Elements().FirstOrDefault(node =>
                node.Name.LocalName == "DESC")?.Value.Trim() ?? string.Empty;
            var status = string.IsNullOrWhiteSpace(meaning)
                ? "Missing text meaning"
                : !isTextTable
                    ? "Non-texttable scale: decoding unverified"
                    : lower.Length == 0 || upper.Length == 0
                    ? "Raw boundary absent"
                    : string.Equals(lower, upper, StringComparison.Ordinal)
                        ? "Exact code" : "Declared range";
            result.Add(new DiagnosticDeclaredValueMeaning(
                lower, upper, meaning, description, status));
        }
        return result;
    }

    public static IReadOnlyList<DiagnosticEnumValue> ExactIntegerValues(
        IReadOnlyList<DiagnosticDeclaredValueMeaning> meanings)
    {
        ArgumentNullException.ThrowIfNull(meanings);
        var result = new Dictionary<long, DiagnosticEnumValue>();
        var invalid = new HashSet<long>();
        foreach (var item in meanings)
        {
            if (item.Status != "Exact code" ||
                string.IsNullOrWhiteSpace(item.Meaning) ||
                !TryExactCodedInteger(item.LowerRaw, out var code) ||
                !TryExactCodedInteger(item.UpperRaw, out var upper) ||
                code != upper)
                continue;
            // Conflicting duplicate mappings are not a valid one-to-one enum.
            if (result.TryGetValue(code, out var previous) &&
                !string.Equals(previous.Text, item.Meaning, StringComparison.Ordinal))
            {
                invalid.Add(code);
                continue;
            }
            result.TryAdd(code, new DiagnosticEnumValue(code, item.Meaning, item.Description));
        }
        // An exact coded value that also belongs to a differently-labelled
        // declared interval is not an unambiguous enum. Keep both original
        // declarations for the PAR inspector/XLSX, but suppress the misleading
        // one-to-one decoded lookup.
        foreach (var range in meanings.Where(item => item.Status == "Declared range"))
        {
            if (!TryExactCodedInteger(range.LowerRaw, out var lower) ||
                !TryExactCodedInteger(range.UpperRaw, out var upper) ||
                lower > upper)
                continue;
            foreach (var pair in result)
                if (pair.Key >= lower && pair.Key <= upper &&
                    !string.Equals(pair.Value.Text, range.Meaning,
                        StringComparison.Ordinal))
                    invalid.Add(pair.Key);
        }
        foreach (var code in invalid) result.Remove(code);
        return result.Values.OrderBy(item => item.Value).ToArray();
    }

    private static bool TryExactCodedInteger(string raw, out long result)
    {
        if (long.TryParse(raw, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out result))
            return true;
        // CDD TEXTMAP can use explicit 0x literals. Parse as unsigned first:
        // a 64-bit hex pattern exceeding Int64.MaxValue must not wrap negative.
        if (raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
            ulong.TryParse(raw[2..], NumberStyles.AllowHexSpecifier,
                CultureInfo.InvariantCulture, out var hex) && hex <= (ulong)long.MaxValue)
        {
            result = (long)hex;
            return true;
        }
        result = 0;
        return false;
    }
}
