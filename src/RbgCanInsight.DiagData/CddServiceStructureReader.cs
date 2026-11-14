using System.Globalization;
using System.Xml.Linq;

namespace RbgCanInsight.DiagData;

/// <summary>A service identifier resolved from the CANdela request PDU.</summary>
internal sealed record CddStructuralService(byte ServiceId, byte? SubFunction, int? Identifier);

/// <summary>
/// Structural reader of the CANdela service model. A DIAGINST/SERVICE carries
/// <c>tmplref</c> to a DCLSRVTMPL, whose <c>tmplref</c> names the PROTOCOLSERVICE
/// that defines the request PDU. The SID is the first constant request component
/// (CONSTCOMP spec='sid'); a fixed sub-function is a further constant (spec='sub',
/// or a second 'sid' constant as in "EcuReset - Power On"). The DID / local
/// identifier / session mode lives in a STATICVALUE owned by the DIAGINST and is
/// typed by the request's STATICCOMP (spec 'sub' or 'lid') through shstaticref.
/// </summary>
internal static class CddServiceStructureReader
{
    // ISO 14229 / ISO 14230 services whose single request discriminator is a data
    // identifier, not a sub-function: ReadDataByLocalIdentifier (0x21), ReadECUIdentification
    // (0x1A), ReadDataByIdentifier (0x22), WriteDataByIdentifier (0x2E),
    // WriteDataByLocalIdentifier (0x3B).
    private static readonly HashSet<byte> IdentifierServices = [0x1A, 0x21, 0x22, 0x2E, 0x3B];

    public static CddStructuralService? Resolve(
        XElement service, IReadOnlyDictionary<string, XElement> byId)
    {
        var protocolService = FindProtocolService(service, byId);
        var request = protocolService?.Elements().FirstOrDefault(e => e.Name.LocalName == "REQ");
        if (request is null) return null;

        var constants = request.Elements()
            .Where(e => e.Name.LocalName == "CONSTCOMP")
            .Select(e => (Spec: ((string?)e.Attribute("spec"))?.Trim().ToLowerInvariant() ?? "",
                          Value: CddCatalogReader.LongAttr(e, "v")))
            .ToArray();
        var sidIndex = Array.FindIndex(constants, c => c.Spec == "sid" && c.Value is >= 0 and <= 0xFF);
        if (sidIndex < 0) return null;
        var sid = (byte)constants[sidIndex].Value!.Value;

        byte? sub = null;
        foreach (var (spec, value) in constants.Where((_, i) => i != sidIndex))
        {
            if ((spec is "sub" or "sid") && value is >= 0 and <= 0xFF)
            {
                sub = (byte)value.Value;
                break;
            }
        }

        int? identifier = null;
        if (service.Parent is { } instance)
        {
            // Exactly one owned discriminator is unambiguous; with several
            // STATICVALUEs the owner of a value cannot be told apart.
            var values = instance.Elements().Where(e => e.Name.LocalName == "STATICVALUE").ToArray();
            if (values.Length == 1 && CddCatalogReader.LongAttr(values[0], "v") is >= 0 and <= int.MaxValue and var v)
            {
                identifier = (int)v;
                if (sub is null && !IdentifierServices.Contains(sid) && v <= 0xFF &&
                    RequestStaticSpec(request, values[0], byId) == "sub")
                    sub = (byte)v;
            }
        }
        return new CddStructuralService(sid, sub, identifier);
    }

    private static XElement? FindProtocolService(
        XElement service, IReadOnlyDictionary<string, XElement> byId)
    {
        var current = service;
        for (var hop = 0; hop < 4; hop++)
        {
            var reference = ((string?)current.Attribute("tmplref"))?.Trim();
            if (string.IsNullOrEmpty(reference) ||
                !byId.TryGetValue(reference, out var target) ||
                DiagnosticParameterHierarchyReader.IsAmbiguousCddType(target))
                return null;
            if (target.Name.LocalName == "PROTOCOLSERVICE") return target;
            current = target;
        }
        return null;
    }

    /// <summary>The spec of the request STATICCOMP that the STATICVALUE's SHSTATIC refers to.</summary>
    private static string RequestStaticSpec(
        XElement request, XElement staticValue, IReadOnlyDictionary<string, XElement> byId)
    {
        var shared = ((string?)staticValue.Attribute("shstaticref"))?.Trim();
        if (string.IsNullOrEmpty(shared) || !byId.TryGetValue(shared, out var shstatic)) return "";
        var refs = shstatic.Elements()
            .Where(e => e.Name.LocalName == "STATICCOMPREF")
            .Select(e => (string?)e.Attribute("idref"))
            .ToHashSet();
        foreach (var comp in request.Elements().Where(e => e.Name.LocalName == "STATICCOMP"))
            if (refs.Contains((string?)comp.Attribute("id")))
                return ((string?)comp.Attribute("spec"))?.Trim().ToLowerInvariant() ?? "";
        return "";
    }
}

/// <summary>Scalar facts a CANdela datatype states explicitly; nothing is inferred.</summary>
internal sealed record CddScalarType(
    int? BitLength, string DataType, string Unit, double? Factor, double? Offset);

internal static class CddDataTypeReader
{
    public static string PhysicalUnit(XElement type) =>
        type.Elements().FirstOrDefault(e => e.Name.LocalName == "PVALUETYPE")?
            .Elements().FirstOrDefault(e => e.Name.LocalName == "UNIT")?.Value.Trim() ?? string.Empty;

    /// <summary>
    /// Scalar DATAOBJ of a DIAGINST: the only data-carrying object of its
    /// SIMPLECOMPCONT containers (SPECDATAOBJ negative-response codes ignored).
    /// Structured, DTC and record objects make the layout non-scalar, so null.
    /// </summary>
    public static CddScalarType? ReadSingleScalar(
        XElement instance, IReadOnlyDictionary<string, XElement> byId)
    {
        var objects = instance.Elements().Where(e => e.Name.LocalName == "SIMPLECOMPCONT")
            .SelectMany(e => e.Elements())
            .Where(e => e.Name.LocalName is not ("NAME" or "DESC" or "QUAL" or "SPECDATAOBJ"))
            .ToArray();
        if (objects.Length != 1 || objects[0].Name.LocalName != "DATAOBJ") return null;
        var reference = ((string?)objects[0].Attribute("dtref"))?.Trim();
        if (string.IsNullOrEmpty(reference) || !byId.TryGetValue(reference, out var type) ||
            DiagnosticParameterHierarchyReader.IsAmbiguousCddType(type))
            return null;
        return ReadScalar(type);
    }

    public static CddScalarType? ReadScalar(XElement type)
    {
        var coded = type.Elements().FirstOrDefault(e => e.Name.LocalName == "CVALUETYPE");
        if (coded is null) return null;
        int? bits = null;
        if (int.TryParse((string?)coded.Attribute("bl"), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var bl) && bl > 0)
        {
            bits = bl;
            // A fixed-size field of elements (e.g. 13 ASCII characters) declares
            // equal min/max element counts.
            if (((string?)coded.Attribute("qty")) == "field" &&
                int.TryParse((string?)coded.Attribute("minsz"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var min) &&
                int.TryParse((string?)coded.Attribute("maxsz"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var max) &&
                min == max && min > 0)
                bits = bl * min;
            else if (((string?)coded.Attribute("qty")) == "field")
                bits = null;
        }

        var sign = (string?)coded.Attribute("sig") == "1";
        var dataType = ((string?)coded.Attribute("enc"))?.Trim().ToLowerInvariant() switch
        {
            "uns" => sign ? "SIGNED" : "UNSIGNED",
            "bcd" => "BCD",
            "asc" => "ASCII",
            _ => string.Empty
        };

        // Linear scaling is stated only by a LINCOMP / COMPTBL with one COMP.
        double? factor = null, offset = null;
        if (type.Name.LocalName is "LINCOMP" or "COMPTBL")
        {
            var comps = type.Elements().Where(e => e.Name.LocalName == "COMP").ToArray();
            if (comps.Length == 1 &&
                double.TryParse((string?)comps[0].Attribute("f"), NumberStyles.Float, CultureInfo.InvariantCulture, out var f) &&
                double.IsFinite(f))
            {
                factor = f;
                if (double.TryParse((string?)comps[0].Attribute("o"), NumberStyles.Float, CultureInfo.InvariantCulture, out var o) &&
                    double.IsFinite(o))
                    offset = o;
            }
        }
        return new CddScalarType(bits, dataType, PhysicalUnit(type), factor, offset);
    }
}
