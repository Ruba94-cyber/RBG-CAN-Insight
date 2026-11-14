using RbgCanInsight.DiagData;
using Xunit;

namespace RbgCanInsight.Tests;

public sealed class CatalogConsistencyCheckerTests
{
    private static DiagnosticFieldDefinition Field(
        string name, int offset, int length, double? factor = 1, double? min = null, double? max = null,
        string type = "A_UINT8", IReadOnlyList<DiagnosticEnumValue>? values = null, bool physical = true) =>
        new(name, name, "", offset, length, type, "", min, max, factor, 0, false, "BIG_ENDIAN", values, physical);

    private static DidParameter Did(int did, string name = "Speed", int? bits = 16, string type = "A_UINT16",
        DiagnosticFieldDefinition[]? fields = null, string ecu = "ECM", string[]? issues = null,
        DiagnosticEngineeringParameter[]? parameters = null) =>
        new(did, name, name, "", "", null, null, 1, 0, type, bits, ecu, "fixture.cdd", fields, parameters, issues);

    private static DidCatalog Catalog(
        DidParameter[]? dids = null, DiagnosticTroubleCode[]? dtcs = null, DiagnosticRoutine[]? routines = null,
        DiagnosticServiceDefinition[]? services = null, DiagnosticConnection[]? connections = null,
        DiagnosticServiceCondition[]? conditions = null, string name = "fixture", string ecu = "ECM") =>
        new(name, ecu, "fixture.cdd", dids ?? [], dtcs, routines, services, connections, null, conditions);

    private static CatalogConsistencyReport Check(params DidCatalog[] catalogs) =>
        CatalogConsistencyChecker.Check(catalogs);

    private static IEnumerable<CatalogConsistencyFinding> Of(CatalogConsistencyReport report, string id) =>
        report.Findings.Where(f => f.RuleId == id);

    [Fact]
    public void RulesHaveUniqueStableIdsAndDocumentation()
    {
        var rules = CatalogConsistencyChecker.Rules;
        Assert.Equal(rules.Count, rules.Select(r => r.Id).Distinct().Count());
        Assert.All(rules, r =>
        {
            Assert.Matches("^CAT-\\d{3}$", r.Id);
            Assert.False(string.IsNullOrWhiteSpace(r.Description));
            Assert.False(string.IsNullOrWhiteSpace(r.Category));
        });
    }

    [Fact]
    public void CleanCatalogProducesNoErrorsOrWarnings()
    {
        var did = Did(0xF190, "VIN", 8, "A_UINT8", [Field("Value", 0, 8)]);
        var dtc = new DiagnosticTroubleCode(0x123456, "P0123", "Throttle low", "", "ECM", "fixture.cdd");
        var report = Check(Catalog([did], [dtc]));
        Assert.Equal(0, report.Count(CatalogSeverity.Error));
        Assert.Equal(0, report.Count(CatalogSeverity.Warning));
        Assert.Equal(CatalogConsistencyChecker.Rules.Count, report.RulesEvaluated.Count);
    }

    [Fact]
    public void DuplicateDidDtcAndRoutineAreReportedPerEcuOnly()
    {
        var report = Check(Catalog(
            [Did(0x1000), Did(0x1000), Did(0x1000, ecu: "BCM")],
            [new DiagnosticTroubleCode(1, "A", "A", "", "ECM", "f"), new DiagnosticTroubleCode(1, "A", "A", "", "ECM", "f")],
            [new DiagnosticRoutine(0x0203, "R", "R", "", "ECM", "f"), new DiagnosticRoutine(0x0203, "R", "R", "", "ECM", "f")]));
        Assert.Single(Of(report, "CAT-001"));
        Assert.Single(Of(report, "CAT-002"));
        Assert.Single(Of(report, "CAT-003"));
        Assert.All(Of(report, "CAT-001"), f => Assert.Equal(CatalogSeverity.Warning, f.Severity));
    }

    [Fact]
    public void OverlappingAndOutOfRangeFieldsAreErrors()
    {
        var did = Did(0x2000, bits: 16, fields:
        [
            Field("A", 0, 8), Field("B", 4, 8), Field("C", 12, 8), Field("D", -1, 4), Field("E", 0, 0)
        ]);
        var report = Check(Catalog([did]));
        Assert.Contains(Of(report, "CAT-005"), f => f.Detail.Contains("'A'") && f.Detail.Contains("'B'"));
        Assert.Contains(Of(report, "CAT-006"), f => f.Subject.EndsWith("/ C", StringComparison.Ordinal));
        Assert.Equal(2, Of(report, "CAT-007").Count());
        Assert.All(Of(report, "CAT-005").Concat(Of(report, "CAT-006")), f => Assert.Equal(CatalogSeverity.Error, f.Severity));
    }

    [Fact]
    public void TouchingFieldsDoNotOverlap()
    {
        var report = Check(Catalog([Did(0x2000, bits: 16, fields: [Field("A", 0, 8), Field("B", 8, 8)])]));
        Assert.Empty(Of(report, "CAT-005"));
        Assert.Empty(Of(report, "CAT-006"));
    }

    [Fact]
    public void ScalingProblemsAreFlagged()
    {
        var did = Did(0x3000, bits: 8, fields:
        [
            Field("Inverted", 0, 4, min: 10, max: 5),
            Field("Zero", 4, 4, factor: 0)
        ]);
        var report = Check(Catalog([did]));
        Assert.Single(Of(report, "CAT-008"));
        Assert.Single(Of(report, "CAT-009"));
    }

    [Fact]
    public void ValueTableDuplicatesAndOversizedValuesAreWarnings()
    {
        var table = new DiagnosticEnumValue[] { new(0, "Off"), new(1, "On"), new(1, "Again"), new(5, "TooBig") };
        var report = Check(Catalog([Did(0x4000, bits: 2, fields: [Field("Mode", 0, 2, values: table)])]));
        var findings = Of(report, "CAT-010").ToArray();
        Assert.Equal(2, findings.Length);
        Assert.Contains(findings, f => f.Detail.Contains("more than once"));
        Assert.Contains(findings, f => f.Detail.Contains("do not fit"));
    }

    [Fact]
    public void DidWithoutTypeAndConflictingDefinitionsAreReported()
    {
        var report = Check(Catalog([
            Did(0x5000, "Raw", bits: null, type: ""),
            Did(0x5001, "Conflicted", issues: ["Services disagree on layout"]),
            Did(0x5002, "", bits: 8)
        ]));
        Assert.Single(Of(report, "CAT-004"));
        Assert.Contains(Of(report, "CAT-011"), f => f.Detail.Contains("disagree"));
        Assert.Single(Of(report, "CAT-026"));
    }

    [Fact]
    public void DtcRulesCoverMissingTextAndRange()
    {
        var report = Check(Catalog(dtcs:
        [
            new DiagnosticTroubleCode(0x000010, "0x000010", "", "", "ECM", "f"),
            new DiagnosticTroubleCode(0x2000000, "Big", "Big", "", "ECM", "f")
        ]));
        Assert.Single(Of(report, "CAT-012"));
        Assert.Single(Of(report, "CAT-013"));
    }

    [Fact]
    public void ServiceRulesCoverGuessedIdsDuplicatesAndMissingConditions()
    {
        var services = new[]
        {
            new DiagnosticServiceDefinition(0x31, 1, 0x0203, "Start", "", "", "ECM", "f", DiagnosticServiceIdSource.NameHeuristic),
            new DiagnosticServiceDefinition(0x2E, null, 0xF190, "Write", "", "", "ECM", "f", DiagnosticServiceIdSource.Structural),
            new DiagnosticServiceDefinition(0x2E, null, 0xF190, "Write", "", "", "ECM", "f", DiagnosticServiceIdSource.Structural),
            new DiagnosticServiceDefinition(0x22, null, 0xF190, "Read", "", "", "ECM", "f", DiagnosticServiceIdSource.Structural)
        };
        var conditions = new[]
        {
            new DiagnosticServiceCondition(0x31, 0x0203, "Start", DiagnosticConditionKind.Session, "Session", "Extended", "ECM", "f"),
            new DiagnosticServiceCondition(0x2F, 0x1111, "Ghost", DiagnosticConditionKind.Security, "Security", "L1", "ECM", "f")
        };
        var report = Check(Catalog(services: services, conditions: conditions));
        Assert.Single(Of(report, "CAT-015"));
        Assert.Single(Of(report, "CAT-016"));
        Assert.Contains(Of(report, "CAT-017"), f => f.Subject.Contains("0x2E"));
        Assert.DoesNotContain(Of(report, "CAT-017"), f => f.Subject.Contains("0x22")); // reads are not judged
        Assert.Contains(Of(report, "CAT-018"), f => f.Subject.Contains("0x2F"));
    }

    [Fact]
    public void ConnectionIdentifierRules()
    {
        var connections = new[]
        {
            new DiagnosticConnection("A", 0x800, 0x7E8, false, "physical", "ECM", "f"),
            new DiagnosticConnection("B", 0x7E0, 0x7E0, false, "physical", "ECM", "f"),
            new DiagnosticConnection("C", 0x7E1, 0x7E9, false, "physical", "BCM", "f")
        };
        var other = Catalog(connections: [new DiagnosticConnection("D", 0x7E1, 0x7EA, false, "physical", "TCM", "g")], name: "other");
        var report = Check(Catalog(connections: connections), other);
        Assert.Equal(2, Of(report, "CAT-019").Count());
        Assert.Single(Of(report, "CAT-020"));
    }

    [Fact]
    public void UnresolvedReferencesAreErrorsAndAmbiguousOnesWarnings()
    {
        DiagnosticEngineeringParameter Par(string status) => new(
            "P", "P", "", "DID/P", "DID", 1, "PARAM", "id", "T", "", "", null, status);
        var report = Check(Catalog([Did(0x6000, parameters:
            [Par("Unresolved type reference"), Par("Ambiguous short-name reference (multiple type matches)"), Par("")])]));
        var findings = Of(report, "CAT-021").ToArray();
        Assert.Equal(2, findings.Length);
        Assert.Contains(findings, f => f.Severity == CatalogSeverity.Error);
        Assert.Contains(findings, f => f.Severity == CatalogSeverity.Warning);
    }

    [Fact]
    public void EmptyCatalogAndFieldCompletenessReviewItems()
    {
        var empty = Check(Catalog(name: "nothing"));
        Assert.Single(Of(empty, "CAT-025"));

        var report = Check(Catalog([Did(0x7000, bits: 8, fields: [Field("NoType", 0, 8, type: ""), Field("Raw", 0, 0)])]));
        Assert.Single(Of(report, "CAT-023"));
        var piecewise = Check(Catalog([Did(0x7001, bits: 8, fields: [Field("Pw", 0, 8, physical: false)])]));
        Assert.Single(Of(piecewise, "CAT-024"));
        Assert.All(Of(piecewise, "CAT-024"), f => Assert.Equal(CatalogSeverity.Review, f.Severity));
    }

    [Fact]
    public void FindingsAreSortedBySeverityAndExportAsCsv()
    {
        var report = Check(Catalog([Did(0x1000), Did(0x1000), Did(0x2000, bits: 8, fields: [Field("Z", 0, 8, factor: 0)])]));
        Assert.Equal(CatalogSeverity.Error, report.Findings[0].Severity);
        Assert.True(report.Findings.Zip(report.Findings.Skip(1)).All(p => p.First.Severity <= p.Second.Severity));
        var csv = report.ToCsv();
        Assert.StartsWith("Rule,Title,Severity,Category,ECU,Subject,Detail,Source file", csv);
        Assert.Contains("CAT-009,Zero scaling factor,Error", csv);
        Assert.Contains("fixture.cdd", csv);
        var summary = report.ToSummaryText();
        Assert.StartsWith("Catalog consistency check of 1 catalog(s): 1 error(s), 1 warning(s)", summary);
    }

    [Fact]
    public void FindingCapIsReported()
    {
        var dids = Enumerable.Range(0, 30).Select(i => Did(0x1000 + i, bits: null, type: "")).ToArray();
        var report = CatalogConsistencyChecker.Check([Catalog(dids)], new CatalogConsistencyOptions { MaxFindings = 5 });
        Assert.Equal(5, report.Findings.Count);
        Assert.True(report.Truncated);
    }
}
