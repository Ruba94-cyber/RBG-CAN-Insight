using RbgCanInsight.Analysis;
using RbgCanInsight.Dbc;
using RbgCanInsight.Domain;
using Xunit;

namespace RbgCanInsight.Tests;

public sealed class SignalRelationshipAnalyzerTests
{
    private static (IReadOnlyList<SignalTimeline> Series, SignalTimeline Target,
        Func<int, int> SourceAt) Fixture()
    {
        var db = new DbcParser().Parse("relationship", new StringReader(
            """
            BO_ 256 TargetData: 8 ECU_A
             SG_ EngineSpeed : 0|8@1+ (1,0) [0|255] "rpm" ECU_A
            BO_ 257 LeadData: 8 ECU_B
             SG_ DriverDemand : 0|8@1+ (1,0) [0|255] "%" ECU_B
            BO_ 258 FollowData: 8 ECU_C
             SG_ ExhaustPressure : 0|8@1+ (1,0) [0|255] "bar" ECU_C
            BO_ 259 ConstantData: 8 ECU_D
             SG_ Constant : 0|8@1+ (1,0) [0|255] "" ECU_D
            """));
        var frames = new List<CanFrame>();
        var sources = new List<int>();
        void Append(int source, uint id, byte value, long timestamp)
        {
            frames.Add(new CanFrame(0, id, [value, 0, 0, 0, 0, 0, 0, 0], timestamp));
            sources.Add(source);
        }

        // Synthetic source A: Demand is recorded 100ms BEFORE EngineSpeed,
        // while ExhaustPressure changes 100ms AFTER it. The lag is a
        // historical observation only; this synthetic fixture asserts no ECU causality.
        for (var i = 0; i < 28; i++)
        {
            var t = 1_000_000_000L + i * 1_000_000_000L;
            // Non-monotonic test values avoid interpreting simple time trends as proof.
            var value = (byte)((i * 19 + i * i * 3 + 11) % 160 + 20);
            Append(0, 257, value, t);
            Append(1, 257, (byte)(220 - value), t + 20_000_000L);
            Append(0, 256, value, t + 100_000_000L);
            Append(0, 258, (byte)(230 - value), t + 200_000_000L);
            Append(0, 259, 41, t + 210_000_000L);
        }

        int SourceAt(int frameIndex) => frameIndex >= 0 && frameIndex < sources.Count
            ? sources[frameIndex] : -1;
        var timelines = SignalTimelineBuilder.Build(frames, [db], sourceAt: SourceAt,
            sourcePaths: ["first.asc", "independent.asc"]);
        var focus = Assert.Single(timelines.Where(item =>
            item.OriginalSourceIndex == 0 && item.Signal.Name == "EngineSpeed"));
        return (timelines, focus, SourceAt);
    }

    [Fact]
    public void FindsIndependentRecordedLeadAndFollowButNotCausality()
    {
        var (curves, target, sourceAt) = Fixture();
        var report = SignalRelationshipAnalyzer.Analyze(target, curves, sourceAt,
            0, 32_000_000_000L, toleranceNanoseconds: 20_000_000L,
            maxLagNanoseconds: 100_000_000L);
        var lead = Assert.Single(report.Results.Where(row => row.SignalText == "LeadData.DriverDemand"));
        Assert.Equal(100_000_000L, lead.CandidateLeadNanoseconds);
        Assert.Equal(1d, lead.LevelPearson, 8);
        Assert.Equal(1d, lead.ChangePearson!.Value, 8);
        Assert.True(lead.PairedSamples >= 24);
        Assert.True(sourceAt(lead.ReferenceFrameIndex) == 0 && sourceAt(lead.CandidateFrameIndex) == 0);
        Assert.Contains("does NOT prove influence", lead.EvidenceText);

        var follow = Assert.Single(report.Results.Where(row => row.SignalText == "FollowData.ExhaustPressure"));
        Assert.Equal(-100_000_000L, follow.CandidateLeadNanoseconds);
        Assert.Equal(-1d, follow.LevelPearson, 8);
        Assert.Equal(-1d, follow.ChangePearson!.Value, 8);
        Assert.DoesNotContain(report.Results, row => row.SignalText.Contains("Constant"));
        Assert.DoesNotContain(report.Results, row => row.Candidate.OriginalSourceIndex == 1);
    }

    [Fact]
    public void TooNarrowToleranceOrUnrelatedWindowReturnsNoSpuriousMatches()
    {
        var (curves, target, sourceAt) = Fixture();
        var noLag = SignalRelationshipAnalyzer.Analyze(target, curves, sourceAt,
            0, 32_000_000_000L, toleranceNanoseconds: 20_000_000L,
            maxLagNanoseconds: 0);
        Assert.Empty(noLag.Results);
        var outside = SignalRelationshipAnalyzer.Analyze(target, curves, sourceAt,
            35_000_000_000L, 40_000_000_000L);
        Assert.Empty(outside.Results);
    }

    [Fact]
    public void AmbiguousOrForeignReferenceAndCancellationFailClosed()
    {
        var (curves, target, sourceAt) = Fixture();
        Assert.Throws<InvalidOperationException>(() =>
            SignalRelationshipAnalyzer.Analyze(target, [..curves, target], sourceAt,
                0, 32_000_000_000L));
        var invalidSource = SignalRelationshipAnalyzer.Analyze(target, curves,
            _ => -1, 0, 32_000_000_000L);
        Assert.Empty(invalidSource.Results);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            SignalRelationshipAnalyzer.Analyze(target, curves, sourceAt,
                0, 32_000_000_000L, cancellationToken: cts.Token));
    }

    [Fact]
    public void ExplicitCandidateBoundReportsTruncationAndPreservesSourceIdentity()
    {
        var (curves, target, sourceAt) = Fixture();
        var report = SignalRelationshipAnalyzer.Analyze(target, curves, sourceAt,
            0, 32_000_000_000L, toleranceNanoseconds: 20_000_000L,
            maxLagNanoseconds: 100_000_000L, maxCandidates: 1);
        Assert.True(report.Truncated);
        Assert.Equal(1, report.CandidateCountScanned);
        Assert.True(report.Results.Count <= 1);
    }
}
