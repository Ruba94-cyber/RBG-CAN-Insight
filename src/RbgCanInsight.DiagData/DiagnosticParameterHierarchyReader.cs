using System.Globalization;
using System.Xml.Linq;

namespace RbgCanInsight.DiagData;

/// <summary>
/// Source-backed engineering parameter tree. These are description nodes, not
/// verified UDS data-record bit positions and not proof of physical decoding.
/// Path encodes sibling ordinals to distinguish duplicate names.
/// </summary>
public sealed record DiagnosticEngineeringParameter(
    string Name,
    string LongName,
    string Description,
    string Path,
    string ParentPath,
    int Depth,
    string Kind,
    string SourceId,
    string TypeReference,
    string DataType,
    string Unit,
    int? DeclaredBitLength,
    string ReferenceStatus = "",
    string DefinitionOrigin = "",
    string DeclaredParCode = "",
    IReadOnlyList<DiagnosticDeclaredValueMeaning>? DeclaredValueDefinitions = null,
    string DeclaredBytePosition = "",
    string DeclaredBitPosition = "",
    string DeclaredBitOffset = "")
{
    public string DisplayName => string.IsNullOrWhiteSpace(LongName) ? Name : LongName;
    /// <summary>All source-declared exact and ranged textual meanings;
    /// no conversion to a guessed numeric code has occurred.</summary>
    public IReadOnlyList<DiagnosticDeclaredValueMeaning> DeclaredValues =>
        DeclaredValueDefinitions ?? [];
}

/// <summary>Conservatively collects all declared CDD/ODX response parameters,
/// including nested containers and referenced structures. Unknown nodes and
/// unresolved references remain explicit; no layout/conversion is guessed.</summary>
public static class DiagnosticParameterHierarchyReader
{
    private const int MaximumDepth = 32;
    private const int MaximumNodes = 20000;
    private static readonly XElement AmbiguousCddType = new("AMBIGUOUS-CDDTYPE");

    public static bool IsAmbiguousCddType(XElement? value) =>
        ReferenceEquals(value, AmbiguousCddType);

    private static readonly HashSet<string> ParameterNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "DATAFIELD", "DATAOBJ", "DATAPARAM", "PARAM", "SPECDATAOBJ",
        "GAPDATAOBJ", "STRUCT", "STRUCTURE", "UNION", "PACKET",
        "BITSFIELD", "MUX", "MULTIPLEXER", "END-OF-PDU-FIELD", "TABLE",
        "FIELD", "ENGPARAM", "ENGPAR", "ENGINEERINGPARAMETER", "SUBPARAM"
    };

    private static readonly HashSet<string> OdxHeaderSemantics = new(StringComparer.OrdinalIgnoreCase)
    {
        "SERVICE-ID", "ID", "DATA-ID", "DID", "SUBFUNCTION", "SUB-FUNCTION", "RESERVED"
    };

    public static IReadOnlyList<DiagnosticEngineeringParameter> FromCdd(
        XElement owner, XDocument document)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(document);
        return FromCdd(owner, IndexCddTypes(document));
    }

    /// <summary>Index CDD type IDs once per imported source, not once per DID.</summary>
    public static IReadOnlyDictionary<string, XElement> IndexCddTypes(XDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return IndexById(document.Descendants(), "id");
    }

    public static IReadOnlyList<DiagnosticEngineeringParameter> FromCdd(
        XElement owner, IReadOnlyDictionary<string, XElement> byId)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(byId);
        return Collect(owner.Elements(), byId, isOdx: false);
    }

    public static IReadOnlyList<DiagnosticEngineeringParameter> FromOdx(
        XElement service, IReadOnlyDictionary<string, XElement> byId,
        IReadOnlyDictionary<string, XElement?>? byShortName = null)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(byId);
        var responses = new List<XElement>();
        foreach (var reference in service.Descendants().Where(element =>
                     element.Name.LocalName == "POS-RESPONSE-REF"))
        {
            var id = (string?)reference.Attribute("ID-REF");
            if (id is not null && byId.TryGetValue(id, out var response))
                responses.Add(response);
        }

        if (responses.Count == 0) return [];
        // Wrapper traversal is limited to resolved positive responses. Requests,
        // negative responses and unrelated global DOPs are never swept in.
        return Collect(responses.Distinct(), byId, isOdx: true,
            byShortName ?? IndexOdxShortNames(byId));
    }

    /// <summary>Read/write Engineering Parameters are commonly defined on
    /// the ODX 0x2E request rather than on a positive response. Only this
    /// request's parameter tree is traversed, with protocol constants excluded.</summary>
    public static IReadOnlyList<DiagnosticEngineeringParameter> FromOdxRequest(
        XElement request, IReadOnlyDictionary<string, XElement> byId,
        IReadOnlyDictionary<string, XElement?>? byShortName = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(byId);
        // The REQUEST wrapper is deliberately excluded by Collect.Walk so
        // unrelated protocol nodes are never classified as parameters. Pass
        // its children instead; passing [request] silently exported no 0x2E
        // engineering parameters, even when PARAMS contained a full tree.
        return Collect(request.Elements(), byId, isOdx: true,
            byShortName ?? IndexOdxShortNames(byId));
    }

    /// <summary>
    /// Resolve ODX SNREFs only if the short name occurs in exactly one
    /// id-bearing diagnostic type across the source/package. Null means
    /// ambiguous; guessing an ECU variant would risk wrong sub-PAR mappings.
    /// Build this index once per effective catalog, not once per DID.
    /// </summary>
    public static IReadOnlyDictionary<string, XElement?> IndexOdxShortNames(
        IReadOnlyDictionary<string, XElement> byId)
    {
        ArgumentNullException.ThrowIfNull(byId);
        return IndexOdxShortNames(byId.Values);
    }

    /// <summary>Index only the types visible in an effective ODX variant.
    /// Types from unrelated ECU variants must not create false matches.</summary>
    public static IReadOnlyDictionary<string, XElement?> IndexOdxShortNames(
        IEnumerable<XElement> visibleTypes)
    {
        ArgumentNullException.ThrowIfNull(visibleTypes);
        var result = new Dictionary<string, XElement?>(StringComparer.Ordinal);
        foreach (var node in visibleTypes)
        {
            if (node.Name.LocalName is not ("DATA-OBJECT-PROP" or "STRUCTURE" or
                "END-OF-PDU-FIELD" or "DYNAMIC-LENGTH-FIELD" or
                "TABLE" or "MUX" or "DOP-BASE" or "DTC-DOP"))
                continue;
            var name = node.Elements().FirstOrDefault(child =>
                child.Name.LocalName == "SHORT-NAME")?.Value.Trim();
            if (string.IsNullOrWhiteSpace(name)) continue;
            if (!result.TryAdd(name, node))
                result[name] = null; // Repeated even within one document: ambiguous.
        }
        return result;
    }

    private static IReadOnlyDictionary<string, XElement> IndexById(
        IEnumerable<XElement> nodes, string attribute)
    {
        var result = new Dictionary<string, XElement>(StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            var id = (string?)node.Attribute(attribute);
            if (string.IsNullOrWhiteSpace(id)) continue;
            if (!result.TryAdd(id, node))
                result[id] = AmbiguousCddType;
        }
        return result;
    }

    private static IReadOnlyList<DiagnosticEngineeringParameter> Collect(
        IEnumerable<XElement> roots, IReadOnlyDictionary<string, XElement> byId,
        bool isOdx, IReadOnlyDictionary<string, XElement?>? byShortName = null)
    {
        var result = new List<DiagnosticEngineeringParameter>();
        // Track target object identity, so mixed ID-REF and SNREF loops are
        // recognized even when the two references have different text.
        var activeReferences = new HashSet<XElement>();
        var ordinalsByParent = new Dictionary<string, int>(StringComparer.Ordinal);
        var nestedReferenceCache = new Dictionary<XElement, bool>();
        // Many DIDs/PARs refer to the same immutable datatype node. Parse its
        // localized value list once per imported hierarchy rather than once
        // per occurrence of that datatype.
        var declaredValueCache =
            new Dictionary<XElement, IReadOnlyList<DiagnosticDeclaredValueMeaning>>();

        void Walk(IEnumerable<XElement> elements, string parentPath, int depth)
        {
            if (depth > MaximumDepth)
                throw new InvalidDataException("Diagnostic parameter nesting exceeds the 32-level safety limit.");

            foreach (var element in elements)
            {
                var tag = element.Name.LocalName;
                if (tag is "NEG" or "NEG-RESPONSE" or "NEG-RESPONSES" or "REQ" or "REQUEST" or
                    "REQUESTS" or "STATICVALUE" or "SERVICE" or "SERVICE-ID" or "DTC")
                    continue;
                if (tag is "SPECDATAOBJ" &&
                    string.Equals((string?)element.Attribute("spec"), "rc",
                        StringComparison.OrdinalIgnoreCase))
                    continue;
                if (tag is "PARAM" && isOdx && depth == 0 &&
                    OdxHeaderSemantics.Contains((string?)element.Attribute("SEMANTIC") ?? string.Empty))
                    continue;

                if (!ParameterNames.Contains(tag))
                {
                    // Skip global metadata and scalar definitions, traverse only
                    // response/container wrappers within the selected service.
                    if (tag is "NAME" or "DESC" or "QUAL" or "SHORT-NAME" or "LONG-NAME" or
                        "TEXT" or "TUV" or "DATATYPES" or "COMPU-METHOD" or "CVALUETYPE" or
                        "PVALUETYPE" or "TEXTMAP" or "DOP-REF" or "DOP-SNREF" or "UNIT" or
                        "FACTOR" or "BITLENGTH" or "BIT-LENGTH" or "BIT-OFFSET" or
                        "BYTE-POSITION" or "BIT-POSITION" or "STATICCOMPREF")
                        continue;
                    Walk(element.Elements(), parentPath, depth);
                    continue;
                }

                if (result.Count >= MaximumNodes)
                    throw new InvalidDataException("Diagnostic parameter count exceeds the 20,000-node safety limit.");
                var ordinal = ordinalsByParent.TryGetValue(parentPath, out var previous)
                    ? previous + 1 : 1;
                ordinalsByParent[parentPath] = ordinal;
                var shortName = Direct(element, "SHORT-NAME", "SHORTCUTQUAL", "QUAL", "NAME");
                if (shortName.Length == 0)
                    shortName = (string?)element.Attribute("qual") ??
                        (string?)element.Attribute("name") ?? tag + "_" + ordinal;
                var label = Direct(element, "LONG-NAME", "NAME");
                var description = Direct(element, "DESC");
                // XML id identifies a source node; it is not necessarily a
                // numeric Engineering PAR code. Preserve both independently.
                var id = (string?)element.Attribute("id") ??
                         (string?)element.Attribute("ID") ??
                         (string?)element.Attribute("par") ?? string.Empty;
                var declaredParCode = (string?)element.Attribute("par") ??
                    (string?)element.Attribute("PAR") ??
                    (string?)element.Attribute("parid") ??
                    (string?)element.Attribute("PAR-ID") ??
                    Direct(element, "PAR-ID", "PARAM-ID", "PARAMETER-ID",
                        "PARCODE", "PAR-CODE");
                var referenceNode = element.Elements().FirstOrDefault(child =>
                    child.Name.LocalName is "DOP-REF" or "DOP-SNREF" or "STRUCTURE-REF" or
                        "STRUCTURE-SNREF" or "TABLE-REF" or "TABLE-SNREF");
                var reference = (string?)element.Attribute("dtref") ??
                    (string?)referenceNode?.Attribute("ID-REF") ??
                    (string?)referenceNode?.Attribute("SHORT-NAME") ?? string.Empty;
                var referenceIsShortName = referenceNode?.Name.LocalName.EndsWith(
                    "SNREF", StringComparison.OrdinalIgnoreCase) == true;
                var declared = ParseLength(Direct(element, "BITLENGTH", "BIT-LENGTH", "BITSIZE"));
                // OEM descriptions expose positional declarations in
                // different coordinate systems (PDU, record, structure).
                // Preserve the original tokens without claiming they are
                // verified UDS data-record bit offsets.
                var declaredBytePosition = Direct(element, "BYTE-POSITION");
                var declaredBitPosition = Direct(element, "BIT-POSITION");
                var declaredBitOffset = Direct(element,
                    "BITOFFSET", "BIT-OFFSET", "BITPOS");
                var type = Direct(element, "DATATYPE", "DATA-TYPE", "BASE-DATA-TYPE");
                var unit = Direct(element, "UNIT", "UNITREF");
                var status = string.Empty;
                XElement? target = null;
                if (reference.Length > 0)
                {
                    if (referenceIsShortName)
                    {
                        if (byShortName is null ||
                            !byShortName.TryGetValue(reference, out target))
                            status = "Unresolved short-name reference (no global match)";
                        else if (target is null)
                            status = "Ambiguous short-name reference (multiple type matches)";
                    }
                    else if (!byId.TryGetValue(reference, out target))
                        status = "Unresolved type reference";
                    else if (IsAmbiguousCddType(target))
                    {
                        status = "Ambiguous CDD datatype ID (multiple definitions)";
                        target = null;
                    }

                    if (target is not null)
                    {
                        if (activeReferences.Contains(target))
                            status = "Cyclic reference, expansion stopped";
                        else
                        {
                            declared ??= ParseLength(Direct(target, "BITLENGTH", "BIT-LENGTH", "BITSIZE"));
                            declared ??= ParseLength((string?)target.Elements().FirstOrDefault(e =>
                                e.Name.LocalName == "CVALUETYPE")?.Attribute("bl") ?? string.Empty);
                            // An ODX scalar DOP declares its coded bit length and
                            // BASE-DATA-TYPE on a direct DIAG-CODED-TYPE. Do not
                            // borrow a nested child's length for a STRUCTURE.
                            if (target.Name.LocalName is "DATA-OBJECT-PROP" or "DOP-BASE" or "DTC-DOP")
                            {
                                var coded = target.Elements().FirstOrDefault(e =>
                                    e.Name.LocalName == "DIAG-CODED-TYPE");
                                declared ??= ParseLength(coded?.Elements().FirstOrDefault(e =>
                                    e.Name.LocalName == "BIT-LENGTH")?.Value ?? string.Empty);
                                if (type.Length == 0)
                                    type = (string?)coded?.Attribute("BASE-DATA-TYPE") ??
                                        target.Name.LocalName;
                                if (unit.Length == 0)
                                {
                                    var unitId = (string?)target.Elements().FirstOrDefault(e =>
                                        e.Name.LocalName == "UNIT-REF")?.Attribute("ID-REF");
                                    if (!string.IsNullOrWhiteSpace(unitId) &&
                                        byId.TryGetValue(unitId, out var unitNode))
                                        unit = Direct(unitNode, "DISPLAY-NAME", "SHORT-NAME");
                                }
                            }
                            if (type.Length == 0) type = target.Name.LocalName;
                            // CANdela declares the physical unit as UNIT text inside
                            // the datatype's PVALUETYPE.
                            if (!isOdx && unit.Length == 0)
                                unit = CddDataTypeReader.PhysicalUnit(target);
                        }
                    }
                }

                IReadOnlyList<DiagnosticDeclaredValueMeaning> declaredValues = [];
                if (target is not null &&
                    ((isOdx && (target.Name.LocalName is
                        "DATA-OBJECT-PROP" or "DOP-BASE" or "DTC-DOP")) ||
                     (!isOdx && target.Name.LocalName == "TEXTTBL")))
                {
                    if (!declaredValueCache.TryGetValue(target, out var cachedValues))
                    {
                        cachedValues = isOdx
                            ? OdxDeclaredValueReader.Read(target)
                            : CddDeclaredValueReader.Read(target);
                        declaredValueCache.Add(target, cachedValues);
                    }

                    declaredValues = cachedValues;
                }

                var path = parentPath + "/" + ordinal.ToString(CultureInfo.InvariantCulture) +
                    ":" + shortName.Replace("/", "_", StringComparison.Ordinal);
                var rowIndex = result.Count;
                result.Add(new DiagnosticEngineeringParameter(
                    shortName, label, description, path, parentPath, depth, tag,
                    id, reference, type, unit, declared, status,
                    DeclaredParCode: declaredParCode,
                    DeclaredValueDefinitions: declaredValues,
                    DeclaredBytePosition: declaredBytePosition,
                    DeclaredBitPosition: declaredBitPosition,
                    DeclaredBitOffset: declaredBitOffset));

                Walk(element.Elements().Where(child => !IsMetadata(child.Name.LocalName)),
                    path, depth + 1);
                if (target is null || status.Length > 0) continue;

                // ODX dynamic-length and end-of-PDU fields point to a BASIC
                // STRUCTURE template. The template parameters are real source
                // declarations, but repeat counts, runtime lengths and bit
                // locations cannot be deduced from the diagnostic description.
                if (isOdx && (target.Name.LocalName is
                    "DYNAMIC-LENGTH-FIELD" or "END-OF-PDU-FIELD"))
                {
                    var templateRef = target.Elements().FirstOrDefault(child =>
                        child.Name.LocalName is "BASIC-STRUCTURE-REF" or
                            "BASIC-STRUCTURE-SNREF" or "STRUCTURE-REF" or
                            "STRUCTURE-SNREF");
                    var templateId = (string?)templateRef?.Attribute("ID-REF");
                    var templateName = (string?)templateRef?.Attribute("SHORT-NAME");
                    XElement? template = null;
                    if (!string.IsNullOrWhiteSpace(templateId))
                        byId.TryGetValue(templateId, out template);
                    else if (!string.IsNullOrWhiteSpace(templateName) && byShortName is not null)
                        byShortName.TryGetValue(templateName, out template);

                    var warning = target.Name.LocalName +
                        ": repeated/variable-length structure; occurrence count and offsets unverified";
                    if (template is null)
                        warning += "; basic structure reference unresolved or ambiguous";
                    else if (activeReferences.Contains(template))
                        warning += "; cyclic basic structure reference";

                    result[rowIndex] = result[rowIndex] with { ReferenceStatus = warning };
                    if (template is null || activeReferences.Contains(template))
                        continue;

                    activeReferences.Add(target);
                    activeReferences.Add(template);
                    try
                    {
                        Walk(template.Elements().Where(child =>
                            !IsMetadata(child.Name.LocalName)), path, depth + 1);
                    }
                    finally
                    {
                        activeReferences.Remove(template);
                        activeReferences.Remove(target);
                    }
                    continue;
                }

                // Only structural references introduce sub-parameter definitions.
                // A referenced scalar DOP or linear conversion is metadata, not a
                // nested parameter. Never assign its bit offset to the DID record.
                var children = target.Elements().Where(child => !IsMetadata(child.Name.LocalName));
                if (!nestedReferenceCache.TryGetValue(target, out var hasNested))
                {
                    hasNested = HasNestedParameters(target);
                    nestedReferenceCache[target] = hasNested;
                }
                if (!hasNested) continue;
                activeReferences.Add(target);
                try { Walk(children, path, depth + 1); }
                finally { activeReferences.Remove(target); }
            }
        }

        Walk(roots, string.Empty, 0);
        return result;
    }

    private static bool IsMetadata(string name) =>
        name is "NAME" or "DESC" or "QUAL" or "SHORT-NAME" or "LONG-NAME" or
            "TEXT" or "TUV" or "DOP-REF" or "DOP-SNREF" or
            "STRUCTURE-REF" or "STRUCTURE-SNREF" or "TABLE-REF" or
            "TABLE-SNREF" or "UNIT" or
            "UNITREF" or "DATATYPE" or "DATA-TYPE" or "BASE-DATA-TYPE" or
            "BITLENGTH" or "BIT-LENGTH" or "BITOFFSET" or "BIT-OFFSET" or
            "FACTOR" or "OFFSET" or "MINVALUE" or "MAXVALUE" or
            "BYTE-POSITION" or "BIT-POSITION" or "CVALUETYPE" or
            "PVALUETYPE" or "TEXTMAP" or "COMPU-METHOD" or "STATICVALUE";

    private static bool HasNestedParameters(XElement node) =>
        node.Descendants().Any(child => ParameterNames.Contains(child.Name.LocalName));

    private static int? ParseLength(string text)
    {
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var bits) &&
            bits is > 0 and <= 65536 ? bits : null;
    }

    private static string Direct(XElement node, params string[] names)
    {
        foreach (var name in names)
        {
            var element = node.Elements().FirstOrDefault(child => child.Name.LocalName == name);
            if (element is null) continue;
            var t = element.Descendants().FirstOrDefault(child =>
                child.Name.LocalName == "TUV" &&
                ((string?)child.Attribute(XNamespace.Xml + "lang"))?.StartsWith("en",
                    StringComparison.OrdinalIgnoreCase) == true) ??
                element.Descendants().FirstOrDefault(child => child.Name.LocalName == "TUV");
            var value = (string?)element.Attribute("v") ?? (t?.Value ?? element.Value);
            if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
        }
        return string.Empty;
    }
}
