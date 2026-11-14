using System.Globalization;
using System.Xml.Linq;

namespace RbgCanInsight.DiagData;

/// <summary>
/// ODX DIAG-CODED-TYPE of kind MIN-MAX-LENGTH-TYPE: a byte string (A_ASCIISTRING, A_BYTEFIELD, ...) whose length is
/// bounded by MIN-LENGTH / MAX-LENGTH and ended by a TERMINATION (ZERO, HEX-FF or END-OF-PDU).
/// This is a stand-alone extractor: the fixed-offset DID field model has no place for fields without a
/// BIT-LENGTH, so such parameters are not placed into DID layouts yet.
/// </summary>
public sealed record OdxVariableLengthType(string BaseDataType, int MinLength, int? MaxLength, string Termination)
{
    public static OdxVariableLengthType? Parse(XElement codedType)
    {
        ArgumentNullException.ThrowIfNull(codedType);
        var kind = codedType.Attributes().FirstOrDefault(a => a.Name.LocalName == "type")?.Value;
        if (kind is null || !kind.EndsWith("MIN-MAX-LENGTH-TYPE", StringComparison.OrdinalIgnoreCase)) return null;
        static int? Int(XElement e, string name) =>
            int.TryParse(e.Elements().FirstOrDefault(c => c.Name.LocalName == name)?.Value.Trim(),
                NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v >= 0 ? v : null;
        var min = Int(codedType, "MIN-LENGTH");
        if (min is null) return null; // MIN-LENGTH is mandatory in ODX
        var termination = codedType.Elements().FirstOrDefault(c => c.Name.LocalName == "TERMINATION")?.Value.Trim()
            .ToUpperInvariant() ?? string.Empty;
        return new((string?)codedType.Attribute("BASE-DATA-TYPE") ?? string.Empty, min.Value,
            Int(codedType, "MAX-LENGTH"), termination);
    }

    /// <summary>Extracts the value bytes starting at <paramref name="data"/>[0]. <paramref name="consumed"/> includes
    /// the terminator byte. On failure <paramref name="issue"/> says why and nothing is guessed.</summary>
    public bool TryExtract(ReadOnlySpan<byte> data, out ReadOnlySpan<byte> value, out int consumed, out string issue)
    {
        value = default; consumed = 0; issue = string.Empty;
        byte? terminator = Termination switch { "ZERO" => (byte)0x00, "HEX-FF" => (byte)0xFF, _ => null };
        int length;
        if (terminator is { } t)
        {
            var index = data[..Math.Min(data.Length, MaxLength is { } m ? m + 1 : data.Length)].IndexOf(t);
            if (index < 0)
            {
                issue = $"No {Termination} terminator within {(MaxLength is null ? "the data" : $"MAX-LENGTH {MaxLength}")}.";
                return false;
            }
            length = index; consumed = index + 1;
        }
        else if (Termination == "END-OF-PDU")
        {
            if (MaxLength is { } m2 && data.Length > m2) { issue = $"{data.Length} byte(s) exceed MAX-LENGTH {m2}."; return false; }
            length = data.Length; consumed = data.Length;
        }
        else
        {
            issue = $"Unsupported or missing TERMINATION '{Termination}'.";
            return false;
        }
        if (length < MinLength) { issue = $"{length} byte(s) are below MIN-LENGTH {MinLength}."; return false; }
        value = data[..length];
        return true;
    }
}
