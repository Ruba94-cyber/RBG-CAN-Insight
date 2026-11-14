using System.Globalization;
using System.Xml.Linq;

namespace RbgCanInsight.DiagData;

/// <summary>
/// Reads source-backed CANdela TEXTTBL / TEXTMAP mappings. CDD text maps have
/// independent inclusive s/e bounds and multilingual TEXT/TUV labels; a
/// range is not an exact integer enum, and a missing bound is not code zero.
/// No CDD text table is applied to a PAR unless its datatype is referenced.
/// </summary>
public static class CddDeclaredValueReader
{
    public static IReadOnlyList<DiagnosticDeclaredValueMeaning> Read(XElement type)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (!string.Equals(type.Name.LocalName, "TEXTTBL",
                StringComparison.OrdinalIgnoreCase))
            return [];

        var values = new List<DiagnosticDeclaredValueMeaning>();
        foreach (var mapping in type.Elements().Where(element =>
                     string.Equals(element.Name.LocalName, "TEXTMAP",
                         StringComparison.OrdinalIgnoreCase)))
        {
            var lower = ((string?)mapping.Attribute("s") ?? string.Empty).Trim();
            var upper = ((string?)mapping.Attribute("e") ?? string.Empty).Trim();
            var meaning = Localized(mapping, "TEXT");
            var description = Localized(mapping, "DESC");
            // A declared code with no translated meaning is still source data.
            // Keep it in the workbook and mark the missing annotation.
            string status;
            if (string.IsNullOrWhiteSpace(meaning))
                status = "Missing text meaning";
            else if (lower.Length == 0 || upper.Length == 0)
                status = "Raw boundary absent";
            else if (!TryCodedInteger(lower, out var low) ||
                     !TryCodedInteger(upper, out var high))
                status = "Unsupported coded boundary (non-integer or outside Int64)";
            else if (low > high)
                status = "Invalid declared range";
            else
                status = low == high ? "Exact code" : "Declared range";

            values.Add(new DiagnosticDeclaredValueMeaning(
                lower, upper, meaning, description, status));
        }
        return values;
    }

    /// <summary>In CDD a TEXTMAP boundary may be an integer literal with a
    /// 0x-prefixed base-16 representation. Reject overflow and negative
    /// unsigned encodings rather than silently truncating a coded value.</summary>
    private static bool TryCodedInteger(string text, out long value)
    {
        if (long.TryParse(text, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out value))
            return true;
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
            ulong.TryParse(text[2..], NumberStyles.AllowHexSpecifier,
                CultureInfo.InvariantCulture, out var hex) && hex <= (ulong)long.MaxValue)
        {
            value = (long)hex;
            return true;
        }
        value = 0;
        return false;
    }

    private static string Localized(XElement owner, string elementName)
    {
        var element = owner.Elements().FirstOrDefault(child =>
            child.Name.LocalName.Equals(elementName,
                StringComparison.OrdinalIgnoreCase));
        if (element is null) return string.Empty;
        var translations = element.Descendants().Where(child =>
            child.Name.LocalName == "TUV").ToArray();
        var preferred = translations.FirstOrDefault(child =>
            ((string?)child.Attribute(XNamespace.Xml + "lang"))?
                .StartsWith("en", StringComparison.OrdinalIgnoreCase) == true);
        return (preferred ?? translations.FirstOrDefault())?.Value.Trim() ??
            element.Value.Trim();
    }
}
