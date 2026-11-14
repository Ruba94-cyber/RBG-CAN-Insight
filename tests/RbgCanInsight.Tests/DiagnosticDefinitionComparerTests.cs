using RbgCanInsight.DiagData;
using Xunit;

namespace RbgCanInsight.Tests;

public sealed class DiagnosticDefinitionComparerTests
{
    private static DidParameter Did(
        int id = 0xF190, string ecu = "ECM", string name = "VehicleSpeed",
        IReadOnlyList<DiagnosticFieldDefinition>? fields = null,
        IReadOnlyList<DiagnosticEngineeringParameter>? pars = null) =>
        new(id, name, name, "Synthetic DID", "km/h", 0, 250, 0.01, 0,
            "A_UINT16", 16, ecu, "fixture.odx", fields, pars);

    private static DiagnosticFieldDefinition Field(string name = "Speed",
        int bitOffset = 0, double? factor = 0.01) =>
        new(name, name, "Recorded field", bitOffset, 16, "A_UINT16",
            "km/h", 0, 250, factor, 0, false, "LITTLE");

    private static DiagnosticEngineeringParameter Par(string path = "Speed[1]",
        string type = "DOP_SPEED", string status = "") =>
        new("Speed", "Speed", "Engineering parameter", path, "", 0, "PARAM",
            "CDDTYPE_1", type, "A_UINT16", "km/h", 16, status,
            "response", "PAR_42");

    private static DidCatalog Catalog(
        IReadOnlyList<DidParameter>? dids = null,
        IReadOnlyList<DiagnosticTroubleCode>? dtcs = null,
        IReadOnlyList<DiagnosticRoutine>? routines = null,
        IReadOnlyList<DiagnosticServiceDefinition>? services = null,
        IReadOnlyList<DiagnosticVariant>? variants = null,
        IReadOnlyList<DiagnosticConnection>? transport = null,
        string ecu = "ECM") =>
        new("Synthetic", ecu, "fixture.odx", dids ?? [], dtcs, routines,
            services, transport, variants);

    private static DiagnosticDefinitionDiffRow Changed(
        DiagnosticDefinitionDiffReport report, string scope) =>
        Assert.Single(report.Rows.Where(row => row.Scope == scope &&
            row.Change == DiagnosticDefinitionChange.Changed));

    [Fact]
    public void SameParsedDidsWithDifferentInputFilesAreUnchanged()
    {
        var before = Catalog([Did(fields: [Field()], pars: [Par()])]);
        var after = before with { SourcePath = "/new/folder/fixture.odx" };
        var report = DiagnosticDefinitionComparer.Compare([before], [after]);
        Assert.Equal(3, report.Unchanged);
        Assert.Equal(0, report.RequiresReview);
    }

    [Fact]
    public void DidFieldOffsetScalingAndEnumsProduceSeparateEncodingDiff()
    {
        var initial = Catalog([Did(fields: [Field()])]);
        var changedField = Field(bitOffset: 8, factor: 0.02) with
        {
            Values = [new DiagnosticEnumValue(5, "Sensor fault")]
        };
        var updated = Catalog([Did(fields: [changedField])]);
        var diff = Changed(DiagnosticDefinitionComparer.Compare([initial], [updated]), "DID Field");
        Assert.Equal(DiagnosticDefinitionImpact.Encoding, diff.Impact);
        Assert.Contains("Bit offset", diff.ChangedFields);
        Assert.Contains("Factor", diff.ChangedFields);
        Assert.Contains("Raw meanings", diff.ChangedFields);
        Assert.Contains("Sensor fault", diff.CurrentDefinition);
    }

    [Fact]
    public void ChangedUnverifiedEngineeringParReferencesRemainStructuralNotPhysicalProof()
    {
        var original = Catalog([Did(pars: [Par()])]);
        var modified = Catalog([Did(pars: [Par(type: "DOP_NEW", status: "unresolved reference")])]);
        var diff = Changed(DiagnosticDefinitionComparer.Compare([original], [modified]), "Engineering PAR");
        Assert.Equal(DiagnosticDefinitionImpact.Structure, diff.Impact);
        Assert.Contains("Type reference", diff.ChangedFields);
        Assert.Contains("Reference status", diff.ChangedFields);
        Assert.True(diff.RequiresReview);
        var did = Changed(DiagnosticDefinitionComparer.Compare([original], [modified]), "DID");
        Assert.Equal(DiagnosticDefinitionImpact.Structure, did.Impact);
        Assert.Contains("Nested field", did.ChangedFields);
    }

    [Fact]
    public void ParIdentityChangingAtSameOrdinalPathIsAStructuralRisk()
    {
        var baseline = Catalog([Did(pars: [Par(path: "Child[1]")])]);
        var changed = Catalog([Did(pars: [Par(path: "Child[1]") with
        {
            Name = "OtherParameter", LongName = "Different source node"
        }])]);
        var report = DiagnosticDefinitionComparer.Compare([baseline], [changed]);
        var child = Changed(report, "Engineering PAR");
        Assert.Equal(DiagnosticDefinitionImpact.Structure, child.Impact);
        Assert.Contains("Name", child.ChangedFields);
        Assert.Equal(DiagnosticDefinitionImpact.Structure, Changed(report, "DID").Impact);
    }

    [Fact]
    public void SourceOrderDoesNotCreateFalseDtcOrRoutineChanges()
    {
        var baseline = Catalog(
            dtcs: [new DiagnosticTroubleCode(10, "DTC10", "DTC10", "", "ECM", "fixture.odx"),
                   new DiagnosticTroubleCode(20, "DTC20", "DTC20", "", "ECM", "fixture.odx")],
            routines: [new DiagnosticRoutine(3, "RoutineA", "A", "", "ECM", "fixture.odx"),
                       new DiagnosticRoutine(4, "RoutineB", "B", "", "ECM", "fixture.odx")]);
        var current = baseline with
        {
            TroubleCodes = baseline.DtcDefinitions.Reverse().ToArray(),
            Routines = baseline.RoutineDefinitions.Reverse().ToArray()
        };
        var report = DiagnosticDefinitionComparer.Compare([baseline], [current]);
        Assert.Equal(4, report.Unchanged);
    }

    [Fact]
    public void RoutineSecuritySessionAndSnapshotDefinitionsAreCompared()
    {
        var before = Catalog(
            routines: [new DiagnosticRoutine(0x1234, "Reset", "Reset", "", "ECM", "fixture.odx",
                [Field()], null, "extended", "level-1")],
            dtcs: [new DiagnosticTroubleCode(0x123456, "DTC", "DTC", "", "ECM", "fixture.odx",
                [Field()], [Field("Extended")])]);
        var after = Catalog(
            routines: [new DiagnosticRoutine(0x1234, "Reset", "Reset", "", "ECM", "fixture.odx",
                [Field()], null, "programming", "level-2")],
            dtcs: [new DiagnosticTroubleCode(0x123456, "DTC", "DTC", "", "ECM", "fixture.odx",
                [Field(bitOffset: 8)], [Field("Extended")])]);
        var report = DiagnosticDefinitionComparer.Compare([before], [after]);
        Assert.Equal(DiagnosticDefinitionImpact.Access, Changed(report, "Routine").Impact);
        Assert.Equal(DiagnosticDefinitionImpact.Encoding, Changed(report, "DTC Snapshot").Impact);
        var dtc = Changed(report, "DTC");
        Assert.Equal(DiagnosticDefinitionImpact.Structure, dtc.Impact);
        Assert.Contains("Nested", dtc.ChangedFields);
    }

    [Fact]
    public void DuplicateDidVariantsNeverCrossPollinateTheirChildParDefinitions()
    {
        var a = Did(pars: [Par(path: "Param[1]", type: "TYPE_A")]);
        var b = Did(pars: [Par(path: "Other[1]", type: "TYPE_B")]);
        var before = Catalog([a, b]);
        var current = Catalog([a]);
        var report = DiagnosticDefinitionComparer.Compare([before], [current]);
        var collision = Assert.Single(report.Rows);
        Assert.Equal("DID", collision.Scope);
        Assert.Equal(DiagnosticDefinitionChange.Ambiguous, collision.Change);
        Assert.True(collision.RequiresReview);
    }

    [Fact]
    public void PdxEmbeddedMemberProvenanceIsVisibleButNotTreatedAsSemanticChange()
    {
        var baseline = Catalog([Did(fields: [Field()]) with
        {
            SourceFile = "/sources/ecu_a.odx"
        }]);
        var current = Catalog([Did(fields: [Field()]) with
        {
            SourceFile = "/renamed/package/ecu_b.odx"
        }]);
        var report = DiagnosticDefinitionComparer.Compare([baseline], [current]);
        Assert.Equal(2, report.Unchanged);
        var did = Assert.Single(report.Rows.Where(row => row.Scope == "DID"));
        Assert.Equal("ecu_a.odx", did.BaselineSource);
        Assert.Equal("ecu_b.odx", did.CurrentSource);
    }

    [Fact]
    public void SameDidNumberAcrossDifferentEcusNeverMatches()
    {
        var baseCatalog = Catalog([Did(ecu: "ECM")], ecu: "ECM");
        var now = Catalog([Did(ecu: "BCM")], ecu: "BCM");
        var report = DiagnosticDefinitionComparer.Compare([baseCatalog], [now]);
        Assert.Equal(1, report.Added);
        Assert.Equal(1, report.Removed);
    }

    [Fact]
    public void AddedAndRemovedDidsRetainCompleteNestedParRecords()
    {
        var before = Catalog([Did(0xF190, pars: [Par()])]);
        var after = Catalog([Did(0xF405, pars: [Par()])]);
        var report = DiagnosticDefinitionComparer.Compare([before], [after]);
        Assert.Equal(2, report.Added);
        Assert.Equal(2, report.Removed);
        Assert.Contains(report.Rows, row => row.Scope == "Engineering PAR" &&
            row.Change == DiagnosticDefinitionChange.Added &&
            row.Identifier == "0xF405");
    }

    [Fact]
    public void ServicesVariantsAndTransportPreserveExplicitProfiles()
    {
        var before = Catalog(
            services: [new DiagnosticServiceDefinition(0x22, null, 0xF190,
                "ReadSpeed", "Read speed", "", "ECM", "fixture.odx")],
            variants: [new DiagnosticVariant("VariantA", "ECU-VARIANT", "")],
            transport: [new DiagnosticConnection("CAN0", 0x7E0, 0x7E8, false,
                "normal", "ECM", "fixture.odx")]);
        var after = Catalog(
            services: [new DiagnosticServiceDefinition(0x22, null, 0xF190,
                "ReadSpeed", "Read speed", "diagnostic service changed", "ECM", "fixture.odx")],
            variants: [new DiagnosticVariant("VariantA", "BASE-VARIANT", "")],
            transport: [new DiagnosticConnection("CAN0", 0x7E1, 0x7E9, false,
                "normal", "ECM", "fixture.odx")]);
        var report = DiagnosticDefinitionComparer.Compare([before], [after]);
        Assert.Equal(DiagnosticDefinitionImpact.Transport, Changed(report, "Transport").Impact);
        Assert.Equal(DiagnosticDefinitionImpact.Structure, Changed(report, "Variant").Impact);
        Assert.Equal(DiagnosticDefinitionImpact.Documentation, Changed(report, "Service").Impact);
    }
}
