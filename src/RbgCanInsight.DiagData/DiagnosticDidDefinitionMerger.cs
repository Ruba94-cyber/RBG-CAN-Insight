namespace RbgCanInsight.DiagData;

/// <summary>
/// Preserve all source-declared engineering trees when one ECU declares a DID
/// in multiple read/write or variant-related services. Existing UI/correlation
/// consumers still receive one DidParameter per numeric DID. Conflicting physical
/// layouts are disabled rather than silently choosing an arbitrary decoder.
/// </summary>
public static class DiagnosticDidDefinitionMerger
{
    public static void AddOrMerge(
        IDictionary<int, DidParameter> parameters, DidParameter candidate, string origin)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentException.ThrowIfNullOrWhiteSpace(origin);

        var incoming = candidate with
        {
            EngineeringParameterDefinitions = candidate.EngineeringParameters
                .Select(node => node with { DefinitionOrigin = origin }).ToArray()
        };

        if (!parameters.TryGetValue(incoming.Did, out var existing))
        {
            parameters.Add(incoming.Did, incoming);
            return;
        }

        // Only definitions from the same ECU and source are mergeable. The
        // numeric DID is not globally unique across ECU variants/files.
        if (!string.Equals(existing.Ecu, incoming.Ecu, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(existing.SourceFile, incoming.SourceFile, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Cannot merge diagnostic definitions from different ECU/source scopes.");

        var issues = existing.EngineeringIssues.Concat(incoming.EngineeringIssues).ToList();
        var tree = existing.EngineeringParameters.ToList();
        var addition = incoming.EngineeringParameters;
        // All already-merged sources are discoverable by their origin labels.
        // A later service can be equivalent to *one* earlier source tree, even
        // after a different service introduced a second hierarchy namespace.
        var matchedIndices = FindEquivalentSourceTree(tree, addition);
        if (matchedIndices is not null)
        {
            foreach (var index in matchedIndices)
            {
                var origins = SplitOrigins(tree[index].DefinitionOrigin)
                    .ToHashSet(StringComparer.Ordinal);
                origins.Add(origin);
                tree[index] = tree[index] with
                {
                    DefinitionOrigin = string.Join(" | ",
                        origins.OrderBy(value => value, StringComparer.Ordinal))
                };
            }
        }
        else if (addition.Count > 0)
        {
            if (tree.Count > 0)
            {
                // Prefix every node of the new source tree in a distinct
                // namespace; ensure the prefix itself is never reused.
                var prefixes = tree.Select(node =>
                    node.Path.Split('/', StringSplitOptions.RemoveEmptyEntries)
                        .FirstOrDefault() ?? string.Empty)
                    .ToHashSet(StringComparer.Ordinal);
                var counter = 2;
                string prefix;
                do
                {
                    prefix = "/additional-definition-" +
                        counter.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    counter++;
                }
                while (prefixes.Contains(prefix.TrimStart('/')));

                tree.AddRange(addition.Select(node => node with
                {
                    Path = prefix + node.Path,
                    ParentPath = node.ParentPath.Length == 0
                        ? string.Empty : prefix + node.ParentPath
                }));
                issues.Add("Multiple engineering structures declared for the same DID; " +
                    "each definition is preserved in a separate hierarchy scope.");
            }
            else tree.AddRange(addition);
        }

        var currentFields = existing.Fields;
        var newFields = incoming.Fields;
        // Once an ambiguity is observed, a later third service must never
        // re-enable a decoder by providing another superficially valid layout.
        var priorFieldConflict = existing.EngineeringIssues.Any(issue =>
            issue.StartsWith("Conflicting physical field layouts", StringComparison.Ordinal));
        var priorScalarConflict = existing.EngineeringIssues.Any(issue =>
            issue.StartsWith("Conflicting whole-DID scalar descriptions", StringComparison.Ordinal));
        var fieldConflict = priorFieldConflict ||
            currentFields.Count > 0 && newFields.Count > 0 &&
            !EquivalentFields(currentFields, newFields);
        var scalarConflict = priorScalarConflict ||
            Contradicts(existing.Unit, incoming.Unit) ||
            Contradicts(existing.Factor, incoming.Factor) ||
            Contradicts(existing.Offset, incoming.Offset) ||
            Contradicts(existing.BitLength, incoming.BitLength) ||
            Contradicts(existing.Minimum, incoming.Minimum) ||
            Contradicts(existing.Maximum, incoming.Maximum) ||
            Contradicts(existing.DataType, incoming.DataType);

        if (fieldConflict)
            issues.Add("Conflicting physical field layouts: automatic DID field decoding disabled.");
        if (scalarConflict)
            issues.Add("Conflicting whole-DID scalar descriptions: physical conversion " +
                "and bit length remain unverified.");

        var fields = fieldConflict ? Array.Empty<DiagnosticFieldDefinition>() :
            currentFields.Count > 0 ? currentFields : newFields;
        // Even when both services declare the same top-level scale, different
        // field structures make the whole-DID physical interpretation unsafe.
        var suppressScalar = fieldConflict || scalarConflict;

        parameters[incoming.Did] = existing with
        {
            FieldDefinitions = fields,
            EngineeringParameterDefinitions = tree,
            DefinitionIssues = issues.Distinct(StringComparer.Ordinal).ToArray(),
            // Read and write services can be listed in either order.
            // Preserve declared values from the one that actually provides
            // them, but never resurrect any contradicted conversion.
            Unit = suppressScalar ? string.Empty :
                existing.Unit.Length > 0 ? existing.Unit : incoming.Unit,
            Factor = suppressScalar ? null : existing.Factor ?? incoming.Factor,
            Offset = suppressScalar ? null : existing.Offset ?? incoming.Offset,
            BitLength = suppressScalar ? null : existing.BitLength ?? incoming.BitLength,
            Minimum = suppressScalar ? null : existing.Minimum ?? incoming.Minimum,
            Maximum = suppressScalar ? null : existing.Maximum ?? incoming.Maximum,
            DataType = suppressScalar ? string.Empty :
                existing.DataType.Length > 0 ? existing.DataType : incoming.DataType
        };
    }

    private static bool Contradicts(string left, string right) =>
        left.Length > 0 && right.Length > 0 &&
        !string.Equals(left, right, StringComparison.Ordinal);

    private static bool Contradicts<T>(T? left, T? right) where T : struct =>
        left.HasValue && right.HasValue &&
        !EqualityComparer<T>.Default.Equals(left.Value, right.Value);

    private static IEnumerable<string> SplitOrigins(string origins) =>
        origins.Split(" | ",
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Compare one existing service's complete tree, not the union of
    /// all trees already merged into the DID. Return the indexes whose origin
    /// should be augmented, or null when the incoming tree is genuinely new.</summary>
    private static IReadOnlyList<int>? FindEquivalentSourceTree(
        IReadOnlyList<DiagnosticEngineeringParameter> existing,
        IReadOnlyList<DiagnosticEngineeringParameter> incoming)
    {
        if (incoming.Count == 0 || existing.Count == 0) return null;
        var sources = existing.SelectMany(node => SplitOrigins(node.DefinitionOrigin))
            .Distinct(StringComparer.Ordinal);
        foreach (var source in sources)
        {
            var indices = Enumerable.Range(0, existing.Count)
                .Where(index => SplitOrigins(existing[index].DefinitionOrigin)
                    .Contains(source, StringComparer.Ordinal))
                .ToArray();
            if (indices.Length != incoming.Count) continue;
            var same = indices.Select(index => existing[index])
                .Zip(incoming).All(pair =>
                    (CanonicalNode(pair.First) with { DeclaredValueDefinitions = null }) ==
                    (CanonicalNode(pair.Second) with { DeclaredValueDefinitions = null }) &&
                    pair.First.DeclaredValues.SequenceEqual(pair.Second.DeclaredValues));
            if (same) return indices;
        }
        return null;
    }

    private static DiagnosticEngineeringParameter CanonicalNode(
        DiagnosticEngineeringParameter node) =>
        node with
        {
            DefinitionOrigin = string.Empty,
            Path = CanonicalPath(node.Path),
            ParentPath = CanonicalPath(node.ParentPath)
        };

    private static string CanonicalPath(string value)
    {
        // A previous dissimilar source adds this namespace to every nested
        // path. Only that generated prefix is removed for equality checks.
        const string prefix = "/additional-definition-";
        if (!value.StartsWith(prefix, StringComparison.Ordinal)) return value;
        var slash = value.IndexOf('/', prefix.Length);
        return slash < 0 ? value : value[slash..];
    }

    private static bool EquivalentFields(
        IReadOnlyList<DiagnosticFieldDefinition> a,
        IReadOnlyList<DiagnosticFieldDefinition> b) =>
        a.Count == b.Count && a.Zip(b).All(pair =>
        {
            var x = pair.First;
            var y = pair.Second;
            return x.Name == y.Name && x.LongName == y.LongName &&
                x.BitOffset == y.BitOffset && x.BitLength == y.BitLength &&
                x.DataType == y.DataType && x.Unit == y.Unit &&
                x.ByteOrder == y.ByteOrder && x.IsSigned == y.IsSigned &&
                x.Minimum == y.Minimum && x.Maximum == y.Maximum &&
                x.Factor == y.Factor && x.Offset == y.Offset &&
                x.SupportsPhysicalConversion == y.SupportsPhysicalConversion &&
                Equals(x.Odx, y.Odx) &&
                x.EnumValues.SequenceEqual(y.EnumValues);
        });
}
