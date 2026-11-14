using System.Globalization;

namespace RbgCanInsight.DiagData;

public enum DiagnosticDefinitionChange
{
    Unchanged,
    Changed,
    Added,
    Removed,
    Ambiguous
}

public enum DiagnosticDefinitionImpact
{
    None,
    Documentation,
    Structure,
    Encoding,
    Access,
    Transport,
    Ambiguous
}

/// <summary>A comparison of two parsed diagnostic descriptions, not evidence of
/// correct ECU runtime behaviour, exact OEM format fidelity or physical decoding.</summary>
public sealed record DiagnosticDefinitionDiffRow(
    string Scope, string Ecu, string Identifier, string Path,
    DiagnosticDefinitionChange Change, DiagnosticDefinitionImpact Impact,
    string ChangedFields, string BaselineName, string CurrentName,
    string BaselineDefinition, string CurrentDefinition,
    string BaselineSource, string CurrentSource, bool RequiresReview);

public sealed record DiagnosticDefinitionDiffReport(
    IReadOnlyList<DiagnosticDefinitionDiffRow> Rows)
{
    public int Added => Rows.Count(row => row.Change == DiagnosticDefinitionChange.Added);
    public int Removed => Rows.Count(row => row.Change == DiagnosticDefinitionChange.Removed);
    public int Changed => Rows.Count(row => row.Change == DiagnosticDefinitionChange.Changed);
    public int Ambiguous => Rows.Count(row => row.Change == DiagnosticDefinitionChange.Ambiguous);
    public int Unchanged => Rows.Count(row => row.Change == DiagnosticDefinitionChange.Unchanged);
    public int RequiresReview => Rows.Count(row => row.RequiresReview);
    public string Summary => $"{Rows.Count:N0} definitions: {Added:N0} added, " +
        $"{Removed:N0} removed, {Changed:N0} changed, {Ambiguous:N0} ambiguous, " +
        $"{RequiresReview:N0} to review";
}

/// <summary>
/// Read-only comparison of the diagnostic graph actually extracted from two
/// CDD/ODX/PDX source sets. Identity is ECU + kind + service/DID/RID/DTC key,
/// with field name or declared engineering hierarchy path for children.
/// Duplicate identities are never paired by source-order. Nested PAR paths are
/// position-based; a reorder may appear as a structural change and requires
/// analyst inspection. Unknown OEM metadata is not asserted to be identical.
/// </summary>
public static class DiagnosticDefinitionComparer
{
    private sealed record Entry(
        string Scope, string Ecu, string Identifier, string Path,
        string RootScope, string Name, string Source,
        IReadOnlyDictionary<string, string> Fields);

    private readonly record struct Key(string Ecu, string Scope, string Identifier, string Path);
    private readonly record struct RootKey(string Ecu, string Scope, string Identifier);

    public static DiagnosticDefinitionDiffReport Compare(
        IReadOnlyList<DidCatalog> baseline, IReadOnlyList<DidCatalog> current)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(current);
        var oldEntries = Flatten(baseline);
        var newEntries = Flatten(current);
        var oldIndex = Index(oldEntries);
        var newIndex = Index(newEntries);
        var blocking = new HashSet<RootKey>();
        foreach (var key in oldIndex.Keys.Concat(newIndex.Keys).Distinct())
        {
            if (key.Scope is not ("DID" or "DTC" or "Routine") ||
                key.Path.Length != 0) continue;
            if ((oldIndex.GetValueOrDefault(key)?.Count ?? 0) > 1 ||
                (newIndex.GetValueOrDefault(key)?.Count ?? 0) > 1)
                blocking.Add(new RootKey(key.Ecu, key.Scope, key.Identifier));
        }

        var allKeys = oldIndex.Keys.Concat(newIndex.Keys).Distinct()
            .OrderBy(key => key.Ecu, StringComparer.Ordinal)
            .ThenBy(key => key.Scope, StringComparer.Ordinal)
            .ThenBy(key => key.Identifier, StringComparer.Ordinal)
            .ThenBy(key => key.Path, StringComparer.Ordinal);
        var rows = new List<DiagnosticDefinitionDiffRow>();
        foreach (var key in allKeys)
        {
            oldIndex.TryGetValue(key, out var oldGroup);
            newIndex.TryGetValue(key, out var newGroup);
            var before = oldGroup?.Count == 1 ? oldGroup[0] : null;
            var after = newGroup?.Count == 1 ? newGroup[0] : null;
            var template = before ?? after ?? oldGroup?.FirstOrDefault() ?? newGroup!.First();

            var blockedRoot = new RootKey(key.Ecu, template.RootScope, key.Identifier);
            // A duplicated root DID/RID/DTC invalidates the identity of ALL
            // its children. Show a single actionable root collision rather
            // than invented field-level changes between unrelated variants.
            if (template.Scope != template.RootScope && blocking.Contains(blockedRoot))
                continue;

            if ((oldGroup?.Count ?? 0) > 1 || (newGroup?.Count ?? 0) > 1)
            {
                rows.Add(new DiagnosticDefinitionDiffRow(
                    key.Scope, template.Ecu, key.Identifier, key.Path,
                    DiagnosticDefinitionChange.Ambiguous, DiagnosticDefinitionImpact.Ambiguous,
                    $"Repeated identity: baseline {oldGroup?.Count ?? 0}, current {newGroup?.Count ?? 0}; no automatic pairing.",
                    before?.Name ?? string.Empty, after?.Name ?? string.Empty,
                    string.Empty, string.Empty, before?.Source ?? string.Empty,
                    after?.Source ?? string.Empty, true));
                continue;
            }
            if (before is null || after is null)
            {
                var added = before is null;
                rows.Add(new DiagnosticDefinitionDiffRow(
                    key.Scope, template.Ecu, key.Identifier, key.Path,
                    added ? DiagnosticDefinitionChange.Added : DiagnosticDefinitionChange.Removed,
                    DiagnosticDefinitionImpact.Structure,
                    added ? "Definition added" : "Definition removed",
                    before?.Name ?? string.Empty, after?.Name ?? string.Empty,
                    Render(before), Render(after),
                    before?.Source ?? string.Empty, after?.Source ?? string.Empty, true));
                continue;
            }

            var changes = before.Fields.Keys.Concat(after.Fields.Keys)
                .Distinct(StringComparer.Ordinal).OrderBy(name => name, StringComparer.Ordinal)
                .Where(name => !string.Equals(before.Fields.GetValueOrDefault(name),
                    after.Fields.GetValueOrDefault(name), StringComparison.Ordinal)).ToArray();
            var impact = ClassifyImpact(key.Scope, changes);
            rows.Add(new DiagnosticDefinitionDiffRow(
                key.Scope, template.Ecu, key.Identifier, key.Path,
                changes.Length == 0 ? DiagnosticDefinitionChange.Unchanged : DiagnosticDefinitionChange.Changed,
                impact, changes.Length == 0 ? "Same parsed definition" : string.Join("; ", changes),
                before.Name, after.Name, Render(before), Render(after),
                before.Source, after.Source, changes.Length > 0));
        }
        // The root DID/DTC/RID must be actionable when a nested definition
        // changes even if its own scalar metadata stayed byte-for-byte equal.
        // This avoids falsely displaying "Unchanged DID" beside a changed PAR.
        var affectedParents = rows
            .Where(row => row.Change != DiagnosticDefinitionChange.Unchanged &&
                ParentScope(row.Scope) is not null)
            .Select(row => new RootKey(row.Ecu.Trim().ToUpperInvariant(),
                ParentScope(row.Scope)!, row.Identifier))
            .ToHashSet();
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            if (row.Change != DiagnosticDefinitionChange.Unchanged ||
                row.Scope is not ("DID" or "DTC" or "Routine") ||
                !affectedParents.Contains(new RootKey(row.Ecu.Trim().ToUpperInvariant(),
                    row.Scope, row.Identifier)))
                continue;
            rows[i] = row with
            {
                Change = DiagnosticDefinitionChange.Changed,
                Impact = DiagnosticDefinitionImpact.Structure,
                ChangedFields = "Nested field / Engineering PAR definition changed",
                RequiresReview = true
            };
        }
        return new DiagnosticDefinitionDiffReport(rows);
    }

    private static string? ParentScope(string scope) => scope switch
    {
        "DID Field" or "Engineering PAR" => "DID",
        "DTC Snapshot" or "DTC Extended" => "DTC",
        "Routine Input" or "Routine Output" => "Routine",
        _ => null
    };

    private static DiagnosticDefinitionImpact ClassifyImpact(string scope, IReadOnlyList<string> changed)
    {
        if (changed.Count == 0) return DiagnosticDefinitionImpact.None;
        if (scope == "Transport") return DiagnosticDefinitionImpact.Transport;
        if (changed.Any(field => field is "Required session" or "Security level"))
            return DiagnosticDefinitionImpact.Access;
        if (scope == "DID Field" || scope == "DTC Snapshot" ||
            scope == "DTC Extended" || scope == "Routine Input" || scope == "Routine Output")
        {
            if (changed.Any(field => field is "Bit offset" or "Bit length" or
                    "Byte order" or "Signed" or "Data type" or "Factor" or
                    "Offset" or "Unit" or "Minimum" or "Maximum" or
                    "Conversion supported" or "Raw meanings"))
                return DiagnosticDefinitionImpact.Encoding;
        }
        if (scope == "Engineering PAR" &&
            changed.Any(field => field is "Name" or "Long name" or
                "Type reference" or "Source ID" or "Definition origin" or
                "Declared PAR code" or "Parent path" or "Depth" or "Node type" or
                "Data type" or "Unit" or "Declared bits" or "Declared byte position" or
                "Declared bit position" or "Declared bit offset" or
                "Reference status" or "Raw declared meanings"))
            return DiagnosticDefinitionImpact.Structure;
        if (changed.Any(field => field is "Field count" or "PAR count" or
                "Snapshot count" or "Extended count" or "Input count" or
                "Output count" or "DID bit length" or "DID data type" or
                "DID factor" or "DID offset" or "DID unit" or "DID minimum" or
                "DID maximum" or "Definition issues" or "Variant type"))
            return DiagnosticDefinitionImpact.Structure;
        return DiagnosticDefinitionImpact.Documentation;
    }

    private static Dictionary<Key, List<Entry>> Index(IReadOnlyList<Entry> entries)
    {
        var index = new Dictionary<Key, List<Entry>>();
        foreach (var entry in entries)
        {
            var key = new Key(entry.Ecu.Trim().ToUpperInvariant(), entry.Scope,
                entry.Identifier, entry.Path);
            if (!index.TryGetValue(key, out var group))
                index[key] = group = [];
            group.Add(entry);
        }
        return index;
    }

    private static IReadOnlyList<Entry> Flatten(IReadOnlyList<DidCatalog> catalogs)
    {
        var entries = new List<Entry>();
        foreach (var catalog in catalogs)
        {
            var source = FileName(catalog.SourcePath);
            foreach (var did in catalog.Parameters)
            {
                // PDX sources may expose the original member path per DID,
                // rather than just the containing package's file name.
                var origin = FileName(string.IsNullOrWhiteSpace(did.SourceFile)
                    ? catalog.SourcePath : did.SourceFile);
                var id = $"0x{did.Did:X4}";
                var ecu = did.Ecu.Length == 0 ? catalog.Ecu : did.Ecu;
                Add(entries, "DID", "DID", ecu, id, "", did.Name, origin,
                    ("Name", did.Name), ("Long name", did.LongName),
                    ("Description", did.Description), ("DID unit", did.Unit),
                    ("DID minimum", Fmt(did.Minimum)), ("DID maximum", Fmt(did.Maximum)),
                    ("DID factor", Fmt(did.Factor)), ("DID offset", Fmt(did.Offset)),
                    ("DID data type", did.DataType), ("DID bit length", Fmt(did.BitLength)),
                    ("Field count", Fmt(did.Fields.Count)), ("PAR count", Fmt(did.EngineeringParameters.Count)),
                    ("Definition issues", string.Join(" | ", did.EngineeringIssues)));
                AddFields(entries, "DID Field", "DID", ecu, id, origin, did.Fields);
                foreach (var node in did.EngineeringParameters)
                {
                    Add(entries, "Engineering PAR", "DID", ecu, id, node.Path, node.Name, origin,
                        ("Name", node.Name), ("Long name", node.LongName),
                        ("Description", node.Description), ("Parent path", node.ParentPath),
                        ("Depth", Fmt(node.Depth)), ("Node type", node.Kind),
                        ("Source ID", node.SourceId), ("Type reference", node.TypeReference),
                        ("Data type", node.DataType), ("Unit", node.Unit),
                        ("Declared bits", Fmt(node.DeclaredBitLength)),
                        ("Reference status", node.ReferenceStatus),
                        ("Definition origin", node.DefinitionOrigin),
                        ("Declared PAR code", node.DeclaredParCode),
                        ("Declared byte position", node.DeclaredBytePosition),
                        ("Declared bit position", node.DeclaredBitPosition),
                        ("Declared bit offset", node.DeclaredBitOffset),
                        ("Raw declared meanings", string.Join(" | ", node.DeclaredValues
                            .Select(value => string.Join("/", value.LowerRaw,
                                value.UpperRaw, value.Meaning, value.Description, value.Status)))));
                }
            }
            foreach (var dtc in catalog.DtcDefinitions)
            {
                var origin = FileName(string.IsNullOrWhiteSpace(dtc.SourceFile)
                    ? catalog.SourcePath : dtc.SourceFile);
                var ecu = dtc.Ecu.Length == 0 ? catalog.Ecu : dtc.Ecu;
                var id = $"0x{dtc.Code:X6}";
                Add(entries, "DTC", "DTC", ecu, id, "", dtc.Name, origin,
                    ("Name", dtc.Name), ("Display name", dtc.DisplayText),
                    ("Description", dtc.Description),
                    ("Snapshot count", Fmt(dtc.SnapshotFields.Count)),
                    ("Extended count", Fmt(dtc.ExtendedDataFields.Count)));
                AddFields(entries, "DTC Snapshot", "DTC", ecu, id, origin, dtc.SnapshotFields);
                AddFields(entries, "DTC Extended", "DTC", ecu, id, origin, dtc.ExtendedDataFields);
            }
            foreach (var routine in catalog.RoutineDefinitions)
            {
                var origin = FileName(string.IsNullOrWhiteSpace(routine.SourceFile)
                    ? catalog.SourcePath : routine.SourceFile);
                var ecu = routine.Ecu.Length == 0 ? catalog.Ecu : routine.Ecu;
                var id = $"0x{routine.Identifier:X4}";
                Add(entries, "Routine", "Routine", ecu, id, "", routine.Name, origin,
                    ("Name", routine.Name), ("Long name", routine.LongName),
                    ("Description", routine.Description),
                    ("Required session", routine.RequiredSession),
                    ("Security level", routine.RequiredSecurityLevel),
                    ("Input count", Fmt(routine.InputFields.Count)),
                    ("Output count", Fmt(routine.OutputFields.Count)));
                AddFields(entries, "Routine Input", "Routine", ecu, id, origin, routine.InputFields);
                AddFields(entries, "Routine Output", "Routine", ecu, id, origin, routine.OutputFields);
            }
            foreach (var service in catalog.ServiceDefinitions)
            {
                var origin = FileName(string.IsNullOrWhiteSpace(service.SourceFile)
                    ? catalog.SourcePath : service.SourceFile);
                var ecu = service.Ecu.Length == 0 ? catalog.Ecu : service.Ecu;
                var key = $"0x{service.ServiceId:X2}/" +
                    (service.SubFunction is byte sub ? $"0x{sub:X2}" : "?") + "/" +
                    (service.Identifier is int sid ? $"0x{sid:X}" : "?");
                // SID alone is not always a service identity; unconstrained
                // services are name-scoped to avoid cross-ECU guessing.
                if (service.Identifier is null && service.SubFunction is null)
                    key += "/" + service.Name;
                Add(entries, "Service", "Service", ecu, key, "", service.Name, origin,
                    ("Name", service.Name), ("Long name", service.LongName),
                    ("Description", service.Description));
            }
            foreach (var variant in catalog.VariantDefinitions)
                Add(entries, "Variant", "Variant", catalog.Ecu, variant.Name, "",
                    variant.Name, source,
                    ("Name", variant.Name), ("Variant type", variant.Type),
                    ("Description", variant.Description));
            foreach (var connection in catalog.ConnectionDefinitions)
            {
                var origin = FileName(string.IsNullOrWhiteSpace(connection.SourceFile)
                    ? catalog.SourcePath : connection.SourceFile);
                var ecu = connection.Ecu.Length == 0 ? catalog.Ecu : connection.Ecu;
                Add(entries, "Transport", "Transport", ecu, connection.Name, "",
                    connection.Name, origin,
                    ("Name", connection.Name),
                    ("Request CAN ID", Fmt(connection.RequestIdentifier)),
                    ("Response CAN ID", Fmt(connection.ResponseIdentifier)),
                    ("Extended CAN ID", Fmt(connection.IsExtendedIdentifier)),
                    ("Addressing", connection.AddressingType));
            }
        }
        return entries;
    }

    private static void AddFields(List<Entry> output, string scope, string root,
        string ecu, string id, string source, IReadOnlyList<DiagnosticFieldDefinition> fields)
    {
        foreach (var field in fields)
            Add(output, scope, root, ecu, id, field.Name, field.Name, source,
                ("Name", field.Name), ("Long name", field.LongName),
                ("Description", field.Description),
                ("Bit offset", Fmt(field.BitOffset)), ("Bit length", Fmt(field.BitLength)),
                ("Data type", field.DataType), ("Byte order", field.ByteOrder),
                ("Signed", Fmt(field.IsSigned)), ("Unit", field.Unit),
                ("Factor", Fmt(field.Factor)), ("Offset", Fmt(field.Offset)),
                ("Minimum", Fmt(field.Minimum)), ("Maximum", Fmt(field.Maximum)),
                ("Conversion supported", Fmt(field.SupportsPhysicalConversion)),
                ("Raw meanings", string.Join(" | ", field.EnumValues.OrderBy(value => value.Value)
                    .ThenBy(value => value.Text, StringComparer.Ordinal)
                    .Select(value => $"{value.Value}:{value.Text}:{value.Description}"))));
    }

    private static void Add(List<Entry> entries, string scope, string root,
        string ecu, string id, string path, string name, string source,
        params (string Name, string Value)[] data)
    {
        var props = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in data)
            props.Add(key, value);
        entries.Add(new Entry(scope, ecu, id, path, root, name, source, props));
    }

    private static string FileName(string path) =>
        path.Replace('\\', '/').Split('/').LastOrDefault() ?? string.Empty;

    private static string Fmt(object? value) => value switch
    {
        null => "(undefined)",
        double number => number.ToString("R", CultureInfo.InvariantCulture),
        bool flag => flag ? "true" : "false",
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty
    };

    private static string Render(Entry? entry) => entry is null ? string.Empty :
        string.Join("; ", entry.Fields.OrderBy(item => item.Key, StringComparer.Ordinal)
            .Select(item => $"{item.Key}: {item.Value}"));
}
