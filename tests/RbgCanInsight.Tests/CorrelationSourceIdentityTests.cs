using RbgCanInsight.Analysis;
using RbgCanInsight.Correlation;
using RbgCanInsight.Dbc;
using RbgCanInsight.DiagData;
using RbgCanInsight.Diagnostics;
using RbgCanInsight.Domain;
using Xunit;

namespace RbgCanInsight.Tests;

/// <summary>Cross-workspace links must never substitute a similarly named DBC
/// signal from another database, bus or 11/29-bit frame identity.</summary>
public sealed class CorrelationSourceIdentityTests
{
    private static DbcDatabase Database(string name) => new DbcParser().Parse(name, new StringReader(
        """
        BO_ 256 EngineData: 8 ECM
         SG_ Temperature : 0|8@1+ (1,0) [0|255] "degC" ECM
        """));

    private static CorrelationCandidate Candidate(DbcDatabase database) => new(
        new DidParameter(0xF405, "Temperature", "Temperature", "Coolant temperature",
            "degC", 0, 255, 1, 0, "A_UINT8", 8, "ECM", "fixture.cdd"),
        database.Messages[256], database.Messages[256].Signals[0],
        database.Name, 0.88, ["matching unit and name"]);

    private static DiagnosticDidSample Did(int index, int channel) =>
        new(0xF405, "Temperature", "Temperature", index + 10, (index + 10).ToString(),
            "degC", index * 1_000_000_000L, index, "ECM", "Recorded", channel);


    [Fact]
    public void DtcSourceExactPlotMatchingRejectsOtherLoggersAndJ1939Aliases()
    {
        var database = Database("engine");
        var frames = new[]
        {
            new CanFrame(0, 256, [10], 0),
            new CanFrame(0, 256, [20], 0)
        };
        var timelines = SignalTimelineBuilder.Build(frames, [database],
            sourceAt: index => index, sourcePaths: ["A.asc", "B.asc"]);
        Assert.Equal(2, timelines.Count);
        var candidate = Candidate(database);
        var result = new UdsCanCorrelationResult(candidate,
            CorrelationVerdict.Candidate, .8, .8, .8, 2, 1, 0,
            1, 1, 1, 0, 0, 0, "recorded",
            [new CorrelationPoint(0, 0, 10, 10, 10, 0, 0, 0)])
        {
            OriginalSourceIndex = 1,
            SignalChannel = 0,
            SignalSourceIdentifier = 256,
            SignalSourceIsExtended = false
        };
        Assert.True(CorrelationSourceIdentity.MatchesObservedTimeline(result,
            Assert.Single(timelines.Where(t => t.OriginalSourceIndex == 1))));
        Assert.False(CorrelationSourceIdentity.MatchesObservedTimeline(result,
            Assert.Single(timelines.Where(t => t.OriginalSourceIndex == 0))));
        Assert.All(timelines, timeline =>
        {
            Assert.False(CorrelationSourceIdentity.MatchesObservedTimeline(
                result with { OriginalSourceIndex = -1 }, timeline));
            Assert.False(CorrelationSourceIdentity.MatchesObservedTimeline(
                result with { SignalSourceIdentifier = 257 }, timeline));
            Assert.False(CorrelationSourceIdentity.MatchesObservedTimeline(
                result with { SignalChannel = 1 }, timeline));
        });
    }

    [Fact]
    public void MatchingCANSignalOnTwoBusesRetainsOnlyTheActualDiagnosticChannel()
    {
        var database = Database("engine");
        var frames = Enumerable.Range(0, 8).SelectMany(index =>
            new[]
            {
                new CanFrame(0, 256, [(byte)(200 - index)], index * 1_000_000_000L),
                new CanFrame(1, 256, [(byte)(index + 10)], index * 1_000_000_000L)
            }).ToArray();
        var observations = Enumerable.Range(0, 8).Select(index => Did(index, 1)).ToArray();
        var result = Assert.Single(UdsCanTimeCorrelation.Correlate(
            observations, [Candidate(database)], SignalTimelineBuilder.Build(frames, [database]),
            new TimeCorrelationOptions(MaxCandidates: 1,
                MaximumLagMilliseconds: 0, MaximumPairDistanceMilliseconds: 1)));

        Assert.Equal(1, result.SignalChannel);
        Assert.Equal(8, result.PairCount);
        Assert.True(result.HasSourcePairs);
        Assert.Contains("CAN 2", result.SignalSourceText);
        Assert.All(result.Points, point => Assert.Equal(1, frames[point.SignalFrameIndex].Channel));
        Assert.True(CorrelationSourceIdentity.MatchesObservedChannel(result, null));
        Assert.True(CorrelationSourceIdentity.MatchesObservedChannel(result, 1));
        Assert.False(CorrelationSourceIdentity.MatchesObservedChannel(result, 0));
    }


    [Fact]
    public void UniqueMappedCanCurveDoesNotBypassMissingDiagnosticSourceIdentity()
    {
        var database = Database("mapped");
        var frames = Enumerable.Range(0, 6).Select(index =>
            new CanFrame(0, 256, [(byte)(index + 10)],
                index * 1_000_000_000L)).ToArray();
        var timelines = SignalTimelineBuilder.Build(frames, [database],
            sourceAt: _ => 1, sourcePaths: ["empty.asc", "measured.asc"]);
        var did = Enumerable.Range(0, 6).Select(index => Did(index, 0)).ToArray();
        var options = new TimeCorrelationOptions(MaxCandidates: 1,
            MaximumLagMilliseconds: 0, MaximumPairDistanceMilliseconds: 1);

        var rejected = Assert.Single(UdsCanTimeCorrelation.Correlate(
            did, [Candidate(database)], timelines, options));
        Assert.Equal(CorrelationVerdict.InsufficientData, rejected.Verdict);
        Assert.False(rejected.HasSourcePairs);
        Assert.Contains("Original logger mapping", rejected.Explanation);

        var accepted = Assert.Single(UdsCanTimeCorrelation.Correlate(
            did, [Candidate(database)], timelines, options,
            sourceAt: _ => 1));
        Assert.Equal(1, accepted.OriginalSourceIndex);
        Assert.True(accepted.HasSourcePairs);
        Assert.All(accepted.Points, point => Assert.True(point.SignalFrameIndex >= 0));
    }

    [Fact]
    public void DidCorrelationSelectsOnlyTheSamePhysicalLoggerAndReportsItsEvidence()
    {
        var database = Database("engine");
        var frames = Enumerable.Range(0, 6).SelectMany(index => new[]
        {
            new CanFrame(0, 256, [(byte)(index + 10)],
                index * 1_000_000_000L),
            new CanFrame(0, 256, [(byte)(100 + index)],
                index * 1_000_000_000L)
        }).ToArray();
        int SourceAt(int frameIndex) => frameIndex % 2;
        var timelines = SignalTimelineBuilder.Build(frames, [database],
            sourceAt: SourceAt, sourcePaths: ["logger-A.asc", "logger-B.asc"]);
        var did = Enumerable.Range(0, 6).Select(index =>
            new DiagnosticDidSample(0xF405, "Temperature", "Temperature",
                index + 10, (index + 10).ToString(), "degC",
                index * 1_000_000_000L, index * 2, "ECM", "Recorded", 0,
                [index * 2])).ToArray();
        var options = new TimeCorrelationOptions(MaxCandidates: 1,
            MaximumLagMilliseconds: 0, MaximumPairDistanceMilliseconds: 1);
        var safe = Assert.Single(UdsCanTimeCorrelation.Correlate(
            did, [Candidate(database)], timelines, options, sourceAt: SourceAt));
        Assert.Equal(0, safe.OriginalSourceIndex);
        Assert.Equal(6, safe.PairCount);
        Assert.True(safe.HasSourcePairs);
        Assert.Contains("Logger 1", safe.SignalSourceText);
        Assert.All(safe.Points, point =>
        {
            Assert.Equal(0, SourceAt(point.SignalFrameIndex));
            Assert.Equal(0, SourceAt(point.DidFrameIndex));
        });
        // A caller without the original frame mapping may not pick one
        // of two physically identical logger IDs by statistical fit alone.
        var ambiguous = Assert.Single(UdsCanTimeCorrelation.Correlate(
            did, [Candidate(database)], timelines, options));
        Assert.Equal(CorrelationVerdict.InsufficientData, ambiguous.Verdict);
    }

    [Fact]
    public void MixingDiagnosticLoggersOrForeignIsoTpSegmentsIsInsufficientEvidence()
    {
        var database = Database("engine");
        var frames = Enumerable.Range(0, 6).SelectMany(index => new[]
        {
            new CanFrame(0, 256, [(byte)(index + 10)],
                index * 1_000_000_000L),
            new CanFrame(0, 256, [(byte)(100 + index)],
                index * 1_000_000_000L)
        }).ToArray();
        int SourceAt(int frameIndex) => frameIndex % 2;
        var timelines = SignalTimelineBuilder.Build(frames, [database],
            sourceAt: SourceAt, sourcePaths: ["a.asc", "b.asc"]);
        var did = Enumerable.Range(0, 6).Select(index =>
            new DiagnosticDidSample(0xF405, "Temperature", "Temperature",
                index + 10, (index + 10).ToString(), "degC",
                index * 1_000_000_000L, index * 2, "ECM", "Recorded", 0,
                [index * 2])).ToArray();
        var options = new TimeCorrelationOptions(MaxCandidates: 1,
            MaximumLagMilliseconds: 0, MaximumPairDistanceMilliseconds: 1);
        var badTransport = did.Select((sample, i) => i == 0
            ? sample with { ResponseFrameIndices = new[] { 0, 1 } } : sample).ToArray();
        var foreignSegment = Assert.Single(UdsCanTimeCorrelation.Correlate(
            badTransport, [Candidate(database)], timelines, options, sourceAt: SourceAt));
        Assert.Equal(CorrelationVerdict.InsufficientData, foreignSegment.Verdict);
        Assert.Contains("another recording", foreignSegment.Explanation);

        var mixed = did.Select((sample, i) => i == 0
            ? sample with { FrameIndex = 1 } : sample).ToArray();
        var mixedSource = Assert.Single(UdsCanTimeCorrelation.Correlate(
            mixed, [Candidate(database)], timelines, options, sourceAt: SourceAt));
        Assert.Equal(CorrelationVerdict.InsufficientData, mixedSource.Verdict);
        Assert.Contains("multiple original recordings", mixedSource.Explanation);
    }

    private static (DbcDatabase Database, CorrelationCandidate Candidate) J1939Candidate()
    {
        var database = new DbcParser().Parse("truck", new StringReader(
            """
            BO_ 2364540158 EEC1: 8 ECM
             SG_ EngineSpeed : 24|16@1+ (0.125,0) [0|8031.875] "rpm" ECM
            """));
        var message = Assert.Single(database.AllMessageDefinitions);
        return (database, new CorrelationCandidate(
            new DidParameter(0xF405, "EngineSpeed", "EngineSpeed",
                "Synthetic engine speed", "rpm", 0, 8031.875, 1, 0,
                "A_UINT16", 16, "ECM", "synthetic.odx"),
            message, Assert.Single(message.Signals),
            database.Name, 0.88, ["same unit and name"]));
    }

    private static CanFrame Eec1Frame(uint identifier, int rpm, long timestamp)
    {
        var raw = checked(rpm * 8);
        return new CanFrame(0, identifier,
            [0, 0, 0, (byte)(raw & 0xFF), (byte)(raw >> 8), 0, 0, 0],
            timestamp, CanFrameFlags.Extended);
    }

    [Fact]
    public void J1939CorrelationReportsActualWireCanIdInUiSearchAndCsvNotDbcAlias()
    {
        const uint declared = 0x0CF004FE;
        const uint observed = 0x0CF00400;
        var (database, candidate) = J1939Candidate();
        var frames = Enumerable.Range(0, 6)
            .Select(index => Eec1Frame(observed, index + 10,
                index * 1_000_000_000L)).ToArray();
        var did = Enumerable.Range(0, 6).Select(index =>
            new DiagnosticDidSample(0xF405, "EngineSpeed", "EngineSpeed",
                index + 10, (index + 10).ToString(), "rpm",
                index * 1_000_000_000L, index, "ECM", "Recorded", 0)).ToArray();
        var decoded = SignalTimelineBuilder.Build(frames,
            [new DbcDecoder(database, new DbcDecoderOptions(MatchByPgn: true))]);
        var row = Assert.Single(UdsCanTimeCorrelation.Correlate(
            did, [candidate], decoded,
            new TimeCorrelationOptions(MaxCandidates: 1,
                MaximumLagMilliseconds: 0, MaximumPairDistanceMilliseconds: 1)));

        Assert.True(row.HasSourcePairs);
        Assert.Equal(6, row.PairCount);
        Assert.Equal(0, row.SignalChannel);
        Assert.Equal(observed, row.SignalSourceIdentifier);
        Assert.True(row.SignalSourceIsExtended);
        Assert.Equal(declared, row.Candidate.Message.Identifier);
        Assert.Contains($"0x{observed:X}", row.SignalSourceText);
        Assert.DoesNotContain($"0x{declared:X}", row.SignalSourceText);
        Assert.True(CorrelationCandidateSearch.Matches(row,
            $"id:0x{observed:X}"));
        Assert.False(CorrelationCandidateSearch.Matches(row,
            $"id:0x{declared:X}"));
        Assert.True(CorrelationCandidateSearch.Matches(row,
            $"dbc-id:0x{declared:X}"));
        Assert.All(row.Points, point =>
            Assert.Equal(observed, frames[point.SignalFrameIndex].Identifier));

        using var writer = new StringWriter();
        Assert.Equal(6, CorrelationEvidenceCsvExporter.Write(writer, row));
        var lines = writer.ToString().Split('\n');
        Assert.Contains($"truck,CAN 1,0x{observed:X},29-bit,EEC1,EngineSpeed",
            lines[1]);
        Assert.DoesNotContain($",0x{declared:X},", lines[1]);
    }

    [Fact]
    public void J1939CorrelationNeverPicksArbitrarySourceAddressWithSamePgn()
    {
        const uint source00 = 0x0CF00400;
        const uint sourceFE = 0x0CF004FE;
        var (database, candidate) = J1939Candidate();
        var frames = Enumerable.Range(0, 6).SelectMany(index =>
            new[]
            {
                Eec1Frame(source00, index + 10, index * 1_000_000_000L),
                Eec1Frame(sourceFE, index + 30, index * 1_000_000_000L)
            }).ToArray();
        var did = Enumerable.Range(0, 6).Select(index =>
            new DiagnosticDidSample(0xF405, "EngineSpeed", "EngineSpeed",
                index + 10, (index + 10).ToString(), "rpm",
                index * 1_000_000_000L, index * 2,
                "ECM", "Recorded", 0)).ToArray();
        var timelines = SignalTimelineBuilder.Build(frames,
            [new DbcDecoder(database, new DbcDecoderOptions(MatchByPgn: true))]);
        Assert.Equal(2, timelines.Count);
        var result = Assert.Single(UdsCanTimeCorrelation.Correlate(
            did, [candidate], timelines,
            new TimeCorrelationOptions(MaxCandidates: 1,
                MaximumLagMilliseconds: 0, MaximumPairDistanceMilliseconds: 1)));
        Assert.Equal(CorrelationVerdict.InsufficientData, result.Verdict);
        Assert.False(result.HasSourcePairs);
        Assert.Null(result.SignalSourceIdentifier);
        Assert.Contains("Ambiguous physical CAN sources", result.Explanation);
        Assert.False(CorrelationCandidateSearch.Matches(result,
            $"id:0x{source00:X}"));
        Assert.True(CorrelationCandidateSearch.Matches(result,
            $"dbc-id:0x{sourceFE:X}"));
    }

    [Fact]
    public void ExactDefinitionMatchingRejectsSameNameFromAnotherDbcOrFrameFormat()
    {
        var database = Database("engine-A");
        var frames = Enumerable.Range(0, 6)
            .Select(index => new CanFrame(0, 256, [(byte)(index + 10)],
                index * 1_000_000_000L)).ToArray();
        var result = Assert.Single(UdsCanTimeCorrelation.Correlate(
            Enumerable.Range(0, 6).Select(index => Did(index, 0)).ToArray(),
            [Candidate(database)], SignalTimelineBuilder.Build(frames, [database]),
            new TimeCorrelationOptions(MaxCandidates: 1,
                MaximumLagMilliseconds: 0, MaximumPairDistanceMilliseconds: 1)));

        Assert.True(CorrelationSourceIdentity.MatchesDefinition(result,
            "engine-A", 256, false, "EngineData", "Temperature"));
        Assert.False(CorrelationSourceIdentity.MatchesDefinition(result,
            "engine-B", 256, false, "EngineData", "Temperature"));
        Assert.False(CorrelationSourceIdentity.MatchesDefinition(result,
            "engine-A", 256, true, "EngineData", "Temperature"));
        Assert.False(CorrelationSourceIdentity.MatchesDefinition(result,
            "engine-A", 257, false, "EngineData", "Temperature"));
        Assert.False(CorrelationSourceIdentity.MatchesDefinition(result,
            "engine-A", 256, false, "OtherMessage", "Temperature"));
        Assert.False(CorrelationSourceIdentity.MatchesDefinition(result,
            "engine-A", 256, false, "EngineData", "OtherSignal"));
    }

    [Fact]
    public void CorrelationChartMustNotEraseTheActualCanSampleLag()
    {
        var paired = new CorrelationPoint[]
        {
            new(1_000_000_000L, 1_040_000_000L, 10, 10, 10, 0, 0, 1),
            new(2_000_000_000L, 2_040_000_000L, 20, 20, 20, 0, 2, 3)
        };
        var (start, end) = CorrelationTimeAxis.Bounds(paired);
        Assert.Equal(1_000_000_000L, start);
        Assert.Equal(2_040_000_000L, end);

        // CAN measurement comes 40 ms after the diagnostic value. Mapping
        // both onto DidTimestamp would hide this delay completely.
        var didX = CorrelationTimeAxis.Fraction(paired[0].DidTimestampNanoseconds, start, end);
        var canX = CorrelationTimeAxis.Fraction(paired[0].SignalTimestampNanoseconds, start, end);
        Assert.True(canX > didX);
        Assert.Equal(40_000_000d / 1_040_000_000d, canX - didX, 7);
    }

    [Fact]
    public void CorrelationChartSourceTimeProjectionIsSafeAcrossExtremeSignedTimestamps()
    {
        var left = long.MinValue + 100;
        var right = long.MaxValue - 100;
        var points = new CorrelationPoint[]
        {
            new(left, right, 1, 1, 1, 0, 0, 1)
        };
        var (start, end) = CorrelationTimeAxis.Bounds(points);
        Assert.Equal(left, start);
        Assert.Equal(right, end);
        Assert.Equal(0d, CorrelationTimeAxis.Fraction(left, start, end));
        Assert.Equal(1d, CorrelationTimeAxis.Fraction(right, start, end));
        Assert.InRange(CorrelationTimeAxis.Fraction(0, start, end), 0.49d, 0.51d);
        Assert.Equal(0.5d, CorrelationTimeAxis.Fraction(0, 0, 0));
    }

    [Fact]
    public void OriginalIsoTpContinuationFrameSelectsItsCorrelatedSourcePair()
    {
        var pairs = new CorrelationPoint[]
        {
            new(1_000_000_000, 1_050_000_000, 10, 10, 10, 0,
                DidFrameIndex: 6, SignalFrameIndex: 2,
                DidField: "Temperature", DidResponseFrameIndices: [4, 5, 6]),
            new(2_000_000_000, 2_050_000_000, 20, 20, 20, 0,
                DidFrameIndex: 16, SignalFrameIndex: 12,
                DidField: "Temperature", DidResponseFrameIndices: [14, 15, 16])
        };

        Assert.Equal(0, CorrelationEvidenceNavigator.FindPairForFrame(pairs, 5));
        Assert.Equal(1, CorrelationEvidenceNavigator.FindPairForFrame(pairs, 15));
        Assert.Equal(1, CorrelationEvidenceNavigator.FindPairForFrame(pairs, 12));
        Assert.Equal(1, CorrelationEvidenceNavigator.FindPairForFrame(pairs, 16));
        Assert.Equal(-1, CorrelationEvidenceNavigator.FindPairForFrame(pairs, 999));
        Assert.Equal(-1, CorrelationEvidenceNavigator.FindPairForFrame(pairs, -1));
    }

    [Fact]
    public void LastMatchingDidSourceIsChosenWithoutConfusingCanAndDidFrames()
    {
        var pairs = new CorrelationPoint[]
        {
            new(1_000, 1_500, 1, 1, 1, 0, 10, 20, "Value", [8, 9, 10]),
            new(2_000, 2_500, 2, 2, 2, 0, 30, 40, "Value", [28, 29, 30]),
            new(3_000, 3_500, 3, 3, 3, 0, 50, 60, "Value", [48, 49, 50])
        };
        Assert.Equal(1, CorrelationEvidenceNavigator.FindLastDidPair(
            pairs, new HashSet<int> { 8, 29, 40 }));
        Assert.Equal(2, CorrelationEvidenceNavigator.FindLastDidPair(
            pairs, new HashSet<int> { 10, 48 }));
        Assert.Equal(-1, CorrelationEvidenceNavigator.FindLastDidPair(
            pairs, new HashSet<int> { 60 }));
        Assert.Equal(-1, CorrelationEvidenceNavigator.FindLastDidPair(
            pairs, new HashSet<int>()));
    }

    [Fact]
    public void CsvExportPreservesOriginalSourceFramesAndIndependentTimeStamps()
    {
        var database = Database("engine");
        var frames = Enumerable.Range(0, 6)
            .Select(index => new CanFrame(0, 256, [(byte)(index + 10)],
                index * 1_000_000_000L)).ToArray();
        var observations = Enumerable.Range(0, 6)
            .Select(index => Did(index, 0)).ToArray();
        var result = Assert.Single(UdsCanTimeCorrelation.Correlate(
            observations, [Candidate(database)], SignalTimelineBuilder.Build(frames, [database]),
            new TimeCorrelationOptions(MaxCandidates: 1,
                MaximumLagMilliseconds: 0, MaximumPairDistanceMilliseconds: 1)));
        using var writer = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
        var exported = CorrelationEvidenceCsvExporter.Write(writer, result);

        Assert.Equal(6, exported);
        var lines = writer.ToString().TrimEnd('\r', '\n').Split('\n');
        Assert.Equal(7, lines.Length); // header + one original source observation per line
        Assert.Contains("DidTimestampNs,CanTimestampNs", lines[0]);
        Assert.Contains("IsoTpResponseFramesOneBased", lines[0]);
        Assert.Contains("StatisticalFitScore,AssociationIndicator", lines[0]);
        Assert.Contains("engine,CAN 1,0x100,11-bit,EngineData,Temperature", lines[1]);
        Assert.Contains("Observed paired CAN and UDS frames; statistical association only", lines[1]);
        Assert.Contains(",0,0,10,10,", lines[1]);
        Assert.Contains(",1,1,", lines[1]); // human-readable 1-based raw frame references
    }

    [Fact]
    public void CsvExportProtectsFormulaLikeImportedNamesWithoutRewritingPhysicalNumbers()
    {
        var database = Database("engine");
        var frames = Enumerable.Range(0, 6)
            .Select(index => new CanFrame(0, 256, [(byte)(index + 10)],
                index * 1_000_000_000L)).ToArray();
        var result = Assert.Single(UdsCanTimeCorrelation.Correlate(
            Enumerable.Range(0, 6).Select(index => Did(index, 0)).ToArray(),
            [Candidate(database)], SignalTimelineBuilder.Build(frames, [database]),
            new TimeCorrelationOptions(MaxCandidates: 1,
                MaximumLagMilliseconds: 0, MaximumPairDistanceMilliseconds: 1)));
        var untrusted = result with
        {
            Candidate = result.Candidate with
            {
                Database = " =HYPERLINK(\"http://invalid.test\")",
                Did = result.Candidate.Did with
                {
                    LongName = "=SUM(1,1)"
                }
            }
        };
        using var writer = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
        CorrelationEvidenceCsvExporter.Write(writer, untrusted);
        var csv = writer.ToString();
        Assert.Contains("'=SUM(1,1)", csv);
        Assert.Contains("' =HYPERLINK", csv);
        Assert.Contains("DidValue,CanValue", csv); // raw numeric evidence unchanged
    }

    [Fact]
    public void IndexedChartHitTestingUsesOriginalDidAndCanTimestamps()
    {
        var pairs = new CorrelationPoint[]
        {
            new(1_000_000_000L, 1_040_000_000L,
                10, 10, 10, 0, 0, 1),
            new(2_000_000_000L, 2_040_000_000L,
                20, 20, 20, 0, 2, 3)
        };
        var index = new CorrelationPointHitIndex(pairs);
        var (start, end) = CorrelationTimeAxis.Bounds(pairs);

        double X(long timestamp) =>
            CorrelationTimeAxis.Fraction(timestamp, start, end) * 1000d;
        int Hit(double x, double y) => index.FindNearest(
            x, y, start, end, 0, 1000, 0, 100,
            didMin: 10, didMax: 20, canMin: 10, canMax: 20,
            radiusPixels: 9);

        Assert.Equal(0, Hit(X(pairs[0].SignalTimestampNanoseconds), 100));
        Assert.Equal(1, Hit(X(pairs[1].DidTimestampNanoseconds), 0));
        Assert.Equal(-1, Hit(500, 50));
        Assert.Equal(-1, Hit(double.NaN, 50));
        Assert.Equal(-1, Hit(X(pairs[0].DidTimestampNanoseconds), -100));
    }

    [Fact]
    public void IndexedChartHitTestingDoesNotMisselectDenseDistantPoints()
    {
        var pairs = Enumerable.Range(0, 10_000).Select(index =>
            new CorrelationPoint(
                DidTimestampNanoseconds: index * 1_000_000_000L,
                SignalTimestampNanoseconds: index * 1_000_000_000L + 50_000_000L,
                DidValue: index,
                SignalValue: index,
                PredictedSignalValue: index,
                Error: 0,
                DidFrameIndex: 2 * index,
                SignalFrameIndex: 2 * index + 1)).ToArray();
        var indexer = new CorrelationPointHitIndex(pairs);
        var (start, end) = CorrelationTimeAxis.Bounds(pairs);
        const int target = 5_017;
        var x = CorrelationTimeAxis.Fraction(pairs[target].SignalTimestampNanoseconds,
            start, end) * 5000d;
        var y = 300d - target / 9999d * 300d;
        var selected = indexer.FindNearest(x, y,
            start, end, 0, 5000, 0, 300,
            didMin: 0, didMax: 9999, canMin: 0, canMax: 9999,
            radiusPixels: 0.01d);
        Assert.Equal(target, selected);
    }

    [Fact]
    public void CorrelationSearchSupportsTypedSourceFiltersAndChannelShortcuts()
    {
        var database = Database("engine");
        var frames = Enumerable.Range(0, 6)
            .Select(index => new CanFrame(1, 256, [(byte)(index + 10)],
                index * 1_000_000_000L)).ToArray();
        var observed = Enumerable.Range(0, 6).Select(index => Did(index, 1)).ToArray();
        var row = Assert.Single(UdsCanTimeCorrelation.Correlate(
            observed, [Candidate(database)],
            SignalTimelineBuilder.Build(frames, [database]),
            new TimeCorrelationOptions(MaxCandidates: 1,
                MaximumLagMilliseconds: 0, MaximumPairDistanceMilliseconds: 1)));

        Assert.Equal(1, row.SignalChannel); // UI channel label is CAN 2
        var prepared = CorrelationCandidateSearch.Tokenize(
            "did:0xF405 dbc:engine bus:2 id:0x100 format:11 signal:Temperature has:paired");
        Assert.Equal(7, prepared.Length);
        Assert.True(CorrelationCandidateSearch.MatchesPrepared(row, prepared));
        Assert.True(CorrelationCandidateSearch.Matches(row,
            "did:0xF405 dbc:engine bus:2 id:0x100 format:11 signal:Temperature has:paired"));
        Assert.True(CorrelationCandidateSearch.Matches(row, "did:f405 CH2 -ch1"));
        Assert.True(CorrelationCandidateSearch.Matches(row, "can2 -dbc:unrelated"));
        Assert.True(CorrelationCandidateSearch.Matches(row, "ecu:ECM -ecu:BCM"));
        Assert.True(CorrelationCandidateSearch.Matches(row,
            "verdict:" + row.Verdict));

        Assert.False(CorrelationCandidateSearch.Matches(row, "bus:1"));
        Assert.False(CorrelationCandidateSearch.Matches(row, "-bus:2"));
        Assert.False(CorrelationCandidateSearch.Matches(row, "dbc:unrelated"));
        Assert.False(CorrelationCandidateSearch.Matches(row, "id:0x101"));
        Assert.False(CorrelationCandidateSearch.Matches(row, "format:extended"));
        Assert.False(CorrelationCandidateSearch.Matches(row, "has:unpaired"));
        Assert.False(CorrelationCandidateSearch.Matches(row, "did:"));
    }

    [Fact]
    public void CorrelationSearchKeepsInsufficientEvidenceInspectable()
    {
        var database = Database("engine");
        var row = Assert.Single(UdsCanTimeCorrelation.Correlate(
            [], [Candidate(database)], [],
            new TimeCorrelationOptions(MaxCandidates: 1)));

        Assert.False(row.HasSourcePairs);
        Assert.True(CorrelationCandidateSearch.Matches(row, "has:unpaired"));
        Assert.True(CorrelationCandidateSearch.Matches(row, "-has:pairs"));
        Assert.False(CorrelationCandidateSearch.Matches(row, "has:paired"));
        Assert.False(CorrelationCandidateSearch.Matches(row, "bus:1"));
        Assert.True(CorrelationCandidateSearch.Matches(row, ""));
        Assert.True(CorrelationCandidateSearch.MatchesPrepared(row,
            CorrelationCandidateSearch.Tokenize("")));
        Assert.False(CorrelationCandidateSearch.MatchesPrepared(row,
            CorrelationCandidateSearch.Tokenize("has:paired -dbc:engine")));
    }

    [Fact]
    public void SourceFiltersAndCsvCannotTurnUnpairedDbcCandidateIntoRecordedCanEvidence()
    {
        var database = Database("engine");
        var insufficient = Assert.Single(UdsCanTimeCorrelation.Correlate(
            [], [Candidate(database)], [],
            new TimeCorrelationOptions(MaxCandidates: 1)));

        // Simulate an upstream candidate selection that resolved a DBC
        // timeline but failed to pair even one DID/real CAN frame. The
        // source-shaped metadata MUST NOT escape as recorded provenance.
        var unpaired = insufficient with
        {
            SignalChannel = 0,
            SignalSourceIdentifier = 0x100,
            SignalSourceIsExtended = false,
            Points = [new CorrelationPoint(1_000, 1_000,
                10, 10, 10, 0, -1, -1)]
        };
        Assert.Equal(0, unpaired.PairCount);
        Assert.False(unpaired.HasSourcePairs);
        Assert.Contains("not established", unpaired.SignalSourceText);
        Assert.False(CorrelationCandidateSearch.Matches(unpaired, "id:0x100"));
        Assert.False(CorrelationCandidateSearch.Matches(unpaired, "format:11"));
        Assert.False(CorrelationCandidateSearch.Matches(unpaired, "bus:1"));
        Assert.True(CorrelationCandidateSearch.Matches(unpaired, "dbc-id:0x100"));
        Assert.True(CorrelationCandidateSearch.Matches(unpaired, "dbc-format:11"));
        Assert.True(CorrelationCandidateSearch.Matches(unpaired, "has:unpaired"));

        using var output = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(1, CorrelationEvidenceCsvExporter.Write(output, unpaired));
        var lines = output.ToString().TrimEnd('\r', '\n').Split('\n');
        Assert.Equal(2, lines.Length);
        Assert.Contains("engine,unverified,unverified,unverified", lines[1]);
        Assert.DoesNotContain(",0x100,11-bit,", lines[1]);
    }

    [Fact]
    public void CandidateWithoutActualDidCanPairsCannotClaimObservedSource()
    {
        var database = Database("engine");
        var result = Assert.Single(UdsCanTimeCorrelation.Correlate(
            [], [Candidate(database)], [], new TimeCorrelationOptions(MaxCandidates: 1)));

        Assert.Equal(CorrelationVerdict.InsufficientData, result.Verdict);
        Assert.Equal(-1, result.SignalChannel);
        Assert.False(result.HasSourcePairs);
        Assert.False(CorrelationSourceIdentity.MatchesObservedChannel(result, null));
        Assert.False(CorrelationSourceIdentity.MatchesObservedChannel(result, 0));
        Assert.Contains("not established", result.SignalSourceText);
    }
}
