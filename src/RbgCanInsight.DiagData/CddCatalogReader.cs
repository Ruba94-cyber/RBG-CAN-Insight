using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace RbgCanInsight.DiagData;

/// <summary>
/// Extracts the readable data identifiers (DIDs) from a Vector CANdela (.cdd) diagnostic
/// description. CANdela stores the DID as a STATICVALUE following each SERVICE element; the
/// human name, description and (when present) unit/range metadata are collected best-effort
/// so the correlation engine has as much context as the file provides.
/// </summary>
public static class CddCatalogReader
{
    public static DidCatalog Load(string path)
    {
        // CDDs are externally supplied; never resolve DTDs, entities or network
        // resources, and bound XML expansion before creating its in-memory tree.
        using (var guard = XmlReader.Create(path, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null,
            MaxCharactersInDocument = 64L * 1024 * 1024
        }))
            XmlDepthGuard.Check(guard);
        using var xmlReader = XmlReader.Create(path, new XmlReaderSettings
        {
            // Real CANdela files often declare an external candela.dtd.
            // Ignore declarations without ever resolving or expanding them.
            // An attempted entity reference remains an undeclared-entity error.
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null,
            MaxCharactersInDocument = 64L * 1024 * 1024
        });
        var document = XDocument.Load(xmlReader, LoadOptions.None);
        var typeIndex = DiagnosticParameterHierarchyReader.IndexCddTypes(document);
        var parameters = new Dictionary<int, DidParameter>();
        var services = new List<DiagnosticServiceDefinition>();
        var routines = new Dictionary<int, DiagnosticRoutine>();

        var ecuName = document.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "ECU")?.Attribute("qual")?.Value
            ?? Path.GetFileNameWithoutExtension(path);

        foreach (var service in document.Descendants().Where(e => e.Name.LocalName == "SERVICE"))
        {
            var shortcut = ChildValue(service, "SHORTCUTQUAL");
            var semantic = ChildValue(service, "SEMANTIC");
            // A global "next STATICVALUE" search crosses the following SERVICE
            // and silently associates the wrong DID. Only a directly owned or
            // immediately adjacent sibling discriminator is unambiguous here.
            var adjacent = service.ElementsAfterSelf().FirstOrDefault();
            var staticValue = service.Elements()
                .FirstOrDefault(e => e.Name.LocalName == "STATICVALUE") ??
                (adjacent?.Name.LocalName == "STATICVALUE" ? adjacent : null);
            // In real CANdela documents a DIAGINST lists a contiguous run of SERVICEs
            // (e.g. Read and Write of one DID) followed by ONE STATICVALUE carrying that DID.
            // Only the last service of the run is directly adjacent to it. Walk the
            // run of immediately following SERVICE siblings: any unrelated element in
            // between ends the run, so a DID is never lent across foreign content.
            if (staticValue is null)
            {
                foreach (var next in service.ElementsAfterSelf())
                {
                    if (next.Name.LocalName == "SERVICE") continue;
                    if (next.Name.LocalName == "STATICVALUE") staticValue = next;
                    break;
                }
            }
            var did = staticValue is null ? null : LongAttr(staticValue, "v");
            var name = string.IsNullOrWhiteSpace(shortcut)
                ? ChildText(service, "NAME")
                : TrimReadSuffix(shortcut);
            var longName = ChildText(service, "NAME");
            var description = ChildText(service, "DESC");

            // 1. Structural: SID constant of the request PDU (exact).
            // 2. FALLBACK ONLY: name heuristic, flagged NameHeuristic.
            var structural = CddServiceStructureReader.Resolve(service, typeIndex);
            var heuristicId = structural is null ? InferServiceId(shortcut, semantic) : null;
            var resolvedId = structural?.ServiceId ?? heuristicId;
            if (resolvedId is { } serviceId)
            {
                var identifier = structural?.Identifier ??
                    (did is >= 0 and <= int.MaxValue ? (int)did.Value : null);
                services.Add(new DiagnosticServiceDefinition(
                    serviceId,
                    structural?.SubFunction,
                    identifier,
                    name,
                    longName,
                    description,
                    ecuName,
                    Path.GetFullPath(path),
                    structural is not null
                        ? DiagnosticServiceIdSource.Structural
                        : DiagnosticServiceIdSource.NameHeuristic));

                if (serviceId == 0x31 && identifier is >= 0 and <= 0xFFFF)
                {
                    did ??= identifier;
                }

                if (serviceId == 0x31 && did is >= 0 and <= 0xFFFF)
                {
                    routines.TryAdd((int)did.Value, new DiagnosticRoutine(
                        (int)did.Value,
                        name,
                        longName,
                        description,
                        ecuName,
                        Path.GetFullPath(path)));
                }
            }

            if (did is not (>= 0x0001 and <= 0xFFFF))
            {
                continue;
            }

            var isRead = shortcut.EndsWith("_Read", StringComparison.OrdinalIgnoreCase) ||
                         semantic is "IDENTIFICATION" or "CURRENTDATA" or "STOREDDATA" ||
                         semantic.Contains("READ", StringComparison.OrdinalIgnoreCase);
            // A configured write-only DID still has engineering parameters
            // worth exporting. Require a supported write-service discriminator
            // rather than treating arbitrary routine/session data as a DID.
            var isWriteDid = resolvedId is 0x2E or 0x3B &&
                (shortcut.EndsWith("_Write", StringComparison.OrdinalIgnoreCase) ||
                 semantic.Contains("WRITE", StringComparison.OrdinalIgnoreCase));
            if (!isRead && !isWriteDid)
            {
                continue;
            }

            // CDD implementations differ by OEM template. The old code used the
            // first *descendant* BITLENGTH to fabricate a whole-DID scalar at
            // bit offset zero, even when the length belonged to a nested field.
            // Retain only metadata declared directly on the service or an
            // unambiguous single response field. Unresolved layouts remain raw.
            var fields = ParseDeclaredResponseFields(service, typeIndex);
            // Even a single described response field may be preceded by UDS
            // response headers or OEM template fields. Do not advertise its
            // physical scale/range as belonging to the complete DID without
            // a verified data-record boundary; keep it on the raw-only field.
            var unit = DirectText(service, "UNIT", "UNITREF");
            double? minimum = DirectDouble(service, "MINVALUE", "MIN", "LOWERLIMIT");
            double? maximum = DirectDouble(service, "MAXVALUE", "MAX", "UPPERLIMIT");
            double? factor = DirectDouble(service, "FACTOR", "SLOPE", "MUL");
            double? offset = DirectDouble(service, "OFFSET", "ADD");
            var explicitLength = DirectDouble(service, "BITLENGTH", "LENGTH", "BITSIZE");
            int? bitLength = explicitLength is > 0 and <= int.MaxValue
                ? (int)explicitLength.Value : null;

            var key = (int)did.Value;
            DiagnosticDidDefinitionMerger.AddOrMerge(parameters, new DidParameter(
                key, name, longName, description, unit, minimum, maximum,
                factor, offset, DataType: string.Empty, bitLength, ecuName,
                Path.GetFullPath(path), fields,
                DiagnosticParameterHierarchyReader.FromCdd(service, typeIndex)),
                "CDD SERVICE: " + (shortcut.Length > 0 ? shortcut : name));
        }

        // Older CANdela documents describe an identification DID at DIAGINST
        // level: SERVICE (Read), STATICVALUE and a SIMPLECOMPCONT containing
        // DATAOBJ/STRUCT children. No global "find next" matching is allowed.
        foreach (var instance in document.Descendants().Where(element =>
                     element.Name.LocalName == "DIAGINST"))
        {
            var staticValue = instance.Elements().FirstOrDefault(element =>
                element.Name.LocalName == "STATICVALUE");
            var id = staticValue is null ? null : LongAttr(staticValue, "v");
            if (id is not (>= 1 and <= 0xFFFF)) continue;
            var instanceServices = instance.Elements().Where(element =>
                element.Name.LocalName == "SERVICE").ToArray();
            var hasReadService = instanceServices.Any(element =>
                (ChildValue(element, "QUAL") + " " + ChildText(element, "NAME"))
                    .Contains("Read", StringComparison.OrdinalIgnoreCase));
            var hasWriteService = instanceServices.Any(element =>
                (ChildValue(element, "QUAL") + " " + ChildText(element, "NAME"))
                    .Contains("Write", StringComparison.OrdinalIgnoreCase));
            var diagnosticClass = instance.Ancestors().FirstOrDefault(element =>
                element.Name.LocalName == "DIAGCLASS");
            var className = diagnosticClass is null ? string.Empty :
                ChildValue(diagnosticClass, "QUAL") + " " + ChildText(diagnosticClass, "NAME");
            var isClearlyNotDid = new[] { "DTC", "FAULT", "ROUTINE", "SESSION",
                    "SECURITY", "RESET", "JOB" }
                .Any(token => className.Contains(token, StringComparison.OrdinalIgnoreCase));
            var isDataClass = new[] { "IDENT", "DATA", "DID", "ENGINEERING", "CODING" }
                .Any(token => className.Contains(token, StringComparison.OrdinalIgnoreCase));
            if (isClearlyNotDid || !(hasReadService || hasWriteService && isDataClass))
                continue;
            var name = ChildValue(instance, "QUAL");
            if (string.IsNullOrWhiteSpace(name)) name = ChildText(instance, "NAME");
            var longName = ChildText(instance, "NAME");
            var description = ChildText(instance, "DESC");
            var key = (int)id.Value;
            var hierarchy = DiagnosticParameterHierarchyReader.FromCdd(instance, typeIndex);
            // Only a lone scalar DATAOBJ describes the whole DID; its datatype
            // states length, encoding, unit and (single-segment) linear scaling.
            var scalar = CddDataTypeReader.ReadSingleScalar(instance, typeIndex);
            DiagnosticDidDefinitionMerger.AddOrMerge(parameters, new DidParameter(
                key, name, longName, description, scalar?.Unit ?? string.Empty, null, null,
                scalar?.Factor, scalar?.Offset, scalar?.DataType ?? string.Empty,
                scalar?.BitLength, ecuName, Path.GetFullPath(path),
                EngineeringParameterDefinitions: hierarchy),
                "CDD DIAGINST: " + name);
        }

        var troubleCodes = ParseTroubleCodes(document, ecuName, path);
        return new DidCatalog(
            Path.GetFileNameWithoutExtension(path),
            ecuName,
            Path.GetFullPath(path),
            parameters.Values.OrderBy(parameter => parameter.Did).ToArray(),
            troubleCodes,
            routines.Values.OrderBy(routine => routine.Identifier).ToArray(),
            services,
            ParseConnections(document, ecuName, path),
            ParseVariants(document));
    }

    /// <summary>
    /// Preserve direct response-field descriptions, but do not claim that a
    /// template-specific BITOFFSET is a UDS *data-record* offset. The source
    /// coordinate system and optional header bytes must be resolved by a full
    /// CANdela template model before permitting physical DID decoding.
    /// </summary>
    private static IReadOnlyList<DiagnosticFieldDefinition> ParseDeclaredResponseFields(
        XElement service, IReadOnlyDictionary<string, XElement> typeIndex)
    {
        var responseNodes = service.Descendants()
            .Where(element => element.Name.LocalName is
                "POSRESPONSE" or "POS-RESPONSE" or "POS_RESPONSE" or "RESPONSE" or "DATARECORD");
        var result = new List<DiagnosticFieldDefinition>();
        foreach (var response in responseNodes)
        {
            foreach (var element in response.Descendants().Where(element =>
                         element.Name.LocalName is "DATAFIELD" or "DATAOBJ" or "DATAPARAM" or "PARAM"))
            {
                var typeReference = ((string?)element.Attribute("dtref"))?.Trim();
                XElement? type = null;
                if (!string.IsNullOrWhiteSpace(typeReference))
                    typeIndex.TryGetValue(typeReference, out type);
                // Reused CDD datatype IDs are ambiguous: do not assign a
                // random type's coded length or exact value mappings.
                if (DiagnosticParameterHierarchyReader.IsAmbiguousCddType(type))
                    type = null;
                // CANdela commonly declares the coded length in the referenced
                // datatype's CVALUETYPE bl attribute, not on the DATAOBJ itself.
                // This is a *length only*, never evidence for a UDS bit offset.
                var length = DirectDouble(element, "BITLENGTH", "BIT-LENGTH", "BITSIZE");
                if (length is null && type is not null)
                {
                    var codedLength = (string?)type.Elements().FirstOrDefault(child =>
                        child.Name.LocalName == "CVALUETYPE")?.Attribute("bl");
                    if (double.TryParse(codedLength, NumberStyles.Float,
                            CultureInfo.InvariantCulture, out var parsed))
                        length = parsed;
                }
                if (length is not (> 0 and <= 65536) || length.Value != Math.Truncate(length.Value))
                    continue;

                var name = (string?)element.Attribute("qual") ??
                    DirectText(element, "SHORTCUTQUAL", "SHORT-NAME", "NAME");
                if (string.IsNullOrWhiteSpace(name)) continue;
                var longName = ChildText(element, "NAME");
                var description = ChildText(element, "DESC");
                var declaredOffset = DirectDouble(element, "BITOFFSET", "BIT-OFFSET", "BITPOS");
                if (declaredOffset is not null)
                    description = (description.Length == 0 ? "" : description + " ") +
                        $"CDD declared position {declaredOffset.Value:G}; the relative data-record origin has not been verified.";
                var rawType = DirectText(element, "DATATYPE", "DATA-TYPE", "BASE-DATA-TYPE");
                if (rawType.Length == 0 && type is not null)
                    rawType = type.Name.LocalName;
                IReadOnlyList<DiagnosticEnumValue> declaredMeanings =
                    type is not null && type.Name.LocalName == "TEXTTBL"
                    ? OdxDeclaredValueReader.ExactIntegerValues(
                        CddDeclaredValueReader.Read(type))
                    : [];
                var field = new DiagnosticFieldDefinition(
                    name,
                    longName,
                    description,
                    BitOffset: -1, // Never fabricate a UDS data-record offset.
                    BitLength: (int)length.Value,
                    DataType: rawType,
                    Unit: DirectText(element, "UNIT", "UNITREF"),
                    Minimum: DirectDouble(element, "MINVALUE", "MIN", "LOWERLIMIT"),
                    Maximum: DirectDouble(element, "MAXVALUE", "MAX", "UPPERLIMIT"),
                    Factor: DirectDouble(element, "FACTOR", "SLOPE", "MUL"),
                    Offset: DirectDouble(element, "OFFSET", "ADD"),
                    IsSigned: IsSignedType(rawType),
                    ByteOrder: DirectText(element, "BYTEORDER", "BYTE-ORDER"),
                    Values: declaredMeanings,
                    SupportsPhysicalConversion: false);
                if (!result.Any(previous => previous.Name == field.Name &&
                                            previous.BitLength == field.BitLength))
                    result.Add(field);
            }
        }
        return result;
    }

    private static bool IsSignedType(string value)
    {
        var type = value.Trim();
        // "UNSIGNED" contains "SIGNED"; substring matching incorrectly
        // labels unsigned CDD fields as signed. Fail closed for unknown types.
        return type.StartsWith("A_INT", StringComparison.OrdinalIgnoreCase) ||
            type.StartsWith("SINT", StringComparison.OrdinalIgnoreCase) ||
            type.StartsWith("INT", StringComparison.OrdinalIgnoreCase) ||
            type.Equals("SIGNED", StringComparison.OrdinalIgnoreCase) ||
            type.StartsWith("SIGNED_", StringComparison.OrdinalIgnoreCase);
    }

    private static string DirectText(XElement element, params string[] names)
    {
        foreach (var name in names)
        {
            var child = element.Elements().FirstOrDefault(item => item.Name.LocalName == name);
            if (child is null) continue;
            var value = (string?)child.Attribute("v") ?? child.Value;
            if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
        }
        return string.Empty;
    }

    private static double? DirectDouble(XElement element, params string[] names)
    {
        var raw = DirectText(element, names);
        return double.TryParse(raw, NumberStyles.Float,
            CultureInfo.InvariantCulture, out var value) && double.IsFinite(value)
            ? value : null;
    }

    private static IReadOnlyList<DiagnosticVariant> ParseVariants(XDocument document) =>
        document.Descendants()
            .Where(element => element.Name.LocalName is "ECU" or "ECUVARIANT" or "VARIANT")
            .Select(element => new DiagnosticVariant(
                (string?)element.Attribute("qual") ?? ChildText(element, "NAME"),
                element.Name.LocalName,
                ChildText(element, "DESC")))
            .Where(variant => !string.IsNullOrWhiteSpace(variant.Name))
            .DistinctBy(variant => (variant.Name, variant.Type))
            .ToArray();

    private static IReadOnlyList<DiagnosticConnection> ParseConnections(
        XDocument document,
        string ecuName,
        string path)
    {
        uint? FindId(params string[] tokens)
        {
            foreach (var element in document.Descendants())
            {
                var qualifier = ((string?)element.Attribute("qual") ?? element.Name.LocalName).ToUpperInvariant();
                if (!tokens.All(token => qualifier.Contains(token, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                var raw = LongAttr(element, "v") ??
                          element.Descendants().Select(item => LongAttr(item, "v")).FirstOrDefault(value => value is not null);
                if (raw is >= 0 and <= uint.MaxValue)
                {
                    return (uint)raw.Value;
                }
            }

            return null;
        }

        var request = FindId("REQ", "ID") ?? FindId("REQUEST", "CAN");
        var response = FindId("RESP", "ID") ?? FindId("RESPONSE", "CAN");
        if (request is null && response is null) return [];
        var extended = (request ?? response) > 0x7FF;
        return
        [
            new DiagnosticConnection(ecuName, request, response, extended,
                extended ? "29-bit physical" : "11-bit physical", ecuName, Path.GetFullPath(path))
        ];
    }

    /// <summary>
    /// FALLBACK ONLY (used when the request PDU has no resolvable SID constant):
    /// guesses the service ID from names. Unreliable; callers flag the result
    /// <see cref="DiagnosticServiceIdSource.NameHeuristic"/>.
    /// </summary>
    private static byte? InferServiceId(string shortcut, string semantic)
    {
        var text = shortcut + " " + semantic;
        if (text.Contains("ROUTINE", StringComparison.OrdinalIgnoreCase)) return 0x31;
        // A "clear" operation on the fault memory (CANdela: FaultMemory_Clear) must win over
        // the generic fault-memory read mapping below.
        var isFaultMemory = text.Contains("FAULTMEMORY", StringComparison.OrdinalIgnoreCase) ||
                            text.Contains("FAULT_MEMORY", StringComparison.OrdinalIgnoreCase);
        if (text.Contains("CLEARDTC", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("CLEARFAULT", StringComparison.OrdinalIgnoreCase) ||
            (isFaultMemory && text.Contains("CLEAR", StringComparison.OrdinalIgnoreCase))) return 0x14;
        if (text.Contains("READDTC", StringComparison.OrdinalIgnoreCase) || isFaultMemory) return 0x19;
        if (text.Contains("SECURITY", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("REQUESTSEED", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("SENDKEY", StringComparison.OrdinalIgnoreCase)) return 0x27;
        if (text.Contains("TESTERPRESENT", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("TESTER_PRESENT", StringComparison.OrdinalIgnoreCase)) return 0x3E;
        if (text.Contains("MESSAGETRANSMISSION", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("MESSAGE_TRANSMISSION", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("COMMUNICATIONCONTROL", StringComparison.OrdinalIgnoreCase)) return 0x28;
        if (text.Contains("SESSION", StringComparison.OrdinalIgnoreCase)) return 0x10;
        if (text.Contains("RESET", StringComparison.OrdinalIgnoreCase)) return 0x11;
        if (text.Contains("WRITE", StringComparison.OrdinalIgnoreCase)) return 0x2E;
        if (text.Contains("READ", StringComparison.OrdinalIgnoreCase) ||
            semantic is "IDENTIFICATION" or "CURRENTDATA" or "STOREDDATA") return 0x22;
        return null;
    }

    private static IReadOnlyList<DiagnosticTroubleCode> ParseTroubleCodes(
        XDocument document,
        string ecuName,
        string path)
    {
        var result = new Dictionary<int, DiagnosticTroubleCode>();
        foreach (var element in document.Descendants().Where(item => item.Name.LocalName == "DTC"))
        {
            var value = LongAttr(element, "v") ?? LongAttr(element, "id");
            if (value is not (>= 0 and <= 0xFFFFFF))
            {
                continue;
            }

            var name = ChildValue(element, "SHORTCUTQUAL");
            if (string.IsNullOrWhiteSpace(name))
            {
                name = ChildText(element, "NAME");
            }

            result.TryAdd((int)value.Value, new DiagnosticTroubleCode(
                (int)value.Value,
                name,
                ChildText(element, "NAME"),
                ChildText(element, "DESC"),
                ecuName,
                Path.GetFullPath(path)));
        }

        return result.Values.OrderBy(dtc => dtc.Code).ToArray();
    }

    private static string TrimReadSuffix(string shortcut) =>
        shortcut.EndsWith("_Read", StringComparison.OrdinalIgnoreCase)
            ? shortcut[..^5]
            : shortcut;

    private static string ChildValue(XElement element, string name) =>
        element.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value.Trim() ?? string.Empty;

    private static string ChildText(XElement element, string name)
    {
        var child = element.Elements().FirstOrDefault(e => e.Name.LocalName == name);
        if (child is null)
        {
            return string.Empty;
        }

        // NAME/DESC wrap the localized text in a TUV element.
        var tuv = child.Descendants().FirstOrDefault(e => e.Name.LocalName == "TUV");
        return (tuv?.Value ?? child.Value).Trim();
    }

    private static string FindFirstValue(XElement scope, params string[] localNames)
    {
        foreach (var name in localNames)
        {
            var element = scope.Descendants().FirstOrDefault(e => e.Name.LocalName == name);
            if (element is not null)
            {
                var text = element.Value.Trim();
                if (!string.IsNullOrEmpty(text))
                {
                    return text;
                }
            }
        }

        return string.Empty;
    }

    private static double? FindFirstDouble(XElement scope, params string[] localNames)
    {
        foreach (var name in localNames)
        {
            foreach (var element in scope.Descendants().Where(e => e.Name.LocalName == name))
            {
                var text = element.Value.Trim();
                if (text.Length == 0)
                {
                    text = (string?)element.Attribute("v") ?? string.Empty;
                }

                if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                {
                    return value;
                }
            }
        }

        return null;
    }

    internal static long? LongAttr(XElement element, string name)
    {
        var text = ((string?)element.Attribute(name))?.Trim();
        if (string.IsNullOrEmpty(text)) return null;
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return long.TryParse(text.AsSpan(2), NumberStyles.AllowHexSpecifier,
                CultureInfo.InvariantCulture, out var hexadecimal)
                ? hexadecimal : null;
        return long.TryParse(text, NumberStyles.Integer,
            CultureInfo.InvariantCulture, out var decimalValue) ? decimalValue : null;
    }
}
