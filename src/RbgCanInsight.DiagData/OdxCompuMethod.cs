using System.Globalization;
using System.Xml.Linq;

namespace RbgCanInsight.DiagData;

/// <summary>Kind of an ODX COMPU-METHOD as declared by its CATEGORY.</summary>
public enum OdxCompuCategory { Identical, Linear, ScaleLinear, RatFunc, ScaleRatFunc, TextTable, TabIntp, Unsupported }

/// <summary>Result of converting an internal (coded) value with an ODX COMPU-METHOD.</summary>
public readonly record struct OdxPhysicalValue(double? Number, string? Text, bool InRange);

/// <summary>
/// Evaluator for ODX COMPU-INTERNAL-TO-PHYS (ISO 22901-1): IDENTICAL, LINEAR, SCALE-LINEAR, RAT-FUNC,
/// SCALE-RAT-FUNC, TEXTTABLE and TAB-INTP (piecewise-linear interpolation between table points, no extrapolation),
/// including piecewise scales with open/closed limits and the COMPU-DEFAULT-VALUE. COMPUCODE and unknown
/// categories are reported as <see cref="OdxCompuCategory.Unsupported"/> instead of being approximated. Rational coefficients are listed in ascending powers of x
/// (V[0] + V[1]*x + V[2]*x^2 ...), as ODX defines them.
/// </summary>
public sealed class OdxCompuMethod
{
    private sealed record Scale(
        double? Lower, bool LowerClosed, double? Upper, bool UpperClosed,
        double[] Numerator, double[] Denominator, string? Text, double? ConstantNumber);

    private readonly List<Scale> _scales = [];
    private string? _defaultText;
    private double? _defaultNumber;

    public OdxCompuCategory Category { get; private set; } = OdxCompuCategory.Unsupported;
    public string RawCategory { get; private set; } = string.Empty;

    /// <summary>True when a limit or coefficient could not be parsed; such a method never converts.</summary>
    public bool Malformed { get; private set; }

    /// <summary>Canonical text of the method content (names, ids and descriptions excluded). Two methods with
    /// different signatures are different definitions even when their short names match.</summary>
    public string Signature { get; private set; } = string.Empty;

    /// <summary>Parses a COMPU-METHOD element (or any element that contains one, e.g. a DATA-OBJECT-PROP).</summary>
    public static OdxCompuMethod? Parse(XElement dopOrMethod)
    {
        ArgumentNullException.ThrowIfNull(dopOrMethod);
        var method = dopOrMethod.Name.LocalName == "COMPU-METHOD"
            ? dopOrMethod
            : dopOrMethod.Descendants().FirstOrDefault(e => e.Name.LocalName == "COMPU-METHOD");
        if (method is null) return null;

        var result = new OdxCompuMethod();
        result.RawCategory = method.Elements().FirstOrDefault(e => e.Name.LocalName == "CATEGORY")?.Value.Trim() ?? string.Empty;
        result.Category = result.RawCategory.ToUpperInvariant() switch
        {
            "IDENTICAL" => OdxCompuCategory.Identical,
            "LINEAR" => OdxCompuCategory.Linear,
            "SCALE-LINEAR" => OdxCompuCategory.ScaleLinear,
            "RAT-FUNC" => OdxCompuCategory.RatFunc,
            "SCALE-RAT-FUNC" => OdxCompuCategory.ScaleRatFunc,
            "TEXTTABLE" => OdxCompuCategory.TextTable,
            "TAB-INTP" => OdxCompuCategory.TabIntp,
            _ => OdxCompuCategory.Unsupported
        };

        result.Signature = string.Concat(method.DescendantsAndSelf()
            .Where(e => e.Name.LocalName is not ("SHORT-NAME" or "LONG-NAME" or "DESC" or "SHORT-LABEL"))
            .Select(e => $"<{e.Name.LocalName} {(string?)e.Attribute("INTERVAL-TYPE")}>{(e.HasElements ? "" : e.Value.Trim())}"));
        var toPhys = method.Elements().FirstOrDefault(e => e.Name.LocalName == "COMPU-INTERNAL-TO-PHYS");
        if (toPhys is null) return result;
        foreach (var scale in toPhys.Descendants().Where(e => e.Name.LocalName == "COMPU-SCALE"))
        {
            var (lo, loClosed, loBad) = Limit(scale, "LOWER-LIMIT");
            var (hi, hiClosed, hiBad) = Limit(scale, "UPPER-LIMIT");
            if (loBad || hiBad) result.Malformed = true;
            // TEXTTABLE codes are single points unless a range is declared: an omitted UPPER-LIMIT means
            // "equal to LOWER-LIMIT" (as OdxDeclaredValueReader always read it), never "unbounded above".
            if (result.Category == OdxCompuCategory.TextTable && lo is not null && !hiBad &&
                !scale.Elements().Any(e => e.Name.LocalName == "UPPER-LIMIT"))
            {
                hi = lo;
                hiClosed = true;
            }
            var rational = scale.Elements().FirstOrDefault(e => e.Name.LocalName == "COMPU-RATIONAL-COEFFS");
            var constant = scale.Elements().FirstOrDefault(e => e.Name.LocalName == "COMPU-CONST");
            string? text = null;
            double? number = null;
            if (constant is not null)
            {
                text = constant.Elements().FirstOrDefault(e => e.Name.LocalName == "VT")?.Value.Trim();
                number = ParseNumber(constant.Elements().FirstOrDefault(e => e.Name.LocalName == "V")?.Value);
            }
            result._scales.Add(new Scale(lo, loClosed, hi, hiClosed,
                Coefficients(rational, "COMPU-NUMERATOR"), Coefficients(rational, "COMPU-DENOMINATOR"), text, number));
        }
        // CATEGORY is mandatory in ISO 22901-1, but files without it were always read as affine scaling
        // (OdxCatalogReader.ResolveLinearConversion). Keep that: a blank category whose scales are all
        // affine coefficient pairs (<=2 numerator terms, <=1 denominator term) is evaluated as RAT-FUNC.
        // RawCategory stays empty so provenance still shows the file did not declare a category; any other
        // shape (constants, higher polynomials, unknown category names) stays Unsupported.
        if (result.RawCategory.Length == 0 && result._scales.Count > 0 &&
            result._scales.All(s => s.Text is null && s.ConstantNumber is null &&
                                    s.Numerator.Length == 2 && s.Denominator.Length <= 1))
            result.Category = OdxCompuCategory.RatFunc;
        var def = toPhys.Elements().FirstOrDefault(e => e.Name.LocalName == "COMPU-DEFAULT-VALUE");
        if (def is not null)
        {
            result._defaultText = def.Elements().FirstOrDefault(e => e.Name.LocalName == "VT")?.Value.Trim();
            result._defaultNumber = ParseNumber(def.Elements().FirstOrDefault(e => e.Name.LocalName == "V")?.Value);
        }
        return result;
    }

    public int ScaleCount => _scales.Count;

    /// <summary>
    /// Converts an internal value. Returns null when the method is unsupported or malformed
    /// (e.g. an unparsable coefficient or a zero denominator); otherwise a value whose
    /// <c>InRange</c> flag is false when no scale covered the input and only the default (if any) applied.
    /// </summary>
    public OdxPhysicalValue? Convert(double internalValue)
    {
        if (Category == OdxCompuCategory.Unsupported || Malformed || double.IsNaN(internalValue)) return null;
        if (Category == OdxCompuCategory.Identical) return new OdxPhysicalValue(internalValue, null, true);
        if (Category == OdxCompuCategory.TabIntp) return Interpolate(internalValue);

        foreach (var scale in _scales)
        {
            if (!Covers(scale, internalValue)) continue;
            if (Category == OdxCompuCategory.TextTable)
            {
                if (scale.Text is null && scale.ConstantNumber is null) return null;
                return new OdxPhysicalValue(scale.ConstantNumber, scale.Text, true);
            }
            if (scale.Numerator.Length == 0 || scale.Numerator.Any(double.IsNaN) ||
                scale.Denominator.Any(double.IsNaN)) return null;
            var num = Polynomial(scale.Numerator, internalValue);
            var den = scale.Denominator.Length == 0 ? 1d : Polynomial(scale.Denominator, internalValue);
            if (Math.Abs(den) <= double.Epsilon || !double.IsFinite(num / den)) return null;
            return new OdxPhysicalValue(num / den, null, true);
        }
        if (_defaultText is not null || _defaultNumber is not null)
            return new OdxPhysicalValue(_defaultNumber, _defaultText, false);
        return new OdxPhysicalValue(null, null, false);
    }

    /// <summary>TAB-INTP: each COMPU-SCALE is a table point (LOWER-LIMIT = internal x, COMPU-CONST/V = physical y).
    /// y = y0 + (y1 - y0) * (x - x0) / (x1 - x0) between the neighbouring points; outside the table there is no
    /// extrapolation (not in range). Returns null for a malformed table (missing/duplicate x, non-numeric y).</summary>
    private OdxPhysicalValue? Interpolate(double x)
    {
        if (_scales.Count == 0 || _scales.Any(s => s.Lower is null || s.ConstantNumber is null)) return null;
        var points = _scales.OrderBy(s => s.Lower!.Value).ToArray();
        for (var i = 1; i < points.Length; i++)
            if (points[i].Lower == points[i - 1].Lower) return null;
        if (x < points[0].Lower!.Value || x > points[^1].Lower!.Value)
            return new OdxPhysicalValue(null, null, false);
        for (var i = 0; i < points.Length; i++)
        {
            var x0 = points[i].Lower!.Value; var y0 = points[i].ConstantNumber!.Value;
            if (x == x0) return new OdxPhysicalValue(y0, null, true);
            if (i + 1 >= points.Length) break;
            var x1 = points[i + 1].Lower!.Value; var y1 = points[i + 1].ConstantNumber!.Value;
            if (x > x0 && x < x1)
            {
                var y = y0 + (y1 - y0) * (x - x0) / (x1 - x0);
                return double.IsFinite(y) ? new OdxPhysicalValue(y, null, true) : null;
            }
        }
        return null;
    }

    /// <summary>COMPU-DEFAULT-VALUE text or number when the file declares one.</summary>
    public (string? Text, double? Number)? DefaultValue =>
        _defaultText is null && _defaultNumber is null ? null : (_defaultText, _defaultNumber);

    private static bool Covers(Scale scale, double x)
    {
        if (scale.Lower is { } lo && (scale.LowerClosed ? x < lo : x <= lo)) return false;
        if (scale.Upper is { } hi && (scale.UpperClosed ? x > hi : x >= hi)) return false;
        return true;
    }

    private static double Polynomial(double[] coefficients, double x)
    {
        double result = 0, power = 1;
        foreach (var c in coefficients) { result += c * power; power *= x; }
        return result;
    }

    private static double[] Coefficients(XElement? rational, string listName)
    {
        var list = rational?.Elements().FirstOrDefault(e => e.Name.LocalName == listName);
        if (list is null) return [];
        return list.Elements().Where(e => e.Name.LocalName == "V")
            .Select(e => ParseNumber(e.Value) ?? double.NaN).ToArray();
    }

    private static (double? Value, bool Closed, bool Malformed) Limit(XElement scale, string name)
    {
        var element = scale.Elements().FirstOrDefault(e => e.Name.LocalName == name);
        if (element is null) return (null, true, false);
        // INTERVAL-TYPE defaults to CLOSED; INFINITE means no bound at all.
        var type = ((string?)element.Attribute("INTERVAL-TYPE"))?.Trim().ToUpperInvariant();
        if (type == "INFINITE") return (null, true, false);
        var value = ParseNumber(element.Value);
        // A present but unreadable bound must not silently widen the scale to infinity.
        return (value, type != "OPEN", value is null);
    }

    internal static double? ParseNumber(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = text.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            // Unsigned parse: 0xFFFFFFFFFFFFFFFF must never wrap to -1. Values a double cannot hold exactly
            // (> 2^53 - 1) are reported as unreadable (null) so callers flag them instead of using a rounded number.
            if (ulong.TryParse(text.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var hex) &&
                hex <= 9007199254740991UL)
                return hex;
            return null;
        }
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) &&
               double.IsFinite(value) ? value : null;
    }
}
