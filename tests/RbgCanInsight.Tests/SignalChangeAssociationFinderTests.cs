using RbgCanInsight.Analysis;
using RbgCanInsight.Dbc;
using RbgCanInsight.Domain;
using Xunit;

namespace RbgCanInsight.Tests;

public sealed class SignalChangeAssociationFinderTests
{
    private static (IReadOnlyList<SignalTimeline> Curves, SignalTimeline Reference,
        Func<int, int> SourceAt) Fixture()
    {
        var db = new DbcParser().Parse("sequence", new StringReader(
            """
            BO_ 256 TargetData: 8 ECU
             SG_ Temperature : 0|8@1+ (1,0) [0|255] "degC" ECU
            BO_ 257 CommandData: 8 ECU
             SG_ CoolantCommand : 0|8@1+ (1,0) [0|1] "" ECU
            BO_ 258 UnrelatedData: 8 ECU
             SG_ Unrelated : 0|8@1+ (1,0) [0|1] "" ECU
            """));
        var frames = new List<CanFrame>();
        var originalSources = new List<int>();
        void Append(uint id, byte data, long timestamp, int source = 0)
        {
            frames.Add(new CanFrame(0, id, [data, 0, 0, 0, 0, 0, 0, 0], timestamp));
            originalSources.Add(source);
        }

        Append(256, 30, 0);
        Append(257, 0, 0);
        Append(258, 0, 0);
        for (var i = 1; i <= 12; i++)
        {
            var time = i * 5_000_000_000L;
            Append(257, (byte)(i & 1), time - 200_000_000L); // distinct source-backed lead
            Append(257, (byte)((i + 1) & 1), time - 180_000_000L, 1); // foreign logger
            Append(256, (byte)(20 + (i * 13 + i * i * 7) % 110), time);
            Append(258, (byte)(i & 1), time + 2_500_000_000L); // unrelated timing
        }
        int At(int index) => index >= 0 && index < originalSources.Count ? originalSources[index] : -1;
        var curves = SignalTimelineBuilder.Build(frames, [db], sourceAt: At,
            sourcePaths: ["main.asc", "independent.asc"]);
        var focus = Assert.Single(curves.Where(item => item.Signal.Name == "Temperature" &&
            item.OriginalSourceIndex == 0));
        return (curves, focus, At);
    }

    [Fact]
    public void SparseDigitallyGatedChangeSequenceCanLeadAnalogueReferenceWithoutCausalityClaim()
    {
        var (curves, reference, at) = Fixture();
        var command = Assert.Single(curves.Where(item => item.Signal.Name == "CoolantCommand" &&
            item.OriginalSourceIndex == 0));
        var found = Assert.IsType<SignalChangeAssociation>(
            SignalChangeAssociationFinder.Find(reference, command, at,
                0, 70_000_000_000L));
        Assert.Equal(12, found.ReferenceTransitions);
        Assert.Equal(12, found.MatchedTransitions);
        Assert.Equal(200_000_000L, found.MedianCandidateLeadNanoseconds);
        Assert.InRange(found.ExpectedRandomOverlapFraction, 0.05, 0.3);
        Assert.Contains("NOT physical influence or cause", found.Description);
        Assert.Equal(0, at(found.ReferenceWitness.FrameIndex));
        Assert.Equal(0, at(found.CandidateWitness.FrameIndex));
    }

    [Fact]
    public void RejectsForeignLoggerAndCoincidencesAfterTheReferenceChange()
    {
        var (curves, reference, at) = Fixture();
        var foreign = Assert.Single(curves.Where(item => item.Signal.Name == "CoolantCommand" &&
            item.OriginalSourceIndex == 1));
        var unrelated = Assert.Single(curves.Where(item => item.Signal.Name == "Unrelated" &&
            item.OriginalSourceIndex == 0));
        Assert.Null(SignalChangeAssociationFinder.Find(reference, foreign, at,
            0, 70_000_000_000L));
        Assert.Null(SignalChangeAssociationFinder.Find(reference, unrelated, at,
            0, 70_000_000_000L));
    }

    [Fact]
    public void RefusesSourceIndicesNotMatchingActualOriginalFramesAndCancellation()
    {
        var (curves, reference, _) = Fixture();
        var command = Assert.Single(curves.Where(item => item.Signal.Name == "CoolantCommand" &&
            item.OriginalSourceIndex == 0));
        Assert.Null(SignalChangeAssociationFinder.Find(reference, command, _ => -1,
            0, 70_000_000_000L));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            SignalChangeAssociationFinder.Find(reference, command, _ => 0,
                0, 70_000_000_000L, token: cts.Token));
    }

    [Fact]
    public void GeneralScanIncludesChangeSequenceClueWithoutInventingValueCorrelation()
    {
        var (curves, reference, at) = Fixture();
        var report = SignalRelationshipAnalyzer.Analyze(reference, curves, at,
            0, 70_000_000_000L, toleranceNanoseconds: 25_000_000L,
            maxLagNanoseconds: 100_000_000L);
        var change = Assert.Single(report.Results.Where(item =>
            item.Candidate.Signal.Name == "CoolantCommand"));
        Assert.True(change.IsChangeSequenceOnly);
        Assert.Equal("not evaluated", change.LevelText);
        Assert.Contains("NOT physical influence or cause", change.EvidenceText);
    }
}
