using System.Globalization;

namespace RbgCanInsight.DiagData;

/// <summary>Conservative navigation target for a diagnostic-diff row. Matching
/// the loaded source file and unique ECU/identifier prevents a different
/// imported variant being selected just because its DID number is equal.
/// This is source provenance, not an assertion of OEM semantic equivalence.</summary>
public sealed record DiagnosticDiffNavigationTarget(
    string RootKind,
    string Identifier,
    string Ecu,
    string SourceFile,
    string ChildScope = "",
    string ChildPath = "");

public sealed record DiagnosticDiffNavigationResult(
    DiagnosticDiffNavigationTarget? Target, string Explanation)
{
    public bool CanNavigate => Target is not null;
}

public static class DiagnosticDiffNavigationResolver
{
    public static DiagnosticDiffNavigationResult Resolve(
        DiagnosticDefinitionDiffRow diff,
        IReadOnlyList<DidCatalog> loadedCatalogs,
        string selectedVersionPath)
    {
        ArgumentNullException.ThrowIfNull(diff);
        ArgumentNullException.ThrowIfNull(loadedCatalogs);
        ArgumentException.ThrowIfNullOrWhiteSpace(selectedVersionPath);

        var rootKind = diff.Scope switch
        {
            "DID" or "DID Field" or "Engineering PAR" => "DID",
            "DTC" or "DTC Snapshot" or "DTC Extended" => "DTC",
            "Routine" or "Routine Input" or "Routine Output" => "Routine",
            _ => string.Empty
        };
        if (rootKind.Length == 0)
            return Fail("This comparison row does not have a supported DID/DTC/routine inspector target.");

        if (!diff.Identifier.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ||
            !int.TryParse(diff.Identifier.AsSpan(2), NumberStyles.HexNumber,
                CultureInfo.InvariantCulture, out var identifier))
            return Fail("The comparison row does not contain a valid diagnostic hex identifier.");

        // A previously loaded catalog with the same DID/ECU but a different
        // physical source path must NOT be mistaken for the version compared.
        var sourceMatches = loadedCatalogs.Where(catalog =>
            SamePath(catalog.SourcePath, selectedVersionPath)).ToArray();
        if (sourceMatches.Length == 0)
            return Fail("The selected comparison version is not loaded. Load that exact CDD/ODX/PDX file into the diagnostic catalog first.");

        var candidates = new List<(string Source, string Ecu, IReadOnlyList<DiagnosticFieldDefinition> Fields,
            IReadOnlyList<DiagnosticEngineeringParameter> Parameters)>();
        foreach (var catalog in sourceMatches)
        {
            if (rootKind == "DID")
                foreach (var did in catalog.Parameters.Where(item =>
                             item.Did == identifier && SameEcu(item.Ecu, catalog.Ecu, diff.Ecu)))
                    candidates.Add((did.SourceFile, ActualEcu(did.Ecu, catalog.Ecu),
                        did.Fields, did.EngineeringParameters));
            else if (rootKind == "DTC")
                foreach (var dtc in catalog.DtcDefinitions.Where(item =>
                             item.Code == identifier && SameEcu(item.Ecu, catalog.Ecu, diff.Ecu)))
                    candidates.Add((dtc.SourceFile, ActualEcu(dtc.Ecu, catalog.Ecu),
                        [.. dtc.SnapshotFields, .. dtc.ExtendedDataFields], []));
            else
                foreach (var routine in catalog.RoutineDefinitions.Where(item =>
                             item.Identifier == identifier && SameEcu(item.Ecu, catalog.Ecu, diff.Ecu)))
                    candidates.Add((routine.SourceFile, ActualEcu(routine.Ecu, catalog.Ecu),
                        [.. routine.InputFields, .. routine.OutputFields], []));
        }
        if (candidates.Count == 0)
            return Fail("No matching ECU and diagnostic identifier exists in the loaded version. It may have been removed.");
        if (candidates.Count != 1)
            return Fail("Several matching ECU/identifier definitions exist in this source. Select the correct variant manually; no first-match navigation.");

        var selected = candidates[0];
        // Selecting a missing nested node would show an unrelated default
        // subparameter in the inspector, so reject instead of guessing.
        if (diff.Scope == "Engineering PAR" &&
            selected.Parameters.Count(parameter => parameter.Path == diff.Path) != 1)
            return Fail("The exact Engineering PAR hierarchy path is absent or ambiguous in this version.");
        if (diff.Scope is "DID Field" or "DTC Snapshot" or "DTC Extended" or
            "Routine Input" or "Routine Output")
        {
            var matchingFields = SelectFields(sourceMatches, rootKind, identifier,
                    diff.Ecu, diff.Scope)
                .Where(field => string.Equals(field.Name, diff.Path, StringComparison.Ordinal))
                .Take(2).Count();
            if (matchingFields != 1)
                return Fail("The exact source field is absent or ambiguous in this version.");
        }

        return new DiagnosticDiffNavigationResult(
            new DiagnosticDiffNavigationTarget(rootKind, diff.Identifier, selected.Ecu,
                selected.Source, diff.Scope == rootKind ? string.Empty : diff.Scope,
                diff.Path), string.Empty);
    }

    private static IEnumerable<DiagnosticFieldDefinition> SelectFields(
        IEnumerable<DidCatalog> catalogs, string rootKind, int code, string ecu,
        string scope)
    {
        foreach (var catalog in catalogs)
        {
            if (rootKind == "DID")
            {
                if (scope != "DID Field") continue;
                foreach (var did in catalog.Parameters.Where(did =>
                             did.Did == code && SameEcu(did.Ecu, catalog.Ecu, ecu)))
                    foreach (var field in did.Fields) yield return field;
            }
            else if (rootKind == "DTC")
            {
                foreach (var dtc in catalog.DtcDefinitions.Where(dtc =>
                             dtc.Code == code && SameEcu(dtc.Ecu, catalog.Ecu, ecu)))
                    foreach (var field in scope == "DTC Snapshot" ?
                                 dtc.SnapshotFields : dtc.ExtendedDataFields)
                        yield return field;
            }
            else
            {
                foreach (var routine in catalog.RoutineDefinitions.Where(routine =>
                             routine.Identifier == code && SameEcu(routine.Ecu, catalog.Ecu, ecu)))
                    foreach (var field in scope == "Routine Input" ?
                                 routine.InputFields : routine.OutputFields)
                        yield return field;
            }
        }
    }

    private static string ActualEcu(string sourceEcu, string fallback) =>
        string.IsNullOrWhiteSpace(sourceEcu) ? fallback : sourceEcu;
    private static bool SameEcu(string sourceEcu, string fallback, string wanted) =>
        string.Equals(ActualEcu(sourceEcu, fallback), wanted,
            StringComparison.OrdinalIgnoreCase);
    private static bool SamePath(string lhs, string rhs) =>
        string.Equals(Path.GetFullPath(lhs), Path.GetFullPath(rhs),
            StringComparison.OrdinalIgnoreCase);

    private static DiagnosticDiffNavigationResult Fail(string why) => new(null, why);
}
