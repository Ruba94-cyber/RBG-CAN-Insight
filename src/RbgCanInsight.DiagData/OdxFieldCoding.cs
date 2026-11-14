using System.Globalization;
using System.Xml.Linq;

namespace RbgCanInsight.DiagData;

/// <summary>Outcome class of evaluating a coded value against its ODX data-object-property.</summary>
public enum OdxConversionStatus
{
    /// <summary>A physical number (or TEXTTABLE text) was computed from the file's COMPU-METHOD.</summary>
    Converted,
    /// <summary>The raw value is outside a scale/INTERNAL-CONSTR/table or has no TEXTTABLE entry; flagged, never clamped.</summary>
    OutOfRange,
    /// <summary>COMPUCODE or an unknown category: raw kept, no number computed.</summary>
    Unsupported,
    /// <summary>The method exists but a limit/coefficient/table is unreadable or degenerate.</summary>
    Malformed,
    /// <summary>The DOP carries no COMPU-METHOD: the coded value is shown unconverted and flagged as uncertain.</summary>
    NoDefinition
}

public sealed record OdxConversionResult(
    OdxConversionStatus Status, string Physical, double? Numeric, string Meaning);

/// <summary>
/// What the ODX data-object-property of one PARAM declares about turning the coded value into a physical one:
/// the COMPU-METHOD, the INTERNAL-CONSTR validity ranges and where this came from. Attached to a
/// <see cref="DiagnosticFieldDefinition"/> so the UDS decoder can show raw and interpreted values together.
/// </summary>
public sealed class OdxFieldCoding : IEquatable<OdxFieldCoding>
{
    private sealed record Constraint(double? Lower, bool LowerClosed, double? Upper, bool UpperClosed, string Validity);

    private readonly List<Constraint> _constraints = [];
    private (double? Lower, bool LowerClosed, double? Upper, bool UpperClosed)? _internal;
    private bool _constraintMalformed;

    public OdxCompuMethod? Compu { get; private set; }
    /// <summary>ODX BASE-DATA-TYPE of the DIAG-CODED-TYPE (A_UINT32, A_FLOAT64, ...).</summary>
    public string CodedDataType { get; private set; } = string.Empty;
    public string DopName { get; private set; } = string.Empty;
    public string DimensionText { get; private set; } = string.Empty;
    /// <summary>Canonical text of everything that influences the conversion (used to detect conflicts).</summary>
    public string Signature { get; private set; } = string.Empty;

    public string Provenance =>
        $"ODX DOP '{DopName}' " + (Compu is null ? "has no COMPU-METHOD" : $"COMPU-METHOD {Compu.RawCategory}") +
        (DimensionText.Length > 0 ? $"; unit dimension {DimensionText}" : "");

    /// <summary>True when an evaluator can produce physical output (or flagged results) for this field.</summary>
    public bool IsEvaluable => Compu is { Category: not OdxCompuCategory.Unsupported, Malformed: false } && !_constraintMalformed;

    public static OdxFieldCoding Create(
        XElement dop, IReadOnlyDictionary<string, XElement> elementsById, string dopName)
    {
        var compu = OdxCompuMethod.Parse(dop);
        var coded = dop.Descendants().FirstOrDefault(e => e.Name.LocalName == "DIAG-CODED-TYPE");
        var dataType = (string?)coded?.Attribute("BASE-DATA-TYPE") ?? string.Empty;

        var unitId = dop.Descendants().FirstOrDefault(e => e.Name.LocalName == "UNIT-REF")?.Attribute("ID-REF")?.Value;
        var dimension = string.Empty;
        if (unitId is not null && elementsById.TryGetValue(unitId, out var unit))
        {
            var dimId = unit.Elements().FirstOrDefault(e => e.Name.LocalName == "PHYSICAL-DIMENSION-REF")
                ?.Attribute("ID-REF")?.Value;
            if (dimId is not null && elementsById.TryGetValue(dimId, out var dim))
                dimension = string.Join(' ', dim.Elements()
                    .Where(e => e.Name.LocalName.EndsWith("-EXP", StringComparison.Ordinal) &&
                                OdxCompuMethod.ParseNumber(e.Value) is { } n && n != 0)
                    .Select(e => $"{e.Name.LocalName[..^4].ToLowerInvariant()}^{e.Value.Trim()}"));
        }

        var coding = new OdxFieldCoding { Compu = compu, CodedDataType = dataType, DopName = dopName, DimensionText = dimension };
        var constr = dop.Elements().FirstOrDefault(e => e.Name.LocalName == "INTERNAL-CONSTR");
        var internalSignature = string.Empty;
        if (constr is not null)
        {
            internalSignature = string.Concat(constr.DescendantsAndSelf().Select(e =>
                $"<{e.Name.LocalName} {(string?)e.Attribute("INTERVAL-TYPE")} {(string?)e.Attribute("VALIDITY")}>" +
                (e.HasElements ? "" : e.Value.Trim())));
            var (lo, loClosed, loBad) = ConstraintLimit(constr, "LOWER-LIMIT");
            var (hi, hiClosed, hiBad) = ConstraintLimit(constr, "UPPER-LIMIT");
            if (loBad || hiBad) coding._constraintMalformed = true;
            if (lo is not null || hi is not null) coding._internal = (lo, loClosed, hi, hiClosed);
            foreach (var sc in constr.Descendants().Where(e => e.Name.LocalName == "SCALE-CONSTR"))
            {
                var (slo, sloClosed, sloBad) = ConstraintLimit(sc, "LOWER-LIMIT");
                var (shi, shiClosed, shiBad) = ConstraintLimit(sc, "UPPER-LIMIT");
                if (sloBad || shiBad) coding._constraintMalformed = true;
                coding._constraints.Add(new Constraint(slo, sloClosed, shi, shiClosed,
                    ((string?)sc.Attribute("VALIDITY") ?? "VALID").Trim().ToUpperInvariant()));
            }
        }
        coding.Signature = (compu is null ? "NO-COMPU" : compu.Signature) + "|" + internalSignature + "|" + dataType;
        return coding;
    }

    /// <summary>An absent bound is unbounded; a present but unparsable bound is reported as malformed
    /// (never silently widened to unbounded).</summary>
    private static (double? Value, bool Closed, bool Malformed) ConstraintLimit(XElement parent, string name)
    {
        var element = parent.Elements().FirstOrDefault(e => e.Name.LocalName == name);
        if (element is null) return (null, true, false);
        var type = ((string?)element.Attribute("INTERVAL-TYPE"))?.Trim().ToUpperInvariant();
        if (type == "INFINITE") return (null, true, false);
        var value = OdxCompuMethod.ParseNumber(element.Value);
        return (value, type != "OPEN", value is null);
    }

    private static bool Inside(double x, double? lo, bool loClosed, double? hi, bool hiClosed) =>
        !(lo is { } l && (loClosed ? x < l : x <= l)) && !(hi is { } h && (hiClosed ? x > h : x >= h));

    /// <summary>
    /// Evaluates a coded value. <paramref name="rawText"/> is the exact raw representation (hex) shown next to the
    /// result; nothing here clamps, rounds to a guess or invents text: a value without a defined meaning yields a
    /// flagged result whose <see cref="OdxConversionResult.Numeric"/> is null.
    /// </summary>
    public OdxConversionResult Evaluate(double internalValue, string rawText)
    {
        if (Compu is null)
            // Non-conformant ODX (COMPU-METHOD is mandatory in a DATA-OBJECT-PROP). The coded value is shown
            // unconverted, as before this evaluator existed, and the meaning says that no scaling is declared.
            return new(OdxConversionStatus.NoDefinition,
                internalValue.ToString("R", CultureInfo.InvariantCulture), internalValue,
                $"{Provenance}: physical scaling not declared; the coded value {rawText} is shown unconverted (uncertain).");

        if (_constraintMalformed)
            return new(OdxConversionStatus.Malformed, rawText, null,
                $"{Provenance}: INTERNAL-CONSTR/SCALE-CONSTR has an unreadable limit; raw value {rawText} kept, not converted.");

        if (_internal is { } range && !Inside(internalValue, range.Lower, range.LowerClosed, range.Upper, range.UpperClosed))
            return new(OdxConversionStatus.OutOfRange, $"out-of-range raw value {rawText}", null,
                $"{Provenance}: raw value {rawText} violates the INTERNAL-CONSTR limits; not converted.");
        foreach (var c in _constraints)
            if (c.Validity != "VALID" && Inside(internalValue, c.Lower, c.LowerClosed, c.Upper, c.UpperClosed))
                return new(OdxConversionStatus.OutOfRange, $"invalid raw value {rawText}", null,
                    $"{Provenance}: raw value {rawText} lies in a SCALE-CONSTR declared {c.Validity}; not converted.");

        if (Compu.Category == OdxCompuCategory.Unsupported)
            return new(OdxConversionStatus.Unsupported, rawText, null,
                $"{Provenance}: UNSUPPORTED method '{Compu.RawCategory}'; raw value {rawText} kept, no physical value computed.");

        var converted = Compu.Convert(internalValue);
        if (converted is null)
            return new(OdxConversionStatus.Malformed, rawText, null,
                $"{Provenance}: COMPU-METHOD is malformed or degenerate (limit/coefficient/table/denominator); raw value {rawText} kept.");

        var value = converted.Value;
        if (!value.InRange)
        {
            var kind = Compu.Category == OdxCompuCategory.TextTable ? "unmapped raw value" : "out-of-range raw value";
            if (Compu.DefaultValue is { } def)
            {
                var shown = def.Text ?? def.Number?.ToString("R", CultureInfo.InvariantCulture) ?? rawText;
                return new(OdxConversionStatus.OutOfRange, shown, null,
                    $"{Provenance}: {kind} {rawText}; shown value is the file's COMPU-DEFAULT-VALUE, not a conversion.");
            }
            return new(OdxConversionStatus.OutOfRange, $"{kind} {rawText}", null,
                $"{Provenance}: {kind} {rawText}; no scale or table entry covers it and the file declares no default.");
        }

        if (value.Text is not null)
            return new(OdxConversionStatus.Converted, value.Text, null, $"{Provenance}: raw {rawText} = '{value.Text}'.");
        return new(OdxConversionStatus.Converted, value.Number!.Value.ToString("R", CultureInfo.InvariantCulture),
            value.Number, $"{Provenance}: raw {rawText}.");
    }

    public bool Equals(OdxFieldCoding? other) => other is not null && Signature == other.Signature;
    public override bool Equals(object? obj) => Equals(obj as OdxFieldCoding);
    public override int GetHashCode() => Signature.GetHashCode(StringComparison.Ordinal);
}
