using RbgCanInsight.DiagData;
using Xunit;

namespace RbgCanInsight.Tests;

public sealed class DiagnosticDiffNavigationResolverTests
{
    private static string SourcePath(string name) =>
        Path.Combine(Path.GetTempPath(), "RbgDiffNav", name);

    private static DidParameter Did(string file, string ecu = "ECM",
        int code = 0xF405) => new(code, "Temperature", "Temperature",
            "Synth", "degC", -40, 250, 1, -40, "A_UINT8", 8, ecu, file,
            [new DiagnosticFieldDefinition("Temp", "Temp", "", 0, 8, "A_UINT8",
                "degC", -40, 215, 1, -40, false, "Intel")],
            [new DiagnosticEngineeringParameter("Mode", "Mode", "", "Data[1]/Mode[1]",
                "Data[1]", 1, "PARAM", "ID01", "DOP_MODE", "A_UINT8",
                "", 8)]);

    private static DidCatalog Catalog(string file, params DidParameter[] dids) =>
        new("Synthetic", "ECM", file, dids);

    private static DiagnosticDefinitionDiffRow Diff(string scope, string path = "",
        string ecu = "ECM", string id = "0xF405") =>
        new(scope, ecu, id, path, DiagnosticDefinitionChange.Changed,
            DiagnosticDefinitionImpact.Structure, "changed", "Before", "After",
            "before definition", "after definition", "old.odx", "new.odx", true);

    [Fact]
    public void UniqueLoadedSourceAndDidFindsExactNestedPar()
    {
        var file = SourcePath("new.odx");
        var catalog = Catalog(file, Did(file));
        var result = DiagnosticDiffNavigationResolver.Resolve(
            Diff("Engineering PAR", "Data[1]/Mode[1]"), [catalog], file);
        Assert.True(result.CanNavigate);
        Assert.Equal("DID", result.Target!.RootKind);
        Assert.Equal("0xF405", result.Target.Identifier);
        Assert.Equal("Data[1]/Mode[1]", result.Target.ChildPath);
        Assert.Equal(file, result.Target.SourceFile);
    }

    [Fact]
    public void SameEcuAndDidFromAnUnrelatedSourceMustNotBeSelected()
    {
        var wrong = SourcePath("unrelated.odx");
        var result = DiagnosticDiffNavigationResolver.Resolve(
            Diff("DID"), [Catalog(wrong, Did(wrong))], SourcePath("new.odx"));
        Assert.False(result.CanNavigate);
        Assert.Contains("not loaded", result.Explanation);
    }

    [Fact]
    public void CollisionOfRepeatedDidAndEcuIsRejectedNotFirstMatched()
    {
        var file = SourcePath("multi-variant.odx");
        var result = DiagnosticDiffNavigationResolver.Resolve(
            Diff("DID"), [Catalog(file, Did(file), Did(file))], file);
        Assert.False(result.CanNavigate);
        Assert.Contains("Several", result.Explanation);
    }

    [Fact]
    public void SameDidOnAnotherEcuCannotSatisfyNavigation()
    {
        var file = SourcePath("other-ecu.odx");
        var result = DiagnosticDiffNavigationResolver.Resolve(
            Diff("DID"), [Catalog(file, Did(file, "ABS"))], file);
        Assert.False(result.CanNavigate);
        Assert.Contains("No matching ECU", result.Explanation);
    }

    [Fact]
    public void MissingChildDoesNotSelectUnrelatedEngineeringPar()
    {
        var file = SourcePath("missing-node.odx");
        var result = DiagnosticDiffNavigationResolver.Resolve(
            Diff("Engineering PAR", "Different[1]"), [Catalog(file, Did(file))], file);
        Assert.False(result.CanNavigate);
        Assert.Contains("path is absent", result.Explanation);
    }

    [Fact]
    public void SelectExactDtcSnapshotAndRejectExtendedDataForSameName()
    {
        var file = SourcePath("dtc.odx");
        var catalog = new DidCatalog("Synthetic", "ECM", file, [],
            [new DiagnosticTroubleCode(0xAB1234, "Sensor", "Sensor", "", "ECM", file,
                [new DiagnosticFieldDefinition("SnapshotState", "", "", 0, 8,
                    "A_UINT8", "", null, null, null, null, false, "Intel")])]);
        var good = DiagnosticDiffNavigationResolver.Resolve(
            Diff("DTC Snapshot", "SnapshotState", id: "0xAB1234"), [catalog], file);
        Assert.True(good.CanNavigate);
        Assert.Equal("DTC", good.Target!.RootKind);
        var wrong = DiagnosticDiffNavigationResolver.Resolve(
            Diff("DTC Extended", "SnapshotState", id: "0xAB1234"), [catalog], file);
        Assert.False(wrong.CanNavigate);
    }

    [Fact]
    public void UnsupportedAndMalformedRowTypesNeverNavigate()
    {
        var file = SourcePath("unsupported.odx");
        var catalog = Catalog(file, Did(file));
        Assert.False(DiagnosticDiffNavigationResolver.Resolve(
            Diff("Variant"), [catalog], file).CanNavigate);
        Assert.False(DiagnosticDiffNavigationResolver.Resolve(
            Diff("DID", id: "F405"), [catalog], file).CanNavigate);
    }
}
