using System.Globalization;
using System.Text;
using System.Xml;

namespace RbgCanInsight.TraceIO;

/// <summary>Keep untrusted catalog metadata readable in XML and CSV outputs.
/// XML 1.0 disallows many control characters and unpaired UTF-16 surrogates;
/// emit visible \\uXXXX escapes instead of silently losing source characters.
/// Valid supplementary Unicode pairs, CR, LF and TAB are preserved unchanged.
/// </summary>
internal static class ExportTextSafety
{
    /// <summary>Protect formula-looking and numeric identifier text when Excel
    /// opens a quoted CSV field. Caller must insert the apostrophe inside quotes.</summary>
    public static bool NeedsSpreadsheetTextProtection(string value)
    {
        var trimmed = value.TrimStart(' ', '\t', '\r', '\n');
        if (trimmed.Length == 0) return false;
        if (trimmed[0] is '=' or '+' or '-' or '@') return true;
        return double.TryParse(trimmed, NumberStyles.Float,
            CultureInfo.InvariantCulture, out _);
    }

    public static string EscapeInvalidXmlCharacters(string value)
    {
        StringBuilder? builder = null;
        var segmentStart = 0;
        for (var index = 0; index < value.Length; index++)
        {
            var current = value[index];
            if (char.IsHighSurrogate(current) &&
                index + 1 < value.Length && char.IsLowSurrogate(value[index + 1]))
            {
                index++;
                continue;
            }
            if (XmlConvert.IsXmlChar(current))
                continue;

            builder ??= new StringBuilder(value.Length + 16);
            builder.Append(value, segmentStart, index - segmentStart);
            builder.Append("\\u");
            builder.Append(((int)current).ToString("X4", CultureInfo.InvariantCulture));
            segmentStart = index + 1;
        }
        if (builder is null) return value;
        builder.Append(value, segmentStart, value.Length - segmentStart);
        return builder.ToString();
    }
}
