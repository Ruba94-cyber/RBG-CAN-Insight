using RbgCanInsight.Analysis;
using RbgCanInsight.Dbc;
using RbgCanInsight.Domain;
using Xunit;

namespace RbgCanInsight.Tests;

public sealed class SignalTimelineIntegrityTests
{
    [Fact]
    public void OutOfOrderCapturedFramesAreChronologicalButKeepTheirExactSourceIndices()
    {
        var database = new DbcParser().Parse("order", new StringReader(
            """
            BO_ 256 State: 8 ECU
             SG_ Value : 0|8@1+ (1,0) [0|255] "" ECU
            """));
        var frames = new[]
        {
            new CanFrame(0, 256, [30], 30_000_000),
            new CanFrame(0, 256, [10], 10_000_000),
            new CanFrame(0, 256, [20], 20_000_000),
            new CanFrame(0, 256, [21], 20_000_000)
        };
        var timeline = Assert.Single(SignalTimelineBuilder.Build(frames, [database]));

        Assert.Equal(new long[]
            {
                10_000_000, 20_000_000, 20_000_000, 30_000_000
            }, timeline.Samples.Select(item => item.TimestampNanoseconds));
        Assert.Equal(new[] { 1, 2, 3, 0 },
            timeline.Samples.Select(item => item.FrameIndex));
        Assert.Equal(new double[] { 10, 20, 21, 30 },
            timeline.Samples.Select(item => item.Value));
        Assert.Null(timeline.SampleAt(9_999_999));
        Assert.Equal(1, timeline.SampleAt(10_000_000)?.FrameIndex);
        Assert.Equal(3, timeline.SampleAt(20_000_000)?.FrameIndex);
        Assert.Equal(0, timeline.SampleAt(99_000_000)?.FrameIndex);
    }

    [Fact]
    public void SparseCanSignalsHoldPreviousValueWithoutReadingFutureFrames()
    {
        var database = new DbcParser().Parse("sparse", new StringReader(
            """
            BO_ 256 Sensor: 8 ECU
             SG_ Temperature : 0|8@1+ (1,0) [0|255] "degC" ECU
            """));
        var frames = new[]
        {
            new CanFrame(0, 256, [10], 100_000_000L),
            new CanFrame(0, 256, [20], 2_100_000_000L),
            new CanFrame(0, 256, [30], 9_100_000_000L)
        };

        var timeline = Assert.Single(SignalTimelineBuilder.Build(frames, [database]));
        Assert.Null(timeline.SampleAt(99_999_999L));
        Assert.Equal(10d, timeline.SampleAt(2_099_999_999L)?.Value);
        Assert.Equal(20d, timeline.SampleAt(2_100_000_000L)?.Value);
        Assert.Equal(20d, timeline.SampleAt(9_099_999_999L)?.Value);
        Assert.Equal(30d, timeline.SampleAt(9_100_000_000L)?.Value);
        Assert.Equal(30d, timeline.SampleAt(10_000_000_000L)?.Value);
    }



    [Fact]
    public void MissingSignalClassificationNeverLeaksAcrossIndependentOriginalLoggers()
    {
        var database = new DbcParser().Parse("multifile-missing", new StringReader(
            """
            BO_ 256 Engine: 8 ECU
             SG_ Speed : 0|8@1+ (1,0) [0|255] "" ECU
             SG_ Unreadable : 8|8@1+ (1,0) [0|255] "" ECU
            """));
        var frames = new[]
        {
            // Logger 1 contains a short payload: Speed decodes, Unreadable
            // has no bytes. Logger 2 never emits this DBC message.
            new CanFrame(0, 256, [10], 10),
            new CanFrame(0, 0x500, [88], 10),
            new CanFrame(0, 256, [11], 20),
            new CanFrame(0, 0x500, [89], 20)
        };
        var paths = new[] { "primary.asc", "secondary.asc" };
        int SourceAt(int index) => index % 2;
        var timelines = SignalTimelineBuilder.Build(frames, [database],
            sourceAt: SourceAt, sourcePaths: paths);
        var observed = frames
            .Where(frame => !frame.IsError)
            .Select(frame => (frame.Channel, frame.Identifier, frame.IsExtended))
            .ToHashSet();
        var scoped = frames.Select((frame, index) =>
                (Source: SourceAt(index), frame.Channel,
                    frame.Identifier, Extended: frame.IsExtended))
            .ToHashSet();
        var reports = SignalChangeAnalyzer.Analyze(timelines, [database],
            seenFrameIdentities: observed,
            seenSourceFrameIdentities: scoped, sourcePaths: paths);
        var speed = Assert.Single(reports.Where(report =>
            report.SampleCount > 0 && report.SignalName == "Speed"));
        Assert.Equal(0, speed.Timeline.OriginalSourceIndex);
        Assert.Equal(2, speed.SampleCount);

        // It would be a false missing finding to claim Logger 2 should
        // have decoded a DBC message it never transmitted.
        Assert.DoesNotContain(reports, report =>
            report.Timeline.OriginalSourceIndex == 1);
        Assert.All(reports, report => Assert.Equal(paths[0],
            report.Timeline.OriginalSourcePath));
        var missing = Assert.Single(reports.Where(report =>
            report.Activity == SignalActivity.Missing));
        Assert.Equal("Unreadable", missing.SignalName);
        Assert.Equal(0, missing.Timeline.OriginalSourceIndex);
        Assert.Equal(0, missing.SampleCount);
    }

    [Fact]
    public void IdenticalCanIdsInTwoLoggersKeepSeparateMissingAndObservedReports()
    {
        var database = new DbcParser().Parse("split", new StringReader(
            """
            BO_ 256 Engine: 8 ECU
             SG_ Speed : 0|8@1+ (1,0) [0|255] "" ECU
             SG_ Unreadable : 8|8@1+ (1,0) [0|255] "" ECU
            """));
        var frames = new[]
        {
            new CanFrame(0, 256, [10], 0),
            new CanFrame(0, 256, [80], 0),
            new CanFrame(0, 256, [11], 100),
            new CanFrame(0, 256, [90], 100)
        };
        var paths = new[] { "A.asc", "B.asc" };
        int SourceAt(int index) => index % 2;
        var timelines = SignalTimelineBuilder.Build(frames, [database],
            sourceAt: SourceAt, sourcePaths: paths);
        var observed = frames.Select(frame => (frame.Channel, frame.Identifier,
            Extended: frame.IsExtended)).ToHashSet();
        var scoped = frames.Select((frame, i) =>
            (Source: SourceAt(i), frame.Channel, frame.Identifier,
                Extended: frame.IsExtended)).ToHashSet();
        var reports = SignalChangeAnalyzer.Analyze(timelines, [database],
            seenFrameIdentities: observed,
            seenSourceFrameIdentities: scoped, sourcePaths: paths);
        Assert.Equal(4, reports.Count);
        Assert.Equal(2, reports.Count(r => r.Activity == SignalActivity.Missing));
        Assert.Equal(new[] { 0, 1 }, reports
            .Where(r => r.Activity == SignalActivity.Missing)
            .Select(r => r.Timeline.OriginalSourceIndex).Order().ToArray());
        Assert.Equal(2, reports.Count(r => r.SignalName == "Speed" && r.SampleCount == 2));
        Assert.All(reports.Where(r => r.Activity == SignalActivity.Missing),
            report => Assert.Equal(0, report.ChangeCount));
    }

    [Fact]
    public void SameCanIdAndBusInIndependentLoggersProduceIndependentSignalCurves()
    {
        var database = new DbcParser().Parse("same-id", new StringReader(
            """
            BO_ 256 Engine: 8 ECM
             SG_ Temperature : 0|8@1+ (1,0) [0|255] "C" ECM
            """));
        var frames = new[]
        {
            new CanFrame(0, 256, [10], 100_000_000),
            new CanFrame(0, 256, [200], 100_000_000),
            new CanFrame(0, 256, [11], 200_000_000),
            new CanFrame(0, 256, [201], 200_000_000)
        };
        var sourceIndices = new[] { 0, 1, 0, 1 };
        var paths = new[] { @"C:\a\logger.asc", @"D:\b\logger.asc" };
        var timelines = SignalTimelineBuilder.Build(frames, [database],
            sourceAt: frameIndex => sourceIndices[frameIndex], sourcePaths: paths)
            .OrderBy(timeline => timeline.OriginalSourceIndex).ToArray();

        Assert.Equal(2, timelines.Length);
        Assert.NotEqual(timelines[0].Key, timelines[1].Key);
        Assert.Equal(paths[0], timelines[0].OriginalSourcePath);
        Assert.Equal(paths[1], timelines[1].OriginalSourcePath);
        Assert.Equal(new[] { 0, 2 }, timelines[0].Samples.Select(x => x.FrameIndex));
        Assert.Equal(new[] { 1, 3 }, timelines[1].Samples.Select(x => x.FrameIndex));
        Assert.Equal(new double[] { 10, 11 }, timelines[0].Samples.Select(x => x.Value));
        Assert.Equal(new double[] { 200, 201 }, timelines[1].Samples.Select(x => x.Value));
        Assert.Equal(1, timelines[0].Samples.Count - 1);
        Assert.Equal(1, timelines[1].Samples.Count - 1);
        Assert.Equal(11d, timelines[0].SampleAt(200_000_000)?.Value);
        Assert.Equal(201d, timelines[1].SampleAt(200_000_000)?.Value);
    }

    [Fact]
    public void OriginalLoggerScopeCannotBeFakedByMissingOrInvalidSourceMapping()
    {
        var database = new DbcParser().Parse("guard", new StringReader(
            """
            BO_ 256 Engine: 8 ECM
             SG_ Temperature : 0|8@1+ (1,0) [0|255] "C" ECM
            """));
        var frames = new[] { new CanFrame(0, 256, [42], 1_000_000) };
        Assert.Throws<ArgumentException>(() =>
            SignalTimelineBuilder.Build(frames, [database],
                sourcePaths: new[] { "first.asc", "second.asc" }));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SignalTimelineBuilder.Build(frames, [database],
                sourceAt: _ => 2, sourcePaths: new[] { "first.asc", "second.asc" }));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SignalTimelineBuilder.Build(frames, [database], sourceAt: _ => -1));
        var legacy = Assert.Single(SignalTimelineBuilder.Build(frames, [database]));
        Assert.Equal(-1, legacy.OriginalSourceIndex);
        Assert.DoesNotContain("|logger", legacy.Key);
    }

    [Fact]
    public void DifferentFilesWithIdenticalChannelAndIdDoNotFabricateSignalTransitions()
    {
        var database = new DbcParser().Parse("constant", new StringReader(
            """
            BO_ 256 Engine: 8 ECM
             SG_ State : 0|8@1+ (1,0) [0|255] "" ECM
            """));
        var frames = new[]
        {
            new CanFrame(0, 256, [1], 0),
            new CanFrame(0, 256, [2], 0),
            new CanFrame(0, 256, [1], 100),
            new CanFrame(0, 256, [2], 100)
        };
        var source = new[] { 0, 1, 0, 1 };
        var curves = SignalTimelineBuilder.Build(frames, [database],
            sourceAt: i => source[i], sourcePaths: ["a.asc", "b.asc"]);
        Assert.Equal(2, curves.Count);
        Assert.All(curves, timeline =>
            Assert.All(timeline.Samples, sample =>
                Assert.Equal(timeline.Samples[0].Value, sample.Value)));
    }

    [Fact]
    public void SameDbcSignalOnDifferentCanInterfacesNeverMergesRecordedTimelines()
    {
        var database = new DbcParser().Parse("channels", new StringReader(
            """
            BO_ 256 SharedMessage: 8 ECU
             SG_ Counter : 0|8@1+ (1,0) [0|255] "" ECU
            """));
        var frames = new[]
        {
            new CanFrame(0, 256, [10], 1_000_000L),
            new CanFrame(1, 256, [90], 1_000_000L),
            new CanFrame(0, 256, [11], 2_000_000L),
            new CanFrame(1, 256, [91], 2_000_000L)
        };

        var timelines = SignalTimelineBuilder.Build(frames, [database])
            .OrderBy(row => row.Channel).ToArray();
        Assert.Equal(2, timelines.Length);
        Assert.NotEqual(timelines[0].Key, timelines[1].Key);
        Assert.Equal(0, timelines[0].Channel);
        Assert.Equal(1, timelines[1].Channel);
        Assert.Equal(new double[] { 10, 11 },
            timelines[0].Samples.Select(sample => sample.Value));
        Assert.Equal(new double[] { 90, 91 },
            timelines[1].Samples.Select(sample => sample.Value));
        Assert.Equal(new[] { 0, 2 },
            timelines[0].Samples.Select(sample => sample.FrameIndex));
        Assert.Equal(new[] { 1, 3 },
            timelines[1].Samples.Select(sample => sample.FrameIndex));
    }

    [Fact]
    public void CachedDecoderResolutionPreservesObservedSourceFramesAcrossBusesAndNoise()
    {
        var engine = new DbcParser().Parse("engine", new StringReader(
            """
            BO_ 256 Engine: 8 ECM
             SG_ Rpm : 0|8@1+ (10,0) [0|2550] "rpm" ECM
            """));
        var body = new DbcParser().Parse("body", new StringReader(
            """
            BO_ 512 Body: 8 BCM
             SG_ Door : 0|8@1+ (1,0) [0|255] "" BCM
            """));
        var frames = new[]
        {
            // Many unrelated IDs must not contaminate the observed
            // signal list or acquire empty/ghost DBC timelines.
            new CanFrame(0, 0x777, [0xAA], 0),
            new CanFrame(0, 256, [10], 1_000_000),
            new CanFrame(1, 256, [20], 2_000_000),
            new CanFrame(0, 512, [1], 3_000_000),
            new CanFrame(0, 0x777, [0xBB], 4_000_000),
            new CanFrame(0, 256, [30], 5_000_000),
            new CanFrame(1, 256, [40], 6_000_000),
            new CanFrame(0, 512, [0], 7_000_000)
        };
        var timelines = SignalTimelineBuilder.Build(frames,
            [new DbcDecoder(engine), new DbcDecoder(body)]);
        Assert.Equal(3, timelines.Count);
        var engineCh1 = Assert.Single(timelines.Where(timeline =>
            timeline.DatabaseName == "engine" && timeline.Channel == 0));
        var engineCh2 = Assert.Single(timelines.Where(timeline =>
            timeline.DatabaseName == "engine" && timeline.Channel == 1));
        var bodyCh1 = Assert.Single(timelines.Where(timeline =>
            timeline.DatabaseName == "body" && timeline.Channel == 0));
        Assert.Equal(new[] { 1, 5 }, engineCh1.Samples.Select(sample => sample.FrameIndex));
        Assert.Equal(new[] { 100d, 300d }, engineCh1.Samples.Select(sample => sample.Value));
        Assert.Equal(new[] { 2, 6 }, engineCh2.Samples.Select(sample => sample.FrameIndex));
        Assert.Equal(new[] { 200d, 400d }, engineCh2.Samples.Select(sample => sample.Value));
        Assert.Equal(new[] { 3, 7 }, bodyCh1.Samples.Select(sample => sample.FrameIndex));
        Assert.Equal(new[] { 1d, 0d }, bodyCh1.Samples.Select(sample => sample.Value));
        Assert.DoesNotContain(timelines, timeline =>
            timeline.Samples.Any(sample => sample.FrameIndex is 0 or 4));
    }

    [Fact]
    public void RepeatedRemoteOrErrorFrameNeverBecomesAnObservedSignal()
    {
        var database = new DbcParser().Parse("noise", new StringReader(
            """
            BO_ 256 State: 8 ECU
             SG_ Value : 0|8@1+ (1,0) [0|255] "" ECU
            """));
        var frames = new[]
        {
            new CanFrame(0, 256, [], 0, CanFrameFlags.Remote),
            new CanFrame(0, 256, [100], 1_000_000, CanFrameFlags.Error),
            new CanFrame(0, 256, [10], 2_000_000),
            new CanFrame(0, 256, [15], 3_000_000)
        };
        var timeline = Assert.Single(SignalTimelineBuilder.Build(frames, [database]));
        Assert.Equal(new[] { 2, 3 }, timeline.Samples.Select(sample => sample.FrameIndex));
        Assert.Equal(new double[] { 10, 15 }, timeline.Samples.Select(sample => sample.Value));
    }

    [Fact]
    public void ConflictingSameNamedDbcDefinitionsAreNotMixedByCachedDecoder()
    {
        var standard = """
            BO_ 256 Engine: 8 ECM
             SG_ Temperature : 0|8@1+ (1,0) [0|255] "degC" ECM
            """;
        var changedScale = """
            BO_ 256 Engine: 8 ECM
             SG_ Temperature : 0|8@1+ (2,0) [0|510] "degC" ECM
            """;
        var parser = new DbcParser();
        var first = parser.Parse("duplicate", new StringReader(standard));
        var second = parser.Parse("duplicate", new StringReader(changedScale));
        var frames = new[]
        {
            new CanFrame(0, 256, [10], 1_000_000),
            new CanFrame(0, 256, [20], 2_000_000)
        };
        var timelines = SignalTimelineBuilder.Build(frames,
            [new DbcDecoder(first), new DbcDecoder(second)]);
        Assert.Empty(timelines);
        // Wrong DBC ownership must not silently appear as a plausible
        // source-backed temperature curve despite real CAN frames.
        Assert.Equal(new byte[] { 10 }, frames[0].GetPayload());
    }

    [Fact]
    public void J1939PgnMatchedSourcesAreDistinctRealRecordedSignalTimelines()
    {
        // A single EEC1 PGN definition can match several real ECU source
        // addresses. Their engine RPM histories must NEVER be joined into
        // one plausible curve. Source frame indices stay audit-friendly.
        var database = new DbcParser().Parse("truck", new StringReader(
            """
            BO_ 2364540158 EEC1: 8 ECM
             SG_ EngineSpeed : 24|16@1+ (0.125,0) [0|8031.875] "rpm" ECM
            """));
        const uint ecmFe = 0x0CF004FE;
        const uint ecm00 = 0x0CF00400;
        var frames = new[]
        {
            new CanFrame(0, ecmFe, [0, 0, 0, 0x10, 0x27, 0, 0, 0], 1_000_000,
                CanFrameFlags.Extended),
            new CanFrame(0, ecm00, [0, 0, 0, 0x20, 0x4E, 0, 0, 0], 2_000_000,
                CanFrameFlags.Extended),
            new CanFrame(0, ecmFe, [0, 0, 0, 0xE0, 0x2E, 0, 0, 0], 3_000_000,
                CanFrameFlags.Extended),
            new CanFrame(0, ecm00, [0, 0, 0, 0x80, 0x3E, 0, 0, 0], 4_000_000,
                CanFrameFlags.Extended),
            new CanFrame(1, ecmFe, [0, 0, 0, 0x40, 0x1F, 0, 0, 0], 5_000_000,
                CanFrameFlags.Extended)
        };
        var decoder = new DbcDecoder(database, new DbcDecoderOptions(MatchByPgn: true));
        var series = SignalTimelineBuilder.Build(frames, [decoder]);
        Assert.Equal(3, series.Count);
        var fe = Assert.Single(series.Where(item =>
            item.Channel == 0 && item.SourceIdentifier == ecmFe));
        var zero = Assert.Single(series.Where(item =>
            item.Channel == 0 && item.SourceIdentifier == ecm00));
        var otherBus = Assert.Single(series.Where(item =>
            item.Channel == 1 && item.SourceIdentifier == ecmFe));
        Assert.All(series, item =>
        {
            Assert.Equal(ecmFe, item.Message.Identifier);
            Assert.True(item.SourceIsExtended);
        });
        Assert.Equal(3, series.Select(item => item.Key).Distinct().Count());
        Assert.Equal(new[] { 0, 2 }, fe.Samples.Select(item => item.FrameIndex));
        Assert.Equal(new[] { 1, 3 }, zero.Samples.Select(item => item.FrameIndex));
        Assert.Equal(new[] { 4 }, otherBus.Samples.Select(item => item.FrameIndex));
        Assert.Equal(new[] { 1250d, 1500d }, fe.Samples.Select(item => item.Value));
        Assert.Equal(new[] { 2500d, 2000d }, zero.Samples.Select(item => item.Value));
        Assert.Equal(1000d, Assert.Single(otherBus.Samples).Value);
        // The source snapshot remains unchanged and each sample's
        // provenance resolves to its actual CAN header, not a PGN alias.
        Assert.Equal(ecm00, frames[zero.Samples[0].FrameIndex].Identifier);
        Assert.Equal(ecmFe, frames[fe.Samples[1].FrameIndex].Identifier);
    }

    [Fact]
    public void J1939PgnMatchingCannotBeAssumedWhenDecoderOptionIsDisabled()
    {
        var database = new DbcParser().Parse("truck", new StringReader(
            """
            BO_ 2364540158 EEC1: 8 ECM
             SG_ EngineSpeed : 24|16@1+ (0.125,0) [0|8031.875] "rpm" ECM
            """));
        var frames = new[]
        {
            new CanFrame(0, 0x0CF00400, [0, 0, 0, 0x20, 0x4E, 0, 0, 0],
                1_000_000, CanFrameFlags.Extended),
            new CanFrame(0, 0x0CF004FE, [0, 0, 0, 0x10, 0x27, 0, 0, 0],
                2_000_000, CanFrameFlags.Extended)
        };
        var strict = SignalTimelineBuilder.Build(frames,
            [new DbcDecoder(database)]);
        var series = Assert.Single(strict);
        Assert.Equal(0x0CF004FEu, series.SourceIdentifier);
        Assert.Equal(new[] { 1 }, series.Samples.Select(item => item.FrameIndex));
    }

    [Fact]
    public void StandardAndExtendedSameNumericCanIdHaveDistinctPlotEvidence()
    {
        var database = new DbcParser().Parse("dual-format", new StringReader(
            """
            BO_ 256 StandardStatus: 8 ECU11
             SG_ State : 0|8@1+ (1,0) [0|255] "" ECU11

            BO_ 2147483904 ExtendedStatus: 8 ECU29
             SG_ State : 0|8@1+ (10,0) [0|2550] "" ECU29
            """));
        var frames = new[]
        {
            new CanFrame(0, 256, [4], 1_000_000),
            new CanFrame(0, 256, [7], 2_000_000, CanFrameFlags.Extended),
            new CanFrame(0, 256, [5], 3_000_000),
            new CanFrame(0, 256, [8], 4_000_000, CanFrameFlags.Extended)
        };
        var timelines = SignalTimelineBuilder.Build(frames, [database]);
        Assert.Equal(2, timelines.Count);
        var standard = Assert.Single(timelines.Where(item => !item.SourceIsExtended));
        var extended = Assert.Single(timelines.Where(item => item.SourceIsExtended));
        Assert.Equal(256u, standard.SourceIdentifier);
        Assert.Equal(256u, extended.SourceIdentifier);
        Assert.NotEqual(standard.Key, extended.Key);
        Assert.Equal(new[] { 0, 2 }, standard.Samples.Select(item => item.FrameIndex));
        Assert.Equal(new[] { 1, 3 }, extended.Samples.Select(item => item.FrameIndex));
        Assert.Equal(new double[] { 4, 5 }, standard.Samples.Select(item => item.Value));
        Assert.Equal(new double[] { 70, 80 }, extended.Samples.Select(item => item.Value));
    }

    [Fact]
    public void DecoderCacheCapKeepsRealLaterSignalAfterManyUnknownExtendedIds()
    {
        var database = new DbcParser().Parse("engine", new StringReader(
            """
            BO_ 256 Engine: 8 ECM
             SG_ State : 0|8@1+ (1,0) [0|255] "" ECM
            """));
        // More distinct 29-bit IDs than the 65,536 cache-entry limit.
        // Correctness must not depend on a useful signal being observed
        // before the cache reaches its configured memory bound.
        var frames = Enumerable.Range(0, 65_541).Select(index =>
            new CanFrame(0, 0x0C000000u + (uint)index, [(byte)index],
                index * 1_000L, CanFrameFlags.Extended)).ToList();
        frames.Add(new CanFrame(0, 256, [42], 65_541_000L));
        frames.Add(new CanFrame(0, 256, [43], 65_542_000L));

        var timeline = Assert.Single(SignalTimelineBuilder.Build(frames, [database]));
        Assert.Equal(new[] { 65_541, 65_542 },
            timeline.Samples.Select(sample => sample.FrameIndex));
        Assert.Equal(new[] { 42d, 43d },
            timeline.Samples.Select(sample => sample.Value));
        Assert.Equal(256u, timeline.SourceIdentifier);
        Assert.False(timeline.SourceIsExtended);
    }

    [Fact]
    public void NonfiniteDecodedFloatSamplesAreNotInsertedIntoNumericSignalTimeline()
    {
        var database = new DbcParser().Parse("float", new StringReader(
            """
            BO_ 256 Sensor: 8 ECU
             SG_ Temperature : 0|32@1+ (1,0) [-100|100] "" ECU
            SIG_VALTYPE_ 256 Temperature : 1;
            """));
        var frames = new[]
        {
            new CanFrame(0, 256, BitConverter.GetBytes(5f), 0),
            new CanFrame(0, 256, BitConverter.GetBytes(float.NaN), 1_000_000),
            new CanFrame(0, 256, BitConverter.GetBytes(float.PositiveInfinity), 2_000_000),
            new CanFrame(0, 256, BitConverter.GetBytes(7f), 3_000_000)
        };

        var timeline = Assert.Single(SignalTimelineBuilder.Build(frames, [database]));
        Assert.Equal(new[] { 0, 3 },
            timeline.Samples.Select(item => item.FrameIndex));
        Assert.Equal(new double[] { 5, 7 },
            timeline.Samples.Select(item => item.Value));
        // Filtering the numeric plot must never mutate or lose evidence in Trace.
        Assert.Equal(4, frames.Length);
        Assert.Equal(BitConverter.SingleToUInt32Bits(float.NaN),
            BitConverter.ToUInt32(frames[1].GetPayload()));
    }
}
