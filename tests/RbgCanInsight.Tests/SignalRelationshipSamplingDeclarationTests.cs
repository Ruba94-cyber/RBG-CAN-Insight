using RbgCanInsight.Analysis;
using RbgCanInsight.Dbc;
using RbgCanInsight.Domain;
using Xunit;

namespace RbgCanInsight.Tests;

/// <summary>No silent data reduction: any subsample of the reference signal or cut-off of candidates is stated
/// in the report summary and in the map CSV; a signal below the sample limit declares nothing.</summary>
public sealed class SignalRelationshipSamplingDeclarationTests
{
    private static (IReadOnlyList<SignalTimeline> Series, SignalTimeline Target, Func<int, int> SourceAt) Fixture()
    {
        var db = new DbcParser().Parse("sampling", new StringReader(
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
        void Append(uint id, byte value, long timestamp) =>
            frames.Add(new CanFrame(0, id, [value, 0, 0, 0, 0, 0, 0, 0], timestamp));
        for (var i = 0; i < 28; i++)
        {
            var t = 1_000_000_000L + i * 1_000_000_000L;
            var value = (byte)((i * 19 + i * i * 3 + 11) % 160 + 20);
            Append(257, value, t);
            Append(256, value, t + 100_000_000L);
            Append(258, (byte)(230 - value), t + 200_000_000L);
            Append(259, 41, t + 210_000_000L);
        }
        int SourceAt(int frameIndex) => frameIndex >= 0 && frameIndex < frames.Count ? 0 : -1;
        var timelines = SignalTimelineBuilder.Build(frames, [db], sourceAt: SourceAt, sourcePaths: ["first.asc"]);
        var focus = Assert.Single(timelines.Where(item => item.Signal.Name == "EngineSpeed"));
        return (timelines, focus, SourceAt);
    }

    [Fact]
    public void SignalBelowTheSampleLimit_DeclaresNoSampling()
    {
        var (curves, target, sourceAt) = Fixture();
        var report = SignalRelationshipAnalyzer.Analyze(target, curves, sourceAt, 0, 32_000_000_000L,
            toleranceNanoseconds: 20_000_000L, maxLagNanoseconds: 100_000_000L);

        Assert.False(report.ReferenceWasSubsampled);
        Assert.Equal(28, report.ReferenceSamplesInWindow);
        Assert.Equal(28, report.ReferenceSamplesAnalyzed);
        Assert.Equal(string.Empty, report.SamplingText);
        Assert.DoesNotContain("Data reduction", report.Summary);
        Assert.All(report.Results.Where(r => !r.IsChangeSequenceOnly), r => Assert.False(r.ReferenceWasSubsampled));
    }

    [Fact]
    public void SubsampledReference_IsDeclaredAsAnalysedNOfM()
    {
        var (curves, target, sourceAt) = Fixture();
        var report = SignalRelationshipAnalyzer.Analyze(target, curves, sourceAt, 0, 32_000_000_000L,
            toleranceNanoseconds: 20_000_000L, maxLagNanoseconds: 100_000_000L, maxReferenceSamples: 16);

        Assert.True(report.ReferenceWasSubsampled);
        Assert.Equal(28, report.ReferenceSamplesInWindow);
        Assert.Equal(16, report.ReferenceSamplesAnalyzed);
        Assert.Contains("analysed 16 of 28 reference samples", report.SamplingText);
        Assert.Contains("Data reduction", report.Summary);
    }

    [Fact]
    public void CandidateLimit_IsDeclaredAsEvaluatedNOfM()
    {
        var (curves, target, sourceAt) = Fixture();
        var report = SignalRelationshipAnalyzer.Analyze(target, curves, sourceAt, 0, 32_000_000_000L,
            toleranceNanoseconds: 20_000_000L, maxLagNanoseconds: 100_000_000L, maxCandidates: 1);

        Assert.True(report.Truncated);
        Assert.Equal(1, report.CandidateCountScanned);
        Assert.Equal(3, report.CandidatesAvailable);
        Assert.Contains("evaluated 1 of 3 candidate signals", report.Summary);
    }

    [Fact]
    public void MapCsv_CarriesTheReferenceSamplingColumns()
    {
        var (curves, target, _) = Fixture();
        var other = curves.First(c => c.Signal.Name == "DriverDemand");
        var observed = new SignalRelationship(target, other, 100_000_000L, 0.83, -0.72, 24, 12, 11, 0, 1,
            1_000_000_000L, 1_100_000_000L)
        { ReferenceSamplesInWindow = 600, ReferenceSamplesAnalyzed = 512 };
        var edge = new SignalMapEdge(observed);
        var report = new SignalRelationshipMapReport(0, 2, 2, 1, 1, false, false,
            [new SignalMapGroup(1, [target, other], [edge])], [edge]);

        var csv = SignalRelationshipMapCsv.Export(report);

        Assert.Contains("reference_samples_in_window,reference_samples_analyzed,reference_subsampled", csv);
        Assert.Contains(",600,512,true", csv);
    }

    [Fact]
    public void MapSummary_StatesSubsamplingOnlyWhenItHappened()
    {
        var none = new SignalRelationshipMapReport(0, 4, 4, 3, 0, false, false, [], [])
        { ReferenceSignalsAnalyzed = 3, ReferenceSignalsSubsampled = 0, MaxReferenceSamples = 512 };
        var some = none with { ReferenceSignalsSubsampled = 2 };

        Assert.DoesNotContain("evenly spaced", none.Summary);
        Assert.Contains("at most 512 evenly spaced samples", some.Summary);
        Assert.Contains("2/3 reference signals were subsampled", some.Summary);
    }
}
