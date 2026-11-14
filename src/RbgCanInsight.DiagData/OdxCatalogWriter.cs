using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace RbgCanInsight.DiagData;

/// <summary>
/// Exports a <see cref="DidCatalog"/> as an ODX 2.2 diagnostic layer (ODX-D) with one BASE-VARIANT.
/// Only facts the catalog states are written: DID services with a typed DATA-OBJECT-PROP when the
/// bit length is known (plus unit / linear scaling / limits / data type when stated), a flagged raw
/// byte-array DOP otherwise, other known services (SID, sub-function, identifier), routines and DTCs.
/// Output is deterministic (no timestamps, stable ordering, deterministic ID sanitising).
/// The result is NOT validated against the official (licensed) ASAM ODX XSD; tests validate it against the public Apache-2.0 community ODX 2.2 XSD of the Eclipse OpenSOVD odx-converter (with one documented ordering deviation of that XSD); <see cref="Validate"/> is a structural
/// self-check only (well-formed, unique NCName IDs, every *-REF resolves).
/// </summary>
public static class OdxCatalogWriter
{
    /// <summary>Text of the SD marker attached to DOPs whose layout the catalog does not state.</summary>
    public const string UnknownLayoutMarker = "rbg: layout unknown";
    public const string AssumedDataTypeMarker = "rbg: data type not stated; A_UINT32 assumed";

    private static readonly XNamespace Xsi = "http://www.w3.org/2001/XMLSchema-instance";

    private static readonly HashSet<string> OdxDataTypes = new(StringComparer.Ordinal)
    {
        "A_INT32", "A_UINT32", "A_FLOAT32", "A_FLOAT64", "A_ASCIISTRING", "A_UNICODE2STRING", "A_BYTEFIELD"
    };

    public static void Write(DidCatalog catalog, string path)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var document = new Builder(catalog).Build();
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var temp = full + ".tmp";
        var settings = new XmlWriterSettings
        {
            Indent = true,
            IndentChars = "  ",
            Encoding = new UTF8Encoding(false),
            NewLineChars = "\n",
            NewLineHandling = NewLineHandling.Replace
        };
        using (var writer = XmlWriter.Create(temp, settings))
            document.Save(writer);
        File.Move(temp, full, overwrite: true);
    }

    /// <summary>Structural check of an ODX file: well-formed XML, ODX root, every ID a unique valid
    /// NCName with a SHORT-NAME, every *-REF/ID-REF resolving, CODED-CONST params complete.
    /// Returns the list of problems; an empty list means the structure is sound. Not an XSD validation.</summary>
    public static IReadOnlyList<string> Validate(string path)
    {
        var issues = new List<string>();
        XDocument document;
        try
        {
            using var reader = XmlReader.Create(path, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null
            });
            document = XDocument.Load(reader);
        }
        catch (Exception ex) when (ex is XmlException or IOException or UnauthorizedAccessException)
        {
            return [$"Cannot read well-formed XML: {ex.Message}"];
        }

        if (document.Root?.Name.LocalName != "ODX")
            issues.Add("Root element is not ODX.");

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var element in document.Descendants())
        {
            if (element.Attribute("ID")?.Value is not { } id) continue;
            try { XmlConvert.VerifyNCName(id); }
            catch (XmlException) { issues.Add($"ID '{id}' is not a valid NCName."); }
            if (!ids.Add(id)) issues.Add($"Duplicate ID '{id}'.");
            if (!element.Elements().Any(child => child.Name.LocalName == "SHORT-NAME" &&
                                                 child.Value.Trim().Length > 0))
                issues.Add($"Element {element.Name.LocalName} '{id}' has no SHORT-NAME.");
        }

        foreach (var element in document.Descendants())
        {
            if (!element.Name.LocalName.EndsWith("-REF", StringComparison.Ordinal)) continue;
            if (element.Attribute("ID-REF")?.Value is not { } target)
            {
                issues.Add($"{element.Name.LocalName} has no ID-REF.");
                continue;
            }

            if (!ids.Contains(target))
                issues.Add($"{element.Name.LocalName} references unknown ID '{target}'.");
        }

        foreach (var param in document.Descendants().Where(e => e.Name.LocalName == "PARAM" &&
                     (string?)e.Attribute(Xsi + "type") == "CODED-CONST"))
        {
            if (!param.Elements().Any(e => e.Name.LocalName == "CODED-VALUE"))
                issues.Add("CODED-CONST PARAM without CODED-VALUE.");
            if (!param.Elements().Any(e => e.Name.LocalName == "DIAG-CODED-TYPE"))
                issues.Add("CODED-CONST PARAM without DIAG-CODED-TYPE.");
        }

        return issues;
    }

    /// <summary>Removes characters that are not legal in XML 1.0 (keeps valid surrogate pairs).</summary>
    internal static string CleanText(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var builder = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                builder.Append(c).Append(text[++i]);
            }
            else if (XmlConvert.IsXmlChar(c))
            {
                builder.Append(c);
            }
        }

        return builder.ToString().Trim();
    }

    /// <summary>Deterministic name sanitiser: ASCII letters, digits and underscore survive, anything
    /// else becomes '_'; a leading digit gets a '_' prefix; empty becomes "unnamed".</summary>
    internal static string Sanitize(string? text)
    {
        var cleaned = CleanText(text);
        var builder = new StringBuilder(cleaned.Length);
        foreach (var c in cleaned)
            builder.Append(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' ? c : '_');
        if (builder.Length == 0) return "unnamed";
        if (builder.Length > 100) builder.Length = 100;
        return char.IsDigit(builder[0]) ? "_" + builder : builder.ToString();
    }

    private static string Num(double value) => value.ToString("R", CultureInfo.InvariantCulture);
    private static string Num(long value) => value.ToString(CultureInfo.InvariantCulture);
    private static bool Stated(double? value) => value is { } v && double.IsFinite(v);

    private sealed record ServiceSpec(
        int Sid, int? Sub, int? Identifier, string Name, string LongName, string Description,
        DidParameter? Did);

    private sealed class Builder(DidCatalog catalog)
    {
        private readonly HashSet<string> _ids = new(StringComparer.Ordinal);
        private readonly HashSet<string> _serviceNames = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _unitIds = new(StringComparer.Ordinal);
        private readonly List<XElement> _units = [];
        private readonly List<XElement> _dops = [];
        private readonly List<XElement> _services = [];
        private readonly List<XElement> _requests = [];
        private readonly List<XElement> _responses = [];
        private string _negativeId = string.Empty;

        private string Id(string prefix, string name)
        {
            var baseId = prefix + "." + Sanitize(name);
            var candidate = baseId;
            for (var n = 2; !_ids.Add(candidate); n++) candidate = baseId + "_" + n;
            return candidate;
        }

        private static XElement Text(string name, string value) => new(name, value);

        private static XElement? Desc(string description) => CleanText(description) is { Length: > 0 } text
            ? new XElement("DESC", new XElement("p", text))
            : null;

        public XDocument Build()
        {
            var ecu = CleanText(catalog.Ecu) is { Length: > 0 } e ? e
                : CleanText(catalog.Name) is { Length: > 0 } n ? n : "Ecu";
            var ecuShort = Sanitize(ecu);
            var containerId = Id("DLC", ecuShort);
            var variantId = Id("BV", ecuShort);

            var specs = CollectSpecs();
            foreach (var spec in specs) AddService(ecuShort, spec);
            var dtcDop = BuildDtcDop(ecuShort);
            if (_services.Count > 0) AddNegativeResponse();

            var dictionary = new XElement("DIAG-DATA-DICTIONARY-SPEC");
            if (dtcDop is not null) dictionary.Add(new XElement("DTC-DOPS", dtcDop));
            if (_dops.Count > 0) dictionary.Add(new XElement("DATA-OBJECT-PROPS", _dops));
            if (_units.Count > 0) dictionary.Add(new XElement("UNIT-SPEC", new XElement("UNITS", _units)));

            var variant = new XElement("BASE-VARIANT", new XAttribute("ID", variantId), Text("SHORT-NAME", ecuShort));
            if (ecuShort != ecu) variant.Add(Text("LONG-NAME", ecu));
            if (dictionary.HasElements) variant.Add(dictionary);
            if (_services.Count > 0) variant.Add(new XElement("DIAG-COMMS", _services));
            if (_requests.Count > 0) variant.Add(new XElement("REQUESTS", _requests));
            var positives = _responses.Where(r => r.Name == "POS-RESPONSE").ToArray();
            var negatives = _responses.Where(r => r.Name == "NEG-RESPONSE").ToArray();
            if (positives.Length > 0) variant.Add(new XElement("POS-RESPONSES", positives));
            if (negatives.Length > 0) variant.Add(new XElement("NEG-RESPONSES", negatives));

            var container = new XElement("DIAG-LAYER-CONTAINER", new XAttribute("ID", containerId),
                Text("SHORT-NAME", ecuShort));
            if (ecuShort != ecu) container.Add(Text("LONG-NAME", ecu));
            container.Add(new XElement("BASE-VARIANTS", variant));

            return new XDocument(new XDeclaration("1.0", "UTF-8", null),
                new XElement("ODX",
                    new XAttribute("MODEL-VERSION", "2.2.0"),
                    new XAttribute(XNamespace.Xmlns + "xsi", Xsi.NamespaceName),
                    new XAttribute(Xsi + "noNamespaceSchemaLocation", "odx.xsd"),
                    container));
        }

        private List<ServiceSpec> CollectSpecs()
        {
            var specs = new List<ServiceSpec>();
            var covered = new HashSet<(int, int?, int?)>();
            var serviceDefinitions = catalog.ServiceDefinitions;

            foreach (var did in catalog.Parameters.Where(p => p.Did is >= 0 and <= 0xFFFF)
                         .OrderBy(p => p.Did).DistinctBy(p => p.Did))
            {
                var source = serviceDefinitions.FirstOrDefault(s => s.ServiceId == 0x22 && s.Identifier == did.Did);
                var name = CleanText(source?.Name) is { Length: > 0 } sn ? sn : CleanText(did.Name);
                var longName = CleanText(did.LongName) is { Length: > 0 } ln ? ln
                    : CleanText(source?.LongName);
                var description = CleanText(did.Description) is { Length: > 0 } d ? d
                    : CleanText(source?.Description);
                specs.Add(new ServiceSpec(0x22, null, did.Did, name, longName, description, did));
                covered.Add((0x22, null, did.Did));
            }

            foreach (var service in serviceDefinitions
                         .Where(s => s.Identifier is null or >= 0 and <= 0xFFFF)
                         .OrderBy(s => s.ServiceId).ThenBy(s => s.Identifier ?? -1)
                         .ThenBy(s => s.SubFunction ?? -1).ThenBy(s => s.Name, StringComparer.Ordinal))
            {
                if (!covered.Add((service.ServiceId, service.SubFunction, service.Identifier))) continue;
                specs.Add(new ServiceSpec(service.ServiceId, service.SubFunction, service.Identifier,
                    CleanText(service.Name), CleanText(service.LongName), CleanText(service.Description), null));
            }

            foreach (var routine in catalog.RoutineDefinitions.Where(r => r.Identifier is >= 0 and <= 0xFFFF)
                         .OrderBy(r => r.Identifier))
            {
                if (specs.Any(s => s.Sid == 0x31 && s.Identifier == routine.Identifier)) continue;
                specs.Add(new ServiceSpec(0x31, null, routine.Identifier, CleanText(routine.Name),
                    CleanText(routine.LongName), CleanText(routine.Description), null));
            }

            return specs;
        }

        private string UniqueServiceName(string name, ServiceSpec spec)
        {
            var fallback = spec.Identifier is { } id
                ? $"Service_{spec.Sid:X2}_{id:X4}"
                : $"Service_{spec.Sid:X2}";
            var baseName = Sanitize(name.Length > 0 ? name : fallback);
            var candidate = baseName;
            for (var n = 2; !_serviceNames.Add(candidate); n++) candidate = baseName + "_" + n;
            return candidate;
        }

        private static XElement CodedConst(string name, string semantic, int bytePosition, long value, int bits) =>
            new("PARAM",
                new XAttribute(Xsi + "type", "CODED-CONST"),
                new XAttribute("SEMANTIC", semantic),
                Text("SHORT-NAME", name),
                Text("BYTE-POSITION", Num(bytePosition)),
                Text("CODED-VALUE", Num(value)),
                new XElement("DIAG-CODED-TYPE",
                    new XAttribute("BASE-DATA-TYPE", "A_UINT32"),
                    new XAttribute(Xsi + "type", "STANDARD-LENGTH-TYPE"),
                    Text("BIT-LENGTH", Num(bits))));

        private static XElement Sdgs(string si, string ti, string text, string captionId) =>
            new("SDGS", new XElement("SDG",
                new XElement("SDG-CAPTION", new XAttribute("ID", captionId), Text("SHORT-NAME", "rbg")),
                new XElement("SD", new XAttribute("SI", si), new XAttribute("TI", ti), text)));

        private void AddService(string ecuShort, ServiceSpec spec)
        {
            var shortName = UniqueServiceName(spec.Name, spec);
            var longName = spec.LongName.Length > 0 ? spec.LongName
                : shortName != spec.Name && spec.Name.Length > 0 ? spec.Name : string.Empty;

            var requestParams = new List<XElement> { CodedConst("sid", "SERVICE-ID", 0, spec.Sid, 8) };
            var responseParams = new List<XElement>();
            var positive = spec.Sid + 0x40 <= 0xFF;
            if (positive) responseParams.Add(CodedConst("sid", "SERVICE-ID", 0, spec.Sid + 0x40, 8));

            var position = 1;
            if (spec.Sub is { } sub)
            {
                requestParams.Add(CodedConst("subfunction", "SUBFUNCTION", position, sub, 8));
                if (positive) responseParams.Add(CodedConst("subfunction", "SUBFUNCTION", position, sub, 8));
                position++;
            }

            if (spec.Identifier is { } identifier)
            {
                requestParams.Add(CodedConst("did", "DATA-ID", position, identifier, 16));
                if (positive) responseParams.Add(CodedConst("did", "DATA-ID", position, identifier, 16));
                position += 2;
            }

            if (spec.Did is { } did && positive)
            {
                var dopId = AddDidDop(shortName, did);
                responseParams.Add(new XElement("PARAM",
                    new XAttribute(Xsi + "type", "VALUE"),
                    new XAttribute("SEMANTIC", "DATA"),
                    Text("SHORT-NAME", "data"),
                    Text("BYTE-POSITION", Num(position)),
                    new XElement("DOP-REF", new XAttribute("ID-REF", dopId))));
            }

            var requestId = Id("RQ", shortName);
            _requests.Add(Named("REQUEST", requestId, shortName, longName, null, new XElement("PARAMS", requestParams)));
            var service = Named("DIAG-SERVICE", Id("DS", shortName), shortName, longName, spec.Description,
                new XElement("REQUEST-REF", new XAttribute("ID-REF", requestId)));
            if (positive)
            {
                var responseId = Id("PR", shortName);
                _responses.Add(Named("POS-RESPONSE", responseId, shortName, longName, null,
                    new XElement("PARAMS", responseParams)));
                service.Add(new XElement("POS-RESPONSE-REFS", new XElement("POS-RESPONSE-REF",
                    new XAttribute("ID-REF", responseId))));
            }

            service.Add(new XElement("NEG-RESPONSE-REFS", new XElement("NEG-RESPONSE-REF",
                new XAttribute("ID-REF", NegativeId()))));
            _services.Add(service);
        }

        private string NegativeId() => _negativeId.Length > 0 ? _negativeId : _negativeId = Id("NR", "general_negative_response");

        private static XElement Named(string element, string id, string shortName, string longName,
            string? description, params object[] content)
        {
            var result = new XElement(element, new XAttribute("ID", id), Text("SHORT-NAME", shortName));
            if (longName.Length > 0) result.Add(Text("LONG-NAME", longName));
            if (description is not null && Desc(description) is { } desc) result.Add(desc);
            result.Add(content);
            return result;
        }

        private string AddNegativeResponse()
        {
            var nrcDopId = Id("DOP", "NRC");
            _dops.Insert(0, new XElement("DATA-OBJECT-PROP", new XAttribute("ID", nrcDopId),
                Text("SHORT-NAME", "NRC"),
                new XElement("COMPU-METHOD", Text("CATEGORY", "IDENTICAL")),
                CodedType("A_UINT32", 8),
                new XElement("PHYSICAL-TYPE", new XAttribute("BASE-DATA-TYPE", "A_UINT32"))));
            var negativeId = NegativeId();
            _responses.Add(Named("NEG-RESPONSE", negativeId, "general_negative_response", string.Empty, null,
                new XElement("PARAMS",
                    CodedConst("sid", "SERVICE-ID", 0, 0x7F, 8),
                    new XElement("PARAM", new XAttribute(Xsi + "type", "MATCHING-REQUEST-PARAM"),
                        Text("SHORT-NAME", "rq_sid"), Text("BYTE-POSITION", "1"),
                        Text("REQUEST-BYTE-POS", "0"), Text("BYTE-LENGTH", "1")),
                    new XElement("PARAM", new XAttribute(Xsi + "type", "VALUE"),
                        Text("SHORT-NAME", "response_code"), Text("BYTE-POSITION", "2"),
                        new XElement("DOP-REF", new XAttribute("ID-REF", nrcDopId))))));
            return negativeId;
        }

        private static XElement CodedType(string baseType, int bits) =>
            new("DIAG-CODED-TYPE",
                new XAttribute("BASE-DATA-TYPE", baseType),
                new XAttribute(Xsi + "type", "STANDARD-LENGTH-TYPE"),
                Text("BIT-LENGTH", Num(bits)));

        private string AddDidDop(string serviceShortName, DidParameter did)
        {
            var dopId = Id("DOP", serviceShortName);
            var dop = new XElement("DATA-OBJECT-PROP", new XAttribute("ID", dopId),
                Text("SHORT-NAME", serviceShortName));

            if (did.BitLength is not > 0)
            {
                // No stated layout: raw byte array of unknown length, flagged so nobody decodes it as a scalar.
                dop.Add(Sdgs("rbg", "layout", UnknownLayoutMarker, Id("SDGC", serviceShortName + "_layout")));
                dop.Add(new XElement("COMPU-METHOD", Text("CATEGORY", "IDENTICAL")));
                dop.Add(new XElement("DIAG-CODED-TYPE",
                    new XAttribute("BASE-DATA-TYPE", "A_BYTEFIELD"),
                    new XAttribute("TERMINATION", "END-OF-PDU"),
                    new XAttribute(Xsi + "type", "MIN-MAX-LENGTH-TYPE"),
                    Text("MIN-LENGTH", "0")));
                dop.Add(new XElement("PHYSICAL-TYPE", new XAttribute("BASE-DATA-TYPE", "A_BYTEFIELD")));
                _dops.Add(dop);
                return dopId;
            }

            var declared = CleanText(did.DataType).ToUpperInvariant();
            var typeStated = OdxDataTypes.Contains(declared);
            var codedType = typeStated ? declared : "A_UINT32";
            if (!typeStated)
                dop.Add(Sdgs("rbg", "datatype", AssumedDataTypeMarker, Id("SDGC", serviceShortName + "_datatype")));

            var linear = Stated(did.Factor) || Stated(did.Offset);
            var factor = Stated(did.Factor) ? did.Factor!.Value : 1d;
            var offset = Stated(did.Offset) ? did.Offset!.Value : 0d;
            if (linear)
            {
                dop.Add(new XElement("COMPU-METHOD",
                    Text("CATEGORY", "LINEAR"),
                    new XElement("COMPU-INTERNAL-TO-PHYS", new XElement("COMPU-SCALES",
                        new XElement("COMPU-SCALE", new XElement("COMPU-RATIONAL-COEFFS",
                            new XElement("COMPU-NUMERATOR", Text("V", Num(offset)), Text("V", Num(factor)))))))));
            }
            else
            {
                dop.Add(new XElement("COMPU-METHOD", Text("CATEGORY", "IDENTICAL")));
            }

            dop.Add(CodedType(codedType, did.BitLength.Value));
            var physical = codedType;
            if (linear)
            {
                if (factor % 1 != 0 || offset % 1 != 0) physical = "A_FLOAT64";
                else if ((factor < 0 || offset < 0) && codedType == "A_UINT32") physical = "A_INT32";
            }

            dop.Add(new XElement("PHYSICAL-TYPE", new XAttribute("BASE-DATA-TYPE", physical)));
            if (CleanText(did.Unit) is { Length: > 0 } unit)
                dop.Add(new XElement("UNIT-REF", new XAttribute("ID-REF", UnitId(unit))));
            if (Stated(did.Minimum) || Stated(did.Maximum))
            {
                var constraint = new XElement("PHYS-CONSTR");
                if (Stated(did.Minimum)) constraint.Add(Text("LOWER-LIMIT", Num(did.Minimum!.Value)));
                if (Stated(did.Maximum)) constraint.Add(Text("UPPER-LIMIT", Num(did.Maximum!.Value)));
                dop.Add(constraint);
            }

            _dops.Add(dop);
            return dopId;
        }

        private string UnitId(string unit)
        {
            if (_unitIds.TryGetValue(unit, out var existing)) return existing;
            var shortName = Sanitize(unit);
            var id = Id("UNIT", shortName);
            _unitIds[unit] = id;
            var element = new XElement("UNIT", new XAttribute("ID", id), Text("SHORT-NAME", shortName),
                Text("DISPLAY-NAME", unit));
            _units.Add(element);
            return id;
        }

        private XElement? BuildDtcDop(string ecuShort)
        {
            var codes = catalog.DtcDefinitions.Where(d => d.Code is >= 0 and <= 0xFFFFFF)
                .DistinctBy(d => d.Code).OrderBy(d => d.Code).ToArray();
            if (codes.Length == 0) return null;

            var names = new HashSet<string>(StringComparer.Ordinal);
            var dtcs = new List<XElement>();
            foreach (var code in codes)
            {
                var name = CleanText(code.Name);
                var baseName = Sanitize(name.Length > 0 ? name : $"DTC_{code.Code:X6}");
                var shortName = baseName;
                for (var n = 2; !names.Add(shortName); n++) shortName = baseName + "_" + n;
                var dtc = new XElement("DTC", new XAttribute("ID", Id("DTC", $"{code.Code:X6}_{shortName}")),
                    Text("SHORT-NAME", shortName));
                if (name.Length > 0 && name != shortName) dtc.Add(Text("LONG-NAME", name));
                if (Desc(code.Description) is { } desc) dtc.Add(desc);
                dtc.Add(Text("TROUBLE-CODE", Num(code.Code)));
                if (CleanText(code.DisplayText) is { Length: > 0 } text) dtc.Add(Text("TEXT", text));
                dtcs.Add(dtc);
            }

            var dopName = ecuShort + "_DTC";
            return new XElement("DTC-DOP", new XAttribute("ID", Id("DTCDOP", dopName)),
                Text("SHORT-NAME", Sanitize(dopName)),
                CodedType("A_UINT32", 24),
                new XElement("PHYSICAL-TYPE", new XAttribute("BASE-DATA-TYPE", "A_UINT32")),
                new XElement("COMPU-METHOD", Text("CATEGORY", "IDENTICAL")),
                new XElement("DTCS", dtcs));
        }
    }
}
