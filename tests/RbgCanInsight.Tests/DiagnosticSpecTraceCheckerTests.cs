using RbgCanInsight.DiagData;
using RbgCanInsight.Diagnostics;
using RbgCanInsight.Domain;
using Xunit;

namespace RbgCanInsight.Tests;

public sealed class DiagnosticSpecTraceCheckerTests
{
    private static CanFrame Frame(uint id, byte[] data, long ms) =>
        new(0, id, data.Concat(new byte[8 - data.Length]).ToArray(), ms * 1_000_000);

    private static DidParameter Did(int did) =>
        new(did, "Did" + did.ToString("X"), "", "", "", null, null, null, null, "", null, "ECU", "fixture");

    [Fact]
    public void ReportsUndeclaredAndUnobservedIdentifiersWithSourceFrames()
    {
        var frames = new[]
        {
            Frame(0x7E0, [0x03, 0x22, 0xF4, 0x05], 0),
            Frame(0x7E8, [0x04, 0x62, 0xF4, 0x05, 0x32], 10),
            Frame(0x7E0, [0x03, 0x22, 0xF1, 0x90], 100),
            Frame(0x7E8, [0x04, 0x62, 0xF1, 0x90, 0x5A], 110),
            Frame(0x7E0, [0x04, 0x31, 0x01, 0x02, 0x03], 200),
            Frame(0x7E8, [0x03, 0x7F, 0x31, 0x22], 210)
        };
        var catalog = new DidCatalog("c", "ECU", "c.odx", [Did(0xF405), Did(0xF999)],
            Routines: [new DiagnosticRoutine(0x0777, "Declared", "", "", "ECU", "c.odx")]);
        var transactions = UdsTransactionAnalyzer.Analyze(frames, [catalog]);

        var report = DiagnosticSpecTraceChecker.Check(transactions, [catalog]);

        var undeclared = Assert.Single(report.Findings, f => f.Kind == SpecTraceFindingKind.ObservedDidNotDeclared);
        Assert.Equal("0xF190", undeclared.Identifier);
        Assert.Equal([2, 3], undeclared.FrameIndices);
        Assert.Contains(report.Findings, f => f.Kind == SpecTraceFindingKind.DeclaredDidNotObserved &&
            f.Identifier == "0xF999" && f.FrameIndices.Count == 0);
        Assert.DoesNotContain(report.Findings, f => f.Identifier == "0xF405");
        Assert.Contains(report.Findings, f => f.Kind == SpecTraceFindingKind.ObservedRoutineNotDeclared &&
            f.Identifier == "0x0203");
        Assert.Contains(report.Findings, f => f.Kind == SpecTraceFindingKind.DeclaredRoutineNotObserved &&
            f.Identifier == "0x0777");
        var nrc = Assert.Single(report.Findings, f => f.Kind == SpecTraceFindingKind.NegativeResponse);
        Assert.Equal("SID 0x31 / NRC 0x22", nrc.Identifier);
        Assert.All(report.Findings, f => Assert.False(string.IsNullOrWhiteSpace(f.Caveat)));
        Assert.Contains("not evaluated", report.Limitations);
    }

    [Fact]
    public void EmptyTraceDeclaresEverythingUnobservedWithoutInventingFrames()
    {
        var catalog = new DidCatalog("c", "ECU", "c.odx", [Did(0xF405)]);
        var report = DiagnosticSpecTraceChecker.Check([], [catalog]);
        var finding = Assert.Single(report.Findings);
        Assert.Equal(SpecTraceFindingKind.DeclaredDidNotObserved, finding.Kind);
        Assert.Empty(finding.FrameIndices);
        Assert.Equal(0, report.TransactionsAnalyzed);
    }
}
