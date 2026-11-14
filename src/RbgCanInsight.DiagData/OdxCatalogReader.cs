using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace RbgCanInsight.DiagData;

/// <summary>
/// Extracts readable DIDs from an ODX diagnostic layer (.odx / .odx-d). A DID is recognised as a
/// DIAG-SERVICE whose request carries a CODED-CONST service id 0x22 plus a 16-bit CODED-CONST
/// identifier. Response parameters are followed through their DOP references to recover unit,
/// scaling (linear COMPU-METHOD) and limits when the file provides them.
/// </summary>
public static class OdxCatalogReader
{
    /// <summary>Loads the first (or only) ECU description of an ODX file or PDX package. Files with
    /// layer inheritance can describe several ECU variants; use <see cref="LoadAll"/> for all of them.</summary>
    public static DidCatalog Load(string path) => LoadAll(path)[0];

    /// <summary>Loads every ECU description in an ODX file or PDX package (a ZIP of ODX files).
    /// Without inheritance this is one catalog containing every service in the file. With
    /// PARENT-REFS each ECU variant gets its own catalog holding the services it effectively has:
    /// inherited from its protocol, functional-group and base-variant layers, minus
    /// NOT-INHERITED-DIAG-COMMS exclusions, with its own services overriding inherited ones that
    /// share a short name.</summary>
    public static IReadOnlyList<DidCatalog> LoadAll(string path)
    {
        if (Path.GetExtension(path).Equals(".pdx", StringComparison.OrdinalIgnoreCase))
        {
            return BuildCatalogs(ReadPdxDocuments(path), path);
        }

        return BuildCatalogs([ReadSecureDocument(path)], path);
    }

    /// <summary>Same as <see cref="LoadAll"/> for ODX documents that are already in memory (for
    /// example the members of a PDX package). <paramref name="sourcePath"/> is recorded as the origin.</summary>
    public static IReadOnlyList<DidCatalog> LoadFromDocuments(IReadOnlyList<XDocument> documents, string sourcePath) =>
        BuildCatalogs(documents, sourcePath);

    private const long MaxMemberBytes = 256L * 1024 * 1024;
    private const long MaxTotalBytes = 512L * 1024 * 1024;
    private const int MaxPdxDocuments = 2048;
    private const int MaxLayerInheritanceDepth = 64;

    private static XmlReaderSettings SecureXmlSettings() => new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        MaxCharactersInDocument = MaxMemberBytes
    };

    private static XDocument ReadSecureDocument(string path)
    {
        if (new FileInfo(path).Length > MaxMemberBytes)
            throw new InvalidDataException("ODX document exceeds the safe 256 MiB size limit.");
        using (var guard = XmlReader.Create(path, SecureXmlSettings()))
            XmlDepthGuard.Check(guard);
        using var reader = XmlReader.Create(path, SecureXmlSettings());
        return XDocument.Load(reader, LoadOptions.None);
    }

    private static IReadOnlyList<XDocument> ReadPdxDocuments(string path)
    {
        using var archive = System.IO.Compression.ZipFile.OpenRead(path);
        if (archive.Entries.Count > 8_192)
            throw new InvalidDataException("PDX archive contains too many members.");
        var documents = new List<XDocument>();
        long total = 0;
        foreach (var entry in archive.Entries.OrderBy(item => item.FullName, StringComparer.OrdinalIgnoreCase))
        {
            // Diagnostic layers live in .odx-d (and plain .odx); other members (communication
            // parameters, vehicle info, catalogs) carry no services or DIDs.
            var name = entry.Name;
            if (!(name.EndsWith(".odx-d", StringComparison.OrdinalIgnoreCase) ||
                  name.EndsWith(".odx", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (entry.Length > MaxMemberBytes)
                throw new InvalidDataException($"PDX member '{entry.FullName}' exceeds the 256 MiB limit.");
            if (documents.Count >= MaxPdxDocuments || entry.Length > MaxTotalBytes - total)
                throw new InvalidDataException("PDX diagnostic XML exceeds the safe document-count or aggregate size limit.");
            total += entry.Length;

            using (var guardStream = entry.Open())
            using (var guard = XmlReader.Create(guardStream, SecureXmlSettings()))
                XmlDepthGuard.Check(guard);
            using var stream = entry.Open();
            using var reader = XmlReader.Create(stream, SecureXmlSettings());
            documents.Add(XDocument.Load(reader, LoadOptions.None));
        }

        if (documents.Count == 0)
        {
            throw new InvalidDataException("The PDX package contains no .odx-d diagnostic layer files.");
        }

        return documents;
    }

    private static readonly HashSet<string> LayerNames =
        ["PROTOCOL", "FUNCTIONAL-GROUP", "BASE-VARIANT", "ECU-VARIANT", "ECU-SHARED-DATA"];

    private sealed class Layer(XElement element)
    {
        public XElement Element { get; } = element;
        public string Id { get; } = (string?)element.Attribute("ID") ?? string.Empty;
        public string Name { get; } = ChildValue(element, "SHORT-NAME");
        public string Kind { get; } = element.Name.LocalName;
        public List<string> ParentIds { get; } = [];
        public HashSet<string> NotInherited { get; } = new(StringComparer.Ordinal);
    }

    private static IReadOnlyList<DidCatalog> BuildCatalogs(IReadOnlyList<XDocument> documents, string sourcePath)
    {
        var indexedNodes = documents.SelectMany(document => document.Descendants())
            .Where(element => !string.IsNullOrWhiteSpace(
                (string?)element.Attribute("ID"))).ToArray();
        // Cache the lexical owner of each ID once. On large PDX files,
        // scanning every node's ancestor chain for every ECU variant is
        // unnecessarily quadratic in the number of variants and datatypes.
        var globalIdNodes = new List<XElement>();
        var idNodesByLayer = new Dictionary<XElement, List<XElement>>();
        foreach (var node in indexedNodes)
        {
            var owner = node.Ancestors().FirstOrDefault(ancestor =>
                LayerNames.Contains(ancestor.Name.LocalName));
            if (owner is null)
                globalIdNodes.Add(node);
            else
            {
                if (!idNodesByLayer.TryGetValue(owner, out var bucket))
                    idNodesByLayer[owner] = bucket = [];
                bucket.Add(node);
            }
        }
        // Conflicting IDs are not an authorization to pick the first one in
        // a PDX archive. A flat/unspecified scope may fail to resolve them;
        // a real ECU variant builds its own scoped index below.
        var elementsById = IndexUniqueOdxIds(indexedNodes);
        var shortNameIndex =
            DiagnosticParameterHierarchyReader.IndexOdxShortNames(elementsById);

        var layers = documents.SelectMany(document => document.Descendants())
            .Where(element => LayerNames.Contains(element.Name.LocalName))
            .Select(element => new Layer(element)).ToList();
        // An ODX package can contain multiple layers with the same ID in
        // separate documents. An ID-REF without an unambiguous target is
        // never authorization to inherit the first matching ECU definition.
        var layerGroups = layers.Where(layer => layer.Id.Length > 0)
            .GroupBy(layer => layer.Id, StringComparer.Ordinal).ToArray();
        var ambiguousLayerIds = layerGroups.Where(group => group.Count() > 1)
            .Select(group => group.Key).ToHashSet(StringComparer.Ordinal);
        var layersById = layerGroups.Where(group => !ambiguousLayerIds.Contains(group.Key))
            .ToDictionary(group => group.Key, group => group.Single(), StringComparer.Ordinal);
        foreach (var layer in layers)
        {
            foreach (var parent in layer.Element.Elements().Where(e => e.Name.LocalName == "PARENT-REFS")
                         .Elements().Where(e => e.Name.LocalName == "PARENT-REF"))
            {
                if ((string?)parent.Attribute("ID-REF") is { Length: > 0 } parentId)
                {
                    if (ambiguousLayerIds.Contains(parentId))
                        throw new InvalidDataException(
                            $"ODX parent layer ID '{parentId}' is ambiguous; cannot safely resolve ECU inheritance.");
                    if (!layersById.ContainsKey(parentId))
                        throw new InvalidDataException(
                            $"ODX parent layer ID '{parentId}' is missing; refusing incomplete ECU inheritance.");
                    layer.ParentIds.Add(parentId);
                }

                foreach (var excluded in parent.Descendants().Where(e => e.Name.LocalName == "DIAG-COMM-SNREF"))
                {
                    if ((string?)excluded.Attribute("SHORT-NAME") is { Length: > 0 } shortName)
                        layer.NotInherited.Add(shortName);
                }
            }

            foreach (var excluded in layer.Element.Elements()
                         .Where(e => e.Name.LocalName == "NOT-INHERITED-DIAG-COMMS")
                         .Descendants().Where(e => e.Name.LocalName == "DIAG-COMM-SNREF"))
            {
                if ((string?)excluded.Attribute("SHORT-NAME") is { Length: > 0 } shortName)
                    layer.NotInherited.Add(shortName);
            }
        }

        var fileName = Path.GetFileNameWithoutExtension(sourcePath);
        if (!layers.Any(layer => layer.ParentIds.Count > 0))
        {
            // Independent ECU variants with their own DIAG-COMMS do not share
            // an effective service set. Previously the flat-file shortcut
            // mixed all services into one catalog and silently crossed ECUs.
            var independentEcus = layers.Where(layer => layer.Kind == "ECU-VARIANT")
                .ToArray();
            // Even an empty sibling variant must remain separate. It is
            // safer to report no DID definitions than inherit another ECU's
            // services merely because this variant has no local service.
            if (independentEcus.Length > 1)
            {
                return independentEcus.Select(layer =>
                {
                    var ownServices = layer.Element.Elements()
                        .Where(element => element.Name.LocalName == "DIAG-COMMS")
                        .SelectMany(element => element.Elements())
                        .Where(element => element.Name.LocalName == "DIAG-SERVICE")
                        .ToArray();
                    var localIds = ScopeOdxIds(globalIdNodes, idNodesByLayer,
                        [layer.Element]);
                    var localIndex =
                        DiagnosticParameterHierarchyReader.IndexOdxShortNames(localIds);
                    var name = layer.Name.Length > 0 ? layer.Name : fileName;
                    return BuildCatalog(documents, ownServices, localIds,
                        name, name, sourcePath,
                        [new DiagnosticVariant(name, layer.Kind, "Effective variant")],
                        localIndex);
                }).ToArray();
            }

            var ecuName = documents.SelectMany(document => document.Descendants())
                .Where(element => element.Name.LocalName is "ECU-VARIANT" or "BASE-VARIANT" or "DIAG-LAYER-CONTAINER")
                .Select(element => ChildValue(element, "SHORT-NAME"))
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
                ?? fileName;
            var allServices = documents.SelectMany(document => document.Descendants())
                .Where(element => element.Name.LocalName == "DIAG-SERVICE").ToArray();
            return [BuildCatalog(documents, allServices, elementsById, ecuName, fileName, sourcePath,
                ParseVariants(documents), shortNameIndex)];
        }

        // Inheritance: one catalog per leaf layer that is an ECU variant (or, without ECU
        // variants, per base variant that no other layer inherits from).
        var parents = layers.SelectMany(layer => layer.ParentIds).ToHashSet(StringComparer.Ordinal);
        var leaves = layers.Where(layer => layer.Kind == "ECU-VARIANT").ToList();
        if (leaves.Count == 0)
            leaves = layers.Where(layer => layer.Kind == "BASE-VARIANT" && !parents.Contains(layer.Id)).ToList();
        if (leaves.Count == 0) leaves = layers.Where(layer => !parents.Contains(layer.Id)).ToList();
        if (leaves.Count == 0) leaves = layers; // every layer is someone's parent (cyclic declarations)

        var catalogs = new List<DidCatalog>();
        foreach (var leaf in leaves)
        {
            var chain = new List<Layer>();
            var services = EffectiveServices(leaf, layersById, chain, new HashSet<Layer>());
            var variants = chain.Select(layer => new DiagnosticVariant(
                layer.Name, layer.Kind,
                layer == leaf
                    ? "Effective variant"
                    : "Inherited by " + leaf.Name)).ToArray();
            var localIds = ScopeOdxIds(globalIdNodes, idNodesByLayer,
                chain.Select(layer => layer.Element));
            var localIndex =
                DiagnosticParameterHierarchyReader.IndexOdxShortNames(localIds);
            catalogs.Add(BuildCatalog(documents, services, localIds,
                leaf.Name.Length > 0 ? leaf.Name : fileName, leaf.Name.Length > 0 ? leaf.Name : fileName,
                sourcePath, variants, localIndex));
        }

        return catalogs;
    }

    /// <summary>Keep each ID only when its target is unique in the effective
    /// layer scope. Duplicate IDs across sibling variants are resolved within
    /// each variant; duplicates *inside* a scope are omitted rather than
    /// selecting an arbitrary request/response/DOP for numeric DID decoding.</summary>
    private static IReadOnlyDictionary<string, XElement> IndexUniqueOdxIds(
        IEnumerable<XElement> nodes)
    {
        var unique = new Dictionary<string, XElement>(StringComparer.Ordinal);
        var ambiguous = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            var id = (string?)node.Attribute("ID");
            if (string.IsNullOrWhiteSpace(id) || ambiguous.Contains(id)) continue;
            if (!unique.TryAdd(id, node))
            {
                unique.Remove(id);
                ambiguous.Add(id);
            }
        }
        return unique;
    }

    private static IReadOnlyDictionary<string, XElement> ScopeOdxIds(
        IReadOnlyList<XElement> globalNodes,
        IReadOnlyDictionary<XElement, List<XElement>> nodesByLayer,
        IEnumerable<XElement> activeLayers)
    {
        // Only visit globally visible IDs and the effective parent/leaf
        // chain. Sibling variants are never scanned or selected by accident.
        var visible = globalNodes.Concat(activeLayers.Distinct().SelectMany(layer =>
            nodesByLayer.TryGetValue(layer, out var values)
                ? values : Enumerable.Empty<XElement>()));
        return IndexUniqueOdxIds(visible);
    }

    /// <summary>Services a layer effectively has: parents first (so the child can override them),
    /// without the services this layer lists as not inherited, then the layer's own services.</summary>
    private static List<XElement> EffectiveServices(
        Layer layer, IReadOnlyDictionary<string, Layer> layersById, List<Layer> chain, HashSet<Layer> visiting)
    {
        var result = new List<XElement>();
        // Returning an empty service set on a cycle could silently export a
        // plausible but incomplete DID/PAR catalog for the wrong ECU variant.
        if (!visiting.Add(layer))
            throw new InvalidDataException(
                $"Cyclic ODX layer inheritance at '{layer.Id}' ('{layer.Name}').");
        // Bound recursive graph traversal independently from the XML character
        // budget: a malformed but small package can declare thousands of layers.
        if (visiting.Count > MaxLayerInheritanceDepth)
            throw new InvalidDataException(
                $"ODX layer inheritance exceeds the {MaxLayerInheritanceDepth}-layer safety limit.");
        foreach (var parentId in layer.ParentIds)
        {
            var inherited = EffectiveServices(layersById[parentId], layersById, chain, visiting);
            result.AddRange(inherited.Where(service => !layer.NotInherited.Contains(ChildValue(service, "SHORT-NAME"))));
        }

        var own = layer.Element.Elements().Where(e => e.Name.LocalName == "DIAG-COMMS")
            .Elements().Where(e => e.Name.LocalName == "DIAG-SERVICE").ToList();
        var ownNames = own.Select(service => ChildValue(service, "SHORT-NAME")).ToHashSet(StringComparer.Ordinal);
        result.RemoveAll(service => ownNames.Contains(ChildValue(service, "SHORT-NAME")));
        result.AddRange(own);
        if (!chain.Contains(layer)) chain.Add(layer);
        visiting.Remove(layer);
        return result;
    }

    private static DidCatalog BuildCatalog(
        IReadOnlyList<XDocument> documents,
        IEnumerable<XElement> serviceElements,
        IReadOnlyDictionary<string, XElement> elementsById,
        string ecuName,
        string catalogName,
        string sourcePath,
        IReadOnlyList<DiagnosticVariant> variants,
        IReadOnlyDictionary<string, XElement?> shortNameIndex)
    {
        var path = sourcePath;
        var parameters = new Dictionary<int, DidParameter>();
        var services = new List<DiagnosticServiceDefinition>();
        var conditions = new List<DiagnosticServiceCondition>();
        var routines = new Dictionary<int, DiagnosticRoutine>();
        foreach (var service in serviceElements)
        {
            var requestRef = Child(service, "REQUEST-REF")?.Attribute("ID-REF")?.Value;
            if (requestRef is null || !elementsById.TryGetValue(requestRef, out var request))
            {
                continue;
            }

            var constants = request.Descendants()
                .Where(element => element.Name.LocalName == "PARAM")
                .Select(param => new
                {
                    Param = param,
                    Semantic = (string?)param.Attribute("SEMANTIC") ?? string.Empty,
                    Coded = TryParseUnsigned(ChildValue(param, "CODED-VALUE")),
                    BitLength = TryParseInt(param.Descendants()
                        .FirstOrDefault(item => item.Name.LocalName == "BIT-LENGTH")?.Value)
                })
                .Where(item => item.Coded is not null)
                .ToArray();

            var sid = constants.FirstOrDefault(item =>
                item.Semantic.Contains("SERVICE-ID", StringComparison.OrdinalIgnoreCase) ||
                item.BitLength == 8);
            if (sid?.Coded is not { } sidValue || sidValue > byte.MaxValue)
            {
                continue;
            }

            var shortName = ChildValue(service, "SHORT-NAME");
            var serviceLongName = ChildValue(service, "LONG-NAME");
            var serviceDescription = service.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "DESC")?.Value.Trim() ?? string.Empty;
            var subFunction = constants.FirstOrDefault(item =>
                !ReferenceEquals(item, sid) &&
                item.Semantic.Contains("SUB", StringComparison.OrdinalIgnoreCase) &&
                item.Coded <= byte.MaxValue)?.Coded;
            var fixedIdentifier = constants.FirstOrDefault(item =>
                !ReferenceEquals(item, sid) && item.BitLength is >= 16)?.Coded;

            services.Add(new DiagnosticServiceDefinition(
                (byte)sidValue,
                subFunction is { } sub ? (byte)sub : null,
                fixedIdentifier is { } identifier && identifier <= int.MaxValue ? (int)identifier : null,
                shortName,
                serviceLongName,
                serviceDescription,
                ecuName,
                Path.GetFullPath(path)));

            var serviceConditions = ReadPreConditions(service, elementsById)
                .Select(item => new DiagnosticServiceCondition(
                    (byte)sidValue,
                    fixedIdentifier is { } conditionId && conditionId <= int.MaxValue ? (int)conditionId : null,
                    shortName, item.Kind, item.Chart, item.State, ecuName, Path.GetFullPath(path)))
                .ToArray();
            conditions.AddRange(serviceConditions);

            if (sidValue == 0x31 && fixedIdentifier is { } routineId and <= 0xFFFF)
            {
                // Several allowed states are kept as "A | B": the prerequisite checker evaluates
                // them as a set, and an unparseable name is reported as unverifiable, never guessed.
                string Required(DiagnosticConditionKind kind) => string.Join(" | ",
                    serviceConditions.Where(item => item.Kind == kind).Select(item => item.State).Distinct());
                routines.TryAdd((int)routineId, new DiagnosticRoutine(
                    (int)routineId,
                    shortName,
                    serviceLongName,
                    serviceDescription,
                    ecuName,
                    Path.GetFullPath(path),
                    RequiredSession: Required(DiagnosticConditionKind.Session),
                    RequiredSecurityLevel: Required(DiagnosticConditionKind.Security)));
            }

            // 0x22 parameters live in positive responses; 0x2E engineering
            // definitions generally live in the request payload. Both
            // describe a DID, but only 0x22 gets response-decoding metadata.
            if (sidValue is not (0x22 or 0x2E))
            {
                continue;
            }

            var didParam = constants.FirstOrDefault(item =>
                !ReferenceEquals(item, sid) && item.BitLength == 16) ??
                constants.FirstOrDefault(item => !ReferenceEquals(item, sid));
            if (didParam?.Coded is not { } did || did > 0xFFFF)
            {
                continue;
            }

            var name = shortName;
            var longName = serviceLongName;
            var description = serviceDescription;

            var responseMetadata = sidValue == 0x22
                ? ResolveResponseDop(service, elementsById, shortNameIndex)
                : (Unit: string.Empty, Min: (double?)null, Max: (double?)null,
                   Factor: (double?)null, Offset: (double?)null,
                   DataType: string.Empty, BitLength: (int?)null,
                   ByteOrder: "Not declared", VerifiedConversion: false);
            var (unit, minimum, maximum, factor, offset, dataType, bitLength,
                byteOrder, verifiedConversion) = responseMetadata;
            IReadOnlyList<DiagnosticFieldDefinition> resolvedFields = sidValue == 0x22
                ? ResolveResponseFields(service, elementsById, shortNameIndex) : [];
            var fields = resolvedFields.Count > 0 ? resolvedFields : bitLength is > 0
                ? new[]
                {
                    new DiagnosticFieldDefinition(
                        TrimServiceName(name), longName,
                        "ODX response type metadata has no verified data-record position; " +
                        "do not decode it as a whole-DID scalar. " + description,
                        -1, bitLength.Value,
                        dataType, unit, minimum, maximum, factor, offset,
                        dataType.Contains("INT", StringComparison.OrdinalIgnoreCase) &&
                        !dataType.Contains("UINT", StringComparison.OrdinalIgnoreCase),
                        byteOrder, SupportsPhysicalConversion: false)
                }
                : [];

            // Metadata from the first response DOP is not whole-DID physical
            // metadata if the record contains multiple fields, an unpositioned
            // field or a non-linear conversion. Keep field definitions and
            // engineering tree, but never advertise a verified whole-DID scale.
            var wholeDidVerified = resolvedFields.Count == 1 &&
                resolvedFields[0].BitOffset == 0 &&
                resolvedFields[0].SupportsPhysicalConversion;
            if (!wholeDidVerified)
            {
                unit = string.Empty;
                minimum = null;
                maximum = null;
                factor = null;
                offset = null;
                dataType = string.Empty;
                bitLength = null;
            }

            var key = (int)did;
            var engineering = sidValue == 0x22
                ? DiagnosticParameterHierarchyReader.FromOdx(service, elementsById, shortNameIndex)
                : DiagnosticParameterHierarchyReader.FromOdxRequest(request, elementsById, shortNameIndex);
            DiagnosticDidDefinitionMerger.AddOrMerge(parameters, new DidParameter(
                key, TrimServiceName(name), longName, description, unit,
                minimum, maximum, factor, offset, dataType, bitLength, ecuName,
                Path.GetFullPath(path), fields, engineering),
                $"ODX SID 0x{sidValue:X2}: {shortName}");
        }

        var troubleCodes = documents
            .SelectMany(document => ParseTroubleCodes(document, ecuName, path, elementsById, shortNameIndex))
            .GroupBy(dtc => dtc.Code).Select(group => group.First())
            .OrderBy(dtc => dtc.Code).ToArray();
        var connections = documents
            .SelectMany(document => ParseConnections(document, ecuName, path)).Take(1).ToArray();

        return new DidCatalog(
            catalogName,
            ecuName,
            Path.GetFullPath(path),
            parameters.Values.OrderBy(parameter => parameter.Did).ToArray(),
            troubleCodes,
            routines.Values.OrderBy(routine => routine.Identifier).ToArray(),
            services,
            connections,
            variants,
            conditions);
    }

    /// <summary>ISO 22901 pre-condition states of a service: PRE-CONDITION-STATE-REF → STATE inside a
    /// STATE-CHART whose SEMANTIC (or name) says SESSION / SECURITY. Anything else is reported as
    /// "Other" rather than dropped. Only declared references are read; nothing is inferred.</summary>
    private static IEnumerable<(DiagnosticConditionKind Kind, string Chart, string State)> ReadPreConditions(
        XElement service, IReadOnlyDictionary<string, XElement> elementsById)
    {
        foreach (var reference in service.Descendants().Where(e => e.Name.LocalName == "PRE-CONDITION-STATE-REF"))
        {
            var id = (string?)reference.Attribute("ID-REF");
            if (id is null || !elementsById.TryGetValue(id, out var state)) continue;
            var chart = state.Ancestors().FirstOrDefault(e => e.Name.LocalName == "STATE-CHART");
            var chartName = chart is null ? string.Empty : ChildValue(chart, "SHORT-NAME");
            var semantic = chart is null ? string.Empty : ChildValue(chart, "SEMANTIC");
            var kind =
                semantic.Contains("SESSION", StringComparison.OrdinalIgnoreCase) ||
                chartName.Contains("SESSION", StringComparison.OrdinalIgnoreCase) ? DiagnosticConditionKind.Session
                : semantic.Contains("SECUR", StringComparison.OrdinalIgnoreCase) ||
                  chartName.Contains("SECUR", StringComparison.OrdinalIgnoreCase) ? DiagnosticConditionKind.Security
                : DiagnosticConditionKind.Other;
            var name = ChildValue(state, "SHORT-NAME");
            yield return (kind, chartName, name.Length > 0 ? name : id);
        }
    }
    private static IReadOnlyList<DiagnosticVariant> ParseVariants(IEnumerable<XDocument> documents) =>
        documents.SelectMany(document => document.Descendants())
            .Where(element => element.Name.LocalName is "ECU-VARIANT" or "BASE-VARIANT" or "PROTOCOL")
            .Select(element => new DiagnosticVariant(
                ChildValue(element, "SHORT-NAME"),
                element.Name.LocalName,
                element.Descendants().FirstOrDefault(item => item.Name.LocalName == "DESC")?.Value.Trim() ?? string.Empty))
            .Where(variant => !string.IsNullOrWhiteSpace(variant.Name))
            .DistinctBy(variant => (variant.Name, variant.Type))
            .ToArray();

    private static IReadOnlyList<DiagnosticConnection> ParseConnections(
        XDocument document,
        string ecuName,
        string path)
    {
        uint? FindIdentifier(params string[] tokens)
        {
            foreach (var element in document.Descendants())
            {
                var name = ChildValue(element, "SHORT-NAME");
                if (string.IsNullOrWhiteSpace(name) ||
                    !tokens.All(token => name.Contains(token, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                var valueText = element.Descendants()
                    .FirstOrDefault(item => item.Name.LocalName is "VALUE" or "CODED-VALUE")?.Value;
                var value = TryParseUnsigned(valueText);
                if (value is <= uint.MaxValue)
                {
                    return (uint)value.Value;
                }
            }

            return null;
        }

        var request = FindIdentifier("REQUEST", "ID") ?? FindIdentifier("REQ", "CAN");
        var response = FindIdentifier("RESPONSE", "ID") ?? FindIdentifier("RESP", "CAN");
        if (request is null && response is null)
        {
            return [];
        }

        var extended = (request ?? response) > 0x7FF;
        return
        [
            new DiagnosticConnection(
                ecuName, request, response, extended,
                extended ? "29-bit physical" : "11-bit physical", ecuName, Path.GetFullPath(path))
        ];
    }

    private static IReadOnlyList<DiagnosticTroubleCode> ParseTroubleCodes(
        XDocument document,
        string ecuName,
        string path,
        IReadOnlyDictionary<string, XElement> elementsById,
        IReadOnlyDictionary<string, XElement?> shortNameIndex)
    {
        var env = ReadEnvironmentData(document, elementsById, shortNameIndex);
        var result = new Dictionary<int, DiagnosticTroubleCode>();
        foreach (var dtc in document.Descendants().Where(element => element.Name.LocalName == "DTC"))
        {
            var codeText = ChildValue(dtc, "TROUBLE-CODE");
            var code = TryParseUnsigned(codeText);
            if (code is null)
            {
                code = TryParseUnsigned((string?)dtc.Attribute("TROUBLE-CODE"));
            }

            if (code is not { } value || value > 0xFFFFFF)
            {
                continue;
            }

            var name = ChildValue(dtc, "SHORT-NAME");
            var displayText = ChildValue(dtc, "TEXT");
            if (string.IsNullOrWhiteSpace(displayText))
            {
                displayText = ChildValue(dtc, "DISPLAY-TROUBLE-CODE");
            }

            var description = dtc.Descendants()
                .FirstOrDefault(element => element.Name.LocalName == "DESC")?.Value.Trim() ?? string.Empty;
            result.TryAdd((int)value, new DiagnosticTroubleCode(
                (int)value,
                name,
                displayText,
                description,
                ecuName,
                Path.GetFullPath(path),
                Pick(env.Snapshot, env.AllSnapshot, (int)value),
                Pick(env.Extended, env.AllExtended, (int)value)));
        }

        return result.Values.OrderBy(dtc => dtc.Code).ToArray();

        static IReadOnlyList<DiagnosticFieldDefinition>? Pick(
            Dictionary<int, List<DiagnosticFieldDefinition>> specific,
            List<DiagnosticFieldDefinition> all, int code)
        {
            // A block naming this DTC wins over an ALL-VALUE block; they are not concatenated
            // because both would claim the same byte positions.
            if (specific.TryGetValue(code, out var own) && own.Count > 0) return own;
            return all.Count > 0 ? all : null;
        }
    }

    private static readonly HashSet<string> ResponseHeaderSemantics =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "SERVICE-ID", "ID", "DATA-ID", "DID",
            "SUBFUNCTION", "SUB-FUNCTION", "RESERVED"
        };

    private static bool IsResponseDataParameter(XElement param)
    {
        var semantic = (string?)param.Attribute("SEMANTIC") ?? string.Empty;
        if (ResponseHeaderSemantics.Contains(semantic))
            return false;
        return !semantic.Contains("SERVICE-ID", StringComparison.OrdinalIgnoreCase) &&
               !(semantic.Contains("ID", StringComparison.OrdinalIgnoreCase) &&
                 !semantic.Contains("DATA", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Resolve a scalar DOP only through an exact ID or a globally
    /// unique short name. A STRUCTURE and an ambiguous SNREF are not a
    /// one-field physical decoder, even when their children have coded types.</summary>
    private static XElement? ResolveScalarDop(
        XElement param,
        IReadOnlyDictionary<string, XElement> byId,
        IReadOnlyDictionary<string, XElement?> byShortName)
    {
        var reference = param.Elements().FirstOrDefault(e =>
            e.Name.LocalName is "DOP-REF" or "DOP-SNREF");
        if (reference is null) return null;

        XElement? target;
        if (reference.Name.LocalName == "DOP-SNREF")
        {
            var name = (string?)reference.Attribute("SHORT-NAME");
            if (string.IsNullOrWhiteSpace(name) ||
                !byShortName.TryGetValue(name, out target))
                return null;
        }
        else
        {
            var id = (string?)reference.Attribute("ID-REF");
            if (string.IsNullOrWhiteSpace(id) || !byId.TryGetValue(id, out target))
                return null;
        }
        return target?.Name.LocalName is "DATA-OBJECT-PROP" or "DOP-BASE" or "DTC-DOP"
            ? target : null;
    }

    private static (string Unit, double? Min, double? Max, double? Factor, double? Offset,
        string DataType, int? BitLength, string ByteOrder, bool VerifiedConversion) ResolveResponseDop(
            XElement service,
            IReadOnlyDictionary<string, XElement> elementsById,
            IReadOnlyDictionary<string, XElement?> shortNameIndex)
    {
        // Follow POS-RESPONSE-REF -> response PARAM (semantic DATA) -> DOP-REF -> DATA-OBJECT-PROP.
        foreach (var responseRef in service.Descendants()
                     .Where(e => e.Name.LocalName == "POS-RESPONSE-REF"))
        {
            var idRef = (string?)responseRef.Attribute("ID-REF");
            if (idRef is null || !elementsById.TryGetValue(idRef, out var response))
            {
                continue;
            }

            foreach (var param in response.Descendants().Where(e =>
                         e.Name.LocalName == "PARAM" && IsResponseDataParameter(e)))
            {
                var dop = ResolveScalarDop(param, elementsById, shortNameIndex);
                if (dop is null) continue;

                var unit = string.Empty;
                var unitRef = dop.Descendants()
                    .FirstOrDefault(e => e.Name.LocalName == "UNIT-REF")?.Attribute("ID-REF")?.Value;
                if (unitRef is not null && elementsById.TryGetValue(unitRef, out var unitElement))
                {
                    unit = ChildValue(unitElement, "DISPLAY-NAME");
                    if (string.IsNullOrWhiteSpace(unit))
                    {
                        unit = ChildValue(unitElement, "SHORT-NAME");
                    }
                }

                var (factor, offset, verifiedConversion) = ResolveLinearConversion(dop);

                double? minimum = TryParseDouble(dop.Descendants()
                    .FirstOrDefault(e => e.Name.LocalName == "LOWER-LIMIT")?.Value);
                double? maximum = TryParseDouble(dop.Descendants()
                    .FirstOrDefault(e => e.Name.LocalName == "UPPER-LIMIT")?.Value);

                var codedType = dop.Descendants()
                    .FirstOrDefault(e => e.Name.LocalName == "DIAG-CODED-TYPE");
                var dataType = (string?)codedType?.Attribute("BASE-DATA-TYPE") ?? string.Empty;
                var order = CodedByteOrder(codedType);
                int? bitLength = TryParseInt(codedType?.Descendants()
                    .FirstOrDefault(e => e.Name.LocalName == "BIT-LENGTH")?.Value);

                if (!string.IsNullOrEmpty(unit) || factor is not null || minimum is not null ||
                    bitLength is not null || !string.IsNullOrEmpty(dataType))
                {
                    return (unit, minimum, maximum, factor, offset, dataType, bitLength,
                        order, verifiedConversion);
                }
            }
        }

        return (string.Empty, null, null, null, null, string.Empty, null, "Not declared", false);
    }

    private static IReadOnlyList<DiagnosticFieldDefinition> ResolveResponseFields(
        XElement service,
        IReadOnlyDictionary<string, XElement> elementsById,
        IReadOnlyDictionary<string, XElement?> shortNameIndex)
    {
        var fields = new List<DiagnosticFieldDefinition>();
        foreach (var responseRef in service.Descendants().Where(element => element.Name.LocalName == "POS-RESPONSE-REF"))
        {
            var responseId = (string?)responseRef.Attribute("ID-REF");
            if (responseId is null || !elementsById.TryGetValue(responseId, out var response)) continue;
            var responseParams = response.Descendants()
                .Where(element => element.Name.LocalName == "PARAM").ToArray();
            var dataParams = responseParams.Where(IsResponseDataParameter).ToArray();
            var knownDataPositions = dataParams.Select(param =>
                TryParseInt(ChildValue(param, "BYTE-POSITION")))
                .Where(position => position is >= 0)
                .Select(position => position!.Value).ToArray();
            var sidInResponse = responseParams.Any(param =>
                ((string?)param.Attribute("SEMANTIC"))?.Contains("SERVICE-ID",
                    StringComparison.OrdinalIgnoreCase) == true);
            // The first three bytes of a 0x62 response are SID + 16-bit DID.
            // Some ODX descriptions express DATA positions relative to the
            // entire response; others use offsets relative to the DID record.
            // A complete header, or data positions starting at byte >= 3,
            // identifies an absolute response layout. Smaller positions are
            // data-record-relative; do not collapse byte 1/2 to bit offset 0.
            var recordOrigin = sidInResponse ||
                knownDataPositions.Length > 0 && knownDataPositions.Min() >= 3
                ? 3 : 0;
            foreach (var param in dataParams)
            {
                var field = BuildField(param, recordOrigin, elementsById, shortNameIndex);
                if (field is not null) fields.Add(field);
            }
        }
        return fields.OrderBy(field => field.BitOffset).ToArray();
    }

    /// <summary>One PARAM becomes one field. <paramref name="recordOrigin"/> is the byte that the
    /// record-relative offset starts at; a -1 offset means the position is not declared.</summary>
    private static DiagnosticFieldDefinition? BuildField(
        XElement param, int recordOrigin,
        IReadOnlyDictionary<string, XElement> elementsById,
        IReadOnlyDictionary<string, XElement?> shortNameIndex)
    {
        var dop = ResolveScalarDop(param, elementsById, shortNameIndex);
        if (dop is null) return null;
        var coded = dop.Descendants().FirstOrDefault(element => element.Name.LocalName == "DIAG-CODED-TYPE");
        var length = TryParseInt(coded?.Descendants().FirstOrDefault(element => element.Name.LocalName == "BIT-LENGTH")?.Value);
        if (length is not > 0) return null;
        var declaredBytePosition = TryParseInt(ChildValue(param, "BYTE-POSITION"));
        var declaredBitPosition = TryParseInt(ChildValue(param, "BIT-POSITION"));
        var bitOffset = -1;
        if (declaredBytePosition is { } bytePos && bytePos >= recordOrigin &&
            declaredBitPosition is null or (>= 0 and <= 7))
        {
            // ODX BIT-POSITION is the position of the parameter's least significant bit inside
            // its byte, counted from that byte's LSB. A little-endian field is stored LSB-first,
            // so the position is the offset itself. A big-endian field (the ODX default) is
            // stored MSB-first: it spans ceil((bitPosition + length) / 8) bytes and its most
            // significant bit sits (span - bitPosition - length) bits after the first byte.
            var bitPosition = declaredBitPosition ?? 0;
            var byteStart = (bytePos - recordOrigin) * 8;
            if (CodedByteOrder(coded) == "LITTLE_ENDIAN")
                bitOffset = byteStart + bitPosition;
            else
                bitOffset = byteStart + (bitPosition + length.Value + 7) / 8 * 8 - bitPosition - length.Value;
        }
        var unit = string.Empty;
        var unitId = dop.Descendants().FirstOrDefault(element => element.Name.LocalName == "UNIT-REF")
            ?.Attribute("ID-REF")?.Value;
        if (unitId is not null && elementsById.TryGetValue(unitId, out var unitElement))
            unit = ChildValue(unitElement, "DISPLAY-NAME") is { Length: > 0 } display ? display : ChildValue(unitElement, "SHORT-NAME");
        var (factor, offset, verifiedConversion) = ResolveLinearConversion(dop);
        var enumValues = OdxDeclaredValueReader.ExactIntegerValues(OdxDeclaredValueReader.Read(dop));
        var dataType = (string?)coded?.Attribute("BASE-DATA-TYPE") ?? string.Empty;
        return new DiagnosticFieldDefinition(
            ChildValue(param, "SHORT-NAME") is { Length: > 0 } shortName ? shortName : ChildValue(dop, "SHORT-NAME"),
            ChildValue(param, "LONG-NAME"),
            param.Descendants().FirstOrDefault(element => element.Name.LocalName == "DESC")?.Value.Trim() ?? string.Empty,
            bitOffset, length.Value, dataType, unit,
            TryParseDouble(dop.Descendants().FirstOrDefault(element => element.Name.LocalName == "LOWER-LIMIT")?.Value),
            TryParseDouble(dop.Descendants().FirstOrDefault(element => element.Name.LocalName == "UPPER-LIMIT")?.Value),
            factor,
            offset,
            dataType.Contains("INT", StringComparison.OrdinalIgnoreCase) && !dataType.Contains("UINT", StringComparison.OrdinalIgnoreCase),
            CodedByteOrder(coded), enumValues,
            SupportsPhysicalConversion: verifiedConversion && bitOffset >= 0,
            Odx: OdxFieldCoding.Create(dop, elementsById, ChildValue(dop, "SHORT-NAME")));
    }

    /// <summary>ISO 22901 ENV-DATA blocks, grouped by the DTC codes they name (DTC-VALUES) or, for
    /// ALL-VALUE, applied to every DTC of the ECU. Positions are relative to the start of the
    /// environment record, which is where the 0x19 decoder starts reading after the record number.
    /// ODX has no separate extended-data element: a block is extended only when its own
    /// SEMANTIC or name says so (EXT...); everything else is a snapshot.</summary>
    private static (Dictionary<int, List<DiagnosticFieldDefinition>> Snapshot,
        Dictionary<int, List<DiagnosticFieldDefinition>> Extended,
        List<DiagnosticFieldDefinition> AllSnapshot, List<DiagnosticFieldDefinition> AllExtended)
        ReadEnvironmentData(
            XDocument document,
            IReadOnlyDictionary<string, XElement> elementsById,
            IReadOnlyDictionary<string, XElement?> shortNameIndex)
    {
        var snapshot = new Dictionary<int, List<DiagnosticFieldDefinition>>();
        var extended = new Dictionary<int, List<DiagnosticFieldDefinition>>();
        var allSnapshot = new List<DiagnosticFieldDefinition>();
        var allExtended = new List<DiagnosticFieldDefinition>();
        foreach (var env in document.Descendants().Where(e => e.Name.LocalName == "ENV-DATA"))
        {
            var label = string.Join(' ', (string?)env.Attribute("SEMANTIC"), ChildValue(env, "SHORT-NAME"),
                env.Parent?.Parent is { } desc && desc.Name.LocalName == "ENV-DATA-DESC"
                    ? ChildValue(desc, "SHORT-NAME") : string.Empty);
            var isExtended = label.Contains("EXT", StringComparison.OrdinalIgnoreCase);
            var fields = new List<DiagnosticFieldDefinition>();
            foreach (var param in env.Elements().Where(e => e.Name.LocalName == "PARAMS")
                         .SelectMany(e => e.Elements()).Where(e => e.Name.LocalName == "PARAM"))
            {
                var field = BuildField(param, 0, elementsById, shortNameIndex);
                if (field is not null) fields.Add(field);
            }
            if (fields.Count == 0) continue;
            fields = fields.OrderBy(f => f.BitOffset).ToList();
            if (env.Elements().Any(e => e.Name.LocalName == "ALL-VALUE"))
                (isExtended ? allExtended : allSnapshot).AddRange(fields);
            var target = isExtended ? extended : snapshot;
            foreach (var value in env.Descendants().Where(e => e.Name.LocalName == "DTC-VALUE"))
            {
                if (TryParseUnsigned(value.Value) is not { } code || code > 0xFFFFFF) continue;
                if (!target.TryGetValue((int)code, out var list)) target[(int)code] = list = [];
                list.AddRange(fields);
            }
        }
        return (snapshot, extended, allSnapshot, allExtended);
    }

    /// <summary>Read only transformations that can be represented by one
    /// verified affine formula. Multi-segment, TEXTTABLE and non-linear DOPs
    /// are preserved as coded data, never faked as linear physical measurements.</summary>
    private static (double? Factor, double? Offset, bool Verified) ResolveLinearConversion(XElement dop)
    {
        var method = dop.Descendants().FirstOrDefault(element => element.Name.LocalName == "COMPU-METHOD");
        if (method is null)
            return (null, null, true); // No conversion specified; raw code remains an identity value.

        var category = ChildValue(method, "CATEGORY");
        if (category.Equals("IDENTICAL", StringComparison.OrdinalIgnoreCase))
            return (1d, 0d, true);
        if (!string.IsNullOrWhiteSpace(category) &&
            !category.Equals("LINEAR", StringComparison.OrdinalIgnoreCase) &&
            !category.Equals("SCALE-LINEAR", StringComparison.OrdinalIgnoreCase))
            return (null, null, false);

        var scales = method.Descendants().Where(element =>
            element.Name.LocalName == "COMPU-SCALE").ToArray();
        if (scales.Length == 0) return (null, null, false);

        var conversions = new List<(double Factor, double Offset)>();
        foreach (var scale in scales)
        {
            var numerators = scale.Descendants()
                .FirstOrDefault(element => element.Name.LocalName == "COMPU-NUMERATOR")?
                .Descendants().Where(element => element.Name.LocalName == "V")
                .Select(element => TryParseDouble(element.Value)).ToArray() ?? [];
            var denominators = scale.Descendants()
                .FirstOrDefault(element => element.Name.LocalName == "COMPU-DENOMINATOR")?
                .Descendants().Where(element => element.Name.LocalName == "V")
                .Select(element => TryParseDouble(element.Value)).ToArray() ?? [];
            var divisor = denominators.Length == 0 ? 1d : denominators[0];
            // More than two numerator terms or any nonconstant denominator
            // describes a rational/polynomial transform, not affine scaling.
            if (numerators.Length != 2 || denominators.Length > 1 ||
                numerators[0] is null || numerators[1] is null || divisor is null ||
                !double.IsFinite(divisor.Value) || Math.Abs(divisor.Value) <= double.Epsilon)
                return (null, null, false);

            var offset = numerators[0]!.Value / divisor.Value;
            var factor = numerators[1]!.Value / divisor.Value;
            if (!double.IsFinite(offset) || !double.IsFinite(factor))
                return (null, null, false);
            conversions.Add((factor, offset));
        }
        var first = conversions[0];
        if (conversions.Any(item => item.Factor != first.Factor || item.Offset != first.Offset))
            return (null, null, false); // True piecewise scaling needs a richer DOP model.

        return (first.Factor, first.Offset, true);
    }

    /// <summary>ODX DIAG-CODED-TYPE defaults IS-HIGHLOW-BYTE-ORDER to true.
    /// The override false is important for little-endian DID scalars.</summary>
    private static string CodedByteOrder(XElement? coded)
    {
        if (coded is null) return "Not declared";
        var setting = (string?)coded.Attribute("IS-HIGHLOW-BYTE-ORDER") ??
            ChildValue(coded, "IS-HIGHLOW-BYTE-ORDER");
        return string.Equals(setting, "false", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(setting, "0", StringComparison.OrdinalIgnoreCase)
            ? "LITTLE_ENDIAN" : "BIG_ENDIAN";
    }

    private static string TrimServiceName(string name)
    {
        foreach (var prefix in new[] { "Read_", "RDBI_", "ReadDataByIdentifier_" })
        {
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return name[prefix.Length..];
            }
        }

        return name.EndsWith("_Read", StringComparison.OrdinalIgnoreCase) ? name[..^5] : name;
    }

    private static XElement? Child(XElement element, string localName) =>
        element.Elements().FirstOrDefault(child => child.Name.LocalName == localName);

    private static string ChildValue(XElement element, string localName) =>
        Child(element, localName)?.Value.Trim() ?? string.Empty;

    private static int? TryParseInt(string? text) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    private static double? TryParseDouble(string? text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    private static ulong? TryParseUnsigned(string? text)
    {
        text = text?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return ulong.TryParse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex)
                ? hex
                : null;
        }

        return ulong.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }
}
