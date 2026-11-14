using System.Globalization;
using System.Text;

namespace RbgCanInsight.DiagData;

public enum CatalogSeverity
{
    Error,
    Warning,
    Review
}

/// <summary>A named, documented consistency rule for CDD / ODX / PDX diagnostic descriptions.</summary>
public sealed record CatalogConsistencyRule(
    string Id, string Title, CatalogSeverity DefaultSeverity, string Category, string Description);

/// <summary>One violation of a rule. <see cref="Subject"/> names the catalog object (DID, DTC, routine ...).</summary>
public sealed record CatalogConsistencyFinding(
    CatalogConsistencyRule Rule, CatalogSeverity Severity, string Ecu, string Subject, string Detail, string SourceFile)
{
    public string RuleId => Rule.Id;
}

public sealed record CatalogConsistencyReport(
    IReadOnlyList<string> CatalogNames,
    IReadOnlyList<CatalogConsistencyFinding> Findings,
    IReadOnlyList<CatalogConsistencyRule> RulesEvaluated,
    bool Truncated)
{
    public int Count(CatalogSeverity severity) => Findings.Count(f => f.Severity == severity);

    /// <summary>First line is a one-line summary suitable for a status bar; the following lines list findings.</summary>
    public string ToSummaryText()
    {
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture,
            $"Catalog consistency check of {CatalogNames.Count} catalog(s): {Count(CatalogSeverity.Error)} error(s), " +
            $"{Count(CatalogSeverity.Warning)} warning(s), {Count(CatalogSeverity.Review)} review item(s); " +
            $"{RulesEvaluated.Count} rule(s) evaluated");
        if (Truncated) sb.Append("; output truncated");
        sb.AppendLine(".");
        foreach (var f in Findings)
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"[{f.Severity}] {f.Rule.Id} {f.Rule.Title} · {f.Ecu} · {f.Subject} · {f.Detail}");
        return sb.ToString();
    }

    /// <summary>RFC 4180 CSV: rule, severity, category, ECU, subject, detail, source file.</summary>
    public string ToCsv()
    {
        var sb = new StringBuilder("Rule,Title,Severity,Category,ECU,Subject,Detail,Source file\r\n");
        foreach (var f in Findings)
            sb.Append(string.Join(',', new[]
                {
                    f.Rule.Id, f.Rule.Title, f.Severity.ToString(), f.Rule.Category, f.Ecu, f.Subject, f.Detail,
                    Path.GetFileName(f.SourceFile)
                }.Select(Csv)))
              .Append("\r\n");
        return sb.ToString();

        static string Csv(string v)
        {
            if (v.Length > 0 && v[0] is '=' or '+' or '-' or '@') v = "'" + v;
            return v.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
        }
    }
}

public sealed record CatalogConsistencyOptions
{
    public int MaxFindings { get; init; } = 5000;
}

/// <summary>
/// Rule-based consistency check of loaded diagnostic catalogs (CANdelaStudio "consistency checks" /
/// ODX Checker equivalent, restricted to what <see cref="DidCatalog"/> retains). Every rule has a
/// stable ID (CAT-0xx), severity and documented meaning (<see cref="Rules"/>). It checks the
/// description, not an ECU: a clean report does not mean the file is a valid ODX/CDD against its
/// schema, and the rule set was not compared to Vector's own checker. Absence of a declaration is
/// reported as "review", never as proof of absence in the ECU.
/// </summary>
public static class CatalogConsistencyChecker
{
    private static CatalogConsistencyRule R(string id, string title, CatalogSeverity severity, string category, string description) =>
        new(id, title, severity, category, description);

    public static readonly CatalogConsistencyRule DuplicateDid = R("CAT-001", "Duplicate DID", CatalogSeverity.Warning, "Identity",
        "The same DID is defined more than once for one ECU in one catalog; the decoder cannot choose between the definitions.");
    public static readonly CatalogConsistencyRule DuplicateDtc = R("CAT-002", "Duplicate DTC", CatalogSeverity.Warning, "Identity",
        "The same DTC number is defined more than once for one ECU in one catalog.");
    public static readonly CatalogConsistencyRule DuplicateRoutine = R("CAT-003", "Duplicate routine", CatalogSeverity.Warning, "Identity",
        "The same routine identifier is defined more than once for one ECU in one catalog.");
    public static readonly CatalogConsistencyRule DidWithoutType = R("CAT-004", "DID without data type or length", CatalogSeverity.Review, "Completeness",
        "A DID has no bit length, no field layout and no data type: its payload cannot be decoded or delimited.");
    public static readonly CatalogConsistencyRule FieldOverlap = R("CAT-005", "Overlapping fields in a DID", CatalogSeverity.Error, "Layout",
        "Two fields of one DID occupy common bits.");
    public static readonly CatalogConsistencyRule FieldOutsideDid = R("CAT-006", "Field outside the DID length", CatalogSeverity.Error, "Layout",
        "A field extends beyond the declared bit length of its DID.");
    public static readonly CatalogConsistencyRule InvalidFieldGeometry = R("CAT-007", "Invalid field position or length", CatalogSeverity.Error, "Layout",
        "A field has a negative bit offset or a bit length of zero or less.");
    public static readonly CatalogConsistencyRule InvertedLimits = R("CAT-008", "Minimum greater than maximum", CatalogSeverity.Error, "Scaling",
        "The declared minimum of a DID or field is larger than its maximum.");
    public static readonly CatalogConsistencyRule ZeroFactor = R("CAT-009", "Zero scaling factor", CatalogSeverity.Error, "Scaling",
        "A linear conversion with factor 0 maps every raw value to the offset.");
    public static readonly CatalogConsistencyRule EnumProblem = R("CAT-010", "Inconsistent value table", CatalogSeverity.Warning, "Scaling",
        "A field's value table repeats a numeric value or contains a value that does not fit in the field's bit length.");
    public static readonly CatalogConsistencyRule ConflictingDefinitions = R("CAT-011", "Conflicting definitions merged", CatalogSeverity.Warning, "Identity",
        "The reader recorded that several service definitions share a DID with conflicting layouts; the decoder treats them as raw.");
    public static readonly CatalogConsistencyRule DtcWithoutText = R("CAT-012", "DTC without name or text", CatalogSeverity.Review, "Completeness",
        "A DTC has neither a display text, a description nor a distinct name.");
    public static readonly CatalogConsistencyRule DtcCodeRange = R("CAT-013", "DTC number out of range", CatalogSeverity.Error, "Identity",
        "A DTC number does not fit in 24 bits.");
    public static readonly CatalogConsistencyRule IdentifierRange = R("CAT-014", "Identifier out of range", CatalogSeverity.Error, "Identity",
        "A DID or routine identifier does not fit in 16 bits.");
    public static readonly CatalogConsistencyRule DuplicateService = R("CAT-015", "Duplicate service definition", CatalogSeverity.Review, "Identity",
        "The same service ID, sub-function and identifier are defined more than once for one ECU.");
    public static readonly CatalogConsistencyRule GuessedServiceId = R("CAT-016", "Service ID guessed from names", CatalogSeverity.Review, "Provenance",
        "The service ID was derived from service or qualifier names, not from the request PDU structure; it may be wrong.");
    public static readonly CatalogConsistencyRule ServiceWithoutCondition = R("CAT-017", "Service without session/security condition", CatalogSeverity.Review, "Completeness",
        "Other services of this catalog declare session or security pre-conditions but this one declares none. Absence is not proof that none is required.");
    public static readonly CatalogConsistencyRule DanglingCondition = R("CAT-018", "Condition refers to an undefined service", CatalogSeverity.Warning, "References",
        "A session/security condition names a service (and identifier) that the catalog does not define.");
    public static readonly CatalogConsistencyRule ConnectionIdentifiers = R("CAT-019", "Invalid diagnostic connection identifiers", CatalogSeverity.Error, "Addressing",
        "A CAN identifier exceeds 11 bits without the extended flag, exceeds 29 bits, or request and response identifier are identical.");
    public static readonly CatalogConsistencyRule DuplicateConnectionId = R("CAT-020", "Request identifier shared by several ECUs", CatalogSeverity.Warning, "Addressing",
        "Two connections of different ECUs use the same request CAN identifier.");
    public static readonly CatalogConsistencyRule UnresolvedReference = R("CAT-021", "Unresolved reference", CatalogSeverity.Error, "References",
        "A parameter refers to a data type or structure that is not defined (or only ambiguously) in the file.");
    public static readonly CatalogConsistencyRule DuplicateFieldName = R("CAT-022", "Duplicate field name in a DID", CatalogSeverity.Warning, "Identity",
        "Two fields of one DID share a name, so exports and mappings cannot tell them apart.");
    public static readonly CatalogConsistencyRule FieldWithoutDataType = R("CAT-023", "Field without data type", CatalogSeverity.Review, "Completeness",
        "A field has a position but no data type; it is shown as raw bytes.");
    public static readonly CatalogConsistencyRule NoPhysicalConversion = R("CAT-024", "Field without physical conversion", CatalogSeverity.Review, "Scaling",
        "The reader could not establish a verified physical conversion (piecewise, unsupported or text-table only); raw values are shown.");
    public static readonly CatalogConsistencyRule EmptyCatalog = R("CAT-025", "Catalog without diagnostic content", CatalogSeverity.Warning, "Completeness",
        "No DID, DTC, routine or service could be read from the file.");
    public static readonly CatalogConsistencyRule DidWithoutName = R("CAT-026", "DID without name", CatalogSeverity.Review, "Completeness",
        "A DID has no name or long name.");

    public static readonly IReadOnlyList<CatalogConsistencyRule> Rules =
    [
        DuplicateDid, DuplicateDtc, DuplicateRoutine, DidWithoutType, FieldOverlap, FieldOutsideDid,
        InvalidFieldGeometry, InvertedLimits, ZeroFactor, EnumProblem, ConflictingDefinitions, DtcWithoutText,
        DtcCodeRange, IdentifierRange, DuplicateService, GuessedServiceId, ServiceWithoutCondition,
        DanglingCondition, ConnectionIdentifiers, DuplicateConnectionId, UnresolvedReference,
        DuplicateFieldName, FieldWithoutDataType, NoPhysicalConversion, EmptyCatalog, DidWithoutName
    ];

    public static CatalogConsistencyReport Check(DidCatalog catalog, CatalogConsistencyOptions? options = null) =>
        Check([catalog], options);

    public static CatalogConsistencyReport Check(IReadOnlyList<DidCatalog> catalogs, CatalogConsistencyOptions? options = null)
    {
        options ??= new CatalogConsistencyOptions();
        var findings = new List<CatalogConsistencyFinding>();
        foreach (var catalog in catalogs)
            CheckCatalog(catalog, findings);
        CheckConnectionsAcrossCatalogs(catalogs, findings);

        var ordered = findings
            .OrderBy(f => f.Severity)
            .ThenBy(f => f.Rule.Id, StringComparer.Ordinal)
            .ThenBy(f => f.Ecu, StringComparer.OrdinalIgnoreCase)
            .ThenBy(f => f.Subject, StringComparer.Ordinal)
            .ToList();
        var truncated = ordered.Count > options.MaxFindings;
        if (truncated) ordered.RemoveRange(options.MaxFindings, ordered.Count - options.MaxFindings);
        return new CatalogConsistencyReport(catalogs.Select(c => c.Name).ToArray(), ordered, Rules, truncated);
    }

    private static string Fmt(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static void CheckCatalog(DidCatalog catalog, List<CatalogConsistencyFinding> findings)
    {
        void Add(CatalogConsistencyRule rule, string ecu, string subject, string detail, CatalogSeverity? severity = null) =>
            findings.Add(new CatalogConsistencyFinding(rule, severity ?? rule.DefaultSeverity,
                string.IsNullOrEmpty(ecu) ? catalog.Ecu : ecu, subject, detail, catalog.SourcePath));

        if (catalog.Parameters.Count == 0 && catalog.DtcDefinitions.Count == 0 &&
            catalog.RoutineDefinitions.Count == 0 && catalog.ServiceDefinitions.Count == 0)
            Add(EmptyCatalog, catalog.Ecu, catalog.Name, "Nothing could be read from this description file.");

        // --- DIDs ---
        foreach (var group in catalog.Parameters.GroupBy(p => (p.Ecu.ToUpperInvariant(), p.Did)).Where(g => g.Count() > 1))
            Add(DuplicateDid, group.First().Ecu, $"DID 0x{group.Key.Did:X4}", $"{group.Count()} definitions: {string.Join(", ", group.Select(p => p.DisplayName))}.");

        foreach (var did in catalog.Parameters)
        {
            var subject = $"DID 0x{did.Did:X4} {did.DisplayName}".TrimEnd();
            if (did.Did is < 0 or > 0xFFFF)
                Add(IdentifierRange, did.Ecu, subject, $"DID value {did.Did} does not fit in 16 bits.");
            if (string.IsNullOrWhiteSpace(did.Name) && string.IsNullOrWhiteSpace(did.LongName))
                Add(DidWithoutName, did.Ecu, subject, "No name or long name.");
            if (did.BitLength is not > 0 && did.Fields.Count == 0 && string.IsNullOrWhiteSpace(did.DataType))
                Add(DidWithoutType, did.Ecu, subject, "No bit length, field layout or data type.");
            if (did.Minimum is { } min && did.Maximum is { } max && min > max)
                Add(InvertedLimits, did.Ecu, subject, $"Minimum {min.ToString("R", CultureInfo.InvariantCulture)} > maximum {max.ToString("R", CultureInfo.InvariantCulture)}.");
            if (did.Factor == 0)
                Add(ZeroFactor, did.Ecu, subject, "Scaling factor is 0.");
            foreach (var issue in did.EngineeringIssues)
                Add(ConflictingDefinitions, did.Ecu, subject, issue);

            CheckFields(did, subject, Add);

            foreach (var parameter in did.EngineeringParameters.Where(p => p.ReferenceStatus.Length > 0))
            {
                var status = parameter.ReferenceStatus;
                if (status.StartsWith("Unresolved", StringComparison.Ordinal))
                    Add(UnresolvedReference, did.Ecu, $"{subject} / {parameter.Path}", status);
                else if (status.StartsWith("Ambiguous", StringComparison.Ordinal) || status.StartsWith("Cyclic", StringComparison.Ordinal))
                    Add(UnresolvedReference, did.Ecu, $"{subject} / {parameter.Path}", status, CatalogSeverity.Warning);
            }
        }

        // --- DTCs ---
        foreach (var group in catalog.DtcDefinitions.GroupBy(d => (d.Ecu.ToUpperInvariant(), d.Code)).Where(g => g.Count() > 1))
            Add(DuplicateDtc, group.First().Ecu, $"DTC 0x{group.Key.Code:X6}", $"{group.Count()} definitions.");
        foreach (var dtc in catalog.DtcDefinitions)
        {
            var subject = $"DTC {dtc.CodeHex}";
            if (dtc.Code is < 0 or > 0xFFFFFF)
                Add(DtcCodeRange, dtc.Ecu, subject, $"DTC value {dtc.Code} does not fit in 24 bits.");
            if (string.IsNullOrWhiteSpace(dtc.DisplayText) && string.IsNullOrWhiteSpace(dtc.Description) &&
                (string.IsNullOrWhiteSpace(dtc.Name) || dtc.Name.Equals(dtc.CodeHex, StringComparison.OrdinalIgnoreCase)))
                Add(DtcWithoutText, dtc.Ecu, subject, "No display text, description or distinct name.");
            CheckFieldList(dtc.SnapshotFields, $"{subject} snapshot", dtc.Ecu, Add);
            CheckFieldList(dtc.ExtendedDataFields, $"{subject} extended data", dtc.Ecu, Add);
        }

        // --- Routines ---
        foreach (var group in catalog.RoutineDefinitions.GroupBy(r => (r.Ecu.ToUpperInvariant(), r.Identifier)).Where(g => g.Count() > 1))
            Add(DuplicateRoutine, group.First().Ecu, $"Routine 0x{group.Key.Identifier:X4}", $"{group.Count()} definitions.");
        foreach (var routine in catalog.RoutineDefinitions)
        {
            var subject = $"Routine {routine.IdentifierHex} {routine.DisplayName}".TrimEnd();
            if (routine.Identifier is < 0 or > 0xFFFF)
                Add(IdentifierRange, routine.Ecu, subject, $"Routine identifier {routine.Identifier} does not fit in 16 bits.");
            CheckFieldList(routine.InputFields, $"{subject} input", routine.Ecu, Add);
            CheckFieldList(routine.OutputFields, $"{subject} output", routine.Ecu, Add);
        }

        // --- Services and conditions ---
        foreach (var group in catalog.ServiceDefinitions
                     .GroupBy(s => (s.Ecu.ToUpperInvariant(), s.ServiceId, s.SubFunction, s.Identifier)).Where(g => g.Count() > 1))
            Add(DuplicateService, group.First().Ecu, ServiceText(group.First()), $"{group.Count()} definitions.");
        foreach (var service in catalog.ServiceDefinitions.Where(s => s.Source == DiagnosticServiceIdSource.NameHeuristic))
            Add(GuessedServiceId, service.Ecu, ServiceText(service), "Service ID was guessed from names; treat decoding of this service with care.");

        var conditions = catalog.ConditionDefinitions;
        if (conditions.Count > 0)
        {
            var conditioned = conditions.Select(c => (c.Ecu.ToUpperInvariant(), c.ServiceId, c.Identifier)).ToHashSet();
            foreach (var service in catalog.ServiceDefinitions
                         .Where(s => s.ServiceId is 0x2E or 0x31 or 0x27 or 0x2F or 0x34 or 0x36 or 0x37 or 0x11 or 0x28 or 0x85)
                         .Where(s => !conditioned.Contains((s.Ecu.ToUpperInvariant(), s.ServiceId, s.Identifier))))
                Add(ServiceWithoutCondition, service.Ecu, ServiceText(service), "No session or security pre-condition declared.");

            var defined = catalog.ServiceDefinitions.Select(s => (s.Ecu.ToUpperInvariant(), s.ServiceId, s.Identifier)).ToHashSet();
            var definedAnyIdentifier = catalog.ServiceDefinitions.Select(s => (s.Ecu.ToUpperInvariant(), s.ServiceId)).ToHashSet();
            foreach (var condition in conditions.Where(c => !defined.Contains((c.Ecu.ToUpperInvariant(), c.ServiceId, c.Identifier)) &&
                                                             !(c.Identifier is null && definedAnyIdentifier.Contains((c.Ecu.ToUpperInvariant(), c.ServiceId)))))
                Add(DanglingCondition, condition.Ecu,
                    $"Service 0x{condition.ServiceId:X2}{(condition.Identifier is { } id ? $" 0x{id:X4}" : string.Empty)} {condition.ServiceName}".TrimEnd(),
                    $"Condition '{condition.State}' ({condition.Kind}) refers to a service not defined in the catalog.");
        }

        // --- Connections ---
        foreach (var connection in catalog.ConnectionDefinitions)
        {
            var subject = $"Connection {connection.Name}";
            var maxId = connection.IsExtendedIdentifier ? 0x1FFF_FFFFu : 0x7FFu;
            if (connection.RequestIdentifier is { } req && req > maxId)
                Add(ConnectionIdentifiers, connection.Ecu, subject, $"Request identifier 0x{req:X} exceeds {(connection.IsExtendedIdentifier ? "29" : "11")} bits.");
            if (connection.ResponseIdentifier is { } res && res > maxId)
                Add(ConnectionIdentifiers, connection.Ecu, subject, $"Response identifier 0x{res:X} exceeds {(connection.IsExtendedIdentifier ? "29" : "11")} bits.");
            if (connection.RequestIdentifier is { } a && connection.ResponseIdentifier is { } b && a == b)
                Add(ConnectionIdentifiers, connection.Ecu, subject, $"Request and response share identifier 0x{a:X}.");
        }
    }

    private static string ServiceText(DiagnosticServiceDefinition service) =>
        $"Service 0x{service.ServiceId:X2}" +
        (service.SubFunction is { } sub ? $" sub 0x{sub:X2}" : string.Empty) +
        (service.Identifier is { } id ? $" id 0x{id:X4}" : string.Empty) +
        (string.IsNullOrWhiteSpace(service.Name) ? string.Empty : " " + service.Name);

    private delegate void AddFinding(CatalogConsistencyRule rule, string ecu, string subject, string detail, CatalogSeverity? severity = null);

    private static void CheckFields(DidParameter did, string subject, AddFinding add)
    {
        var declaredBits = did.BitLength;
        CheckFieldList(did.Fields, subject, did.Ecu, add, declaredBits);
    }

    private static void CheckFieldList(
        IReadOnlyList<DiagnosticFieldDefinition> fields, string subject, string ecu, AddFinding add, int? containerBits = null)
    {
        if (fields.Count == 0) return;
        var valid = new List<DiagnosticFieldDefinition>();
        foreach (var field in fields)
        {
            var label = $"{subject} / {field.Name}";
            if (field.BitOffset < 0 || field.BitLength <= 0)
            {
                add(InvalidFieldGeometry, ecu, label, $"Bit offset {Fmt(field.BitOffset)}, bit length {Fmt(field.BitLength)}.", null);
                continue;
            }
            valid.Add(field);
            if (containerBits is > 0 && (long)field.BitOffset + field.BitLength > containerBits)
                add(FieldOutsideDid, ecu, label,
                    $"Bits {Fmt(field.BitOffset)}..{Fmt(field.BitOffset + field.BitLength - 1)} exceed the declared {Fmt(containerBits.Value)} bit(s).", null);
            if (field.Minimum is { } min && field.Maximum is { } max && min > max)
                add(InvertedLimits, ecu, label, $"Minimum {min.ToString("R", CultureInfo.InvariantCulture)} > maximum {max.ToString("R", CultureInfo.InvariantCulture)}.", null);
            if (field.Factor == 0)
                add(ZeroFactor, ecu, label, "Scaling factor is 0.", null);
            if (string.IsNullOrWhiteSpace(field.DataType))
                add(FieldWithoutDataType, ecu, label, "No data type.", null);
            if (!field.SupportsPhysicalConversion && !field.HasOdxConversion)
                add(NoPhysicalConversion, ecu, label, "Physical conversion unsupported or piecewise; raw values only.", null);

            var values = field.EnumValues;
            if (values.Count > 0)
            {
                var repeated = values.GroupBy(v => v.Value).Where(g => g.Count() > 1).Select(g => g.Key).ToArray();
                if (repeated.Length > 0)
                    add(EnumProblem, ecu, label, $"Value(s) {string.Join(", ", repeated.Take(5))} defined more than once.", null);
                if (field.BitLength < 63)
                {
                    var limit = 1L << field.BitLength;
                    var tooBig = values.Where(v => !field.IsSigned ? v.Value >= limit || v.Value < 0 : v.Value >= limit / 2 || v.Value < -limit / 2)
                        .Select(v => v.Value).ToArray();
                    if (tooBig.Length > 0)
                        add(EnumProblem, ecu, label,
                            $"Value(s) {string.Join(", ", tooBig.Take(5))} do not fit in {Fmt(field.BitLength)} bit(s).", null);
                }
            }
        }

        foreach (var dup in valid.Where(f => !string.IsNullOrWhiteSpace(f.Name)).GroupBy(f => f.Name, StringComparer.Ordinal).Where(g => g.Count() > 1))
            add(DuplicateFieldName, ecu, subject, $"Field name '{dup.Key}' used {dup.Count()} times.", null);

        var ordered = valid.OrderBy(f => f.BitOffset).ThenBy(f => f.BitLength).ToArray();
        var reported = 0;
        for (var i = 0; i < ordered.Length && reported < 20; i++)
        {
            var end = (long)ordered[i].BitOffset + ordered[i].BitLength;
            // Only the first overlapping neighbour is reported per field.
            var j = i + 1;
            if (j < ordered.Length && ordered[j].BitOffset < end)
            {
                add(FieldOverlap, ecu, subject,
                    $"'{ordered[i].Name}' (bits {Fmt(ordered[i].BitOffset)}..{end - 1}) overlaps '{ordered[j].Name}' (bits {Fmt(ordered[j].BitOffset)}..{(long)ordered[j].BitOffset + ordered[j].BitLength - 1}).", null);
                reported++;
            }
        }
    }

    private static void CheckConnectionsAcrossCatalogs(IReadOnlyList<DidCatalog> catalogs, List<CatalogConsistencyFinding> findings)
    {
        var all = catalogs.SelectMany(c => c.ConnectionDefinitions.Select(conn => (Catalog: c, Connection: conn)))
            .Where(item => item.Connection.RequestIdentifier is not null)
            .GroupBy(item => (item.Connection.RequestIdentifier, item.Connection.IsExtendedIdentifier));
        foreach (var group in all)
        {
            var ecus = group.Select(item => item.Connection.Ecu).Where(e => !string.IsNullOrWhiteSpace(e))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (ecus.Length < 2) continue;
            findings.Add(new CatalogConsistencyFinding(DuplicateConnectionId, DuplicateConnectionId.DefaultSeverity,
                string.Join(" / ", ecus), $"Request id 0x{group.Key.RequestIdentifier:X}",
                $"Shared by {string.Join(", ", ecus)}. Functional addressing legitimately shares an identifier; physical addressing must not.",
                group.First().Catalog.SourcePath));
        }
    }
}
