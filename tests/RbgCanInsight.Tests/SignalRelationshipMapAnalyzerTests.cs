using RbgCanInsight.Analysis;
using RbgCanInsight.Dbc;
using RbgCanInsight.Domain;
using Xunit;

namespace RbgCanInsight.Tests;

public sealed class SignalRelationshipMapAnalyzerTests
{
    private static (IReadOnlyList<SignalTimeline> Series, Func<int, int> SourceAt) Fixture()
    {
        var dbc = new DbcParser().Parse("relationship-map", new StringReader(
            """
            BO_ 256 Engine: 8 ECM
             SG_ EngineSpeed : 0|8@1+ (1,0) [0|255] "rpm" ECM
            BO_ 257 Demand: 8 ECM
             SG_ DriverRequest : 0|8@1+ (1,0) [0|255] "%" ECM
            BO_ 258 Load: 8 ECM
             SG_ IntakePressure : 0|8@1+ (1,0) [0|255] "kPa" ECM
            BO_ 259 Auxiliary: 8 ECM
             SG_ SteadyState : 0|8@1+ (1,0) [0|255] "" ECM
            """));
        var frames = new List<CanFrame>();
        var sourceIndices = new List<int>();
        void Put(uint id, byte value, long t, int logger = 0)
        {
            frames.Add(new CanFrame(0, id,
                [value, 0, 0, 0, 0, 0, 0, 0], t));
            sourceIndices.Add(logger);
        }
        for (var i = 0; i < 36; i++)
        {
            var t = 1_000_000_000L + i * 500_000_000L;
            var x = (byte)(20 + (17 * i + 7 * i * i) % 95);
            Put(256, x, t);
            Put(257, (byte)(x + 1), t + 10_000_000L);
            Put(258, (byte)(235 - x), t + 20_000_000L);
            Put(259, 44, t + 25_000_000L);
            // Identical wire identifier in a distinct, unaligned logger:
            // it must never be borrowed into the first source's graph.
            Put(256, (byte)(200 - x), t + 15_000_000L, 1);
        }

        int At(int frame) => frame >= 0 && frame < sourceIndices.Count
            ? sourceIndices[frame] : -1;
        var series = SignalTimelineBuilder.Build(frames, [dbc], sourceAt: At,
            sourcePaths: ["source-1.asc", "source-2.asc"]);
        return (series, At);
    }

    [Fact]
    public void BuildsOnlyObservedPairwiseLinksAndOneConnectedGroupFromRecordedSources()
    {
        var (curves, sourceAt) = Fixture();
        var report = SignalRelationshipMapAnalyzer.Analyze(curves, sourceAt, 0);
        Assert.Equal(3, report.EligibleSignals);
        Assert.Equal(3, report.ExaminedSignals);
        Assert.Equal(3, report.TotalPairsConsidered);
        Assert.False(report.SignalSelectionTruncated);
        var group = Assert.Single(report.Groups);
        Assert.Equal(3, group.Members.Count);
        Assert.Equal(3, report.Edges.Count);
        Assert.All(report.Edges, edge =>
        {
            Assert.Equal(0, edge.A.OriginalSourceIndex);
            Assert.Equal(0, edge.B.OriginalSourceIndex);
            Assert.Equal(0, sourceAt(edge.Evidence.ReferenceFrameIndex));
            Assert.Equal(0, sourceAt(edge.Evidence.CandidateFrameIndex));
            Assert.True(edge.Evidence.PairedSamples >= 12);
            Assert.True(Math.Abs(edge.Evidence.LevelPearson) >= 0.72);
        });
        Assert.DoesNotContain(group.Members, item => item.Signal.Name == "SteadyState");
        Assert.Contains("NOT causal", report.Summary);
    }

    [Fact]
    public void NeverMergesUnalignedPhysicalLoggerOrInfersCorrelationFromSimilarIds()
    {
        var (curves, sourceAt) = Fixture();
        var report = SignalRelationshipMapAnalyzer.Analyze(curves, sourceAt, 1);
        Assert.Empty(report.Edges);
        Assert.Empty(report.Groups);
        Assert.Single(curves.Where(item => item.OriginalSourceIndex == 1));

        var missingSource = SignalRelationshipMapAnalyzer.Analyze(curves, _ => -1, 0);
        Assert.Empty(missingSource.Edges);
        Assert.Empty(missingSource.Groups);
    }

    [Fact]
    public void TruncationAndAmbiguousDuplicateKeysAreExplicitlyExcluded()
    {
        var (curves, sourceAt) = Fixture();
        var capped = SignalRelationshipMapAnalyzer.Analyze(curves, sourceAt, 0,
            maxSignals: 2);
        Assert.True(capped.SignalSelectionTruncated);
        Assert.Equal(2, capped.ExaminedSignals);
        Assert.Equal(1, capped.TotalPairsConsidered);
        Assert.Single(capped.Edges);

        // An actively chosen but lower-ranked observed signal must not
        // vanish just because the automatic discovery cap is small.
        var alreadyShown = capped.Groups.SelectMany(group => group.Members)
            .Select(signal => signal.Key).ToHashSet(StringComparer.Ordinal);
        var userSelected = Assert.Single(curves.Where(signal =>
            signal.OriginalSourceIndex == 0 &&
            signal.Signal.Name != "SteadyState" &&
            !alreadyShown.Contains(signal.Key)));
        var focused = SignalRelationshipMapAnalyzer.Analyze(curves, sourceAt, 0,
            maxSignals: 2, focusSignalKeys:
                new HashSet<string>(StringComparer.Ordinal) { userSelected.Key });
        Assert.Contains(focused.Groups.SelectMany(group => group.Members),
            signal => ReferenceEquals(signal, userSelected));
        Assert.Equal(2, focused.ExaminedSignals);
        Assert.Equal(1, focused.PreservedOperatorSignals);
        Assert.Contains("1 selected/plotted signal(s) prioritized", focused.Summary);
        Assert.True(focused.SignalSelectionTruncated);

        var limitedLinks = SignalRelationshipMapAnalyzer.Analyze(curves, sourceAt, 0,
            maxEdges: 1);
        Assert.True(limitedLinks.EdgeSelectionTruncated);
        Assert.Equal(3, limitedLinks.TotalEdgesDetected);
        Assert.Single(limitedLinks.Edges);
        Assert.Contains("omitted from display", limitedLinks.Summary);

        var speed = Assert.Single(curves.Where(item =>
            item.Signal.Name == "EngineSpeed" && item.OriginalSourceIndex == 0));
        var duplicate = SignalRelationshipMapAnalyzer.Analyze(
            [..curves, speed], sourceAt, 0);
        Assert.DoesNotContain(duplicate.Edges, edge =>
            ReferenceEquals(edge.A, speed) || ReferenceEquals(edge.B, speed));
        Assert.Equal(2, duplicate.EligibleSignals);
    }

    [Fact]
    public void ConnectedGroupDoesNotInventMissingDirectLinkBetweenItsMembers()
    {
        var db = new DbcParser().Parse("chain", new StringReader(
            """
            BO_ 512 A: 8 ECU
             SG_ SinOne : 0|8@1+ (1,0) [0|255] "" ECU
            BO_ 513 B: 8 ECU
             SG_ SharedBlend : 0|8@1+ (1,0) [0|255] "" ECU
            BO_ 514 C: 8 ECU
             SG_ CosTwo : 0|8@1+ (1,0) [0|255] "" ECU
            """));
        var frames = new List<CanFrame>();
        for (var index = 0; index < 120; index++)
        {
            var phase = 2d * Math.PI * index / 60d;
            var x = (byte)Math.Round(120 + 50 * Math.Sin(phase));
            var z = (byte)Math.Round(120 + 50 *
                (0.2 * Math.Sin(phase) + Math.Sqrt(0.96) * Math.Cos(phase)));
            var y = (byte)Math.Round((x + z) / 2d);
            var time = 1_000_000_000L + index * 500_000_000L;
            frames.Add(new CanFrame(0, 512, [x, 0, 0, 0, 0, 0, 0, 0], time));
            frames.Add(new CanFrame(0, 513, [y, 0, 0, 0, 0, 0, 0, 0], time + 10_000_000L));
            frames.Add(new CanFrame(0, 514, [z, 0, 0, 0, 0, 0, 0, 0], time + 20_000_000L));
        }
        var curves = SignalTimelineBuilder.Build(frames, [db],
            sourceAt: _ => 0, sourcePaths: ["one.asc"]);
        var report = SignalRelationshipMapAnalyzer.Analyze(curves, _ => 0, 0);
        var group = Assert.Single(report.Groups);
        Assert.Equal(3, group.Members.Count);
        Assert.Equal(2, group.Edges.Count);
        Assert.DoesNotContain(report.Edges, edge =>
            (edge.A.Signal.Name == "SinOne" && edge.B.Signal.Name == "CosTwo") ||
            (edge.B.Signal.Name == "SinOne" && edge.A.Signal.Name == "CosTwo"));
        Assert.Contains("not all pairs", group.Warning);
        Assert.NotNull(group.SuggestedStartingSignal);
    }

    [Fact]
    public void EmptySignalsInvalidSourceAndCancelledScanAreSafe()
    {
        var (curves, sourceAt) = Fixture();
        var empty = SignalRelationshipMapAnalyzer.Analyze([], sourceAt, 0);
        Assert.Empty(empty.Groups);
        Assert.Equal(0, empty.TotalPairsConsidered);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SignalRelationshipMapAnalyzer.Analyze(curves, sourceAt, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SignalRelationshipMapAnalyzer.Analyze(curves, sourceAt, 0, maxSignals: 1));

        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            SignalRelationshipMapAnalyzer.Analyze(curves, sourceAt, 0,
                cancellationToken: cancel.Token));
    }
}
