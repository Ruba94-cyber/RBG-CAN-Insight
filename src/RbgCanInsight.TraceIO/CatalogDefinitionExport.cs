using System.Globalization;
using System.Numerics;
using RbgCanInsight.Dbc;
using RbgCanInsight.DiagData;

namespace RbgCanInsight.TraceIO;

/// <summary>Exact source/ECU/DID identity for an analyst-selected diagnostic export.
/// DID numeric identity alone may occur in multiple ECU descriptions.</summary>
public sealed record DiagnosticDidSelection(string SourceFile, string Ecu, int Did, string? DisplayName = null);

/// <summary>Read-only catalog exports: definitions do not require a loaded CAN trace.
/// Search terms are AND-composed; matched messages keep only matched signals unless
/// the message itself matches. Original database and diagnostic files are never modified.</summary>
public static class CatalogDefinitionExport
{
    public static IReadOnlyList<ExportSheet> CreateDbcSheets(
        IReadOnlyList<DbcDatabase> databases, string? search = null)
    {
        ArgumentNullException.ThrowIfNull(databases);
        var terms = Terms(search);
        var messages = new Builder("Messages",
            ["DBC", "CAN ID", "Frame format", "Message", "DLC", "Transmitter", "Cycle time [ms]", "Signals", "Comment"],
            [4, 6, 7]);
        var signals = new Builder("Signals",
            ["DBC", "CAN ID", "Frame format", "Message", "Signal", "Start bit", "Bits", "Byte order",
             "Signed", "Value type", "Factor", "Offset", "Minimum", "Maximum", "Unit", "Receiver",
             "Multiplexor", "Mux value", "Mux conditions", "Description"],
            [5, 6, 10, 11, 12, 13, 17]);
        var bitPositions = new Builder("Bit Positions",
            ["DBC", "CAN ID", "Frame format", "Message", "Signal",
             "DBC start bit", "Signal bits", "Encoded position", "Byte (0-based)",
             "Bit in byte (LSB0)", "Physical bit (LSB0)", "Raw value bit (LSB0)",
             "Byte order", "Multiplex conditions", "Other signals using bit",
             "Inside declared DLC (max 64 bytes)", "Interpretation"],
            [5, 6, 7, 8, 9, 10, 11]);
        var bitIssues = new Builder("Bit Layout Issues",
            ["DBC", "CAN ID", "Frame format", "Message", "Signal", "Issue"]);
        var rawLimits = new Builder("Raw Physical Limits",
            ["DBC", "CAN ID", "Frame format", "Message", "Signal", "Value type",
             "Signed", "Bits", "Exact raw minimum (text)", "Exact raw maximum (text)",
             "Declared physical minimum", "Declared physical maximum", "Factor",
             "Offset", "Unit", "Declared VAL codes", "Out-of-range VAL codes",
             "Interpretation"],
            [7, 10, 11, 12, 13, 15, 16]);
        var values = new Builder("Value Meanings",
            ["DBC", "CAN ID", "Frame format", "Message", "Signal", "Raw value", "Meaning"]);
        var attrs = new Builder("Attributes",
            ["DBC", "CAN ID", "Frame format", "Message", "Signal / scope", "Attribute", "Original value"]);
        var declarations = new Builder("Attribute Definitions", ["DBC", "Original declaration"]);

        foreach (var db in databases.OrderBy(db => db.Name, StringComparer.OrdinalIgnoreCase))
        {
            var matchedDb = Matches(terms, db.Name);
            var included = false;
            foreach (var msg in db.AllMessageDefinitions.OrderBy(msg => msg.Identifier)
                         .ThenBy(msg => msg.IsExtended))
            {
                var id = $"0x{msg.Identifier:X}";
                var format = msg.IsExtended ? "29-bit" : "11-bit";
                var matchMsg = matchedDb ||
                    Matches(terms, id, format, msg.Name, msg.Transmitter, msg.Comment,
                        msg.NominalCycleTimeMilliseconds?.ToString(CultureInfo.InvariantCulture));
                var signalSet = msg.Signals.Where(signal => matchMsg ||
                    Matches(terms, id, format, msg.Name, signal.Name, signal.Unit,
                        signal.Receiver, signal.Comment, db.Name)).ToArray();
                if (!matchMsg && signalSet.Length == 0)
                    continue;
                included = true;
                messages.Add(db.Name, id, format, msg.Name, msg.Length, msg.Transmitter,
                    msg.NominalCycleTimeMilliseconds, signalSet.Length, msg.Comment);
                foreach (var attr in msg.SourceAttributes)
                    attrs.Add(db.Name, id, format, msg.Name, "(message)", attr.Name, attr.RawValue);

                // Consider every definition, not just visible/search-matched
                // signals, when disclosing a physical bit shared by other signals.
                // Shared mux branches are not automatically an invalid overlap.
                var bitOccupants = new Dictionary<int, List<string>>();
                foreach (var defined in msg.Signals)
                {
                    foreach (var position in DbcSignalInspector.PhysicalBits(defined))
                    {
                        if (!bitOccupants.TryGetValue(position, out var occupants))
                            bitOccupants[position] = occupants = [];
                        occupants.Add(defined.Name);
                    }
                }
                if (msg.Length is < 0 or > 64)
                    bitIssues.Add(db.Name, id, format, msg.Name, "(message)",
                        $"Declared DLC {msg.Length} is outside supported 0–64 bytes.");
                foreach (var signal in signalSet)
                {
                    var mux = signal.MultiplexConditions is { Count: > 0 } conditions
                        ? string.Join("; ", conditions.Select(condition =>
                            condition.Multiplexor + ": " +
                            string.Join(", ", condition.Ranges.Select(range =>
                                $"{range.Minimum}-{range.Maximum}"))))
                        : string.Empty;
                    signals.Add(db.Name, id, format, msg.Name, signal.Name,
                        signal.StartBit, signal.BitLength, signal.ByteOrder.ToString(),
                        signal.IsSigned ? "yes" : "no", signal.ValueType.ToString(),
                        signal.Factor, signal.Offset, signal.Minimum, signal.Maximum, signal.Unit,
                        signal.Receiver, signal.IsMultiplexor ? "yes" : "no",
                        signal.MultiplexValue, mux, signal.Comment);
                    var isInteger = signal.ValueType == DbcSignalValueType.Integer;
                    var validWidth = signal.BitLength is >= 1 and <= 64;
                    var codedMin = isInteger && validWidth
                        ? signal.IsSigned
                            ? -(BigInteger.One << (signal.BitLength - 1))
                            : BigInteger.Zero
                        : (BigInteger?)null;
                    var codedMax = isInteger && validWidth
                        ? (BigInteger.One << (signal.IsSigned
                            ? signal.BitLength - 1 : signal.BitLength)) - BigInteger.One
                        : (BigInteger?)null;
                    var declaredCodes = signal.ValueDescriptions?.Count ?? 0;
                    int? invalidCodes = codedMin is { } minCode && codedMax is { } maxCode
                        ? signal.ValueDescriptions?.Count(pair =>
                            (BigInteger)pair.Key < minCode || (BigInteger)pair.Key > maxCode) ?? 0
                        : null;
                    rawLimits.Add(db.Name, id, format, msg.Name, signal.Name,
                        signal.ValueType.ToString(), signal.IsSigned ? "yes" : "no",
                        signal.BitLength,
                        codedMin?.ToString(CultureInfo.InvariantCulture),
                        codedMax?.ToString(CultureInfo.InvariantCulture),
                        signal.Minimum, signal.Maximum, signal.Factor, signal.Offset,
                        signal.Unit, declaredCodes, invalidCodes,
                        !validWidth ? "Unsupported bit length: raw range cannot be established." :
                        !isInteger ? "IEEE-754 bit pattern, not an integer code. Declared physical limits are not captured measurements; integer VAL codes are not applied." :
                        invalidCodes > 0 ? "Declared VAL entries outside representable raw range; review DBC. Physical limits come from the definition, not trace evidence." :
                        "Exact integer raw range from bit width/signedness. Physical limits are source-declared, not observed.");
                    var positions = DbcSignalInspector.PhysicalBits(signal);
                    var bitNote = "Definition-only coordinate; no recorded payload or active mux state verified";
                    var muxText = mux.Length > 0 ? mux :
                        signal.MultiplexValue is { } selector
                            ? $"Selector branch {selector}"
                            : signal.IsMultiplexor ? "Multiplexor" : "Not multiplexed";
                    if (signal.BitLength is < 1 or > 64 || positions.Count != signal.BitLength)
                        bitIssues.Add(db.Name, id, format, msg.Name, signal.Name,
                            $"Unsupported or unrepresentable bit layout: start {signal.StartBit}, length {signal.BitLength}.");
                    else
                    {
                        var outsideDlc = false;
                        var sharedBit = false;
                        for (var encodedPosition = 0; encodedPosition < positions.Count; encodedPosition++)
                        {
                            var physical = positions[encodedPosition];
                            var insideDlc = physical >= 0 &&
                                physical < (long)msg.Length * 8 && physical < 512;
                            if (!insideDlc) outsideDlc = true;
                            var rawValueBit = signal.ByteOrder == DbcByteOrder.Intel
                                ? encodedPosition : signal.BitLength - encodedPosition - 1;
                            // Multiple definitions with an identical signal name can
                            // be ambiguous. Exclude only this occurrence rather than
                            // all same-named signals from the overlap report.
                            var others = bitOccupants.TryGetValue(physical, out var names)
                                ? names.ToList() : [];
                            others.Remove(signal.Name);
                            if (others.Count > 0) sharedBit = true;
                            bitPositions.Add(db.Name, id, format, msg.Name, signal.Name,
                                signal.StartBit, signal.BitLength, encodedPosition,
                                physical / 8, physical % 8, physical, rawValueBit,
                                signal.ByteOrder.ToString(), muxText,
                                string.Join("; ", others), insideDlc ? "YES" : "NO",
                                others.Count > 0
                                    ? bitNote + "; shared bit may be valid across mutually exclusive mux branches"
                                    : bitNote);
                        }
                        if (outsideDlc)
                            bitIssues.Add(db.Name, id, format, msg.Name, signal.Name,
                                "Some declared bits fall outside this message DLC or the 64-byte CAN-FD payload limit.");
                        if (sharedBit)
                            bitIssues.Add(db.Name, id, format, msg.Name, signal.Name,
                                "Physical bits are shared with other signals; check multiplex exclusivity before treating as a defect.");
                    }
                    if (signal.ValueDescriptions is not null)
                        foreach (var entry in signal.ValueDescriptions.OrderBy(entry => entry.Key))
                            values.Add(db.Name, id, format, msg.Name, signal.Name, entry.Key.ToString(CultureInfo.InvariantCulture), entry.Value);
                    foreach (var attr in signal.SourceAttributes)
                        attrs.Add(db.Name, id, format, msg.Name, signal.Name, attr.Name, attr.RawValue);
                }
            }
            if (included)
                foreach (var decl in db.RawAttributeDeclarations)
                    declarations.Add(db.Name, decl);
        }
        return [messages.Sheet, signals.Sheet, bitPositions.Sheet, bitIssues.Sheet,
            rawLimits.Sheet, values.Sheet, attrs.Sheet, declarations.Sheet];
    }

    public static IReadOnlyList<ExportSheet> CreateDiagnosticSheets(
        IReadOnlyList<DidCatalog> catalogs, string? search = null,
        DiagnosticDidSelection? selectedDid = null, bool engineeringOnly = false,
        string? ecuFilter = null)
    {
        ArgumentNullException.ThrowIfNull(catalogs);
        var terms = Terms(search);
        var dids = new Builder("DIDs",
            ["Source", "ECU", "DID", "Name", "Long name", "Description", "Type",
             "Bits", "Unit", "Factor", "Offset", "Minimum", "Maximum", "Declared fields"],
            [7, 9, 10, 11, 12, 13]);
        var didFields = new Builder("DID Fields",
            ["Source", "ECU", "DID", "DID name", "Field", "Long name", "Bit offset",
             "Bit length", "Data type", "Byte order", "Signed", "Unit",
             "Factor", "Offset", "Minimum", "Maximum", "Verified conversion", "Description"],
            [6, 7, 12, 13, 14, 15]);
        var didEnums = new Builder("DID Values",
            ["Source", "ECU", "DID", "DID name", "Field", "Raw value", "Meaning", "Description"]);
        // One row for every declared engineering parameter at every nesting level.
        // PAR is the top-level ancestor; Sub-PAR is the current nested node.
        // Hierarchy and source references remain explicit without guessing bit offsets.
        var engineering = new Builder("Engineering Parameters",
            ["Source", "ECU", "DID", "DID name", "Engineering PAR", "PAR description",
             "Sub-PAR", "Sub-PAR description", "Parameter ID", "Parameter name",
             "Parameter long name", "Parameter description", "Node kind", "Depth",
             "Hierarchy", "Parent path", "Data type", "Type reference",
             "Declared bits", "Unit", "Reference status", "Definition origin",
             "Declared PAR code", "Source byte position (unverified)",
             "Source bit position (unverified)", "Source bit offset (unverified)"],
            [13, 18]);
        var engineeringValues = new Builder("Engineering PAR Values",
            ["Source", "ECU", "DID", "DID name", "Engineering PAR",
             "PAR / Sub-PAR", "Hierarchy", "Declared PAR code", "Source node ID",
             "Type reference", "Raw lower", "Raw upper", "Meaning", "Description",
             "Boundary status", "Definition origin"]);
        var engineeringCoverage = new Builder("PAR Extraction Coverage",
            ["Source", "ECU", "DID", "DID name", "Source-backed PAR nodes",
             "Fallback field nodes", "Unresolved references", "Extraction assessment",
             "Definition issues", "Missing PAR descriptions",
             "Missing declared PAR codes", "Named parameters",
             "Coverage note", "Variable-length definitions",
             "Declared value meanings", "Unverifiable value boundaries"],
            [4, 5, 6, 9, 10, 11, 13, 14, 15]);
        var dtcs = new Builder("DTCs",
            ["Source", "ECU", "DTC", "Name", "Display text", "Description", "Snapshot fields", "Extended fields"],
            [6, 7]);
        var routines = new Builder("Routines",
            ["Source", "ECU", "Routine ID", "Name", "Description", "Session", "Security", "Input fields", "Output fields"],
            [7, 8]);
        var services = new Builder("Services",
            ["Source", "ECU", "SID", "Subfunction", "Identifier", "Name", "Long name", "Description"]);
        var otherFields = new Builder("DTC Routine Fields",
            ["Source", "ECU", "Kind", "Identifier", "Section", "Field", "Bit offset", "Bit length",
             "Data type", "Byte order", "Unit", "Signed", "Factor", "Offset", "Description"],
            [6, 7, 12, 13]);
        var connections = new Builder("Transport",
            ["Source", "ECU", "Profile", "Request CAN ID", "Response CAN ID", "ID format", "Addressing"]);
        var variants = new Builder("Variants", ["Source", "ECU", "Name", "Type", "Description"]);

        foreach (var catalog in catalogs
                     .Where(cat => string.IsNullOrWhiteSpace(ecuFilter) ||
                         string.Equals(cat.Ecu, ecuFilter, StringComparison.OrdinalIgnoreCase))
                     .OrderBy(cat => cat.Ecu, StringComparer.OrdinalIgnoreCase))
        {
            var source = Path.GetFileName(catalog.SourcePath);
            var baseMatch = Matches(terms, source, catalog.Name, catalog.Ecu);
            foreach (var did in catalog.Parameters)
            {
                if (selectedDid is { } selected &&
                    (did.Did != selected.Did ||
                     !string.Equals(did.Ecu, selected.Ecu, StringComparison.OrdinalIgnoreCase) ||
                     !string.Equals(did.SourceFile, selected.SourceFile, StringComparison.OrdinalIgnoreCase) ||
                     (selected.DisplayName is not null &&
                      !string.Equals(did.DisplayName, selected.DisplayName, StringComparison.Ordinal))))
                    continue;
                var key = $"0x{did.Did:X4}";
                var match = baseMatch || Matches(terms, "DID", key, did.Name, did.LongName,
                    did.Description, did.DataType, did.Unit) ||
                    did.Fields.Any(field => Matches(terms, "DID", key, field.Name,
                        field.LongName, field.Description, field.Unit, field.DataType)) ||
                    did.EngineeringParameters.Any(parameter =>
                        Matches(terms, "DID", key, parameter.Name, parameter.LongName,
                            parameter.Description, parameter.SourceId,
                            parameter.TypeReference, parameter.DefinitionOrigin,
                            parameter.DeclaredParCode,
                            parameter.DeclaredBytePosition,
                            parameter.DeclaredBitPosition,
                            parameter.DeclaredBitOffset) ||
                        parameter.DeclaredValues.Any(meaning => Matches(terms,
                            key, parameter.Name, meaning.LowerRaw, meaning.UpperRaw,
                            meaning.Meaning, meaning.Description, meaning.Status)));
                if (!match) continue;
                AddEngineeringParameters(source, catalog.Ecu, key, did,
                    engineering, engineeringValues);
                var sourceNodes = did.EngineeringParameters.Count;
                var fallbackNodes = sourceNodes == 0 ? did.Fields.Count : 0;
                var unresolved = did.EngineeringParameters.Count(node =>
                    node.ReferenceStatus.Contains("unresolved", StringComparison.OrdinalIgnoreCase) ||
                    node.ReferenceStatus.Contains("ambiguous", StringComparison.OrdinalIgnoreCase) ||
                    node.ReferenceStatus.Contains("cyclic", StringComparison.OrdinalIgnoreCase));
                var dynamic = did.EngineeringParameters.Count(node =>
                    node.ReferenceStatus.Contains("occurrence count and offsets unverified",
                        StringComparison.OrdinalIgnoreCase));
                var declaredMeanings = did.EngineeringParameters.Sum(node =>
                    node.DeclaredValues.Count);
                var unverifiableMeanings = did.EngineeringParameters.Sum(node =>
                    node.DeclaredValues.Count(meaning =>
                        meaning.Status is not ("Exact code" or "Declared range")));
                // Missing OEM descriptions and numeric PAR codes are quality
                // signals, not evidence of a parser error. Many source files
                // legitimately identify parameters only by their short names.
                var missingDescriptions = did.EngineeringParameters.Count(node =>
                    string.IsNullOrWhiteSpace(node.Description));
                var missingParCodes = did.EngineeringParameters.Count(node =>
                    string.IsNullOrWhiteSpace(node.DeclaredParCode));
                var namedParameters = did.EngineeringParameters.Count(node =>
                    !string.IsNullOrWhiteSpace(node.Name) ||
                    !string.IsNullOrWhiteSpace(node.LongName));
                var assessment = did.EngineeringIssues.Count > 0
                    ? "PARTIAL: same-DID definitions differ; review extraction issues"
                    : sourceNodes > 0
                        ? unresolved > 0
                            ? "PARTIAL: type references unresolved or ambiguous; verify source"
                            : dynamic > 0
                                ? "PARTIAL: variable-length parameter cardinality unknown"
                                : unverifiableMeanings > 0
                                    ? "PARTIAL: declared value boundaries unverified"
                                    : "SOURCE EXTRACTED: OEM completeness not independently verified"
                        : fallbackNodes > 0
                            ? "FALLBACK: field definitions only, engineering hierarchy unavailable"
                            : "NO PAR TREE: verify unsupported or empty DID source";
                engineeringCoverage.Add(source, catalog.Ecu, key, did.DisplayName,
                    sourceNodes, fallbackNodes, unresolved, assessment,
                    string.Join(" | ", did.EngineeringIssues),
                    missingDescriptions, missingParCodes, namedParameters,
                    sourceNodes == 0
                        ? "No source-backed PAR hierarchy; review input"
                        : "Missing fields reflect source declarations, not verified OEM completeness",
                    dynamic, declaredMeanings, unverifiableMeanings);
                if (engineeringOnly) continue;
                dids.Add(source, catalog.Ecu, key, did.Name, did.LongName, did.Description,
                    did.DataType, did.BitLength, did.Unit, did.Factor, did.Offset,
                    did.Minimum, did.Maximum, did.Fields.Count);
                foreach (var field in did.Fields)
                {
                    didFields.Add(source, catalog.Ecu, key, did.Name, field.Name, field.LongName,
                        field.BitOffset >= 0 ? field.BitOffset : null,
                        field.BitLength > 0 ? field.BitLength : null,
                        field.DataType, field.ByteOrder, field.IsSigned ? "yes" : "no",
                        field.Unit, field.Factor, field.Offset, field.Minimum, field.Maximum,
                        field.SupportsPhysicalConversion ? "yes" : "raw only", field.Description);
                    foreach (var value in field.EnumValues)
                        didEnums.Add(source, catalog.Ecu, key, did.Name, field.Name,
                            value.Value.ToString(CultureInfo.InvariantCulture), value.Text, value.Description);
                }
            }
            // A focused DID workbook never leaks unrelated DTC, service,
            // routine, transport or ECU-variant definitions into the result.
            if (selectedDid is not null || engineeringOnly) continue;
            foreach (var dtc in catalog.DtcDefinitions)
            {
                var key = $"0x{dtc.Code:X6}";
                if (!baseMatch && !Matches(terms, "DTC", key, dtc.Name, dtc.DisplayText,
                        dtc.Description) && !dtc.SnapshotFields.Concat(dtc.ExtendedDataFields)
                        .Any(field => Matches(terms, "DTC", key, field.Name, field.LongName)))
                    continue;
                dtcs.Add(source, catalog.Ecu, key, dtc.Name, dtc.DisplayText, dtc.Description,
                    dtc.SnapshotFields.Count, dtc.ExtendedDataFields.Count);
                AddOtherFields(source, catalog.Ecu, "DTC", key, "Snapshot", dtc.SnapshotFields, otherFields);
                AddOtherFields(source, catalog.Ecu, "DTC", key, "Extended data", dtc.ExtendedDataFields, otherFields);
            }
            foreach (var routine in catalog.RoutineDefinitions)
            {
                var key = $"0x{routine.Identifier:X4}";
                if (!baseMatch && !Matches(terms, "Routine", key, routine.Name, routine.LongName,
                        routine.Description, routine.RequiredSession, routine.RequiredSecurityLevel) &&
                    !routine.InputFields.Concat(routine.OutputFields)
                        .Any(field => Matches(terms, "Routine", key, field.Name, field.LongName)))
                    continue;
                routines.Add(source, catalog.Ecu, key, routine.Name, routine.Description,
                    routine.RequiredSession, routine.RequiredSecurityLevel,
                    routine.InputFields.Count, routine.OutputFields.Count);
                AddOtherFields(source, catalog.Ecu, "Routine", key, "Input", routine.InputFields, otherFields);
                AddOtherFields(source, catalog.Ecu, "Routine", key, "Output", routine.OutputFields, otherFields);
            }
            foreach (var service in catalog.ServiceDefinitions)
            {
                if (!baseMatch && !Matches(terms, "Service", $"0x{service.ServiceId:X2}",
                        service.Name, service.LongName, service.Description, service.Identifier?.ToString("X4")))
                    continue;
                services.Add(source, catalog.Ecu, $"0x{service.ServiceId:X2}",
                    service.SubFunction is { } sub ? $"0x{sub:X2}" : string.Empty,
                    service.Identifier is { } id ? $"0x{id:X4}" : string.Empty,
                    service.Name, service.LongName, service.Description);
            }
            foreach (var connection in catalog.ConnectionDefinitions)
            {
                if (!baseMatch && !Matches(terms, "Transport", connection.Name,
                        connection.AddressingType, connection.Ecu))
                    continue;
                connections.Add(source, catalog.Ecu, connection.Name,
                    connection.RequestIdentifier is { } req ? $"0x{req:X}" : string.Empty,
                    connection.ResponseIdentifier is { } resp ? $"0x{resp:X}" : string.Empty,
                    connection.IsExtendedIdentifier ? "29-bit" : "11-bit", connection.AddressingType);
            }
            foreach (var variant in catalog.VariantDefinitions)
            {
                if (!baseMatch && !Matches(terms, "Variant", variant.Name, variant.Type, variant.Description))
                    continue;
                variants.Add(source, catalog.Ecu, variant.Name, variant.Type, variant.Description);
            }
        }
        if (engineeringOnly)
            return [engineering.Sheet, engineeringCoverage.Sheet,
                engineeringValues.Sheet];
        return [dids.Sheet, didFields.Sheet, didEnums.Sheet, engineering.Sheet,
            engineeringCoverage.Sheet, dtcs.Sheet, routines.Sheet, services.Sheet,
            otherFields.Sheet, connections.Sheet, variants.Sheet,
            engineeringValues.Sheet];
    }

    private static void AddEngineeringParameters(string source, string ecu,
        string didCode, DidParameter did, Builder output, Builder values)
    {
        // A field-only synthetic/catalog description is still usable. Mark it
        // as a fallback rather than inventing a complete hierarchy.
        var nodes = did.EngineeringParameters.Count > 0
            ? did.EngineeringParameters
            : did.Fields.Select((field, index) => new DiagnosticEngineeringParameter(
                field.Name, field.LongName, field.Description,
                "/" + (index + 1).ToString(CultureInfo.InvariantCulture) + ":" + field.Name,
                "", 0, "Declared DID field (no structure)",
                "", "", field.DataType, field.Unit,
                field.BitLength > 0 ? field.BitLength : null,
                "Structure not available from source")).ToArray();

        var indexByPath = nodes.ToDictionary(node => node.Path, StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            var ancestor = node;
            var guard = 0;
            while (ancestor.ParentPath.Length > 0 &&
                   indexByPath.TryGetValue(ancestor.ParentPath, out var parent) &&
                   guard++ < 32)
                ancestor = parent;
            var isSub = ancestor.Path != node.Path;
            output.Add(source, ecu, didCode, did.DisplayName,
                ancestor.DisplayName, ancestor.Description,
                isSub ? node.DisplayName : string.Empty,
                isSub ? node.Description : string.Empty,
                node.SourceId, node.Name, node.LongName, node.Description,
                node.Kind, node.Depth, node.Path, node.ParentPath,
                node.DataType, node.TypeReference, node.DeclaredBitLength,
                node.Unit, node.ReferenceStatus, node.DefinitionOrigin,
                node.DeclaredParCode, node.DeclaredBytePosition,
                node.DeclaredBitPosition, node.DeclaredBitOffset);
            foreach (var meaning in node.DeclaredValues)
            {
                values.Add(source, ecu, didCode, did.DisplayName,
                    ancestor.DisplayName, node.DisplayName, node.Path,
                    node.DeclaredParCode, node.SourceId, node.TypeReference,
                    meaning.LowerRaw, meaning.UpperRaw, meaning.Meaning,
                    meaning.Description, meaning.Status, node.DefinitionOrigin);
            }
        }
    }

    private static void AddOtherFields(string source, string ecu, string kind, string id,
        string section, IReadOnlyList<DiagnosticFieldDefinition> fields, Builder output)
    {
        foreach (var field in fields)
            output.Add(source, ecu, kind, id, section, field.Name,
                field.BitOffset >= 0 ? field.BitOffset : null,
                field.BitLength > 0 ? field.BitLength : null, field.DataType,
                field.ByteOrder, field.Unit, field.IsSigned ? "yes" : "no",
                field.Factor, field.Offset, field.Description);
    }

    private static string[] Terms(string? search) =>
        (search ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool Matches(string[] terms, params string?[] fields) =>
        terms.Length == 0 || terms.All(term =>
            fields.Any(field => field?.Contains(term, StringComparison.OrdinalIgnoreCase) == true));

    private sealed class Builder(string name, string[] headers, int[]? numeric = null)
    {
        private readonly List<IReadOnlyList<string>> _rows = [];
        public ExportSheet Sheet => new(name, headers, _rows,
            numeric is null ? null : new HashSet<int>(numeric));

        public void Add(params object?[] cells)
        {
            if (cells.Length != headers.Length)
                throw new InvalidOperationException($"Incorrect export width for {name}.");
            _rows.Add(cells.Select(cell => cell switch
            {
                null => string.Empty,
                IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
                _ => cell.ToString() ?? string.Empty
            }).ToArray());
        }
    }
}
